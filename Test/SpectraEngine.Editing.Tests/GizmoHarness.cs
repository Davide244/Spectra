using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Input;
using SpectraEngine.Editing.Undo;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// A headless viewport for driving any <see cref="GizmoTool"/> in tests: a scene
/// with a configured camera, an undo history, a <see cref="GizmoController"/>
/// with all three tools, and the frame plumbing to aim a cursor at a world point
/// and press, drag, and release.
/// </summary>
/// <remarks>
/// <b>Aiming is done in world space and projected</b>, never in raw pixels.
/// <see cref="WorldToScreen"/> is the exact inverse of the mapping
/// <see cref="Camera.ScreenPointToRay"/> applies, so pointing the cursor at a
/// world point <c>P</c> produces a ray that passes through <c>P</c> — which is
/// what lets a test say "drag exactly 5.37 units along x" or "sweep exactly 30°
/// around the y ring" and check the answer to float precision, instead of
/// guessing pixel offsets.
/// <para>
/// <b><see cref="Gizmo"/> follows the controller's mode</b>, so a test switches
/// tools with <see cref="Use"/> and then uses the same grab/drag/release verbs
/// whichever tool is live.
/// </para>
/// <para>
/// No GPU, no window: only <see cref="Camera"/> maths and the editing layer's
/// own backend-neutral input snapshot.
/// </para>
/// </remarks>
internal sealed class GizmoHarness
{
    // The point the current gesture was grabbed at, so DragBy can express a
    // movement as an absolute aim point (grab aim + delta) rather than
    // accumulating — the same discipline the gizmo itself follows.
    private Vector3 _grabAim;

    public GizmoHarness(Vector3 cameraPosition, Vector3 lookAt, float viewportWidth = 800f, float viewportHeight = 600f)
    {
        ViewportSize = new Vector2(viewportWidth, viewportHeight);
        Scene = new Scene("Gizmo");
        Scene.Camera.Position = cameraPosition;
        Scene.Camera.LookAt(lookAt);
        // The pipelines normally write this every frame; without it the
        // projection and the viewport would disagree and every projected aim
        // point would land a few pixels off.
        Scene.Camera.AspectRatio = ViewportSize.X / ViewportSize.Y;
        Undo = new UndoStack(Scene);
        Gizmos = new GizmoController(Scene, Undo);
    }

    /// <summary>A camera looking at the origin from a three-quarter view, where all three axes are separated on screen.</summary>
    public static GizmoHarness ThreeQuarterView() => new(new Vector3(8f, 6f, 10f), Vector3.Zero);

    /// <summary>A camera on the +z axis looking straight down −z: the x and y axes lie in the view plane, and z projects to a point.</summary>
    public static GizmoHarness FrontView(float distance = 10f) => new(new Vector3(0f, 0f, distance), Vector3.Zero);

    public Scene Scene { get; }

    public UndoStack Undo { get; }

    public GizmoController Gizmos { get; }

    /// <summary>The live tool — whichever mode the controller is in.</summary>
    public GizmoTool Gizmo => Gizmos.Active;

    /// <summary>The move tool, for tests that need its translate-specific surface.</summary>
    public TranslateGizmo Translate => Gizmos.Translate;

    /// <summary>The rotate tool.</summary>
    public RotateGizmo Rotate => Gizmos.Rotate;

    /// <summary>The resize tool.</summary>
    public ScaleGizmo Scale => Gizmos.Scale;

    public Vector2 ViewportSize { get; }

    /// <summary>Switches the live tool and returns it.</summary>
    public GizmoTool Use(GizmoMode mode)
    {
        Gizmos.Mode = mode;
        return Gizmos.Active;
    }

    /// <summary>Adds a plain child node of the root at <paramref name="position"/> and selects it.</summary>
    public SceneNode AddSelectedNode(Vector3 position, string name = "Node")
    {
        SceneNode node = AddNode(position, name);
        Scene.Selection.Add(node);
        return node;
    }

    /// <summary>Adds a plain child node of the root at <paramref name="position"/>.</summary>
    public SceneNode AddNode(Vector3 position, string name = "Node")
    {
        SceneNode node = Scene.Root.CreateChild(name);
        node.LocalPosition = position;
        return node;
    }

    /// <summary>
    /// Adds a selected node carrying a centred box brush of the given half
    /// extent — the authoring shape the resize tests measure.
    /// </summary>
    public SceneNode AddSelectedBrushNode(Vector3 position, float halfExtent = 1f, string name = "Brush")
    {
        SceneNode node = AddNode(position, name);
        node.Brush = Brush.CreateBox(new Vector3(-halfExtent), new Vector3(halfExtent));
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

    /// <summary>This frame's gizmo geometry for a world-aligned pivot, without running the state machine.</summary>
    public GizmoGeometry GeometryAt(Vector3 pivot) => GeometryAt(pivot, Quaternion.Identity);

    /// <summary>This frame's gizmo geometry for a pivot in an explicit frame.</summary>
    public GizmoGeometry GeometryAt(Vector3 pivot, Quaternion frame) =>
        GizmoGeometry.Build(Scene.Camera, pivot, frame, ViewportSize, Gizmo.HandlePixelSize);

    /// <summary>Builds one input frame.</summary>
    public EditorInputFrame Frame(
        Vector2 cursor,
        PointerButtons down = PointerButtons.None,
        PointerButtons pressed = PointerButtons.None,
        PointerButtons released = PointerButtons.None,
        KeyModifiers modifiers = KeyModifiers.None) =>
        new(cursor, ViewportSize, down, pressed, released, modifiers, Vector2.Zero, 1f / 60f);

    /// <summary>Moves the cursor over <paramref name="aimAt"/> with no button held.</summary>
    public GizmoUpdateResult Hover(Vector3 aimAt) =>
        Gizmos.Update(Frame(WorldToScreen(aimAt)));

    /// <summary>Presses the drag button with the cursor over <paramref name="aimAt"/>.</summary>
    public GizmoUpdateResult Grab(Vector3 aimAt)
    {
        _grabAim = aimAt;
        return Gizmos.Update(Frame(
            WorldToScreen(aimAt), down: PointerButtons.Left, pressed: PointerButtons.Left));
    }

    /// <summary>
    /// Holds the drag button and aims at the grab point displaced by
    /// <paramref name="worldDelta"/>. Because the aim is absolute, a sequence
    /// of <c>DragBy</c> calls is a sequence of cursor <em>positions</em>, not
    /// accumulated movements — exactly what a real mouse produces.
    /// </summary>
    public GizmoUpdateResult DragBy(Vector3 worldDelta, KeyModifiers modifiers = KeyModifiers.None) =>
        DragTo(_grabAim + worldDelta, modifiers);

    /// <summary>Holds the drag button and aims at an absolute world point.</summary>
    public GizmoUpdateResult DragTo(Vector3 aimAt, KeyModifiers modifiers = KeyModifiers.None) =>
        Gizmos.Update(Frame(
            WorldToScreen(aimAt), down: PointerButtons.Left, modifiers: modifiers));

    /// <summary>Releases the drag button, committing the gesture.</summary>
    public GizmoUpdateResult Release(KeyModifiers modifiers = KeyModifiers.None) =>
        Gizmos.Update(Frame(
            WorldToScreen(_grabAim), released: PointerButtons.Left, modifiers: modifiers));

    /// <summary>Sends the Escape edge to an in-progress drag.</summary>
    public GizmoUpdateResult PressEscape() =>
        Gizmos.Update(Frame(WorldToScreen(_grabAim), down: PointerButtons.Left), cancelRequested: true);

    /// <summary>Presses the cancel (right) button during a drag.</summary>
    public GizmoUpdateResult RightClick() =>
        Gizmos.Update(Frame(
            WorldToScreen(_grabAim),
            down: PointerButtons.Left | PointerButtons.Right,
            pressed: PointerButtons.Right));
}
