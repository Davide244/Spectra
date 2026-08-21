using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Windowing;
using System;
using System.Globalization;
using System.Threading;

namespace SpectraEngine.Executable;

/// <summary>
/// Drives the windowed ↔ borderless-fullscreen toggle on a timer, with nobody
/// at the keyboard, so a smoke run exercises the transition that used to kill
/// the render thread with <c>DXGI_ERROR_INVALID_CALL</c> out of
/// <c>ResizeBuffers</c>.
/// </summary>
/// <remarks>
/// Gate instrumentation, in the spirit of <see cref="Editing.EditingSelfTest"/>
/// and opt-in for the same reason: a window that resizes itself every couple of
/// seconds is exactly right for an automated run and exactly wrong for anybody
/// trying to use the editor. Off unless <c>--fullscreen-cycle</c> asks for it.
/// <para>
/// <b>The toggle it performs is the real one, on the real thread.</b> This class
/// only <em>requests</em> — which is the latch's documented contract from any
/// thread — and <c>Engine.Run</c> undecorates, moves and resizes the window in
/// its event pump on the window thread, while the render thread keeps
/// presenting and resizing the swap chain underneath. That concurrency is the
/// whole point: it is the same overlap a human produces by hitting F11 (or, on
/// an engine that has not taken Alt+Enter away from DXGI, Alt+Enter), and the
/// reason a driver thread rather than a synthetic <c>SetFramebufferSize</c> call
/// is what proves anything here.
/// </para>
/// <para>
/// The first toggle waits out <see cref="SettleSeconds"/> so the run has a
/// window, a compiled static world and a steady frame rate before anything
/// moves — a resize during startup would prove the wrong thing.
/// </para>
/// </remarks>
internal sealed class FullscreenCycleHarness : IDisposable
{
    /// <summary>Seconds of ordinary rendering before the first toggle.</summary>
    public const double SettleSeconds = 3.0;

    /// <summary>Interval used when <c>--fullscreen-cycle</c> carries no value.</summary>
    public const double DefaultIntervalSeconds = 2.0;

    private readonly ILogger _logger;
    private readonly IWindowModeLatch _windowMode;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Thread _thread;

    /// <summary>
    /// Starts the driver thread immediately; it idles through
    /// <see cref="SettleSeconds"/> before the first toggle.
    /// </summary>
    /// <param name="logger">Where each requested toggle is announced.</param>
    /// <param name="windowMode">The engine's window-mode latch.</param>
    /// <param name="interval">Seconds between toggles.</param>
    public FullscreenCycleHarness(ILogger logger, IWindowModeLatch windowMode, TimeSpan interval)
    {
        _logger = logger;
        _windowMode = windowMode;
        _interval = interval;

        // Background so a harness thread can never be what keeps the process
        // alive after the window has gone.
        _thread = new Thread(Run) { Name = "Spectra Fullscreen Cycle", IsBackground = true };
        _thread.Start();
    }

    private void Run()
    {
        CancellationToken token = _cancellation.Token;
        if (token.WaitHandle.WaitOne(TimeSpan.FromSeconds(SettleSeconds)))
            return;

        for (int index = 1; !token.IsCancellationRequested; index++)
        {
            // Read the applied mode, not the requested one: this line is the
            // gate's record of what the toggle was asked to do, and it should
            // say what the window actually is right now.
            WindowMode from = _windowMode.WindowMode;
            _windowMode.ToggleFullscreen();
            _logger.LogInformation(
                "Fullscreen cycle: toggle #{Index} requested ({From} -> {To})",
                index, from, from == WindowMode.Windowed ? WindowMode.BorderlessFullscreen : WindowMode.Windowed);

            if (token.WaitHandle.WaitOne(_interval))
                return;
        }
    }

    /// <summary>Stops the driver thread and waits briefly for it to unwind.</summary>
    public void Dispose()
    {
        _cancellation.Cancel();
        _thread.Join(TimeSpan.FromSeconds(1));
        _cancellation.Dispose();
    }

    /// <summary>
    /// One startup line describing what the run is about to do to itself, so a
    /// log that ends in a window jumping around explains itself.
    /// </summary>
    public static string DescribeStartup(TimeSpan interval) => string.Format(
        CultureInfo.InvariantCulture,
        "Fullscreen cycle ENABLED: after {0:0.#} s the window toggles windowed <-> borderless fullscreen every " +
        "{1:0.#} s, driven off the render thread, while rendering continues. Gate instrumentation for the " +
        "DXGI resize path — drop --fullscreen-cycle for a window that stays where you put it.",
        SettleSeconds, interval.TotalSeconds);
}
