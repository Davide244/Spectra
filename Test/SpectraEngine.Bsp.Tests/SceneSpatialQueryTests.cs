using System.Numerics;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;
using static SpectraEngine.Bsp.Tests.SpatialTestHelpers;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The scene-level spatial queries. <see cref="Scene.Raycast"/> must return
/// analytically exact hits (distance, point, world normal) and the NEAREST hit
/// under the fat-AABB ordering hazard — a far node's fat box can be entered
/// before a near node's narrow-phase hit, so pruning may only ever compare
/// against narrow-phase results. <see cref="Scene.QueryFrustum"/> must agree
/// exactly with brute force over every node's true world AABB: the fat boxes
/// inside the tree are allowed to cost extra visits, never to change results.
/// </summary>
public sealed class SceneSpatialQueryTests
{
    private const float Tolerance = 1e-4f;

    // --- Raycast: exact hits -------------------------------------------------

    [Fact]
    public void Raycast_hits_a_mesh_cube_at_the_exact_analytic_distance()
    {
        var scene = new Scene("Test");
        SceneNode cube = CreateMeshNode(scene.Root, "cube", new Vector3(10f, 0f, 0f));

        var ray = new Ray3(Vector3.Zero, Vector3.UnitX);
        scene.Raycast(ray, out SceneRaycastHit hit).ShouldBeTrue();

        hit.Node.ShouldBeSameAs(cube);
        hit.Distance.ShouldBe(9.5f, Tolerance);
        hit.Point.X.ShouldBe(9.5f, Tolerance);
        hit.Point.Y.ShouldBe(0f, Tolerance);
        hit.Point.Z.ShouldBe(0f, Tolerance);
        hit.Normal.X.ShouldBe(-1f, Tolerance);
        hit.Normal.Y.ShouldBe(0f, Tolerance);
        hit.Normal.Z.ShouldBe(0f, Tolerance);
    }

    [Fact]
    public void Raycast_hits_a_brush_box_at_the_exact_analytic_distance()
    {
        var scene = new Scene("Test");
        SceneNode wall = CreateBrushNode(scene.Root, "wall", new Vector3(10f, 0f, 0f));

        var ray = new Ray3(Vector3.Zero, Vector3.UnitX);
        scene.Raycast(ray, out SceneRaycastHit hit).ShouldBeTrue();

        hit.Node.ShouldBeSameAs(wall);
        hit.Distance.ShouldBe(9.5f, Tolerance);
        hit.Normal.X.ShouldBe(-1f, Tolerance);
        hit.Normal.Y.ShouldBe(0f, Tolerance);
        hit.Normal.Z.ShouldBe(0f, Tolerance);
    }

    [Fact]
    public void Raycast_respects_max_distance()
    {
        var scene = new Scene("Test");
        CreateMeshNode(scene.Root, "cube", new Vector3(10f, 0f, 0f));

        var ray = new Ray3(Vector3.Zero, Vector3.UnitX);
        scene.Raycast(ray, out _, maxDistance: 5f).ShouldBeFalse();
        scene.Raycast(ray, out _, maxDistance: 20f).ShouldBeTrue();
    }

    [Fact]
    public void Raycast_misses_cleanly()
    {
        var scene = new Scene("Test");
        CreateMeshNode(scene.Root, "cube", new Vector3(10f, 0f, 0f));
        CreateBrushNode(scene.Root, "wall", new Vector3(0f, 10f, 0f));

        // Away from everything, and squeezed between the two nodes.
        scene.Raycast(new Ray3(Vector3.Zero, -Vector3.UnitX), out _).ShouldBeFalse();
        scene.Raycast(new Ray3(Vector3.Zero, Vector3.UnitZ), out _).ShouldBeFalse();
    }

    [Fact]
    public void Raycast_picks_the_nearest_of_two_aligned_nodes_despite_fat_box_ordering()
    {
        var scene = new Scene("Test");
        // The far node is inserted FIRST, and its fat box (min x = 10.1 - 0.2
        // = 9.9) is entered before the near node's narrow-phase hit at x = 10:
        // an implementation that stops at the first leaf hit, or prunes boxes
        // against box-entry distances instead of narrow-phase distances, picks
        // the wrong node here.
        SceneNode far = CreateMeshNode(scene.Root, "far", new Vector3(10.6f, 0f, 0f));
        SceneNode near = CreateMeshNode(scene.Root, "near", new Vector3(10.5f, 0f, 0f));

        var ray = new Ray3(Vector3.Zero, Vector3.UnitX);
        scene.Raycast(ray, out SceneRaycastHit hit).ShouldBeTrue();

        hit.Node.ShouldBeSameAs(near);
        far.ShouldNotBeSameAs(hit.Node);
        hit.Distance.ShouldBe(10f, Tolerance);
    }

    [Fact]
    public void Raycast_reports_the_rotated_brush_normal_in_world_space()
    {
        // A 2x2x2 brush rotated 45 degrees about Y. The +X local face's plane
        // becomes n = (cos45, 0, -sin45), n.p = 1 in world space; a -X ray
        // from (5, 0, -0.3) enters through that face at t = 5.3 - sqrt(2).
        var scene = new Scene("Test");
        SceneNode node = scene.Root.CreateChild("rotated");
        node.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f);
        node.Brush = Brush.CreateBox(new Vector3(-1f), new Vector3(1f));

        var ray = new Ray3(new Vector3(5f, 0f, -0.3f), -Vector3.UnitX);
        scene.Raycast(ray, out SceneRaycastHit hit).ShouldBeTrue();

        float sqrt2Over2 = MathF.Sqrt(2f) / 2f;
        hit.Node.ShouldBeSameAs(node);
        hit.Distance.ShouldBe(5.3f - MathF.Sqrt(2f), Tolerance);
        hit.Normal.X.ShouldBe(sqrt2Over2, Tolerance);
        hit.Normal.Y.ShouldBe(0f, Tolerance);
        hit.Normal.Z.ShouldBe(-sqrt2Over2, Tolerance);
    }

    [Fact]
    public void Raycast_handles_scaled_mesh_nodes()
    {
        // Mesh nodes may scale (only brush placements must stay rigid): a unit
        // cube scaled by 2 spans [-1, 1], so a -X ray from x = 5 hits at t = 4
        // and the normal must survive the non-rigid inverse transform.
        var scene = new Scene("Test");
        SceneNode node = CreateMeshNode(scene.Root, "scaled", Vector3.Zero);
        node.LocalScale = new Vector3(2f);

        var ray = new Ray3(new Vector3(5f, 0f, 0f), -Vector3.UnitX);
        scene.Raycast(ray, out SceneRaycastHit hit).ShouldBeTrue();

        hit.Node.ShouldBeSameAs(node);
        hit.Distance.ShouldBe(4f, Tolerance);
        hit.Normal.X.ShouldBe(1f, Tolerance);
        hit.Normal.Y.ShouldBe(0f, Tolerance);
        hit.Normal.Z.ShouldBe(0f, Tolerance);
    }

    [Fact]
    public void Raycast_handles_scaled_brush_nodes()
    {
        // The graph permits scale on brush nodes (only the static-world
        // snapshot rejects it, and the scene stays live when it does), so
        // picking must stay exact under it: a half-extent-0.5 box brush scaled
        // by 2 spans [-1, 1], so a -X ray from x = 5 hits at t = 4 — a rigid
        // inverse shortcut would report t = 4.5 with a point inside the solid
        // and a normal of length 2.
        var scene = new Scene("Test");
        SceneNode node = CreateBrushNode(scene.Root, "scaled", Vector3.Zero);
        node.LocalScale = new Vector3(2f);

        var ray = new Ray3(new Vector3(5f, 0f, 0f), -Vector3.UnitX);
        scene.Raycast(ray, out SceneRaycastHit hit).ShouldBeTrue();

        hit.Node.ShouldBeSameAs(node);
        hit.Distance.ShouldBe(4f, Tolerance);
        hit.Point.X.ShouldBe(1f, Tolerance);
        hit.Normal.Length().ShouldBe(1f, Tolerance);
        hit.Normal.X.ShouldBe(1f, Tolerance);
        hit.Normal.Y.ShouldBe(0f, Tolerance);
        hit.Normal.Z.ShouldBe(0f, Tolerance);
    }

    [Fact]
    public void Raycast_falls_back_to_the_unit_box_for_meshes_without_cpu_geometry()
    {
        // A FakeMesh built with no vertex data has no CPU positions, so the
        // documented fallback applies: a unit box around the node origin.
        var scene = new Scene("Test");
        SceneNode node = scene.Root.CreateChild("gpu-only");
        node.LocalPosition = new Vector3(3f, 0f, 0f);
        node.MeshRenderer = new MeshRenderer(new FakeMesh([], []), NoopMaterial);

        var ray = new Ray3(Vector3.Zero, Vector3.UnitX);
        scene.Raycast(ray, out SceneRaycastHit hit).ShouldBeTrue();

        hit.Node.ShouldBeSameAs(node);
        hit.Distance.ShouldBe(2.5f, Tolerance);
        hit.Normal.X.ShouldBe(-1f, Tolerance);
    }

    [Fact]
    public void Raycast_on_an_empty_scene_returns_false()
    {
        var scene = new Scene("Test");
        scene.Raycast(new Ray3(Vector3.Zero, Vector3.UnitX), out _).ShouldBeFalse();
    }

    // --- QueryFrustum: brute-force oracle ------------------------------------

    [Fact]
    public void QueryFrustum_matches_brute_force_over_a_grid_for_several_camera_poses()
    {
        var scene = new Scene("Test");
        var allNodes = new List<SceneNode>();
        int i = 0;
        for (int x = -2; x <= 2; x++)
        for (int y = -2; y <= 2; y++)
        for (int z = -2; z <= 2; z++)
        {
            var position = new Vector3(x * 4f, y * 4f, z * 4f);
            allNodes.Add(i++ % 2 == 0
                ? CreateMeshNode(scene.Root, $"m{i}", position)
                : CreateBrushNode(scene.Root, $"b{i}", position));
        }

        var results = new List<SceneNode>();
        bool sawPartialView = false;
        foreach (Camera camera in CameraPoses())
        {
            Frustum frustum = camera.GetFrustum();

            var expected = allNodes.Where(n => frustum.Intersects(WorldBoundsOf(n))).ToHashSet();
            // Every pose sees something; the partial-view check below (across
            // all poses) keeps the comparison from passing vacuously.
            expected.ShouldNotBeEmpty($"pose at {camera.Position} sees nothing — bad test setup");
            if (expected.Count < allNodes.Count)
                sawPartialView = true;

            results.Clear();
            scene.QueryFrustum(frustum, results);

            results.Count.ShouldBe(expected.Count,
                $"pose at {camera.Position}: BVH count differs from brute force");
            results.ToHashSet().SetEquals(expected).ShouldBeTrue(
                $"pose at {camera.Position}: BVH set differs from brute force");
        }

        sawPartialView.ShouldBeTrue("every pose saw the whole grid — the oracle comparison proved nothing");
    }

    [Fact]
    public void QueryFrustum_appends_without_clearing_the_callers_list()
    {
        var scene = new Scene("Test");
        CreateMeshNode(scene.Root, "cube", Vector3.Zero);
        Camera camera = new() { Position = new Vector3(0f, 0f, 10f) };

        var results = new List<SceneNode>();
        scene.QueryFrustum(camera.GetFrustum(), results);
        scene.QueryFrustum(camera.GetFrustum(), results);

        results.Count.ShouldBe(2); // contract: caller owns clearing
    }

    [Fact]
    public void QueryFrustum_stays_correct_while_nodes_move()
    {
        var scene = new Scene("Test");
        SceneNode mover = CreateMeshNode(scene.Root, "mover", new Vector3(0f, 0f, -5f));
        Camera camera = new() { Position = new Vector3(0f, 0f, 10f) }; // looking down -Z
        Frustum frustum = camera.GetFrustum();

        var results = new List<SceneNode>();
        scene.QueryFrustum(frustum, results);
        results.ShouldBe(new[] { mover });

        // Jump far behind the camera — the stale fat box must not keep it visible.
        mover.LocalPosition = new Vector3(0f, 0f, 500f);
        results.Clear();
        scene.QueryFrustum(frustum, results);
        results.ShouldBeEmpty();

        // And back into view.
        mover.LocalPosition = new Vector3(2f, 1f, -20f);
        results.Clear();
        scene.QueryFrustum(frustum, results);
        results.ShouldBe(new[] { mover });
    }

    // A spread of poses around/inside the grid: axis-aligned from two sides, a
    // diagonal look-at, a tilted close-up, and a narrow-FOV far view.
    private static IEnumerable<Camera> CameraPoses()
    {
        yield return new Camera { Position = new Vector3(0f, 0f, 30f), Yaw = -MathF.PI / 2f };
        yield return new Camera { Position = new Vector3(30f, 6f, 0f), Yaw = MathF.PI };
        var lookAt = new Camera { Position = new Vector3(25f, 25f, 25f) };
        lookAt.LookAt(Vector3.Zero);
        yield return lookAt;
        yield return new Camera { Position = new Vector3(3f, 2f, 12f), Yaw = -MathF.PI / 2f, Pitch = -0.4f };
        yield return new Camera
        {
            Position = new Vector3(0f, 0f, 60f),
            Yaw = -MathF.PI / 2f,
            FieldOfView = MathF.PI / 12f,
        };
    }
}
