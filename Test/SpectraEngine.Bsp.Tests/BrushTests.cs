using System.Numerics;
using SpectraEngine.Core.Bsp;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// <see cref="Brush.CreateBox"/> geometry and the brush construction contract:
/// invalid plane sets (too few, unbounded, duplicated, all-empty) must be
/// rejected with <see cref="ArgumentException"/> instead of producing a
/// silently-broken solid — while legitimate extreme shapes (sharp spires far
/// larger than their plane offsets) must be accepted.
/// </summary>
public sealed class BrushTests
{
    [Fact]
    public void CreateBox_produces_six_planes_and_six_quad_faces()
    {
        Brush box = Brush.CreateBox(new Vector3(-1f, -2f, -3f), new Vector3(1f, 2f, 3f));

        box.LocalPlanes.Count.ShouldBe(6);
        box.LocalFaces.Count.ShouldBe(6);
        foreach (Polygon face in box.LocalFaces)
            face.VertexCount.ShouldBe(4);
    }

    [Fact]
    public void CreateBox_planes_are_outward_axis_aligned_at_the_half_extents()
    {
        var half = new Vector3(1f, 2f, 3f);
        Brush box = Brush.CreateBox(-half, half);

        Vector3[] expectedNormals =
            [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        float[] expectedOffsets = [-half.X, -half.X, -half.Y, -half.Y, -half.Z, -half.Z];

        for (int i = 0; i < expectedNormals.Length; i++)
        {
            int matches = 0;
            foreach (Plane plane in box.LocalPlanes)
            {
                if ((plane.Normal - expectedNormals[i]).Length() < 1e-5f)
                {
                    plane.D.ShouldBe(expectedOffsets[i], 1e-5);
                    matches++;
                }
            }
            matches.ShouldBe(1, $"expected exactly one plane with normal {expectedNormals[i]}");
        }

        // Outward normals put the brush centre (the local origin) behind every plane.
        foreach (Plane plane in box.LocalPlanes)
            Plane.DotCoordinate(plane, Vector3.Zero).ShouldBeLessThan(0f);
    }

    [Fact]
    public void CreateBox_faces_lie_on_their_planes_and_wind_ccw_around_the_normal()
    {
        Brush box = Brush.CreateBox(new Vector3(0f, 0f, 0f), new Vector3(2f, 2f, 2f));

        foreach (Polygon face in box.LocalFaces)
        {
            foreach (Vector3 v in face.Vertices)
                Plane.DotCoordinate(face.Surface, v).ShouldBe(0f, 1e-3);

            // The fan normal must agree with the surface normal, or the face
            // would render back-to-front.
            Vector3 crossSum = Vector3.Zero;
            IReadOnlyList<Vector3> verts = face.Vertices;
            for (int i = 1; i + 1 < verts.Count; i++)
                crossSum += Vector3.Cross(verts[i] - verts[0], verts[i + 1] - verts[0]);
            Vector3.Dot(crossSum, face.Surface.Normal).ShouldBeGreaterThan(0f);
        }
    }

    [Fact]
    public void CreateBox_stores_extents_locally_and_position_in_the_transform()
    {
        // Asymmetric box far from the origin: local data must stay centred (the
        // whole point of brush-local frames) while the transform carries position.
        Brush box = Brush.CreateBox(new Vector3(1f, 2f, 3f), new Vector3(3f, 6f, 9f));

        box.LocalBounds.Min.X.ShouldBe(-1f, 1e-3);
        box.LocalBounds.Min.Y.ShouldBe(-2f, 1e-3);
        box.LocalBounds.Min.Z.ShouldBe(-3f, 1e-3);
        box.LocalBounds.Max.X.ShouldBe(1f, 1e-3);
        box.LocalBounds.Max.Y.ShouldBe(2f, 1e-3);
        box.LocalBounds.Max.Z.ShouldBe(3f, 1e-3);

        box.Transform.Translation.X.ShouldBe(2f, 1e-5);
        box.Transform.Translation.Y.ShouldBe(4f, 1e-5);
        box.Transform.Translation.Z.ShouldBe(6f, 1e-5);

        box.WorldBounds.Min.X.ShouldBe(1f, 1e-3);
        box.WorldBounds.Min.Y.ShouldBe(2f, 1e-3);
        box.WorldBounds.Min.Z.ShouldBe(3f, 1e-3);
        box.WorldBounds.Max.X.ShouldBe(3f, 1e-3);
        box.WorldBounds.Max.Y.ShouldBe(6f, 1e-3);
        box.WorldBounds.Max.Z.ShouldBe(9f, 1e-3);
    }

    [Fact]
    public void Sharp_pyramid_with_distant_apex_is_accepted()
    {
        // Square pyramid: 2x2 base centred on the local origin (y = 0), apex
        // at (0, 60, 0). Every normalized plane offset is <= ~1, yet the apex
        // vertex sits 60 units out — a magnitude threshold tied to the plane
        // offsets would misread this perfectly valid closed spire as unbounded
        // (regression test for exactly that false rejection). The constructor
        // normalizes planes, so the side planes are written unnormalized:
        // (±60, 1, 0) and (0, 1, ±60) each pass through the apex and one base
        // edge.
        var spire = new Brush(
        [
            new Plane(new Vector3(60f, 1f, 0f), -60f),  // +X side, through (1, 0, ±1) and the apex
            new Plane(new Vector3(-60f, 1f, 0f), -60f), // -X side
            new Plane(new Vector3(0f, 1f, 60f), -60f),  // +Z side
            new Plane(new Vector3(0f, 1f, -60f), -60f), // -Z side
            new Plane(new Vector3(0f, -1f, 0f), 0f),    // base at y = 0
        ]);

        spire.LocalFaces.Count.ShouldBe(5);

        // The apex must have survived clipping intact.
        spire.LocalBounds.Max.Y.ShouldBe(60f, 1e-2);
        spire.LocalBounds.Min.Y.ShouldBe(0f, 1e-3);
    }

    [Fact]
    public void Open_tube_with_no_end_caps_is_rejected()
    {
        // Four prism side planes closing a 2x2 cross-section but open along
        // BOTH ends of the Z axis (four planes, so the minimum-plane-count
        // check cannot mask the boundedness guard). Every face is a strip
        // running off to the clipper's seed boundary — the seed-sensitivity
        // test must catch that the geometry changes with the seed size.
        Should.Throw<ArgumentException>(() => new Brush(
        [
            new Plane(Vector3.UnitX, -1f),
            new Plane(-Vector3.UnitX, -1f),
            new Plane(Vector3.UnitY, -1f),
            new Plane(-Vector3.UnitY, -1f),
        ]));
    }

    [Fact]
    public void Brush_with_fewer_than_four_planes_is_rejected() =>
        Should.Throw<ArgumentException>(() => new Brush(
        [
            new Plane(Vector3.UnitX, -1f),
            new Plane(-Vector3.UnitX, -1f),
            new Plane(Vector3.UnitY, -1f),
        ]));

    [Fact]
    public void Open_plane_set_missing_a_cap_is_rejected()
    {
        // Five box planes without the +Y cap: the half-space intersection is
        // unbounded, which can never be a valid solid.
        Should.Throw<ArgumentException>(() => new Brush(
        [
            new Plane(Vector3.UnitX, -1f),
            new Plane(-Vector3.UnitX, -1f),
            new Plane(-Vector3.UnitY, -1f),
            new Plane(Vector3.UnitZ, -1f),
            new Plane(-Vector3.UnitZ, -1f),
        ]));
    }

    [Fact]
    public void Duplicated_plane_is_rejected()
    {
        Should.Throw<ArgumentException>(() => new Brush(
        [
            new Plane(Vector3.UnitX, -1f),
            new Plane(-Vector3.UnitX, -1f),
            new Plane(Vector3.UnitY, -1f),
            new Plane(-Vector3.UnitY, -1f),
            new Plane(Vector3.UnitZ, -1f),
            new Plane(-Vector3.UnitZ, -1f),
            new Plane(Vector3.UnitX, -1f), // duplicate of the first plane
        ]));
    }

    [Fact]
    public void Inside_out_plane_set_with_all_empty_faces_is_rejected()
    {
        // All normals flipped inward: the "solid" region (behind every plane)
        // is empty, so every face clips away to nothing.
        Should.Throw<ArgumentException>(() => new Brush(
        [
            new Plane(Vector3.UnitX, 1f),
            new Plane(-Vector3.UnitX, 1f),
            new Plane(Vector3.UnitY, 1f),
            new Plane(-Vector3.UnitY, 1f),
            new Plane(Vector3.UnitZ, 1f),
            new Plane(-Vector3.UnitZ, 1f),
        ]));
    }
}
