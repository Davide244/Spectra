using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Gizmos;
using System;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// The invariant the whole CSG pipeline rests on, checked against the two new
/// tools: <b>a brush node's world transform stays rigid — rotation and
/// translation only — no matter what the manipulator does to it.</b>
/// </summary>
/// <remarks>
/// <c>Scene</c>'s snapshot rejects a non-rigid brush node rather than carving it,
/// because the carve epsilons assume unit-length plane normals and a scale in the
/// matrix silently changes what every distance tolerance means. A gizmo is the
/// one place in the editor that writes transforms continuously, so it is the one
/// place a violation would show up sixty times a second. These tests drive the
/// real gestures and then run the real compile.
/// </remarks>
public sealed class GizmoBrushRigidityTests
{
    [Fact]
    public void Rotating_a_brush_node_keeps_its_transform_rigid_and_compiles()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        SceneNode node = harness.AddSelectedBrushNode(Vector3.Zero, halfExtent: 1f);
        var renderer = new CompilingRenderer();
        harness.Scene.RebuildStaticWorld(renderer);

        RotateGizmo rotate = (RotateGizmo)harness.Use(GizmoMode.Rotate);
        rotate.Snap.Increment = 45f;
        RotateGizmoDragTests.Sweep(harness, GizmoHandle.AxisY, 0.8f); // snaps to 45°

        node.LocalScale.ShouldBe(Vector3.One);
        ShouldBeRigid(node.WorldMatrix);

        // The compile is the real judge: it validates rigidity itself and would
        // report a defect instead of a result.
        harness.Scene.StaticWorldDirty.ShouldBeTrue();
        harness.Scene.RebuildStaticWorld(renderer);
        harness.Scene.LastCompileDirtyCells.ShouldNotBeEmpty();
        harness.Scene.StaticWorld.ShouldNotBeNull();
    }

    [Fact]
    public void Resizing_a_brush_node_keeps_its_transform_rigid_and_compiles()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        SceneNode node = harness.AddSelectedBrushNode(Vector3.Zero, halfExtent: 1f);
        var renderer = new CompilingRenderer();
        harness.Scene.RebuildStaticWorld(renderer);

        ScaleGizmo scale = (ScaleGizmo)harness.Use(GizmoMode.Scale);
        scale.Snap.Enabled = false;

        harness.Gizmos.Update(harness.Frame(new Vector2(-10f, -10f)));
        GizmoGeometry geometry = harness.Gizmo.Geometry;
        harness.Grab(geometry.AxisX * geometry.AxisLength).ShouldBe(GizmoUpdateResult.DragBegan);
        // Three world units wider: the cursor's travel along the axis IS the
        // size change, so the two-unit brush ends up five across.
        harness.DragTo(geometry.AxisX * (geometry.AxisLength + 3f));
        harness.Release().ShouldBe(GizmoUpdateResult.DragCommitted);

        node.LocalScale.ShouldBe(Vector3.One);
        ShouldBeRigid(node.WorldMatrix);
        node.Brush!.LocalBounds.Max.X.ShouldBe(2.5f, 1e-3f);
        // Face-anchored, so the node carried half the growth — a translation,
        // which is rigid, and never a scale.
        node.LocalPosition.X.ShouldBe(1.5f, 1e-3f);

        harness.Scene.RebuildStaticWorld(renderer);
        harness.Scene.StaticWorld.ShouldNotBeNull();
    }

    [Fact]
    public void Rotating_a_brush_node_moves_it_the_same_way_a_scripted_rotation_would()
    {
        // The same claim GizmoBrushRecompileTests makes for a move: the gizmo has
        // no privileged path into the scene, so a dragged rotation and an
        // assigned one are the same edit.
        var scripted = GizmoHarness.ThreeQuarterView();
        SceneNode scriptedNode = scripted.AddSelectedBrushNode(new Vector3(4f, 0f, 0f));
        scriptedNode.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 45f * MathF.PI / 180f);

        var dragged = GizmoHarness.ThreeQuarterView();
        SceneNode draggedNode = dragged.AddSelectedBrushNode(new Vector3(4f, 0f, 0f));
        RotateGizmo rotate = (RotateGizmo)dragged.Use(GizmoMode.Rotate);
        rotate.Snap.Increment = 45f;
        RotateGizmoDragTests.Sweep(dragged, GizmoHandle.AxisY, 0.8f);

        // A single-node selection rotates about its own position, so the
        // position must come back bit-identical while the rotation matches to
        // float precision.
        draggedNode.LocalPosition.ShouldBe(scriptedNode.LocalPosition);
        RotateGizmoDragTests.ShouldRotateLike(draggedNode.LocalRotation, scriptedNode.LocalRotation);
    }

    [Fact]
    public void Resizing_a_group_node_with_brush_children_is_refused_outright()
    {
        // Rigidity is a SUBTREE property: a scale written on a brushless group
        // node makes every brush hanging under it non-rigid, and the compile
        // rejects the whole placement snapshot rather than that one entry — so
        // one such scale freezes the static world for the entire scene until it
        // is undone. Routing on the node's own Brush missed this shape entirely.
        var harness = GizmoHarness.ThreeQuarterView();
        SceneNode group = harness.AddNode(Vector3.Zero, "Group");
        SceneNode child = group.CreateChild("BrushChild");
        child.Brush = Brush.CreateBox(new Vector3(-1f), new Vector3(1f));
        harness.Scene.Selection.Add(group);
        group.SubtreeBrushCount.ShouldBe(1);

        var renderer = new CompilingRenderer();
        harness.Scene.RebuildStaticWorld(renderer);
        harness.Scene.StaticWorld.ShouldNotBeNull();

        ScaleGizmo scale = (ScaleGizmo)harness.Use(GizmoMode.Scale);
        scale.Snap.Enabled = false;

        harness.Gizmos.Update(harness.Frame(new Vector2(-10f, -10f)));
        GizmoGeometry geometry = harness.Gizmo.Geometry;

        // The handle is there and hit-tests, but the gesture is declined rather
        // than opening a transaction it has nowhere to land.
        harness.Grab(geometry.AxisX * geometry.AxisLength).ShouldBe(GizmoUpdateResult.Hovering);
        harness.Gizmo.ActiveHandle.ShouldBe(GizmoHandle.None);
        harness.Undo.IsTransactionOpen.ShouldBeFalse();
        harness.Undo.Count.ShouldBe(0);

        group.LocalScale.ShouldBe(Vector3.One);
        ShouldBeRigid(child.WorldMatrix);

        // The judge again: an unrelated brush edit still compiles, which is the
        // property a non-rigid placement destroys scene-wide.
        SceneNode elsewhere = harness.AddNode(new Vector3(50f, 0f, 0f), "Far");
        elsewhere.Brush = Brush.CreateBox(new Vector3(-1f), new Vector3(1f));
        harness.Scene.RebuildStaticWorld(renderer);
        harness.Scene.StaticWorld.ShouldNotBeNull();
    }

    [Fact]
    public void A_mixed_selection_resizes_what_it_can_and_declines_the_rest()
    {
        // The declined node keeps a slot in the tool's per-target list, so the
        // mesh node beside it must still be resized — and by the right factor.
        var harness = GizmoHarness.ThreeQuarterView();
        SceneNode mesh = harness.AddMeshNode(new Vector3(-4f, 0f, 0f), 0.5f, "Mesh");
        SceneNode group = harness.AddNode(new Vector3(4f, 0f, 0f), "Group");
        SceneNode child = group.CreateChild("BrushChild");
        child.Brush = Brush.CreateBox(new Vector3(-1f), new Vector3(1f));

        harness.Scene.Selection.Add(mesh);
        harness.Scene.Selection.Add(group);

        ScaleGizmo scale = (ScaleGizmo)harness.Use(GizmoMode.Scale);
        scale.Snap.Enabled = false;

        harness.Gizmos.Update(harness.Frame(new Vector2(-10f, -10f)));
        GizmoGeometry geometry = harness.Gizmo.Geometry;
        Vector3 pivot = geometry.Pivot;

        harness.Grab(pivot + geometry.AxisX * geometry.AxisLength).ShouldBe(GizmoUpdateResult.DragBegan);
        // The mesh is one world unit across, so +1 is exactly a doubling.
        harness.DragTo(pivot + geometry.AxisX * (geometry.AxisLength + 1f));
        harness.Release().ShouldBe(GizmoUpdateResult.DragCommitted);

        mesh.LocalScale.X.ShouldBe(2f, 1e-2f);
        group.LocalScale.ShouldBe(Vector3.One);
        ShouldBeRigid(child.WorldMatrix);

        var renderer = new CompilingRenderer();
        harness.Scene.RebuildStaticWorld(renderer);
        harness.Scene.StaticWorld.ShouldNotBeNull();
    }

    // The same definition Scene enforces: an affine matrix whose basis is
    // orthonormal and positively oriented.
    private static void ShouldBeRigid(Matrix4x4 m)
    {
        var x = new Vector3(m.M11, m.M12, m.M13);
        var y = new Vector3(m.M21, m.M22, m.M23);
        var z = new Vector3(m.M31, m.M32, m.M33);

        const float tolerance = 1e-4f;
        x.Length().ShouldBe(1f, tolerance);
        y.Length().ShouldBe(1f, tolerance);
        z.Length().ShouldBe(1f, tolerance);

        Vector3.Dot(x, y).ShouldBe(0f, tolerance);
        Vector3.Dot(y, z).ShouldBe(0f, tolerance);
        Vector3.Dot(z, x).ShouldBe(0f, tolerance);

        Vector3.Dot(Vector3.Cross(x, y), z).ShouldBe(1f, tolerance); // right-handed, not mirrored
    }
}
