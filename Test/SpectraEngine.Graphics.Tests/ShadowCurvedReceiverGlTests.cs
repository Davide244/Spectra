using Silk.NET.Maths;
using Silk.NET.OpenGL;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.OpenGL;
using SpectraEngine.Core.Scene;
using System;
using System.Numerics;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// A lone sphere must not shadow itself along its terminator, and the harness
/// discipline that measuring it depends on.
/// </summary>
/// <remarks>
/// <para>
/// <b>THE PROBE MUST BE THE SAME SIZE AS THE G-BUFFER, and an earlier version of
/// this file got that wrong and measured itself.</b> The G-buffer is sized to the
/// WINDOW and deliberately never follows the frame target (following it resizes
/// the G-buffer mid-command-list, which on D3D12 releases a resource the open
/// list still references). A deferred render into a differently sized probe
/// therefore reconstructs world position from a depth buffer at one resolution
/// while the light pass runs at another, and that produces false self-shadowing
/// several times larger than the real defect: <b>369 at a 96 probe, 207 at 128,
/// 420 at 256 and 489 at 512, all against a 64x64 G-buffer</b>. Every conclusion
/// drawn from those numbers was about a condition the demo does not have. The
/// tests here drive the framebuffer latch instead, so the G-buffer follows.
/// </para>
/// <para>
/// <b>The defect this was written for, measured with the two matched.</b> Worst
/// darkening of a lone sphere, shadows on against off, before the fix: <b>45 at
/// 64, 66 at 128, 90 at 256, 102 at 512 and 102 at 1024</b> - it converges, so
/// 102 of a possible 765 was the artifact's true size rather than a sampling
/// accident. The shape was a thin arc along the terminator, a few pixels wide,
/// which is what the report of mottling on the demo's PBR spheres looked like.
/// </para>
/// <para>
/// <b>THE FIX: the slope-scaled raster bias has to cover the FILTER'S
/// FOOTPRINT, and the value it shipped with covered one texel.</b> A tap does
/// not compare the receiver against its own texel but against one up to
/// <c>FilterRadius</c> away plus the texel the bilinear weighting straddles, so
/// the bias must span the depth change across all of that. The smallest slope
/// term leaving zero false self-shadowing, swept against filter radius: radius
/// 0, 0.4 and 0.8 need 6; radius 1.2 (the default) needs 8; radius 2 needs 10;
/// radius 3 needs 14. The shipped 2.5 was below what even a zero-radius filter
/// needs. <b>Widening the filter without raising the bias brings this straight
/// back</b>, which is why the two are documented against each other on
/// <c>ShadowMap.RasterBias</c>.
/// </para>
/// <para>
/// <b>The depth pass is innocent.</b> <c>ShadowStrength = 0</c> makes
/// <c>ShadowFactor</c> return 1.0 on its first line while the depth pass still
/// runs in full, and that reads a drop of 0 at every size. The whole effect is in
/// the lookup.
/// </para>
/// <para>
/// <b>The filter sweep is the evidence for the mechanism.</b> Worst darkening
/// against <c>FilterRadius</c> at the shipped bias: 45, 45, 69, 102, 162, 258 at
/// radius 0, 0.4, 0.8, 1.2, 2 and 3. A wider filter reaches further across a
/// grazing surface and the artifact grows with it, monotonically. An earlier
/// sweep appeared to show the opposite and was used to rule this out; it was
/// taken on the mismatched harness above and measured nothing.
/// </para>
/// <para>
/// <b>What the lookup is doing, established by replaying it on the CPU against
/// the real depth atlas and the real matrices</b> (that analysis touches no
/// G-buffer, so the harness fault above does not reach it). Sampling the exact
/// triangles of the sphere, at points on facets whose own plane faces the light
/// and which are therefore present in the map: a SINGLE-TEXEL comparison is
/// clean at all 2080 of them, and the filter's own neighbourhood falsely shadows
/// <b>243</b>, worst factor 0.526. So the tap offset is the mechanism, not the
/// bias.
/// </para>
/// <para>
/// <b>Receiver-plane depth bias is the textbook answer and it did NOT work
/// here.</b> Implemented for real (screen-space derivatives of world position,
/// solved for the receiver's depth gradient, applied per tap; it needed
/// <c>Math.Ddx</c>/<c>Math.Ddy</c> added to SpectraShade) and measured at 512
/// with the G-buffer matched, it made the artifact WORSE at every useful
/// setting: 102 with the correction disabled, then 120, 171 and 219 as the
/// gradient clamp was raised, and only 57 at a clamp of 0.5. Flipping the sign
/// was uniformly and monotonically worse, which confirms the derivation pointed
/// the right way. The reason it overshoots is curvature: at 512 one screen pixel
/// spans about 0.46 shadow texels while the taps sit 1.2 texels out, so a plane
/// fitted over a one-pixel baseline is extrapolated well past where a curved,
/// grazing surface still resembles that plane. On the CPU, the same correction
/// taken from the exact FACET plane removes all 243, and taken from the smooth
/// shading normal leaves 34 - so the technique is sound and the estimator is
/// what fails.
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

    // Where the artifact stops growing with resolution. Below this a measurement
    // understates it: the same scene reads 45 at 64 and 102 from 512 up.
    private const int ConvergedSize = 512;

    /// <summary>
    /// The invariant every measurement in this file stands on, asserted rather
    /// than assumed.
    /// </summary>
    /// <remarks>
    /// This is the one test here that passes today, and it exists because the
    /// trap it guards cost a whole investigation: if the G-buffer ever stops
    /// following the framebuffer latch, the two oracles below silently go back
    /// to measuring resampling instead of the renderer, and they would still
    /// produce a plausible-looking number while doing it.
    /// </remarks>
    [Fact]
    public void The_gbuffer_follows_the_framebuffer_latch()
    {
        OpenGLRenderer renderer = _fixture.Renderer;
        renderer.GetFramebufferSize(out int restoreWidth, out int restoreHeight);

        try
        {
            foreach (int size in new[] { 64, 128, 256 })
            {
                renderer.SetFramebufferSize(new Vector2D<int>(size, size));
                RenderBlock(size);

                GBuffer gbuffer = renderer.GBuffer!;
                gbuffer.Width.ShouldBe(size, "the G-buffer must follow the framebuffer latch");
                gbuffer.Height.ShouldBe(size, "the G-buffer must follow the framebuffer latch");
            }
        }
        finally
        {
            renderer.SetFramebufferSize(new Vector2D<int>(restoreWidth, restoreHeight));
        }
    }

    [Fact]
    public void A_lit_sphere_is_not_shadowed_by_itself()
    {
        // The oracle: render the same sphere with shadows on and off, and
        // compare a block straddling the terminator. A correct shadow map
        // changes nothing on a lone convex receiver, because there is nothing
        // else in the scene to cast onto it and its own far side is dark by
        // Lambert rather than by occlusion.
        OpenGLRenderer renderer = _fixture.Renderer;
        bool restoreShadows = renderer.ShadowsEnabled;
        renderer.GetFramebufferSize(out int restoreWidth, out int restoreHeight);

        try
        {
            renderer.SetFramebufferSize(new Vector2D<int>(ConvergedSize, ConvergedSize));

            renderer.ShadowsEnabled = true;
            int[,] on = RenderBlock(ConvergedSize);
            renderer.GBuffer!.Width.ShouldBe(ConvergedSize, "or this measures resampling");

            renderer.ShadowsEnabled = false;
            int[,] off = RenderBlock(ConvergedSize);

            (int worstX, int worstY, int worstDrop) = WorstDarkening(on, off, ConvergedSize);

            // A tolerance rather than equality: the shadow term multiplies in at
            // ShadowStrength wherever the filter straddles the terminator, and
            // the tone-mapped 8-bit read has its own wobble. Acne is a drop far
            // outside that.
            worstDrop.ShouldBeLessThan(24,
                $"a lone sphere darkened by {worstDrop} at ({worstX}, {worstY}) when shadows were " +
                "turned on; nothing in the scene can cast onto it, so that darkening is the sphere " +
                "shadowing itself");
        }
        finally
        {
            renderer.ShadowsEnabled = restoreShadows;
            renderer.SetFramebufferSize(new Vector2D<int>(restoreWidth, restoreHeight));
        }
    }

    [Fact(Skip = "Reproduces an OPEN defect: a deferred frame rendered into a target that is not " +
                "the G-buffer's size falsely self-shadows, by up to 5x what it does at matched " +
                "size. --offscreen-probe runs exactly this path and reads only debug-layer error " +
                "counts, so nothing in the repo can currently see it.")]
    public void A_deferred_frame_does_not_depend_on_the_target_size()
    {
        OpenGLRenderer renderer = _fixture.Renderer;
        bool restoreShadows = renderer.ShadowsEnabled;
        renderer.GetFramebufferSize(out int restoreWidth, out int restoreHeight);

        try
        {
            renderer.GetFramebufferSize(out int width, out int height);
            int matched = Math.Min(width, height);
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
            renderer.ShadowsEnabled = restoreShadows;
            renderer.SetFramebufferSize(new Vector2D<int>(restoreWidth, restoreHeight));
        }
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
