using System;
using System.Numerics;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The unified gameplay ray: the query that has to agree with what is drawn.
/// </summary>
/// <remarks>
/// <para>
/// Before this existed the engine had two raycasts and neither was the one a
/// game wants. <see cref="Scene.Raycast(in Ray3, out SceneRaycastHit, float)"/>
/// tests authored brush planes, so it reports solid in the middle of an open
/// doorway. <see cref="CsgWorld.Raycast"/> knows about the carve but nothing
/// about part brushes, which are deliberately absent from the compile.
/// </para>
/// <para>
/// The headline test here is the doorway. Everything a player does through an
/// opening, shooting, seeing, throwing, is wrong if that answer is wrong, and it
/// is wrong in a way that looks like a bug in the gun rather than in the query.
/// </para>
/// </remarks>
public sealed class GameplayRaycastTests
{
    [Fact]
    public void A_shot_through_a_carved_doorway_passes_through()
    {
        World world = Room();

        // Down the middle of the opening, from inside the room, outward.
        var ray = new Ray3(new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, -1f));

        Assert.False(world.Scene.RaycastGameplay(in ray, out GameplayRayHit hit, 8f),
            $"the ray should have passed through the doorway, but stopped at {hit.Point} " +
            $"(static world: {hit.StaticWorld})");
    }

    [Fact]
    public void The_authored_geometry_raycast_still_reports_the_doorway_as_solid()
    {
        // Not a bug, and worth pinning so nobody "fixes" it: Scene.Raycast is
        // authored-geometry authority. It answers about the brush somebody drew,
        // which is what an editor pick needs. The divergence between the two is
        // the entire reason the gameplay query had to be a separate entry point.
        World world = Room();
        var ray = new Ray3(new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, -1f));

        Assert.True(world.Scene.Raycast(in ray, out SceneRaycastHit hit, 8f));
        Assert.Equal("wall", hit.Node.Name);
    }

    [Fact]
    public void A_shot_beside_the_doorway_stops_at_the_wall()
    {
        World world = Room();

        // Same wall, offset sideways past the opening's edge.
        var ray = new Ray3(new Vector3(2f, 1f, 0f), new Vector3(0f, 0f, -1f));

        Assert.True(world.Scene.RaycastGameplay(in ray, out GameplayRayHit hit, 8f));
        Assert.True(hit.StaticWorld, "the wall is world geometry");
        Assert.Null(hit.Node);
        Assert.Equal(-4f, hit.Point.Z, 1);
        Assert.True(hit.Normal.Z > 0.9f, $"the wall's near face should face +z, got {hit.Normal}");
    }

    [Fact]
    public void A_world_hit_reports_the_material_of_the_face_it_struck()
    {
        // The reason the query resolves a surface at all: footstep sounds,
        // impact decals and surface properties all key off this. The BSP reports
        // the plane it crossed and not the polygon on it, so a regression here
        // is a silent fallback to the default material rather than a crash.
        World world = Room();
        var ray = new Ray3(new Vector3(2f, 1f, 0f), new Vector3(0f, 0f, -1f));

        Assert.True(world.Scene.RaycastGameplay(in ray, out GameplayRayHit hit, 8f));
        Assert.Equal(world.WallMaterial, hit.Material);
    }

    [Fact]
    public void A_part_brush_is_hit_and_reports_itself()
    {
        // Parts are absent from the compile on purpose, so they can only come
        // from the live lane. If that lane is dropped, a part brush is a
        // solid-looking box that everything shoots straight through.
        World world = Room();
        world.AddPart("crate", new Vector3(0f, 1f, -2f), new Vector3(0.5f, 0.5f, 0.5f));

        var ray = new Ray3(new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, -1f));

        Assert.True(world.Scene.RaycastGameplay(in ray, out GameplayRayHit hit, 8f));
        Assert.False(hit.StaticWorld);
        Assert.Equal("crate", hit.Node?.Name);
    }

    [Fact]
    public void The_nearer_of_the_two_lanes_wins()
    {
        // A part in front of a wall, and the same part behind it. Getting the
        // ordering wrong shows up as shooting through crates, or as crates you
        // cannot shoot because a wall five metres behind them absorbed the shot.
        World near = Room();
        near.AddPart("crate", new Vector3(2f, 1f, -2f), new Vector3(0.5f, 0.5f, 0.5f));
        var ray = new Ray3(new Vector3(2f, 1f, 0f), new Vector3(0f, 0f, -1f));

        Assert.True(near.Scene.RaycastGameplay(in ray, out GameplayRayHit nearHit, 8f));
        Assert.Equal("crate", nearHit.Node?.Name);

        World far = Room();
        far.AddPart("crate", new Vector3(2f, 1f, -6f), new Vector3(0.5f, 0.5f, 0.5f));

        Assert.True(far.Scene.RaycastGameplay(in ray, out GameplayRayHit farHit, 8f));
        Assert.True(farHit.StaticWorld, "the wall is nearer than a part behind it");
    }

    [Fact]
    public void A_node_the_filter_ignores_is_not_hit()
    {
        World world = Room();
        SceneNode crate = world.AddPart("crate", new Vector3(0f, 1f, -2f), new Vector3(0.5f, 0.5f, 0.5f));

        var ray = new Ray3(new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, -1f));
        var filter = new SceneQueryFilter { Ignore = [crate] };

        Assert.False(world.Scene.RaycastGameplay(in ray, out _, in filter, 8f),
            "with the crate ignored the ray should go through the doorway again");
    }

    [Fact]
    public void A_ray_that_reaches_nothing_reports_no_hit()
    {
        World world = Room();
        var ray = new Ray3(new Vector3(0f, 40f, 0f), Vector3.UnitY);

        Assert.False(world.Scene.RaycastGameplay(in ray, out _, 8f));
    }

    /// <summary>A room whose north wall carries a doorway cut by a negative brush.</summary>
    private sealed class World
    {
        public Scene Scene { get; } = new("GameplayRayTest");

        public MaterialRef WallMaterial { get; } = MaterialRegistry.Intern("Materials/test_wall.spectramat");

        public void Compile() => Scene.RebuildStaticWorld(new FakeRenderer());

        public void AddBox(string name, Vector3 center, Vector3 half, MaterialRef material = default)
        {
            SceneNode node = Scene.Root.CreateChild(name);
            node.LocalPosition = center;
            node.Brush = Brush.CreateBox(-half, half, material);
        }

        public void AddCut(string name, Vector3 center, Vector3 half)
        {
            SceneNode node = Scene.Root.CreateChild(name);
            node.LocalPosition = center;
            node.Brush = Brush.CreateBox(-half, half).WithOperation(BrushOperation.Subtractive);
        }

        public SceneNode AddPart(string name, Vector3 center, Vector3 half)
        {
            SceneNode node = Scene.Root.CreateChild(name);
            node.LocalPosition = center;
            node.BrushKind = BrushKind.Part;
            node.Brush = Brush.CreateBox(-half, half);
            return node;
        }
    }

    private static World Room()
    {
        var world = new World();

        // A wall at z = -4, half a unit thick, with a 2 x 2.4 opening cut flush
        // through it. Flush is the case that matters: the cut's z planes
        // coincide with the wall's.
        world.AddBox("wall", new Vector3(0f, 1.5f, -4.25f), new Vector3(6f, 1.5f, 0.25f), world.WallMaterial);
        world.AddCut("door", new Vector3(0f, 1.2f, -4.25f), new Vector3(1f, 1.2f, 0.25f));
        // The floor sits 0.01 BELOW the doorway's bottom plane rather than
        // exactly on it, which is a workaround and not a preference: an exactly
        // coplanar contact seals the opening. That is a CSG defect with its own
        // reproduction in CoplanarCutSealingTests; these tests are about the
        // query, so they step around it rather than inheriting it.
        world.AddBox("floor", new Vector3(0f, -0.51f, 0f), new Vector3(6f, 0.5f, 6f));

        world.Compile();
        return world;
    }
}
