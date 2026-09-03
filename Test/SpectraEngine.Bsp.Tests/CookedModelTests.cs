using Microsoft.Extensions.Logging.Abstractions;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Assets.Models;
using SpectraEngine.Core.Assets.Sources;
using SpectraEngine.Core.Graphics;
using System;
using System.IO;
using System.Numerics;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The runtime half of the model cook: the redirection that decides which of two
/// files a model IS, and the load that turns a <c>.smodel</c> into the same
/// <see cref="ModelData"/> an import produces.
/// </summary>
/// <remarks>
/// <para><b>The <c>.smodel</c> here is built by <see cref="HandBuiltSmodel"/>,
/// not by the cooker.</b> The cook's own tests live beside the cook and prove
/// that what it writes is readable; this file's claim is the other one, that the
/// engine loads a conforming file whatever wrote it - which is only a real claim
/// if the bytes come from a transcription of the specification rather than from
/// the writer whose output is already the reader's input everywhere else.</para>
/// <para><b>No <c>.gltf</c> is ever written in these fixtures.</b> A cooked model
/// that quietly fell back to the authored file would pass every assertion about
/// its contents; the only way to prove it did not is for there to be nothing to
/// fall back to.</para>
/// </remarks>
public class CookedModelTests : IDisposable
{
    private const string Authored = "Models/prop.gltf";
    private const string Cooked = "Models/prop.smodel";
    private const string Material = "Materials/hand.spectramat";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "SpectraCookedModelTests", Guid.NewGuid().ToString("N"));

    public CookedModelTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Models"));
        Directory.CreateDirectory(Path.Combine(_root, "Materials"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void The_cooked_file_wins_where_one_exists_and_the_authored_path_stays_the_name()
    {
        // A model is named by its SOURCE path forever - a map, a script and a
        // scene node all say Models/prop.gltf - so cooking is a source swap
        // rather than a migration of everything that names a prop.
        ModelContentPath.CookedPathFor(Authored).ShouldBe(Cooked);
        ModelContentPath.CookedPathFor(Cooked).ShouldBe(Cooked);
        ModelContentPath.IsCooked(Cooked).ShouldBeTrue();
        ModelContentPath.IsCooked(Authored).ShouldBeFalse();

        var empty = new ContentSourceStack();
        ModelContentPath.Resolve(empty, Authored).ShouldBe(Authored, "a miss names the file it looked for");

        Write(Cooked, Model((0u, 3u, 0u)));
        ModelContentPath.Resolve(Stack(), Authored).ShouldBe(Cooked);
    }

    [Fact]
    public void A_cooked_model_loads_with_one_gpu_mesh_per_submesh()
    {
        Write(Cooked, Model((0u, 3u, 0u), (3u, 3u, 0u)));
        WriteMaterial();

        using AssetManager assets = Attach(out FakeRenderer renderer);

        assets.IsModelCooked(Authored).ShouldBeTrue();

        ModelAsset model = assets.LoadModel(Authored);
        ModelData data = model.Data.ShouldNotBeNull();

        model.Error.ShouldBeNull();
        data.Meshes.Count.ShouldBe(2);
        renderer.CreatedMeshes.Count.ShouldBe(2);

        // The whole model's bounds ride the header, so Mesh.LocalBounds and the
        // BVH cost no vertex walk at load.
        data.LocalBounds.Min.ShouldBe(new Vector3(-1f, -2f, -3f));
        data.LocalBounds.Max.ShouldBe(new Vector3(4f, 5f, 6f));

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Each_submesh_gets_a_zero_based_slice_of_the_shared_vertex_buffer()
    {
        // The format keeps ONE vertex buffer with submeshes as index ranges,
        // because an LOD switch has to be a draw-range change. ModelMesh predates
        // it and wants a self-contained zero-based array per submesh, so the load
        // gathers - and the gather is by the MINIMUM index in the range, never by
        // an assumed partition, or a file whose submeshes interleave their
        // vertices would be mis-addressed rather than merely copied widely.
        Write(Cooked, Model((0u, 3u, 0u), (3u, 3u, 0u)));

        using AssetManager assets = Attach(out _);
        ModelData data = assets.LoadModel(Authored).Data.ShouldNotBeNull();

        data.Meshes[0].Indices.ShouldBe([0u, 1u, 2u]);
        data.Meshes[0].VertexCount.ShouldBe(3);
        data.Meshes[0].Vertices[0].ShouldBe(0f, 1e-5f);

        // The second range names vertices 3, 4 and 5, so its slice starts at 3
        // and its indices come back rebased on it.
        data.Meshes[1].Indices.ShouldBe([0u, 1u, 2u]);
        data.Meshes[1].VertexCount.ShouldBe(3);
        data.Meshes[1].Vertices[0].ShouldBe(30f, 1e-5f);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void A_submesh_binds_the_material_the_file_named_by_path()
    {
        // By PATH, not by name: the cook resolved this once and recorded the
        // answer, and a loader that rebuilt "Materials/<name>.spectramat" from
        // the stem would be a second spelling of that rule, agreeing exactly
        // until a material lives somewhere else.
        Write(Cooked, Model((0u, 3u, 0u)));
        WriteMaterial();

        using AssetManager assets = Attach(out _);
        ModelAsset model = assets.LoadModel(Authored);
        ModelData data = model.Data.ShouldNotBeNull();

        Material bound = model.MaterialFor(data.Meshes[0]);
        bound.ShouldNotBeSameAs(assets.DefaultMaterial);
        bound.Name.ShouldBe("hand");
        bound.SourcePath.ShouldBe(Material);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void A_submesh_that_names_no_material_degrades_to_the_default_one()
    {
        // The cook says so at SC3002 where the author's file is in hand; here it
        // is the runtime's ordinary soft landing, which content errors must never
        // escape.
        Write(Cooked, Model((0u, 3u, HandBuiltSmodel.NameOffsetAbsent)));

        using AssetManager assets = Attach(out _);
        ModelAsset model = assets.LoadModel(Authored);

        model.MaterialFor(model.Data.ShouldNotBeNull().Meshes[0]).ShouldBeSameAs(assets.DefaultMaterial);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void A_layout_this_build_cannot_upload_is_refused_naming_both_ids()
    {
        // The format reserves a stride-copying fallback for the day
        // VertexAttribute.StandardLayout grows a tangent, and this build has
        // none. Refusing names the moment that arrives; converting silently would
        // upload floats in an order nothing agreed on.
        byte[] file = new HandBuiltSmodel()
            .VertexLayout(
                strideFloats: 11,
                (Semantic: (byte)0, ComponentType: (byte)0, ComponentCount: (byte)3, ByteOffset: (ushort)0),
                (Semantic: (byte)1, ComponentType: (byte)0, ComponentCount: (byte)3, ByteOffset: (ushort)12),
                (Semantic: (byte)2, ComponentType: (byte)0, ComponentCount: (byte)4, ByteOffset: (ushort)24),
                (Semantic: (byte)3, ComponentType: (byte)0, ComponentCount: (byte)2, ByteOffset: (ushort)40))
            .VertexBuffer(new float[11 * 3])
            .Indices16(0, 1, 2)
            .Submeshes((0u, 3u, HandBuiltSmodel.NameOffsetAbsent))
            .Build();

        Write(Cooked, file);

        using AssetManager assets = Attach(out _);

        SmodelFormatException refused = Should.Throw<SmodelFormatException>(() => assets.LoadModel(Authored));
        refused.Message.ShouldContain(SmodelStandardLayout.LayoutId.ToString("X8"));
        refused.Message.ShouldContain("Recook");

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void With_no_cooked_file_the_authored_one_is_imported_as_before()
    {
        // The loose path is untouched, which is what makes the whole layer a
        // source swap: the repo's own crate has no .smodel and still loads
        // through the importer.
        using var assets = new AssetManager(
            NullLogger<AssetManager>.Instance, ContentRoot.Path, hotReloadEnabled: false);
        assets.AttachRenderer(new FakeRenderer());

        assets.IsModelCooked("Models/crate.obj").ShouldBeFalse();
        assets.LoadModel("Models/crate.obj").Data.ShouldNotBeNull().Meshes.Count.ShouldBe(2);

        assets.ReleaseGraphicsResources();
    }

    // ---- fixtures -----------------------------------------------------------

    // Six vertices in the engine's standard layout, two triangles, and whatever
    // submesh records the caller asks for. The x of vertex v is v * 10, so a
    // slice that started at the wrong vertex is a different number rather than a
    // different arrangement of the same ones.
    private static byte[] Model(params (uint Start, uint Count, uint MaterialName)[] submeshes)
    {
        var vertices = new float[6 * 8];
        for (int v = 0; v < 6; v++)
        {
            vertices[v * 8] = v * 10f;
            vertices[(v * 8) + 5] = 1f;
            vertices[(v * 8) + 6] = v * 0.125f;
        }

        return new HandBuiltSmodel()
            .VertexLayout(
                strideFloats: 8,
                (Semantic: (byte)0, ComponentType: (byte)0, ComponentCount: (byte)3, ByteOffset: (ushort)0),
                (Semantic: (byte)1, ComponentType: (byte)0, ComponentCount: (byte)3, ByteOffset: (ushort)12),
                (Semantic: (byte)3, ComponentType: (byte)0, ComponentCount: (byte)2, ByteOffset: (ushort)24))
            .VertexBuffer(vertices)
            .Indices16(0, 1, 2, 3, 4, 5)
            .Submeshes(submeshes)
            .Names(out _, Material)
            .Build();
    }

    private void WriteMaterial()
    {
        File.WriteAllText(
            Path.Combine(_root, "Materials", "hand.spectramat"), "shader = lit\ncolor uBaseColor = #FFFFFF\n");
    }

    private void Write(string contentPath, byte[] bytes) =>
        File.WriteAllBytes(Path.Combine(_root, contentPath.Replace('/', Path.DirectorySeparatorChar)), bytes);

    private ContentSourceStack Stack()
    {
        var stack = new ContentSourceStack();
        stack.Mount(new LooseFileSource(NullLogger.Instance, _root));
        return stack;
    }

    private AssetManager Attach(out FakeRenderer renderer)
    {
        renderer = new FakeRenderer();
        var assets = new AssetManager(NullLogger<AssetManager>.Instance, _root, hotReloadEnabled: false);
        assets.AttachRenderer(renderer);
        return assets;
    }
}
