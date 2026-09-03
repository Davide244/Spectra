using Spectra.Kitchen.Cooking;
using Spectra.Kitchen.Rules;
using SpectraEngine.Bsp.Tests;
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
/// The bake oracle, over a corpus: cook a map, load it, and assert that every
/// array the runtime received is element-identical to a fresh cache-free compile
/// of the same source.
/// </summary>
/// <remarks>
/// <para><b>This is the guard that replaces <c>ROADMAP.md</c>'s <c>P11b</c>.</b>
/// That milestone pinned the shipping map format with "binary-load, text-save,
/// byte-identical to the original text map", and the test is unsatisfiable for the
/// artifact anybody actually wants: welding, T-junction repair and per-cell carving
/// are not invertible, so <c>.scmap</c> to <c>.smap</c> is not a valid operation
/// and must not be attempted. What CAN be claimed, and is claimed here, is that the
/// bake loses nothing on the way through: the file holds, and the load delivers,
/// exactly what a compile of the same source produces.</para>
/// <para><b>Stated against what the RUNTIME received, not against what the file
/// holds.</b> <c>MapRuleTests</c> already asserts the file's own side of this over
/// one room; the load adds the <c>ASTB</c> remap, the vertex spans handed to
/// <c>CreateMesh</c> and the <c>FlatBspTree</c> built over the mapped bytes, and
/// every one of those is a place the bake's arrays could arrive intact and be
/// delivered wrongly.</para>
/// <para><b>Element-identical means BIT-identical, never a tolerance.</b> The
/// compile cache keys on exact equality and the determinism oracles compare bytes,
/// so a comparison here that forgave a last-place difference would forgive exactly
/// the class of bug this file exists to catch - and it would forgive the two
/// float values that compare equal and are not the same number, <c>0f</c> against
/// <c>-0f</c>, which is a real bit in a real vertex buffer.</para>
/// <para><b>The corpus is the coverage.</b> One room exercises one submesh
/// directory of two entries and no cell that carries a tree without a mesh, so
/// three quarters of the format's shapes are never written by it. See
/// <see cref="BakeCorpus"/> for what each fixture alone reaches, and
/// <see cref="Every_fixture_exercises_what_it_claims"/> for the measurement that
/// stops a fixture quietly ceasing to bite.</para>
/// </remarks>
[Trait("Suite", "Determinism")]
public class BakeOracleTests
{
    // --- the oracle -----------------------------------------------------------

    [Theory]
    [InlineData(BakeCorpus.FlushCoplanarRoom)]
    [InlineData(BakeCorpus.Cavities)]
    [InlineData(BakeCorpus.Palette)]
    [InlineData(BakeCorpus.Sprawl)]
    public void The_loaded_geometry_is_bit_identical_to_a_fresh_cache_free_compile(string fixture)
    {
        using Baked baked = Bake(fixture);

        // Not a blind instrument. A bake that emitted nothing would satisfy every
        // loop below by iterating none of them.
        baked.Fresh.ChunkMeshes.Count.ShouldBeGreaterThan(0, "the fixture compiled to no geometry");
        baked.Scene.StaticWorldChunkMeshes.Count.ShouldBe(baked.Fresh.ChunkMeshes.Count);

        // The loader's own arithmetic, which the arrays below do not cover: this
        // is the number the double-geometry guard is graded on, so a load that
        // counted a submesh twice would report a level drawn twice while every
        // array it uploaded was still correct.
        baked.Report.TriangleCount.ShouldBe(Triangles(baked.Fresh));

        foreach (ChunkMesh expected in baked.Fresh.ChunkMeshes)
        {
            baked.Scene
                .TryGetStaticWorldChunkMesh(expected.Coord, out StaticWorldChunkMesh loaded)
                .ShouldBeTrue($"cell {expected.Coord} is missing from the loaded world");

            ShouldBeBits(loaded.RenderBounds.Min, expected.RenderBounds.Min, $"{expected.Coord} bounds min");
            ShouldBeBits(loaded.RenderBounds.Max, expected.RenderBounds.Max, $"{expected.Coord} bounds max");

            // The directory as a whole: same length, and no material named twice,
            // which is what makes the per-material match below a bijection rather
            // than a lookup that happens to find something.
            loaded.Submeshes.Length.ShouldBe(
                expected.Submeshes.Count, $"cell {expected.Coord} submesh count");

            loaded.Submeshes.Select(s => s.SourceMaterial).Distinct().Count()
                .ShouldBe(loaded.Submeshes.Length, $"cell {expected.Coord} names a material twice");

            // Matched by MATERIAL, never positionally: the file is in ascending
            // asset-table row and the compile is in ascending material id, and
            // those are different orders on purpose. The Palette fixture is the
            // one that makes them genuinely differ.
            foreach (ChunkSubmesh submesh in expected.Submeshes)
            {
                StaticWorldSubmesh actual = loaded.Submeshes
                    .Single(s => s.SourceMaterial == submesh.Material);

                var mesh = (FakeMesh)actual.Mesh;
                ShouldBeBits(
                    mesh.VertexData, submesh.Vertices,
                    $"{expected.Coord} {Describe(submesh.Material)} vertices");

                mesh.IndexData.ShouldBe(
                    submesh.Indices, $"{expected.Coord} {Describe(submesh.Material)} indices");
            }
        }
    }

    [Theory]
    [InlineData(BakeCorpus.FlushCoplanarRoom)]
    [InlineData(BakeCorpus.Cavities)]
    [InlineData(BakeCorpus.Palette)]
    [InlineData(BakeCorpus.Sprawl)]
    public void The_loaded_bsp_nodes_are_bit_identical_to_a_fresh_flatten(string fixture)
    {
        using Baked baked = Bake(fixture);

        CompiledStaticWorld adopted = baked.Scene.CompiledStaticWorld.ShouldNotBeNull();
        IReadOnlyList<WorldChunk> cells = baked.Fresh.Chunks.OrderedChunks;

        // The chunk directory is written in ChunkCoord.CompareTo order and read
        // back in file order, which is the same order OrderedChunks is in, so the
        // two lists are compared position for position rather than by lookup: a
        // cell that moved is a defect here and not something to paper over with a
        // dictionary.
        adopted.Chunks.Count.ShouldBe(cells.Count, "cell count");
        adopted.Chunks.Count.ShouldBeGreaterThan(0);

        for (int i = 0; i < cells.Count; i++)
        {
            WorldChunk cell = cells[i];
            CompiledStaticWorldChunk loaded = adopted.Chunks[i];

            loaded.Coord.ShouldBe(cell.Coord, $"cell {i} is out of order");

            // A cell nothing draws in is never culled - its directory entry says
            // MeshSize zero - so the bake writes the CELL CUBE rather than a
            // fabricated box. Only the Sprawl fixture reaches this at all, which
            // is the one shape a single-cell map structurally cannot have.
            if (loaded.TriangleCount == 0)
            {
                ShouldBeBits(loaded.RenderBounds.Min, cell.Coord.Bounds.Min, $"cell {cell.Coord} empty min");
                ShouldBeBits(loaded.RenderBounds.Max, cell.Coord.Bounds.Max, $"cell {cell.Coord} empty max");
            }

            if (cell.Bsp is null)
            {
                loaded.Bsp.ShouldBeNull($"cell {cell.Coord} grew a tree the compile did not build");
                continue;
            }

            FlatBspNode[] expected = BspFlattener.Flatten(cell.Bsp, out int rootIndex);
            FlatBspTree tree = loaded.Bsp.ShouldNotBeNull(
                $"cell {cell.Coord} lost the tree the compile built for it");

            tree.RootIndex.ShouldBe(rootIndex, $"cell {cell.Coord} root");
            tree.NodeCount.ShouldBe(expected.Length, $"cell {cell.Coord} node count");

            ReadOnlySpan<FlatBspNode> actual = tree.Nodes.Span;
            for (int n = 0; n < expected.Length; n++)
            {
                string what = $"cell {cell.Coord} node {n}";

                // The plane bit for bit: a split plane an ulp out of place moves
                // the boundary between solid and empty by a distance no point
                // query in a test is going to be standing on, so a value
                // comparison here would pass a tree that answers differently
                // somewhere nobody looked.
                ShouldBeBits(actual[n].Plane.Normal, expected[n].Plane.Normal, $"{what} normal");
                ShouldBeBits(actual[n].Plane.D, expected[n].Plane.D, $"{what} d");

                actual[n].Front.ShouldBe(expected[n].Front, $"{what} front");
                actual[n].Back.ShouldBe(expected[n].Back, $"{what} back");
            }
        }
    }

    [Theory]
    [InlineData(BakeCorpus.FlushCoplanarRoom)]
    [InlineData(BakeCorpus.Cavities)]
    [InlineData(BakeCorpus.Palette)]
    [InlineData(BakeCorpus.Sprawl)]
    public void The_adopted_trees_answer_a_LATTICE_of_points_exactly_as_a_fresh_compile_does(string fixture)
    {
        // Element-identical nodes make this redundant on paper and it is kept
        // anyway, because the two claims fail differently: identical arrays
        // routed to the wrong cell answer wrongly everywhere, and only a query
        // that goes through ChunkCoord.FromPosition on both sides can see it. A
        // lattice rather than a handful of probes, because a hand-placed point is
        // exactly where somebody already knows the answer.
        using Baked baked = Bake(fixture);

        CompiledStaticWorld adopted = baked.Scene.CompiledStaticWorld.ShouldNotBeNull();
        Aabb bounds = WorldBounds(baked.Fresh);

        int solid = 0;
        int empty = 0;
        const int Steps = 13;

        for (int x = 0; x < Steps; x++)
        {
            for (int y = 0; y < Steps; y++)
            {
                for (int z = 0; z < Steps; z++)
                {
                    var point = new Vector3(
                        Lerp(bounds.Min.X, bounds.Max.X, x, Steps),
                        Lerp(bounds.Min.Y, bounds.Max.Y, y, Steps),
                        Lerp(bounds.Min.Z, bounds.Max.Z, z, Steps));

                    bool expected = baked.Fresh.ContainsPoint(point);
                    adopted.ContainsPoint(point).ShouldBe(expected, $"the baked world disagrees at {point}");

                    if (expected) solid++;
                    else empty++;
                }
            }
        }

        // Not a blind instrument: a world that answered one way everywhere would
        // agree with itself perfectly.
        solid.ShouldBeGreaterThan(0, "the lattice never landed inside the level");
        empty.ShouldBeGreaterThan(0, "the lattice never landed outside the level");
    }

    [Theory]
    [InlineData(BakeCorpus.FlushCoplanarRoom)]
    [InlineData(BakeCorpus.Cavities)]
    [InlineData(BakeCorpus.Palette)]
    [InlineData(BakeCorpus.Sprawl)]
    public void The_submesh_directory_is_in_ascending_asset_order_in_every_cell(string fixture)
    {
        using Baked baked = Bake(fixture);

        ScmapProbe map = ScmapProbe.Read(baked.File);
        var seen = new List<uint>();

        foreach (ScmapProbe.CellGeometry cell in map.Geometry)
        {
            for (int s = 1; s < cell.Submeshes.Count; s++)
            {
                cell.Submeshes[s].AssetIndex.ShouldBeGreaterThan(
                    cell.Submeshes[s - 1].AssetIndex,
                    $"cell ({cell.X},{cell.Y},{cell.Z}) directory is not strictly ascending");
            }

            foreach (ScmapProbe.SubmeshCopy submesh in cell.Submeshes) seen.Add(submesh.AssetIndex);
        }

        // A sort over one-entry directories proves nothing about a sort, so the
        // fixture has to have produced a directory with something to order.
        seen.Count.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData(BakeCorpus.FlushCoplanarRoom)]
    [InlineData(BakeCorpus.Cavities)]
    [InlineData(BakeCorpus.Palette)]
    [InlineData(BakeCorpus.Sprawl)]
    public void Loading_any_fixture_in_the_corpus_runs_no_carve_at_all(string fixture)
    {
        // The stage's own claim, restated per fixture rather than per room: a
        // shipped game runs zero CSG, and a re-carved world draws the same walls
        // twice with nothing in a frame reporting it.
        using Baked baked = Bake(fixture);

        baked.CarvesDuringLoad.ShouldBe(0, "a compiled map must reach the GPU with no CSG at all");
        baked.Scene.StaticWorld.ShouldBeNull();
        baked.Scene.StaticWorldCompileCount.ShouldBe(0);
    }

    // --- the corpus is not four copies of one test ----------------------------

    [Theory]
    [InlineData(BakeCorpus.FlushCoplanarRoom)]
    [InlineData(BakeCorpus.Cavities)]
    [InlineData(BakeCorpus.Palette)]
    [InlineData(BakeCorpus.Sprawl)]
    public void Every_fixture_exercises_what_it_claims(string fixture)
    {
        // MEASURED off the compile, never trusted from the comment beside the
        // fixture. A fixture that has quietly stopped biting - a cut that no
        // longer reaches its solid, a box that drifted inside one cell - reports
        // coverage that does not exist, which is worse than not being written.
        using Baked baked = Bake(fixture);

        CompiledStaticWorld adopted = baked.Scene.CompiledStaticWorld.ShouldNotBeNull();
        int widest = baked.Fresh.ChunkMeshes.Max(m => m.Submeshes.Count);
        int treesWithoutMeshes = adopted.Chunks.Count(c => c.Bsp is not null && c.TriangleCount == 0);

        switch (fixture)
        {
            case BakeCorpus.FlushCoplanarRoom:
                // The coincident-plane case, and the two materials that make a
                // directory orderable at all.
                widest.ShouldBeGreaterThanOrEqualTo(2);
                HasSubtractiveBrush(baked.Source).ShouldBeTrue();
                break;

            case BakeCorpus.Cavities:
                HasSubtractiveBrush(baked.Source).ShouldBeTrue();

                // The cuts CUT, measured against the same level with every
                // negative deleted. Without this the whole fixture could be four
                // subtractive brushes floating clear of everything, agreeing
                // perfectly with a compile that also carved nothing.
                CsgWorld uncut = Compile(baked.Corpus.BuildSceneWithoutCuts());
                Triangles(baked.Fresh).ShouldNotBe(
                    Triangles(uncut), "the cuts changed no geometry at all");

                // The sealed pocket is really sealed and the tunnel is really
                // open: empty space in the carved world, solid in the uncut one.
                // That pair is what distinguishes a cavity the seeding built from
                // a hole somebody could have reached from outside.
                foreach (Vector3 inside in new[] { new Vector3(5f, 0f, 5f), Vector3.Zero })
                {
                    baked.Fresh.ContainsPoint(inside).ShouldBeFalse($"nothing was removed at {inside}");
                    uncut.ContainsPoint(inside).ShouldBeTrue($"{inside} was never inside the slab");
                }

                break;

            case BakeCorpus.Palette:
                // Seven in one directory: six painted faces and the bare brush.
                widest.ShouldBeGreaterThanOrEqualTo(7);

                // And the two orders genuinely disagree, which is the whole point
                // of interning backwards. Compare the compile's ascending-id order
                // against the file's ascending-row order over the same cell.
                AssetOrderDiffersFromMaterialIdOrder(baked).ShouldBeTrue(
                    "the file's asset order is the compile's material order, so the sort is untested here");
                break;

            case BakeCorpus.Sprawl:
                adopted.Chunks.Count.ShouldBeGreaterThanOrEqualTo(12, "cells");

                // The shape no single-cell fixture can reach: a cell carrying a
                // tree built from a resident brush it does not own, and therefore
                // no mesh of its own.
                treesWithoutMeshes.ShouldBeGreaterThan(0);

                // One material everywhere, deliberately: this fixture is the
                // control that proves the corpus is not four copies of one test.
                widest.ShouldBe(1);

                // And the tilted brush really tilted. Every other plane in the
                // corpus is a number somebody could have typed, so without this
                // the bit comparison is only ever run over exact binary
                // fractions - which is the one input on which a float bug hides.
                HasObliquePlane(adopted).ShouldBeTrue("nothing in this level is off-axis");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(fixture), fixture, "Unclaimed fixture.");
        }
    }

    [Fact]
    public void The_corpus_is_enumerated_in_one_place()
    {
        // The [InlineData] rows above are the runner's view of the corpus and
        // BakeCorpus.Names is everything else's; two lists is how a fixture gets
        // added and silently never run.
        BakeCorpus.Names.ShouldBe(
            [BakeCorpus.FlushCoplanarRoom, BakeCorpus.Cavities, BakeCorpus.Palette, BakeCorpus.Sprawl]);
    }

    // --- helpers --------------------------------------------------------------

    /// <summary>One fixture, cooked, loaded, and compiled again beside itself.</summary>
    private sealed class Baked : IDisposable
    {
        public required BakeCorpus Corpus { get; init; }
        public required TempProject Project { get; init; }
        public required byte[] File { get; init; }

        /// <summary>The scene the compiled map was adopted into.</summary>
        public required SpectraEngine.Core.Scene.Scene Scene { get; init; }

        public required FakeRenderer Renderer { get; init; }

        /// <summary>The authored scene, re-bound from the bundle on disk.</summary>
        public required SpectraEngine.Core.Scene.Scene Source { get; init; }

        /// <summary>A fresh cache-free compile of that scene: the oracle's other side.</summary>
        public required CsgWorld Fresh { get; init; }

        /// <summary>What the load itself said it did.</summary>
        public required CompiledMapLoadReport Report { get; init; }

        /// <summary>Carves counted across the load, on the loading thread.</summary>
        public required long CarvesDuringLoad { get; init; }

        public void Dispose()
        {
            Scene.ReleaseCompiledStaticWorld(Renderer);
            Project.Dispose();
        }
    }

    private static Baked Bake(string fixture)
    {
        BakeCorpus corpus = BakeCorpus.Fresh(fixture);
        var project = new TempProject();

        try
        {
            MapFixture.WriteBundle(project, "Level.smap", corpus.BuildScene());

            var context = new RuleContext(project.Root, "Maps/Level.smap", CookProfile.Ship);
            new MapRule().Cook(context);

            context.Diagnostics.Count.ShouldBe(
                0, string.Join(Environment.NewLine, context.Diagnostics.Select(d => d.ToString())));

            byte[] file = context.Emissions[0].Payload;

            var renderer = new FakeRenderer();
            var scene = new SpectraEngine.Core.Scene.Scene("empty");

            // Thread-static, so this suite may run beside every other one: a
            // process-wide counter would be moved by any unrelated compile on any
            // thread and a real regression would be indistinguishable from an
            // unlucky schedule.
            long before = Csg.CarveInvocationsOnThisThread;
            CompiledMapLoadReport report =
                CompiledMapLoader.Load(scene, renderer, ContentBlob.CopyOf(file), "Maps/Level.scmap");
            long carves = Csg.CarveInvocationsOnThisThread - before;

            // Re-bound from the bundle ON DISK rather than from the scene object
            // the fixture built, so the round trip through the authored text is
            // inside the oracle rather than beside it.
            MapDocument document = MapBundle.Load(Path.Combine(project.Layout.MapsPath, "Level.smap"));
            var source = new SpectraEngine.Core.Scene.Scene(document.Scene.Name);
            MapSceneBinder.ApplyTo(document, source);

            return new Baked
            {
                Corpus = corpus,
                Project = project,
                File = file,
                Scene = scene,
                Renderer = renderer,
                Source = source,
                Fresh = Compile(source),
                Report = report,
                CarvesDuringLoad = carves,
            };
        }
        catch
        {
            project.Dispose();
            throw;
        }
    }

    private static CsgWorld Compile(SpectraEngine.Core.Scene.Scene scene)
    {
        IReadOnlyList<BrushPlacement> placements =
            scene.CaptureStaticWorldPlacements(out string? defect).ShouldNotBeNull(defect);

        // Cache-free, which is the bake's own rule: the incremental overloads
        // carry state across compiles to make an EDIT cheap, and a build artifact
        // must not depend on build history.
        return CsgWorld.Build(placements);
    }

    // True when some baked split plane points off every axis, which is what a
    // rotated brush produces and an axis-aligned one never can.
    private static bool HasObliquePlane(CompiledStaticWorld world)
    {
        foreach (CompiledStaticWorldChunk chunk in world.Chunks)
        {
            if (chunk.Bsp is not { } tree) continue;

            ReadOnlySpan<FlatBspNode> nodes = tree.Nodes.Span;
            for (int i = 0; i < nodes.Length; i++)
            {
                Vector3 n = nodes[i].Plane.Normal;
                if (n.X != 0f && n.Y != 0f && n.Z != 0f) return true;
            }
        }

        return false;
    }

    private static float Lerp(float min, float max, int step, int steps) =>
        min + ((max - min) * step / (steps - 1));

    // The union of every cell's render bounds, inflated so the lattice lands
    // outside the level as well as inside it.
    private static Aabb WorldBounds(CsgWorld world)
    {
        Aabb bounds = world.ChunkMeshes[0].RenderBounds;
        foreach (ChunkMesh mesh in world.ChunkMeshes)
        {
            bounds = new Aabb(
                Vector3.Min(bounds.Min, mesh.RenderBounds.Min),
                Vector3.Max(bounds.Max, mesh.RenderBounds.Max));
        }

        var margin = new Vector3(1.5f);
        return new Aabb(bounds.Min - margin, bounds.Max + margin);
    }

    private static int Triangles(CsgWorld world)
    {
        int indices = 0;
        foreach (ChunkMesh mesh in world.ChunkMeshes)
        {
            foreach (ChunkSubmesh submesh in mesh.Submeshes) indices += submesh.Indices.Length;
        }

        return indices / 3;
    }

    private static bool HasSubtractiveBrush(SpectraEngine.Core.Scene.Scene scene) =>
        scene.Root.Traverse().Any(n => n.Brush is { Operation: BrushOperation.Subtractive });

    // True when the file's ascending-asset-row order over some cell is a real
    // permutation of the compile's ascending-material-id order for the same cell.
    private static bool AssetOrderDiffersFromMaterialIdOrder(Baked baked)
    {
        ScmapProbe map = ScmapProbe.Read(baked.File);

        foreach (ChunkMesh mesh in baked.Fresh.ChunkMeshes)
        {
            ScmapProbe.CellGeometry? cell = map.Geometry.SingleOrDefault(
                c => c.X == mesh.Coord.X && c.Y == mesh.Coord.Y && c.Z == mesh.Coord.Z);

            if (cell is null || cell.Submeshes.Count < 2) continue;

            // The compile's own order, expressed as the asset rows it would land
            // on. Equal to the file's order exactly when the sort was the
            // identity.
            var asCompiled = mesh.Submeshes.Select(s => RowOf(map, s.Material)).ToArray();
            var asWritten = cell.Submeshes.Select(s => s.AssetIndex).ToArray();

            if (!asCompiled.SequenceEqual(asWritten)) return true;
        }

        return false;
    }

    private static uint RowOf(ScmapProbe map, MaterialRef material)
    {
        if (material.IsDefault) return ScmapFormat.NoAssetIndex;

        MaterialRegistry.TryGetPath(material, out string path).ShouldBeTrue();
        int row = map.Assets.FindIndex(
            a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase));

        row.ShouldBeGreaterThanOrEqualTo(0, $"'{path}' has no asset row");
        return (uint)row;
    }

    private static string Describe(MaterialRef material) =>
        material.IsDefault || !MaterialRegistry.TryGetPath(material, out string path)
            ? "(default material)"
            : path;

    // --- bit comparisons ------------------------------------------------------

    private static void ShouldBeBits(float[] actual, float[] expected, string what)
    {
        actual.Length.ShouldBe(expected.Length, $"{what}: length");
        for (int i = 0; i < expected.Length; i++) ShouldBeBits(actual[i], expected[i], $"{what}[{i}]");
    }

    private static void ShouldBeBits(Vector3 actual, Vector3 expected, string what)
    {
        ShouldBeBits(actual.X, expected.X, $"{what}.x");
        ShouldBeBits(actual.Y, expected.Y, $"{what}.y");
        ShouldBeBits(actual.Z, expected.Z, $"{what}.z");
    }

    /// <summary>
    /// Raw bits, never <c>==</c>.
    /// </summary>
    /// <remarks>
    /// Two floats can compare equal and be different numbers - <c>0f</c> and
    /// <c>-0f</c> - and two can be the same number and compare unequal, which is
    /// every NaN. A cooked vertex buffer is bytes, so the comparison that matches
    /// what the format promises is the one over bits.
    /// </remarks>
    private static void ShouldBeBits(float actual, float expected, string what)
    {
        int actualBits = BitConverter.SingleToInt32Bits(actual);
        int expectedBits = BitConverter.SingleToInt32Bits(expected);

        if (actualBits == expectedBits) return;

        actualBits.ShouldBe(
            expectedBits,
            $"{what}: got {actual:R} (0x{actualBits:X8}), expected {expected:R} (0x{expectedBits:X8})");
    }
}
