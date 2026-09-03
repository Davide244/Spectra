using Microsoft.Extensions.Logging;
using SpectraEngine.Core;
using SpectraEngine.Core.Graphics;
using System;
using System.Diagnostics;
using System.Threading;

namespace SpectraEngine.Executable;

/// <summary>
/// Runs the engine against a windowless composited surface for as long as
/// <see cref="SharedPacingProbe"/> needs, then stops it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A measurement of the coupling rather than a session</b>, the same shape
/// <see cref="ViewportCompareRun"/> takes and for the same reason: what is
/// being measured only exists on a composited surface, and it needs REAL FRAMES
/// because the question is what the engine's frame rate does, not what a
/// capability flag says.
/// </para>
/// <para>
/// <b>Why it is worth shipping rather than deleting after one answer.</b> The
/// composited viewport's pacing is the one part of the engine whose cost is set
/// by somebody else's scheduler, and every other instrument is blind to it -
/// frame time includes the wait, so a stalled producer and a slow one report
/// the same number. A run of this on a machine that reports a slow viewport
/// separates the two in about fifteen seconds and needs no compositor, no
/// window and nobody with a mouse.
/// </para>
/// </remarks>
internal static class SharedPacingRun
{
    /// <summary>
    /// A real viewport's shape, because the consumer's turn includes a copy of
    /// the whole texture and a token target would measure a copy nobody makes.
    /// </summary>
    private const int Width = 1280;

    private const int Height = 720;

    /// <summary>
    /// How long to wait before giving up. The script is fixed-length and the
    /// engine ends the run itself, so this only rules out a wait that never
    /// returns.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Starts the engine, waits for the probe's table, stops it, and returns
    /// whether a measurement was produced.
    /// </summary>
    internal static bool Run(Engine engine, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(logger);

        engine.RunSharedPacingProbe = true;

        var surface = new CompositedProbeSurface(Width, Height);
        engine.Start(surface);
        try
        {
            // Faulted as well as the shutdown latch: a render thread that
            // crashed before the first frame never asks for a shutdown, and
            // waiting the whole timeout out reports "the probe did not report"
            // over a stack trace that is already in the log and says why.
            var waited = Stopwatch.StartNew();
            while (!engine.Host.ShutdownRequested && !engine.Faulted && waited.Elapsed < Timeout)
                Thread.Sleep(10);

            if (!engine.Host.ShutdownRequested && !engine.Faulted)
            {
                logger.LogError(
                    "Shared pacing probe: FAIL - the probe did not report within {Seconds:0} s. The render " +
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

        return engine.SharedPacingProbePassed == true && !engine.Faulted;
    }
}
