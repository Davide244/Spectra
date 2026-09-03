using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Hosting;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SpectraEngine.Editor.Viewport;

/// <summary>
/// The pane the engine renders into, whichever way it gets there.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is exactly what the shell already consumed, written down.</b> There
/// were never eleven things a window could tell the shell and eleven a
/// composited surface could not: the shell asks for a surface, hands back a
/// host, forwards the chords the viewport intercepted, pumps the cursor once a
/// pass and takes the keyboard back. Naming that set is the whole of what makes
/// a second implementation possible, and it was extracted before the second one
/// existed precisely so the extraction could be judged on its own.
/// </para>
/// <para>
/// <b><see cref="Control"/> is a member because an interface cannot demand a
/// base class.</b> Both implementations ARE controls; the shell puts one in a
/// <c>Border</c> and opens a context menu over it, and neither is expressible
/// through an interface without handing the control back. It returns
/// <c>this</c> in both cases and is not a wrapper.
/// </para>
/// <para>
/// <b>What is deliberately NOT here.</b> Whether a platform can host a viewport
/// at all, and which implementation a session gets, are decisions taken before
/// there is an instance to ask - so they live on <see cref="EngineViewports"/>
/// rather than becoming a static member nobody could call through the
/// interface.
/// </para>
/// <para>
/// <b>Threading:</b> UI thread only, every member. A viewport is a control.
/// </para>
/// </remarks>
public interface IEngineViewport
{
    /// <summary>
    /// Raised on the UI thread once the render surface exists. A host starts
    /// the engine here.
    /// </summary>
    /// <remarks>
    /// <b>The surface arrives after the control does, and that ordering is
    /// forced on both paths.</b> A renderer cannot initialise without something
    /// to draw into, and neither a native child window nor a compositor surface
    /// exists until the control is attached to a visual tree - so this is what
    /// a host waits for rather than starting the engine at construction and
    /// hoping.
    /// </remarks>
    event Action<IRenderSurface>? SurfaceCreated;

    /// <summary>
    /// Raised on the UI thread before the render surface goes away. A host must
    /// have stopped the engine by the time this returns.
    /// </summary>
    event Action? SurfaceDestroying;

    /// <summary>
    /// Raised for a Ctrl chord the shell owns rather than the engine.
    /// </summary>
    /// <remarks>
    /// The reason is the native child's (the OS delivers the keyboard there, so
    /// a menu accelerator is inert exactly while somebody is working), and the
    /// composited path inherits it deliberately: the chord table lives in
    /// <see cref="ViewportInputRouter"/>, and two viewports that answered Ctrl+S
    /// differently would be a shortcut that works in one layout and not the
    /// other.
    /// </remarks>
    event Action<ShellChord>? ShellChord;

    /// <summary>
    /// Raised on the UI thread for a right-click that never became a freelook
    /// drag, in the viewport's own FRAMEBUFFER pixels. The shell opens its
    /// context menu; the engine has already seen the balanced button events.
    /// </summary>
    event Action<int, int>? ContextMenuRequested;

    /// <summary>
    /// The running engine's host, once there is one. Setting it is what turns
    /// the viewport's input on; setting it to null is what turns it off.
    /// </summary>
    EngineHost? Host { get; set; }

    /// <summary>
    /// Applies whatever cursor mode the engine has asked for. <b>UI thread
    /// only</b>, once per pass of the shell's pump.
    /// </summary>
    /// <remarks>
    /// The shell's equivalent of the slot <c>Engine.Run</c> applies its own
    /// cursor latch in, and it exists for the identical reason: capturing and
    /// hiding a pointer is window-thread work, and the engine asks for it from
    /// the render thread.
    /// </remarks>
    void PumpCursorMode();

    /// <summary>Hands the keyboard to whatever the engine is listening through.</summary>
    /// <remarks>
    /// The two implementations mean genuinely different things by this. A
    /// native child is not focusable to Avalonia at all and needs Win32
    /// <c>SetFocus</c> on its own HWND; a composited viewport is an ordinary
    /// focusable control and wants <c>Focus()</c>. Calling the wrong one is a
    /// silent no-op, which is how Escape once left the caret in a text box
    /// while every tool key typed into it.
    /// </remarks>
    void FocusEngine();

    /// <summary>
    /// This viewport as a control, for the shell to place and to anchor popups
    /// on. Returns <c>this</c>.
    /// </summary>
    Control Control { get; }
}

/// <summary>
/// Which viewport a session gets, and whether this machine can host one at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>One place decides, because the decision is about to become a policy.</b>
/// The native child is still the default: it is the path with a year of use
/// behind it, and the composited one is opt-in until there is evidence from
/// real sessions to flip it on. That evidence is what this stage produces, and
/// the flip is the next one - so the choice is a function here rather than a
/// <c>new</c> at the call site, where a second one would appear the first time
/// somebody needed a viewport somewhere else.
/// </para>
/// <para>
/// <b>Both paths are Windows-only today and for different reasons.</b> The
/// native child is a Win32 window; the composited one imports a D3D11
/// keyed-mutex NT handle, which is a Windows shared-texture concept, and its
/// cursor lock is <c>ClipCursor</c>. Neither is a permanent limit and both
/// wanted saying out loud rather than degrading.
/// </para>
/// </remarks>
public static class EngineViewports
{
    /// <summary>The command line's opt-in for the composited viewport.</summary>
    public const string CompositedSwitch = "--composited";

    /// <summary>Whether this platform can host the engine in a viewport at all.</summary>
    public static bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>Whether the command line asked for the composited viewport.</summary>
    public static bool CompositedRequested(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        foreach (string arg in args)
        {
            if (string.Equals(arg, CompositedSwitch, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Builds the viewport a session should run in.</summary>
    /// <param name="composited">
    /// Whether to composite the engine's output instead of hosting a native
    /// child window. See <see cref="CompositedRequested"/>.
    /// </param>
    /// <param name="loggerFactory">Owned by the caller.</param>
    /// <param name="onUnavailable">
    /// Called on the UI thread if a composited viewport turns out to be
    /// impossible on this machine. <b>It is asynchronous by construction</b> -
    /// the compositor's GPU interop is negotiated with the render backend after
    /// the control attaches - so there is nothing to return, and a viewport
    /// that failed silently would be an editor with a blank pane and a healthy
    /// status bar.
    /// </param>
    public static IEngineViewport Create(
        bool composited, ILoggerFactory loggerFactory, Action<string>? onUnavailable = null)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        return composited
            ? new CompositionEngineViewport(
                loggerFactory.CreateLogger<CompositionEngineViewport>(), onUnavailable)
            : new Win32EngineViewport();
    }
}
