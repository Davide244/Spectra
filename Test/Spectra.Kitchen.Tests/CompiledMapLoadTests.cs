using Spectra.Kitchen.Diagnostics;
using Spectra.Kitchen.Maps;
using Spectra.Kitchen.Cooking;
using Spectra.Kitchen.Rules;
using SpectraEngine.Bsp.Tests;
using SpectraEngine.Core;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Assets.Sources;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Maps;
using SpectraEngine.Core.Maps.Compiled;
using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// The runtime load path for a baked map: a level arrives on the GPU with no
/// carve anywhere in its history, and the scene then refuses to carve it.
/// </summary>
/// <remarks>
/// <para><b>The gate is MEASURED, not asserted.</b> A test that only checked the
/// picture would pass just as happily with a carve running behind it - the
/// re-carved world draws the same walls, twice - so every load here is bracketed
/// by <see cref="Csg.CarveInvocationsOnThisThread"/> and the delta is the claim.
/// The counter is thread-static precisely so this suite can run beside every
/// other one without a parallel compile moving it.</para>
/// <para><b>Every refusal is driven by EDITING BYTES rather than by asking the
/// builder to misbehave</b>, which is the rule D-Stage 19 established for this
/// format: a builder refusing is a fact about this cook, a reader refusing is a
/// fact about the bytes, and only the second survives a file written by something
/// else.</para>
/// </remarks>
public class CompiledMapLoadTests
{
    // --- the gate -------------------------------------------------------------

    [Fact]
    public void Loading_a_baked_map_runs_no_carve_at_all()
    {
        using var project = new TempProject();
        MapFixture fixture = MapFixture.Fresh();
        fixture.WriteBundle(project, "Room.smap");
        byte[] file = Bake(project, "Maps/Room.smap");

        var renderer = new FakeRenderer();
        var scene = new SpectraEngine.Core.Scene.Scene("empty");

        long before = Csg.CarveInvocationsOnThisThread;
        CompiledMapLoadReport report = Load(scene, renderer, file);
        long carves = Csg.CarveInvocationsOnThisThread - before;

        // The whole point of the stage, in one number.
        carves.ShouldBe(0, "a compiled map must reach the GPU with no CSG at all");

        // And the corroborating facts, which are what would be left if somebody
        // ever found a way to carve without going through Csg.
        scene.StaticWorld.ShouldBeNull("a compiled world is not the output of a compile");
        scene.StaticWorldCompileCount.ShouldBe(0);
        scene.StaticWorldDirty.ShouldBeFalse();

        report.ChunksLoaded.ShouldBeGreaterThan(0);
        report.SubmeshesUploaded.ShouldBeGreaterThan(0);
        report.TriangleCount.ShouldBeGreaterThan(0);
        scene.StaticWorldChunkMeshes.Count.ShouldBe(report.ChunksLoaded);
        renderer.LiveMeshes.Count.ShouldBe(report.SubmeshesUploaded);
    }

    [Fact]
    public void An_adopted_world_refuses_a_rebuild_and_names_the_reason()
    {
        using var project = new TempProject();
        MapFixture fixture = MapFixture.Fresh();
        fixture.WriteBundle(project, "Room.smap");

        var renderer = new FakeRenderer();
        var scene = new SpectraEngine.Core.Scene.Scene("empty");
        Load(scene, renderer, Bake(project, "Maps/Room.smap"));

        int meshes = renderer.LiveMeshes.Count;
        long before = Csg.CarveInvocationsOnThisThread;

        scene.RebuildStaticWorld(renderer);
        scene.RebuildStaticWorldIfDirty(renderer);

        (Csg.CarveInvocationsOnThisThread - before).ShouldBe(
            0, "a rebuild on an adopted world is the double-geometry bug");

        scene.RefusedStaticWorldRebuilds.ShouldBe(2);
        scene.StaticWorld.ShouldBeNull();
        renderer.LiveMeshes.Count.ShouldBe(meshes, "nothing was uploaded and nothing was destroyed");

        string said = scene.StaticWorldGuardMessage.ShouldNotBeNull();
        said.ShouldContain("Maps/Room.scmap");
        said.ShouldContain("ReleaseCompiledStaticWorld");
    }

    [Fact]
    public void An_adopted_world_refuses_the_automatic_dirty_marks_and_logs_once()
    {
        using var project = new TempProject();
        MapFixture fixture = MapFixture.Fresh();
        fixture.WriteBundle(project, "Room.smap");

        var renderer = new FakeRenderer();
        var scene = new SpectraEngine.Core.Scene.Scene("empty");
        Load(scene, renderer, Bake(project, "Maps/Room.smap"));

        // The three doors into the dirty machinery, each through the API a real
        // edit would come through rather than through the mark itself.
        scene.MarkStaticWorldDirty();

        SceneNode added = scene.Root.CreateChild("LateWall");
        added.Brush = Brush.CreateBox(-Vector3.One, Vector3.One);   // a WORLD brush: attach dirties

        SceneNode part = FindPartBrushNode(scene).ShouldNotBeNull();
        part.BrushKind = BrushKind.World;                            // an admission change

        scene.RefusedStaticWorldDirtyMarks.ShouldBeGreaterThanOrEqualTo(3);
        scene.StaticWorldDirty.ShouldBeFalse("a refused mark must not arm the pump");

        long before = Csg.CarveInvocationsOnThisThread;
        var logger = new CapturingLogger();
        scene.ProcessStaticWorldCompilation(renderer, logger);
        scene.ProcessStaticWorldCompilation(renderer, logger);

        (Csg.CarveInvocationsOnThisThread - before).ShouldBe(0);
        scene.StaticWorld.ShouldBeNull();

        // Said out loud exactly once: the marks run from property setters on the
        // editing hot path, so a line per call would be a line per frame.
        List<string> warnings = logger
            .MessagesAt(Microsoft.Extensions.Logging.LogLevel.Warning)
            .Where(m => m.Contains("Static world guard"))
            .ToList();

        warnings.Count.ShouldBe(1);
        warnings[0].ShouldContain("dirty mark(s) refused");
    }

    // --- the ASTB remap -------------------------------------------------------

    [Fact]
    public void A_submesh_resolves_through_the_asset_TABLE_and_never_through_a_material_id()
    {
        // Interned FIRST, and unrelated to this map, because a MaterialRef.Id and
        // an ASTB row index agree by coincidence in a process that interned
        // nothing else - and under that coincidence a loader reading the index as
        // an id passes every assertion below. Five, so the ids are pushed well
        // clear of the two rows this map will have.
        for (int i = 0; i < 5; i++)
            MaterialRegistry.Intern($"Materials/unrelated_{Guid.NewGuid():N}.spectramat");

        using var project = new TempProject();
        MapFixture fixture = MapFixture.Fresh();
        fixture.WriteBundle(project, "Room.smap");

        var renderer = new FakeRenderer();
        var scene = new SpectraEngine.Core.Scene.Scene("empty");
        Load(scene, renderer, Bake(project, "Maps/Room.smap"));

        // Every material the loaded world wears is one of the fixture's two, by
        // PATH. A loader that read the row index as an id would resolve to
        // whichever material this process happened to intern fifth.
        var worn = new HashSet<string>(StringComparer.Ordinal);
        foreach (StaticWorldChunkMesh chunk in scene.StaticWorldChunkMeshes)
        {
            foreach (StaticWorldSubmesh submesh in chunk.Submeshes)
            {
                if (submesh.SourceMaterial.IsDefault) continue;

                MaterialRegistry.TryGetPath(submesh.SourceMaterial, out string path).ShouldBeTrue();
                worn.Add(path);
            }
        }

        worn.ShouldBe(
            new HashSet<string>([fixture.WallMaterial, fixture.FloorMaterial], StringComparer.Ordinal));
    }

    [Fact]
    public void A_part_brushs_faces_resolve_through_the_same_table()
    {
        for (int i = 0; i < 3; i++)
            MaterialRegistry.Intern($"Materials/unrelated_{Guid.NewGuid():N}.spectramat");

        using var project = new TempProject();
        MapFixture fixture = MapFixture.Fresh();
        fixture.WriteBundle(project, "Room.smap");

        var renderer = new FakeRenderer();
        var scene = new SpectraEngine.Core.Scene.Scene("empty");
        Load(scene, renderer, Bake(project, "Maps/Room.smap"));

        SceneNode part = FindPartBrushNode(scene).ShouldNotBeNull();
        Brush brush = part.Brush.ShouldNotBeNull();
        brush.LocalPlanes.Count.ShouldBe(6);
        brush.FaceSurfaces.Count.ShouldBe(6);

        MaterialRegistry.TryGetPath(brush.FaceSurfaces[0].Material, out string path).ShouldBeTrue();
        path.ShouldBe(fixture.FloorMaterial);
    }

    // --- the bake oracle, from the LOADED arrays -------------------------------

    [Fact]
    public void The_loaded_arrays_are_element_identical_to_a_fresh_cache_free_compile()
    {
        using var project = new TempProject();
        MapFixture fixture = MapFixture.Fresh();
        fixture.WriteBundle(project, "Room.smap");

        var renderer = new FakeRenderer();
        var scene = new SpectraEngine.Core.Scene.Scene("empty");
        Load(scene, renderer, Bake(project, "Maps/Room.smap"));

        // The same source, compiled here and now, with no cache: the guard that
        // replaces P11b's unsatisfiable text round trip, stated against what the
        // GPU actually received rather than against what the file holds.
        MapDocument document = MapBundle.Load(Path.Combine(project.Layout.MapsPath, "Room.smap"));
        var authored = new SpectraEngine.Core.Scene.Scene(document.Scene.Name);
        MapSceneBinder.ApplyTo(document, authored);
        IReadOnlyList<BrushPlacement> placements =
            authored.CaptureStaticWorldPlacements(out _).ShouldNotBeNull();

        CsgWorld fresh = CsgWorld.Build(placements);
        fresh.ChunkMeshes.Count.ShouldBeGreaterThan(0);
        scene.StaticWorldChunkMeshes.Count.ShouldBe(fresh.ChunkMeshes.Count);

        foreach (ChunkMesh expected in fresh.ChunkMeshes)
        {
            scene.TryGetStaticWorldChunkMesh(expected.Coord, out StaticWorldChunkMesh loaded)
                .ShouldBeTrue($"cell {expected.Coord} is missing from the loaded world");

            loaded.RenderBounds.Min.ShouldBe(expected.RenderBounds.Min);
            loaded.RenderBounds.Max.ShouldBe(expected.RenderBounds.Max);
            loaded.Submeshes.Length.ShouldBe(expected.Submeshes.Count);

            // Matched by MATERIAL, never positionally: the file is in ascending
            // asset-table row and the compile is in ascending material id, and
            // those are different orders on purpose.
            foreach (ChunkSubmesh submesh in expected.Submeshes)
            {
                StaticWorldSubmesh actual = loaded.Submeshes.Single(s => s.SourceMaterial == submesh.Material);
                var mesh = (FakeMesh)actual.Mesh;
                mesh.VertexData.ShouldBe(submesh.Vertices);
                mesh.IndexData.ShouldBe(submesh.Indices);
            }
        }
    }

    [Fact]
    public void The_adopted_trees_answer_point_queries_exactly_as_a_fresh_compile_does()
    {
        using var project = new TempProject();
        MapFixture fixture = MapFixture.Fresh();
        fixture.WriteBundle(project, "Room.smap");

        var renderer = new FakeRenderer();
        var scene = new SpectraEngine.Core.Scene.Scene("empty");
        Load(scene, renderer, Bake(project, "Maps/Room.smap"));

        MapDocument document = MapBundle.Load(Path.Combine(project.Layout.MapsPath, "Room.smap"));
        var authored = new SpectraEngine.Core.Scene.Scene(document.Scene.Name);
        MapSceneBinder.ApplyTo(document, authored);
        CsgWorld fresh = CsgWorld.Build(authored.CaptureStaticWorldPlacements(out _).ShouldNotBeNull());

        CompiledStaticWorld adopted = scene.CompiledStaticWorld.ShouldNotBeNull();

        // Inside the floor slab, in the air above it, in the wall, and in the
        // doorway cut flush through the wall's base - which is the fixture's whole
        // reason for being.
        Vector3[] probes =
        [
            new(0f, -0.25f, 0f),
            new(0f, 3f, 0f),
            new(2.5f, 1.5f, -4.25f),
            new(0f, 1.0f, -4.25f),
        ];

        foreach (Vector3 probe in probes)
        {
            adopted.ContainsPoint(probe).ShouldBe(
                fresh.ContainsPoint(probe), $"the baked tree disagrees at {probe}");
        }

        // Not a blind instrument: the probes have to disagree with each other, or
        // a tree that answered false everywhere would pass.
        probes.Select(fresh.ContainsPoint).Distinct().Count().ShouldBe(2);
        adopted.BspChunkCount.ShouldBeGreaterThan(0);
    }

    // --- the double-geometry guard, from the file's own side --------------------

    [Fact]
    public void A_baked_brush_is_not_rebuilt_even_when_the_file_carries_its_planes()
    {
        using var project = new TempProject();
        MapFixture fixture = MapFixture.Fresh();
        fixture.WriteBundle(project, "Room.smap");

        var plainRenderer = new FakeRenderer();
        var plain = new SpectraEngine.Core.Scene.Scene("plain");
        CompiledMapLoadReport plainReport = Load(plain, plainRenderer, Bake(project, "Maps/Room.smap"));

        var keptRenderer = new FakeRenderer();
        var kept = new SpectraEngine.Core.Scene.Scene("kept");
        CompiledMapLoadReport keptReport =
            Load(kept, keptRenderer, Bake(project, "Maps/Room.smap", keepBrushSource: true));

        // The file offered the world brushes' planes, and the load declined them.
        keptReport.BakedBrushSourcesSkipped.ShouldBeGreaterThan(0);
        plainReport.BakedBrushSourcesSkipped.ShouldBe(0);

        // The oracle is a triangle count: a loader that rebuilt them would arm a
        // compile whose surfaces are already in the chunks, and every wall would
        // be drawn twice.
        keptReport.TriangleCount.ShouldBe(plainReport.TriangleCount);
        keptRenderer.LiveMeshes.Count.ShouldBe(plainRenderer.LiveMeshes.Count);

        // And no node in the kept-source scene carries a world brush at all, which
        // is the mechanism behind the number above.
        CountStaticWorldBrushes(kept.Root).ShouldBe(0);
        kept.StaticWorld.ShouldBeNull();
        kept.RefusedStaticWorldDirtyMarks.ShouldBe(0, "attaching nothing dirties nothing");
    }

    // --- the graph -------------------------------------------------------------

    [Fact]
    public void The_graph_is_rebuilt_in_one_forward_pass_with_ids_parents_and_order_intact()
    {
        byte[] file = ScmapFixture.Build();

        var renderer = new FakeRenderer();
        var scene = new SpectraEngine.Core.Scene.Scene("empty");
        CompiledMapLoadReport report = Load(scene, renderer, file);

        report.NodesLoaded.ShouldBe(ScmapFixture.NodeNames.Length);
        scene.Name.ShouldBe(ScmapFixture.SceneName);

        SceneNode world = scene.Root.Children.Single();
        world.Name.ShouldBe("World");
        world.Id.ShouldBe(ScmapFixture.NodeId(0));

        // Sibling order is authored data: traversal order is placement order is
        // the order the carve breaks its overlap ties in, so a load that appended
        // in any other order rebuilds a level that is valid and different.
        world.Children.Select(c => c.Name).ShouldBe(new[] { "zeta_room", "alpha_room" });
        world.Children[0].Children.Select(c => c.Name).ShouldBe(new[] { "Wall", "Cut" });
        world.Children[1].Children.Select(c => c.Name).ShouldBe(new[] { "Lamp", "Crate" });

        SceneNode wall = world.Children[0].Children[0];
        wall.Id.ShouldBe(ScmapFixture.NodeId(2));
        wall.LocalPosition.ShouldBe(ScmapFixture.Transforms[2].Position);
        wall.LocalRotation.ShouldBe(ScmapFixture.Transforms[2].Rotation);
        wall.LocalScale.ShouldBe(ScmapFixture.Transforms[2].Scale);

        // A baked brush gets NO brush, whatever else it gets.
        wall.Brush.ShouldBeNull();
        scene.TryFindById(ScmapFixture.NodeId(3), out SceneNode? cut).ShouldBeTrue();
        cut.ShouldNotBeNull().Brush.ShouldBeNull();
    }

    [Fact]
    public void A_mesh_instance_is_NAMED_rather_than_silently_dropped()
    {
        byte[] file = ScmapFixture.Build();

        var renderer = new FakeRenderer();
        var scene = new SpectraEngine.Core.Scene.Scene("empty");
        CompiledMapLoadReport report = Load(scene, renderer, file);

        report.UnboundMeshInstances.ShouldBe(new[] { "Crate" });
        report.IsComplete.ShouldBeFalse();
        report.Describe().ShouldNotBeNull().ShouldContain("Crate");

        // The node is still there, in its place, with its id: what is missing is
        // its geometry, not the level's shape.
        scene.TryFindById(ScmapFixture.NodeId(6), out SceneNode? crate).ShouldBeTrue();
        crate.ShouldNotBeNull().MeshRenderer.ShouldBeNull();
    }

    [Fact]
    public void The_format_gaps_are_stated_rather_than_discovered()
    {
        // A constant, printed on every load. The failure being guarded against is
        // a level that quietly loses its lights: a lamp's node arrives and nothing
        // anywhere says the lamp did not.
        string said = CompiledMapLoadReport.DescribeFormatGaps();

        said.ShouldContain("lights");
        said.ShouldContain("spawns");
        said.ShouldContain("entities");
        said.ShouldContain("submesh indices");
        said.ShouldContain("collision");
        CompiledMapLoadReport.FormatGaps.Count.ShouldBe(7);
    }

    // --- refusals, all by BYTE SURGERY ----------------------------------------

    [Fact]
    public void A_map_at_another_format_version_is_refused_naming_both_numbers()
    {
        byte[] file = ScmapFixture.Build();
        BitConverter.GetBytes((ushort)(EngineInfo.CompiledMapFormatVersion + 1)).CopyTo(file, 0x04);

        ScmapFormatException refused = Should.Throw<ScmapFormatException>(
            () => Load(new SpectraEngine.Core.Scene.Scene("empty"), new FakeRenderer(), file));

        refused.Message.ShouldContain((EngineInfo.CompiledMapFormatVersion + 1).ToString());
        refused.Message.ShouldContain(EngineInfo.CompiledMapFormatVersion.ToString());
        refused.Message.ShouldContain("Recook");
    }

    [Fact]
    public void A_map_compiled_on_another_cell_size_is_refused_naming_both_numbers()
    {
        byte[] file = ScmapFixture.Build();

        // META's preamble: SceneNameString, SpawnCount, then the three compile
        // constants. A world chunked on another lattice mis-routes every point and
        // ray query, which reads as sporadic collision bugs rather than a version
        // problem - and its chunks do not line up, which renders as gaps.
        (int offset, _) = FindSection(file, ScmapFormat.MetaSection);
        BitConverter.GetBytes(ChunkCoord.CellSize * 2f).CopyTo(file, offset + 8);

        ScmapFormatException refused = Should.Throw<ScmapFormatException>(
            () => Load(new SpectraEngine.Core.Scene.Scene("empty"), new FakeRenderer(), file));

        refused.Message.ShouldContain("cell size");
        refused.Message.ShouldContain((ChunkCoord.CellSize * 2f).ToString());
        refused.Message.ShouldContain(ChunkCoord.CellSize.ToString());
        refused.Message.ShouldContain("Recook");
    }

    [Fact]
    public void A_map_welded_on_another_grid_is_refused_naming_both_numbers()
    {
        byte[] file = ScmapFixture.Build();
        (int offset, _) = FindSection(file, ScmapFormat.MetaSection);
        BitConverter.GetBytes(ScmapFormat.EngineSnapGrid * 4f).CopyTo(file, offset + 16);

        ScmapFormatException refused = Should.Throw<ScmapFormatException>(
            () => Load(new SpectraEngine.Core.Scene.Scene("empty"), new FakeRenderer(), file));

        refused.Message.ShouldContain("snap grid");
        refused.Message.ShouldContain("Recook");
    }

    [Fact]
    public void A_refused_map_releases_the_bytes_it_was_handed()
    {
        // Not housekeeping. On a mounted pack the blob holds a PackHandle
        // reference, so one left undisposed by a failed load is a mount that can
        // never be released, for the life of the process, with no message
        // anywhere.
        byte[] file = ScmapFixture.Build();
        BitConverter.GetBytes((ushort)(EngineInfo.CompiledMapFormatVersion + 1)).CopyTo(file, 0x04);

        ContentBlob blob = ContentBlob.CopyOf(file);
        Should.Throw<ScmapFormatException>(
            () => CompiledMapLoader.Load(
                new SpectraEngine.Core.Scene.Scene("empty"), new FakeRenderer(), blob, "Maps/Room.scmap"));

        Should.Throw<ObjectDisposedException>(() => blob.Span.Length);
    }

    // --- lifetime -------------------------------------------------------------

    [Fact]
    public void Releasing_an_adopted_world_destroys_its_meshes_and_frees_its_bytes()
    {
        using var project = new TempProject();
        MapFixture fixture = MapFixture.Fresh();
        fixture.WriteBundle(project, "Room.smap");

        var renderer = new FakeRenderer();
        var scene = new SpectraEngine.Core.Scene.Scene("empty");
        Load(scene, renderer, Bake(project, "Maps/Room.smap"));

        CompiledStaticWorld adopted = scene.CompiledStaticWorld.ShouldNotBeNull();
        FlatBspTree tree = adopted.Chunks.First(c => c.Bsp is not null).Bsp!;
        renderer.LiveMeshes.Count.ShouldBeGreaterThan(0);

        scene.ReleaseCompiledStaticWorld(renderer);

        scene.CompiledStaticWorld.ShouldBeNull();
        scene.HasCompiledStaticWorld.ShouldBeFalse();
        renderer.LiveMeshes.ShouldBeEmpty("DestroyMesh, so the tracking list loses them too");
        scene.StaticWorldChunkMeshes.ShouldBeEmpty();

        // The nodes were a window into the released bytes, so a query afterwards
        // is an exception naming the blob rather than a read of address space the
        // pack no longer owns.
        Should.Throw<ObjectDisposedException>(() => tree.ContainsPoint(Vector3.Zero));

        // And the guard is lifted: an authored scene can be compiled again.
        long before = Csg.CarveInvocationsOnThisThread;
        scene.Root.CreateChild("Block").Brush = Brush.CreateBox(-Vector3.One, Vector3.One);
        scene.RebuildStaticWorld(renderer);

        (Csg.CarveInvocationsOnThisThread - before).ShouldBeGreaterThan(0);
        scene.StaticWorld.ShouldNotBeNull();
        scene.RefusedStaticWorldRebuilds.ShouldBe(0);
    }

    // --- helpers ---------------------------------------------------------------

    private static CompiledMapLoadReport Load(
        SpectraEngine.Core.Scene.Scene scene, Renderer renderer, byte[] file) =>
        CompiledMapLoader.Load(scene, renderer, ContentBlob.CopyOf(file), "Maps/Room.scmap");

    private static byte[] Bake(TempProject project, string bundlePath, bool keepBrushSource = false)
    {
        var context = new RuleContext(
            project.Root, bundlePath, CookProfile.Ship, keepBrushSource: keepBrushSource);

        new MapRule().Cook(context);

        context.Diagnostics.Count.ShouldBe(
            0, string.Join(Environment.NewLine, context.Diagnostics.Select(d => d.ToString())));

        return context.Emissions[0].Payload;
    }

    private static SceneNode? FindPartBrushNode(SpectraEngine.Core.Scene.Scene scene)
    {
        foreach (SceneNode node in scene.Root.Traverse())
        {
            if (node.Brush is not null && node.BrushKind == BrushKind.Part) return node;
        }

        return null;
    }

    private static int CountStaticWorldBrushes(SceneNode root)
    {
        int found = 0;
        foreach (SceneNode node in root.Traverse())
        {
            if (node.IsStaticWorldBrush) found++;
        }

        return found;
    }

    private static (int Offset, int Size) FindSection(byte[] file, uint kind)
    {
        uint count = BitConverter.ToUInt32(file, 0x0C);
        for (int i = 0; i < count; i++)
        {
            int at = ScmapFormat.SectionTableOffset + (i * ScmapFormat.SectionSize);
            if (BitConverter.ToUInt32(file, at) != kind) continue;

            return ((int)BitConverter.ToUInt64(file, at + 8), (int)BitConverter.ToUInt64(file, at + 16));
        }

        throw new InvalidOperationException($"No section '{ScmapFormat.DescribeFourCc(kind)}' in this file.");
    }
}
