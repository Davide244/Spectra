using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Viewport;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// The two manipulator styles, and the defect that made them necessary:
/// <b>a resize could only ever move three of an object's six faces.</b>
/// </summary>
/// <remarks>
/// The engine shipped Studio's semantics (a resize plants the opposite face) on
/// Blender's roster (handles on the positive ends only). Those two do not
/// compose: with nothing to grab on the −x side, "grow this leftwards" had no
/// gesture at all, and the only way to move a negative face was to move the
/// whole object afterwards. The fix is that the roster and the anchoring are one
/// decision, made by <see cref="GizmoStyle"/>:
/// <list type="bullet">
///   <item><description>
///     <see cref="GizmoStyle.Studio"/>: six per-face handles, standing on the
///     selection's own box, each planting the face opposite itself.
///   </description></item>
///   <item><description>
///     <see cref="GizmoStyle.Classic"/>: three handles at a fixed distance from
///     the pivot, scaling about it so both faces move. Three is the right number
///     here precisely because the negative end is the same drag pushed the other
///     way.
///   </description></item>
/// </list>
/// <para>
/// Suites that pin one style's shape say so; the invariants at the bottom run
/// under both, because a gesture that cannot be cancelled exactly is broken in
/// any style.
/// </para>
/// </remarks>
public sealed class GizmoStyleTests
{
    private const float Tolerance = 1e-3f;

    // --- The roster ----------------------------------------------------------

    [Fact]
    public void Studio_offers_a_handle_on_every_face_and_classic_offers_three()
    {
        foreach (GizmoMode mode in new[] { GizmoMode.Translate, GizmoMode.Scale })
        {
            GizmoStyle.Studio.LastAxisHandle.ShouldBe(GizmoHandle.AxisNegZ);
            GizmoStyle.Classic.LastAxisHandle.ShouldBe(GizmoHandle.AxisZ);

            GizmoStyle.Studio.Offers(GizmoHandle.AxisNegX, mode).ShouldBeTrue();
            GizmoStyle.Classic.Offers(GizmoHandle.AxisNegX, mode).ShouldBeFalse();
        }
    }

    [Fact]
    public void A_rotation_has_three_rings_in_both_styles_because_a_negative_ring_is_the_same_ring()
    {
        // Turning about −x sweeps the circle turning about +x sweeps. Offering
        // both would be two handles competing for one gesture.
        GizmoStyle.Studio.Offers(GizmoHandle.AxisNegX, GizmoMode.Rotate).ShouldBeFalse();
        GizmoStyle.Classic.Offers(GizmoHandle.AxisNegX, GizmoMode.Rotate).ShouldBeFalse();
    }

    [Fact]
    public void Studio_drops_the_plane_quads_the_centre_disc_and_the_view_ring()
    {
        GizmoStyle.Studio.Offers(GizmoHandle.PlaneXY, GizmoMode.Translate).ShouldBeFalse();
        GizmoStyle.Studio.Offers(GizmoHandle.Screen, GizmoMode.Translate).ShouldBeFalse();
        GizmoStyle.Studio.Offers(GizmoHandle.Screen, GizmoMode.Rotate).ShouldBeFalse();

        GizmoStyle.Classic.Offers(GizmoHandle.PlaneXY, GizmoMode.Translate).ShouldBeTrue();
        GizmoStyle.Classic.Offers(GizmoHandle.Screen, GizmoMode.Translate).ShouldBeTrue();
        GizmoStyle.Classic.Offers(GizmoHandle.Screen, GizmoMode.Rotate).ShouldBeTrue();

        // The uniform resize cube survives in both: dropping it for fidelity
        // would remove the editor's only uniform resize.
        GizmoStyle.Studio.Offers(GizmoHandle.Screen, GizmoMode.Scale).ShouldBeTrue();
        GizmoStyle.Classic.Offers(GizmoHandle.Screen, GizmoMode.Scale).ShouldBeTrue();
    }

    [Fact]
    public void What_a_style_offers_is_what_it_draws()
    {
        // Six arrows of nine lines each in Studio; three of those plus three
        // four-line quads and the centre circle in Classic. Drawing reads the
        // roster through the same geometry the hit tester picks against, so a
        // count is a real check that the two agree on the roster.
        DrawnLines(GizmoStyle.Studio, GizmoMode.Translate).ShouldBe(6 * 9);
        DrawnLines(GizmoStyle.Classic, GizmoMode.Translate)
            .ShouldBe((3 * 9) + (3 * 4) + GizmoHitTesting.RingSegments);

        // A shaft and a twelve-edge cube per handle, plus the centre cube.
        DrawnLines(GizmoStyle.Studio, GizmoMode.Scale).ShouldBe((6 * 13) + 12);
        DrawnLines(GizmoStyle.Classic, GizmoMode.Scale).ShouldBe((3 * 13) + 12);

        DrawnLines(GizmoStyle.Studio, GizmoMode.Rotate).ShouldBe(3 * GizmoHitTesting.RingSegments);
        DrawnLines(GizmoStyle.Classic, GizmoMode.Rotate).ShouldBe(4 * GizmoHitTesting.RingSegments);
    }

    // --- The defect this all exists for --------------------------------------

    [Fact]
    public void A_negative_face_handle_grows_the_negative_face_and_plants_the_positive_one()
    {
        // THE regression test. Before the negative handles existed there was no
        // gesture that could do this at all.
        var harness = GizmoHarness.ThreeQuarterView(GizmoStyle.Studio);
        SceneNode node = harness.AddSelectedBrushNode(Vector3.Zero, halfExtent: 1f);
        ScaleGizmoDragTests.Scale(harness).Snap.Enabled = false;

        ScaleGizmoDragTests.DragAxisBy(harness, GizmoHandle.AxisNegX, 2f);

        // Two units wider, all of it on the −x side.
        node.Brush!.LocalBounds.Size.X.ShouldBe(4f, Tolerance);
        (node.LocalPosition.X + node.Brush.LocalBounds.Max.X).ShouldBe(1f, Tolerance);   // planted
        (node.LocalPosition.X + node.Brush.LocalBounds.Min.X).ShouldBe(-3f, Tolerance);  // out by two
        node.LocalScale.ShouldBe(Vector3.One);
    }

    [Fact]
    public void A_negative_handle_resizes_its_own_axis_and_nothing_else()
    {
        // The axis mask is taken from the handle's AXIS, not its direction. Read
        // straight, the negative values fall into the mask table's uniform
        // default and a −x drag silently resizes all three axes: no throw, no
        // log, and geometry that is simply wrong.
        var harness = GizmoHarness.ThreeQuarterView(GizmoStyle.Studio);
        SceneNode node = harness.AddSelectedBrushNode(Vector3.Zero, halfExtent: 1f);
        ScaleGizmoDragTests.Scale(harness).Snap.Enabled = false;

        ScaleGizmoDragTests.DragAxisBy(harness, GizmoHandle.AxisNegY, 1.5f);

        Vector3 size = node.Brush!.LocalBounds.Size;
        size.Y.ShouldBe(3.5f, Tolerance);
        size.X.ShouldBe(2f, Tolerance);
        size.Z.ShouldBe(2f, Tolerance);
    }

    [Fact]
    public void Both_ends_of_an_axis_are_pickable_and_they_are_different_handles()
    {
        // Pick/drag agreement for the new values: aiming at the −x handle must
        // highlight the −x handle, not its +x twin on the same line.
        var harness = GizmoHarness.ThreeQuarterView(GizmoStyle.Studio);
        harness.AddSelectedBrushNode(Vector3.Zero, halfExtent: 1f);
        ScaleGizmoDragTests.Scale(harness);

        harness.Hover(harness.GrabPointFor(GizmoHandle.AxisX));
        harness.Gizmo.HoveredHandle.ShouldBe(GizmoHandle.AxisX);

        harness.Hover(harness.GrabPointFor(GizmoHandle.AxisNegX));
        harness.Gizmo.HoveredHandle.ShouldBe(GizmoHandle.AxisNegX);
    }

    [Fact]
    public void A_negative_arrow_moves_the_selection_the_same_way_its_positive_twin_does()
    {
        // A move has no anchored face, so the two ends of an axis are one
        // constraint seen twice. The negative arrow exists so there is something
        // to grab on that side, not to mean something different.
        var harness = GizmoHarness.ThreeQuarterView(GizmoStyle.Studio);
        SceneNode node = harness.AddSelectedBrushNode(Vector3.Zero, halfExtent: 1f);
        harness.Use(GizmoMode.Translate);
        harness.Translate.Snap.Enabled = false;

        harness.Grab(harness.GrabPointFor(GizmoHandle.AxisNegX)).ShouldBe(GizmoUpdateResult.DragBegan);
        harness.Gizmo.ActiveHandle.ShouldBe(GizmoHandle.AxisNegX);
        harness.DragBy(Vector3.UnitX * 3f);
        harness.Release().ShouldBe(GizmoUpdateResult.DragCommitted);

        node.LocalPosition.X.ShouldBe(3f, Tolerance);
    }

    [Fact]
    public void Every_member_of_a_selection_grows_the_way_the_handle_points()
    {
        // The handle's sign is a fact about the GIZMO's frame; the anchor is a
        // coordinate in the NODE's. A member turned half a turn away has its
        // local +x pointing where the handle does not, so reading the sign
        // straight plants the face on the side the user is dragging toward and
        // that one object grows backwards out of the same drag.
        var harness = GizmoHarness.ThreeQuarterView(GizmoStyle.Studio);
        SceneNode flipped = harness.AddSelectedBrushNode(new Vector3(-4f, 0f, 0f), 1f, "Flipped");
        flipped.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);
        SceneNode plain = harness.AddSelectedBrushNode(new Vector3(4f, 0f, 0f), 1f, "Plain");
        ScaleGizmoDragTests.Scale(harness).Snap.Enabled = false;

        ScaleGizmoDragTests.DragAxisBy(harness, GizmoHandle.AxisX, 2f);

        // Both started two units across and both are now four, and both grew
        // toward +x: each planted the face on its own −x side, whichever of its
        // local faces that happens to be.
        (float Min, float Max) flippedSpan = WorldSpanX(flipped);
        flippedSpan.Min.ShouldBe(-5f, Tolerance);
        flippedSpan.Max.ShouldBe(-1f, Tolerance);

        (float Min, float Max) plainSpan = WorldSpanX(plain);
        plainSpan.Min.ShouldBe(3f, Tolerance);
        plainSpan.Max.ShouldBe(7f, Tolerance);
    }

    [Fact]
    public void A_symmetric_resize_holds_the_object_s_centre_not_its_origin()
    {
        // Off-centre geometry: the mesh spans 0..2 in x, so its centre is a whole
        // unit from the node's origin. Scaling about the origin would move the
        // near face by nothing and the far face by the whole size change, which
        // leaves the handle under the cursor only for centred objects.
        var harness = GizmoHarness.ThreeQuarterView(GizmoStyle.Classic);
        SceneNode node = harness.AddNode(Vector3.Zero, "Offset");
        node.MeshRenderer = new MeshRenderer(
            BoxMesh.Spanning(new Vector3(0f, -0.5f, -0.5f), new Vector3(2f, 0.5f, 0.5f)),
            new Material(null));
        harness.Scene.Selection.Add(node);
        ScaleGizmoDragTests.Scale(harness).Snap.Enabled = false;

        // Classic is symmetric by default: one unit of travel makes it two units
        // bigger, one on each side of its own centre.
        ScaleGizmoDragTests.DragAxisBy(harness, GizmoHandle.AxisX, 1f);

        node.LocalScale.X.ShouldBe(2f, Tolerance);
        // The centre stayed at x = 1 and the object grew to span −1..3.
        (node.LocalPosition.X + 0f * node.LocalScale.X).ShouldBe(-1f, Tolerance);
        (node.LocalPosition.X + 2f * node.LocalScale.X).ShouldBe(3f, Tolerance);
    }

    // --- Classic's half of the pairing ---------------------------------------

    [Fact]
    public void A_classic_resize_moves_both_faces_and_leaves_the_node_where_it_is()
    {
        var harness = GizmoHarness.ThreeQuarterView(GizmoStyle.Classic);
        SceneNode node = harness.AddSelectedBrushNode(new Vector3(3f, 0f, 0f), halfExtent: 1f);
        ScaleGizmoDragTests.Scale(harness).Snap.Enabled = false;

        // The handle tracks the cursor, so one unit of travel moves the face it
        // stands over by one unit and the opposite face by one the other way.
        ScaleGizmoDragTests.DragAxisBy(harness, GizmoHandle.AxisX, 1f);

        node.Brush!.LocalBounds.Size.X.ShouldBe(4f, Tolerance);
        node.LocalPosition.ShouldBe(new Vector3(3f, 0f, 0f)); // bit-identical: nothing wrote it
    }

    [Theory]
    [InlineData(GizmoStyleKind.Studio)]
    [InlineData(GizmoStyleKind.Classic)]
    public void The_modifier_asks_for_the_anchoring_the_style_is_not_doing(GizmoStyleKind kind)
    {
        GizmoStyle style = StyleFor(kind);
        var harness = GizmoHarness.ThreeQuarterView(style);
        SceneNode node = harness.AddSelectedBrushNode(new Vector3(3f, 0f, 0f), halfExtent: 1f);
        ScaleGizmo scale = ScaleGizmoDragTests.Scale(harness);
        scale.Snap.Enabled = false;
        scale.SymmetricModifier.ShouldBe(KeyModifiers.Shift);

        ScaleGizmoDragTests.DragAxisBy(harness, GizmoHandle.AxisX, 1f, KeyModifiers.Shift);

        if (style.FaceAnchoredResize)
        {
            // Studio, inverted: symmetric. Travel is half the size change, so one
            // unit of travel makes it two units bigger and moves nothing.
            node.Brush!.LocalBounds.Size.X.ShouldBe(4f, Tolerance);
            node.LocalPosition.ShouldBe(new Vector3(3f, 0f, 0f));
        }
        else
        {
            // Classic, inverted: face-anchored. Travel IS the size change, and
            // the node carries half of it so the far face stays put.
            node.Brush!.LocalBounds.Size.X.ShouldBe(3f, Tolerance);
            node.LocalPosition.X.ShouldBe(3.5f, Tolerance);
        }
    }

    // --- Where the handles stand ---------------------------------------------

    [Fact]
    public void Studio_handles_stand_clear_of_the_selection_box_and_classic_handles_do_not_move()
    {
        var studio = GizmoHarness.ThreeQuarterView(GizmoStyle.Studio);
        studio.AddSelectedBrushNode(Vector3.Zero, halfExtent: 2f);
        studio.Use(GizmoMode.Scale);
        GizmoGeometry studioGeometry = studio.LiveGeometry();

        // Outside the face it sits on, and by a gap rather than by a whole
        // gizmo's worth: the handle is ON the object, not floating beside it.
        studioGeometry.AxisReach(GizmoHandle.AxisX).ShouldBeGreaterThan(2f);
        studioGeometry.AxisReach(GizmoHandle.AxisX).ShouldBeLessThan(2f + studioGeometry.AxisLength);
        studioGeometry.AxisReach(GizmoHandle.AxisNegX)
            .ShouldBe(studioGeometry.AxisReach(GizmoHandle.AxisX), Tolerance);

        // And the handle's whole BODY clears the face, not just the point the
        // reach is measured to. Measuring to a cube's centre buries half of it in
        // the surface it is meant to be standing on, and measuring to an arrow's
        // tip buries the entire head.
        studioGeometry.TryGetHandleBox(GizmoHandle.AxisX, out Vector3 cube, out float radius).ShouldBeTrue();
        (Vector3.Dot(cube - studioGeometry.Pivot, Vector3.UnitX) - radius).ShouldBeGreaterThan(2f);

        studio.Use(GizmoMode.Translate);
        GizmoGeometry arrows = studio.LiveGeometry();
        arrows.TryGetAxisSegment(GizmoHandle.AxisNegZ, out Vector3 tail, out _).ShouldBeTrue();
        Vector3.Dot(tail - arrows.Pivot, -Vector3.UnitZ).ShouldBeGreaterThan(2f);

        var classic = GizmoHarness.ThreeQuarterView(GizmoStyle.Classic);
        classic.AddSelectedBrushNode(Vector3.Zero, halfExtent: 2f);
        classic.Use(GizmoMode.Scale);
        GizmoGeometry classicGeometry = classic.LiveGeometry();

        // The classic layout is a property of the screen alone, so a bigger
        // object does not push its handles anywhere.
        classicGeometry.AxisReach(GizmoHandle.AxisX).ShouldBe(classicGeometry.AxisLength);
    }

    [Fact]
    public void A_tiny_object_still_gets_handles_far_enough_apart_to_aim_at()
    {
        // Without a floor, the smallest thing in the scene becomes the hardest to
        // manipulate: every handle collapses into one pile at the pivot. A flat
        // object (zero extent on an axis) is the same case.
        var harness = GizmoHarness.ThreeQuarterView(GizmoStyle.Studio);
        harness.AddSelectedBrushNode(Vector3.Zero, halfExtent: 0.001f);
        harness.Use(GizmoMode.Scale);

        GizmoGeometry geometry = harness.LiveGeometry();
        geometry.AxisReach(GizmoHandle.AxisX)
            .ShouldBeGreaterThanOrEqualTo(geometry.AxisLength * GizmoStyle.Studio.MinimumReachFactor);

        harness.Hover(harness.GrabPointFor(GizmoHandle.AxisNegZ));
        harness.Gizmo.HoveredHandle.ShouldBe(GizmoHandle.AxisNegZ);
    }

    [Fact]
    public void The_studio_pivot_is_the_centre_of_the_selection_box_and_the_classic_one_is_the_average_origin()
    {
        // Two brushes of different sizes: the box centre and the average of the
        // origins are genuinely different points, which is the whole reason the
        // pivot is a style decision.
        foreach (GizmoStyle style in new[] { GizmoStyle.Studio, GizmoStyle.Classic })
        {
            var harness = new GizmoHarness(new Vector3(20f, 15f, 25f), Vector3.Zero, style: style);
            harness.AddSelectedBrushNode(Vector3.Zero, halfExtent: 1f, name: "Small");
            harness.AddSelectedBrushNode(new Vector3(10f, 0f, 0f), halfExtent: 3f, name: "Big");
            harness.Use(GizmoMode.Scale);

            float expected = style.PivotMode == GizmoPivotMode.BoundsCentre ? 6f : 5f;
            harness.LiveGeometry().Pivot.X.ShouldBe(expected, Tolerance);
        }
    }

    // --- Switching -----------------------------------------------------------

    [Fact]
    public void The_toggle_verb_flips_the_style_and_reaches_all_three_tools()
    {
        var harness = GizmoHarness.ThreeQuarterView(GizmoStyle.Studio);
        GizmoStyle? announced = null;
        harness.Gizmos.StyleChanged += style => announced = style;

        harness.Gizmos.Apply(GizmoCommand.ToggleStyle).ShouldBeTrue();

        harness.Gizmos.Style.ShouldBeSameAs(GizmoStyle.Classic);
        announced.ShouldBeSameAs(GizmoStyle.Classic);
        harness.Translate.Style.ShouldBeSameAs(GizmoStyle.Classic);
        harness.Rotate.Style.ShouldBeSameAs(GizmoStyle.Classic);
        harness.Scale.Style.ShouldBeSameAs(GizmoStyle.Classic);

        harness.Gizmos.Apply(GizmoCommand.ToggleStyle).ShouldBeTrue();
        harness.Gizmos.Style.ShouldBeSameAs(GizmoStyle.Studio);
    }

    [Fact]
    public void Y_is_the_default_binding_for_the_style_toggle()
    {
        GizmoShortcuts.TryResolve("Y", out GizmoCommand command).ShouldBeTrue();
        command.ShouldBe(GizmoCommand.ToggleStyle);
    }

    [Fact]
    public void Switching_style_mid_drag_rolls_the_gesture_back()
    {
        // The style decides where the handles stand, so changing it during a
        // gesture would move the constraint out from under a live grab, and would
        // leave the outgoing tool holding an open undo transaction.
        var harness = GizmoHarness.ThreeQuarterView(GizmoStyle.Studio);
        SceneNode node = harness.AddSelectedBrushNode(Vector3.Zero, halfExtent: 1f);
        harness.Use(GizmoMode.Translate);

        harness.Grab(harness.GrabPointFor(GizmoHandle.AxisX)).ShouldBe(GizmoUpdateResult.DragBegan);
        harness.DragBy(Vector3.UnitX * 4f);
        node.LocalPosition.X.ShouldNotBe(0f);

        harness.Gizmos.Style = GizmoStyle.Classic;

        node.LocalPosition.ShouldBe(Vector3.Zero);
        harness.Undo.Count.ShouldBe(0);
        harness.Gizmo.State.ShouldBe(GizmoInteractionState.Idle);
    }

    [Fact]
    public void Setting_the_same_style_changes_nothing_and_does_not_disturb_a_drag()
    {
        var harness = GizmoHarness.ThreeQuarterView(GizmoStyle.Studio);
        SceneNode node = harness.AddSelectedBrushNode(Vector3.Zero, halfExtent: 1f);
        harness.Use(GizmoMode.Translate);

        harness.Grab(harness.GrabPointFor(GizmoHandle.AxisX)).ShouldBe(GizmoUpdateResult.DragBegan);
        harness.DragBy(Vector3.UnitX * 4f);

        harness.Gizmos.Style = GizmoStyle.Studio;

        harness.Gizmo.State.ShouldBe(GizmoInteractionState.Dragging);
        harness.Release().ShouldBe(GizmoUpdateResult.DragCommitted);
        node.LocalPosition.X.ShouldBe(4f, Tolerance);
    }

    [Fact]
    public void A_press_on_an_object_still_picks_it_up_in_a_style_with_no_centre_disc()
    {
        // Studio draws no centre disc, so the hit tester offers none. The
        // free-move CONSTRAINT behind it still has to exist, because that is what
        // a press on the object itself is routed into: without it, "grab the
        // thing and move it" would stop working the moment the style changed, and
        // the press would fall through to a plain click-select.
        var harness = new ViewportHarness(gizmoStyle: GizmoStyle.Studio);
        harness.Orbit(Vector3.Zero, 24f, 0.9f, -0.4f);
        SceneNode node = harness.AddBrush(new Vector3(3f, 0f, 0f), 1f);
        Vector2 press = harness.WorldToScreen(node.WorldPosition);

        harness.Gizmos.Translate.FreeMoveHandle.ShouldBe(GizmoHandle.Screen);
        harness.Press(press).ShouldBe(ViewportDragMode.SelectAndMove);
        harness.Scene.Selection.Items.ShouldBe(new[] { node });

        harness.Drag(press + new Vector2(60f, -30f));
        node.LocalPosition.ShouldNotBe(new Vector3(3f, 0f, 0f));

        harness.Release(press + new Vector2(60f, -30f));
        harness.Undo.UndoCount.ShouldBe(1);
        harness.Undo.Undo().ShouldBeTrue();
        node.LocalPosition.ShouldBe(new Vector3(3f, 0f, 0f));
    }

    // --- Invariants, under both styles ---------------------------------------

    [Theory]
    [InlineData(GizmoStyleKind.Studio)]
    [InlineData(GizmoStyleKind.Classic)]
    public void A_cancelled_resize_restores_the_transform_bit_for_bit(GizmoStyleKind kind)
    {
        var harness = GizmoHarness.ThreeQuarterView(StyleFor(kind));
        SceneNode node = harness.AddSelectedBrushNode(new Vector3(1.5f, 0f, 0f), halfExtent: 1f);
        Brush original = node.Brush!;
        Transform before = node.LocalTransform;
        ScaleGizmoDragTests.Scale(harness).Snap.Enabled = false;

        ScaleGizmoDragTests.GrabAxis(
            harness, GizmoHandle.AxisX, out Vector3 pivot, out Vector3 axis, out float reach);
        harness.DragTo(pivot + axis * (reach + 2.6f));
        node.Brush.ShouldNotBeSameAs(original); // the live drag really did swap it

        harness.PressEscape().ShouldBe(GizmoUpdateResult.DragCancelled);

        node.Brush.ShouldBeSameAs(original);
        node.LocalTransform.Position.ShouldBe(before.Position);
        node.LocalTransform.Scale.ShouldBe(before.Scale);
        harness.Undo.Count.ShouldBe(0);
    }

    [Theory]
    [InlineData(GizmoStyleKind.Studio)]
    [InlineData(GizmoStyleKind.Classic)]
    public void A_grab_that_never_moved_commits_nothing(GizmoStyleKind kind)
    {
        var harness = GizmoHarness.ThreeQuarterView(StyleFor(kind));
        SceneNode node = harness.AddSelectedBrushNode(Vector3.Zero, halfExtent: 1f);
        Brush original = node.Brush!;
        ScaleGizmoDragTests.Scale(harness);

        ScaleGizmoDragTests.GrabAxis(harness, GizmoHandle.AxisX, out _, out _, out _);
        harness.Release().ShouldBe(GizmoUpdateResult.DragCancelled);

        node.Brush.ShouldBeSameAs(original);
        harness.Undo.Count.ShouldBe(0);
    }

    [Theory]
    [InlineData(GizmoStyleKind.Studio)]
    [InlineData(GizmoStyleKind.Classic)]
    public void One_notch_is_one_increment_in_either_style(GizmoStyleKind kind)
    {
        // The fixed-increment property is about the SIZE, so it survives the
        // styles disagreeing about which face moves and about how the cursor's
        // travel maps onto the change.
        GizmoStyle style = StyleFor(kind);
        var harness = GizmoHarness.ThreeQuarterView(style);
        SceneNode node = harness.AddSelectedBrushNode(Vector3.Zero, halfExtent: 1f);
        ScaleGizmo scale = ScaleGizmoDragTests.Scale(harness);
        scale.Snap.Enabled = true;
        scale.Snap.Increment = 1f;

        // Comfortably past half a notch on whichever mapping this style uses.
        float travel = style.FaceAnchoredResize ? 0.6f : 0.3f;
        ScaleGizmoDragTests.DragAxisBy(harness, GizmoHandle.AxisX, travel);

        node.Brush!.LocalBounds.Size.X.ShouldBe(3f, Tolerance);
    }

    // --- Helpers -------------------------------------------------------------

    // A brush node's extent along world x, taken through its world matrix so a
    // rotated node reports where its geometry actually is rather than what its
    // local bounds say.
    private static (float Min, float Max) WorldSpanX(SceneNode node)
    {
        Aabb bounds = node.Brush!.LocalBounds;
        Matrix4x4 world = node.WorldMatrix;
        float min = float.MaxValue;
        float max = float.MinValue;

        for (int corner = 0; corner < 8; corner++)
        {
            var local = new Vector3(
                (corner & 1) == 0 ? bounds.Min.X : bounds.Max.X,
                (corner & 2) == 0 ? bounds.Min.Y : bounds.Max.Y,
                (corner & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);

            float x = Vector3.Transform(local, world).X;
            min = MathF.Min(min, x);
            max = MathF.Max(max, x);
        }

        return (min, max);
    }

    private static GizmoStyle StyleFor(GizmoStyleKind kind) =>
        kind == GizmoStyleKind.Studio ? GizmoStyle.Studio : GizmoStyle.Classic;

    private static int DrawnLines(GizmoStyle style, GizmoMode mode)
    {
        var harness = GizmoHarness.ThreeQuarterView(style);
        harness.AddSelectedBrushNode(Vector3.Zero, halfExtent: 1f);
        harness.Use(mode);
        harness.Hover(new Vector3(0f, 0f, 100f)); // aimed away, so nothing highlights

        var output = new DebugDraw();
        harness.Gizmo.Draw(output);

        // Two vertices per line, six interleaved floats per vertex.
        return output.VertexCount / 2;
    }
}
