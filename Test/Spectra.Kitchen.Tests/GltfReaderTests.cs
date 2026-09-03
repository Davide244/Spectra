using Spectra.Kitchen.Models;
using System;
using System.Numerics;
using System.Text;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// The managed glTF reader on its own: no cook, no pack, no filesystem - bytes in
/// and geometry out.
/// </summary>
/// <remarks>
/// <para><b>Half of these are refusals, and that is the shape the file
/// wants.</b> The reader's whole stance is that a construct it does not implement
/// is named rather than guessed at, because the failure of guessing is not an
/// exception: it is an accessor walked at a stride the file never meant, which
/// produces a model that draws and is wrong. A refusal that stopped naming what
/// it refused would still pass a test asserting only that something threw, so
/// every one of these asserts on the WORDS.</para>
/// <para><b>The other half is the conversion arithmetic</b> - the column-major
/// matrix, the v flip, the mirrored winding - each of which is silent when it is
/// wrong and each of which has exactly one right answer.</para>
/// </remarks>
public class GltfReaderTests
{
    private const float Tolerance = 1e-5f;

    private static byte[] NoBuffers(string path) => throw new Xunit.Sdk.XunitException(
        $"the reader asked for external buffer '{path}', and this fixture is self-contained");

    [Fact]
    public void A_triangle_reads_its_positions_normals_and_flipped_uvs()
    {
        GltfModel model = Read(GltfFixture.Json());

        GltfSubmesh submesh = model.Submeshes.ShouldHaveSingleItem();
        submesh.VertexCount.ShouldBe(3);
        submesh.Indices.ShouldBe([0u, 1u, 2u]);
        submesh.MaterialIndex.ShouldBe(0);

        for (int v = 0; v < 3; v++)
        {
            submesh.Vertices[(v * 8) + 0].ShouldBe(GltfFixture.ExpectedPositions[(v * 3) + 0], Tolerance);
            submesh.Vertices[(v * 8) + 1].ShouldBe(GltfFixture.ExpectedPositions[(v * 3) + 1], Tolerance);
            submesh.Vertices[(v * 8) + 2].ShouldBe(GltfFixture.ExpectedPositions[(v * 3) + 2], Tolerance);

            submesh.Vertices[(v * 8) + 5].ShouldBe(1f, Tolerance, "the file's normals must be carried");

            // u passes through and v is flipped, because glTF puts v = 0 at the
            // top of an image and this engine samples v = 0 at the bottom. The
            // fixture's v values are away from 0 and 1 precisely so that a flip
            // and a swap are different numbers.
            submesh.Vertices[(v * 8) + 6].ShouldBe(GltfFixture.AuthoredUvs[v * 2], Tolerance);
            submesh.Vertices[(v * 8) + 7].ShouldBe(1f - GltfFixture.AuthoredUvs[(v * 2) + 1], Tolerance);
        }

        model.Materials.ShouldHaveSingleItem().Name.ShouldBe(GltfFixture.MaterialName);
        model.Materials[0].BaseColorImageUri.ShouldBe("../Textures/fixture.png");
    }

    [Fact]
    public void Bounds_are_of_the_baked_positions()
    {
        GltfModel model = Read(GltfFixture.Json(nodeTranslation: [10f, -1f, 4f]));

        model.BoundsMin.ShouldBe(new Vector3(10f, -1f, 4f));
        model.BoundsMax.ShouldBe(new Vector3(12f, 2f, 5.5f));
        model.Submeshes[0].BoundsMin.ShouldBe(model.BoundsMin);
    }

    [Fact]
    public void A_node_matrix_is_read_column_major_and_lands_where_the_TRS_form_does()
    {
        // The classic failure this pins: glTF stores a matrix column-major for
        // column vectors, so reading its sixteen floats into Matrix4x4's
        // row-major fields IN ORDER is exactly the transpose the row-vector
        // convention wants. Writing an explicit transpose undoes it, and the
        // symptom is a part of a model somewhere nobody asked for.
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 5f);
        Matrix4x4 expected =
            Matrix4x4.CreateScale(new Vector3(2f, 3f, 4f))
            * Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(new Vector3(5f, 6f, 7f));

        float[] gltfMatrix =
        [
            expected.M11, expected.M12, expected.M13, expected.M14,
            expected.M21, expected.M22, expected.M23, expected.M24,
            expected.M31, expected.M32, expected.M33, expected.M34,
            expected.M41, expected.M42, expected.M43, expected.M44,
        ];

        GltfSubmesh submesh = Read(GltfFixture.Json(nodeMatrix: gltfMatrix)).Submeshes[0];

        for (int v = 0; v < 3; v++)
        {
            var authored = new Vector3(
                GltfFixture.ExpectedPositions[(v * 3) + 0],
                GltfFixture.ExpectedPositions[(v * 3) + 1],
                GltfFixture.ExpectedPositions[(v * 3) + 2]);

            Vector3 placed = Vector3.Transform(authored, expected);

            submesh.Vertices[(v * 8) + 0].ShouldBe(placed.X, 1e-4f);
            submesh.Vertices[(v * 8) + 1].ShouldBe(placed.Y, 1e-4f);
            submesh.Vertices[(v * 8) + 2].ShouldBe(placed.Z, 1e-4f);
        }
    }

    [Fact]
    public void A_mirroring_transform_reverses_the_winding()
    {
        // A negative determinant mirrors, and a mirrored triangle keeps its index
        // order while its geometric winding reverses - so it renders inside out
        // under backface culling with nothing anywhere reporting it.
        float[] mirror =
        [
            -1f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, 0f,
            0f, 0f, 0f, 1f,
        ];

        Read(GltfFixture.Json(nodeMatrix: mirror)).Submeshes[0].Indices.ShouldBe([0u, 2u, 1u]);

        // And an ordinary transform leaves it alone, or the test above would pass
        // against a reader that reversed everything.
        Read(GltfFixture.Json(nodeTranslation: [1f, 0f, 0f])).Submeshes[0].Indices.ShouldBe([0u, 1u, 2u]);
    }

    [Fact]
    public void A_primitive_with_no_normals_gets_flat_ones_and_one_vertex_per_corner()
    {
        // The glTF specification's own rule. It needs one vertex per corner, so
        // the primitive is expanded rather than smoothed: smoothing would need a
        // weld by position, which would make the cooked model differ from the
        // file for a reason the file did not state.
        GltfSubmesh submesh = Read(GltfFixture.Json(omitNormals: true)).Submeshes[0];

        submesh.VertexCount.ShouldBe(3);
        submesh.Indices.ShouldBe([0u, 1u, 2u]);

        var a = new Vector3(0f, 0f, 0f);
        var b = new Vector3(2f, 0f, 0.5f);
        var c = new Vector3(0f, 3f, 1.5f);
        Vector3 face = Vector3.Normalize(Vector3.Cross(b - a, c - a));

        for (int v = 0; v < 3; v++)
        {
            submesh.Vertices[(v * 8) + 3].ShouldBe(face.X, Tolerance);
            submesh.Vertices[(v * 8) + 4].ShouldBe(face.Y, Tolerance);
            submesh.Vertices[(v * 8) + 5].ShouldBe(face.Z, Tolerance);
        }
    }

    [Fact]
    public void A_primitive_with_no_indices_draws_its_vertices_in_order()
    {
        Read(GltfFixture.Json(omitIndices: true)).Submeshes[0].Indices.ShouldBe([0u, 1u, 2u]);
    }

    [Fact]
    public void A_glb_reads_the_same_model_as_the_json_it_wraps()
    {
        GltfModel loose = Read(GltfFixture.Json());
        GltfModel packed = Read(GltfFixture.Glb(GltfFixture.GlbJson(), GltfFixture.Buffer()));

        packed.Submeshes[0].Vertices.ShouldBe(loose.Submeshes[0].Vertices);
        packed.Submeshes[0].Indices.ShouldBe(loose.Submeshes[0].Indices);
        packed.Materials[0].Name.ShouldBe(loose.Materials[0].Name);
    }

    [Fact]
    public void An_external_buffer_arrives_through_the_resolver()
    {
        byte[] expected = GltfFixture.Buffer();
        string? asked = null;

        GltfModel model = GltfReader.Read(
            Encoding.UTF8.GetBytes(GltfFixture.Json(bufferUri: "fixture.bin")),
            "Models/fixture.gltf",
            path =>
            {
                asked = path;
                return expected;
            });

        // The uri is joined against the MODEL's own folder and normalised, which
        // is what makes it a content path a rule can record as a dependency.
        asked.ShouldBe("Models/fixture.bin");
        model.Submeshes[0].VertexCount.ShouldBe(3);
    }

    [Fact]
    public void A_uri_that_climbs_out_of_the_model_folder_resolves_against_the_content_root()
    {
        GltfReader.ResolveSiblingPath("Models/props/sign.gltf", "../../Textures/wall.png")
            .ShouldBe("Textures/wall.png");

        // Percent encoding is undone, because a glTF uri is a URI: a file with a
        // space in its name arrives as %20 and would otherwise be looked for
        // under that name.
        GltfReader.ResolveSiblingPath("Models/sign.gltf", "sign%20base.bin")
            .ShouldBe("Models/sign base.bin");
    }

    [Fact]
    public void An_external_buffer_nothing_provides_is_refused_by_the_path_it_looked_for()
    {
        Refuse(GltfFixture.Json(bufferUri: "fixture.bin"), "Models/fixture.bin", resolve: _ => null);
    }

    // ---- refusals, each naming what it refused -----------------------------

    [Fact]
    public void A_primitive_that_is_not_triangles_is_refused_by_its_mode_number()
    {
        Refuse(GltfFixture.Json(mode: 5), "mode 5", "TRIANGLE_STRIP");
        Refuse(GltfFixture.Json(mode: 0), "mode 0", "POINTS");
    }

    [Fact]
    public void A_sparse_accessor_is_refused_by_name()
    {
        // Reading only its base array would drop exactly the values a sparse
        // accessor exists to carry, which is geometry that is silently wrong.
        Refuse(GltfFixture.Json(sparsePositions: true), "sparse");
    }

    [Fact]
    public void A_required_extension_is_refused_by_its_own_name()
    {
        Refuse(
            GltfFixture.Json(requiredExtension: "KHR_draco_mesh_compression"),
            "KHR_draco_mesh_compression");
    }

    [Fact]
    public void An_index_component_type_gltf_forbids_is_refused_by_number()
    {
        // 5122 is SHORT. Read anyway, a negative index becomes a very large
        // vertex index rather than an error, which is why the allowlist exists.
        Refuse(GltfFixture.Json(indexComponentType: 5122), "5122", "SHORT");
    }

    [Fact]
    public void An_asset_version_that_is_not_2_is_refused()
    {
        Refuse(GltfFixture.Json(assetVersion: "1.0"), "1.0", "glTF 2.0");
        Refuse(GltfFixture.Json(assetVersion: null), "nothing", "glTF 2.0");
    }

    [Fact]
    public void Bytes_that_are_not_a_model_at_all_are_refused()
    {
        Refuse("not json", "not readable JSON");
        Refuse("[ 1, 2 ]", "does not begin with a JSON object");

        // Shorter than a magic number, which is its own message: a file too
        // small to identify has not failed a parse, it has nothing in it.
        RefuseBytes([1, 2], "too short to be glTF");
    }

    [Fact]
    public void A_truncated_or_wrong_version_glb_is_refused()
    {
        byte[] glb = GltfFixture.Glb(GltfFixture.GlbJson(), GltfFixture.Buffer());

        RefuseBytes(glb.AsSpan(0, 8).ToArray(), "too short");
        RefuseBytes(GltfFixture.Glb(GltfFixture.GlbJson(), GltfFixture.Buffer(), version: 1), "version 1");

        // A declared length past the file. The chunk walk bounds itself with a
        // SUBTRACTION rather than an addition, because a sum near uint.MaxValue
        // wraps and passes a naive bound.
        RefuseBytes(
            GltfFixture.Glb(GltfFixture.GlbJson(), GltfFixture.Buffer(), declaredLengthDelta: 64),
            "truncated");
    }

    [Fact]
    public void A_node_that_is_its_own_ancestor_is_refused()
    {
        const string Cyclic = """
            {
              "asset": { "version": "2.0" },
              "scenes": [ { "nodes": [0] } ],
              "scene": 0,
              "nodes": [ { "children": [1] }, { "children": [0] } ]
            }
            """;

        Refuse(Cyclic, "its own ancestor");
    }

    [Fact]
    public void A_node_carrying_both_a_matrix_and_a_translation_is_refused()
    {
        const string Both = """
            {
              "asset": { "version": "2.0" },
              "scenes": [ { "nodes": [0] } ],
              "scene": 0,
              "nodes": [ { "matrix": [1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1], "translation": [1,2,3] } ]
            }
            """;

        Refuse(Both, "both a matrix and a translation");
    }

    [Fact]
    public void A_document_that_places_no_geometry_is_refused()
    {
        const string Empty = """
            { "asset": { "version": "2.0" }, "scenes": [ { "nodes": [] } ], "scene": 0, "nodes": [] }
            """;

        Refuse(Empty, "no drawable triangles");
    }

    // ---- helpers ------------------------------------------------------------

    private static GltfModel Read(string json) =>
        GltfReader.Read(Encoding.UTF8.GetBytes(json), "Models/fixture.gltf", NoBuffers);

    private static GltfModel Read(byte[] file) =>
        GltfReader.Read(file, "Models/fixture.glb", NoBuffers);

    private static void Refuse(string json, params string[] expected) =>
        RefuseBytes(Encoding.UTF8.GetBytes(json), expected);

    private static void Refuse(string json, string expected, GltfBufferResolver resolve)
    {
        GltfFormatException thrown = Should.Throw<GltfFormatException>(
            () => GltfReader.Read(Encoding.UTF8.GetBytes(json), "Models/fixture.gltf", resolve));

        thrown.Message.ShouldContain(expected);
    }

    private static void RefuseBytes(byte[] file, params string[] expected)
    {
        GltfFormatException thrown = Should.Throw<GltfFormatException>(
            () => GltfReader.Read(file, "Models/fixture.gltf", NoBuffers));

        foreach (string fragment in expected) thrown.Message.ShouldContain(fragment);
    }
}
