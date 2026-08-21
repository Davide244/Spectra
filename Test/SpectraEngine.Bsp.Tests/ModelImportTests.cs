using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Bsp;
using System.Numerics;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// <see cref="ModelImporter"/> on its own: no renderer, no asset manager, no
/// GPU — just a file on disk turning into <see cref="ModelData"/>.
/// </summary>
/// <remarks>
/// The counts and bounds asserted here are pinned to the two models the repo
/// ships (<c>Assets/Models/crate.obj</c> and <c>Assets/Models/signpost.gltf</c>),
/// which are hand-authored precisely so those numbers can be derived by reading
/// the file rather than by trusting the importer. The crate covers the classic
/// path (OBJ + MTL, multi-material, flat hierarchy); the signpost covers the
/// modern one (glTF, nested nodes carrying real transforms).
/// </remarks>
public sealed class ModelImportTests
{
    private const string Crate = "Models/crate.obj";
    private const string Signpost = "Models/signpost.gltf";
    private const float Tolerance = 1e-4f;

    [Fact]
    public void Obj_imports_one_submesh_per_material_with_the_authored_counts()
    {
        ModelData model = Import(Crate);

        // Four side quads and two cap quads, split by the two materials.
        model.Meshes.Count.ShouldBe(2);
        model.VertexCount.ShouldBe(24);
        model.IndexCount.ShouldBe(36);

        ModelMesh sides = model.Meshes[0];
        sides.Name.ShouldBe("Crate_Sides");
        sides.VertexCount.ShouldBe(16);
        sides.TriangleCount.ShouldBe(8);
        sides.HadNormals.ShouldBeTrue();
        sides.HadTextureCoordinates.ShouldBeTrue();

        ModelMesh caps = model.Meshes[1];
        caps.Name.ShouldBe("Crate_Caps");
        caps.VertexCount.ShouldBe(8);
        caps.TriangleCount.ShouldBe(4);

        // Distinct materials is the whole point of splitting them.
        sides.MaterialIndex.ShouldNotBe(caps.MaterialIndex);
        model.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Vertices_are_written_in_the_engines_standard_layout()
    {
        ModelData model = Import(Crate);
        ModelMesh sides = model.Meshes[0];

        sides.Vertices.Length.ShouldBe(sides.VertexCount * ModelVertexLayout.FloatsPerVertex);

        // Every vertex is on the crate's shell, carries a unit normal, and has
        // uvs inside the unit square — which together prove the three streams
        // landed at the right offsets rather than being shuffled.
        for (int v = 0; v < sides.VertexCount; v++)
        {
            int b = v * ModelVertexLayout.FloatsPerVertex;
            var position = new Vector3(sides.Vertices[b], sides.Vertices[b + 1], sides.Vertices[b + 2]);
            var normal = new Vector3(sides.Vertices[b + 3], sides.Vertices[b + 4], sides.Vertices[b + 5]);
            float u = sides.Vertices[b + 6];
            float w = sides.Vertices[b + 7];

            MathF.Abs(position.X).ShouldBe(16f, Tolerance * 16f);
            position.Y.ShouldBeOneOf(0f, 32f);
            normal.Length().ShouldBe(1f, Tolerance);
            u.ShouldBeOneOf(0f, 1f);
            w.ShouldBeOneOf(0f, 1f);
        }
    }

    [Fact]
    public void Obj_bounds_match_the_authored_extents()
    {
        ModelData model = Import(Crate);

        // Authored centred on X/Z with the base on the floor.
        model.LocalBounds.Min.ShouldBe(new Vector3(-16f, 0f, -16f));
        model.LocalBounds.Max.ShouldBe(new Vector3(16f, 32f, 16f));
    }

    [Fact]
    public void Mtl_texture_references_come_back_relative_to_the_content_root()
    {
        ModelData model = Import(Crate);

        ModelMaterial body = MaterialOf(model, model.Meshes[0]);
        body.Name.ShouldBe("crate_body");
        // Written in the .mtl as "../Textures/checker_orange.png", relative to
        // the model file — the importer re-expresses it against the content root.
        body.DiffuseTexturePath.ShouldBe("Textures/checker_orange.png");
        body.BaseColor.ShouldBe(Vector3.One);

        ModelMaterial trim = MaterialOf(model, model.Meshes[1]);
        trim.Name.ShouldBe("crate_trim");
        trim.DiffuseTexturePath.ShouldBe("Textures/dev_grid.png");
        trim.BaseColor.X.ShouldBe(0.75f, Tolerance);
    }

    [Fact]
    public void Obj_objects_become_child_nodes_of_one_root()
    {
        ModelData model = Import(Crate);

        model.Root.Children.Count.ShouldBe(2);
        model.Root.MeshIndices.ShouldBeEmpty();

        ModelNode sides = model.Root.Children[0];
        sides.Name.ShouldBe("Crate_Sides");
        sides.MeshIndices.ShouldHaveSingleItem().ShouldBe(0);
        sides.TransformIsExact.ShouldBeTrue();
        sides.Position.ShouldBe(Vector3.Zero);

        model.Root.Children[1].Name.ShouldBe("Crate_Caps");
        model.Root.Children[1].MeshIndices.ShouldHaveSingleItem().ShouldBe(1);
    }

    [Fact]
    public void Gltf_node_transforms_survive_the_import()
    {
        ModelData model = Import(Signpost);

        model.Root.Name.ShouldBe("Signpost");
        model.Root.Children.Count.ShouldBe(2);

        ModelNode post = model.Root.Children[0];
        post.Name.ShouldBe("Post");
        post.Position.ShouldBe(Vector3.Zero);
        post.Rotation.ShouldBe(Quaternion.Identity);

        // The sign is authored 26 units up and yawed 20 degrees. Getting this
        // wrong is the classic symptom of forgetting that Assimp stores
        // column-vector matrices where System.Numerics stores row-vector ones.
        ModelNode sign = model.Root.Children[1];
        sign.Name.ShouldBe("Sign");
        sign.Position.X.ShouldBe(0f, Tolerance);
        sign.Position.Y.ShouldBe(26f, Tolerance);
        sign.Position.Z.ShouldBe(0f, Tolerance);
        // Not exactly one: decomposing a rotation matrix goes through a square
        // root, so an unscaled node comes back a few ulps off unity.
        sign.Scale.X.ShouldBe(1f, Tolerance);
        sign.Scale.Y.ShouldBe(1f, Tolerance);
        sign.Scale.Z.ShouldBe(1f, Tolerance);
        sign.TransformIsExact.ShouldBeTrue();

        // Yaw the +X axis by the node's rotation and check where it lands.
        Vector3 rotatedX = Vector3.Transform(Vector3.UnitX, sign.Rotation);
        rotatedX.X.ShouldBe(MathF.Cos(MathF.PI * 20f / 180f), 1e-3f);
        rotatedX.Y.ShouldBe(0f, 1e-3f);
        // A +Y yaw of 20 degrees swings +X toward -Z.
        rotatedX.Z.ShouldBe(-MathF.Sin(MathF.PI * 20f / 180f), 1e-3f);

        // Translation lives in the matrix's fourth ROW after conversion.
        sign.LocalMatrix.M42.ShouldBe(26f, Tolerance);
    }

    [Fact]
    public void Model_bounds_account_for_the_node_transforms_that_place_the_parts()
    {
        ModelData model = Import(Signpost);
        Aabb bounds = model.LocalBounds;

        // The post alone is 4 units wide; the sign is 24 wide, lifted to y=26
        // and yawed, so the model box has to be much wider than any single
        // submesh's raw vertex box and has to reach the sign's full height.
        float yaw = MathF.PI * 20f / 180f;
        float expectedHalfWidth = (12f * MathF.Cos(yaw)) + (0.5f * MathF.Sin(yaw));
        float expectedHalfDepth = (12f * MathF.Sin(yaw)) + (0.5f * MathF.Cos(yaw));

        bounds.Min.X.ShouldBe(-expectedHalfWidth, 1e-2f);
        bounds.Max.X.ShouldBe(expectedHalfWidth, 1e-2f);
        bounds.Min.Z.ShouldBe(-expectedHalfDepth, 1e-2f);
        bounds.Max.Z.ShouldBe(expectedHalfDepth, 1e-2f);
        bounds.Min.Y.ShouldBe(0f, Tolerance);
        bounds.Max.Y.ShouldBe(32f, Tolerance);
    }

    // ---- degraded content ------------------------------------------------

    [Fact]
    public void A_mesh_without_uvs_gets_zeroed_uvs_and_says_so()
    {
        string root = CreateTempContentRoot();
        WriteModel(root, "flat.obj", """
            v 0 0 0
            v 4 0 0
            v 0 4 0
            f 1 2 3
            """);

        ModelData model = Import(root, "Models/flat.obj");

        ModelMesh mesh = model.Meshes.ShouldHaveSingleItem();
        mesh.HadTextureCoordinates.ShouldBeFalse();
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            int b = v * ModelVertexLayout.FloatsPerVertex;
            mesh.Vertices[b + ModelVertexLayout.TexCoordOffset].ShouldBe(0f);
            mesh.Vertices[b + ModelVertexLayout.TexCoordOffset + 1].ShouldBe(0f);
        }

        model.Warnings.ShouldContain(w => w.Contains("no texture coordinates"));
    }

    [Fact]
    public void A_mesh_without_normals_gets_generated_ones()
    {
        string root = CreateTempContentRoot();
        // Counter-clockwise in the XY plane, so the generated normal is +Z.
        WriteModel(root, "flat.obj", """
            v 0 0 0
            v 4 0 0
            v 0 4 0
            f 1 2 3
            """);

        ModelData model = Import(root, "Models/flat.obj");

        ModelMesh mesh = model.Meshes.ShouldHaveSingleItem();
        mesh.HadNormals.ShouldBeTrue("GenerateMissingNormals should have filled the channel");
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            int b = (v * ModelVertexLayout.FloatsPerVertex) + ModelVertexLayout.NormalOffset;
            var normal = new Vector3(mesh.Vertices[b], mesh.Vertices[b + 1], mesh.Vertices[b + 2]);
            normal.Length().ShouldBe(1f, Tolerance);
            normal.Z.ShouldBe(1f, Tolerance);
        }
    }

    [Fact]
    public void A_model_with_no_material_library_still_imports_with_a_material_slot()
    {
        string root = CreateTempContentRoot();
        WriteModel(root, "bare.obj", """
            v 0 0 0
            v 4 0 0
            v 0 4 0
            f 1 2 3
            """);

        ModelData model = Import(root, "Models/bare.obj");

        // The importer always produces a slot, so a submesh's material index is
        // never dangling; it just describes nothing usable.
        model.Materials.ShouldNotBeEmpty();
        ModelMaterial material = MaterialOf(model, model.Meshes[0]);
        material.DiffuseTexturePath.ShouldBeNull();
    }

    [Fact]
    public void A_texture_outside_the_content_root_is_dropped_with_a_warning()
    {
        string root = CreateTempContentRoot();
        WriteModel(root, "stray.obj", """
            mtllib stray.mtl
            usemtl stray
            v 0 0 0
            v 4 0 0
            v 0 4 0
            f 1 2 3
            """);
        WriteModel(root, "stray.mtl", """
            newmtl stray
            map_Kd C:/definitely/not/here/nothing.png
            """);

        ModelData model = Import(root, "Models/stray.obj");

        MaterialOf(model, model.Meshes[0]).DiffuseTexturePath.ShouldBeNull();
        model.Warnings.ShouldContain(w => w.Contains("nothing.png"));
    }

    [Fact]
    public void A_texture_the_exporter_baked_an_absolute_path_for_is_relocated_under_Textures()
    {
        string root = CreateTempContentRoot();
        File.Copy(
            ContentRoot.ResolveAbsolute(ContentRoot.Path, "Textures/dev_grid.png"),
            Path.Combine(root, "Textures", "dev_grid.png"));
        WriteModel(root, "baked.obj", """
            mtllib baked.mtl
            usemtl baked
            v 0 0 0
            v 4 0 0
            v 0 4 0
            f 1 2 3
            """);
        // What a DCC export from someone else's machine actually looks like.
        WriteModel(root, "baked.mtl", """
            newmtl baked
            map_Kd D:/Art/WIP/textures/dev_grid.png
            """);

        ModelData model = Import(root, "Models/baked.obj");

        MaterialOf(model, model.Meshes[0]).DiffuseTexturePath.ShouldBe("Textures/dev_grid.png");
        model.Warnings.ShouldContain(w => w.Contains("Textures/dev_grid.png"));
    }

    // ---- failure ---------------------------------------------------------

    [Fact]
    public void A_missing_file_throws_FileNotFoundException()
    {
        string root = CreateTempContentRoot();

        Should.Throw<FileNotFoundException>(() => Import(root, "Models/absent.obj"));
    }

    [Fact]
    public void An_unreadable_format_throws_with_the_importers_own_diagnosis()
    {
        string root = CreateTempContentRoot();
        WriteModel(root, "notamodel.zzz", "this is not a model file");

        var ex = Should.Throw<InvalidDataException>(() => Import(root, "Models/notamodel.zzz"));
        ex.Message.ShouldContain("notamodel.zzz");
    }

    [Fact]
    public void A_corrupt_file_fails_cleanly_instead_of_crashing()
    {
        string root = CreateTempContentRoot();
        // Valid extension, garbage contents: the path a truncated download or a
        // half-written export takes.
        var noise = new byte[4096];
        new Random(1234).NextBytes(noise);
        File.WriteAllBytes(Path.Combine(root, "Models", "broken.obj"), noise);

        var ex = Should.Throw<InvalidDataException>(() => Import(root, "Models/broken.obj"));
        ex.Message.ShouldContain("broken.obj");
    }

    [Fact]
    public void A_model_with_no_drawable_geometry_is_rejected()
    {
        string root = CreateTempContentRoot();
        // Points only: nothing the renderer could ever draw.
        WriteModel(root, "points.obj", """
            v 0 0 0
            v 1 0 0
            p 1
            p 2
            """);

        Should.Throw<InvalidDataException>(() => Import(root, "Models/points.obj"))
            .Message.ShouldContain("no triangle geometry");
    }

    // ---- helpers ---------------------------------------------------------

    private static ModelData Import(string relativePath)
        => Import(ContentRoot.Path, relativePath);

    private static ModelData Import(string contentRoot, string relativePath)
        => ModelImporter.Import(
            ContentRoot.ResolveAbsolute(contentRoot, relativePath), contentRoot);

    private static ModelMaterial MaterialOf(ModelData model, in ModelMesh mesh)
        => model.Materials[mesh.MaterialIndex];

    private static string CreateTempContentRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "SpectraModelTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Models"));
        Directory.CreateDirectory(Path.Combine(root, "Textures"));
        Directory.CreateDirectory(Path.Combine(root, "Materials"));
        return root;
    }

    private static void WriteModel(string root, string fileName, string contents)
        => File.WriteAllText(Path.Combine(root, "Models", fileName), contents);
}
