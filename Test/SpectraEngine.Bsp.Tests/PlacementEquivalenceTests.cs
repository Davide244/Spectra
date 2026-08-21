using System.Numerics;
using SpectraEngine.Core.Bsp;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The brush-list overloads of <see cref="Csg.Carve(IReadOnlyList{Brush})"/>
/// and <see cref="CsgWorld.Build(IReadOnlyList{Brush})"/> are documented as
/// pure conveniences over the <see cref="BrushPlacement"/> overloads: carving
/// a brush at a snapshot transform must be indistinguishable from carving it
/// at its own <see cref="Brush.Transform"/>. That equivalence is what lets the
/// async static-world compile hand an immutable placement snapshot to a
/// background thread and still produce exactly the world the live brushes
/// describe — so it is pinned here bit-for-bit, together with the determinism
/// the swap-compare logic in tests (and future editor diffing) relies on.
/// </summary>
public sealed class PlacementEquivalenceTests
{
    [Fact]
    public void Carve_over_placements_matches_carve_over_brush_transforms()
    {
        (Brush[] brushes, BrushPlacement[] placements) = CreateEquivalentSets();

        Polygon[] viaTransforms = Csg.Carve(brushes);
        Polygon[] viaPlacements = Csg.Carve(placements);

        viaTransforms.ShouldNotBeEmpty(); // guard against a vacuous comparison
        ShouldBeIdenticalSurfaces(viaTransforms, viaPlacements);
    }

    [Fact]
    public void World_built_from_placements_matches_world_built_from_brush_transforms()
    {
        (Brush[] brushes, BrushPlacement[] placements) = CreateEquivalentSets();

        CsgWorld viaTransforms = CsgWorld.Build(brushes);
        CsgWorld viaPlacements = CsgWorld.Build(placements);

        viaTransforms.Surfaces.ShouldNotBeEmpty();
        ShouldBeIdenticalSurfaces(viaTransforms.Surfaces, viaPlacements.Surfaces);

        (float[] expectedVertices, uint[] expectedIndices) = viaTransforms.BuildMesh();
        (float[] actualVertices, uint[] actualIndices) = viaPlacements.BuildMesh();
        actualVertices.SequenceEqual(expectedVertices).ShouldBeTrue();
        actualIndices.SequenceEqual(expectedIndices).ShouldBeTrue();
    }

    [Fact]
    public void Building_the_same_placements_twice_is_deterministic()
    {
        // The carve parallelises per brush and the welder per polygon, but both
        // write index-addressed slots, so scheduling must never reorder output.
        // The mesh arrays are the render-facing artefact — compare those.
        (_, BrushPlacement[] placements) = CreateEquivalentSets();

        CsgWorld first = CsgWorld.Build(placements);
        CsgWorld second = CsgWorld.Build(placements);

        ShouldBeIdenticalSurfaces(first.Surfaces, second.Surfaces);

        (float[] firstVertices, uint[] firstIndices) = first.BuildMesh();
        (float[] secondVertices, uint[] secondIndices) = second.BuildMesh();
        secondVertices.SequenceEqual(firstVertices).ShouldBeTrue();
        secondIndices.SequenceEqual(firstIndices).ShouldBeTrue();
    }

    // One translated box and one yaw-rotated box overlapping it: enough to push
    // both translation and rotation through the transform plumbing, with a real
    // carve (not just pass-through faces) happening in the overlap.
    //
    // The placement set deliberately uses *fresh* Brush instances whose own
    // Transform stays at the CreateBox default: if the placement overload ever
    // read Brush.Transform instead of the placement matrix, every brush would
    // land at the origin and the outputs would visibly diverge.
    private static (Brush[] Brushes, BrushPlacement[] Placements) CreateEquivalentSets()
    {
        Matrix4x4[] transforms =
        [
            Matrix4x4.CreateTranslation(0.5f, 0f, 0f),
            Matrix4x4.CreateRotationY(MathF.PI / 4f) * Matrix4x4.CreateTranslation(1.5f, 0.25f, 0.5f),
        ];

        var brushes = new Brush[transforms.Length];
        var placements = new BrushPlacement[transforms.Length];
        for (int i = 0; i < transforms.Length; i++)
        {
            brushes[i] = CreateCentredUnitBox();
            brushes[i].Transform = transforms[i];
            placements[i] = new BrushPlacement(CreateCentredUnitBox(), transforms[i]);
        }

        return (brushes, placements);
    }

    private static Brush CreateCentredUnitBox() =>
        Brush.CreateBox(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f));

    // Bit-exact comparison on purpose: both paths run the same code over the
    // same float inputs, so any drift indicates a real divergence (an extra
    // transform application, a reordered carve) rather than FP noise.
    private static void ShouldBeIdenticalSurfaces(IReadOnlyList<Polygon> expected, IReadOnlyList<Polygon> actual)
    {
        actual.Count.ShouldBe(expected.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            actual[i].Surface.ShouldBe(expected[i].Surface);
            actual[i].VertexCount.ShouldBe(expected[i].VertexCount);
            for (int v = 0; v < expected[i].VertexCount; v++)
                actual[i].Vertices[v].ShouldBe(expected[i].Vertices[v]);
        }
    }
}
