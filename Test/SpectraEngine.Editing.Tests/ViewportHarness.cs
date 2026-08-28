using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Cameras;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Input;
using SpectraEngine.Editing.Undo;
using SpectraEngine.Editing.Viewport;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// A headless viewport with the full editing stack wired up — scene, undo,
/// manipulator, marquee, drag arbitration and the orbit camera — plus the frame
/// plumbing to press, drag, scroll and release at chosen pixels or at chosen
/// world points.
/// </summary>
/// <remarks>
/// Sibling of <see cref="GizmoHarness"/>, which drives a gizmo in isolation;
/// this one exists because the camera and the marquee are about the
/// <em>viewport</em> rather than about a tool, and both need pixel-space aiming
/// that the gizmo harness deliberately hides behind world-space aim points.
/// <para>
/// The camera controller starts with damping OFF
/// (<see cref="EditorCameraController.SmoothingTimeConstant"/> = 0), because a
/// test asserting where the camera ended up should not have to simulate the
/// settle; the tests that are specifically about damping turn it back on.
/// </para>
/// <para>
/// No GPU, no window: pure <see cref="Camera"/> maths and the editing layer's
/// backend-neutral input snapshot.
/// </para>
/// </remarks>
internal sealed class ViewportHarness
{
    public ViewportHarness(
        float viewportWidth = 800f, float viewportHeight = 600f, GizmoStyle? gizmoStyle = null)
    {
        ViewportSize = new Vector2(viewportWidth, viewportHeight);
        Scene = new Scene("Viewport");
        // The pipelines normally write this every frame; without it the
        // projection and the viewport disagree and every projected aim point
        // lands a few pixels off.
        Scene.Camera.AspectRatio = ViewportSize.X / ViewportSize.Y;
        Undo = new UndoStack(Scene);
        // Classic by default, not the engine's Studio default, for the reason
        // GizmoHarness gives: these tests aim at pixels near a gizmo whose
        // handles stand a fixed distance from the pivot, and Studio stands them
        // on the selection's own box instead. The arbitration this suite is
        // about (press means manipulate, or select, or marquee) is style
        // independent; where the handles are is not.
        Gizmos = new GizmoController(Scene, Undo) { Style = gizmoStyle ?? GizmoStyle.Classic };
        CursorLock = new FakeCursorLock();
        EditorCamera = new EditorCameraController(Scene)
        {
            SmoothingTimeConstant = 0f,
            CursorLock = CursorLock,
        };
        Viewport = new ViewportInteractionController(Scene, Gizmos) { CameraController = EditorCamera };
    }

    /// <summary>
    /// The cursor-lock double the camera requests through. It records requests
    /// and only "applies" them when <see cref="FakeCursorLock.Pump"/> is called,
    /// mirroring the real latch's main-thread round trip.
    /// </summary>
    public FakeCursorLock CursorLock { get; }

    public Scene Scene { get; }

    public UndoStack Undo { get; }

    public GizmoController Gizmos { get; }

    public EditorCameraController EditorCamera { get; }

    public ViewportInteractionController Viewport { get; }

    public Vector2 ViewportSize { get; }

    /// <summary>The viewport's centre pixel.</summary>
    public Vector2 CenterPixel => ViewportSize * 0.5f;

    /// <summary>Places the orbit outright and writes the camera immediately.</summary>
    public ViewportHarness Orbit(Vector3 focus, float distance, float yaw, float pitch)
    {
        EditorCamera.SetOrbit(focus, distance, yaw, pitch);
        return this;
    }

    /// <summary>Adds a box-brush node under the root.</summary>
    public SceneNode AddBrush(Vector3 position, float halfExtent = 0.5f, string name = "Brush")
    {
        SceneNode node = Scene.Root.CreateChild(name);
        node.LocalPosition = position;
        node.Brush = Brush.CreateBox(new Vector3(-halfExtent), new Vector3(halfExtent));
        return node;
    }

    /// <summary>Adds a box-brush node and selects it.</summary>
    public SceneNode AddSelectedBrush(Vector3 position, float halfExtent = 0.5f, string name = "Brush")
    {
        SceneNode node = AddBrush(position, halfExtent, name);
        Scene.Selection.Add(node);
        return node;
    }

    /// <summary>
    /// Projects a world point to viewport pixels — the exact inverse of
    /// <see cref="Camera.ScreenPointToRay"/>'s pixel-to-NDC mapping.
    /// </summary>
    public Vector2 WorldToScreen(Vector3 world)
    {
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), Scene.Camera.GetViewProjection());
        return new Vector2(
            (clip.X / clip.W + 1f) * 0.5f * ViewportSize.X,
            (1f - clip.Y / clip.W) * 0.5f * ViewportSize.Y);
    }

    /// <summary>Builds one input frame at an explicit pixel.</summary>
    /// <remarks>
    /// <paramref name="cursorDelta"/> and <paramref name="locked"/> are the
    /// freelook pair: while the cursor is locked the absolute position is
    /// meaningless and the camera consumes the delta instead, so a locked frame
    /// normally repeats the same <paramref name="cursor"/> and varies only the
    /// delta — exactly as the real input source does once the position freezes.
    /// </remarks>
    public EditorInputFrame Frame(
        Vector2 cursor,
        PointerButtons down = PointerButtons.None,
        PointerButtons pressed = PointerButtons.None,
        PointerButtons released = PointerButtons.None,
        KeyModifiers modifiers = KeyModifiers.None,
        Vector2 scroll = default,
        float deltaTime = 1f / 60f,
        Vector2 cursorDelta = default,
        EditorNavigationInput navigation = default,
        bool locked = false) =>
        new(cursor, ViewportSize, down, pressed, released, modifiers, scroll, deltaTime,
            cursorDelta, navigation, locked);

    /// <summary>Builds one input frame aimed at a world point.</summary>
    public EditorInputFrame FrameAt(
        Vector3 aimAt,
        PointerButtons down = PointerButtons.None,
        PointerButtons pressed = PointerButtons.None,
        PointerButtons released = PointerButtons.None,
        KeyModifiers modifiers = KeyModifiers.None) =>
        Frame(WorldToScreen(aimAt), down, pressed, released, modifiers);

    // --- Viewport gestures ---------------------------------------------------

    /// <summary>Moves the cursor with no button held.</summary>
    public ViewportDragMode Move(Vector2 cursor) => Viewport.Update(Frame(cursor));

    /// <summary>Presses the drag button at a pixel.</summary>
    public ViewportDragMode Press(Vector2 cursor, KeyModifiers modifiers = KeyModifiers.None) =>
        Viewport.Update(Frame(cursor, PointerButtons.Left, PointerButtons.Left, modifiers: modifiers));

    /// <summary>Holds the drag button at a pixel.</summary>
    public ViewportDragMode Drag(Vector2 cursor, KeyModifiers modifiers = KeyModifiers.None) =>
        Viewport.Update(Frame(cursor, PointerButtons.Left, modifiers: modifiers));

    /// <summary>Releases the drag button at a pixel.</summary>
    public ViewportDragMode Release(Vector2 cursor, KeyModifiers modifiers = KeyModifiers.None) =>
        Viewport.Update(Frame(cursor, released: PointerButtons.Left, modifiers: modifiers));

    /// <summary>Press, drag and release across a rectangle, all in pixels.</summary>
    public void DragRect(Vector2 from, Vector2 to, KeyModifiers modifiers = KeyModifiers.None)
    {
        Press(from, modifiers);
        Drag(to, modifiers);
        Release(to, modifiers);
    }
}
