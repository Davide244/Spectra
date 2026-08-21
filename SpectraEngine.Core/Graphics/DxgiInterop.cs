using Microsoft.Extensions.Logging;
using Silk.NET.DXGI;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// The handful of DXGI constants and HRESULT classifications both D3D backends
/// need, in one place so D3D11 and D3D12 cannot drift apart on them.
/// </summary>
/// <remarks>
/// Silk.NET's DXGI binding exposes neither the <c>DXGI_MWA_*</c> window
/// association flags nor the <c>DXGI_ERROR_*</c> HRESULTs as named constants,
/// so they are spelled out here with their documented values.
/// </remarks>
internal static unsafe class DxgiInterop
{
    /// <summary>DXGI_MWA_NO_WINDOW_CHANGES — DXGI must not monitor the message queue.</summary>
    internal const uint MwaNoWindowChanges = 1u << 0;

    /// <summary>
    /// DXGI_MWA_NO_ALT_ENTER — DXGI must not turn Alt+Enter into a fullscreen
    /// transition of its own. The single most important flag this engine sets:
    /// see <see cref="SuppressAltEnter"/>.
    /// </summary>
    internal const uint MwaNoAltEnter = 1u << 1;

    /// <summary>DXGI_MWA_NO_PRINT_SCREEN — DXGI must not respond to Print Screen.</summary>
    internal const uint MwaNoPrintScreen = 1u << 2;

    /// <summary>DXGI_ERROR_INVALID_CALL.</summary>
    internal const int ErrorInvalidCall = unchecked((int)0x887A0001);

    /// <summary>DXGI_ERROR_DEVICE_REMOVED.</summary>
    internal const int ErrorDeviceRemoved = unchecked((int)0x887A0005);

    /// <summary>DXGI_ERROR_DEVICE_HUNG.</summary>
    internal const int ErrorDeviceHung = unchecked((int)0x887A0006);

    /// <summary>DXGI_ERROR_DEVICE_RESET.</summary>
    internal const int ErrorDeviceReset = unchecked((int)0x887A0007);

    /// <summary>DXGI_ERROR_DRIVER_INTERNAL_ERROR.</summary>
    internal const int ErrorDriverInternalError = unchecked((int)0x887A0020);

    /// <summary>DXGI_ERROR_ACCESS_LOST.</summary>
    internal const int ErrorAccessLost = unchecked((int)0x887A0026);

    /// <summary>D3DDDIERR_DEVICEREMOVED, the reason code a removed device usually reports.</summary>
    internal const int DdiErrorDeviceRemoved = unchecked((int)0x88760870);

    /// <summary>
    /// True when <paramref name="hr"/> means <em>the device is gone</em> rather
    /// than <em>this call was wrong</em>. The distinction is the whole point:
    /// a device loss cannot be retried or degraded around, so it ends the run
    /// with a clear message, while everything else leaves the previous state
    /// intact and lets the engine keep going.
    /// </summary>
    internal static bool IsDeviceLost(int hr) =>
        hr is ErrorDeviceRemoved or ErrorDeviceReset or ErrorDeviceHung
           or ErrorDriverInternalError or ErrorAccessLost or DdiErrorDeviceRemoved;

    /// <summary>
    /// Names an HRESULT the DXGI way when it is one this engine knows, so a log
    /// line reads "DXGI_ERROR_INVALID_CALL" rather than "0x887A0001" alone.
    /// </summary>
    internal static string Describe(int hr) => hr switch
    {
        0 => "S_OK",
        ErrorInvalidCall => "DXGI_ERROR_INVALID_CALL",
        ErrorDeviceRemoved => "DXGI_ERROR_DEVICE_REMOVED",
        ErrorDeviceHung => "DXGI_ERROR_DEVICE_HUNG",
        ErrorDeviceReset => "DXGI_ERROR_DEVICE_RESET",
        ErrorDriverInternalError => "DXGI_ERROR_DRIVER_INTERNAL_ERROR",
        ErrorAccessLost => "DXGI_ERROR_ACCESS_LOST",
        DdiErrorDeviceRemoved => "D3DDDIERR_DEVICEREMOVED",
        unchecked((int)0x80070057) => "E_INVALIDARG",
        unchecked((int)0x8007000E) => "E_OUTOFMEMORY",
        _ => "unknown HRESULT",
    };

    /// <summary>
    /// Takes Alt+Enter away from DXGI for <paramref name="hwnd"/>.
    /// </summary>
    /// <param name="factory">
    /// <b>Must be the factory that created the swap chain.</b> The association
    /// is per-factory, so making it on a freshly created second factory
    /// silently does nothing — which is why both backends call this before
    /// releasing the factory they built the chain with.
    /// </param>
    /// <param name="hwnd">The swap chain's window.</param>
    /// <param name="logger">Where a failure is reported.</param>
    /// <param name="backend">Backend name for the log line.</param>
    /// <remarks>
    /// Without this, DXGI keeps its default Alt+Enter handling, which runs a
    /// <c>SetFullscreenState</c> transition <em>inside the window procedure</em>
    /// — i.e. on the thread pumping OS events — while the render thread is
    /// concurrently presenting and calling <c>ResizeBuffers</c> on the same swap
    /// chain. That unsynchronised cross-thread transition is what produced
    /// <c>DXGI_ERROR_INVALID_CALL</c> from <c>ResizeBuffers</c> and killed the
    /// render thread. Fullscreen is the engine's job instead — see
    /// <see cref="Windowing.IWindowModeLatch"/>.
    /// <para>
    /// A failure here is logged, never thrown: the engine still runs, it just
    /// has to be told that Alt+Enter is not safe on this machine.
    /// </para>
    /// </remarks>
    internal static void SuppressAltEnter(IDXGIFactory2* factory, nint hwnd, ILogger logger, string backend)
    {
        int hr = factory->MakeWindowAssociation(hwnd, MwaNoAltEnter);
        if (hr < 0)
        {
            logger.LogWarning(
                "{Backend}: MakeWindowAssociation(DXGI_MWA_NO_ALT_ENTER) failed ({Code}, 0x{Hr:X8}). DXGI may still " +
                "drive its own fullscreen transition on Alt+Enter, which races the render thread's ResizeBuffers.",
                backend, Describe(hr), hr);
            return;
        }

        logger.LogDebug("{Backend}: Alt+Enter taken from DXGI; fullscreen is the engine's (F11, borderless).", backend);
    }
}
