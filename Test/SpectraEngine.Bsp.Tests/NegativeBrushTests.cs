using System.Collections.Generic;
using System.Numerics;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Bsp;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// Subtractive brushes. The compiled solid is <c>⋃{additive} \ ⋃{subtractive}</c>,
/// regularized, evaluated as an unordered set expression — so a negative brush
/// emits no skin of its own and instead induces <em>cavity walls</em>, the
/// inward-facing boundary of the removed region, seeded into each brush it cuts.
/// </summary>
/// <remarks>
/// <para>
/// The tests are written against <see cref="CsgWorld.ContainsPoint"/> rather
/// than against surface counts, deliberately. The design's own review pass
/// found two defects that emit <em>zero mis-oriented polygons</em> — one
/// emitted nothing at all on the offending plane, the other left a gap between
/// two correctly-oriented surfaces — so any test that counts or inspects
/// surfaces passes on both. What actually breaks is <em>closure</em>, and
/// closure is only observable through the solidity the BSP derives.
/// </para>
/// <para>
/// Boxes are authored in world coordinates through
/// <see cref="Brush.CreateBox(Vector3, Vector3)"/>, which stores the centering
/// translation in <see cref="Brush.Transform"/> — the brush-list overload of
/// <see cref="CsgWorld.Build(IReadOnlyList{Brush})"/> then captures it.
/// </para>
/// </remarks>
public sealed class NegativeBrushTests
{
    // --- The bit itself -----------------------------------------------------

    [Fact]
    public void A_brush_is_additive_unless_it_says_otherwise()
    {
        Box(-1f, 1f).Operation.ShouldBe(BrushOperation.Additive);
    }

    [Fact]
    public void An_equal_operation_write_returns_the_same_instance()
    {
        // Reference identity is the carve cache's validity key, so returning
        // the same instance on a no-op write is the correct answer rather than
        // an optimisation: nothing changed, so the cached carve stays valid.
        Brush brush = Box(-1f, 1f);

        brush.WithOperation(BrushOperation.Additive).ShouldBeSameAs(brush);
    }

    [Fact]
    public void A_changed_operation_returns_a_new_instance_with_identical_geometry()
    {
        Brush additive = Box(-1f, 1f);
        Brush subtractive = additive.WithOperation(BrushOperation.Subtractive);

        subtractive.ShouldNotBeSameAs(additive);
        subtractive.Operation.ShouldBe(BrushOperation.Subtractive);
        subtractive.LocalPlanes.Count.ShouldBe(additive.LocalPlanes.Count);
        subtractive.LocalFaces.Count.ShouldBe(additive.LocalFaces.Count);
        subtractive.LocalBounds.Min.ShouldBe(additive.LocalBounds.Min);
        subtractive.LocalBounds.Max.ShouldBe(additive.LocalBounds.Max);
        subtractive.Transform.ShouldBe(additive.Transform);
    }

    [Fact]
    public void The_operation_survives_a_resize()
    {
        // The highest-value small test in the set: if WithScaledExtents drops
        // the operation, RESIZING A HOLE TURNS IT INTO A SOLID BLOCK — silently,
        // and only once somebody drags the resize gizmo.
        Brush hole = Box(-1f, 1f).WithOperation(BrushOperation.Subtractive);

        hole.WithScaledExtents(new Vector3(2f, 2f, 2f)).Operation
            .ShouldBe(BrushOperation.Subtractive);
    }

    [Fact]
    public void The_operation_survives_a_retexture()
    {
        // The same silent failure, reached through the material picker instead.
        Brush hole = Box(-1f, 1f).WithOperation(BrushOperation.Subtractive);

        hole.WithFaceMaterial(0, MaterialRegistry.Intern("Materials/negative_retexture.spectramat")).Operation
            .ShouldBe(BrushOperation.Subtractive);
    }

    // --- Polygon.Flipped ----------------------------------------------------

    [Fact]
    public void Flipping_a_polygon_reverses_the_winding_AND_negates_the_plane()
    {
        // Both channels or neither. Flipping one without the other produces two
        // different wrong worlds — geometry that renders inside-out but reads
        // solid, or reads inverted but renders correctly.
        var verts = new[]
        {
            new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f),
            new Vector3(1f, 1f, 0f), new Vector3(0f, 1f, 0f),
        };
        var polygon = new Polygon(verts, new Plane(new Vector3(0f, 0f, 1f), 0f));

        Polygon flipped = polygon.Flipped();

        flipped.Surface.Normal.ShouldBe(new Vector3(0f, 0f, -1f));
        flipped.VertexCount.ShouldBe(4);
        for (int i = 0; i < 4; i++)
            flipped.Vertices[i].ShouldBe(verts[3 - i]);
    }

    // --- The subtraction ----------------------------------------------------

    [Fact]
    public void A_negative_brush_removes_solid()
    {
        // A 2-unit notch bitten out of the +x end of a 10-unit bar.
        CsgWorld world = Build(
            Box(new Vector3(0f, 0f, 0f), new Vector3(10f, 2f, 2f)),
            Negative(new Vector3(8f, 0f, 0f), new Vector3(10f, 2f, 2f)));

        world.ContainsPoint(new Vector3(5f, 1f, 1f)).ShouldBeTrue("the uncut part is still solid");
        world.ContainsPoint(new Vector3(9f, 1f, 1f)).ShouldBeFalse("the notch was removed");
    }

    [Fact]
    public void A_negative_brush_emits_no_skin_of_its_own()
    {
        // A negative floating in empty space removes nothing and adds nothing.
        // Its carved array is length 0 by invariant, so there is no world at all.
        CsgWorld world = Build(Negative(new Vector3(0f, 0f, 0f), new Vector3(2f, 2f, 2f)));

        world.Surfaces.Count.ShouldBe(0);
    }

    [Fact]
    public void A_cavity_fully_inside_a_solid_is_hollow_and_still_closed()
    {
        // The hardest closure case: the negative touches none of the cut
        // brush's own planes, so every wall survives its clip and the cavity is
        // bounded entirely by new geometry.
        CsgWorld world = Build(
            Box(new Vector3(0f, 0f, 0f), new Vector3(10f, 10f, 10f)),
            Negative(new Vector3(4f, 4f, 4f), new Vector3(6f, 6f, 6f)));

        world.ContainsPoint(new Vector3(5f, 5f, 5f)).ShouldBeFalse("the cavity is hollow");
        world.ContainsPoint(new Vector3(2f, 5f, 5f)).ShouldBeTrue("the shell is still solid");
        world.ContainsPoint(new Vector3(5f, 2f, 5f)).ShouldBeTrue();
        world.ContainsPoint(new Vector3(5f, 5f, 8f)).ShouldBeTrue();
        world.ContainsPoint(new Vector3(15f, 5f, 5f)).ShouldBeFalse("outside is still outside");
    }

    [Fact]
    public void A_flush_through_cut_opens_a_doorway_through_both_faces()
    {
        // The negative's ±z planes coincide EXACTLY with the slab's, so both
        // faces hit the same-facing coplanar row simultaneously and the hole
        // opens through both surfaces. This is the archetypal authored doorway
        // and the case unmodified coplanar handling gets wrong.
        CsgWorld world = Build(
            Box(new Vector3(0f, 0f, 0f), new Vector3(10f, 8f, 1f)),
            Negative(new Vector3(4f, 0f, 0f), new Vector3(6f, 5f, 1f)));

        world.ContainsPoint(new Vector3(5f, 2f, 0.5f)).ShouldBeFalse("the doorway is open");
        world.ContainsPoint(new Vector3(5f, 7f, 0.5f)).ShouldBeTrue("the lintel above it is solid");
        world.ContainsPoint(new Vector3(1f, 2f, 0.5f)).ShouldBeTrue("the jamb beside it is solid");
        world.ContainsPoint(new Vector3(9f, 2f, 0.5f)).ShouldBeTrue();
    }

    [Fact]
    public void A_negative_resting_on_a_face_removes_nothing()
    {
        // The flush REST: the negative sits on the slab's +y face, coincident
        // and OPPOSITE-facing, and takes nothing away. Unmodified code fails
        // exactly here — CoplanarOrientation returns -1 for that pair and the
        // interior-interface rule deletes the face footprint under a negative
        // that removed nothing, leaving an open solid.
        CsgWorld world = Build(
            Box(new Vector3(0f, 0f, 0f), new Vector3(10f, 2f, 10f)),
            Negative(new Vector3(3f, 2f, 3f), new Vector3(7f, 5f, 7f)));

        world.ContainsPoint(new Vector3(5f, 1f, 5f)).ShouldBeTrue("the slab is untouched");
        world.ContainsPoint(new Vector3(5f, 0.1f, 5f)).ShouldBeTrue();
        world.ContainsPoint(new Vector3(5f, 3f, 5f)).ShouldBeFalse("above it was always air");
    }

    [Fact]
    public void A_notch_under_an_embedded_detail_block_stays_closed()
    {
        // THE FATAL COUNTEREXAMPLE, verbatim from the design's review pass. An
        // earlier rule table dropped the cavity wall on y=0 because an embedded
        // additive brush carried a coincident same-facing plane there, while
        // that brush's own face was independently buried inside the outer one —
        // leaving ZERO surfaces on y=0, which BspTree then reports as empty.
        // Silently: nothing in the pipeline throws on a non-closed surface set.
        CsgWorld world = Build(
            Box(new Vector3(0f, -2f, 0f), new Vector3(10f, 2f, 1f)),        // P
            Box(new Vector3(3f, 0f, 0f), new Vector3(7f, 2f, 1f)),          // P', embedded in P
            Negative(new Vector3(4f, -2f, 0f), new Vector3(6f, 0f, 1f)));   // N, a notch in P's underside

        world.ContainsPoint(new Vector3(5f, -1f, 0.5f)).ShouldBeFalse("the notch is open");
        world.ContainsPoint(new Vector3(5f, 0.5f, 0.5f))
            .ShouldBeTrue("the ceiling over the notch is SOLID — this is the fatal case");
        world.ContainsPoint(new Vector3(5f, 1.5f, 0.5f)).ShouldBeTrue();
        world.ContainsPoint(new Vector3(1f, -1f, 0.5f)).ShouldBeTrue("beside the notch is solid");
    }

    [Fact]
    public void Two_overlapping_negatives_cut_their_union()
    {
        // Subtractives compose with each other only by union, and the two
        // coincident walls their overlap produces must not double up.
        CsgWorld world = Build(
            Box(new Vector3(0f, 0f, 0f), new Vector3(10f, 10f, 2f)),
            Negative(new Vector3(2f, 2f, 0f), new Vector3(6f, 6f, 2f)),
            Negative(new Vector3(4f, 4f, 0f), new Vector3(8f, 8f, 2f)));

        world.ContainsPoint(new Vector3(3f, 3f, 1f)).ShouldBeFalse("first negative");
        world.ContainsPoint(new Vector3(7f, 7f, 1f)).ShouldBeFalse("second negative");
        world.ContainsPoint(new Vector3(5f, 5f, 1f)).ShouldBeFalse("their overlap");
        world.ContainsPoint(new Vector3(9f, 9f, 1f)).ShouldBeTrue("outside both");
        world.ContainsPoint(new Vector3(1f, 1f, 1f)).ShouldBeTrue();
    }

    [Fact]
    public void A_negative_that_swallows_a_brush_annihilates_it()
    {
        // The expressive loss stated rather than hidden: under a set model an
        // additive brush inside a subtractive one disappears entirely, and
        // there is no adding it back afterwards.
        CsgWorld world = Build(
            Box(new Vector3(4f, 4f, 4f), new Vector3(6f, 6f, 6f)),
            Negative(new Vector3(0f, 0f, 0f), new Vector3(10f, 10f, 10f)));

        world.ContainsPoint(new Vector3(5f, 5f, 5f)).ShouldBeFalse();
        world.Surfaces.Count.ShouldBe(0);
    }

    [Fact]
    public void A_negative_cuts_every_additive_brush_it_overlaps()
    {
        // Unordered: every subtractive beats every additive, regardless of
        // placement order or how many it touches.
        CsgWorld world = Build(
            Box(new Vector3(0f, 0f, 0f), new Vector3(4f, 4f, 4f)),
            Negative(new Vector3(3f, 1f, 1f), new Vector3(5f, 3f, 3f)),
            Box(new Vector3(4f, 0f, 0f), new Vector3(8f, 4f, 4f)));

        world.ContainsPoint(new Vector3(3.5f, 2f, 2f)).ShouldBeFalse("cut out of the first");
        world.ContainsPoint(new Vector3(4.5f, 2f, 2f)).ShouldBeFalse("cut out of the second");
        world.ContainsPoint(new Vector3(1f, 2f, 2f)).ShouldBeTrue();
        world.ContainsPoint(new Vector3(7f, 2f, 2f)).ShouldBeTrue();
    }

    [Fact]
    public void Placement_order_does_not_change_the_solid()
    {
        // The unordered rule's whole point: a reparent — which is the only
        // thing that changes traversal order — must never rewrite topology.
        Brush slab = Box(new Vector3(0f, 0f, 0f), new Vector3(10f, 4f, 4f));
        Brush notch = Negative(new Vector3(4f, 0f, 0f), new Vector3(6f, 4f, 4f));

        CsgWorld negativeFirst = Build(notch, slab);
        CsgWorld negativeLast = Build(slab, notch);

        foreach (Vector3 probe in Probes())
        {
            negativeFirst.ContainsPoint(probe)
                .ShouldBe(negativeLast.ContainsPoint(probe), $"order changed the solid at {probe}");
        }
    }

    [Fact]
    public void The_cavity_walls_wear_the_negatives_materials()
    {
        // A hole's walls are the negative's own faces, so they carry the
        // negative's per-face payload — which is what lets an author texture
        // the inside of a doorway by texturing the brush that cut it.
        MaterialRef cutMaterial = MaterialRegistry.Intern("Materials/negative_wall.spectramat");
        Brush slab = Box(new Vector3(0f, 0f, 0f), new Vector3(10f, 4f, 4f));
        Brush notch = Brush
            .CreateBox(new Vector3(4f, 1f, 1f), new Vector3(6f, 3f, 3f), cutMaterial)
            .WithOperation(BrushOperation.Subtractive);

        CsgWorld world = Build(slab, notch);

        bool anyWallWearsIt = false;
        foreach (Polygon surface in world.Surfaces)
        {
            if (surface.Face.Material == cutMaterial)
                anyWallWearsIt = true;
        }
        anyWallWearsIt.ShouldBeTrue("no surface carries the negative's material");
    }

    [Fact]
    public void A_world_with_no_negative_compiles_exactly_as_before()
    {
        // The non-regression pin, positional rather than argued: with no
        // subtractive brush anywhere, no wall seeds are produced, every carver
        // takes the additive row, and walls would have been appended AFTER the
        // faces anyway — so the emitted array is positionally identical.
        Brush a = Box(new Vector3(0f, 0f, 0f), new Vector3(4f, 4f, 4f));
        Brush b = Box(new Vector3(2f, 2f, 2f), new Vector3(6f, 6f, 6f));

        CsgWorld world = Build(a, b);

        world.ContainsPoint(new Vector3(1f, 1f, 1f)).ShouldBeTrue();
        world.ContainsPoint(new Vector3(5f, 5f, 5f)).ShouldBeTrue();
        world.ContainsPoint(new Vector3(3f, 3f, 3f)).ShouldBeTrue("the union merged");
        world.ContainsPoint(new Vector3(7f, 7f, 7f)).ShouldBeFalse();
    }

    [Fact]
    public void A_cut_far_from_the_origin_behaves_identically()
    {
        // The open-world pillar advertises +8,000 units, where cross-frame
        // cancellation error is comparable to the carve epsilons — so every
        // flush case is repeated out there.
        const float O = 8000f;
        CsgWorld world = Build(
            Box(new Vector3(O, 0f, 0f), new Vector3(O + 10f, 8f, 1f)),
            Negative(new Vector3(O + 4f, 0f, 0f), new Vector3(O + 6f, 5f, 1f)));

        world.ContainsPoint(new Vector3(O + 5f, 2f, 0.5f)).ShouldBeFalse("the doorway is open");
        world.ContainsPoint(new Vector3(O + 5f, 7f, 0.5f)).ShouldBeTrue("the lintel is solid");
        world.ContainsPoint(new Vector3(O + 1f, 2f, 0.5f)).ShouldBeTrue();
    }

    // --- Helpers ------------------------------------------------------------

    private static IEnumerable<Vector3> Probes()
    {
        for (float x = -1f; x <= 11f; x += 1.5f)
            for (float y = -1f; y <= 5f; y += 1.5f)
                for (float z = -1f; z <= 5f; z += 1.5f)
                    yield return new Vector3(x, y, z);
    }

    private static CsgWorld Build(params Brush[] brushes) => CsgWorld.Build(brushes);

    private static Brush Box(float min, float max) =>
        Brush.CreateBox(new Vector3(min, min, min), new Vector3(max, max, max));

    private static Brush Box(Vector3 min, Vector3 max) => Brush.CreateBox(min, max);

    private static Brush Negative(Vector3 min, Vector3 max) =>
        Brush.CreateBox(min, max).WithOperation(BrushOperation.Subtractive);
}
