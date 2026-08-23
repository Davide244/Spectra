using System.Numerics;
using Silk.NET.OpenGL;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.OpenGL;
using SpectraEngine.Core.Scene;
using Texture = SpectraEngine.Core.Graphics.Texture;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// The deferred pipeline against a real driver: does the two-pass split
/// actually shade, and does it shade the right place.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every failure this guards against renders a picture.</b> A G-buffer pass
/// that writes only attachment zero, a light pass that samples the wrong
/// footprint, a world position reconstructed on the right ray at the wrong
/// distance. None of them throws, none of them fails a framebuffer-completeness
/// check, and none of them makes a debug layer say anything. The output is
/// simply lit wrongly, so the assertion has to be a pixel.
/// </para>
/// <para>
/// <b>The lit-versus-unlit test is really a reconstruction test.</b> The point
/// light is placed one world unit from the surface with a range of two, so it
/// reaches only if the position the light pass reconstructed from depth is
/// within a unit of where the geometry pass actually put that pixel. Get the
/// depth-to-NDC remap wrong (the one thing that genuinely differs between
/// OpenGL and D3D here) and the reconstructed point lands several units deep,
/// falls outside the range, and the surface drops to ambient.
/// </para>
/// <para>
/// Driven through <see cref="Renderer.ProbeTarget"/>, which renders a real frame
/// into a readable target before the window gets its own. That is the seam that
/// already exists for exactly this, and using it means the test measures the
/// engine's own frame rather than a rehearsal of one.
/// </para>
/// </remarks>
[Collection(GlRendererCollection.Name)]
public sealed class DeferredGlTests
{
    private const int ProbeSize = 64;

    private readonly GlRendererFixture _fixture;

    public DeferredGlTests(GlRendererFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Pixels_no_geometry_covered_come_out_sky_coloured()
    {
        // A full-screen triangle covers every pixel, so unlike a forward pass
        // the light pass cannot leave the background alone: it has to recognise
        // the cleared depth and put the sky back. If it does not, the shading
        // reads a zeroed normal against a zeroed albedo and the frame is black.
        var scene = new Scene("empty");
        scene.Camera.Position = new Vector3(0f, 0f, 3f);
        scene.Camera.LookAt(Vector3.Zero);

        (int r, int g, int b) = RenderDeferred(scene);

        b.ShouldBeGreaterThan(r, "the sky is cornflower blue, so blue must dominate red");
        b.ShouldBeGreaterThan(g, "the sky is cornflower blue, so blue must dominate green");
    }

    [Fact]
    public void A_surface_within_a_lights_range_is_lit_and_outside_it_is_not()
    {
        // Same geometry and the same light both times; only the range changes.
        // A range that reaches gives a bright surface, one that stops short
        // gives ambient, and the difference is only correct if the light pass
        // reconstructed this pixel's world position to within a unit.
        (int litR, int litG, int litB) = RenderDeferred(BuildSurfaceScene(lightRange: 2f));
        (int dimR, int dimG, int dimB) = RenderDeferred(BuildSurfaceScene(lightRange: 0.4f));

        int lit = litR + litG + litB;
        int dim = dimR + dimG + dimB;

        lit.ShouldBeGreaterThan(dim * 3,
            "a light one unit from the surface with a range of two should dominate ambient; " +
            "if it does not, the world position reconstructed from depth is not where the " +
            "geometry pass put this pixel");
    }

    [Fact]
    public void A_metal_and_a_dielectric_of_the_same_colour_shade_differently()
    {
        // Metallic is stored in the G-buffer and read in the light pass, which
        // is two hops either of which can silently drop it. A metal has no
        // diffuse response at all, so the same base colour under the same light
        // must not produce the same pixel.
        // Dimmer than the range test on purpose: that one wants a margin over
        // ambient, this one wants both results BELOW the 8-bit ceiling, because
        // two different numbers that both clamp to 255 compare equal.
        (int dR, int dG, int dB) = RenderDeferred(
            BuildSurfaceScene(lightRange: 2f, metallic: 0f, intensity: 6f));
        (int mR, int mG, int mB) = RenderDeferred(
            BuildSurfaceScene(lightRange: 2f, metallic: 1f, intensity: 6f));

        (dR == mR && dG == mG && dB == mB).ShouldBeFalse(
            $"metallic 0 and metallic 1 both shaded to ({dR}, {dG}, {dB}); " +
            "the metallic channel is not reaching the BRDF");
    }

    // A flat surface square-on to the camera, one point light in front of it.
    // Square-on so N, L and V all agree and the shading is a number that is easy
    // to reason about rather than a gradient.
    private Scene BuildSurfaceScene(float lightRange, float metallic = 0f, float intensity = 40f)
    {
        OpenGLRenderer renderer = _fixture.Renderer;

        var scene = new Scene("surface");
        scene.Camera.Position = new Vector3(0f, 0f, 3f);
        scene.Camera.LookAt(Vector3.Zero);

        var (vertices, indices) = Primitives.Cube();
        Mesh mesh = renderer.CreateMesh(vertices, indices, VertexAttribute.StandardLayout);

        // White, so the base colour reaches the G-buffer unmodulated and any
        // difference between the two runs is the material parameters.
        Texture white = renderer.CreateTexture(
            [255, 255, 255, 255], 1, 1, TextureFormat.Rgba8, TextureColorSpace.Linear,
            TextureFilter.Nearest, TextureWrap.Clamp);

        var material = new Material(renderer.DefaultShader);
        material
            .SetVector3("uBaseColor", new Vector3(0.8f, 0.8f, 0.8f))
            .SetFloat("uRoughness", 0.5f)
            .SetFloat("uMetallic", metallic)
            .SetFloat("uAmbientOcclusion", 1f)
            .SetVector3("uEmissive", Vector3.Zero)
            .SetFloat("uShadingModel", 0f)
            .SetTexture("uDiffuse", 0, white);

        // Wide enough to fill the view at this distance, thin enough that its
        // front face is at a known z.
        var wall = scene.Root.CreateChild("Wall");
        wall.LocalTransform = new Transform
        {
            Position = new Vector3(0f, 0f, 0f),
            Rotation = Quaternion.Identity,
            Scale = new Vector3(8f, 8f, 0.5f),
        };
        wall.MeshRenderer = new MeshRenderer(mesh, material);

        // Exactly one unit in front of the wall's +z face, which sits at z=0.25.
        var lamp = scene.Root.CreateChild("Lamp");
        lamp.LocalPosition = new Vector3(0f, 0f, 1.25f);
        lamp.Light = new Light
        {
            Kind = LightKind.Point,
            Color = new Vector3(1f, 1f, 1f),
            Intensity = intensity,
            Range = lightRange,
        };

        return scene;
    }

    // Renders one real frame of the deferred pipeline into a readable target
    // and returns the centre pixel.
    private (int R, int G, int B) RenderDeferred(Scene scene)
    {
        OpenGLRenderer renderer = _fixture.Renderer;

        string restore = renderer.CurrentPipelineName;
        RenderTarget probe = renderer.CreateRenderTarget(new RenderTargetDesc(ProbeSize, ProbeSize));
        var view = new RenderView();

        try
        {
            renderer.TrySelectPipeline("Deferred").ShouldBeTrue();
            renderer.ProbeTarget = probe;

            scene.BuildRenderView(scene.Camera, view);
            renderer.Render(scene, view, 1.0 / 60.0);

            return ReadPixel(probe, ProbeSize / 2, ProbeSize / 2);
        }
        finally
        {
            renderer.ProbeTarget = null;
            renderer.DestroyRenderTarget(probe);

            // Put the rotation back where it was: the fixture is shared by every
            // class in the collection, and a pipeline left selected here would
            // silently change what another test renders.
            while (renderer.CurrentPipelineName != restore)
                renderer.NextPipeline();
        }
    }

    private unsafe (int R, int G, int B) ReadPixel(RenderTarget target, int x, int y)
    {
        GL gl = _fixture.Gl;
        uint fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fbo);
        gl.FramebufferTexture2D(
            FramebufferTarget.ReadFramebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, ((OpenGLTexture)target.ColorTexture).Handle, 0);

        var pixel = new byte[4];
        fixed (byte* p = pixel)
            gl.ReadPixels(x, y, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, p);

        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        gl.DeleteFramebuffer(fbo);
        return (pixel[0], pixel[1], pixel[2]);
    }
}
