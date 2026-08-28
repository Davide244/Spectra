using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SpectraEngine.Editor.Shell;

/// <summary>
/// Paints the OS title bar to match the window, instead of the user's accent
/// colour.
/// </summary>
/// <remarks>
/// <b>A DWM attribute, deliberately not a custom title bar.</b> Extending the
/// client area and drawing our own caption is the usual answer and it is the
/// wrong one here: it costs Aero Snap, the maximise-hover flyout and correct
/// maximised insets, it needs its own hit-testing, and this window contains a
/// native child that swallows the mouse messages such hit-testing depends on.
/// Three attribute writes get the colour without touching any of that.
/// <para>
/// <b>Everything here degrades to nothing.</b> The attributes landed in Windows
/// 11 build 22000; older builds reject them, which is reported as a failed
/// HRESULT and ignored. A shell whose title bar is the wrong colour is a
/// cosmetic disappointment, not a reason to fail startup.
/// </para>
/// </remarks>
internal static partial class DarkCaption
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const int DwmwaBorderColor = 34;

    // COLORREF is 0x00BBGGRR, which is byte-reversed from every hex colour in
    // the theme. Getting this backwards produces a blue title bar on a red
    // accent and looks like a theming bug rather than a byte-order one.
    private static uint ColorRef(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

    /// <summary>Applies the dark caption to <paramref name="window"/>, if the OS supports it.</summary>
    internal static void Apply(Window window, ILogger logger)
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (window.TryGetPlatformHandle() is not { } handle || handle.Handle == 0)
            return;

        try
        {
            int dark = 1;
            Set(handle.Handle, DwmwaUseImmersiveDarkMode, ref dark);

            // SpectraBgPanel and SpectraTextBody: the caption reads as another
            // docked strip rather than as a separate window frame.
            uint caption = ColorRef(0x12, 0x10, 0x11);
            uint text = ColorRef(0xA9, 0xA2, 0xA3);
            uint border = ColorRef(0x2E, 0x28, 0x29);

            Set(handle.Handle, DwmwaCaptionColor, ref caption);
            Set(handle.Handle, DwmwaTextColor, ref text);
            Set(handle.Handle, DwmwaBorderColor, ref border);
        }
        catch (DllNotFoundException ex)
        {
            logger.LogDebug(ex, "dwmapi is unavailable; the title bar keeps the system colour");
        }
        catch (EntryPointNotFoundException ex)
        {
            logger.LogDebug(ex, "DwmSetWindowAttribute is unavailable; the title bar keeps the system colour");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Set<T>(nint hwnd, int attribute, ref T value) where T : unmanaged
    {
        unsafe
        {
            fixed (T* p = &value)
                _ = DwmSetWindowAttribute(hwnd, attribute, p, sizeof(T));
        }
    }

    [LibraryImport("dwmapi.dll")]
    [SupportedOSPlatform("windows")]
    private static unsafe partial int DwmSetWindowAttribute(nint hwnd, int attribute, void* value, int size);
}
