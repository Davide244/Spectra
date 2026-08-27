using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Input;
using System;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// The failure modes a drag must survive without corrupting the scene: a
/// viewport that vanishes mid-gesture, and handles that promise a grab the
/// drag would refuse.
/// </summary>
/// <remarks>
/// <b>These pin invariants that used to live in the host.</b> The demo host
/// happens to reset the viewport when the window minimizes, which was the only
/// thing standing between a zero-size frame and NaN written through every
/// selected node's transform: the cursor ray divides by the viewport size, and
/// a NaN ray passed every parallel guard because every comparison with NaN is
/// false. A future editor host (the Uno/Avalonia viewport re-host) is under no
/// obligation to behave like the demo host, so the tool itself must refuse.
/// </remarks>
public sealed class GizmoRobustnessTests
{
    private const float AlongAxis = 0.8f;
    private const float RoundTrip = 1e-3f;

    [Fact]
    public void A_zero_size_viewport_frame_mid_drag_holds_the_drag_and_stays_finite()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        SceneNode node = harness.AddSelectedNode(Vector3.Zero);
        float length = harness.GeometryAt(Vector3.Zero).AxisLength;

        harness.Grab(Vector3.UnitX * (length * AlongAxis));
        harness.DragBy(Vector3.UnitX * 2f);

        // The window minimizes mid-drag: a frame arrives with no viewport. The
        // drag must hold its last value exactly, not project through a NaN ray.
        var degenerate = new EditorInputFrame(
            new Vector2(100f, 100f), Vector2.Zero, PointerButtons.Left,
            PointerButtons.None, PointerButtons.None, KeyModifiers.None,
            Vector2.Zero, 1f / 60f);
        harness.Gizmos.Update(degenerate).ShouldBe(GizmoUpdateResult.DragUpdated);

        float.IsFinite(node.LocalPosition.X).ShouldBeTrue();
        node.LocalPosition.X.ShouldBe(2f, RoundTrip);

        // The viewport comes back and the drag continues from the same grab.
        harness.DragBy(Vector3.UnitX * 3f);
        harness.Release();
        node.LocalPosition.X.ShouldBe(3f, RoundTrip);
    }

    [Fact]
    public void The_parallel_guards_refuse_a_non_finite_ray_instead_of_passing_it()
    {
        // Every comparison with NaN is false, so a guard written `< epsilon`
        // waves NaN through as success. The guards are written negated so a
        // NaN denominator refuses, which is what keeps the drag path's
        // "hold the last value" contract meaningful when a ray goes bad.
        var nanRay = new Ray3(new Vector3(float.NaN), new Vector3(float.NaN));

        GizmoMath.TryClosestPointOnLine(in nanRay, Vector3.Zero, Vector3.UnitX, out _).ShouldBeFalse();
        GizmoMath.TryRayPlane(in nanRay, Vector3.Zero, Vector3.UnitY, out _).ShouldBeFalse();
    }

    // --- Pick/drag agreement -------------------------------------------------
    //
    // A handle must never highlight and then have its grab refused: the press
    // would fall through and read as "the gizmo ignored my click" (or worse,
    // as a selection-replacing marquee over empty space). The rotate tester
    // pioneered the rule; these pin it for the translate arrows and the scale
    // shafts, whose drags project through TryClosestPointOnLine and refuse
    // within ~1.8 degrees of end-on.

    [Fact]
    public void An_end_on_translate_arrow_is_not_picked_because_its_drag_would_refuse()
    {
        (GizmoGeometry geometry, Ray3 ray) = EndOnAxisView();

        // The drag-side projection refuses this ray...
        GizmoMath.TryClosestPointOnLine(in ray, geometry.Pivot, geometry.AxisX, out _).ShouldBeFalse();

        // ...so the pick must not offer the handle the drag would refuse.
        GizmoPick pick = TranslateGizmoHitTester.Pick(
            in geometry, in ray, TranslateGizmoHitTester.DefaultTolerancePixels);
        pick.Handle.ShouldNotBe(GizmoHandle.AxisX);
    }

    [Fact]
    public void An_end_on_scale_shaft_is_not_picked_because_its_drag_would_refuse()
    {
        (GizmoGeometry geometry, Ray3 ray) = EndOnAxisView();

        GizmoPick pick = ScaleGizmoHitTester.Pick(
            in geometry, in ray, GizmoHitTesting.DefaultTolerancePixels);
        pick.Handle.ShouldNotBe(GizmoHandle.AxisX);
    }

    // A camera sighting almost straight down the +X axis (about 0.9 degrees
    // off, inside the ~1.8 degree parallel refusal), with the pick ray aimed
    // at the arrow's midpoint: the exact spot where the arrow's foreshortened
    // silhouette used to pass the proximity test while the grab then refused.
    private static (GizmoGeometry Geometry, Ray3 Ray) EndOnAxisView()
    {
        var camera = new Camera
        {
            Position = new Vector3(10f, 0.15f, 0f),
            AspectRatio = 800f / 600f,
        };
        camera.LookAt(Vector3.Zero);
        var viewport = new Vector2(800f, 600f);

        GizmoGeometry geometry = GizmoGeometry.Build(
            camera, Vector3.Zero, Quaternion.Identity, viewport, GizmoGeometry.DefaultPixelSize);

        Vector3 aim = geometry.Pivot + geometry.AxisX * (geometry.AxisLength * 0.5f);
        Vector4 clip = Vector4.Transform(new Vector4(aim, 1f), camera.GetViewProjection());
        var cursor = new Vector2(
            (clip.X / clip.W + 1f) * 0.5f * viewport.X,
            (1f - clip.Y / clip.W) * 0.5f * viewport.Y);

        return (geometry, camera.ScreenPointToRay(cursor, viewport));
    }
}
