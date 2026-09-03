using Microsoft.Extensions.Logging;
using System;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// Renders ONE frame into a shared present target and into an ordinary sRGB
/// target at the same time, reads both back, and reports the largest
/// per-channel difference.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> A composited surface hands its frame to somebody
/// else's device instead of presenting it, and the colour route it takes to get
/// there is not the window's: on D3D11 the resolve writes through an
/// <c>_SRGB</c> view over a UNORM shared resource, and on D3D12 it lands in a
/// private sRGB target that a D3D11On12 bridge copies across. Either route
/// encoding twice washes the picture out and reports NOTHING - no exception, no
/// HRESULT, no debug-layer message - so the only detector available is a byte
/// comparison against what the window path would have produced. See
/// <see cref="ViewportCompare"/> for the arithmetic and the numbers.
/// </para>
/// <para>
/// <b>One frame, two targets, one command list</b>, which is the whole reason
/// this is a probe inside the frame rather than two runs compared afterwards:
/// two frames are two pictures, and a difference between them would be the
/// animation rather than the colour space. <see cref="Renderer.CompareTarget"/>
/// is the hook, and it is the same shape <see cref="Renderer.ProbeTarget"/>
/// already established.
/// </para>
/// <para>
/// <b>It stands in for the consumer as well as measuring it.</b> With nobody
/// importing the handle, the producer writes exactly one shared frame and every
/// frame after it skips the write on a <c>WAIT_TIMEOUT</c> - correct behaviour,
/// and it would leave the shared texture holding a picture several frames older
/// than the one resolved beside it, i.e. a guaranteed false failure with a
/// plausible cause. So every update takes the consumer's turn and hands the key
/// straight back, exactly as a compositor that had nothing to draw would.
/// </para>
/// <para>
/// Render thread only, in the same slot <see cref="OffscreenProbe"/> occupies:
/// before <see cref="Renderer.Render"/>, because it decides what that frame
/// also writes.
/// </para>
/// </remarks>
public sealed class ViewportCompareProbe
{
    /// <summary>
    /// Frames rendered before the comparison is armed.
    /// </summary>
    /// <remarks>
    /// Not needed for correctness - both pictures come out of one source
    /// texture in one frame, so anything still loading is identical on both
    /// sides - and kept because a settled frame is the one worth reporting a
    /// measurement of, and because the shared target does not exist until the
    /// first frame has created it.
    /// </remarks>
    private const int WarmupFrames = 4;

    private readonly ILogger _logger;

    private RenderTarget? _reference;
    private int _frames;
    private bool _armed;
    private int _errorsAtStart;

    /// <summary>True until the probe has finished and reported.</summary>
    public bool Running { get; private set; } = true;

    /// <summary>Set once the probe has run to completion and the two pictures agreed.</summary>
    public bool Passed { get; private set; }

    /// <summary>What the comparison measured, once it has run.</summary>
    public ViewportCompare.Reading Reading { get; private set; }

    public ViewportCompareProbe(ILogger logger) => _logger = logger;

    /// <summary>
    /// Called once per frame on the render thread, before
    /// <see cref="Renderer.Render"/>. Returns when the probe is finished.
    /// </summary>
    public void Update(Renderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        if (!Running) return;

        try
        {
            if (_frames == 0) Begin(renderer);
            _frames++;

            // The armed frame has now been rendered, so read it - and take NO
            // turn first. The read takes the consumer's turn itself, and taking
            // it twice hands key 0 back to the producer before the read asks
            // for key 1, which times out and reports as a shared target nobody
            // ever wrote. That was the first thing this probe got wrong.
            if (_armed)
            {
                Measure(renderer);
                return;
            }

            // The compositor's half of the handshake on every other frame: the
            // producer released key 1 at the end of the last one and nothing
            // else in this process will ever hand key 0 back.
            renderer.TakeSharedConsumerTurn();

            if (_frames >= WarmupFrames) Arm(renderer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Viewport compare: FAIL - the measurement threw");
            Finish(renderer, passed: false);
        }
    }

    private void Begin(Renderer renderer)
    {
        _errorsAtStart = renderer.DebugLayerErrorCount;
        _logger.LogInformation(
            "Viewport compare: warming up {Frames} frame(s) on {Backend}, then rendering one frame into the " +
            "shared present target and an ordinary sRGB target at once. Passes at a max per-channel delta of " +
            "{Threshold} or less.",
            WarmupFrames, renderer.Backend, ViewportCompare.Threshold);

        // Stated rather than counted: the debug layer is the only continuous
        // detector a composited surface has, and a run with it off proves the
        // colours and nothing about barriers or pipeline states.
        if (!renderer.DebugLayerActive && renderer.Backend != GraphicsBackend.OpenGL)
        {
            _logger.LogWarning(
                "Viewport compare: the graphics validation layer is OFF, so this run measures the picture " +
                "only. Re-run with --debug-layer=true to gate the shared route's barriers as well.");
        }
    }

    private void Arm(Renderer renderer)
    {
        if (!renderer.TryGetSharedHandle(out Renderer.SharedTargetHandle handle))
        {
            _logger.LogError(
                "Viewport compare: FAIL - {Backend} has no shared present target, so there is nothing to " +
                "compare against. This probe needs a composited surface.",
                renderer.Backend);
            Finish(renderer, passed: false);
            return;
        }

        // Refused rather than worked around: with HDR off the pipeline draws
        // straight into the presented target and there is no intermediate to
        // resolve a second time from, so a run would report a comparison it
        // never made.
        if (!renderer.HdrEnabled)
        {
            _logger.LogError(
                "Viewport compare: FAIL - HDR is off, so the frame has no intermediate to resolve twice from " +
                "and no reference picture can be produced.");
            Finish(renderer, passed: false);
            return;
        }

        // Rgba8 sRGB and nothing else, because that is byte-for-byte what the
        // window's back buffer is on both backends: the encode happens once, on
        // the write, and the readback returns the stored codes. Depth is off -
        // a resolve is a full-screen triangle with depth testing already
        // disabled, and a full-screen depth surface nothing reads is memory
        // spent for nothing.
        _reference = renderer.CreateRenderTarget(new RenderTargetDesc(
            handle.Width, handle.Height, TextureFormat.Rgba8, TextureColorSpace.Srgb, Depth: false));
        renderer.CompareTarget = _reference;
        _armed = true;

        _logger.LogInformation(
            "Viewport compare: armed at {Width}x{Height} (shared generation {Generation}, handle 0x{Handle:X}).",
            handle.Width, handle.Height, handle.Generation, handle.NtHandle);
    }

    private void Measure(Renderer renderer)
    {
        RenderTarget reference = _reference
            ?? throw new InvalidOperationException("The compare probe measured before it armed.");

        // Disarmed FIRST: the read below can throw, and a compare target left
        // set would go on costing every remaining frame a second resolve into a
        // target nothing reads.
        renderer.CompareTarget = null;

        byte[] windowPicture = new byte[PixelReadback.ByteCount(reference.Width, reference.Height)];
        renderer.ReadTargetPixels(reference, windowPicture);

        // Before the comparison, because two blank pictures agree perfectly: a
        // frame that drew nothing would otherwise report the strongest possible
        // PASS while proving nothing about the colour route at all.
        if (!ViewportCompare.HasVariation(windowPicture))
        {
            _logger.LogError(
                "Viewport compare: FAIL - the reference picture is a single flat colour, so an agreement " +
                "would prove nothing. The frame drew no scene; check the pipeline and the loaded map.");
            Finish(renderer, passed: false);
            return;
        }

        byte[] sharedPicture = new byte[windowPicture.Length];
        if (!renderer.TryReadSharedPixels(sharedPicture))
        {
            _logger.LogError(
                "Viewport compare: FAIL - the shared target's key never came back, so nothing was read " +
                "through the handle and no comparison was made.");
            Finish(renderer, passed: false);
            return;
        }

        Reading = ViewportCompare.Compare(windowPicture, sharedPicture);

        if (Reading.Passes)
        {
            _logger.LogInformation(
                "Viewport compare on {Backend}: {Verdict} - {Reading}",
                renderer.Backend, Reading.Verdict, Reading);
        }
        else
        {
            // Named, because a large delta has one overwhelmingly likely cause
            // and a verdict that only reports a number leaves the reader to
            // rediscover it.
            _logger.LogError(
                "Viewport compare on {Backend}: {Verdict} - {Reading}. A delta of this size on a picture " +
                "both routes drew in one frame is a transfer function applied twice: check that the shared " +
                "resource is UNORM with only its render-target view sRGB.",
                renderer.Backend, Reading.Verdict, Reading);
        }

        Finish(renderer, Reading.Passes);
    }

    private void Finish(Renderer renderer, bool passed)
    {
        renderer.CompareTarget = null;
        if (_reference is not null)
        {
            renderer.DestroyRenderTarget(_reference);
            _reference = null;
        }

        // Same verdict rule the offscreen probe uses: the picture being right
        // and the layer staying quiet are two claims, and a run that proves one
        // must not report the other.
        int newErrors = renderer.DebugLayerErrorCount - _errorsAtStart;
        if (passed && newErrors > 0)
        {
            passed = false;
            _logger.LogError(
                "Viewport compare: FAIL - the graphics debug layer reported {Count} error(s) while rendering " +
                "into a shared target; see the messages above",
                newErrors);
        }

        Running = false;
        Passed = passed;
    }
}
