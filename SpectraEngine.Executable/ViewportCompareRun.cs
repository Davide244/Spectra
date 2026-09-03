using Microsoft.Extensions.Logging;
using Silk.NET.Core.Contexts;
using Silk.NET.Maths;
using SpectraEngine.Core;
using SpectraEngine.Core.Diagnostics;
using SpectraEngine.Core.Graphics;
using System;
using System.Diagnostics;
using System.Threading;

namespace SpectraEngine.Executable;

/// <summary>
/// Runs the engine against a windowless composited surface for as long as
/// <see cref="ViewportCompareProbe"/> needs, then stops it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A measurement of the machine rather than a session</b>, the same shape
/// <c>--interop-probe</c> and <c>--export-entity-schema</c> take: it replaces
/// the ordinary run instead of riding along beside it, and it ends itself. What
/// makes it different from those two is that it needs REAL FRAMES - the thing
/// being measured is the colour route a resolved frame takes on its way out of
/// a composited surface, which does not exist until a scene has been loaded and
/// drawn. <c>--exit-after-save</c> is the precedent for that half.
/// </para>
/// <para>
/// <b>No window at all, deliberately.</b> A shared present target only exists on
/// a <see cref="RenderSurfaceKind.Composited"/> surface, so a probe that opened
/// a window would have to build a second one beside it and would then be
/// measuring a target the frame does not go through. <see cref="Engine.Start"/>
/// already takes a surface somebody else owns, which is exactly this.
/// </para>
/// <para>
/// <b>The wait is bounded.</b> The engine ends the run itself when the probe
/// reports, so the loop here is waiting on a shutdown that has already been
/// decided; a timeout exists because a probe that never finished would
/// otherwise hang an unattended caller forever with nothing on screen, and a
/// hang reported as a timeout is a bug report somebody can act on.
/// </para>
/// </remarks>
internal static class ViewportCompareRun
{
    /// <summary>
    /// A real viewport's shape rather than a token one: the comparison is over
    /// every texel of the picture, and a 64-square target would leave most of
    /// the frame's content out of the measurement.
    /// </summary>
    private const int Width = 1280;

    private const int Height = 720;

    /// <summary>
    /// How long to wait for the probe before giving up. Generous, because a
    /// cold shader compile plus a first static-world build on a slow machine is
    /// seconds; what it rules out is a wait that never returns.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Starts the engine, waits for the probe, stops it, and returns whether
    /// the two pictures agreed.
    /// </summary>
    /// <remarks>
    /// <b>The verdict is left on disk as well as in the log,</b> because the one
    /// thing that has to act on it is the editor shell, which is a different
    /// process and cannot watch this run happen. See
    /// <see cref="ViewportCompareStamp"/>: without it the shell's
    /// composited-viewport flip policy would have a colour condition nothing
    /// could ever satisfy, and a gate that cannot open is worse than no gate.
    /// </remarks>
    internal static bool Run(Engine engine, Renderer renderer, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(logger);

        engine.RunViewportCompare = true;

        var surface = new CompositedProbeSurface(Width, Height);
        engine.Start(surface);
        try
        {
            var waited = Stopwatch.StartNew();
            while (!engine.Host.ShutdownRequested && waited.Elapsed < Timeout)
                Thread.Sleep(10);

            if (!engine.Host.ShutdownRequested)
            {
                logger.LogError(
                    "Viewport compare: FAIL - the probe did not report within {Seconds:0} s. The render " +
                    "thread is still running; see the log above for where it stopped.",
                    Timeout.TotalSeconds);
            }
        }
        finally
        {
            // Blocks until the render thread has finished, which is not
            // optional: it owns every GPU resource in the process, the shared
            // texture included.
            engine.Stop();
        }

        // Faulted covers the case the probe never got to run at all, which
        // would otherwise read as a pass through a null verdict.
        bool passed = engine.ViewportComparePassed == true && !engine.Faulted;

        // Recorded either way. A red verdict is exactly as much information as a
        // green one, and a stamp that only ever appeared on success would let a
        // machine keep the previous run's green answer after breaking.
        // AdapterName is only real once the render thread has initialised the
        // renderer, which the wait above has already happened after.
        RecordVerdict(renderer, passed, logger);
        return passed;
    }

    private static void RecordVerdict(Renderer renderer, bool passed, ILogger logger)
    {
        var stamp = new ViewportCompareStamp(
            renderer.AdapterName, renderer.Backend, passed, DateTime.UtcNow);

        if (stamp.Save())
        {
            logger.LogInformation(
                "Viewport compare: verdict recorded for {Adapter} on {Backend} at {Path}.",
                stamp.Adapter, stamp.Backend, ViewportCompareStamp.DefaultPath);
        }
        else
        {
            logger.LogWarning(
                "Viewport compare: the verdict could not be written to {Path}; the editor shell will see " +
                "no colour measurement for this machine.",
                ViewportCompareStamp.DefaultPath);
        }
    }

    /// <summary>
    /// A surface with no window, no handle and no GL context: exactly what an
    /// embedded host that composites the engine's output offers, and the only
    /// kind that has a shared target to measure.
    /// </summary>
    /// <remarks>
    /// The size never changes, so <see cref="Resized"/> is a real event that
    /// simply never fires. Removing the member is not an option and neither is
    /// throwing from it: <see cref="Engine.AttachSurface"/> subscribes and
    /// unsubscribes on every run.
    /// </remarks>
    private sealed class CompositedProbeSurface(int width, int height) : IRenderSurface
    {
        public RenderSurfaceKind Kind => RenderSurfaceKind.Composited;

        public nint NativeHandle => 0;

        public IGLContext? GLContext => null;

        public Vector2D<int> PixelSize => new(width, height);

        public event Action<Vector2D<int>>? Resized
        {
            add { }
            remove { }
        }
    }
}
