using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Maps;
using SpectraEngine.Core.Scene;
using System.Collections.Generic;
using System.Text;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// A real level saved and loaded compiles to the same world, vertex for vertex.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only test in the suite that asks whether a map preserves a
/// LEVEL rather than a graph.</b> The others check that nodes come back with
/// their ids, order and payloads, which is necessary and is not the same claim:
/// a codec can get every node right and still shift a plane by one ulp, drop a
/// face's texture axis, or reorder two coincident brushes, and the result is a
/// level that looks correct, passes every graph assertion, and is a different
/// solid.
/// </para>
/// <para>
/// <b>The fixture is the demo's own obstacle course, not a synthetic scene.</b>
/// <c>DemoPlayArea</c> is authored content with the constructs that actually
/// break codecs in it: subtractive brushes cutting a doorway and a tunnel, a
/// chasm cut clean through a slab, coincident and flush-coplanar faces, a part
/// brush that must stay out of the carve, and stairs and ramps whose planes are
/// not axis-aligned. A hand-built fixture tends to contain exactly the cases its
/// author already thought of.
/// </para>
/// <para>
/// <b>Comparing compiled chunk meshes is what makes it a bit-identity claim.</b>
/// The engine's own determinism oracles compare vertex and index arrays because
/// anything weaker cannot distinguish "the same level" from "a level that
/// renders similarly", and the whole static-world pipeline is built on the
/// promise that the same placements produce the same bytes.
/// </para>
/// </remarks>
public sealed class MapLevelFidelityTests
{
    private static Scene BuildPlayArea()
    {
        var scene = new Scene("PlayArea");
        DemoPlayArea.Build(scene, MaterialRef.Default, MaterialRef.Default, MaterialRef.Default);
        return scene;
    }

    [Fact]
    public void The_demo_play_area_compiles_identically_after_a_save_and_a_load()
    {
        Scene authored = BuildPlayArea();
        authored.RebuildStaticWorld(new FakeRenderer());

        Scene reloaded = new("Empty");
        MapSceneBinder.ApplyTo(
            MapReader.Read(MapWriter.Write(MapSceneBinder.FromScene(authored))), reloaded);
        reloaded.RebuildStaticWorld(new FakeRenderer());

        CsgWorld before = authored.StaticWorld.ShouldNotBeNull();
        CsgWorld after = reloaded.StaticWorld.ShouldNotBeNull();

        // Not vacuous: the course is a real level, and a codec that produced an
        // empty scene would otherwise compare two empty worlds and pass.
        before.ChunkMeshes.Count.ShouldBeGreaterThan(4,
            "the play area must actually compile to something for this comparison to mean anything");

        after.ChunkMeshes.Count.ShouldBe(before.ChunkMeshes.Count, "chunk count");

        for (int c = 0; c < before.ChunkMeshes.Count; c++)
        {
            ChunkMesh a = before.ChunkMeshes[c];
            ChunkMesh b = after.ChunkMeshes[c];

            b.Coord.ShouldBe(a.Coord, $"chunk {c} coordinate");
            b.Submeshes.Count.ShouldBe(a.Submeshes.Count, $"chunk {a.Coord} submesh count");

            for (int s = 0; s < a.Submeshes.Count; s++)
            {
                ChunkSubmesh want = a.Submeshes[s];
                ChunkSubmesh got = b.Submeshes[s];

                got.Material.ShouldBe(want.Material, $"chunk {a.Coord} submesh {s} material");
                got.Vertices.ShouldBe(want.Vertices, $"chunk {a.Coord} submesh {s} vertices");
                got.Indices.ShouldBe(want.Indices, $"chunk {a.Coord} submesh {s} indices");
            }
        }
    }

    [Fact]
    public void The_demo_play_area_survives_a_second_round_trip_byte_for_byte()
    {
        // Save, load, save. The first pass is where a scene may legitimately
        // change the bytes - Brush's constructor re-normalises every plane it is
        // handed, so an authored value can be canonicalised on the way through.
        // From the second pass on, nothing may move: an editor that rewrote part
        // of a map on every open would put a diff in front of the user for
        // opening a file.
        Scene authored = BuildPlayArea();

        byte[] first = MapWriter.Write(MapSceneBinder.FromScene(authored));

        Scene reloaded = new("Empty");
        MapSceneBinder.ApplyTo(MapReader.Read(first), reloaded);
        byte[] second = MapWriter.Write(MapSceneBinder.FromScene(reloaded));

        if (!first.AsSpan().SequenceEqual(second))
        {
            string want = Encoding.UTF8.GetString(first);
            string got = Encoding.UTF8.GetString(second);
            int at = 0;
            while (at < want.Length && at < got.Length && want[at] == got[at]) at++;
            throw new Xunit.Sdk.XunitException(
                $"The level changed on the second save, first at character {at}:\n"
                + $"  expected: {Excerpt(want, at)}\n  actual:   {Excerpt(got, at)}");
        }
    }

    [Fact]
    public void The_part_brush_in_the_course_is_still_a_part_after_a_load()
    {
        // BrushKind is the admission bit and is not inherited or derived, so a
        // codec that lost it would re-admit a part brush to the carve - which
        // changes the compiled world rather than merely mislabelling a node.
        // The chunk comparison above would catch it; this says which bit broke.
        Scene authored = BuildPlayArea();

        Scene reloaded = new("Empty");
        MapSceneBinder.ApplyTo(
            MapReader.Read(MapWriter.Write(MapSceneBinder.FromScene(authored))), reloaded);

        CountKinds(authored.Root, out int authoredParts, out int authoredSubtractive);
        CountKinds(reloaded.Root, out int reloadedParts, out int reloadedSubtractive);

        authoredParts.ShouldBeGreaterThan(0, "the course is supposed to contain a part brush");
        authoredSubtractive.ShouldBeGreaterThan(0, "the course is supposed to contain subtractive brushes");
        reloadedParts.ShouldBe(authoredParts);
        reloadedSubtractive.ShouldBe(authoredSubtractive);
    }

    private static void CountKinds(SceneNode node, out int parts, out int subtractive)
    {
        int p = 0, s = 0;
        var stack = new Stack<SceneNode>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            SceneNode current = stack.Pop();
            if (current.Brush is { } brush)
            {
                if (current.BrushKind == BrushKind.Part) p++;
                if (brush.Operation == BrushOperation.Subtractive) s++;
            }
            foreach (SceneNode child in current.Children) stack.Push(child);
        }
        parts = p;
        subtractive = s;
    }

    private static string Excerpt(string text, int at) =>
        text.Substring(System.Math.Max(0, at - 40), System.Math.Min(80, text.Length - System.Math.Max(0, at - 40)))
            .Replace("\n", "\\n");
}
