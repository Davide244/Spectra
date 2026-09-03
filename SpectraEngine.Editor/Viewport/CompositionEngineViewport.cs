using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Hosting;
using SpectraEngine.Core.Input;
using SpectraEngine.Editor.Viewport.Windows;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SpectraEngine.Editor.Viewport;

/// <summary>
/// The pane the engine renders into, as a picture the compositor draws rather
/// than as a window that draws itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the end of airspace.</b> The native child composites above
/// everything Avalonia draws, and that single fact is behind every layout limit
/// the shell has: no overlays over the 3D view, no split views, no dockable
/// viewport, no drag-and-drop into the scene. Here the engine's frame arrives
/// as an imported texture on an ordinary composition visual, so it is a control
/// like any other - it can be clipped, layered under, animated, docked and
/// floated, and the rules that kept the rest of the shell out of its rectangle
/// stop applying to it.
/// </para>
/// <para>
/// <b>Nothing about the frame goes through the UI framework even so.</b> The
/// engine still owns its device, its render thread and its pipeline; what
/// changes is only the last step, where a present becomes a resolve into a
/// shared keyed-mutex texture and the compositor takes the picture from there.
/// The mutex is what paces the two sides, so neither polls the other.
/// </para>
/// <para>
/// <b>Windows only, and for a different reason than the native child's.</b> The
/// import is a D3D11 shared NT handle, which is a Windows concept, and the
/// cursor lock is <c>ClipCursor</c>. Both are real work to port and neither is
/// faked.
/// </para>
/// <para>
/// <b>The input path is the same one, deliberately.</b> Every decision about
/// what a press MEANS lives in <see cref="ViewportInputRouter"/>, which names
/// no platform and no UI framework; this class translates Avalonia's events
/// into router calls and implements <see cref="IViewportCursor"/> for it, which
/// is exactly the shape the Win32 window has. Two viewports that arbitrated a
/// right-click differently would be an editor whose gestures depended on its
/// layout.
/// </para>
/// <para>
/// <b>Threading:</b> UI thread only. Avalonia's compositor import and update
/// calls verify that themselves.
/// </para>
/// </remarks>
internal sealed class CompositionEngineViewport : Control, IEngineViewport, IViewportCursor
{
    private readonly ILogger _logger;
    private readonly Action<string>? _onUnavailable;
    private readonly Action<ViewportChoiceReason>? _onFailure;
    private readonly ViewportInputRouter _router;
    private readonly CompositedRenderSurface _surface = new();
    private readonly Dictionary<StandardCursorType, Cursor> _cursors = [];

    private CompositionDrawingSurface? _drawingSurface;
    private CompositionSurfaceVisual? _visual;
    private CompositedFramePump? _pump;
    private TopLevel? _topLevel;
    private Window? _window;

    private EngineHost? _host;
    private IPointer? _pointer;
    private bool _releasingCapture;

    // Whether the shell has a surface from this viewport with an engine on it,
    // and whether the shell has said the viewport is finished. Two bits, and the
    // distinction between them is the whole of what makes this viewport
    // dockable, so they live in a type with a test on them rather than as fields
    // nothing without a GPU can reach.
    private readonly ViewportSurfaceLifetime _lifetime = new();

    // What the last layout pass left behind, so a move, a resize and a DPI
    // change are one comparison each rather than three subscriptions.
    private PixelPoint _originOnScreen;
    private Size _sizeInDips;
    private double _scaling = 1.0;

    // The cursor last handed to Avalonia, so the shape is written on a change
    // rather than per pump.
    private StandardCursorType? _shownCursor;
    private bool _cursorHidden;

    internal CompositionEngineViewport(
        ILogger logger, Action<string>? onUnavailable, Action<ViewportChoiceReason>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _onUnavailable = onUnavailable;
        _onFailure = onFailure;

        // Unlike a NativeControlHost, this really is focusable, which is what
        // makes FocusEngine a plain Focus() here and a Win32 SetFocus there.
        Focusable = true;

        _router = new ViewportInputRouter(this);
        _router.ShellChord += chord => ShellChord?.Invoke(chord);
        _router.ContextMenuRequested += (x, y) => ContextMenuRequested?.Invoke(x, y);

        // TUNNEL, not bubble, and this is the whole reason the keyboard reaches
        // the engine at all. Alt opens the window menu, Tab moves focus and the
        // arrows drive navigation, all from handlers that run while the event is
        // still on its way down; a bubbling handler sees what is left over.
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnTunnelKeyUp, RoutingStrategies.Tunnel);

        LostFocus += OnViewportLostFocus;
    }

    /// <inheritdoc/>
    public event Action<IRenderSurface>? SurfaceCreated;

    /// <inheritdoc/>
    public event Action? SurfaceDestroying;

    /// <inheritdoc/>
    public event Action<ShellChord>? ShellChord;

    /// <inheritdoc/>
    public event Action<int, int>? ContextMenuRequested;

    /// <inheritdoc/>
    public Control Control => this;

    /// <inheritdoc/>
    public EngineHost? Host
    {
        get => _host;
        set
        {
            _host = value;

            // EngineHost is not itself an IInputSink (it owns one), so the
            // router is handed an adapter rather than the host: the router must
            // be constructible in a test with no engine at all.
            _router.Sink = value is null ? null : new EngineHostSink(value);

            // Clearing the host is the shell's teardown, and it happens BEFORE
            // the session stops. That order is load-bearing: an update already
            // in flight is waiting on a keyed-mutex key only the producer can
            // release, so the producer has to outlive the pump.
            if (value is null)
                _pump?.Stop();
        }
    }

    /// <inheritdoc/>
    public void FocusEngine() => Focus();

    /// <inheritdoc/>
    /// <remarks>
    /// The composited viewport does three things in the shell's once-a-pass
    /// slot rather than one, and they belong together: the engine's cursor
    /// request, the shared target it is currently writing into, and the check
    /// for a hand-over that is never going to finish. All three are the same
    /// kind of thing - render-thread state a UI thread has to act on - and a
    /// second entry point for them would be a second thing to remember to call.
    /// </remarks>
    public void PumpCursorMode()
    {
        if (_host is not { } host)
            return;

        _router.ApplyCursorMode(host.RequestedCursorMode);
        host.ApplyPendingCursorMode();
        ApplyCursorShape(host);

        // LastSnapshot rather than a subscription: it is a reference to an
        // immutable object the render thread publishes, so reading it here is
        // free, allocates nothing and cannot tear. A generation the shell has
        // already imported costs the pump one comparison.
        if (host.LastSnapshot.SharedTarget is { } shared)
            _pump?.Observe(shared);

        _pump?.CheckForStall();
    }

    // --- Lifetime ------------------------------------------------------------

    /// <inheritdoc/>
    /// <remarks>
    /// <b>This runs again every time the pane is dragged into another dock or
    /// out into a float window</b>, which is the whole difference between a
    /// dockable viewport and a pinned one. Everything here is therefore written
    /// to be true the second time as well: the top level and the window are
    /// re-read because a float is a different window with a different
    /// compositor, and the compositor half is rebuilt from scratch because a
    /// visual belongs to the compositor that made it. What is NOT redone is the
    /// surface: the engine is still running against it, still resolving into
    /// its shared target, and has no idea any of this happened.
    /// </remarks>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _topLevel = TopLevel.GetTopLevel(this);

        // The window, not the top level: deactivation is a WINDOW event, and it
        // is the half of focus loss that Avalonia's own focus never reports.
        // The window state is here for the same reason - a minimised window's
        // controls keep their layout bounds, so nothing about the LAYOUT ever
        // says the viewport cannot be seen.
        //
        // PositionChanged is the third, and it is the one nothing else would
        // report: a window that MOVES re-lays out nothing at all, so the pane's
        // screen origin changes with no layout pass behind it and a live cursor
        // lock keeps differencing against where the pane used to be.
        _window = _topLevel as Window;
        if (_window is { } window)
        {
            window.Deactivated += OnWindowDeactivated;
            window.PropertyChanged += OnWindowPropertyChanged;
            window.PositionChanged += OnWindowPositionChanged;
        }

        LayoutUpdated += OnLayoutUpdated;
        ReadGeometry();

        _ = InitializeAsync();

        // A re-parent, not the first attach: the keyboard went wherever the
        // dock drag left it, and a viewport that came back with no focus takes
        // every tool key with it until somebody clicks in the scene.
        if (_lifetime.IsPublished)
            Focus();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>A detach is a RE-PARENT until the shell says otherwise, and getting
    /// that backwards is the one defect a dockable viewport is most likely to
    /// ship with.</b> Dragging the pane into another dock detaches and
    /// re-attaches the control; answered as a teardown it raises
    /// <see cref="SurfaceDestroying"/>, the shell stops the engine, the
    /// re-attach publishes a fresh surface and a SECOND session is built on it -
    /// a new scene, an empty undo history, and the level gone, with nothing
    /// anywhere reporting an error. So the surface is published exactly once per
    /// session and <see cref="Shutdown"/> is the only thing that ends it.
    /// </para>
    /// <para>
    /// <b>What genuinely does go is the compositor half</b>, because a
    /// composition visual belongs to the compositor that created it and a float
    /// is a different window. The pump takes the drawing surface with it, for
    /// the ordering reason written on <c>CompositedFramePump.Stop</c>: disposing
    /// it here would dispose it under whatever hand-over was in flight, and the
    /// fault that produced would be reported as the composited viewport
    /// failing, on every re-dock.
    /// </para>
    /// </remarks>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LayoutUpdated -= OnLayoutUpdated;

        // FIRST, while the window is still known. Releasing the lock unfences
        // the pointer and teleports it back to where the press happened, and
        // that restore point is a CLIENT position: with the top level already
        // forgotten there is nothing to map it through, so the cursor would be
        // put at those numbers read as screen coordinates - a pointer thrown
        // into the corner of the display every time a session closes mid-look.
        _router.ApplyCursorMode(CursorMode.Normal);

        if (_window is { } window)
        {
            window.Deactivated -= OnWindowDeactivated;
            window.PropertyChanged -= OnWindowPropertyChanged;
            window.PositionChanged -= OnWindowPositionChanged;
        }

        // Only when the shell has said this viewport is finished. The engine has
        // to be off the surface by the time this returns, exactly as the native
        // child's teardown demands and for the mirror-image reason - there the
        // driver would be handed a dead window, here the pump would be left
        // waiting on a key nothing is going to release.
        if (_lifetime.Detached())
            SurfaceDestroying?.Invoke();

        // Stopped either way: this pump's compositor objects are about to stop
        // being reachable, and a re-parent builds a fresh one on the other side.
        // Safe on the re-parent path precisely because the producer is still
        // running and will answer whatever hand-over is outstanding.
        _pump?.Stop();
        _pump = null;

        ElementComposition.SetElementChildVisual(this, null);
        _visual = null;

        // NOT disposed here: ownership went to the pump with Stop, which lets go
        // of it once no import is still snapshotting into it.
        _drawingSurface = null;

        _window = null;
        _topLevel = null;

        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc/>
    public void Shutdown()
    {
        // A viewport that never reached the tree, or one the shell is closing
        // while it is floated: the surface is published once and ended once, and
        // the detach that follows must not be able to end it a second time.
        if (_lifetime.Shutdown())
            SurfaceDestroying?.Invoke();

        _pump?.Stop();
    }

    /// <summary>
    /// Negotiates the compositor's GPU interop and publishes the surface.
    /// </summary>
    /// <remarks>
    /// <b>Asynchronous by construction, which is why failure is a callback
    /// rather than a return value.</b> The interop is settled with the render
    /// backend the window is attached to, and there is no synchronous form of
    /// that question - so a machine that cannot composite is discovered after
    /// the control is already in the tree, and saying so out loud is the
    /// difference between an editor with a blank pane and an editor with a
    /// reason in the status bar.
    /// </remarks>
    private async Task InitializeAsync()
    {
        try
        {
            CompositionVisual? element = ElementComposition.GetElementVisual(this);
            if (element?.Compositor is not { } compositor)
            {
                Unavailable("this window has no compositor, so the engine's frame has nowhere to go.");
                return;
            }

            ICompositionGpuInterop? interop = await compositor.TryGetCompositionGpuInterop();
            if (interop is null)
            {
                Unavailable(
                    "this compositor exposes no GPU interop, so an engine frame cannot be imported.");
                return;
            }

            if (!interop.SupportedImageHandleTypes.Contains(
                    KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle))
            {
                Unavailable(
                    "this compositor does not import D3D11 NT handles, which is the only kind the engine " +
                    "produces.");
                return;
            }

            // Detached again while the interop was being negotiated: the pane
            // was closed, and publishing a surface now would start an engine
            // against a control that is no longer anywhere.
            if (_topLevel is null)
                return;

            _drawingSurface = compositor.CreateDrawingSurface();
            _visual = compositor.CreateSurfaceVisual();
            _visual.Surface = _drawingSurface;
            _visual.Size = new Vector(Bounds.Width, Bounds.Height);
            ElementComposition.SetElementChildVisual(this, _visual);

            _pump = new CompositedFramePump(
                new CompositorImageSource(interop, _drawingSurface),
                AcknowledgeRelease,
                _logger,
                onFault: OnPumpFaulted);

            // The engine is started ONCE per session. A re-parent has rebuilt
            // everything above it and disturbed nothing below: the renderer has
            // gone on resolving into its shared target throughout, its own
            // acquire timing out and skipping while there was no consumer, and
            // the fresh pump imports whatever generation the next frame names.
            switch (_lifetime.Attached())
            {
                case ViewportAttach.Publish:
                    _logger.LogInformation(
                        "Composited viewport ready on the compositor's own adapter; the native child is " +
                        "not in use.");
                    SurfaceCreated?.Invoke(_surface);
                    break;

                case ViewportAttach.Resume:
                    _logger.LogInformation(
                        "Composited viewport re-attached; the session's shared target will be re-imported.");
                    break;

                default:
                    _logger.LogDebug(
                        "Composited viewport attached after shutdown; publishing nothing.");
                    break;
            }
        }
        catch (Exception ex)
        {
            // The driver under this is exactly the unknown, so a failure here
            // reports rather than taking the shell down with it.
            _logger.LogError(ex, "The composited viewport could not be set up");
            Unavailable($"the composited viewport could not be set up: {ex.Message}");
        }
    }

    private void Unavailable(string reason)
    {
        _logger.LogError("Composited viewport unavailable: {Reason}", reason);
        _onUnavailable?.Invoke(
            $"The composited viewport is not available here: {reason} Relaunch with --viewport=native.");

        // A session that was already running when this happened did not merely
        // fail to start: it stopped showing a picture, which is the one thing
        // the green history exists to count. Without this a viewport that
        // re-attached into a window whose compositor refused it would still be
        // recorded as a clean session and go on earning the flip to composition.
        if (_lifetime.IsPublished)
            _onFailure?.Invoke(ViewportChoiceReason.FirstUpdateFaulted);
    }

    /// <summary>
    /// The window moved on screen. Layout reports nothing at all for this.
    /// </summary>
    /// <remarks>
    /// <b>The gap <see cref="OnLayoutUpdated"/> structurally cannot see.</b> A
    /// window that is dragged, snapped, moved by a keyboard chord or shifted by
    /// a display change re-lays out none of its contents, so every bound in the
    /// tree is unchanged and the pane's SCREEN origin is not - which is exactly
    /// the value a live cursor lock differences against.
    /// </remarks>
    private void OnWindowPositionChanged(object? sender, PixelPointEventArgs e) => ReadGeometry();

    /// <summary>
    /// The hand-over stopped while the session was running.
    /// </summary>
    /// <remarks>
    /// <b>Reported and nothing else, deliberately.</b> Swapping to the native
    /// child here would tear down a live engine, destroy every GPU resource it
    /// owns and rebuild the pane under whatever the user was in the middle of -
    /// and would leave two hosting models in one session's log, which is a bug
    /// report nobody can write. The session stays where it is, says what
    /// happened, and names the switch that avoids it next time.
    /// </remarks>
    private void OnPumpFaulted() => _onFailure?.Invoke(ViewportChoiceReason.FirstUpdateFaulted);

    /// <summary>
    /// Tells the engine a retired shared-target generation may be freed.
    /// </summary>
    /// <remarks>
    /// <b>Through the host's own latch, which is where every piece of engine
    /// state a UI drives goes.</b> The resource is the render thread's and is
    /// held precisely because this side might still have been sampling it, so
    /// the answer is applied over there, in the frame's once-a-pass slot,
    /// rather than by anybody touching a renderer from here. A host that has
    /// already been cleared simply never hears it, and the renderer frees
    /// everything on shutdown anyway.
    /// </remarks>
    private void AcknowledgeRelease(int generation) =>
        _host?.NotifySharedTargetReleased(generation);

    // --- Geometry ------------------------------------------------------------

    /// <inheritdoc/>
    /// <remarks>
    /// <b>A fill is what makes a control hit-testable.</b> Avalonia hit-tests
    /// against what a visual actually drew, so a viewport that painted nothing
    /// would render the scene perfectly and never receive a click - the same
    /// failure, from the opposite direction, that made the shell create its own
    /// native child in the first place. Transparent rather than a colour,
    /// because the bezel behind it already owns the pixels and inventing one
    /// here would put a literal colour outside the theme.
    /// </remarks>
    public override void Render(DrawingContext context) =>
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

    private void OnLayoutUpdated(object? sender, EventArgs e) => ReadGeometry();

    /// <summary>
    /// Picks up a move, a resize or a scaling change, whichever happened.
    /// </summary>
    /// <remarks>
    /// <b>One place, because they are one question asked three ways</b> - and
    /// because the move is the one nothing else would notice. The cursor lock
    /// pins the pointer at a SCREEN point derived from the viewport's centre,
    /// so a pane that slides sideways under a live freelook leaves the anchor
    /// pointing at where it used to be and the very next mouse move hands the
    /// engine the whole displacement as one frame of look. The native child
    /// could not reach that state; a composited one can be re-laid-out under a
    /// held button by anything on the window.
    /// </remarks>
    private void ReadGeometry()
    {
        if (_topLevel is null)
            return;

        double scaling = _topLevel.RenderScaling;
        Size size = Bounds.Size;

        PixelPoint origin;
        try
        {
            origin = this.PointToScreen(default);
        }
        catch (InvalidOperationException)
        {
            // Between attach and the first layout there is no root to measure
            // against. The next pass answers.
            return;
        }

        bool resized = size != _sizeInDips || scaling != _scaling;
        bool moved = origin != _originOnScreen;

        _sizeInDips = size;
        _scaling = scaling;
        _originOnScreen = origin;

        if (resized)
        {
            if (_visual is { } visual)
                visual.Size = new Vector(size.Width, size.Height);

            _surface.SetPixelSize(
                (int)Math.Round(size.Width * scaling), (int)Math.Round(size.Height * scaling));
        }

        if (moved || resized)
            _router.OnViewportMoved();

        UpdateVisibility();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
            UpdateVisibility();
    }

    /// <summary>
    /// Whether there is any point taking the next frame.
    /// </summary>
    /// <remarks>
    /// <b>Two conditions, because neither implies the other and the second one
    /// is invisible to layout.</b> A pane collapsed to nothing has no bounds;
    /// a MINIMISED window's controls keep theirs exactly, so a viewport nobody
    /// can see goes on being laid out at its full size and looks, to every
    /// signal a control has, like it is on screen. Measured: without the window
    /// state the pump kept copying a full-screen texture per vsync for a
    /// minimised editor, and the producer kept rendering full frames to feed
    /// it.
    /// </remarks>
    private void UpdateVisibility()
    {
        bool onScreen = Bounds.Width > 0
            && Bounds.Height > 0
            && _window is not { WindowState: WindowState.Minimized };

        _pump?.SetVisible(onScreen);
    }

    // --- Pointer -------------------------------------------------------------

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        _pointer = e.Pointer;

        // INTERMEDIATE points, not just the current one. A pointer moving fast
        // between two UI frames arrives as one event carrying the whole path,
        // and a handler that reads only the last position loses every bit of
        // travel in between - which a freelook feels as a camera that drifts
        // behind the hand and a marquee feels as a rubber band that skips.
        var path = e.GetIntermediatePoints(this);
        if (path.Count > 0)
        {
            foreach (var point in path)
                SubmitMove(point.Position);
        }
        else
        {
            SubmitMove(e.GetPosition(this));
        }

        e.Handled = true;
        base.OnPointerMoved(e);
    }

    private void SubmitMove(Point positionInDips)
    {
        (int x, int y) = ToPixels(positionInDips);
        _router.OnPointerMove(x, y);
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _pointer = e.Pointer;

        PointerButtons button = AvaloniaKeys.ToPointerButton(
            e.GetCurrentPoint(this).Properties.PointerUpdateKind);
        if (button is PointerButtons.None)
        {
            base.OnPointerPressed(e);
            return;
        }

        // Focus follows the click, because a viewport that responds to the
        // mouse while every shortcut goes to the panel beside it is the exact
        // half-working state the native child's SetFocus exists to avoid.
        Focus();

        // The position first: the router's right-press arbitration measures
        // travel from where the press happened, and a press whose position
        // arrived only with the following move would start it in the wrong
        // place.
        SubmitMove(e.GetPosition(this));
        _router.OnPointerDown(button);

        e.Handled = true;
        base.OnPointerPressed(e);
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _pointer = e.Pointer;

        PointerButtons button = AvaloniaKeys.ToPointerButton(
            e.GetCurrentPoint(this).Properties.PointerUpdateKind);
        if (button is PointerButtons.None)
        {
            base.OnPointerReleased(e);
            return;
        }

        _router.OnPointerUp(button);

        e.Handled = true;
        base.OnPointerReleased(e);
    }

    /// <inheritdoc/>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        _pointer = e.Pointer;
        _router.OnScroll((float)e.Delta.X, (float)e.Delta.Y);

        e.Handled = true;
        base.OnPointerWheelChanged(e);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>A capture can be taken away without focus moving</b> - a touch
    /// cancelled by the system, another control grabbing the pointer, a drag
    /// leaving for a different window - and the release that ended the press is
    /// then never coming. The gesture is cancelled with balanced button
    /// releases rather than with the release-everything event a focus loss
    /// uses, because the keyboard was not lost and dropping the held movement
    /// keys would stop a freelook that is still perfectly valid.
    /// </remarks>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        // Except when this side let go on purpose: releasing a capture raises
        // this event, so without the guard every ordinary button release would
        // cancel the gesture it just finished.
        if (!_releasingCapture)
            _router.OnPointerCaptureLost();

        base.OnPointerCaptureLost(e);
    }

    // --- Keyboard and focus --------------------------------------------------

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        _router.OnKeyDown(AvaloniaKeys.ToInputKey(e.Key), AvaloniaKeys.ToModifiers(e.KeyModifiers));

        // Claimed whether or not the router wanted it, which is what the native
        // child's window procedure does by construction: while the viewport has
        // focus the engine is the keyboard's owner, and a key left to bubble
        // would reach a menu accelerator or the focus navigator instead of the
        // scene the user is looking at.
        e.Handled = true;
    }

    private void OnTunnelKeyUp(object? sender, KeyEventArgs e)
    {
        _router.OnKeyUp(AvaloniaKeys.ToInputKey(e.Key));
        e.Handled = true;
    }

    private void OnViewportLostFocus(object? sender, RoutedEventArgs e) => _router.OnFocusLost();

    /// <summary>
    /// The window went to the background with the viewport still focused.
    /// </summary>
    /// <remarks>
    /// <b>Both this and <see cref="OnLostFocus"/>, because neither implies the
    /// other.</b> Alt-tabbing away leaves Avalonia's focus exactly where it
    /// was, so a viewport that only listened for lost focus would keep a
    /// captured cursor and a held button across the switch, with no event
    /// anywhere that would ever lift them.
    /// </remarks>
    private void OnWindowDeactivated(object? sender, EventArgs e) => _router.OnFocusLost();

    // --- Cursor --------------------------------------------------------------

    private void ApplyCursorShape(EngineHost host)
    {
        if (_cursorHidden)
            return;

        StandardCursorType wanted = AvaloniaKeys.ToStandardCursor(host.RequestedCursorShape);
        if (_shownCursor == wanted)
            return;

        _shownCursor = wanted;
        Cursor = CursorFor(wanted);
    }

    private Cursor CursorFor(StandardCursorType type)
    {
        if (_cursors.TryGetValue(type, out Cursor? cursor))
            return cursor;

        cursor = new Cursor(type);
        _cursors[type] = cursor;
        return cursor;
    }

    // --- IViewportCursor -----------------------------------------------------

    /// <inheritdoc/>
    ViewportSize IViewportCursor.ClientSize
    {
        get
        {
            Silk.NET.Maths.Vector2D<int> size = _surface.PixelSize;
            return new ViewportSize(size.X, size.Y);
        }
    }

    /// <inheritdoc/>
    int IViewportCursor.DragSlack
    {
        get
        {
            // Half of SM_CXDRAG, because the metric is the full width of the
            // rectangle and the travel is measured from its centre. In the
            // viewport's own scaling, so a click may wander the same physical
            // distance on a 200% display as on a 100% one - and read per press
            // rather than cached, since a window can be dragged between
            // monitors between one press and the next.
            const int fallback = 4;

            try
            {
                int width = Win32Interop.GetSystemMetricsForDpi(
                    Win32Interop.SM_CXDRAG, (uint)Math.Round(96.0 * _scaling));
                return width > 1 ? width / 2 : fallback;
            }
            catch (EntryPointNotFoundException)
            {
                // Pre-1607 Windows has no per-DPI metrics; the constant is what
                // the metric returns at 100% anyway.
                return fallback;
            }
        }
    }

    /// <inheritdoc/>
    ViewportPoint IViewportCursor.ClientToScreen(ViewportPoint client)
    {
        if (_topLevel is null)
            return client;

        try
        {
            // The router works in framebuffer pixels and Avalonia in logical
            // units, so the trip out goes through the scaling and the trip back
            // does not: PointToScreen already answers in physical screen
            // pixels, which is what SetCursorPos and ClipCursor want.
            PixelPoint screen = this.PointToScreen(
                new Point(client.X / _scaling, client.Y / _scaling));
            return new ViewportPoint(screen.X, screen.Y);
        }
        catch (InvalidOperationException)
        {
            return client;
        }
    }

    /// <inheritdoc/>
    void IViewportCursor.MoveCursor(int screenX, int screenY) =>
        Win32Interop.SetCursorPos(screenX, screenY);

    /// <inheritdoc/>
    void IViewportCursor.ClipToClient(bool clip)
    {
        if (!clip)
        {
            Win32Interop.ClipCursorRelease(0);
            return;
        }

        if (_topLevel is null)
            return;

        try
        {
            PixelPoint topLeft = this.PointToScreen(default);
            PixelPoint bottomRight = this.PointToScreen(new Point(Bounds.Width, Bounds.Height));

            Win32Interop.ClipCursor(new Win32Interop.RECT
            {
                Left = topLeft.X,
                Top = topLeft.Y,
                Right = bottomRight.X,
                Bottom = bottomRight.Y,
            });
        }
        catch (InvalidOperationException)
        {
            // No visual root to measure against; there is nothing to fence.
        }
    }

    /// <inheritdoc/>
    void IViewportCursor.SetCursorHidden(bool hidden)
    {
        _cursorHidden = hidden;

        if (hidden)
        {
            _shownCursor = null;
            Cursor = CursorFor(StandardCursorType.None);
        }
        else
        {
            _shownCursor = StandardCursorType.Arrow;
            Cursor = CursorFor(StandardCursorType.Arrow);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Avalonia's capture, not Win32's.</b> The composited viewport lives
    /// inside somebody else's window, and taking the OS capture on that HWND
    /// would fight the framework that already owns the pointer for it.
    /// Releasing raises <c>PointerCaptureLost</c>, which is why the guard is
    /// here rather than in the handler alone.
    /// </remarks>
    void IViewportCursor.SetPointerCapture(bool captured)
    {
        if (_pointer is not { } pointer)
            return;

        if (captured)
        {
            pointer.Capture(this);
            return;
        }

        _releasingCapture = true;
        try
        {
            pointer.Capture(null);
        }
        finally
        {
            _releasingCapture = false;
        }
    }

    // --- Plumbing ------------------------------------------------------------

    private (int X, int Y) ToPixels(Point dips) =>
        ((int)Math.Round(dips.X * _scaling), (int)Math.Round(dips.Y * _scaling));

    // The host owns an input sink rather than being one, and the router must not
    // name EngineHost at all: it is constructed in tests against a recorder.
    private sealed class EngineHostSink(EngineHost host) : IInputSink
    {
        public void Submit(in InputEvent input) => host.SubmitInput(in input);
    }

    /// <summary>
    /// The real compositor behind <see cref="CompositedFramePump"/>'s seam.
    /// </summary>
    private sealed class CompositorImageSource(
        ICompositionGpuInterop interop, CompositionDrawingSurface surface) : ICompositedImageSource
    {
        public ICompositedImage Import(nint ntHandle, int width, int height)
        {
            ICompositionImportedGpuImage image = interop.ImportImage(
                new PlatformHandle(
                    ntHandle, KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle),
                new PlatformGraphicsExternalImageProperties
                {
                    Width = width,
                    Height = height,

                    // The engine's shared resource is UNORM with an sRGB view
                    // over it, so the bytes on the way out are already encoded
                    // and this names the layout rather than a colour space.
                    Format = PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm,

                    // A D3D render target's first row is its top one, unlike a
                    // GL framebuffer's. Getting this wrong flips the picture
                    // rather than failing anything.
                    TopLeftOrigin = true,
                });

            return new CompositorImage(image, surface);
        }

        /// <summary>
        /// Lets go of the drawing surface every import snapshots into.
        /// </summary>
        /// <remarks>
        /// <b>The pump calls this, and only once its last import has settled.</b>
        /// The viewport used to dispose the surface itself at the moment it left
        /// the visual tree, which was safe while a detach only ever meant a
        /// session ending and is not once the pane can be dragged into another
        /// dock: there the disposal lands under whatever hand-over was still in
        /// flight, the pending update faults, and the fault is reported as the
        /// composited viewport having failed.
        /// </remarks>
        public ValueTask DisposeAsync()
        {
            surface.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CompositorImage(
        ICompositionImportedGpuImage image, CompositionDrawingSurface surface) : ICompositedImage
    {
        public Task ImportCompleted => image.ImportCompleted;

        public Task UpdateAsync(uint acquireKey, uint releaseKey) =>
            surface.UpdateWithKeyedMutexAsync(image, acquireKey, releaseKey);

        public ValueTask DisposeAsync() => image.DisposeAsync();
    }
}
