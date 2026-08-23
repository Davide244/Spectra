using Microsoft.Extensions.Logging;
using System;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// Drives a real frame through a real offscreen render target, for the two
/// backends that cannot be tested any other way.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> OpenGL's render targets are checked by reading pixels
/// back in <c>GlRenderTargetTests</c>, against a real driver, headlessly. D3D11
/// and D3D12 have no such fixture: a device needs a window, and this process
/// only gets one. So the parts of `R3` most likely to be wrong on those two go
/// unexercised by the suite, and they are exactly the parts that still produce a
/// picture when they are wrong. A missed D3D12 barrier reads undefined data. A
/// pipeline state compiled against the back buffer's format and bound to a
/// target with a different one is a validation failure, not a visibly wrong
/// pixel. Both backends drain their debug layer every frame, so running a
/// genuine offscreen pass on a real device is what turns those into failures
/// somebody sees.
/// </para>
/// <para>
/// <b>What it actually does</b> is set <see cref="Renderer.ProbeTarget"/> for a
/// handful of frames, which makes each frame render into the target as well as
/// into the window, in one command list. It then resizes the target and does it
/// again, because a resize is where the object identity of the colour
/// attachment and the re-creation of every view have to hold together. Nothing
/// it draws is ever displayed.
/// </para>
/// <para>
/// <b>Off by default</b>, like every other gate in this engine: it renders the
/// scene twice per probing frame, and a diagnostic that quietly halves the frame
/// rate of an ordinary run is worse than one that has to be asked for.
/// </para>
/// </remarks>
public sealed class OffscreenProbe
{
    /// <summary>Frames rendered at each of the two sizes.</summary>
    private const int FramesPerStage = 3;

    private readonly ILogger _logger;
    private readonly int _width;
    private readonly int _height;

    private RenderTarget? _target;
    private int _frames;
    private bool _resized;
    private int _errorsAtStart;

    /// <summary>True until the probe has finished and reported.</summary>
    public bool Running { get; private set; } = true;

    /// <summary>Set once the probe has run to completion without throwing.</summary>
    public bool Passed { get; private set; }

    public OffscreenProbe(ILogger logger, int width = 640, int height = 360)
    {
        _logger = logger;
        _width = width;
        _height = height;
    }

    /// <summary>
    /// Called once per frame on the render thread, before
    /// <see cref="Renderer.Render"/>. Returns when the probe is finished.
    /// </summary>
    public void Update(Renderer renderer)
    {
        if (!Running) return;

        try
        {
            if (_target is null)
            {
                // Deliberately not the window's size or aspect: a probe at the
                // same shape as the back buffer would not catch a viewport or an
                // aspect ratio that was still being taken from the window.
                _target = renderer.CreateRenderTarget(new RenderTargetDesc(
                    _width, _height, ColorSpace: TextureColorSpace.Srgb));
                renderer.ProbeTarget = _target;

                // The baseline matters: a run may already have logged debug
                // layer errors for reasons that have nothing to do with render
                // targets, and blaming those on the probe would make it a liar
                // in the other direction.
                _errorsAtStart = renderer.DebugLayerErrorCount;
                _logger.LogInformation(
                    "Offscreen probe: rendering into a {Width}x{Height} target for {Frames} frames, twice",
                    _width, _height, FramesPerStage);
                return;
            }

            _frames++;
            if (_frames < FramesPerStage) return;

            if (!_resized)
            {
                // The half-size second stage. Resizing a target the renderer is
                // about to draw into is the ordinary case (an editor viewport
                // does it whenever a pane moves), and it is where a stale view
                // or a released-but-still-referenced resource shows up.
                _resized = true;
                _frames = 0;
                _target.Resize(_width / 2, _height / 2);
                return;
            }

            Finish(renderer, passed: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Offscreen probe: FAIL");
            Finish(renderer, passed: false);
        }
    }

    private void Finish(Renderer renderer, bool passed)
    {
        renderer.ProbeTarget = null;
        if (_target is not null)
        {
            renderer.DestroyRenderTarget(_target);
            _target = null;
        }

        // Nothing threw, but a graphics debug layer may still have rejected
        // what the frames did. On D3D that is the ONLY report a missing barrier
        // or a mismatched pipeline-state format produces: the frame renders,
        // the API returns success, and the picture may even look right. So the
        // probe's verdict is "no exception AND the debug layer stayed quiet",
        // not just the first half.
        int newErrors = renderer.DebugLayerErrorCount - _errorsAtStart;
        if (passed && newErrors > 0)
        {
            passed = false;
            _logger.LogError(
                "Offscreen probe: FAIL - the graphics debug layer reported {Count} error(s) while " +
                "rendering into an offscreen target; see the messages above",
                newErrors);
        }

        Running = false;
        Passed = passed;

        if (passed)
        {
            _logger.LogInformation(
                "Offscreen probe: PASS - a full frame rendered into an offscreen target at " +
                "{Width}x{Height} and again at {HalfWidth}x{HalfHeight} after an in-place resize, " +
                "with the colour attachment keeping its identity and the debug layer silent",
                _width, _height, _width / 2, _height / 2);
        }
    }
}
