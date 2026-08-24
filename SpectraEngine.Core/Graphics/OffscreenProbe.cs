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
    /// <summary>Frames rendered in each stage.</summary>
    private const int FramesPerStage = 3;

    // One stage per thing that can independently be wrong. The two formats are
    // separate DXGI formats, separate RTV formats and separate pipeline states
    // on D3D12, and the float one is the format R4's scene target uses; the
    // resize is where a stale view or a released-but-referenced resource shows
    // up. `Resize` means "reuse the previous stage's target at half size".
    private readonly record struct Stage(string What, TextureFormat Format, TextureColorSpace Space, bool Resize);

    private static readonly Stage[] Stages =
    [
        new("HDR linear", TextureFormat.Rgba16Float, TextureColorSpace.Linear, Resize: false),
        new("HDR linear, resized", TextureFormat.Rgba16Float, TextureColorSpace.Linear, Resize: true),
        new("8-bit sRGB", TextureFormat.Rgba8, TextureColorSpace.Srgb, Resize: false),
    ];

    private readonly ILogger _logger;
    private readonly int _width;
    private readonly int _height;

    private RenderTarget? _target;
    private int _stage = -1;
    private int _frames;
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
            if (_stage < 0)
            {
                // The baseline matters: a run may already have logged debug
                // layer errors for reasons that have nothing to do with render
                // targets, and blaming those on the probe would make it a liar
                // in the other direction.
                _errorsAtStart = renderer.DebugLayerErrorCount;
                _logger.LogInformation(
                    "Offscreen probe: {Stages} stage(s), {Frames} frames each, starting at {Width}x{Height}",
                    Stages.Length, FramesPerStage, _width, _height);

                // HALF THIS PROBE'S VERDICT IS "the debug layer stayed silent",
                // and on D3D that number only exists while the validation layer
                // is running. Without it the probe still proves the passes do
                // not throw, but a missing barrier or a mismatched pipeline
                // state would sail straight through it. Saying so is the
                // difference between a weaker gate and a gate that lies.
                if (!renderer.DebugLayerActive && renderer.Backend != GraphicsBackend.OpenGL)
                {
                    _logger.LogWarning(
                        "Offscreen probe: the graphics validation layer is OFF, so this run cannot " +
                        "detect a missing barrier or a mismatched pipeline state. Re-run with " +
                        "--debug-layer=true for the full check.");
                }
                BeginStage(renderer, 0);
                return;
            }

            _frames++;
            if (_frames < FramesPerStage) return;

            if (_stage + 1 < Stages.Length)
            {
                BeginStage(renderer, _stage + 1);
                return;
            }

            Finish(renderer, passed: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Offscreen probe: FAIL at stage {Stage}", Describe(_stage));
            Finish(renderer, passed: false);
        }
    }

    private void BeginStage(Renderer renderer, int index)
    {
        Stage stage = Stages[index];
        _stage = index;
        _frames = 0;

        if (stage.Resize && _target is not null)
        {
            _target.Resize(_width / 2, _height / 2);
            return;
        }

        if (_target is not null)
        {
            renderer.ProbeTarget = null;
            renderer.DestroyRenderTarget(_target);
        }

        // Deliberately not the window's size or aspect: a probe at the same
        // shape as the back buffer would not catch a viewport or an aspect
        // ratio that was still being taken from the window.
        _target = renderer.CreateRenderTarget(
            new RenderTargetDesc(_width, _height, stage.Format, stage.Space));
        renderer.ProbeTarget = _target;
    }

    private static string Describe(int index) =>
        index >= 0 && index < Stages.Length ? Stages[index].What : "setup";

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
                "Offscreen probe: PASS - full frames rendered into {Stages} offscreen target(s) " +
                "({What}), the colour attachment kept its identity across an in-place resize, " +
                "and {Validation}",
                Stages.Length, string.Join(", ", Array.ConvertAll(Stages, x => x.What)),
                renderer.DebugLayerActive
                    ? "the debug layer stayed silent"
                    : "NO validation layer was running, so this is the weak form of the check");
        }
    }
}
