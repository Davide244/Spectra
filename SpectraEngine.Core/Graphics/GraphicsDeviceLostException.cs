using System;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// Thrown when the graphics device is gone — removed, reset, hung, or lost to a
/// driver fault — rather than when a single call was made wrongly.
/// </summary>
/// <remarks>
/// This engine cannot recreate a device mid-run yet, so the honest response is
/// to end the run with a message that says what happened and why. That is the
/// entire reason this type exists instead of a raw <c>COMException</c>: the
/// render thread's crash handler logs it, tears the GPU side down on the thread
/// that owns it, and the process exits nonzero — the same clean path a
/// deliberate shutdown takes, with a diagnosis attached.
/// <para>
/// The message carries the failing HRESULT <em>and</em> the device's own
/// <c>GetDeviceRemovedReason</c>, because the second is the one that names the
/// actual fault (a TDR, a hung command list, a driver upgrade under a running
/// process); the first only says "the device is gone".
/// </para>
/// </remarks>
public sealed class GraphicsDeviceLostException : Exception
{
    /// <summary>Creates the exception with a fully formed diagnosis.</summary>
    public GraphicsDeviceLostException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a diagnosis and the underlying failure.</summary>
    public GraphicsDeviceLostException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
