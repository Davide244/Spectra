using Microsoft.Extensions.Logging.Abstractions;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Scene;
using System.Numerics;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// The imported model meeting a real driver: the headless suites prove the
/// conversion is correct, but only a real GL context proves the arrays it
/// produces are something <c>glBufferData</c> and the standard attribute layout
/// actually accept.
/// </summary>
[Collection(GlRendererCollection.Name)]
public sealed class ModelGlUploadTests
{
    private const string Crate = "Models/crate.obj";

    private readonly GlRendererFixture _fixture;

    public ModelGlUploadTests(GlRendererFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void The_shipped_crate_uploads_and_instantiates_against_a_real_context()
    {
        var assets = new AssetManager(
            NullLogger<AssetManager>.Instance, ContentRoot.Path, hotReloadEnabled: false);
        assets.AttachRenderer(_fixture.Renderer);

        try
        {
            ModelAsset model = assets.LoadModel(Crate);

            model.IsReady.ShouldBeTrue();
            model.Meshes.Count.ShouldBe(2);

            // The backend de-interleaved what the importer interleaved: if the
            // layout and the vertex stride disagreed, these would not line up.
            for (int i = 0; i < model.Meshes.Count; i++)
            {
                Mesh mesh = model.Meshes[i];
                ModelMesh source = model.Data!.Meshes[i];
                mesh.IndexCount.ShouldBe((uint)source.Indices.Length);
                mesh.Positions.Count.ShouldBe(source.VertexCount);
                mesh.Normals.Count.ShouldBe(source.VertexCount);
                mesh.LocalBounds.Min.ShouldBe(source.LocalBounds.Min);
                mesh.LocalBounds.Max.ShouldBe(source.LocalBounds.Max);
            }

            // The .mtl's textures became real GL textures, not the placeholder.
            Material body = model.MaterialFor(model.Data!.Meshes[0]);
            body.Shader.ShouldNotBeNull();
            body.TryGetTexture("uDiffuse", out _, out Texture? texture).ShouldBeTrue();
            texture.ShouldNotBeSameAs(assets.PlaceholderTexture);

            // And it drops into a scene the same as any other geometry.
            var scene = new Scene("gl-model-test");
            SceneNode instance = ModelInstantiator.InstantiateInto(scene.Root, model);
            instance.Children.Count.ShouldBe(2);

            // Camera in front of the crate, looking down -Z (the Camera
            // defaults, restated so this does not silently depend on them).
            var camera = new Camera
            {
                Position = new Vector3(0f, 16f, 120f),
                Yaw = -MathF.PI / 2f,
                Pitch = 0f,
                AspectRatio = 16f / 9f,
            };
            var view = new RenderView();
            scene.BuildRenderView(camera, view);
            view.Items.Count.ShouldBe(2);
        }
        finally
        {
            assets.ReleaseGraphicsResources();
        }
    }
}
