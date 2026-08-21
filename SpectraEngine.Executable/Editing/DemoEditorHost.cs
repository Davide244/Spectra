using Microsoft.Extensions.Logging;
using Silk.NET.Input;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Cameras;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Input;
using SpectraEngine.Editing.Undo;
using SpectraEngine.Editing.Commands;
using SpectraEngine.Editing.Viewport;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Executable.Editing;

/// <summary>
/// The demo's editor: everything needed to turn the running engine into a
/// manipulable viewport — an <see cref="EditorInputFrame"/> built per frame from
/// the live input manager and the renderer's framebuffer latch, the viewport's
/// pick/gizmo/marquee arbitration, an orbit camera, an undo history, and the
/// keyboard that drives them.
/// </summary>
/// <remarks>
/// <b>This class is the whole of the host seam.</b> It is the only type in the
/// process that names both <c>Silk.NET.Input.Key</c> and
/// <c>SpectraEngine.Editing</c>: the editing assembly carries no keyboard
/// vocabulary at all (see <see cref="GizmoShortcuts"/>), so somebody has to
/// resolve a physical key into a <see cref="GizmoCommand"/>, and that somebody
/// is the host. Re-hosting the viewport in an Uno editor means writing a sibling
/// of this class against WinUI's input stack — the tools, the history and the
/// arbitration below the seam are untouched.
/// <para>
/// <b>Two navigation models, one toggle.</b> The default is the editor's own
/// Roblox-Studio-shaped navigation: hold the right mouse button and the cursor
/// locks and hides, the mouse looks around in place, and W/A/S/D/Q/E fly.
/// <see cref="NavigationToggleKey"/> hands the camera back to the engine's
/// original <c>FlyCameraController</c>, which is still there and still works.
/// <see cref="Update"/> returns whether the editor drove the camera this frame,
/// and the engine parks the fly camera exactly on the frames it did. The toggle
/// only chooses the camera — picking, manipulation and box select run in both
/// modes.
/// </para>
/// <para>
/// <b>The movement keys are only fed to the camera while the look button is
/// held</b>, which is both what the requirement asks for ("hold right click to
/// lock mouse and wasd to move") and what resolves the one genuine keyboard
/// conflict in this host: <c>W</c> means "move tool" to the manipulator and
/// "forward" to a camera, and <c>E</c> is claimed by both the rotate tool and
/// the rise/fall pair. Gating movement on the button means the letter row keeps
/// meaning "switch tool" whenever you are not actually flying, so nothing had to
/// be given up. The engine's fly camera reads the keyboard directly and cannot
/// be gated that way, so while <em>it</em> is driving the conflicting letter-row
/// tool bindings stand down as before (the 2/3/4 row keeps working in both).
/// </para>
/// <para>
/// <b>Key names are resolved once, not per frame.</b> The shortcut tables match
/// on key <em>names</em>, which would mean a <c>ToString()</c> allocation per
/// key per frame if it were done in the loop. The bindings are therefore
/// resolved in the constructor into a flat array, and the per-frame path is an
/// array walk over already-decided verbs — so a steady-state frame with no
/// keypress allocates nothing at all.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only, like the scene it edits and the
/// <see cref="DebugDraw"/> it fills. It is constructed on the render thread too
/// (from <c>SceneManager.EditorFactory</c>, inside the scene load).
/// </para>
/// </remarks>
internal sealed class DemoEditorHost : ISceneEditor
{
    /// <summary>Toggles between the editor's freelook camera and the engine's fly camera.</summary>
    private const Key NavigationToggleKey = Key.F7;

    // Ctrl+T, echoing Hammer's Ctrl+T (tie to entity) — the nearest thing in
    // that editor to "change what this brush fundamentally is". A Control chord
    // deliberately, so the bare letter row stays free for tool switching.
    private const Key BrushKindToggleKey = Key.T;

    // Interned labels for ISceneEditor.NavigationModeName — the stats line that
    // reads it is otherwise allocation-free, so this must never be a formatted
    // enum.
    private const string EditorNavigationLabel = "editor freelook";
    private const string FlyCameraNavigationLabel = "fly camera";

    // Candidate manipulator keys, offered to the editing layer's own default
    // table. Anything it does not recognise is silently dropped, so this list
    // can stay a superset of whatever the defaults happen to bind today.
    private static readonly Key[] GizmoKeyCandidates =
    [
        Key.W, Key.E, Key.R,
        Key.Number2, Key.Number3, Key.Number4,
        Key.X, Key.G, Key.LeftBracket, Key.RightBracket,
    ];

    // Keys a camera owns while it is driving. Only the overlap with
    // GizmoKeyCandidates matters (today: W and E), but listing the whole set
    // keeps the conflict rule honest if either table grows.
    private static readonly Key[] CameraKeys =
    [
        Key.W, Key.A, Key.S, Key.D, Key.Q, Key.E,
        Key.Space, Key.ControlLeft, Key.ShiftLeft,
    ];

    private readonly ILogger<DemoEditorHost> _logger;
    private readonly Scene _scene;
    private readonly Renderer _renderer;
    private readonly InputManager _input;

    private readonly UndoStack _undo;
    private readonly GizmoController _gizmos;
    private readonly EditorCameraController _camera;
    private readonly ViewportInteractionController _viewport;
    private readonly EngineEditorInputSource _inputSource;
    private readonly GizmoBinding[] _gizmoBindings;
    private readonly EditingSelfTest? _selfTest;

    // Editor-only, and deliberately not in Core: a shipped game has no reason
    // to outline its own parts, and this assembly is the one that never gets
    // linked into one.
    private readonly PartBrushOverlay _partOutlines = new();

    // Last frame's framebuffer latch, kept so Draw can size the marquee without
    // asking the renderer a second time.
    private Vector2 _viewportSize;
    private bool _editorNavigation = true;

    /// <summary>
    /// Builds the demo's editor over a freshly loaded scene.
    /// </summary>
    /// <param name="loggerFactory">Used for this host's log and the self-test's.</param>
    /// <param name="scene">The scene to edit; supplies the camera and the selection.</param>
    /// <param name="renderer">Supplies the framebuffer latch the viewport is sized from.</param>
    /// <param name="input">The live input manager the per-frame snapshot is built from.</param>
    /// <param name="selfTestNode">
    /// The brush node the synthetic self-test manipulates, or null to run
    /// without one — which is the default: the demo passes a node only when
    /// <c>--selftest</c> asked for it, because the test visibly moves whatever
    /// it is given (see <see cref="EditingSelfTest"/>).
    /// </param>
    public DemoEditorHost(
        ILoggerFactory loggerFactory,
        Scene scene,
        Renderer renderer,
        InputManager input,
        SceneNode? selfTestNode)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(input);

        _logger = loggerFactory.CreateLogger<DemoEditorHost>();
        _scene = scene;
        _renderer = renderer;
        _input = input;

        _undo = new UndoStack(scene);
        _gizmos = new GizmoController(scene, _undo);
        // The resize tool's one diagnostic: a target with no measurable size
        // cannot honour a world-unit increment and says so instead of quietly
        // behaving like a factor drag.
        _gizmos.Scale.Logger = loggerFactory.CreateLogger<ScaleGizmo>();
        // The input manager is the engine's ICursorLock: the camera asks for a
        // locked cursor from here (the render thread) and the main thread
        // applies it during its event pump. Handing over the interface rather
        // than the manager is what keeps the editing layer from ever seeing a
        // window.
        _camera = new EditorCameraController(scene) { CursorLock = input };
        _viewport = new ViewportInteractionController(scene, _gizmos) { CameraController = _camera };
        _inputSource = new EngineEditorInputSource(input, renderer);
        _gizmoBindings = BuildGizmoBindings();

        // Seed the viewport size before the first frame so a pick that happens
        // on frame one is not measured against a zero-sized viewport.
        renderer.GetFramebufferSize(out int width, out int height);
        _viewportSize = new Vector2(width, height);

        if (selfTestNode is not null)
            _selfTest = new EditingSelfTest(loggerFactory.CreateLogger<EditingSelfTest>(), scene, selfTestNode);

        _logger.LogInformation(
            "Editor viewport ready: hold right mouse to lock the cursor and look, W/A/S/D + Q/E " +
            "(or Space/Ctrl) fly while you do, Shift boosts, the wheel trims fly speed while " +
            "looking and zooms otherwise; Alt+drag orbits, middle-drag pans, F frames the " +
            "selection; left-drag picks/moves, marquee on empty space; W/E/R or 2/3/4 pick the " +
            "tool, X flips world/local, G and [ ] drive snap, Ctrl+Z / Ctrl+Y walk {Capacity} " +
            "entries of history; Ctrl+T converts the selected brushes between world geometry and " +
            "parts (parts leave the CSG carve, so they stop merging with what they touch and cost " +
            "no recompile when they move — they are outlined in cyan); F7 toggles between the " +
            "editor camera and the engine fly camera (starting: {Mode})",
            _undo.Capacity, NavigationModeName);
    }

    /// <inheritdoc/>
    public int SelectionCount => _scene.Selection.Count;

    /// <inheritdoc/>
    /// <remarks>Interned literals, never a formatted enum — see <see cref="ISceneEditor"/>.</remarks>
    public string GizmoModeName => _gizmos.Mode switch
    {
        GizmoMode.Rotate => "rotate",
        GizmoMode.Scale => "resize",
        _ => "move",
    };

    /// <inheritdoc/>
    /// <remarks>Interned literals, never a formatted enum — see <see cref="ISceneEditor"/>.</remarks>
    public string NavigationModeName => _editorNavigation ? EditorNavigationLabel : FlyCameraNavigationLabel;

    /// <inheritdoc/>
    public int UndoDepth => _undo.UndoCount;

    /// <inheritdoc/>
    public int RedoDepth => _undo.RedoCount;

    /// <inheritdoc/>
    public bool Update(double deltaTime)
    {
        _renderer.GetFramebufferSize(out int width, out int height);
        if (width <= 0 || height <= 0)
        {
            // Minimized. There is no viewport to hit-test against, and every
            // ray through a zero-sized one is undefined — so abandon whatever
            // gesture was live rather than resuming it later against a viewport
            // that changed underneath it.
            _viewport.Reset();
            return _editorNavigation;
        }

        _viewportSize = new Vector2(width, height);

        HandleShortcuts();

        EditorInputFrame frame = _inputSource.CaptureFrame((float)deltaTime, CaptureNavigation());

        // BEFORE the viewport, and standing down on the very frame a press could
        // claim the pointer — both halves matter. The self-test deliberately
        // leaves its subject node displaced for the whole compile watch, so a
        // viewport update that ran first would let a gizmo grab CAPTURE that
        // displaced transform as the drag's start; the self-test would then see
        // a non-idle viewport in the same frame, restore the node underneath the
        // live gesture, and the drag would jump a unit and commit — and undo —
        // to a position the user never authored. Asking the frame whether a
        // press is arriving, rather than asking the viewport what it did with
        // one, is what closes that window: the node is back at rest before
        // anything can capture it.
        //
        // The self-test borrows and hands back the camera within its own call,
        // so running it first costs nothing: the camera controller writes the
        // pose afterwards either way.
        bool viewportIdle =
            _viewport.DragMode == ViewportDragMode.None && !frame.WasPressed(_viewport.DragButton);
        _selfTest?.Update(deltaTime, _viewportSize, viewportIdle);

        // Escape arrives as the cancel flag rather than as a GizmoCommand: it
        // has to reach the marquee as well as the manipulator, and Update is the
        // one call that routes it to whichever owns the pointer.
        _viewport.Update(in frame, _input.WasKeyPressed(Key.Escape));

        return _editorNavigation;
    }

    /// <inheritdoc/>
    public void Draw(DebugDraw output)
    {
        // Part outlines first, manipulators over them: a gizmo is what the user
        // is aiming at, an outline is context, and both share the depth-off
        // line pass so neither hides behind the geometry it describes.
        _partOutlines.Draw(output, _scene);
        _viewport.Draw(output, _viewportSize);
    }

    // --- Navigation keyboard -------------------------------------------------

    /// <summary>
    /// This frame's movement axis, resolved from the host's own keys — the
    /// Roblox-Studio set: W/A/S/D across the ground plane, Q/E down and up, with
    /// Space/Ctrl as the second binding for the same pair, and Shift to boost.
    /// </summary>
    /// <remarks>
    /// <b>Only while the look button is held</b>, so the letter row keeps its
    /// tool meanings the rest of the time (see the type remarks). Returns the
    /// idle axis when the fly camera is driving instead — the engine's
    /// controller reads the same keys itself, and feeding both would double
    /// every step.
    /// </remarks>
    private EditorNavigationInput CaptureNavigation()
    {
        if (!_editorNavigation || !IsLookButtonHeld())
            return default;

        return EditorNavigationInput.FromKeys(
            forward: _input.IsKeyDown(Key.W),
            back: _input.IsKeyDown(Key.S),
            left: _input.IsKeyDown(Key.A),
            right: _input.IsKeyDown(Key.D),
            up: _input.IsKeyDown(Key.E) || _input.IsKeyDown(Key.Space),
            down: _input.IsKeyDown(Key.Q) || _input.IsKeyDown(Key.ControlLeft),
            boost: _input.IsKeyDown(Key.ShiftLeft) || _input.IsKeyDown(Key.ShiftRight));
    }

    // The one place this host maps the camera's look button back onto a physical
    // button, so the gate on the movement keys and the gate on the letter-row
    // tool bindings cannot drift apart.
    private bool IsLookButtonHeld() =>
        (_input.PointerButtonsDown & _camera.FreeLookButton) == _camera.FreeLookButton;

    // --- Keyboard ------------------------------------------------------------

    private void HandleShortcuts()
    {
        if (_input.WasKeyPressed(NavigationToggleKey))
            ToggleNavigation();

        KeyModifiers modifiers = _input.Modifiers;

        // Control chords are handled first and never fall through to a bare-key
        // verb: Ctrl+Z must not also be read as "Z", and no bare key the editor
        // binds is meant to fire while Control is held.
        if ((modifiers & KeyModifiers.Control) != 0)
        {
            bool shift = (modifiers & KeyModifiers.Shift) != 0;
            if (_input.WasKeyPressed(Key.Z))
            {
                if (shift) Redo();
                else Undo();
            }
            else if (_input.WasKeyPressed(Key.Y))
            {
                Redo();
            }
            else if (_input.WasKeyPressed(BrushKindToggleKey))
            {
                ToggleSelectionBrushKind();
            }
            return;
        }

        for (int i = 0; i < _gizmoBindings.Length; i++)
        {
            GizmoBinding binding = _gizmoBindings[i];

            // While the editor camera drives, movement keys are only forwarded
            // to it during a look (see the type remarks), so the letter row is
            // free the rest of the time. The engine's fly camera reads the
            // keyboard itself and cannot be gated, so it takes the whole set.
            if (binding.ConflictsWithCamera && (!_editorNavigation || IsLookButtonHeld()))
                continue;

            if (_input.WasKeyPressed(binding.Key))
                _gizmos.Apply(binding.Command);
        }

        // The camera verbs are a table of one, so asking by the literal name
        // costs nothing and keeps the binding where the editing layer documents
        // it rather than duplicated here.
        if (_input.WasKeyPressed(Key.F) &&
            EditorCameraShortcuts.TryResolve("F", modifiers, out EditorCameraCommand cameraCommand))
        {
            _camera.Apply(cameraCommand);
        }
    }

    private void ToggleNavigation()
    {
        _editorNavigation = !_editorNavigation;

        if (_editorNavigation)
        {
            // Adopt whatever pose the fly camera left behind, so the toggle
            // never teleports the view: the editor camera takes the current
            // position and angles and puts its focus a fixed distance ahead.
            _camera.AdoptCamera();
            _viewport.CameraController = _camera;
        }
        else
        {
            // Handing navigation back mid-gesture would leave the editor camera
            // chasing a target the fly camera is simultaneously walking away
            // from — and, worse, holding a cursor lock nothing would ever
            // release. Suspending does both jobs.
            _camera.SuspendNavigation();
            _viewport.CameraController = null;
        }

        _logger.LogInformation("Navigation: {Mode} ({Key} toggles)", NavigationModeName, NavigationToggleKey);
    }

    // Ctrl+T: convert the selected brushes between world geometry and parts —
    // the one edit that changes whether a brush is admitted to the fused static
    // world, and therefore the one a user must perform deliberately rather than
    // discover by dragging something somewhere.
    //
    // A mixed selection NORMALISES rather than flipping each node
    // independently: "toggle" on a set means the whole set ends up the same
    // way, and per-node flipping would leave a selection the user can never get
    // back into one state. If anything in it is still world geometry, the whole
    // selection becomes parts; only an all-part selection converts back.
    private void ToggleSelectionBrushKind()
    {
        // Mid-gesture the manipulator is holding an open transaction, and a
        // conversion inside it would land in the drag's undo entry.
        _viewport.Reset();

        IReadOnlyList<SceneNode> selected = _scene.Selection.Items;
        var commands = new List<IEditorCommand>();
        bool anyWorld = false;
        int skipped = 0;

        for (int i = 0; i < selected.Count; i++)
        {
            if (selected[i].Brush is null)
            {
                skipped++;
                continue;
            }
            if (selected[i].BrushKind == BrushKind.World)
                anyWorld = true;
        }

        BrushKind target = anyWorld ? BrushKind.Part : BrushKind.World;
        for (int i = 0; i < selected.Count; i++)
        {
            SceneNode node = selected[i];
            if (node.Brush is null || node.BrushKind == target)
                continue;
            commands.Add(SetBrushKindCommand.Capture(node, target));
        }

        if (commands.Count == 0)
        {
            // Never a silent no-op: "I pressed the key and nothing happened" is
            // indistinguishable from a broken binding.
            _logger.LogInformation(
                "Convert brush: nothing to convert ({Selected} selected, {Skipped} without a brush)",
                selected.Count, skipped);
            return;
        }

        string name = target == BrushKind.Part ? "Convert to Part" : "Convert to World";
        _undo.Execute(commands.Count == 1 ? commands[0] : new CompositeCommand(name, commands));

        _logger.LogInformation(
            "{Name}: {Converted} brush(es), {Skipped} selected node(s) had no brush. " +
            "Part brushes leave the CSG carve — they no longer merge with the geometry around them, " +
            "and they cost no static-world recompile when they move.",
            name, commands.Count, skipped);
    }

    private void Undo()
    {
        // A history step in the middle of a gesture would interleave with the
        // edit in progress — the stack refuses it outright while a transaction
        // is open — so abandon the gesture first, which also restores whatever
        // it had moved so far.
        _viewport.Reset();
        if (_undo.Undo())
            _logger.LogInformation("Undo '{Name}' (undo {UndoDepth} / redo {RedoDepth})", _undo.RedoName, _undo.UndoCount, _undo.RedoCount);
    }

    private void Redo()
    {
        _viewport.Reset();
        if (_undo.Redo())
            _logger.LogInformation("Redo '{Name}' (undo {UndoDepth} / redo {RedoDepth})", _undo.UndoName, _undo.UndoCount, _undo.RedoCount);
    }

    // Resolves every candidate key through the editing layer's default table
    // once, at construction. Keys the table does not bind are dropped rather
    // than mapped to a fallback: an unbound key must stay unbound.
    private static GizmoBinding[] BuildGizmoBindings()
    {
        var bindings = new List<GizmoBinding>(GizmoKeyCandidates.Length);
        foreach (Key key in GizmoKeyCandidates)
        {
            if (!GizmoShortcuts.TryResolve(key.ToString(), out GizmoCommand command))
                continue;

            bindings.Add(new GizmoBinding(key, command, Array.IndexOf(CameraKeys, key) >= 0));
        }

        return [.. bindings];
    }

    // One pre-resolved keyboard binding. ConflictsWithCamera marks the keys a
    // camera claims while it is driving, which must therefore stop meaning
    // "switch tool" for as long as it does.
    private readonly record struct GizmoBinding(Key Key, GizmoCommand Command, bool ConflictsWithCamera);
}
