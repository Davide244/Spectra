using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Gizmos;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// What a resize drag does — and, for brush nodes, what it very deliberately
/// does <em>not</em> do.
/// </summary>
/// <remarks>
/// <b>The load-bearing claim of this suite is that a brush node's transform never
/// receives a scale.</b> Brush node transforms must stay rigid — the CSG epsilon
/// scheme assumes unit-length plane normals, and <c>Scene</c>'s snapshot rejects
/// a scaled brush node outright — so the resize tool edits the brush's local
/// plane offsets and swaps the successor brush onto the node instead. Every brush
/// test below asserts the scale is byte-identical afterwards, not merely "close".
/// <para>
/// The node's <em>position</em> does move, by design: a resize is face-anchored,
/// so growing along +x plants the −x face and shifts the node half the growth.
/// A translation keeps the placement rigid; a scale would not.
/// <see cref="ResizeIncrementTests"/> owns that behaviour in detail.
/// </para>
/// <para>
/// <b>Drags here are expressed in world units</b>, because that is now what the
/// tool consumes: the cursor's travel along the constraint <em>is</em> the size
/// change, so "drag the x handle by +1" means "make it one world unit wider".
/// </para>
/// </remarks>
public sealed class ScaleGizmoDragTests
{
    private const float Tolerance = 1e-3f;

    [Theory]
    [InlineData(GizmoHandle.AxisX)]
    [InlineData(GizmoHandle.AxisY)]
    [InlineData(GizmoHandle.AxisZ)]
    public void An_axis_drag_resizes_a_mesh_node_along_that_axis_only(GizmoHandle handle)
    {
        var harness = ResizeHarness();
        // A one-unit cube, so a +1 size change is exactly a ×2 scale.
        SceneNode node = harness.AddSelectedMeshNode(Vector3.Zero, halfExtent: 0.5f);
        ScaleGizmo scale = Scale(harness);
        scale.Snap.Enabled = false;

        DragAxisBy(harness, handle, 1f);

        Vector3 expectedScale = handle switch
        {
            GizmoHandle.AxisX => new Vector3(2f, 1f, 1f),
            GizmoHandle.AxisY => new Vector3(1f, 2f, 1f),
            _ => new Vector3(1f, 1f, 2f),
        };

        node.LocalScale.ShouldBeCloseTo(expectedScale, Tolerance);
        // Face-anchored: the far face stayed, so the node moved half the growth.
        Vector3 expectedPosition = (expectedScale - Vector3.One) * 0.5f;
        node.LocalPosition.ShouldBeCloseTo(expectedPosition, Tolerance);
        node.LocalRotation.ShouldBe(Quaternion.Identity);
    }

    [Fact]
    public void A_negative_drag_shrinks_a_mesh_node()
    {
        var harness = ResizeHarness();
        SceneNode node = harness.AddSelectedMeshNode(Vector3.Zero, halfExtent: 0.5f);
        Scale(harness).Snap.Enabled = false;

        DragAxisBy(harness, GizmoHandle.AxisY, -0.5f);

        node.LocalScale.ShouldBeCloseTo(new Vector3(1f, 0.5f, 1f), Tolerance);
        node.LocalPosition.ShouldBeCloseTo(new Vector3(0f, -0.25f, 0f), Tolerance);
    }

    [Fact]
    public void An_existing_scale_is_multiplied_not_replaced()
    {
        var harness = ResizeHarness();
        SceneNode node = harness.AddSelectedMeshNode(Vector3.Zero, halfExtent: 0.5f);
        node.LocalScale = new Vector3(3f, 4f, 5f);
        Scale(harness).Snap.Enabled = false;

        // The node already measures 3 world units across x; +3 makes it 6, which
        // is the ×2 the scale has to end up carrying.
        DragAxisBy(harness, GizmoHandle.AxisX, 3f);

        node.LocalScale.ShouldBeCloseTo(new Vector3(6f, 4f, 5f), Tolerance * 10f);
    }

    [Fact]
    public void The_uniform_handle_resizes_all_three_axes_together()
    {
        var harness = ResizeFrontHarness();
        SceneNode node = harness.AddSelectedMeshNode(Vector3.Zero, halfExtent: 0.5f);
        ScaleGizmo scale = Scale(harness);
        scale.Snap.Enabled = false;

        GizmoGeometry geometry = Prime(harness);
        Vector3 pivot = harness.Gizmo.Pivot;

        harness.Grab(pivot).ShouldBe(GizmoUpdateResult.DragBegan);
        scale.ActiveHandle.ShouldBe(GizmoHandle.Screen);

        // One world unit up and to the right grows the largest dimension — here
        // the whole one-unit cube — by exactly one unit.
        Vector3 diagonal = Vector3.Normalize(geometry.ViewRight + geometry.ViewUp);
        harness.DragTo(pivot + diagonal);
        scale.DragSizeChange.ShouldBe(1f, Tolerance);
        harness.Release().ShouldBe(GizmoUpdateResult.DragCommitted);

        node.LocalScale.ShouldBeCloseTo(new Vector3(2f), Tolerance);
        // The uniform handle drags no single face, so it stays centred.
        node.LocalPosition.ShouldBe(Vector3.Zero);
    }

    // --- Brush nodes ---------------------------------------------------------

    [Fact]
    public void Resizing_a_brush_node_edits_its_plane_extents_and_never_its_scale()
    {
        var harness = ResizeHarness();
        SceneNode node = harness.AddSelectedBrushNode(Vector3.Zero, halfExtent: 1f);
        Brush original = node.Brush!;
        Transform before = node.LocalTransform;

        Scale(harness).Snap.Enabled = false;
        DragAxisBy(harness, GizmoHandle.AxisX, 2f);

        // THE constraint: the scale is bit-identical. A gizmo that wrote node
        // scale here would corrupt the CSG epsilon scheme and be rejected by the
        // static-world snapshot.
        node.LocalTransform.Scale.ShouldBe(before.Scale);
        node.LocalTransform.Scale.ShouldBe(Vector3.One);
        node.LocalTransform.Rotation.ShouldBe(before.Rotation);

        // The size moved into the brush instead: a NEW brush (reference identity
        // is the carve cache's validity key) two units wider and unchanged in y/z.
        node.Brush.ShouldNotBeSameAs(original);
        node.Brush!.LocalBounds.Min.ShouldBeCloseTo(new Vector3(-2f, -1f, -1f), Tolerance);
        node.Brush.LocalBounds.Max.ShouldBeCloseTo(new Vector3(2f, 1f, 1f), Tolerance);

        // ...and the node carried half the growth, which is what plants the −x
        // face where it was.
        node.LocalPosition.ShouldBeCloseTo(new Vector3(1f, 0f, 0f), Tolerance);

        // And the original instance is untouched — brushes are immutable.
        original.LocalBounds.Max.ShouldBeCloseTo(Vector3.One, Tolerance);
    }

    [Fact]
    public void A_uniform_brush_resize_scales_all_three_extents()
    {
        var harness = ResizeFrontHarness();
        SceneNode node = harness.AddSelectedBrushNode(Vector3.Zero, halfExtent: 2f);
        ScaleGizmo scale = Scale(harness);
        scale.Snap.Enabled = false;

        GizmoGeometry geometry = Prime(harness);
        Vector3 pivot = harness.Gizmo.Pivot;
        harness.Grab(pivot).ShouldBe(GizmoUpdateResult.DragBegan);

        // Four units across, dragged two units smaller: half the size.
        Vector3 diagonal = Vector3.Normalize(geometry.ViewRight + geometry.ViewUp);
        harness.DragTo(pivot - diagonal * 2f);
        harness.Release().ShouldBe(GizmoUpdateResult.DragCommitted);

        node.LocalScale.ShouldBe(Vector3.One);
        // Close to, not exactly, zero: a symmetric resize holds the object's own
        // CENTRE, and this brush's bounds are derived from its planes, so their
        // centre is a few tens of nanometres off the origin rather than bit-zero.
        // Anchoring on the origin instead would return an exact zero here and be
        // wrong for any object whose geometry really is off-centre.
        node.LocalPosition.ShouldBeCloseTo(Vector3.Zero, Tolerance);
        node.Brush!.LocalBounds.Max.ShouldBeCloseTo(new Vector3(1f), Tolerance * 10f);
    }

    [Fact]
    public void Undoing_a_brush_resize_puts_the_original_brush_instance_and_position_back()
    {
        var harness = ResizeHarness();
        SceneNode node = harness.AddSelectedBrushNode(Vector3.Zero);
        Brush original = node.Brush!;
        Transform before = node.LocalTransform;
        Scale(harness).Snap.Enabled = false;

        DragAxisBy(harness, GizmoHandle.AxisZ, 3f);
        harness.Undo.Count.ShouldBe(1);
        harness.Undo.UndoName.ShouldBe("Resize");

        harness.Undo.Undo().ShouldBeTrue();

        // The same instance, not an equal one: reference identity is what the
        // carve cache keys on, so restoring it restores the cached carve too.
        node.Brush.ShouldBeSameAs(original);
        node.LocalPosition.ShouldBe(before.Position);
    }

    [Fact]
    public void A_cancelled_brush_resize_restores_the_original_brush_and_leaves_no_history()
    {
        var harness = ResizeHarness();
        SceneNode node = harness.AddSelectedBrushNode(Vector3.Zero);
        Brush original = node.Brush!;
        Transform before = node.LocalTransform;
        Scale(harness).Snap.Enabled = false;

        GrabAxis(harness, GizmoHandle.AxisX, out Vector3 pivot, out Vector3 axis, out float length);
        harness.DragTo(pivot + axis * (length + 2.5f));
        node.Brush.ShouldNotBeSameAs(original); // the live drag really did swap it

        harness.PressEscape().ShouldBe(GizmoUpdateResult.DragCancelled);

        node.Brush.ShouldBeSameAs(original);
        node.LocalTransform.Scale.ShouldBe(before.Scale);
        node.LocalTransform.Position.ShouldBe(before.Position);
        harness.Undo.Count.ShouldBe(0);
    }

    [Fact]
    public void A_mixed_selection_resizes_the_mesh_node_and_reshapes_the_brush_node()
    {
        var harness = ResizeHarness();
        SceneNode mesh = harness.AddSelectedMeshNode(new Vector3(-2f, 0f, 0f), halfExtent: 0.5f, name: "Mesh");
        SceneNode brush = harness.AddSelectedBrushNode(new Vector3(2f, 0f, 0f));
        Scale(harness).Snap.Enabled = false;

        DragAxisBy(harness, GizmoHandle.AxisY, 1f);

        // Both grew by exactly one world unit, from very different starting
        // sizes — one through its scale, one through its planes.
        mesh.LocalScale.ShouldBeCloseTo(new Vector3(1f, 2f, 1f), Tolerance);
        brush.LocalScale.ShouldBe(Vector3.One);
        brush.Brush!.LocalBounds.Max.ShouldBeCloseTo(new Vector3(1f, 1.5f, 1f), Tolerance);
    }

    [Fact]
    public void A_brush_resize_marks_the_static_world_dirty_like_any_other_brush_edit()
    {
        var harness = ResizeHarness();
        SceneNode node = harness.AddSelectedBrushNode(Vector3.Zero);
        var renderer = new CompilingRenderer();
        harness.Scene.RebuildStaticWorld(renderer);
        harness.Scene.StaticWorldDirty.ShouldBeFalse();

        Scale(harness).Snap.Enabled = false;
        DragAxisBy(harness, GizmoHandle.AxisX, 2f);

        // The node's own Brush setter dirtied the world, so the compile sees a
        // gizmo resize exactly as it would a scripted brush swap — and the
        // recompile is scoped to the cells that brush occupies.
        harness.Scene.StaticWorldDirty.ShouldBeTrue();
        harness.Scene.RebuildStaticWorld(renderer);
        harness.Scene.LastCompileDirtyCells.ShouldNotBeEmpty();
        node.LocalScale.ShouldBe(Vector3.One);
    }

    [Fact]
    public void A_resize_that_never_left_its_starting_size_commits_nothing()
    {
        var harness = ResizeHarness();
        SceneNode node = harness.AddSelectedBrushNode(Vector3.Zero);
        Brush original = node.Brush!;
        Scale(harness);

        GrabAxis(harness, GizmoHandle.AxisX, out _, out _, out _);
        harness.Release().ShouldBe(GizmoUpdateResult.DragCancelled);

        harness.Undo.Count.ShouldBe(0);
        node.Brush.ShouldBeSameAs(original);
    }

    [Fact]
    public void The_resize_gizmo_offers_no_world_orientation()
    {
        var harness = ResizeHarness();
        SceneNode node = harness.AddSelectedNode(Vector3.Zero);
        node.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);

        ScaleGizmo scale = Scale(harness);
        scale.SupportsOrientation.ShouldBeFalse();

        // Even asked for world alignment, the handles follow the node: its local
        // +x points along world −z after the quarter turn.
        harness.Gizmos.Orientation = GizmoOrientation.World;
        Prime(harness).AxisX.ShouldBeCloseTo(-Vector3.UnitZ, Tolerance);
    }

    // --- Helpers -------------------------------------------------------------

    /// <summary>
    /// The harness every resize test uses: the <see cref="GizmoStyle.Studio"/>
    /// style, which is the engine's default and the one whose resize holds a
    /// face still.
    /// </summary>
    /// <remarks>
    /// <b>Face anchoring is a property of the style, not of the tool.</b>
    /// <see cref="GizmoStyle.Classic"/> resizes about the pivot instead, both
    /// faces moving by half the increment, which is what makes three handles
    /// enough there; <see cref="GizmoStyleTests"/> pins that half. Everything in
    /// this suite and in <see cref="ResizeIncrementTests"/> is about the anchored
    /// reading, so it says which style it means rather than inheriting the
    /// harness default (which is Classic, for the aiming reason
    /// <see cref="GizmoHarness"/> documents).
    /// </remarks>
    internal static GizmoHarness ResizeHarness() => GizmoHarness.ThreeQuarterView(GizmoStyle.Studio);

    /// <summary>The <see cref="ResizeHarness"/> camera for the uniform handle, which needs a face-on view.</summary>
    internal static GizmoHarness ResizeFrontHarness() => GizmoHarness.FrontView(style: GizmoStyle.Studio);

    internal static ScaleGizmo Scale(GizmoHarness harness)
    {
        harness.Use(GizmoMode.Scale);
        return harness.Scale;
    }

    // One update with the cursor outside the viewport: builds the gizmo geometry
    // (and publishes the pivot) without claiming a hover, so aims below are
    // computed against what the gizmo actually chose.
    internal static GizmoGeometry Prime(GizmoHarness harness)
    {
        harness.Gizmos.Update(harness.Frame(new Vector2(-10f, -10f)));
        return harness.Gizmo.Geometry;
    }

    internal static void GrabAxis(
        GizmoHarness harness, GizmoHandle handle, out Vector3 pivot, out Vector3 axis, out float length)
    {
        GizmoGeometry geometry = Prime(harness);
        pivot = harness.Gizmo.Pivot;
        axis = geometry.Axis(handle);

        // How far out the handle STANDS, which equals the axis length only in a
        // style that puts it there. Studio's handles sit on the selection's own
        // box, so asking the geometry is the only aim that survives a style
        // switch; `axis` already carries the direction, so a negative handle
        // needs no special case here.
        length = geometry.AxisReach(handle);

        // Aimed at the centre of the cube handle, so the travel measured below is
        // exactly the world distance the cursor is moved.
        harness.Hover(pivot + axis * length);
        harness.Gizmo.HoveredHandle.ShouldBe(handle);
        harness.Grab(pivot + axis * length).ShouldBe(GizmoUpdateResult.DragBegan);
    }

    /// <summary>
    /// Grabs <paramref name="handle"/>'s cube and drags it <paramref name="worldDelta"/>
    /// world units further along its axis, then commits. The travel IS the
    /// requested size change, so this reads as "make it this much bigger".
    /// </summary>
    internal static void DragAxisBy(
        GizmoHarness harness, GizmoHandle handle, float worldDelta, KeyModifiers modifiers = KeyModifiers.None)
    {
        GrabAxis(harness, handle, out Vector3 pivot, out Vector3 axis, out float length);
        harness.DragTo(pivot + axis * (length + worldDelta), modifiers);
        harness.Release().ShouldBe(GizmoUpdateResult.DragCommitted);
    }
}
