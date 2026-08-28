using Silk.NET.OpenGL;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.OpenGL;
using SpectraEngine.Core.Scene;
using System;
using System.Numerics;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// A lone sphere must not shadow itself, and a deferred frame must not depend on
/// the size of the target it is rendered into.
/// </summary>
/// <remarks>
/// <para>
/// <b>THE PROBE TARGET MUST BE THE SAME SIZE AS THE G-BUFFER, and getting that
/// wrong is what an earlier version of this file did.</b> The G-buffer is sized
/// to the WINDOW and deliberately never follows the frame target (see
/// <c>Renderer.EnsureGBuffer</c>: following the target resizes it mid-command-list,
/// which on D3D12 releases a resource the open list still references). So a
/// deferred render into a differently sized probe reconstructs world position
/// from a depth buffer at one resolution while the light pass runs at another.
/// </para>
/// <para>
/// That mismatch produces false self-shadowing an order of magnitude larger than
/// anything the shadow code does wrong. Measured on this fixture, worst darkening
/// of a lone sphere with shadows on versus off: <b>45 at 64x64 into a 64x64
/// G-buffer, 369 at 96x96, 207 at 128x128, 420 at 256x256, 489 at 512x512</b> -
/// the artifact tracks the mismatch ratio and nothing else. A test that picked
/// its own probe size therefore measured the harness, and every bias conclusion
/// drawn from it was about a condition the demo never has.
/// </para>
/// <para>
/// <b>The control that settles it</b> is <c>ShadowStrength = 0</c>, which makes
/// <c>ShadowFactor</c> return 1.0 on its first line while the depth pass still
/// runs in full. It reads 0 at every size, so the depth pass is innocent and the
/// darkening is entirely in the lookup.
/// </para>
/// <para>
/// <b>Two explanations for curved-receiver acne are ruled out by measurement</b>,
/// and both were confidently argued before the data arrived. (1) Faceted hull
/// versus smooth analytic normal: there is no such depth discrepancy, because the
/// receiver's world position is reconstructed from the G-buffer depth, which is
/// the same faceted triangle the depth pass rasterised. Replacing the sphere's
/// smooth normals with per-facet ones made the number WORSE (405 against 369).
/// (2) PCF kernel reach exceeding the slope-scaled bias budget: at
/// <c>FilterRadius</c> 0 the worst drop is larger, not smaller, and falls
/// monotonically as the radius grows.
/// </para>
/// </remarks>
[Collection(GlRendererCollection.Name)]
public sealed class ShadowCurvedReceiverGlTests
{
    private readonly GlRendererFixture _fixture;

    public ShadowCurvedReceiverGlTests(GlRendererFixture fixture) => _fixture = fixture;

    // The sphere fills the frame, so a band of pixels either side of the
    // terminator is many pixels wide rather than a handful.
    private const float Radius = 0.45f;

    [Fact]
    public void A_lit_sphere_is_not_shadowed_by_itself()
    {
        // The oracle: render the same sphere with shadows on and off, and
        // compare a block straddling the terminator. A correct shadow map
        // changes nothing on a lone convex receiver, because there is nothing
        // else in the scene to cast onto it and its own far side is dark by
        // Lambert rather than by occlusion.
        OpenGLRenderer renderer = _fixture.Renderer;
        bool restore = renderer.ShadowsEnabled;

        try
        {
            int size = GBufferSize();

            renderer.ShadowsEnabled = true;
            int[,] on = RenderBlock(size);

            renderer.ShadowsEnabled = false;
            int[,] off = RenderBlock(size);

            (int worstX, int worstY, int worstDrop) = WorstDarkening(on, off, size);

            // A tolerance rather than equality: the shadow term multiplies in at
            // ShadowStrength wherever the filter straddles the terminator, and
            // the tone-mapped 8-bit read has its own wobble. Acne is a drop far
            // outside that; the measured value here is 45.
            worstDrop.ShouldBeLessThan(64,
                $"a lone sphere darkened by {worstDrop} at ({worstX}, {worstY}) when shadows were " +
                "turned on; nothing in the scene can cast onto it, so that darkening is the sphere " +
                "shadowing itself");
        }
        finally
        {
            renderer.ShadowsEnabled = restore;
        }
    }

    [Fact(Skip = "Reproduces an OPEN defect: a deferred frame rendered into a target that is not " +
                "the G-buffer's size falsely self-shadows, by up to 8x what it does at matched " +
                "size. --offscreen-probe runs exactly this path and reads only debug-layer error " +
                "counts, so nothing in the repo can currently see it.")]
    public void A_deferred_frame_does_not_depend_on_the_target_size()
    {
        OpenGLRenderer renderer = _fixture.Renderer;
        bool restore = renderer.ShadowsEnabled;

        try
        {
            int matched = GBufferSize();
            int mismatched = matched + (matched / 2);

            renderer.ShadowsEnabled = true;
            int[,] onA = RenderBlock(matched);
            renderer.ShadowsEnabled = false;
            int[,] offA = RenderBlock(matched);

            renderer.ShadowsEnabled = true;
            int[,] onB = RenderBlock(mismatched);
            renderer.ShadowsEnabled = false;
            int[,] offB = RenderBlock(mismatched);

            int atMatched = WorstDarkening(onA, offA, matched).Drop;
            int atMismatched = WorstDarkening(onB, offB, mismatched).Drop;

            atMismatched.ShouldBeLessThan(atMatched + 32,
                $"the same scene self-shadowed by {atMatched} rendered at {matched}x{matched} " +
                $"(the G-buffer's own size) and by {atMismatched} at {mismatched}x{mismatched}; " +
                "the picture must not depend on the target's size");
        }
        finally
        {
            renderer.ShadowsEnabled = restore;
        }
    }

    // The G-buffer follows the window, so this is the only probe size that
    // measures the renderer rather than the resampling. Asserted rather than
    // assumed: if that sizing rule ever changes, this must fail loudly instead
    // of quietly going back to measuring the harness.
    private int GBufferSize()
    {
        OpenGLRenderer renderer = _fixture.Renderer;
        renderer.GetFramebufferSize(out int width, out int height);
        int size = Math.Min(width, height);

        RenderBlock(size);
        GBuffer gbuffer = renderer.GBuffer!;
        gbuffer.Width.ShouldBe(size, "the probe must match the G-buffer, or it measures resampling");
        gbuffer.Height.ShouldBe(size, "the probe must match the G-buffer, or it measures resampling");
        return size;
    }

    // --- scene ---------------------------------------------------------------

    // One convex receiver, one grazing sun, nothing else. The camera looks along
    // the light so the terminator runs across the visible face rather than
    // hiding on the far side.
    private Scene BuildScene(Mesh mesh, SpectraEngine.Core.Graphics.Texture white)
    {
        OpenGLRenderer renderer = _fixture.Renderer;

        var scene = new Scene("curved-receiver");
        scene.Camera.Position = new Vector3(0f, 0f, 2.2f);
        scene.Camera.LookAt(Vector3.Zero);

        var node = scene.Root.CreateChild("Receiver");
        node.LocalTransform = new Transform
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            // The demo's own sphere size: Primitives.Sphere is radius 0.5 and
            // SceneManager scales it 0.9.
            Scale = new Vector3(Radius * 2f),
        };
        node.MeshRenderer = new MeshRenderer(mesh, new Material(renderer.DefaultShader)
            .SetVector3("uBaseColor", new Vector3(0.9f, 0.9f, 0.9f))
            .SetFloat("uRoughness", 0.85f)
            .SetFloat("uMetallic", 0f)
            .SetFloat("uAmbientOcclusion", 1f)
            .SetVector3("uEmissive", Vector3.Zero)
            .SetFloat("uShadingModel", 0f)
            .SetTexture("uDiffuse", 0, white));

        // The DEMO'S OWN sun direction, because the artifact was reported on the
        // demo. A more grazing light puts the whole visible hemisphere into the
        // failing band, which reproduces something louder than the defect and
        // would pass the moment the defect alone was fixed.
        var sun = scene.Root.CreateChild("Sun");
        sun.LocalRotation = Light.RotationForDirection(new Vector3(-0.35f, -0.85f, -0.4f));
        sun.Light = new Light
        {
            Kind = LightKind.Directional,
            Color = new Vector3(1f, 1f, 1f),
            Intensity = 14f,
        };

        return scene;
    }

    // --- rendering -----------------------------------------------------------

    private int[,] RenderBlock(int size)
    {
        OpenGLRenderer renderer = _fixture.Renderer;

        (float[] vertices, uint[] indices) = Primitives.Sphere();
        Mesh mesh = renderer.CreateMesh(vertices, indices, VertexAttribute.StandardLayout);
        SpectraEngine.Core.Graphics.Texture white = renderer.CreateTexture(
            [255, 255, 255, 255], 1, 1, TextureFormat.Rgba8, TextureColorSpace.Linear,
            TextureFilter.Nearest, TextureWrap.Clamp);

        Scene scene = BuildScene(mesh, white);

        string restorePipeline = renderer.CurrentPipelineName;
        RenderTarget probe = renderer.CreateRenderTarget(new RenderTargetDesc(size, size));
        var view = new RenderView();

        try
        {
            renderer.TrySelectPipeline("Deferred").ShouldBeTrue();
            renderer.ProbeTarget = probe;

            scene.BuildRenderView(scene.Camera, view);
            renderer.Render(scene, view, 1.0 / 60.0);

            return ReadLuminance(probe, size);
        }
        finally
        {
            renderer.ProbeTarget = null;
            renderer.DestroyRenderTarget(probe);
            // Destroyed rather than leaked: this runs several times per test,
            // and a mesh and texture per render is the kind of drift that makes
            // a later measurement disagree with an earlier one for no visible
            // reason.
            renderer.DestroyMesh(mesh);
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

    // The largest drop anywhere the surface is still meaningfully lit. Pixels
    // that are already dark with shadows OFF are skipped: the far side of the
    // sphere is unlit by Lambert, and a shadow term there proves nothing.
    private static (int X, int Y, int Drop) WorstDarkening(int[,] on, int[,] off, int size)
    {
        int worst = 0, worstX = -1, worstY = -1;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (off[x, y] < 40)
                    continue;

                int drop = off[x, y] - on[x, y];
                if (drop > worst)
                {
                    worst = drop;
                    worstX = x;
                    worstY = y;
                }
            }
        }
        return (worstX, worstY, worst);
    }
}
