using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Input;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Editing.Selection;

/// <summary>
/// The marquee: tracks a rectangle from press to release, draws it, and on
/// release replaces / extends / toggles the scene's selection with everything
/// the rectangle covered — in <b>one</b> <c>SelectionChanged</c> event.
/// </summary>
/// <remarks>
/// <b>It does not decide when to start.</b> Whether a press belongs to the
/// marquee, to a gizmo handle, or to an object under the cursor is arbitration,
/// and arbitration lives in <see cref="Viewport.ViewportInteractionController"/>
/// — which calls <see cref="Begin"/> when it has ruled that the press landed on
/// empty space. Keeping the decision out of here is what makes both halves
/// testable in isolation.
/// <para>
/// <b>Rendering: unprojected world-space debug lines, not a new pipeline.</b>
/// The four corners are turned into rays with <c>Camera.ScreenPointToRay</c>
/// and sampled a hair in front of the near plane, giving a world-space quad
/// that reprojects exactly onto the rectangle the user is dragging. Because
/// <see cref="DebugDraw"/> lines already render depth-off on all three
/// backends, the quad composites as a screen overlay and nothing occludes it —
/// so a marquee costs eight vertices and zero backend work, instead of a
/// fourth 2D pipeline copied across OpenGL, D3D11 and D3D12. The visible cost
/// is that the rectangle is drawn with the same 1-pixel debug lines as the
/// gizmos.
/// </para>
/// <para>
/// <b>A press that never moved is a click, not an empty marquee.</b> Below
/// <see cref="ClickThresholdPixels"/> the gesture resolves as a click on empty
/// space: it clears the selection when no modifier is held, and deliberately
/// does nothing when Shift or Ctrl is — a slipped Shift+click must not
/// silently drop a carefully built selection.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only — it reads the scene's spatial index
/// and writes its selection. Allocation-free in steady state once the result
/// list has grown; the selection update itself is
/// <see cref="SelectionSet.Apply"/>, which reuses its own scratch.
/// </para>
/// </remarks>
public sealed class BoxSelectController
{
    // How far in front of the near plane the overlay quad is sampled, as a
    // fraction of the near distance. Far enough not to be clipped by depth
    // rounding, near enough that nothing can get between it and the eye.
    private const float OverlayNearOffset = 0.1f;

    private readonly List<SceneNode> _results = [];

    private Vector2 _anchor;
    private Vector2 _current;

    /// <summary>Creates a marquee over a scene.</summary>
    public BoxSelectController(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        Scene = scene;
    }

    /// <summary>The scene whose nodes the marquee selects.</summary>
    public Scene Scene { get; }

    /// <summary>Touch or fully-contain. See <see cref="BoxSelectMode"/>.</summary>
    public BoxSelectMode Mode { get; set; } = BoxSelectMode.Intersect;

    /// <summary>
    /// The button whose release commits the marquee. The arbiter presses it;
    /// this only has to know which release to watch for.
    /// </summary>
    public PointerButtons DragButton { get; set; } = PointerButtons.Left;

    /// <summary>
    /// How far the cursor must travel before the gesture counts as a drag
    /// rather than a click, measured on the rectangle's longer side.
    /// </summary>
    public float ClickThresholdPixels { get; set; } = 3f;

    /// <summary>The colour the marquee outline is drawn in.</summary>
    public Vector3 OutlineColor { get; set; } = new(1f, 0.75f, 0.15f);

    /// <summary>True while a marquee is being dragged.</summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// The rectangle as it currently stands, normalized. Meaningless when
    /// <see cref="IsActive"/> is false.
    /// </summary>
    public ScreenRect Rect => ScreenRect.FromCorners(_anchor, _current);

    /// <summary>
    /// True when the current rectangle is still within
    /// <see cref="ClickThresholdPixels"/> of its anchor — i.e. releasing now
    /// would resolve as a click.
    /// </summary>
    public bool IsClick => Rect.LongestSide < ClickThresholdPixels;

    /// <summary>
    /// The nodes the last committed marquee covered, in BVH traversal order.
    /// Empty after a cancel or a click.
    /// </summary>
    public IReadOnlyList<SceneNode> LastResult => _results;

    /// <summary>Raised after a marquee committed, carrying how many nodes it covered.</summary>
    public event Action<int>? Committed;

    /// <summary>
    /// Starts a marquee anchored at <paramref name="cursorPosition"/>. A
    /// marquee already in progress is restarted rather than refused, so a lost
    /// release edge cannot strand the state machine.
    /// </summary>
    public void Begin(Vector2 cursorPosition)
    {
        _anchor = cursorPosition;
        _current = cursorPosition;
        _results.Clear();
        IsActive = true;
    }

    /// <summary>
    /// Advances the marquee by one frame: tracks the cursor, and on the release
    /// edge (or <paramref name="cancelRequested"/>) ends the gesture.
    /// </summary>
    /// <param name="frame">This frame's input snapshot.</param>
    /// <param name="cancelRequested">
    /// True on the frame the user asked to abort — Escape, or a viewport that
    /// lost focus. Arrives as a parameter for the same reason it does on
    /// <c>GizmoTool.Update</c>: the input frame carries no keyboard vocabulary.
    /// </param>
    public BoxSelectResult Update(in EditorInputFrame frame, bool cancelRequested = false)
    {
        if (!IsActive)
            return BoxSelectResult.None;

        if (cancelRequested)
        {
            Cancel();
            return BoxSelectResult.Cancelled;
        }

        // Track first, then test the release: a button pressed and released
        // inside one frame must still see where the cursor ended up.
        _current = frame.CursorPosition;

        if (!frame.WasReleased(DragButton))
            return BoxSelectResult.Dragging;

        return Commit(frame.Modifiers, frame.ViewportSize);
    }

    /// <summary>
    /// Abandons the marquee without touching the selection. Returns false when
    /// none was in progress.
    /// </summary>
    public bool Cancel()
    {
        if (!IsActive)
            return false;

        IsActive = false;
        _results.Clear();
        return true;
    }

    /// <summary>
    /// Ends the gesture and applies it to the selection: a real drag selects
    /// everything it covered, a click on empty space clears the selection
    /// unless a modifier says to keep it.
    /// </summary>
    /// <remarks>
    /// Public so a host that ends the gesture some other way (a released
    /// pointer capture, a viewport that is closing) can still land the result.
    /// </remarks>
    public BoxSelectResult Commit(KeyModifiers modifiers, Vector2 viewportSize)
    {
        if (!IsActive)
            return BoxSelectResult.None;

        IsActive = false;
        SelectionUpdate update = SelectionModifiers.Resolve(modifiers);

        if (Rect.LongestSide < ClickThresholdPixels)
        {
            _results.Clear();
            // A plain click on nothing means "deselect everything"; a modified
            // one means the user was reaching for something and missed.
            if (update == SelectionUpdate.Replace)
                Scene.Selection.Clear();

            Committed?.Invoke(0);
            return BoxSelectResult.Clicked;
        }

        ScreenRect rect = Rect;
        BoxSelectQuery.Query(Scene, in rect, viewportSize, Mode, _results);

        // ONE event for the whole marquee, whatever it covered — see
        // SelectionSet.SetRange for why that matters.
        Scene.Selection.Apply(_results, update);

        Committed?.Invoke(_results.Count);
        return BoxSelectResult.Committed;
    }

    /// <summary>
    /// Draws the marquee outline. Does nothing when no marquee is in progress,
    /// or when it is still within the click threshold — a rectangle that flashes
    /// up under every click is noise.
    /// </summary>
    public void Draw(DebugDraw output, Vector2 viewportSize)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!IsActive || IsClick || viewportSize.X <= 0f || viewportSize.Y <= 0f)
            return;

        ScreenRect rect = Rect;
        Camera camera = Scene.Camera;
        float offset = camera.NearPlane * OverlayNearOffset;

        Vector3 topLeft = CornerToWorld(camera, new Vector2(rect.Min.X, rect.Min.Y), viewportSize, offset);
        Vector3 topRight = CornerToWorld(camera, new Vector2(rect.Max.X, rect.Min.Y), viewportSize, offset);
        Vector3 bottomRight = CornerToWorld(camera, new Vector2(rect.Max.X, rect.Max.Y), viewportSize, offset);
        Vector3 bottomLeft = CornerToWorld(camera, new Vector2(rect.Min.X, rect.Max.Y), viewportSize, offset);

        output.Line(topLeft, topRight, OutlineColor);
        output.Line(topRight, bottomRight, OutlineColor);
        output.Line(bottomRight, bottomLeft, OutlineColor);
        output.Line(bottomLeft, topLeft, OutlineColor);
    }

    // A screen corner, unprojected onto a plane just past the near plane. The
    // ray's origin is ON the near plane by contract, so stepping a little way
    // along it keeps the quad from being clipped away by depth rounding.
    private static Vector3 CornerToWorld(Camera camera, Vector2 pixel, Vector2 viewportSize, float offset) =>
        camera.ScreenPointToRay(pixel, viewportSize).PointAt(offset);
}

/// <summary>What one <see cref="BoxSelectController.Update"/> call did.</summary>
public enum BoxSelectResult
{
    /// <summary>No marquee is in progress.</summary>
    None,

    /// <summary>The marquee is being dragged; the selection has not changed.</summary>
    Dragging,

    /// <summary>The marquee ended as a drag and its coverage landed in the selection.</summary>
    Committed,

    /// <summary>
    /// The marquee ended without moving far enough to be a drag, and was
    /// resolved as a click on empty space.
    /// </summary>
    Clicked,

    /// <summary>The marquee was abandoned; the selection is untouched.</summary>
    Cancelled,
}
