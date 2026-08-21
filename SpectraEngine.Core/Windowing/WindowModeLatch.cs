using Microsoft.Extensions.Logging;

namespace SpectraEngine.Core.Windowing;

/// <summary>
/// The engine's <see cref="IWindowModeLatch"/>: holds the requested and the
/// applied window mode, and — on the window thread only — performs the
/// borderless-fullscreen transition against an <see cref="IWindowModeTarget"/>.
/// </summary>
/// <remarks>
/// Modelled on <see cref="Input.InputManager"/>'s cursor-mode latch, down to
/// the lock discipline: requests may arrive from any thread,
/// <see cref="ApplyPendingWindowMode"/> is the main thread's and only ever
/// writes the applied state.
/// </remarks>
public sealed class WindowModeLatch : IWindowModeLatch
{
    private readonly ILogger _logger;
    private readonly object _stateLock = new();

    private WindowMode _requestedMode = WindowMode.Windowed;
    private WindowMode _appliedMode = WindowMode.Windowed;

    // The windowed geometry to come back to. Captured on the way in rather
    // than reconstructed on the way out: once the window is undecorated and
    // covering the display, the size and position it used to have are simply
    // gone. Written and read on the window thread only, inside the apply.
    private WindowRect _restoreBounds;
    private bool _restoreDecorated = true;
    private bool _restoreMaximized;

    /// <summary>Creates a latch that starts, and reports itself, windowed.</summary>
    /// <param name="logger">Logs each applied transition and any refusal.</param>
    public WindowModeLatch(ILogger logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public void RequestWindowMode(WindowMode mode)
    {
        lock (_stateLock)
            _requestedMode = mode;
    }

    /// <inheritdoc/>
    public void ToggleFullscreen()
    {
        lock (_stateLock)
        {
            _requestedMode = _requestedMode == WindowMode.Windowed
                ? WindowMode.BorderlessFullscreen
                : WindowMode.Windowed;
        }
    }

    /// <inheritdoc/>
    public WindowMode WindowMode
    {
        get { lock (_stateLock) return _appliedMode; }
    }

    /// <inheritdoc/>
    public WindowMode RequestedWindowMode
    {
        get { lock (_stateLock) return _requestedMode; }
    }

    /// <summary>
    /// Applies whatever mode was last requested. <b>Window thread only</b>,
    /// once per pass of the OS-event pump — the same slot the title latch and
    /// the cursor-mode latch are applied in, and for the same reason.
    /// </summary>
    /// <param name="target">The window to drive.</param>
    /// <returns>
    /// The mode newly applied, or <c>null</c> when nothing changed — which is
    /// every frame but the one that toggles. The caller uses it to re-seed the
    /// framebuffer-size latch and to log the transition.
    /// </returns>
    /// <remarks>
    /// A fullscreen request the target cannot satisfy (no display bounds, or a
    /// degenerate one) is <em>refused</em>: the requested mode is reset to
    /// windowed so the next pass does not retry forever, and the window is left
    /// exactly as it was. Guessing at a display size would move the window
    /// somewhere the user cannot get it back from.
    /// </remarks>
    public WindowMode? ApplyPendingWindowMode(IWindowModeTarget target)
    {
        WindowMode requested;
        lock (_stateLock)
        {
            if (_requestedMode == _appliedMode)
                return null;
            requested = _requestedMode;
        }

        if (requested == WindowMode.BorderlessFullscreen)
        {
            if (!target.TryGetDisplayBounds(out WindowRect display) || !display.IsPositive)
            {
                _logger.LogWarning(
                    "Fullscreen request refused: the windowing backend reported no usable display bounds. Staying windowed.");
                lock (_stateLock)
                    _requestedMode = _appliedMode;
                return null;
            }

            // Capture the restore geometry BEFORE anything about the window
            // changes. A maximized window reports its restore rect here, which
            // is exactly what we want to put back.
            _restoreDecorated = target.Decorated;
            _restoreMaximized = target.IsMaximized;
            _restoreBounds = target.Bounds;

            // Un-maximize first: a maximized window ignores an explicit
            // position/size on most backends, so the move below would be a
            // no-op and the "fullscreen" window would still wear the taskbar.
            if (_restoreMaximized)
                target.IsMaximized = false;

            // Border before geometry — dropping the frame changes the client
            // area, so setting the size first would leave it off by the frame
            // thickness.
            target.Decorated = false;
            target.Bounds = display;
        }
        else
        {
            // Exact mirror of the way in, in reverse order.
            target.Decorated = _restoreDecorated;
            target.Bounds = _restoreBounds;
            if (_restoreMaximized)
                target.IsMaximized = true;
        }

        lock (_stateLock)
            _appliedMode = requested;

        return requested;
    }
}
