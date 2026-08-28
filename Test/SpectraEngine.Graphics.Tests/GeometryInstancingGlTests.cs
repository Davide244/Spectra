using Silk.NET.OpenGL;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.OpenGL;
using SpectraEngine.Core.Scene;
using System;
using System.Numerics;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// The deferred geometry pass collapses repeated meshes into instanced draws,
/// and the picture does not change when it does.
/// </summary>
/// <remarks>
/// <para>
/// <b>The oracle is two scenes that differ ONLY in whether the batch path can
/// run.</b> Six nodes sharing one <see cref="Brush"/> instance resolve to one
/// GPU mesh (<c>PartBrushMeshCache</c> keys on reference identity), so they
/// become a single batch of six; six nodes carrying six structurally equal but
/// separate brushes resolve to six meshes and stay six ordinary draws. Same
/// geometry, same material, same transforms, same shader maths - so the two
/// frames must be the same picture, and any difference is the instanced path
/// getting a transform wrong.
/// </para>
/// <para>
/// <b>Comparing against a toggle would have been weaker.</b> A switch that
/// disables batching only proves the two branches of one code path agree; two
/// scenes prove the batched frame matches what the engine drew before batching
/// existed, which is the claim that matters.
/// </para>
/// <para>
/// <b>Both halves of the assertion are load-bearing.</b> A batch path that
/// silently drew nothing would also produce two identical frames, so the test
/// pins <c>GeometryDrawsSaved</c> as well: five for the shared scene and zero
/// for the separate one. Without it, "the batch path is broken" and "there was
/// nothing to batch" are the same green tick - which is exactly why no earlier
/// measurement in this repo could see instancing at all.
/// </para>
/// </remarks>
[Collection(GlRendererCollection.Name)]
public sealed class GeometryInstancingGlTests
{
    private readonly GlRendererFixture _fixture;

    public GeometryInstancingGlTests(GlRendererFixture fixture) => _fixture = fixture;

    // Two groups, each above RenderView.MinimumBatchSize, so the view carries
    // TWO batches and the second one starts at a non-zero offset into the shared
    // transform array. With a single batch that offset is always zero and a
    // whole class of indexing bug draws the right picture.
    private const int GroupSize = 5;
    private const int Copies = GroupSize * 2;

    // Setting ProbeTarget makes a frame render the scene TWICE: once into the
    // probe and once into the window, both inside one command list. Every
    // per-frame counter therefore reads double here, which is the honest number
    // rather than a quirk to divide away - and pinning it means a change to that
    // path shows up as this test rather than as a puzzling profile.
    private const int ExecutionsPerFrame = 2;

    private static Brush Box(float halfExtent = 0.5f) =>
        Brush.CreateBox(new Vector3(-halfExtent), new Vector3(halfExtent), default);

    [Fact]
    public void Shared_brushes_batch_and_render_exactly_as_separate_ones()
    {
        OpenGLRenderer renderer = _fixture.Renderer;
        renderer.GetFramebufferSize(out int width, out int height);
        int size = Math.Min(width, height);

        // Two shared brushes, ALTERNATING, so the two batches interleave in the
        // draw list. RenderView groups by first appearance rather than by
        // adjacency precisely because the list arrives in spatial-index order,
        // and alternating here is what exercises that.
        Brush small = Box(0.5f);
        Brush large = Box(0.62f);
        (int[,] batched, int savedBatched, int batchCount, int visibleBatched) =
            Render(i => (i % 2 == 0) ? small : large, size);

        // A separate brush each: ten meshes, ten single items, no batch.
        (int[,] separate, int savedSeparate, int separateBatches, int visibleSeparate) =
            Render(i => Box((i % 2 == 0) ? 0.5f : 0.62f), size);

        visibleBatched.ShouldBe(Copies, "a culled prop would silently shrink a group below the batch minimum");
        visibleSeparate.ShouldBe(Copies, "a culled prop would silently shrink a group below the batch minimum");

        savedBatched.ShouldBe((GroupSize - 1) * 2 * ExecutionsPerFrame,
            "each group of five collapses to one instanced draw, so four draws go per group "
            + "per execution of the pipeline");
        batchCount.ShouldBe(2, "two shared meshes under one material is two batches");
        savedSeparate.ShouldBe(0, "ten distinct meshes cannot be collapsed");
        separateBatches.ShouldBe(0, "nothing repeats, so nothing should be partitioned into a batch");

        // Not vacuous: the props have to actually be on screen, or two blank
        // frames would agree perfectly and prove nothing.
        CountLit(separate, size).ShouldBeGreaterThan(size * 4,
            "the props must cover a meaningful part of the frame for this comparison to mean anything");

        (int x, int y, int worst) = WorstDifference(batched, separate, size);
        worst.ShouldBeLessThanOrEqualTo(2,
            $"the instanced frame differed from the unbatched one by {worst} at ({x}, {y}); " +
            "the same geometry drawn through the generated per-instance stage must land " +
            "in the same place with the same shading");
    }

    // --- rendering -----------------------------------------------------------

    private (int[,] Pixels, int DrawsSaved, int Batches, int Visible) Render(
        Func<int, Brush> brushFor, int size)
    {
        OpenGLRenderer renderer = _fixture.Renderer;

        var scene = new Scene("instancing");
        // Part brushes resolve their material through the scene, and a bare
        // scene has no AssetManager; without this every item carries a null
        // material and the geometry pass skips it, which would make the whole
        // comparison two empty frames.
        SpectraEngine.Core.Graphics.Texture white = renderer.CreateTexture(
            [255, 255, 255, 255], 1, 1, TextureFormat.Rgba8, TextureColorSpace.Linear,
            TextureFilter.Nearest, TextureWrap.Clamp);
        scene.StaticWorldMaterial = new Material(renderer.DefaultShader)
            .SetVector3("uBaseColor", new Vector3(0.8f, 0.8f, 0.8f))
            .SetFloat("uRoughness", 0.7f)
            .SetFloat("uMetallic", 0f)
            .SetFloat("uAmbientOcclusion", 1f)
            .SetVector3("uEmissive", Vector3.Zero)
            .SetFloat("uShadingModel", 0f)
            .SetTexture("uDiffuse", 0, white);

        for (int i = 0; i < Copies; i++)
        {
            SceneNode node = scene.Root.CreateChild($"Prop{i}");
            // A GRID, not a row: ten props in a line runs off the sides of the
            // frustum, and a culled prop leaves its group below the minimum
            // batch size, which reads as "batching is broken" rather than as
            // "the fixture is too wide". PartBrushesVisible is asserted for the
            // same reason.
            node.LocalPosition = new Vector3(
                ((i % 5) - 2) * 1.6f, ((i / 5) - 0.5f) * 1.8f, 0f);
            // Kind before brush, exactly as the demo places props: the brush
            // setter dirties the static world, and a part must never be admitted
            // to the placement list even for one frame.
            node.BrushKind = BrushKind.Part;
            node.Brush = brushFor(i);
        }

        var sun = scene.Root.CreateChild("Sun");
        sun.LocalRotation = Light.RotationForDirection(new Vector3(-0.35f, -0.85f, -0.4f));
        sun.Light = new Light
        {
            Kind = LightKind.Directional,
            Color = new Vector3(1f, 1f, 1f),
            Intensity = 12f,
        };

        scene.Camera.Position = new Vector3(0f, 0f, 7f);
        scene.Camera.LookAt(Vector3.Zero);

        string restorePipeline = renderer.CurrentPipelineName;
        RenderTarget probe = renderer.CreateRenderTarget(new RenderTargetDesc(size, size));
        var view = new RenderView();

        try
        {
            renderer.TrySelectPipeline("Deferred").ShouldBeTrue();
            renderer.ProbeTarget = probe;

            scene.ProcessPartBrushMeshes(renderer);

            // TWO FRAMES, and the second is the one that counts. The instance
            // buffer is sized at a frame boundary from the previous frame's
            // high-water mark, because growing it inside a pass frees a resource
            // the open command list references - so the first frame a batch ever
            // appears is drawn unbatched and correct, and the second is batched.
            // A single-frame test would measure only the fallback and would pass
            // just as happily with the instanced path completely broken.
            for (int frame = 0; frame < 2; frame++)
            {
                scene.BuildRenderView(scene.Camera, view);
                renderer.Render(scene, view, 1.0 / 60.0);
            }

            return (ReadLuminance(probe, size), renderer.GeometryDrawsSaved,
                    view.Batches.Count, view.PartBrushesVisible);
        }
        finally
        {
            renderer.ProbeTarget = null;
            renderer.DestroyRenderTarget(probe);
            scene.ReleasePartBrushMeshes(renderer);
            renderer.DestroyTexture(white);
            while (renderer.CurrentPipelineName != restorePipeline)
                renderer.NextPipeline();
        }
    }

    private unsafe int[,] ReadLuminance(RenderTarget target, int size)
    {
        GL gl = _fixture.Gl;
        uint fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fbo);
        gl.FramebufferTexture2D(
            FramebufferTarget.ReadFramebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, ((OpenGLTexture)target.ColorTexture!).Handle, 0);

        var pixels = new byte[size * size * 4];
        fixed (byte* p = pixels)
            gl.ReadPixels(0, 0, (uint)size, (uint)size, PixelFormat.Rgba, PixelType.UnsignedByte, p);

        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        gl.DeleteFramebuffer(fbo);

        var luma = new int[size, size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = ((y * size) + x) * 4;
                luma[x, y] = pixels[i] + pixels[i + 1] + pixels[i + 2];
            }
        }
        return luma;
    }

    // Pixels brighter than the sky, i.e. something was drawn there.
    private static int CountLit(int[,] frame, int size)
    {
        int background = frame[0, 0];
        int lit = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (Math.Abs(frame[x, y] - background) > 12)
                    lit++;
            }
        }
        return lit;
    }

    private static (int X, int Y, int Worst) WorstDifference(int[,] a, int[,] b, int size)
    {
        int worst = 0, wx = -1, wy = -1;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int difference = Math.Abs(a[x, y] - b[x, y]);
                if (difference > worst)
                {
                    worst = difference;
                    wx = x;
                    wy = y;
                }
            }
        }
        return (wx, wy, worst);
    }
}
