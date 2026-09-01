using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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
    private static uint ColorRef(Color c) => (uint)(c.R | (c.G << 8) | (c.B << 16));

    /// <summary>
    /// Paints the dark caption onto <paramref name="window"/>, if the OS
    /// supports it.
    /// </summary>
    /// <remarks>
    /// <b>Every top-level window, not just the main one.</b> A caption left
    /// alone is painted in the machine's personalisation colour, so a shell
    /// that dresses one window and not its dialogs puts a band of somebody's
    /// wallpaper across the top of every prompt it shows - the same leak
    /// SystemAccentColor closes inside the window, one layer out. The overload
    /// exists because a dialog has no logger to hand and the failure it would
    /// log is "this build of Windows is too old", which one window reporting is
    /// enough of.
    /// </remarks>
    internal static void Apply(Window window) => Apply(window, logger: null);

    /// <inheritdoc cref="Apply(Window)"/>
    internal static void Apply(Window window, ILogger? logger)
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (window.TryGetPlatformHandle() is not { } handle || handle.Handle == 0)
            return;

        try
        {
            int dark = 1;
            Set(handle.Handle, DwmwaUseImmersiveDarkMode, ref dark);

            // READ FROM THE TOKEN DICTIONARY, never transcribed. These three
            // are the only colours in the shell the theme cannot reach through
            // a brush - DWM takes a COLORREF - so the one thing that could go
            // wrong is the palette moving and the title bar staying where it
            // was, which is invisible until somebody notices the caption is a
            // slightly different grey from the strip beneath it.
            uint caption = ColorRef(Token("SpectraBgPanelColor", 0x24, 0x20, 0x21));
            uint text = ColorRef(Token("SpectraTextBodyColor", 0xC2, 0xBB, 0xBD));
            uint border = ColorRef(Token("SpectraBorderStrongColor", 0x3F, 0x39, 0x3B));

            Set(handle.Handle, DwmwaCaptionColor, ref caption);
            Set(handle.Handle, DwmwaTextColor, ref text);
            Set(handle.Handle, DwmwaBorderColor, ref border);
        }
        catch (DllNotFoundException ex)
        {
            logger?.LogDebug(ex, "dwmapi is unavailable; the title bar keeps the system colour");
        }
        catch (EntryPointNotFoundException ex)
        {
            logger?.LogDebug(ex, "DwmSetWindowAttribute is unavailable; the title bar keeps the system colour");
        }
    }

    // The fallback is the value the token holds today, so a missing key paints
    // the intended colour rather than black. It is a belt-and-braces default,
    // not a second source of truth: the dictionary is loaded before any window
    // opens, so in practice the lookup always succeeds.
    private static Color Token(string key, byte r, byte g, byte b)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is Color c
            ? c
            : Color.FromRgb(r, g, b);

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
