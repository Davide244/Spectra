using System.Numerics;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Bsp;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The per-face surface payload (<see cref="FaceSurface"/>): that it survives
/// every polygon-producing stage of the CSG pipeline unchanged, that the
/// DEFAULT payload reproduces the projection the engine used before the payload
/// existed (pinned numerically, so the default path is provably unchanged),
/// that explicit texture axes implement the documented texture-lock semantics,
/// and that a face material change invalidates exactly the brush it belongs to
/// in the incremental carve cache.
/// </summary>
public sealed class FaceSurfaceTests
{
    private static readonly Plane FloorPlane = new(Vector3.UnitY, 0f);

    // A payload nothing could produce by accident: distinct material, oblique
    // axes, non-unit scales, non-zero offsets. If any propagation site drops or
    // rebuilds the payload, one of these seven fields moves.
    private static FaceSurface Marked() => new(
        MaterialRegistry.Intern("Materials/marked.spectramat"),
        Vector3.Normalize(new Vector3(1f, 0f, 2f)),
        Vector3.Normalize(new Vector3(0f, 3f, 1f)),
        uOffset: 0.25f,
        vOffset: -0.75f,
        uScale: 2.5f,
        vScale: 0.5f);

    // ------------------------------------------------------------------
    // (1) Propagation through every polygon-construction site.
    // ------------------------------------------------------------------

    [Fact]
    public void Split_gives_both_fragments_the_parent_payload()
    {
        FaceSurface marked = Marked();
        var quad = new Polygon(
            [new Vector3(-1f, 0f, -1f), new Vector3(1f, 0f, -1f), new Vector3(1f, 0f, 1f), new Vector3(-1f, 0f, 1f)],
            FloorPlane,
            marked);

        // A plane straight through the middle, so both sides are real polygons.
        quad.Split(new Plane(Vector3.UnitX, 0f), out Polygon? front, out Polygon? back);

        front.ShouldNotBeNull().Face.ShouldBe(marked);
        back.ShouldNotBeNull().Face.ShouldBe(marked);
    }

    [Fact]
    public void Split_that_does_not_cut_returns_the_same_instance_with_its_payload()
    {
        FaceSurface marked = Marked();
        var quad = new Polygon(
            [new Vector3(-1f, 0f, -1f), new Vector3(1f, 0f, -1f), new Vector3(1f, 0f, 1f), new Vector3(-1f, 0f, 1f)],
            FloorPlane,
            marked);

        // Entirely in front of the plane: the front output IS the input.
        quad.Split(new Plane(Vector3.UnitX, 10f), out Polygon? front, out Polygon? back);

        front.ShouldBeSameAs(quad);
        back.ShouldBeNull();
        front.ShouldNotBeNull().Face.ShouldBe(marked);
    }

    [Fact]
    public void Snap_leaves_the_payload_untouched()
    {
        FaceSurface marked = Marked();
        var tri = new Polygon(
            [new Vector3(0.30000012f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 1f)],
            FloorPlane,
            marked);

        Polygon[] snapped = VertexSnapper.Snap([tri]);

        snapped[0].Face.ShouldBe(marked);
        snapped[0].Surface.ShouldBe(tri.Surface);
    }

    [Fact]
    public void Weld_carries_the_payload_onto_the_rewritten_polygon()
    {
        FaceSurface marked = Marked();

        // A quad whose (0,0,0)->(2,0,0) edge passes through the candidate's
        // (1,0,0) vertex — a genuine T-junction, so the welder must rebuild the
        // polygon rather than return it as-is.
        var quad = new Polygon(
            [new Vector3(0f, 0f, 0f), new Vector3(2f, 0f, 0f), new Vector3(2f, 0f, 1f), new Vector3(0f, 0f, 1f)],
            FloorPlane,
            marked);
        var candidate = new Polygon(
            [new Vector3(1f, 0f, 0f), new Vector3(1f, 1f, 0f), new Vector3(1f, 0f, -1f)],
            new Plane(Vector3.UnitZ, 0f),
            FaceSurface.Default);

        Polygon[] welded = TJunctionWelder.Weld([quad], [quad, candidate]);

        welded[0].VertexCount.ShouldBe(5, "the T-junction vertex should have been inserted");
        welded[0].ShouldNotBeSameAs(quad);
        welded[0].Face.ShouldBe(marked);
    }

    [Fact]
    public void Weld_with_nothing_to_insert_returns_the_same_instance()
    {
        // The no-op path is load-bearing for the downstream caches: they
        // validate reuse by reference identity, so a gratuitous copy here would
        // invalidate the weld/BSP/mesh caches world-wide on every compile.
        FaceSurface marked = Marked();
        var quad = new Polygon(
            [new Vector3(0f, 0f, 0f), new Vector3(2f, 0f, 0f), new Vector3(2f, 0f, 1f), new Vector3(0f, 0f, 1f)],
            FloorPlane,
            marked);

        Polygon[] welded = TJunctionWelder.Weld([quad]);

        welded[0].ShouldBeSameAs(quad);
        welded[0].Face.ShouldBe(marked);
    }

    [Fact]
    public void Carved_world_surfaces_all_carry_a_face_payload_from_their_source_brush()
    {
        // Two overlapping boxes with distinct materials: every emitted fragment,
        // however finely the carve cut it, must still name the material of the
        // brush face it descends from.
        MaterialRef left = MaterialRegistry.Intern("Materials/carve_left.spectramat");
        MaterialRef right = MaterialRegistry.Intern("Materials/carve_right.spectramat");
        Brush a = Brush.CreateBox(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f), left);
        Brush b = Brush.CreateBox(new Vector3(0f, -1f, -1f), new Vector3(2f, 1f, 1f), right);

        CsgWorld world = CsgWorld.Build([a, b]);

        world.Surfaces.Count.ShouldBeGreaterThan(6);
        foreach (Polygon surface in world.Surfaces)
            (surface.Face.Material == left || surface.Face.Material == right).ShouldBeTrue();

        // Both materials must actually survive the carve (a carve that dropped
        // one brush's skin entirely would pass the loop above vacuously).
        world.Surfaces.ShouldContain(p => p.Face.Material == left);
        world.Surfaces.ShouldContain(p => p.Face.Material == right);
    }

    // ------------------------------------------------------------------
    // (2) The default path is numerically unchanged.
    // ------------------------------------------------------------------

    [Fact]
    public void Default_axes_reproduce_the_previous_dominant_axis_projection_for_an_axis_aligned_box()
    {
        // THE PIN: the UVs of a default (world-aligned) brush must be exactly
        // what the hard-coded projection produced before FaceSurface existed.
        // LegacyUv below is that former code, copied verbatim.
        Brush box = Brush.CreateBox(new Vector3(-1f, -2f, -3f), new Vector3(1f, 2f, 3f));
        CsgWorld world = CsgWorld.Build([box]);
        (float[] vertices, _) = world.BuildMesh();

        int v = 0;
        foreach (Polygon poly in world.Surfaces)
        {
            Vector3 normal = poly.Surface.Normal;
            foreach (Vector3 p in poly.VertexSpan)
            {
                (float expectedU, float expectedV) = LegacyUv(p, normal);
                vertices[v * 8 + 6].ShouldBe(expectedU);
                vertices[v * 8 + 7].ShouldBe(expectedV);
                v++;
            }
        }

        v.ShouldBe(vertices.Length / 8);
    }

    [Fact]
    public void Default_axes_reproduce_the_previous_projection_for_a_rotated_placement_too()
    {
        // World alignment is re-derived from the WORLD normal, so a rotated
        // brush must still match the legacy projection — this is the case a
        // naive "bake the axes at construction and rotate them" design would
        // silently change.
        Brush box = Brush.CreateBox(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f));
        Matrix4x4 placement =
            Matrix4x4.CreateRotationY(0.7f) *
            Matrix4x4.CreateRotationX(0.3f) *
            Matrix4x4.CreateTranslation(4f, -2f, 9f);

        CsgWorld world = CsgWorld.Build([new BrushPlacement(box, placement)]);
        (float[] vertices, _) = world.BuildMesh();

        int v = 0;
        foreach (Polygon poly in world.Surfaces)
        {
            Vector3 normal = poly.Surface.Normal;
            foreach (Vector3 p in poly.VertexSpan)
            {
                (float expectedU, float expectedV) = LegacyUv(p, normal);
                vertices[v * 8 + 6].ShouldBe(expectedU);
                vertices[v * 8 + 7].ShouldBe(expectedV);
                v++;
            }
        }
    }

    [Fact]
    public void Default_payload_is_world_aligned_with_unit_scale_and_no_offset()
    {
        FaceSurface.Default.IsWorldAligned.ShouldBeTrue();
        FaceSurface.Default.Material.IsDefault.ShouldBeTrue();
        FaceSurface.Default.UOffset.ShouldBe(0f);
        FaceSurface.Default.VOffset.ShouldBe(0f);
        FaceSurface.Default.UScale.ShouldBe(1f);
        FaceSurface.Default.VScale.ShouldBe(1f);

        // The all-zeros struct default describes the same projection, so it must
        // compare equal — otherwise every `default(FaceSurface)` would look like
        // a different face to tooling.
        default(FaceSurface).ShouldBe(FaceSurface.Default);
        default(FaceSurface).ComputeUv(new Vector3(3f, 5f, 7f), Vector3.UnitY)
            .ShouldBe(FaceSurface.Default.ComputeUv(new Vector3(3f, 5f, 7f), Vector3.UnitY));
    }

    [Fact]
    public void Zero_or_non_finite_texture_scale_is_rejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new FaceSurface(MaterialRef.Default, Vector3.UnitX, Vector3.UnitY, 0f, 0f, 0f, 1f));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new FaceSurface(MaterialRef.Default, Vector3.UnitX, Vector3.UnitY, 0f, 0f, 1f, float.NaN));
    }

    // ------------------------------------------------------------------
    // (3) Texture axes and texture lock.
    // ------------------------------------------------------------------

    [Fact]
    public void Explicit_axes_scale_and_offset_follow_the_documented_uv_convention()
    {
        // u = dot(p, UAxis) / UScale + UOffset, and likewise for v.
        var face = new FaceSurface(
            MaterialRef.Default, Vector3.UnitX, Vector3.UnitZ,
            uOffset: 0.5f, vOffset: -0.25f, uScale: 4f, vScale: 2f);

        Vector2 uv = face.ComputeUv(new Vector3(8f, 0f, 6f), Vector3.UnitY);

        uv.X.ShouldBe(8f / 4f + 0.5f, 1e-6);
        uv.Y.ShouldBe(6f / 2f - 0.25f, 1e-6);
    }

    [Fact]
    public void World_aligned_axes_pass_through_a_transform_unchanged()
    {
        FaceSurface transformed = FaceSurface.Default.Transformed(
            Matrix4x4.CreateRotationZ(1.1f) * Matrix4x4.CreateTranslation(5f, 6f, 7f));

        transformed.IsWorldAligned.ShouldBeTrue();
        transformed.ShouldBe(FaceSurface.Default);
    }

    [Fact]
    public void Explicit_axes_are_texture_locked_through_a_rigid_transform()
    {
        // The documented semantic: after a rigid move, the moved point wears
        // exactly the UV the original point wore — the texture is glued to the
        // surface, not to the world.
        FaceSurface face = Marked();
        Matrix4x4 rigid =
            Matrix4x4.CreateRotationY(0.9f) *
            Matrix4x4.CreateRotationX(-0.4f) *
            Matrix4x4.CreateTranslation(11f, -3f, 2.5f);

        var localNormal = Vector3.UnitY;
        var localPoint = new Vector3(1.5f, 0f, -2.25f);

        FaceSurface moved = face.Transformed(rigid);
        Vector3 worldPoint = Vector3.Transform(localPoint, rigid);
        Vector3 worldNormal = Vector3.Normalize(Vector3.TransformNormal(localNormal, rigid));

        Vector2 before = face.ComputeUv(localPoint, localNormal);
        Vector2 after = moved.ComputeUv(worldPoint, worldNormal);

        after.X.ShouldBe(before.X, 1e-4);
        after.Y.ShouldBe(before.Y, 1e-4);
        moved.IsWorldAligned.ShouldBeFalse();
        moved.UScale.ShouldBe(face.UScale);
        moved.VScale.ShouldBe(face.VScale);
    }

    [Fact]
    public void Rotated_brush_with_explicit_axes_emits_texture_locked_uvs_in_the_compiled_mesh()
    {
        // End-to-end: an explicit-axis face on a rotated, translated brush must
        // come out of the compile with the axes rotated and the UVs locked.
        var faceAxes = new FaceSurface(
            MaterialRegistry.Intern("Materials/locked.spectramat"),
            Vector3.UnitX, Vector3.UnitZ,
            uOffset: 0f, vOffset: 0f, uScale: 2f, vScale: 2f);

        Brush box = Brush.CreateBox(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f))
            .WithFaceSurface(2, faceAxes); // plane 2 is +Y (see Brush.CreateBox)

        Matrix4x4 placement = Matrix4x4.CreateRotationY(MathF.PI / 2f) * Matrix4x4.CreateTranslation(10f, 0f, 0f);
        CsgWorld world = CsgWorld.Build([new BrushPlacement(box, placement)]);

        Polygon top = world.Surfaces
            .Where(s => s.Face.Material == faceAxes.Material)
            .ToList()
            .ShouldHaveSingleItem();

        // A 90-degree Y rotation maps +X to -Z and +Z to +X.
        top.Face.UAxis.X.ShouldBe(0f, 1e-5);
        top.Face.UAxis.Z.ShouldBe(-1f, 1e-5);
        top.Face.VAxis.X.ShouldBe(1f, 1e-5);
        top.Face.VAxis.Z.ShouldBe(0f, 1e-5);

        // And the lock holds: the world vertex wears the UV its local
        // pre-image wore.
        Matrix4x4.Invert(placement, out Matrix4x4 inverse).ShouldBeTrue();
        foreach (Vector3 worldVertex in top.VertexSpan)
        {
            Vector3 localVertex = Vector3.Transform(worldVertex, inverse);
            Vector2 expected = faceAxes.ComputeUv(localVertex, Vector3.UnitY);
            Vector2 actual = top.Face.ComputeUv(worldVertex, top.Surface.Normal);
            actual.X.ShouldBe(expected.X, 1e-3);
            actual.Y.ShouldBe(expected.Y, 1e-3);
        }
    }

    // ------------------------------------------------------------------
    // (4) Brush per-face materials and cache invalidation.
    // ------------------------------------------------------------------

    [Fact]
    public void CreateBox_assigns_the_material_to_every_face_deterministically()
    {
        MaterialRef material = MaterialRegistry.Intern("Materials/box_all.spectramat");
        Brush box = Brush.CreateBox(-Vector3.One, Vector3.One, material);

        box.FaceSurfaces.Count.ShouldBe(box.LocalPlanes.Count);
        foreach (FaceSurface face in box.FaceSurfaces)
        {
            face.Material.ShouldBe(material);
            face.IsWorldAligned.ShouldBeTrue();
        }
        foreach (Polygon face in box.LocalFaces)
            face.Face.Material.ShouldBe(material);

        // Default overload keeps the engine default material.
        foreach (FaceSurface face in Brush.CreateBox(-Vector3.One, Vector3.One).FaceSurfaces)
            face.Material.IsDefault.ShouldBeTrue();
    }

    [Fact]
    public void WithFaceMaterial_changes_one_face_and_leaves_the_source_brush_untouched()
    {
        MaterialRef baseMaterial = MaterialRegistry.Intern("Materials/base.spectramat");
        MaterialRef accent = MaterialRegistry.Intern("Materials/accent.spectramat");
        Brush original = Brush.CreateBox(-Vector3.One, Vector3.One, baseMaterial);

        Brush retextured = original.WithFaceMaterial(2, accent);

        retextured.ShouldNotBeSameAs(original);
        retextured.FaceSurfaces[2].Material.ShouldBe(accent);
        for (int i = 0; i < retextured.FaceSurfaces.Count; i++)
        {
            if (i != 2)
                retextured.FaceSurfaces[i].Material.ShouldBe(baseMaterial);
        }

        // Immutable-after-construction: the source brush is unchanged.
        foreach (FaceSurface face in original.FaceSurfaces)
            face.Material.ShouldBe(baseMaterial);

        // Geometry is untouched by a retexture — same planes, same faces.
        retextured.LocalFaces.Count.ShouldBe(original.LocalFaces.Count);
        retextured.LocalBounds.Min.ShouldBe(original.LocalBounds.Min);
        retextured.LocalBounds.Max.ShouldBe(original.LocalBounds.Max);

        Should.Throw<ArgumentOutOfRangeException>(() => original.WithFaceMaterial(6, accent));
    }

    [Fact]
    public void Changing_one_face_material_invalidates_exactly_that_brush_in_the_carve_cache()
    {
        // Two boxes far enough apart to share no carvers, so the expected
        // invalidation set is exactly the retextured brush — a brush that DID
        // overlap would also (correctly, conservatively) invalidate its
        // neighbours, which would blur what this test is pinning.
        MaterialRef baseMaterial = MaterialRegistry.Intern("Materials/cache_base.spectramat");
        MaterialRef accent = MaterialRegistry.Intern("Materials/cache_accent.spectramat");

        Brush first = Brush.CreateBox(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f), baseMaterial);
        Brush second = Brush.CreateBox(new Vector3(49f, -1f, -1f), new Vector3(51f, 1f, 1f), baseMaterial);
        List<BrushPlacement> placements =
            [new BrushPlacement(first, first.Transform), new BrushPlacement(second, second.Transform)];

        CsgWorld before = CsgWorld.Build(placements, previousCache: null);
        before.CacheStats.ShouldBe(new CsgCacheStats(Hits: 0, Misses: 2));

        // Retexture the +X face (plane 0) of the first brush only.
        Brush edited = first.WithFaceMaterial(0, accent);
        List<BrushPlacement> next = [new BrushPlacement(edited, edited.Transform), placements[1]];

        CsgWorld after = CsgWorld.Build(next, before.CompileCache);

        // Exactly one miss: the retextured brush. The untouched brush hits.
        after.CacheStats.ShouldBe(new CsgCacheStats(Hits: 1, Misses: 1));

        // And the compiled output carries the new material — a stale cached
        // carve would still be showing the base material here.
        Polygon accentFace = after.Surfaces
            .Where(s => s.Face.Material == accent)
            .ToList()
            .ShouldHaveSingleItem();
        accentFace.Surface.Normal.X.ShouldBe(1f, 1e-5);
        after.Surfaces.Count(s => s.Face.Material == baseMaterial).ShouldBe(after.Surfaces.Count - 1);
    }

    [Fact]
    public void Retextured_brush_compiles_to_the_same_geometry_as_the_original()
    {
        // A material change must move no vertex: the retexture path re-runs
        // brush construction, so this pins that it reproduces the geometry bit
        // for bit rather than re-deriving it slightly differently.
        Brush original = Brush.CreateBox(new Vector3(-1f, -2f, -3f), new Vector3(1f, 2f, 3f));
        Brush retextured = original.WithFaceMaterial(4, MaterialRegistry.Intern("Materials/geom.spectramat"));

        (float[] expectedVertices, uint[] expectedIndices) = CsgWorld.Build([original]).BuildMesh();
        (float[] actualVertices, uint[] actualIndices) = CsgWorld.Build([retextured]).BuildMesh();

        actualIndices.SequenceEqual(expectedIndices).ShouldBeTrue();
        actualVertices.SequenceEqual(expectedVertices).ShouldBeTrue();
    }

    // ------------------------------------------------------------------
    // (5) Material runs — the upload-time handoff.
    // ------------------------------------------------------------------

    [Fact]
    public void Material_runs_partition_the_index_array_in_emission_order()
    {
        MaterialRef baseMaterial = MaterialRegistry.Intern("Materials/run_base.spectramat");
        MaterialRef accent = MaterialRegistry.Intern("Materials/run_accent.spectramat");
        Brush box = Brush.CreateBox(-Vector3.One, Vector3.One, baseMaterial).WithFaceMaterial(3, accent);

        CsgWorld world = CsgWorld.Build([box]);
        (_, uint[] indices) = world.BuildMesh();
        IReadOnlyList<MaterialRun> runs = world.BuildMaterialRuns();

        // Contiguous, gapless, and covering every index exactly once.
        int cursor = 0;
        foreach (MaterialRun run in runs)
        {
            run.IndexStart.ShouldBe(cursor);
            (run.IndexCount % 3).ShouldBe(0);
            cursor += run.IndexCount;
        }
        cursor.ShouldBe(indices.Length);

        runs.ShouldContain(r => r.Material == accent);
        runs.ShouldContain(r => r.Material == baseMaterial);

        // The chunk artifacts carry the same attribution, as real per-material
        // geometry — that is what the render thread actually uploads. Together
        // the submeshes account for every triangle the monolithic mesh has.
        int chunkIndexTotal = 0;
        var chunkMaterials = new List<MaterialRef>();
        foreach (ChunkMesh mesh in world.ChunkMeshes)
        {
            foreach (ChunkSubmesh submesh in mesh.Submeshes)
            {
                submesh.Indices.Length.ShouldBeGreaterThan(0); // an empty submesh would be a wasted draw
                (submesh.Indices.Length % 3).ShouldBe(0);
                chunkMaterials.Add(submesh.Material);
                chunkIndexTotal += submesh.Indices.Length;
            }
        }
        chunkIndexTotal.ShouldBe(indices.Length);
        chunkMaterials.ShouldContain(accent);
        chunkMaterials.ShouldContain(baseMaterial);
    }

    [Fact]
    public void A_single_material_world_is_exactly_one_run()
    {
        CsgWorld world = CsgWorld.Build([Brush.CreateBox(-Vector3.One, Vector3.One)]);
        (_, uint[] indices) = world.BuildMesh();

        MaterialRun run = world.BuildMaterialRuns().ShouldHaveSingleItem();
        run.Material.IsDefault.ShouldBeTrue();
        run.IndexStart.ShouldBe(0);
        run.IndexCount.ShouldBe(indices.Length);
    }

    // ------------------------------------------------------------------
    // (6) Equivalence and determinism WITH the payload present. The existing
    // oracles all run default-material worlds; these extend them to a world
    // where the payload actually varies from face to face.
    // ------------------------------------------------------------------

    [Fact]
    public void Mixed_material_world_compiles_bit_identically_incrementally_and_from_scratch()
    {
        // A 3x3 grid of overlapping boxes, every brush and several individual
        // faces wearing different materials, walked through moves AND a
        // retexture — each step compared bit-for-bit against a from-scratch
        // compile of the same placements, exactly as the carve-cache oracle
        // does for geometry.
        List<BrushPlacement> placements = MakeMixedGrid(3);
        CsgWorld incremental = CsgWorld.Build(placements, previousCache: null);

        for (int move = 0; move < 6; move++)
        {
            int index = move % placements.Count;
            if (move % 3 == 2)
            {
                // A retexture edit: same geometry, new payload on one face.
                Brush edited = placements[index].Brush.WithFaceMaterial(
                    move % 6, MaterialRegistry.Intern($"Materials/walk_{move}.spectramat"));
                placements[index] = placements[index] with { Brush = edited };
            }
            else
            {
                placements[index] = placements[index] with
                {
                    Transform = placements[index].Transform *
                        Matrix4x4.CreateTranslation(0.05f * (move + 1), 0f, 0.03f * move),
                };
            }

            incremental = CsgWorld.Build(
                placements, dirtyCells: null, incremental.CompileCache, incremental.WeldCache);
            CsgWorld scratch = CsgWorld.Build(placements);

            incremental.Surfaces.Count.ShouldBe(scratch.Surfaces.Count, $"surface count at move #{move}");
            for (int i = 0; i < scratch.Surfaces.Count; i++)
            {
                incremental.Surfaces[i].Surface.ShouldBe(scratch.Surfaces[i].Surface, $"plane #{i}, move #{move}");
                incremental.Surfaces[i].Face.ShouldBe(scratch.Surfaces[i].Face, $"payload #{i}, move #{move}");
            }

            (float[] expectedVertices, uint[] expectedIndices) = scratch.BuildMesh();
            (float[] actualVertices, uint[] actualIndices) = incremental.BuildMesh();
            actualVertices.SequenceEqual(expectedVertices).ShouldBeTrue($"mesh vertices diverged at move #{move}");
            actualIndices.SequenceEqual(expectedIndices).ShouldBeTrue($"mesh indices diverged at move #{move}");
            incremental.BuildMaterialRuns().SequenceEqual(scratch.BuildMaterialRuns())
                .ShouldBeTrue($"material runs diverged at move #{move}");
        }
    }

    [Fact]
    public void Mixed_material_world_recompiles_bit_identically_twice()
    {
        // Determinism: the same placements compiled twice must produce the same
        // payload-bearing surfaces and the same mesh arrays, byte for byte.
        List<BrushPlacement> placements = MakeMixedGrid(3);

        CsgWorld first = CsgWorld.Build(placements);
        CsgWorld second = CsgWorld.Build(placements);

        first.Surfaces.Count.ShouldBe(second.Surfaces.Count);
        for (int i = 0; i < first.Surfaces.Count; i++)
            first.Surfaces[i].Face.ShouldBe(second.Surfaces[i].Face);

        (float[] a, uint[] ai) = first.BuildMesh();
        (float[] b, uint[] bi) = second.BuildMesh();
        a.SequenceEqual(b).ShouldBeTrue();
        ai.SequenceEqual(bi).ShouldBeTrue();
        first.BuildMaterialRuns().SequenceEqual(second.BuildMaterialRuns()).ShouldBeTrue();
    }

    // A grid of overlapping boxes where every brush carries its own material and
    // one face of each carries a second — enough payload variety that a stage
    // dropping or confusing payloads shows up as a mismatch.
    private static List<BrushPlacement> MakeMixedGrid(int size)
    {
        var placements = new List<BrushPlacement>(size * size);
        for (int x = 0; x < size; x++)
        {
            for (int z = 0; z < size; z++)
            {
                MaterialRef body = MaterialRegistry.Intern($"Materials/grid_body_{x}_{z}.spectramat");
                MaterialRef trim = MaterialRegistry.Intern($"Materials/grid_trim_{x}_{z}.spectramat");
                Brush brush = Brush
                    .CreateBox(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f), body)
                    .WithFaceMaterial((x + z) % 6, trim);
                placements.Add(new BrushPlacement(
                    brush, Matrix4x4.CreateTranslation(x * 1.5f, 0f, z * 1.5f)));
            }
        }
        return placements;
    }

    // ------------------------------------------------------------------
    // (7) The interned material reference.
    // ------------------------------------------------------------------

    [Fact]
    public void Interning_the_same_path_twice_yields_the_same_reference()
    {
        MaterialRef a = MaterialRegistry.Intern("Materials/intern_probe.spectramat");
        MaterialRef b = MaterialRegistry.Intern("Materials\\Intern_Probe.spectramat");

        b.ShouldBe(a, "separator and case differences name the same content path");
        a.IsDefault.ShouldBeFalse();
        MaterialRegistry.TryGetPath(a, out string path).ShouldBeTrue();
        path.ShouldBe("Materials/intern_probe.spectramat");

        MaterialRegistry.Intern("   ").ShouldBe(MaterialRef.Default);
        MaterialRegistry.TryGetPath(MaterialRef.Default, out _).ShouldBeFalse();
    }

    // The UV projection CsgWorld.BuildMeshArrays hard-coded before per-face
    // surfaces existed, copied verbatim. This is the oracle the default path is
    // pinned against — do not "simplify" it to call FaceSurface, or the pin
    // stops proving anything.
    private static (float U, float V) LegacyUv(Vector3 v, Vector3 normal)
    {
        const float UvScale = 1.0f;
        float ax = MathF.Abs(normal.X), ay = MathF.Abs(normal.Y), az = MathF.Abs(normal.Z);

        float u, w;
        if (ax >= ay && ax >= az) { u = v.Z; w = v.Y; }      // facing X
        else if (ay >= az) { u = v.X; w = v.Z; }              // facing Y
        else { u = v.X; w = v.Y; }                            // facing Z

        return (u * UvScale, w * UvScale);
    }
}
