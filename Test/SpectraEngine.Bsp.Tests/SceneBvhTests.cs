using System.Numerics;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;
using static SpectraEngine.Bsp.Tests.SpatialTestHelpers;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// Structural correctness of the scene's dynamic AABB tree
/// (<see cref="SceneBvh"/>): leaves must track exactly the spatial nodes
/// (mesh or brush) through every add/remove/component-edit/reparent path, the
/// tree invariants (link consistency, parent boxes containing children, fresh
/// tight boxes) must survive arbitrary edit sequences, and steady-state
/// queries must not allocate. <see cref="SceneBvh.Validate"/> is the invariant
/// oracle — it recomputes every leaf's bounds independently and walks all
/// links.
/// </summary>
public sealed class SceneBvhTests
{
    // --- Leaf tracking -------------------------------------------------------

    [Fact]
    public void Only_spatial_nodes_are_indexed()
    {
        var scene = new Scene("Test");
        scene.Root.CreateChild("camera rig").CreateChild("empty group");
        scene.Bvh.LeafCount.ShouldBe(0);

        CreateMeshNode(scene.Root, "mesh", new Vector3(1f, 0f, 0f));
        CreateBrushNode(scene.Root, "brush", new Vector3(-1f, 0f, 0f));

        scene.Bvh.LeafCount.ShouldBe(2);
        scene.Bvh.Validate();
    }

    [Fact]
    public void Assigning_and_clearing_a_mesh_renderer_inserts_and_removes_the_leaf()
    {
        var scene = new Scene("Test");
        SceneNode node = scene.Root.CreateChild("prop");
        scene.Bvh.LeafCount.ShouldBe(0);

        node.MeshRenderer = new MeshRenderer(CreateCubeMesh(0.5f), NoopMaterial);
        scene.Bvh.LeafCount.ShouldBe(1);
        scene.Bvh.Validate();

        node.MeshRenderer = null;
        scene.Bvh.LeafCount.ShouldBe(0);
        scene.Bvh.Validate();
    }

    [Fact]
    public void Assigning_and_clearing_a_brush_inserts_and_removes_the_leaf()
    {
        var scene = new Scene("Test");
        SceneNode node = scene.Root.CreateChild("wall");

        node.Brush = Brush.CreateBox(new Vector3(-1f), new Vector3(1f));
        scene.Bvh.LeafCount.ShouldBe(1);
        scene.Bvh.Validate();

        node.Brush = null;
        scene.Bvh.LeafCount.ShouldBe(0);
        scene.Bvh.Validate();
    }

    [Fact]
    public void Replacing_a_brush_refits_the_leaf_to_the_new_bounds()
    {
        var scene = new Scene("Test");
        SceneNode node = CreateBrushNode(scene.Root, "wall", Vector3.Zero, half: 0.5f);

        // Much larger than the old fat box — must force a re-insert, and
        // Validate recomputes bounds independently, catching a stale box.
        node.Brush = Brush.CreateBox(new Vector3(-5f), new Vector3(5f));

        scene.Bvh.LeafCount.ShouldBe(1);
        scene.Bvh.Validate();
    }

    [Fact]
    public void A_node_with_both_components_keeps_its_leaf_until_both_are_cleared()
    {
        var scene = new Scene("Test");
        SceneNode node = CreateBrushNode(scene.Root, "hybrid", Vector3.Zero);
        node.MeshRenderer = new MeshRenderer(CreateCubeMesh(2f), NoopMaterial);
        scene.Bvh.LeafCount.ShouldBe(1);
        scene.Bvh.Validate();

        node.Brush = null;
        scene.Bvh.LeafCount.ShouldBe(1);
        scene.Bvh.Validate();

        node.MeshRenderer = null;
        scene.Bvh.LeafCount.ShouldBe(0);
        scene.Bvh.Validate();
    }

    [Fact]
    public void Removing_a_subtree_removes_all_of_its_leaves()
    {
        var scene = new Scene("Test");
        SceneNode group = scene.Root.CreateChild("group");
        CreateMeshNode(group, "a", new Vector3(1f, 0f, 0f));
        CreateBrushNode(group, "b", new Vector3(2f, 0f, 0f));
        CreateMeshNode(scene.Root, "stays", new Vector3(-3f, 0f, 0f));
        scene.Bvh.LeafCount.ShouldBe(3);

        scene.Root.RemoveChild(group);

        scene.Bvh.LeafCount.ShouldBe(1);
        scene.Bvh.Validate();
    }

    [Fact]
    public void Attaching_a_prebuilt_subtree_indexes_every_spatial_node_in_it()
    {
        // Built detached — nothing indexed anywhere yet.
        var group = new SceneNode("group");
        SceneNode meshChild = group.CreateChild("mesh");
        meshChild.LocalPosition = new Vector3(4f, 0f, 0f);
        meshChild.MeshRenderer = new MeshRenderer(CreateCubeMesh(0.5f), NoopMaterial);
        SceneNode brushChild = group.CreateChild("brush");
        brushChild.LocalPosition = new Vector3(-4f, 0f, 0f);
        brushChild.Brush = Brush.CreateBox(new Vector3(-0.5f), new Vector3(0.5f));

        var scene = new Scene("Test");
        scene.Root.AddChild(group);

        scene.Bvh.LeafCount.ShouldBe(2);
        scene.Bvh.Validate();
    }

    // --- Movement and refit --------------------------------------------------

    [Fact]
    public void Small_and_large_moves_both_keep_the_tree_valid()
    {
        var scene = new Scene("Test");
        SceneNode node = CreateMeshNode(scene.Root, "mover", Vector3.Zero);
        CreateMeshNode(scene.Root, "anchor", new Vector3(10f, 0f, 0f));
        scene.Bvh.Validate();

        // Within the 0.2 fat margin: only the cached tight box may change.
        node.LocalPosition = new Vector3(0.05f, 0f, 0f);
        scene.Bvh.Validate();

        // Far outside the fat box: the leaf must be re-inserted.
        node.LocalPosition = new Vector3(-25f, 3f, 7f);
        scene.Bvh.Validate();
        scene.Bvh.LeafCount.ShouldBe(2);
    }

    [Fact]
    public void Moving_a_group_refits_all_spatial_descendants()
    {
        var scene = new Scene("Test");
        SceneNode group = scene.Root.CreateChild("group");
        SceneNode a = CreateMeshNode(group, "a", new Vector3(1f, 0f, 0f));
        CreateBrushNode(group, "b", new Vector3(-1f, 0f, 0f));

        group.LocalPosition = new Vector3(0f, 50f, 0f);

        // Validate recomputes world bounds from the (new) world matrices, so a
        // descendant leaf left at the old position would fail here.
        scene.Bvh.Validate();

        // And the moved geometry is actually found at its new place.
        var ray = new Ray3(new Vector3(1f, 60f, 0f), new Vector3(0f, -1f, 0f));
        scene.Raycast(ray, out SceneRaycastHit hit).ShouldBeTrue();
        hit.Node.ShouldBeSameAs(a);
        hit.Distance.ShouldBe(9.5f, 1e-4f);
    }

    [Fact]
    public void Reparenting_within_the_scene_refits_the_moved_subtree()
    {
        var scene = new Scene("Test");
        SceneNode offsetParent = scene.Root.CreateChild("offset");
        offsetParent.LocalPosition = new Vector3(100f, 0f, 0f);
        SceneNode node = CreateMeshNode(scene.Root, "mover", Vector3.Zero);
        scene.Bvh.Validate();

        // No membership events fire for this — the subtree-moved hook must
        // cover it, or the leaf silently keeps its old world box.
        offsetParent.AddChild(node);

        scene.Bvh.Validate();
        var ray = new Ray3(new Vector3(100f, 10f, 0f), new Vector3(0f, -1f, 0f));
        scene.Raycast(ray, out SceneRaycastHit hit).ShouldBeTrue();
        hit.Node.ShouldBeSameAs(node);
        hit.Distance.ShouldBe(9.5f, 1e-4f);
    }

    [Fact]
    public void Cross_scene_moves_reindex_the_subtree_in_the_destination_scene()
    {
        var sceneA = new Scene("A");
        var sceneB = new Scene("B");
        SceneNode node = CreateMeshNode(sceneA.Root, "traveller", new Vector3(5f, 0f, 0f));
        sceneA.Bvh.LeafCount.ShouldBe(1);

        sceneB.Root.AddChild(node);

        sceneA.Bvh.LeafCount.ShouldBe(0);
        sceneB.Bvh.LeafCount.ShouldBe(1);
        sceneA.Bvh.Validate();
        sceneB.Bvh.Validate();
    }

    // --- Cached world bounds -------------------------------------------------

    [Fact]
    public void TryGetWorldBounds_matches_the_independent_bounds_oracle()
    {
        var scene = new Scene("Test");
        SceneNode mesh = CreateMeshNode(scene.Root, "mesh", new Vector3(3f, 1f, -2f));
        SceneNode brush = CreateBrushNode(scene.Root, "brush", new Vector3(-4f, 0f, 5f), half: 1.5f);

        scene.Bvh.TryGetWorldBounds(mesh, out Aabb meshBounds).ShouldBeTrue();
        scene.Bvh.TryGetWorldBounds(brush, out Aabb brushBounds).ShouldBeTrue();

        // Same computation as the index's maintenance path — exact equality.
        meshBounds.Min.ShouldBe(WorldBoundsOf(mesh).Min);
        meshBounds.Max.ShouldBe(WorldBoundsOf(mesh).Max);
        brushBounds.Min.ShouldBe(WorldBoundsOf(brush).Min);
        brushBounds.Max.ShouldBe(WorldBoundsOf(brush).Max);
    }

    [Fact]
    public void TryGetWorldBounds_reflects_a_move_made_since_the_last_query()
    {
        var scene = new Scene("Test");
        SceneNode node = CreateMeshNode(scene.Root, "mover", Vector3.Zero);

        // The move only marks the leaf dirty; the accessor must flush the
        // pending refit itself or it would hand back the pre-move box.
        node.LocalPosition = new Vector3(0f, 25f, 0f);

        scene.Bvh.TryGetWorldBounds(node, out Aabb bounds).ShouldBeTrue();
        bounds.Min.ShouldBe(WorldBoundsOf(node).Min);
        bounds.Max.ShouldBe(WorldBoundsOf(node).Max);
    }

    [Fact]
    public void TryGetWorldBounds_is_false_for_non_spatial_and_foreign_nodes()
    {
        var scene = new Scene("Test");
        SceneNode group = scene.Root.CreateChild("group");

        var otherScene = new Scene("Other");
        SceneNode foreign = CreateMeshNode(otherScene.Root, "foreign", Vector3.Zero);

        scene.Bvh.TryGetWorldBounds(group, out _).ShouldBeFalse();
        scene.Bvh.TryGetWorldBounds(foreign, out _).ShouldBeFalse();
    }

    // --- Edit-sequence stress ------------------------------------------------

    [Fact]
    public void Random_edit_sequences_preserve_invariants_and_query_results()
    {
        var scene = new Scene("Test");
        var random = new Random(20260820); // deterministic
        var spatialNodes = new List<SceneNode>();
        var results = new List<SceneNode>();
        Frustum frustum = MakeCamera(new Vector3(0f, 5f, 40f), yaw: -MathF.PI / 2f).GetFrustum();

        for (int step = 0; step < 300; step++)
        {
            int op = random.Next(10);
            if (op < 4 || spatialNodes.Count == 0)
            {
                // Insert a mesh or brush node at a random position.
                var position = new Vector3(
                    random.Next(-30, 31), random.Next(-30, 31), random.Next(-30, 31));
                SceneNode node = random.Next(2) == 0
                    ? CreateMeshNode(scene.Root, $"mesh{step}", position)
                    : CreateBrushNode(scene.Root, $"brush{step}", position);
                spatialNodes.Add(node);
            }
            else if (op < 8)
            {
                // Move one — sometimes a nudge inside the fat margin,
                // sometimes a jump that forces re-insertion.
                SceneNode node = spatialNodes[random.Next(spatialNodes.Count)];
                Vector3 delta = random.Next(2) == 0
                    ? new Vector3(0.05f, -0.03f, 0.08f)
                    : new Vector3(random.Next(-20, 21), random.Next(-20, 21), random.Next(-20, 21));
                node.LocalPosition += delta;
            }
            else
            {
                // Remove one.
                int victim = random.Next(spatialNodes.Count);
                SceneNode node = spatialNodes[victim];
                spatialNodes.RemoveAt(victim);
                node.Parent!.RemoveChild(node);
            }

            scene.Bvh.Validate();
            scene.Bvh.LeafCount.ShouldBe(spatialNodes.Count);

            if (step % 25 == 24)
            {
                // Query results must match brute force over the live nodes.
                results.Clear();
                scene.QueryFrustum(frustum, results);
                var expected = spatialNodes.Where(n => frustum.Intersects(WorldBoundsOf(n))).ToHashSet();
                results.ToHashSet().SetEquals(expected).ShouldBeTrue(
                    $"step {step}: BVH found {results.Count} nodes, brute force {expected.Count}");
            }
        }
    }

    // --- Allocation discipline ----------------------------------------------

    [Fact]
    public void Raycast_and_QueryFrustum_are_allocation_free_after_warmup()
    {
        var scene = new Scene("Test");
        for (int x = 0; x < 6; x++)
        for (int z = 0; z < 6; z++)
        {
            var position = new Vector3(x * 4f - 10f, 0f, z * 4f - 10f);
            if ((x + z) % 2 == 0)
                CreateMeshNode(scene.Root, $"m{x}_{z}", position);
            else
                CreateBrushNode(scene.Root, $"b{x}_{z}", position);
        }

        Frustum frustum = MakeCamera(new Vector3(0f, 3f, 30f), yaw: -MathF.PI / 2f).GetFrustum();
        var ray = new Ray3(new Vector3(-10f, 0f, -30f), Vector3.Normalize(new Vector3(0.3f, 0f, 1f)));
        var results = new List<SceneNode>(64);

        // Warmup: grows the traversal stack and the results list, tiers up the
        // query paths, and flushes any pending refits.
        for (int i = 0; i < 50; i++)
        {
            scene.Raycast(ray, out _);
            results.Clear();
            scene.QueryFrustum(frustum, results);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            scene.Raycast(ray, out _);
            results.Clear();
            scene.QueryFrustum(frustum, results);
        }
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        delta.ShouldBe(0L);
    }

    private static Camera MakeCamera(Vector3 position, float yaw) => new()
    {
        Position = position,
        Yaw = yaw,
        Pitch = -0.1f,
        AspectRatio = 16f / 9f,
    };
}
