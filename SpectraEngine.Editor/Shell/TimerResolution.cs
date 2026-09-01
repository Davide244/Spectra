using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace SpectraEngine.Editor.Shell;

/// <summary>
/// Asks Windows for a one-millisecond timer, for as long as an engine session
/// is open.
/// </summary>
/// <remarks>
/// <para>
/// <b>Without this an 8ms timer does not exist.</b> Windows' default timer
/// granularity is 15.6ms, so a <c>DispatcherTimer</c> asking for 8 silently
/// fires at 15.6 and sometimes 31.2 - which is most of the shell's measured
/// worst-case lag, and it is invisible because the timer reports success.
/// </para>
/// <para>
/// <b>Scoped to the session, not to the process.</b> A raised timer interrupt
/// rate is measurable battery draw on a laptop, and the start page has nothing
/// to pump - so the resolution's lifetime is exactly the lifetime of the thing
/// that needs it.
/// </para>
/// <para>
/// <b>The classic objection is obsolete on the OS this shell already
/// requires.</b> Since Windows 10 2004 <c>timeBeginPeriod</c> affects only the
/// calling process, so the old "you are slowing down the whole machine"
/// argument does not apply; and this shell already depends on Windows 11-era
/// DWM attributes and <c>GetSystemMetricsForDpi</c>.
/// </para>
/// <para>
/// <b>The achieved resolution is measured and logged once</b>, rather than
/// assumed. A performance fix that silently stops working is worse than none -
/// the same discipline the engine's debug-layer switch already follows, where a
/// gate that quietly weakens is treated as a failure.
/// </para>
/// </remarks>
internal static partial class TimerResolution
{
    private const uint Period = 1;

    private static bool _held;

    /// <summary>Raises the timer resolution, if the OS supports it.</summary>
    public static void Acquire(ILogger logger)
    {
        if (!OperatingSystem.IsWindows() || _held)
            return;

        try
        {
            if (TimeBeginPeriod(Period) != 0)
            {
                logger.LogDebug("timeBeginPeriod refused; the shell keeps the default timer granularity");
                return;
            }

            _held = true;

            // OFF THE UI THREAD. The measurement is a hundred one-millisecond
            // sleeps, which is a tenth of a second - taken here it would be a
            // visible stall at the exact moment a session opens, which is a
            // strange price to pay for a line confirming that the shell is
            // fast.
            _ = Task.Run(() => logger.LogInformation(
                "Timer resolution raised; measured granularity {Granularity:F2} ms", Measure()));
        }
        catch (DllNotFoundException ex)
        {
            logger.LogDebug(ex, "winmm is unavailable; the shell keeps the default timer granularity");
        }
        catch (EntryPointNotFoundException ex)
        {
            logger.LogDebug(ex, "timeBeginPeriod is unavailable; the shell keeps the default timer granularity");
        }
    }

    /// <summary>Gives the timer resolution back. Idempotent.</summary>
    public static void Release()
    {
        if (!OperatingSystem.IsWindows() || !_held)
            return;

        _held = false;

        try
        {
            _ = TimeEndPeriod(Period);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    // The median of a hundred one-tick sleeps. The MEDIAN rather than the mean
    // because a single scheduling hiccup during startup would otherwise report
    // a granularity nothing actually has; and one tick rather than zero because
    // Sleep(0) yields without ever touching the timer.
    private static double Measure()
    {
        Span<double> gaps = stackalloc double[100];
        long previous = Stopwatch.GetTimestamp();

        for (int i = 0; i < gaps.Length; i++)
        {
            Thread.Sleep(1);
            long now = Stopwatch.GetTimestamp();
            gaps[i] = (now - previous) * 1000.0 / Stopwatch.Frequency;
            previous = now;
        }

        gaps.Sort();
        return gaps[gaps.Length / 2];
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint period);

    [SupportedOSPlatform("windows")]
    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint period);
}
