using Silk.NET.OpenGL;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.OpenGL;
using SpectraEngine.Core.Scene;
using System;
using System.Numerics;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// A lone sphere self-shadows. Reproduction and oracle for an OPEN defect.
/// </summary>
/// <remarks>
/// <para>
/// <b>The suite could not see this, and that is why it shipped.</b> Every other
/// self-shadow assertion here stands on a plane -
/// <c>DeferredGlTests.A_lit_surface_with_nothing_over_it_is_not_shadowed_by_itself</c>
/// uses a cube at a 76-degree grazing sun and passes - and <c>Primitives.Sphere</c>
/// appears in no shadow test at all. The bias constants were tuned on geometry
/// that cannot exhibit the failure.
/// </para>
/// <para>
/// <b>What is measured.</b> One sphere, one directional light, nothing else in
/// the scene, so nothing can legitimately cast onto it. Turning shadows on
/// darkens it by 369 of a possible 765 at the worst pixel. Read as a BLOCK
/// rather than a pixel, because the failure is dithered by the filter's
/// per-pixel rotation and a single sample lands in a clean gap often enough to
/// pass on a visibly wrong sphere.
/// </para>
/// <para>
/// <b>Two explanations are already RULED OUT by measurement, and both were
/// confidently argued before the data arrived.</b>
/// </para>
/// <para>
/// (1) <i>Faceted hull versus smooth analytic normal.</i> There is no such depth
/// discrepancy: the receiver's world position is reconstructed from the G-buffer
/// depth, which is the same faceted triangle the depth pass rasterised, so the
/// sagitta cancels on both sides and the shading normal never enters the lookup.
/// </para>
/// <para>
/// (2) <i>PCF kernel reach exceeding the slope-scaled bias budget.</i> Refuted by
/// a sweep: at <c>FilterRadius</c> 0 the worst drop is 483, and it falls
/// monotonically to 369 at 1.2. A reach-driven fault would vanish as the reach
/// shrank; instead a wider kernel HELPS, which is what blurring in unaffected
/// neighbours does to a genuinely shadowed pixel.
/// </para>
/// <para>
/// <b>What the shape says.</b> Mapping the drop field shows a thin diagonal
/// streak one to two pixels wide, not a broad band and not scattered blotches -
/// consistent with roughly one shadow-map texel of falsely shadowed surface
/// along the sphere's silhouette AS SEEN FROM THE LIGHT, where a single texel
/// spans a large depth range and the stored depth cannot represent the surface
/// across it. That is a hypothesis, not a conclusion: the next step is to dump
/// the cascade quadrant of the depth atlas and look, which is what resolved the
/// last shadow investigation here after argument from the shaded picture had
/// failed twice.
/// </para>
/// </remarks>
[Collection(GlRendererCollection.Name)]
public sealed class ShadowCurvedReceiverGlTests
{
    private readonly GlRendererFixture _fixture;

    public ShadowCurvedReceiverGlTests(GlRendererFixture fixture) => _fixture = fixture;

    private const int ProbeSize = 96;

    // The sphere fills the frame, so a band of pixels either side of the
    // terminator is many pixels wide rather than a handful.
    private const float Radius = 0.45f;

    [Fact(Skip = "Reproduces an OPEN defect: a lone sphere self-shadows along its light silhouette. " +
                "Unskip with the fix; see the class remarks for what measurement already ruled out.")]
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
            renderer.ShadowsEnabled = true;
            int[,] on = RenderBlock();

            renderer.ShadowsEnabled = false;
            int[,] off = RenderBlock();

            (int worstX, int worstY, int worstDrop) = WorstDarkening(on, off);

            // A tolerance rather than equality: the shadow term multiplies in
            // at ShadowStrength even where nothing occludes, and the tone-mapped
            // 8-bit read has its own wobble. What acne looks like is a drop far
            // outside that.
            worstDrop.ShouldBeLessThan(24,
                $"a lone sphere darkened by {worstDrop} at ({worstX}, {worstY}) when shadows were " +
                "turned on; nothing in the scene can cast onto it, so that darkening is the sphere " +
                "shadowing itself");
        }
        finally
        {
            renderer.ShadowsEnabled = restore;
        }
    }

    // --- scene ---------------------------------------------------------------

    // One convex receiver, one grazing sun, nothing else. The camera looks along
    // the light so the terminator runs across the visible face rather than
    // hiding on the far side.
    private Scene BuildScene()
    {
        OpenGLRenderer renderer = _fixture.Renderer;

        var scene = new Scene("curved-receiver");
        scene.Camera.Position = new Vector3(0f, 0f, 2.2f);
        scene.Camera.LookAt(Vector3.Zero);

        (float[] vertices, uint[] indices) = Primitives.Sphere();
        Mesh mesh = renderer.CreateMesh(vertices, indices, VertexAttribute.StandardLayout);
        SpectraEngine.Core.Graphics.Texture white = renderer.CreateTexture(
            [255, 255, 255, 255], 1, 1, TextureFormat.Rgba8, TextureColorSpace.Linear,
            TextureFilter.Nearest, TextureWrap.Clamp);

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

    private int[,] RenderBlock()
    {
        OpenGLRenderer renderer = _fixture.Renderer;
        Scene scene = BuildScene();

        string restorePipeline = renderer.CurrentPipelineName;
        RenderTarget probe = renderer.CreateRenderTarget(new RenderTargetDesc(ProbeSize, ProbeSize));
        var view = new RenderView();

        try
        {
            renderer.TrySelectPipeline("Deferred").ShouldBeTrue();
            renderer.ProbeTarget = probe;

            scene.BuildRenderView(scene.Camera, view);
            renderer.Render(scene, view, 1.0 / 60.0);

            return ReadLuminance(probe);
        }
        finally
        {
            renderer.ProbeTarget = null;
            renderer.DestroyRenderTarget(probe);
            while (renderer.CurrentPipelineName != restorePipeline)
                renderer.NextPipeline();
        }
    }

    private unsafe int[,] ReadLuminance(RenderTarget target)
    {
        GL gl = _fixture.Gl;
        uint fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fbo);
        gl.FramebufferTexture2D(
            FramebufferTarget.ReadFramebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, ((OpenGLTexture)target.ColorTexture!).Handle, 0);

        var pixels = new byte[ProbeSize * ProbeSize * 4];
        fixed (byte* p = pixels)
            gl.ReadPixels(0, 0, ProbeSize, ProbeSize, PixelFormat.Rgba, PixelType.UnsignedByte, p);

        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        gl.DeleteFramebuffer(fbo);

        var luma = new int[ProbeSize, ProbeSize];
        for (int y = 0; y < ProbeSize; y++)
        {
            for (int x = 0; x < ProbeSize; x++)
            {
                int i = ((y * ProbeSize) + x) * 4;
                luma[x, y] = pixels[i] + pixels[i + 1] + pixels[i + 2];
            }
        }
        return luma;
    }

    // The largest drop anywhere the surface is still meaningfully lit. Pixels
    // that are already dark with shadows OFF are skipped: the far side of the
    // sphere is unlit by Lambert, and a shadow term there proves nothing.
    private static (int X, int Y, int Drop) WorstDarkening(int[,] on, int[,] off)
    {
        int worst = 0, worstX = -1, worstY = -1;
        for (int y = 0; y < ProbeSize; y++)
        {
            for (int x = 0; x < ProbeSize; x++)
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
