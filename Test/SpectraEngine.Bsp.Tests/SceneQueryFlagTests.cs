using System.Collections.Generic;
using System.Numerics;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The query half of the physics arc: per-node <see cref="PhysicsFlags"/>,
/// the collision-group registry, and the BVH's box and sphere overlap queries.
/// No physics engine is involved — these are the filters and the broad phase a
/// solver will later consume, and they are useful on their own (triggers, area
/// effects, editor tooling).
/// </summary>
public sealed class SceneQueryFlagTests
{
    // --- Flags --------------------------------------------------------------

    [Fact]
    public void A_node_starts_solid_queryable_touchable_and_anchored()
    {
        var node = new SceneNode("n");

        node.CanCollide.ShouldBeTrue();
        node.CanQuery.ShouldBeTrue();
        node.CanTouch.ShouldBeTrue();
        node.Anchored.ShouldBeTrue("nothing simulates until asked — deliberately unlike a Roblox Part");
        node.PhysicsFlags.HasFlag(PhysicsFlags.HasBody).ShouldBeFalse();
    }

    [Fact]
    public void CanQuery_is_independent_of_CanCollide()
    {
        // The deliberate divergence. Roblox requires CanCollide off before
        // CanQuery takes effect, so "a solid wall a targeting ray passes
        // through" is inexpressible there.
        var node = new SceneNode("n") { CanQuery = false };

        node.CanCollide.ShouldBeTrue();
        node.CanQuery.ShouldBeFalse();
    }

    [Fact]
    public void Flag_writes_do_not_dirty_the_static_world()
    {
        // None of these bits changes the compiled world, so a script toggling
        // CanCollide on a world brush must cost nothing.
        var scene = new Scene("Test");
        SceneNode brush = scene.Root.CreateChild("wall");
        brush.Brush = UnitBrush();
        scene.RebuildStaticWorld(new FakeRenderer());
        scene.StaticWorldDirty.ShouldBeFalse();

        brush.CanCollide = false;
        brush.CanQuery = false;
        brush.CanTouch = false;
        brush.Anchored = false;
        brush.CollisionGroup = 3;

        scene.StaticWorldDirty.ShouldBeFalse();
    }

    // --- Raycast filtering --------------------------------------------------

    [Fact]
    public void A_raycast_skips_a_node_that_cannot_be_queried()
    {
        var (scene, near, far) = TwoBrushesInALine();

        near.CanQuery = false;

        scene.Raycast(RayAlongX(), out SceneRaycastHit hit).ShouldBeTrue();
        hit.Node.ShouldBeSameAs(far, "the near brush opted out of queries");
    }

    [Fact]
    public void CanQuery_is_honoured_on_a_static_WORLD_brush()
    {
        // The design once specified refusing the flag on world brushes, on the
        // premise that their queries route through the compiled per-cell BSP.
        // They do not: Scene.Raycast traverses the spatial index per node, so
        // the flag is one bit test and is honoured for every kind of node.
        var (scene, near, far) = TwoBrushesInALine();
        near.BrushKind.ShouldBe(BrushKind.World);

        near.CanQuery = false;

        scene.Raycast(RayAlongX(), out SceneRaycastHit hit).ShouldBeTrue();
        hit.Node.ShouldBeSameAs(far);
    }

    [Fact]
    public void Editor_picking_disregards_the_query_flags()
    {
        // A designer must be able to select what they can see; otherwise
        // clearing CanQuery hides its own undo.
        var (scene, near, _) = TwoBrushesInALine();
        near.CanQuery = false;

        scene.Raycast(RayAlongX(), out SceneRaycastHit hit, SceneQueryFilter.EditorPicking).ShouldBeTrue();

        hit.Node.ShouldBeSameAs(near);
    }

    [Fact]
    public void RespectCanCollide_reproduces_the_Roblox_coupling_on_request()
    {
        var (scene, near, far) = TwoBrushesInALine();
        near.CanCollide = false;   // still queryable by default here

        scene.Raycast(RayAlongX(), out SceneRaycastHit unfiltered).ShouldBeTrue();
        unfiltered.Node.ShouldBeSameAs(near, "the two flags are independent by default");

        var roblox = new SceneQueryFilter { RespectCanCollide = true };
        scene.Raycast(RayAlongX(), out SceneRaycastHit filtered, roblox).ShouldBeTrue();
        filtered.Node.ShouldBeSameAs(far);
    }

    [Fact]
    public void An_ignore_list_excludes_the_caster()
    {
        var (scene, near, far) = TwoBrushesInALine();

        var filter = new SceneQueryFilter { Ignore = new[] { near } };
        scene.Raycast(RayAlongX(), out SceneRaycastHit hit, filter).ShouldBeTrue();

        hit.Node.ShouldBeSameAs(far);
    }

    // --- Collision groups ---------------------------------------------------

    [Fact]
    public void Everything_collides_until_a_pair_is_disabled()
    {
        var groups = new CollisionGroups();
        int players = groups.Register("Players");
        int scenery = groups.Register("Scenery");

        groups.AreCollidable(players, scenery).ShouldBeTrue();
        groups.AreCollidable(CollisionGroups.DefaultGroup, players).ShouldBeTrue();
    }

    [Fact]
    public void Disabling_a_pair_is_symmetric()
    {
        // A matrix that can disagree with itself is a bug whose only symptom is
        // objects passing through each other from one side.
        var groups = new CollisionGroups();
        int a = groups.Register("A");
        int b = groups.Register("B");

        groups.SetCollidable(a, b, false);

        groups.AreCollidable(a, b).ShouldBeFalse();
        groups.AreCollidable(b, a).ShouldBeFalse();
        (groups.GetMask(a) & (1UL << b)).ShouldBe(0UL);
        (groups.GetMask(b) & (1UL << a)).ShouldBe(0UL);
    }

    [Fact]
    public void Registering_the_same_name_twice_returns_the_same_id()
    {
        var groups = new CollisionGroups();

        groups.Register("Players").ShouldBe(groups.Register("Players"));
        groups.Count.ShouldBe(2, "Default plus Players");
    }

    [Fact]
    public void The_sixty_fifth_group_is_a_named_error()
    {
        // Running out of a fixed resource must be reported at the moment it
        // happens; silently reusing a group means geometry collides with
        // things it was configured not to.
        var groups = new CollisionGroups();
        for (int i = 1; i < CollisionGroups.MaxGroups; i++)
            groups.Register($"G{i}");
        groups.Count.ShouldBe(CollisionGroups.MaxGroups);

        Should.Throw<InvalidOperationException>(() => groups.Register("OneTooMany"))
            .Message.ShouldContain("OneTooMany");
    }

    [Fact]
    public void A_query_can_filter_by_collision_group()
    {
        var (scene, near, far) = TwoBrushesInALine();
        int rays = scene.CollisionGroups.Register("Rays");
        int transparent = scene.CollisionGroups.Register("Transparent");
        scene.CollisionGroups.SetCollidable(rays, transparent, false);
        near.CollisionGroup = transparent;

        var filter = new SceneQueryFilter { Groups = scene.CollisionGroups, CollisionGroup = rays };
        scene.Raycast(RayAlongX(), out SceneRaycastHit hit, filter).ShouldBeTrue();

        hit.Node.ShouldBeSameAs(far);
    }

    // --- Overlap queries ----------------------------------------------------

    [Fact]
    public void A_box_query_finds_the_nodes_whose_bounds_it_overlaps()
    {
        var (scene, near, far) = TwoBrushesInALine();
        var results = new List<SceneNode>();

        scene.GetPartBoundsInBox(new Aabb(new Vector3(-2f, -2f, -2f), new Vector3(2f, 2f, 2f)), results);

        results.ShouldContain(near);
        results.ShouldNotContain(far);
    }

    [Fact]
    public void A_box_query_that_overlaps_nothing_reports_nothing()
    {
        var (scene, _, _) = TwoBrushesInALine();
        var results = new List<SceneNode>();

        scene.GetPartBoundsInBox(
            new Aabb(new Vector3(100f, 100f, 100f), new Vector3(101f, 101f, 101f)), results);

        results.ShouldBeEmpty();
    }

    [Fact]
    public void A_sphere_query_finds_the_nodes_it_reaches_and_no_more()
    {
        var (scene, near, far) = TwoBrushesInALine();
        var results = new List<SceneNode>();

        // The near brush spans x in [-1,1]; the far one is centred at x=10.
        scene.GetPartBoundsInRadius(Vector3.Zero, 3f, results);

        results.ShouldContain(near);
        results.ShouldNotContain(far);

        results.Clear();
        scene.GetPartBoundsInRadius(Vector3.Zero, 20f, results);
        results.ShouldContain(far);
    }

    [Fact]
    public void A_sphere_query_measures_from_the_box_not_from_its_centre()
    {
        // The corner case a centre-distance test gets wrong: a sphere that
        // reaches the near face of a box but not its middle still overlaps it.
        var scene = new Scene("Test");
        SceneNode wide = scene.Root.CreateChild("wide");
        wide.Brush = Brush.CreateBox(new Vector3(-10f, -1f, -1f), new Vector3(10f, 1f, 1f));
        wide.LocalPosition = new Vector3(20f, 0f, 0f);   // spans x in [10, 30]
        var results = new List<SceneNode>();

        scene.GetPartBoundsInRadius(Vector3.Zero, 11f, results);

        results.ShouldContain(wide, "the sphere reaches the box's near face at x=10");
    }

    [Fact]
    public void Overlap_queries_honour_the_filter()
    {
        var (scene, near, _) = TwoBrushesInALine();
        near.CanQuery = false;
        var results = new List<SceneNode>();

        scene.GetPartBoundsInBox(
            new Aabb(new Vector3(-2f, -2f, -2f), new Vector3(2f, 2f, 2f)), results);
        results.ShouldNotContain(near);

        results.Clear();
        scene.GetPartBoundsInBox(
            new Aabb(new Vector3(-2f, -2f, -2f), new Vector3(2f, 2f, 2f)),
            results, SceneQueryFilter.EditorPicking);
        results.ShouldContain(near);
    }

    [Fact]
    public void An_overlap_query_finds_part_brushes_as_readily_as_world_ones()
    {
        // The BVH is deliberately BrushKind-blind, so a part brush is queryable
        // exactly like world geometry — which is what a trigger volume and an
        // area effect both need.
        var scene = new Scene("Test");
        SceneNode part = scene.Root.CreateChild("part");
        part.BrushKind = BrushKind.Part;
        part.Brush = UnitBrush();
        var results = new List<SceneNode>();

        scene.GetPartBoundsInRadius(Vector3.Zero, 2f, results);

        results.ShouldContain(part);
    }

    [Fact]
    public void An_overlap_query_follows_a_node_that_moves()
    {
        var scene = new Scene("Test");
        SceneNode node = scene.Root.CreateChild("mover");
        node.Brush = UnitBrush();
        var results = new List<SceneNode>();

        node.LocalPosition = new Vector3(50f, 0f, 0f);
        scene.GetPartBoundsInRadius(Vector3.Zero, 2f, results);
        results.ShouldBeEmpty("the node moved away");

        node.LocalPosition = Vector3.Zero;
        scene.GetPartBoundsInRadius(Vector3.Zero, 2f, results);
        results.ShouldContain(node);
    }

    // --- Helpers ------------------------------------------------------------

    private static Ray3 RayAlongX() => new(new Vector3(-20f, 0f, 0f), Vector3.UnitX);

    private static Brush UnitBrush() =>
        Brush.CreateBox(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f));

    // Two unit brushes on the x axis: one at the origin, one at x = 10, so a
    // ray from -x hits the near one first and the far one only if the near one
    // is filtered out.
    private static (Scene Scene, SceneNode Near, SceneNode Far) TwoBrushesInALine()
    {
        var scene = new Scene("Test");
        SceneNode near = scene.Root.CreateChild("near");
        near.Brush = UnitBrush();
        SceneNode far = scene.Root.CreateChild("far");
        far.Brush = UnitBrush();
        far.LocalPosition = new Vector3(10f, 0f, 0f);
        return (scene, near, far);
    }
}
