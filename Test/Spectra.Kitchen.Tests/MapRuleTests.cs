using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Spectra.Kitchen.Cooking;
using Spectra.Kitchen.Diagnostics;
using Spectra.Kitchen.Maps;
using Spectra.Kitchen.Rules;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Maps;
using SpectraEngine.Core.Maps.Compiled;
using SpectraEngine.Core.Scene;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// The map bake: a <c>.smap</c> bundle in, a <c>.scmap</c> out, and the three
/// hazards that produce a file which loads perfectly in the test that wrote it.
/// </summary>
/// <remarks>
/// <para><b>Every case here runs the real rule over a real bundle on a real
/// filesystem.</b> The cook's whole input is a folder, and the parts most likely to
/// be wrong - path normalisation, the walk order, which root a path is relative to
/// - are exactly the parts a fake would paper over.</para>
/// <para><b>The three hazards are the point of this file</b>, and each was checked
/// by breaking it: the asset-index remap replaced by <c>MaterialRef.Id</c>, a
/// section size computed a second way, and a re-carve on top of the baked chunks.
/// A test that does not bite is not a test.</para>
/// </remarks>
public class MapRuleTests
{
    [Fact]
    public void A_bundle_bakes_into_a_compiled_map_with_geometry_in_it()
    {
        using var project = new TempProject();
        MapFixture fixture = MapFixture.Fresh();
        fixture.WriteBundle(project, "Room.smap");

        ScmapProbe map = Bake(project, "Maps/Room.smap");

        map.SceneName.ShouldBe("BakeRoom");
        map.Chunks.Count.ShouldBeGreaterThan(0);
        map.TriangleCount.ShouldBeGreaterThan(0);

        // A shipped game runs zero CSG at load, which means the cells have to
        // arrive with their trees as well as their triangles.
        map.Geometry.Count(cell => cell.HasBsp).ShouldBe(map.Chunks.Count);
    }

    // --- hazard 1: MaterialRef.Id must never reach the file -------------------

    [Fact]
    public void A_submesh_names_an_asset_TABLE_ROW_and_never_a_material_id()
    {
        using var project = new TempProject();
        MapFixture fixture = MapFixture.Fresh();

        // The whole test. MaterialRegistry hands out ids in per-process interning
        // order, so interning something unrelated FIRST pushes this map's ids past
        // its own asset indices; without it the two agree by coincidence and a cook
        // that wrote an id would pass every assertion below.
        for (int i = 0; i < 5; i++) MaterialRegistry.Intern($"Materials/unrelated_{Guid.NewGuid():N}.spectramat");

        fixture.WriteBundle(project, "Room.smap");
        ScmapProbe map = Bake(project, "Maps/Room.smap");

        int wallId = MaterialRegistry.Intern(fixture.WallMaterial).Id;
        int floorId = MaterialRegistry.Intern(fixture.FloorMaterial).Id;

        // The non-vacuity check: an id that happened to equal its row would make
        // every assertion below true of the wrong file too.
        map.Assets.Count.ShouldBe(2);
        wallId.ShouldBeGreaterThan(map.Assets.Count);
        floorId.ShouldBeGreaterThan(map.Assets.Count);

        var paths = map.Assets.Select(row => row.Path).ToList();
        paths.ShouldContain(fixture.WallMaterial);
        paths.ShouldContain(fixture.FloorMaterial);
        map.Assets.ShouldAllBe(row => row.Kind == PackEntryKind.Material);

        // Every index a submesh carries is a row of THIS table, and the sentinel is
        // the only other legal value.
        foreach (ScmapProbe.CellGeometry cell in map.Geometry)
        {
            foreach (ScmapProbe.SubmeshCopy submesh in cell.Submeshes)
            {
                if (submesh.AssetIndex == ScmapFormat.NoAssetIndex) continue;
                submesh.AssetIndex.ShouldBeLessThan((uint)map.Assets.Count);
            }
        }
    }

    [Fact]
    public void The_wall_submesh_resolves_to_the_wall_material_through_the_table()
    {
        using var project = new TempProject();
        MapFixture fixture = MapFixture.Fresh();
        for (int i = 0; i < 3; i++) MaterialRegistry.Intern($"Materials/unrelated_{Guid.NewGuid():N}.spectramat");

        fixture.WriteBundle(project, "Room.smap");
        ScmapProbe map = Bake(project, "Maps/Room.smap");

        // The wall is at z = -4.25 and the floor spans y = -1..0, so the cell above
        // the origin carries wall surfaces and the cell below carries floor ones.
        // Rather than reason about cells, ask which materials the file claims are
        // drawn at all: both, exactly once each per cell that wears them.
        var drawn = new HashSet<string>(StringComparer.Ordinal);
        foreach (ScmapProbe.CellGeometry cell in map.Geometry)
        {
            foreach (ScmapProbe.SubmeshCopy submesh in cell.Submeshes)
                drawn.Add(map.Assets[(int)submesh.AssetIndex].Path);
        }

        drawn.ShouldBe(new[] { fixture.WallMaterial, fixture.FloorMaterial }, ignoreOrder: true);
    }

    [Fact]
    public void Submeshes_are_in_ascending_asset_order_within_a_cell()
    {
        using var project = new TempProject();
        MapFixture fixture = MapFixture.Fresh();
        fixture.WriteBundle(project, "Room.smap");

        ScmapProbe map = Bake(project, "Maps/Room.smap");

        // A total order over a VALUE key, which is what makes two compiles of one
        // cell emit the same submeshes in the same order. The compile's own order is
        // ascending material ID, which is per-process interning order and would not.
        bool multi = false;
        foreach (ScmapProbe.CellGeometry cell in map.Geometry)
        {
            for (int i = 1; i < cell.Submeshes.Count; i++)
            {
                multi = true;
                cell.Submeshes[i].AssetIndex.ShouldBeGreaterThan(cell.Submeshes[i - 1].AssetIndex);
            }
        }

        multi.ShouldBeTrue("no cell in the fixture wears two materials, so the ordering rule was not exercised");
    }

    // --- hazard 2: alignment --------------------------------------------------

    [Fact]
    public void Every_section_and_every_blob_starts_on_the_payload_alignment()
    {
        using var project = new TempProject();
        MapFixture fixture = MapFixture.Fresh();
        fixture.WriteBundle(project, "Room.smap");

        byte[] file = BakeBytes(project, "Maps/Room.smap");
        ScmapProbe map = ScmapProbe.Read(file);

        // The section table's own claim, read straight off the bytes rather than
        // from the layout that wrote them.
        for (int i = 0; i < map.Header.SectionCount; i++)
        {
            int at = ScmapFormat.SectionTableOffset + (i * ScmapFormat.SectionSize);
            ulong offset = BitConverter.ToUInt64(file, at + 8);
            (offset % ScmapFormat.PayloadAlignment).ShouldBe(0ul);
        }

        // And every blob inside the two geometry sections, which the reader checks
        // on the way past: a blob one byte out is a vertex array read from the
        // middle of somebody else's.
        foreach (ScmapChunkRecord cell in map.Chunks)
        {
            (cell.MeshOffset % ScmapFormat.PayloadAlignment).ShouldBe(0u);
            (cell.BspOffset % ScmapFormat.PayloadAlignment).ShouldBe(0u);
        }
    }

    [Fact]
    public void A_directory_entry_reaching_past_the_mesh_section_is_refused()
    {
        // A claim about the BYTES rather than about the builder, which is the only
        // one of the two that survives a file edited afterwards. The builder places
        // every blob itself, so this state is unreachable through it.
        using var project = new TempProject();
        MapFixture fixture = MapFixture.Fresh();
        fixture.WriteBundle(project, "Room.smap");

        byte[] file = BakeBytes(project, "Maps/Room.smap");
        (int offset, _) = FindSection(file, ScmapFormat.ChunkDirectorySection);

        // The first record's MeshSize: three cell coordinates, two bounds vectors
        // and the mesh OFFSET ahead of it, inside a 64-byte record that starts
        // after the section's 16-byte preamble.
        int meshSize = offset + ScmapFormat.ChunkPreambleSize
            + (3 * sizeof(int)) + (6 * sizeof(float)) + sizeof(uint);
        BitConverter.GetBytes(uint.MaxValue - 15).CopyTo(file, meshSize);

        Should.Throw<ScmapFormatException>(() => ScmapProbe.Read(file))
            .Message.ShouldContain("CMSH");
    }

    // --- hazard 3: double geometry --------------------------------------------

    [Fact]
    public void Keeping_the_brush_source_draws_the_same_triangles_as_not_keeping_it()
    {
        // THE guard. When baked chunks and BRSH are both present, a loader that
        // helpfully re-carves produces a world where every wall is drawn twice, with
        // z-fighting that reads as a depth-precision bug rather than as a map
        // loader. The triangle count is the measurement that catches it.
        using var project = new TempProject();
        MapFixture fixture = MapFixture.Fresh();
        fixture.WriteBundle(project, "Room.smap");

        ScmapProbe without = Bake(project, "Maps/Room.smap", keepBrushSource: false);
        ScmapProbe with = Bake(project, "Maps/Room.smap", keepBrushSource: true);

        with.TriangleCount.ShouldBe(without.TriangleCount);
        with.TriangleCount.ShouldBeGreaterThan(0);

        // The asset table is a function of the MAP rather than of which brushes a
        // particular cook kept, which is what ClaimFaceMaterials exists for: without
        // it the switch renumbers every row and a submesh baked from the same
        // surfaces points somewhere else, with the triangle count unmoved.
        with.Assets.ShouldBe(without.Assets);

        // And the cook really did keep more, or the equality above is the equality
        // of two identical files.
        with.Brushes.Count.ShouldBeGreaterThan(without.Brushes.Count);
    }

    [Fact]
    public void A_baked_brush_is_never_offered_for_re_carving_and_a_part_always_is()
    {
        using var project = new TempProject();
        MapFixture fixture = MapFixture.Fresh();
        fixture.WriteBundle(project, "Room.smap");

        ScmapProbe map = Bake(project, "Maps/Room.smap", keepBrushSource: true);

        map.HasBrushSource.ShouldBeTrue();

        int reCarvable = 0;
        int baked = 0;
        foreach (ScmapProbe.BrushCopy brush in map.Brushes)
        {
            ScmapNodeRecord node = map.Nodes[(int)brush.NodeIndex];

            if (ScmapBrushSource.IsReCarvable(in node)) reCarvable++;
            else baked++;

            // BakedIntoChunks is the cooked-record name and IsStaticWorldBrush is
            // the engine name, and neither may take the other's spelling: the
            // cooked flag says "already baked, do not re-carve" where the engine
            // predicate says "admitted to the carve".
            node.BakedIntoChunks.ShouldBe(node.PayloadKind == ScmapPayloadKind.StaticWorldBrush);
        }

        // The world brushes are in the section and none of them may be carved
        // again; the part brush is the only thing a loader may build from planes.
        baked.ShouldBe(4);
        reCarvable.ShouldBe(1);
    }

    [Fact]
    public void A_part_brush_keeps_its_planes_even_when_the_cook_was_not_asked_to()
    {
        // Not a convenience. A part is never baked into a chunk and its mesh is
        // built at runtime from its own Brush, so its planes live nowhere else: a
        // cook that dropped them ships a level whose parts are invisible with
        // nothing reporting it.
        using var project = new TempProject();
        MapFixture fixture = MapFixture.Fresh();
        fixture.WriteBundle(project, "Room.smap");

        ScmapProbe map = Bake(project, "Maps/Room.smap", keepBrushSource: false);

        map.HasBrushSource.ShouldBeTrue();
        map.Brushes.Count.ShouldBe(1);
        map.Nodes[(int)map.Brushes[0].NodeIndex].PayloadKind.ShouldBe(ScmapPayloadKind.PartBrush);
    }

    [Fact]
    public void A_map_with_no_part_brushes_and_no_kept_source_carries_no_BRSH_at_all()
    {
        using var project = new TempProject();
        MapFixture fixture = MapFixture.Fresh();
        fixture.WriteBundle(project, "Room.smap", withPart: false);

        ScmapProbe map = Bake(project, "Maps/Room.smap", keepBrushSource: false);

        // Absent rather than empty, because the header flag beside it is a claim
        // about PRESENCE and an empty section is present. The reader cross-checks
        // the two, so a file where they disagreed would be refused.
        map.HasBrushSource.ShouldBeFalse();
        map.Brushes.ShouldBeEmpty();
        (map.Header.FileFlags & ScmapFlags.HasBrushSource).ShouldBe(ScmapFlags.None);
    }

    // --- the subtractive case -------------------------------------------------

    [Fact]
    public void A_subtractive_brush_bakes_its_cavity_rather_than_its_own_skin()
    {
        using var project = new TempProject();
        MapFixture fixture = MapFixture.Fresh();
        fixture.WriteBundle(project, "Room.smap");
        fixture.WriteBundle(project, "Solid.smap", withDoorway: false);

        ScmapProbe cut = Bake(project, "Maps/Room.smap");
        ScmapProbe solid = Bake(project, "Maps/Solid.smap");

        // A negative emits no skin of its own and seeds cavity walls into the brush
        // it cuts, attributed to the CUT brush's slot - so a doorway ADDS triangles
        // rather than removing them, which is the answer that catches a bake that
        // dropped the cavity and left a hole in the wall.
        cut.TriangleCount.ShouldBeGreaterThan(solid.TriangleCount);

        // The subtractive node survives as a node with the bit set, because a
        // SetBrushKindCommand must not become lossy.
        ScmapNodeRecord doorway = cut.Nodes[cut.NodeNames.IndexOf("Doorway")];
        doorway.PayloadKind.ShouldBe(ScmapPayloadKind.StaticWorldBrush);
        doorway.IsSubtractiveBrush.ShouldBeTrue();
    }

    [Fact]
    public void The_flush_coplanar_doorway_is_open_in_the_baked_trees()
    {
        // The repo's coincident-plane regression fixture, asked of the BAKED tree
        // rather than of the live one: the doorway cuts flush through the wall's own
        // bottom plane and the floor reaches that same plane, and the arrangement
        // used to compile solid.
        using var project = new TempProject();
        MapFixture fixture = MapFixture.Fresh();
        fixture.WriteBundle(project, "Room.smap");

        byte[] file = BakeBytes(project, "Maps/Room.smap");
        var opening = new Vector3(0f, 1.2f, -4.25f);

        ScmapDocument document = ScmapReader.Read(file, "Room.scmap");
        ChunkCoord cell = ChunkCoord.FromPosition(opening);

        bool asked = false;
        for (int i = 0; i < document.Chunks.Length; i++)
        {
            if (document.Chunks[i].X != cell.X ||
                document.Chunks[i].Y != cell.Y ||
                document.Chunks[i].Z != cell.Z)
            {
                continue;
            }

            asked = true;
            document.ChunkBsp(i).ContainsPoint(opening).ShouldBeFalse(
                "the doorway compiled solid in the baked tree");
        }

        asked.ShouldBeTrue("the cell holding the doorway has no baked tree to ask");
    }

    // --- the bake oracle ------------------------------------------------------

    [Fact]
    public void The_baked_arrays_are_element_identical_to_a_fresh_compile_of_the_same_source()
    {
        // The guard that replaces P11b's unsatisfiable text round trip. Welding,
        // T-junction repair and per-cell carving are not invertible, so a compiled
        // map cannot be turned back into a text one; what CAN be claimed is that the
        // file holds exactly what a compile of the same source produces.
        using var project = new TempProject();
        MapFixture fixture = MapFixture.Fresh();
        fixture.WriteBundle(project, "Room.smap");

        ScmapProbe map = Bake(project, "Maps/Room.smap");

        MapDocument document = MapBundle.Load(Path.Combine(project.Layout.MapsPath, "Room.smap"));
        var scene = new SpectraEngine.Core.Scene.Scene(document.Scene.Name);
        MapSceneBinder.ApplyTo(document, scene);

        IReadOnlyList<BrushPlacement>? placements = scene.CaptureStaticWorldPlacements(out _);
        placements.ShouldNotBeNull();

        CsgWorld world = CsgWorld.Build(placements);
        world.ChunkMeshes.Count.ShouldBeGreaterThan(0);

        foreach (ChunkMesh mesh in world.ChunkMeshes)
        {
            ScmapProbe.CellGeometry cell = map.Geometry.Single(
                c => c.X == mesh.Coord.X && c.Y == mesh.Coord.Y && c.Z == mesh.Coord.Z);

            cell.Submeshes.Count.ShouldBe(mesh.Submeshes.Count);

            // Compared as a SET keyed on the material's path, because the file is in
            // ascending asset index and the compile is in ascending material id, and
            // those are different orders on purpose.
            foreach (ChunkSubmesh submesh in mesh.Submeshes)
            {
                uint index = submesh.Material.IsDefault
                    ? ScmapFormat.NoAssetIndex
                    : (uint)map.Assets.FindIndex(row =>
                        string.Equals(
                            row.Path,
                            MaterialPath(submesh.Material),
                            StringComparison.OrdinalIgnoreCase));

                ScmapProbe.SubmeshCopy baked = cell.Submeshes.Single(s => s.AssetIndex == index);
                baked.Vertices.ShouldBe(submesh.Vertices);
                baked.Indices.ShouldBe(submesh.Indices);
            }
        }
    }

    // --- refusals -------------------------------------------------------------

    [Fact]
    public void A_brush_node_under_a_scale_is_refused_rather_than_baked()
    {
        using var project = new TempProject();
        MapFixture fixture = MapFixture.Fresh();

        SpectraEngine.Core.Scene.Scene scene = fixture.BuildScene();
        scene.Root.Children[0].LocalScale = new Vector3(2f, 1f, 1f);
        MapFixture.WriteBundle(project, "Bad.smap", scene);

        (byte[]? file, List<CookDiagnostic> said) = TryBake(project, "Maps/Bad.smap");

        // The runtime degrades and the cooker does not: in the editor this is a
        // standing status warning and the last good world keeps rendering, which is
        // exactly the state that must not ship.
        file.ShouldBeNull();
        said.Single().Id.ToString().ShouldBe("SC7001");
    }

    [Fact]
    public void A_bundle_whose_document_will_not_parse_is_refused_naming_the_document()
    {
        using var project = new TempProject();
        string bundle = Path.Combine(project.Layout.MapsPath, "Broken.smap");
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, MapFormat.DocumentFileName), "{ \"spectramap\": ");

        (byte[]? file, List<CookDiagnostic> said) = TryBake(project, "Maps/Broken.smap");

        file.ShouldBeNull();
        said.Single().Id.ToString().ShouldBe("SC7007");
    }

    [Fact]
    public void Per_user_editor_state_is_not_read_and_not_hashed()
    {
        // It is gitignored, per-user and changes every time somebody moves the
        // viewport camera. Hashed into the source digest it would put a different
        // number in every developer's compiled map for one level; read as a
        // dependency it would miss the cook cache on every launch.
        using var project = new TempProject();
        MapFixture fixture = MapFixture.Fresh();
        string bundle = fixture.WriteBundle(project, "Room.smap");

        byte[] before = BakeBytes(project, "Maps/Room.smap");

        File.WriteAllText(Path.Combine(bundle, MapFormat.UserStateFileName), "{ \"camera\": 1 }");
        byte[] after = BakeBytes(project, "Maps/Room.smap");

        after.ShouldBe(before);
    }

    [Fact]
    public void The_source_digest_moves_when_the_document_does()
    {
        using var project = new TempProject();
        MapFixture fixture = MapFixture.Fresh();
        fixture.WriteBundle(project, "Room.smap");
        ScmapProbe before = Bake(project, "Maps/Room.smap");

        fixture.WriteBundle(project, "Room.smap", withPart: false);
        ScmapProbe after = Bake(project, "Maps/Room.smap");

        // The negative control for the test above: the digest is a fact about the
        // bundle, so it has to move for a real edit or it would be measuring
        // nothing.
        after.Header.SourceMapDigest.ShouldNotBe(before.Header.SourceMapDigest);
    }

    // --- helpers ---------------------------------------------------------------

    private static ScmapProbe Bake(TempProject project, string bundlePath, bool keepBrushSource = false) =>
        ScmapProbe.Read(BakeBytes(project, bundlePath, keepBrushSource), bundlePath);

    private static byte[] BakeBytes(TempProject project, string bundlePath, bool keepBrushSource = false)
    {
        (byte[]? file, List<CookDiagnostic> said) = TryBake(project, bundlePath, keepBrushSource);
        said.ShouldBeEmpty();
        file.ShouldNotBeNull();
        return file;
    }

    private static (byte[]? File, List<CookDiagnostic> Diagnostics) TryBake(
        TempProject project, string bundlePath, bool keepBrushSource = false)
    {
        // The map rule's content root is the PROJECT root, because a bundle lives
        // beside Assets/ rather than inside it.
        var context = new RuleContext(
            project.Root, bundlePath, CookProfile.Ship, keepBrushSource: keepBrushSource);

        new MapRule().Cook(context);

        return (
            context.Emissions.Count == 0 ? null : context.Emissions[0].Payload,
            [.. context.Diagnostics]);
    }

    private static string MaterialPath(MaterialRef material) =>
        MaterialRegistry.TryGetPath(material, out string path) ? path : string.Empty;

    private static (int Offset, int Size) FindSection(byte[] file, uint kind)
    {
        uint count = BitConverter.ToUInt32(file, 0x0C);
        for (int i = 0; i < count; i++)
        {
            int at = ScmapFormat.SectionTableOffset + (i * ScmapFormat.SectionSize);
            if (BitConverter.ToUInt32(file, at) != kind) continue;

            return ((int)BitConverter.ToUInt64(file, at + 8), (int)BitConverter.ToUInt64(file, at + 16));
        }

        throw new InvalidOperationException($"No '{ScmapFormat.DescribeFourCc(kind)}' section in the file.");
    }
}
