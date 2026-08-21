using SpectraEngine.Core.Input;
using System.Collections.Generic;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// A headless <see cref="ICursorLock"/> that records what was asked for and only
/// makes it true when <see cref="Pump"/> is called.
/// </summary>
/// <remarks>
/// <b>The delay is the point.</b> The real lock is a latch across two threads:
/// the editor camera requests a mode on the render thread and the main thread
/// applies it on some later pass of the event pump. A double that applied
/// requests instantly would hide every bug that lives in that gap — the frames
/// where the lock has been asked for but the cursor is still an ordinary one.
/// <see cref="Pump"/> is where a test decides the request has landed.
/// </remarks>
internal sealed class FakeCursorLock : ICursorLock
{
    private readonly List<CursorMode> _requests = [];

    /// <summary>Every mode requested, in order — including redundant ones, of which there should be none.</summary>
    public IReadOnlyList<CursorMode> Requests => _requests;

    /// <summary>The mode last requested, applied or not.</summary>
    public CursorMode Requested { get; private set; } = CursorMode.Normal;

    /// <inheritdoc/>
    public CursorMode CursorMode { get; private set; } = CursorMode.Normal;

    /// <inheritdoc/>
    public bool IsCursorLocked => CursorMode == CursorMode.Locked;

    /// <inheritdoc/>
    public void RequestCursorMode(CursorMode mode)
    {
        Requested = mode;
        _requests.Add(mode);
    }

    /// <summary>Applies the pending request — the main thread's half of the latch.</summary>
    public void Pump() => CursorMode = Requested;
}
