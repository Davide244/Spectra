using SpectraEngine.Core.Input;
using SpectraEngine.Editor.Viewport;

namespace SpectraEngine.Editor.Tests;

/// <summary>
/// The viewport's input arbitration: the cursor lock, the right-click-versus-
/// right-drag decision, the shell's chord table and the focus-loss path.
/// </summary>
/// <remarks>
/// <b>None of this had ever been reachable from a test.</b> It lived inside a
/// window procedure, so proving any of it needed a real HWND and a message pump,
/// and every one of these behaviours fails SILENTLY when it breaks: a cursor
/// that lands outside the window, a button the engine still believes is held, a
/// context menu that opens only when you click quickly. The router exists so
/// that the next host inherits the answers rather than rediscovering them.
/// </remarks>
public sealed class ViewportInputRouterTests
{
    // A viewport 800x600 whose top-left client pixel sits at screen (100, 50),
    // so the lock's centre anchor is client (400, 300) and screen (500, 350).
    private const int ViewportWidth = 800;
    private const int ViewportHeight = 600;
    private const int OriginX = 100;
    private const int OriginY = 50;
    private const int AnchorScreenX = OriginX + (ViewportWidth / 2);
    private const int AnchorScreenY = OriginY + (ViewportHeight / 2);

    // What SM_CXDRAG halves to at 100%: a press may travel four pixels and still
    // be a click.
    private const int Slack = 4;

    /// <summary>
    /// The platform half, recorded rather than performed. Screen space is client
    /// space translated by the origin, which is all a child window's mapping
    /// ever is.
    /// </summary>
    private sealed class FakeCursor : IViewportCursor
    {
        internal List<string> Calls { get; } = [];

        internal ViewportSize Size { get; set; } = new(ViewportWidth, ViewportHeight);

        internal ViewportPoint Origin { get; set; } = new(OriginX, OriginY);

        internal int Slack { get; set; } = ViewportInputRouterTests.Slack;

        /// <summary>Where the OS cursor was last put.</summary>
        internal ViewportPoint Position { get; private set; }

        internal bool Clipped { get; private set; }

        internal bool Hidden { get; private set; }

        internal bool Captured { get; private set; }

        public ViewportSize ClientSize => Size;

        public int DragSlack => Slack;

        public ViewportPoint ClientToScreen(ViewportPoint client) =>
            new(client.X + Origin.X, client.Y + Origin.Y);

        public void MoveCursor(int screenX, int screenY)
        {
            Position = new ViewportPoint(screenX, screenY);
            Calls.Add($"move {screenX},{screenY}");
        }

        public void ClipToClient(bool clip)
        {
            Clipped = clip;
            Calls.Add(clip ? "clip" : "unclip");
        }

        public void SetCursorHidden(bool hidden)
        {
            Hidden = hidden;
            Calls.Add(hidden ? "hide" : "show");
        }

        public void SetPointerCapture(bool captured)
        {
            Captured = captured;
            Calls.Add(captured ? "capture" : "release");
        }
    }

    /// <summary>The engine, reduced to what it receives.</summary>
    private sealed class RecordingSink : IInputSink
    {
        internal List<InputEvent> Events { get; } = [];

        public void Submit(in InputEvent input) => Events.Add(input);

        /// <summary>
        /// The buttons the engine would still believe are held, reduced from the
        /// stream exactly as <c>InputManager</c> reduces it.
        /// </summary>
        /// <remarks>
        /// <b>A focus loss is a release, not an absence of one.</b> The engine
        /// releases everything held when it is told the viewport lost focus, so
        /// the router must not also synthesise a button-up per held button: that
        /// would arm the same release edges twice. What has to hold is that the
        /// stream never leaves a button down with no release of any kind, which
        /// is what this replay measures.
        /// </remarks>
        internal PointerButtons ReplayHeldButtons()
        {
            PointerButtons held = PointerButtons.None;

            foreach (InputEvent input in Events)
            {
                switch (input.Kind)
                {
                    case InputEventKind.PointerDown: held |= input.Button; break;
                    case InputEventKind.PointerUp: held &= ~input.Button; break;
                    case InputEventKind.FocusLost: held = PointerButtons.None; break;
                }
            }

            return held;
        }
    }

    private readonly FakeCursor _cursor = new();
    private readonly RecordingSink _sink = new();
    private readonly List<ShellChord> _chords = [];
    private readonly List<ViewportPoint> _menus = [];
    private readonly ViewportInputRouter _router;

    public ViewportInputRouterTests()
    {
        _router = new ViewportInputRouter(_cursor) { Sink = _sink };
        _router.ShellChord += chord => _chords.Add(chord);
        _router.ContextMenuRequested += (x, y) => _menus.Add(new ViewportPoint(x, y));
    }

    // --- The cursor lock -----------------------------------------------------

    [Fact]
    public void The_lock_pins_the_cursor_at_the_centre_of_the_viewport()
    {
        // The middle rather than wherever the pointer happens to be: pinning at
        // an edge means half the mouse's travel leaves the window before the
        // teleport catches it.
        _router.OnPointerMove(10, 20);
        _router.ApplyCursorMode(CursorMode.Locked);

        _router.IsCursorLocked.ShouldBeTrue();
        _cursor.Position.ShouldBe(new ViewportPoint(AnchorScreenX, AnchorScreenY));
        _cursor.Hidden.ShouldBeTrue();
        _cursor.Clipped.ShouldBeTrue();
        _cursor.Captured.ShouldBeTrue();
    }

    [Fact]
    public void A_locked_move_is_measured_against_the_anchor_and_re_pinned()
    {
        _router.ApplyCursorMode(CursorMode.Locked);
        _sink.Events.Clear();

        // Twelve pixels right of the centre, reported as an absolute client
        // position because that is the only shape Windows has for a mouse move.
        _router.OnPointerMove((ViewportWidth / 2) + 12, (ViewportHeight / 2) - 5);

        InputEvent submitted = _sink.Events.ShouldHaveSingleItem();
        submitted.Kind.ShouldBe(InputEventKind.PointerDelta);
        submitted.Value.X.ShouldBe(12f);
        submitted.Value.Y.ShouldBe(-5f);

        // Back on the anchor, so the next move is measured from the same place
        // and the pointer never reaches the edge of the screen.
        _cursor.Position.ShouldBe(new ViewportPoint(AnchorScreenX, AnchorScreenY));
    }

    [Fact]
    public void The_re_pin_echo_is_not_reported_as_motion()
    {
        // Moving the cursor generates a move message of its own, which arrives
        // as a zero delta. Passing it on would be a stream of no-op events; the
        // exact-equality test is why the point type is integer.
        _router.ApplyCursorMode(CursorMode.Locked);
        _sink.Events.Clear();

        _router.OnPointerMove(ViewportWidth / 2, ViewportHeight / 2);

        _sink.Events.ShouldBeEmpty();
    }

    [Fact]
    public void Unlocking_releases_the_clip_strictly_before_the_restore_teleport()
    {
        // ORDER, not merely presence. The fence is the whole client rect, so a
        // restore point outside it (the look began near the pane's edge) is
        // clamped onto the fence line if the teleport runs first, and the cursor
        // lands somewhere the user never put it.
        _router.OnPointerMove(20, 30);
        _router.ApplyCursorMode(CursorMode.Locked);
        _router.ApplyCursorMode(CursorMode.Normal);

        int unclip = _cursor.Calls.IndexOf("unclip");
        int restore = _cursor.Calls.IndexOf($"move {20 + OriginX},{30 + OriginY}");

        unclip.ShouldBeGreaterThanOrEqualTo(0);
        restore.ShouldBeGreaterThanOrEqualTo(0);
        unclip.ShouldBeLessThan(restore);
    }

    [Fact]
    public void Unlocking_restores_the_press_point_rather_than_the_anchor()
    {
        // The anchor is the viewport's centre and the press was not, so
        // restoring to the anchor would teleport the pointer at the end of every
        // freelook.
        _router.OnPointerMove(20, 30);
        _router.ApplyCursorMode(CursorMode.Locked);
        _router.ApplyCursorMode(CursorMode.Normal);

        _cursor.Position.ShouldBe(new ViewportPoint(20 + OriginX, 30 + OriginY));
        _cursor.Hidden.ShouldBeFalse();
        _cursor.Clipped.ShouldBeFalse();
        _router.IsCursorLocked.ShouldBeFalse();
    }

    [Fact]
    public void A_viewport_that_moves_under_a_live_lock_re_anchors()
    {
        // The anchor is a SCREEN point derived from the client centre, so a pane
        // that moves without resizing leaves it pointing where the pane used to
        // be, and the next move hands the engine the whole displacement as one
        // frame of look.
        _router.ApplyCursorMode(CursorMode.Locked);
        _cursor.Origin = new ViewportPoint(OriginX + 300, OriginY + 40);
        _router.OnViewportMoved();

        _cursor.Position.ShouldBe(new ViewportPoint(AnchorScreenX + 300, AnchorScreenY + 40));

        _sink.Events.Clear();
        _router.OnPointerMove((ViewportWidth / 2) + 7, ViewportHeight / 2);

        InputEvent submitted = _sink.Events.ShouldHaveSingleItem();
        submitted.Value.X.ShouldBe(7f);
        submitted.Value.Y.ShouldBe(0f);
    }

    // --- Focus loss ----------------------------------------------------------

    [Fact]
    public void Focus_loss_ends_the_lock_and_leaves_no_button_held()
    {
        // The releases that would have ended these presses were delivered to
        // whoever took the focus and are never coming.
        _router.OnPointerMove(40, 40);
        _router.OnPointerDown(PointerButtons.Left);
        _router.OnPointerDown(PointerButtons.Right);
        _router.ApplyCursorMode(CursorMode.Locked);

        _router.OnFocusLost();

        _router.IsCursorLocked.ShouldBeFalse();
        _router.ButtonsDown.ShouldBe(PointerButtons.None);
        _cursor.Clipped.ShouldBeFalse();
        _cursor.Hidden.ShouldBeFalse();

        _sink.Events[^1].Kind.ShouldBe(InputEventKind.FocusLost);
        _sink.ReplayHeldButtons().ShouldBe(PointerButtons.None);
    }

    [Fact]
    public void A_lost_capture_ends_the_gesture_without_dropping_the_keyboard()
    {
        // A capture can be taken away with focus staying exactly where it is: a
        // system-cancelled touch, another control grabbing the pointer, a drag
        // that left for a different window. The releases are never coming, so
        // each held button gets one, and the gesture ends the way letting go
        // would have ended it.
        _router.OnPointerMove(40, 40);
        _router.OnPointerDown(PointerButtons.Left);
        _router.OnPointerDown(PointerButtons.Middle);

        _router.OnPointerCaptureLost();

        _router.ButtonsDown.ShouldBe(PointerButtons.None);
        _sink.ReplayHeldButtons().ShouldBe(PointerButtons.None);

        // Balanced ups, NOT the release-everything event: the keyboard is still
        // here, and FocusLost would drop the movement keys out from under a
        // freelook that is otherwise perfectly valid.
        _sink.Events.ShouldNotContain(input => input.Kind == InputEventKind.FocusLost);
    }

    [Fact]
    public void A_lost_capture_opens_no_context_menu()
    {
        // A right press whose release was taken away is not a click. Opening
        // the menu here would put one on screen from a button nobody let go of,
        // in the middle of whatever took the pointer away.
        _router.OnPointerMove(30, 30);
        _router.OnPointerDown(PointerButtons.Right);

        _router.OnPointerCaptureLost();

        _menus.ShouldBeEmpty();
        _router.ButtonsDown.ShouldBe(PointerButtons.None);
    }

    [Fact]
    public void A_lost_capture_with_nothing_held_is_nothing()
    {
        // The ordinary case, because releasing a capture RAISES the loss: every
        // gesture that ends normally arrives here one line later, and a router
        // that synthesised anything would double every button release in the
        // shell.
        _router.OnPointerMove(30, 30);
        _router.OnPointerDown(PointerButtons.Left);
        _router.OnPointerUp(PointerButtons.Left);

        int before = _sink.Events.Count;
        _router.OnPointerCaptureLost();

        _sink.Events.Count.ShouldBe(before);
    }

    [Fact]
    public void No_button_is_left_down_across_a_lock_transition()
    {
        // A whole freelook gesture: press, lock, look, unlock, release. The
        // stream either side of the lock has to reduce to nothing held, because
        // the lock changes the shape of the MOVES and must not touch the
        // buttons.
        _router.OnPointerMove(50, 50);
        _router.OnPointerDown(PointerButtons.Right);
        _router.ApplyCursorMode(CursorMode.Locked);
        _router.OnPointerMove((ViewportWidth / 2) + 20, ViewportHeight / 2);
        _router.ApplyCursorMode(CursorMode.Normal);
        _router.OnPointerUp(PointerButtons.Right);

        _router.ButtonsDown.ShouldBe(PointerButtons.None);
        _sink.ReplayHeldButtons().ShouldBe(PointerButtons.None);
        _cursor.Captured.ShouldBeFalse();
    }

    // --- Right click versus right drag ---------------------------------------

    [Fact]
    public void A_hesitant_click_inside_the_drag_slack_opens_the_context_menu()
    {
        // A press held while the hand shakes is still a click: the arbitration
        // has no time component at all, exactly like the rectangle Windows draws
        // around a press point.
        _router.OnPointerMove(200, 150);
        _router.OnPointerDown(PointerButtons.Right);
        _router.OnPointerMove(202, 150);
        _router.OnPointerMove(201, 151);
        _router.OnPointerMove(204, 150);
        _router.OnPointerUp(PointerButtons.Right);

        _menus.ShouldHaveSingleItem().ShouldBe(new ViewportPoint(200, 150));
    }

    [Fact]
    public void A_press_that_leaves_the_drag_slack_never_opens_the_menu()
    {
        // And it is one-way: coming back inside afterwards does not turn a
        // freelook into a click.
        _router.OnPointerMove(200, 150);
        _router.OnPointerDown(PointerButtons.Right);
        _router.OnPointerMove(205 + Slack, 150);
        _router.OnPointerMove(200, 150);
        _router.OnPointerUp(PointerButtons.Right);

        _menus.ShouldBeEmpty();
    }

    [Fact]
    public void Travel_accumulates_across_both_move_shapes()
    {
        // The lock engages a frame or two after the press, so a gesture is
        // measured half in absolute positions and half in raw deltas. Three
        // pixels in each shape is inside the slack twice over and outside it
        // once, which is the case a per-shape budget would get wrong.
        _router.OnPointerMove(200, 150);
        _router.OnPointerDown(PointerButtons.Right);
        _router.OnPointerMove(203, 150);
        _router.ApplyCursorMode(CursorMode.Locked);
        _router.OnPointerMove((ViewportWidth / 2) + 3, ViewportHeight / 2);
        _router.OnPointerUp(PointerButtons.Right);

        _menus.ShouldBeEmpty();
    }

    [Fact]
    public void The_arbitration_is_net_displacement_rather_than_path_length()
    {
        // Four pixels out and four back is eight pixels of path and no
        // displacement at all. Summing the path made an ordinary hesitant click
        // on a high-dpi mouse silently open no menu while clicking faster
        // "fixed" it, which is a bug nobody can report.
        _router.OnPointerMove(200, 150);
        _router.OnPointerDown(PointerButtons.Right);
        _router.OnPointerMove(200 + Slack, 150);
        _router.ApplyCursorMode(CursorMode.Locked);
        _router.OnPointerMove((ViewportWidth / 2) - Slack, ViewportHeight / 2);
        _router.OnPointerUp(PointerButtons.Right);

        _menus.ShouldHaveSingleItem().ShouldBe(new ViewportPoint(200, 150));
    }

    [Fact]
    public void The_slack_is_read_from_the_host_at_every_press()
    {
        // Per-window DPI, and a window can be dragged between monitors of
        // different scaling between one press and the next.
        _cursor.Slack = 20;

        _router.OnPointerMove(200, 150);
        _router.OnPointerDown(PointerButtons.Right);
        _router.OnPointerMove(215, 150);
        _router.OnPointerUp(PointerButtons.Right);

        _menus.ShouldHaveSingleItem().ShouldBe(new ViewportPoint(200, 150));
    }

    [Fact]
    public void A_left_press_is_never_a_context_menu()
    {
        _router.OnPointerMove(200, 150);
        _router.OnPointerDown(PointerButtons.Left);
        _router.OnPointerUp(PointerButtons.Left);

        _menus.ShouldBeEmpty();
    }

    [Fact]
    public void The_menu_opens_after_the_engine_has_seen_the_release()
    {
        // So that the freelook has ended and given its cursor request back
        // before the shell opens anything over the viewport.
        InputEventKind lastBeforeMenu = InputEventKind.FocusLost;
        _router.ContextMenuRequested += (_, _) => lastBeforeMenu = _sink.Events[^1].Kind;

        _router.OnPointerMove(200, 150);
        _router.OnPointerDown(PointerButtons.Right);
        _router.OnPointerUp(PointerButtons.Right);

        lastBeforeMenu.ShouldBe(InputEventKind.PointerUp);
    }

    // --- The shell's chords --------------------------------------------------

    [Theory]
    [InlineData(InputKey.N, ShellChord.NewMap)]
    [InlineData(InputKey.O, ShellChord.OpenMap)]
    [InlineData(InputKey.S, ShellChord.SaveMap)]
    [InlineData(InputKey.Number1, ShellChord.InsertBlock)]
    [InlineData(InputKey.Number2, ShellChord.InsertPart)]
    [InlineData(InputKey.Number3, ShellChord.InsertCut)]
    [InlineData(InputKey.Number4, ShellChord.InsertLight)]
    public void A_chord_in_the_table_is_claimed_and_kept_from_the_engine(InputKey key, ShellChord expected)
    {
        _router.OnKeyDown(key, KeyModifiers.Control).ShouldBeTrue();

        _chords.ShouldHaveSingleItem().ShouldBe(expected);
        _sink.Events.ShouldBeEmpty();
    }

    [Fact]
    public void Shift_selects_save_as()
    {
        _router.OnKeyDown(InputKey.S, KeyModifiers.Control | KeyModifiers.Shift).ShouldBeTrue();

        _chords.ShouldHaveSingleItem().ShouldBe(ShellChord.SaveMapAs);
    }

    [Theory]
    [InlineData(InputKey.W)]
    [InlineData(InputKey.Number5)]
    [InlineData(InputKey.Z)]
    public void A_key_outside_the_table_reaches_the_engine_even_with_control(InputKey key)
    {
        // The table is short and closed on purpose: every chord on it is one the
        // engine can no longer see, and Ctrl+Z is the editor's own.
        _router.OnKeyDown(key, KeyModifiers.Control).ShouldBeFalse();

        _chords.ShouldBeEmpty();
        _sink.Events.ShouldHaveSingleItem().Key.ShouldBe(key);
    }

    [Fact]
    public void A_chord_key_without_control_is_just_a_key()
    {
        _router.OnKeyDown(InputKey.S, KeyModifiers.None).ShouldBeFalse();

        _chords.ShouldBeEmpty();
        _sink.Events.ShouldHaveSingleItem().Key.ShouldBe(InputKey.S);
    }

    [Fact]
    public void A_document_chord_during_a_freelook_goes_to_the_engine()
    {
        // During a look Ctrl is the descend key and S flies backwards, so the
        // chord would fire from the ordinary descend-while-reversing gesture,
        // pop a save dialog mid-flight and eat the movement key.
        _router.ApplyCursorMode(CursorMode.Locked);

        _router.OnKeyDown(InputKey.S, KeyModifiers.Control).ShouldBeFalse();

        _chords.ShouldBeEmpty();
        _sink.Events.ShouldHaveSingleItem().Key.ShouldBe(InputKey.S);
    }

    [Fact]
    public void The_chords_come_back_when_the_look_ends()
    {
        _router.ApplyCursorMode(CursorMode.Locked);
        _router.OnKeyDown(InputKey.S, KeyModifiers.Control);
        _router.ApplyCursorMode(CursorMode.Normal);

        _router.OnKeyDown(InputKey.S, KeyModifiers.Control).ShouldBeTrue();

        _chords.ShouldHaveSingleItem().ShouldBe(ShellChord.SaveMap);
    }

    [Fact]
    public void Alt_is_claimed_so_the_window_menu_does_not_eat_the_next_key()
    {
        // Alt alone opens the window menu and swallows whatever follows, which
        // is the difference between Alt-orbit working and the viewport silently
        // going deaf mid-gesture. Claimed from the PLATFORM, not from the
        // engine: the key is submitted either way.
        _router.OnKeyDown(InputKey.AltLeft, KeyModifiers.Alt).ShouldBeTrue();
        _router.OnKeyDown(InputKey.A, KeyModifiers.Alt).ShouldBeTrue();

        _sink.Events.Count.ShouldBe(2);
        _sink.Events[0].Key.ShouldBe(InputKey.AltLeft);
        _sink.Events[1].Key.ShouldBe(InputKey.A);
    }

    [Fact]
    public void A_key_with_no_alt_and_no_chord_is_left_to_the_platform()
    {
        _router.OnKeyDown(InputKey.A, KeyModifiers.None).ShouldBeFalse();
    }

    [Fact]
    public void A_key_release_always_reaches_the_engine()
    {
        // No chord and no claim on the way up: the shell's chords fire on the
        // press, and a release the engine never hears leaves the key held for
        // the rest of the session.
        _router.OnKeyUp(InputKey.S);

        InputEvent submitted = _sink.Events.ShouldHaveSingleItem();
        submitted.Kind.ShouldBe(InputEventKind.KeyUp);
        submitted.Key.ShouldBe(InputKey.S);
    }

    // --- Before there is an engine -------------------------------------------

    [Fact]
    public void The_arbitration_runs_before_a_sink_exists()
    {
        // Input arriving before the engine is dropped rather than queued, but
        // the lock, the buttons and the press arbitration are the SHELL's
        // bookkeeping and would come back inconsistent the moment a session
        // started.
        _router.Sink = null;

        _router.OnPointerMove(200, 150);
        _router.OnPointerDown(PointerButtons.Right);
        _router.OnPointerUp(PointerButtons.Right);

        _menus.ShouldHaveSingleItem().ShouldBe(new ViewportPoint(200, 150));
        _router.ButtonsDown.ShouldBe(PointerButtons.None);
    }
}
