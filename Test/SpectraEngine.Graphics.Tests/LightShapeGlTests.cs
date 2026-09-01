using System.Numerics;
using Silk.NET.OpenGL;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.OpenGL;
using SpectraEngine.Core.Scene;
using Texture = SpectraEngine.Core.Graphics.Texture;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// The light SHAPES against a real driver: a cone that stops at its edge, a
/// panel that lights one side only, and an area light small enough to be a
/// point behaving like one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every failure here renders a picture rather than throwing.</b> A cone axis
/// with the wrong sign lights the hemisphere behind the lamp; a one-sidedness
/// term left out lights the room above a ceiling panel; a representative point
/// clamped to the wrong extent puts the highlight in the wrong place. None of
/// them raises a debug-layer message, none of them fails
/// <c>--offscreen-probe</c>, and the pixel is the only assertion that exists.
/// </para>
/// <para>
/// <b>The graceful-degradation case is the cheapest possible net</b> under the
/// representative-point maths: a rect light with its extents at the minimum has
/// nowhere to move its representative point to, so it must shade within a few
/// codes of a point light of the same intensity. Anything that breaks the
/// clamping, the plane intersection or the basis shows up there before it shows
/// up anywhere a human would notice.
/// </para>
/// </remarks>
[Collection(GlRendererCollection.Name)]
public sealed class LightShapeGlTests
{
    private const int ProbeSize = 64;

    private readonly GlRendererFixture _fixture;

    public LightShapeGlTests(GlRendererFixture fixture) => _fixture = fixture;

    [Fact]
    public void A_spot_lights_the_surface_inside_its_cone_and_not_the_one_outside()
    {
        // One lamp, one wall, one camera. Only the cone's outer angle changes:
        // wide enough to cover the surface, then narrow enough to miss it.
        (int wR, int wG, int wB) = Render(BuildSpotScene(outerAngle: 60f));
        (int nR, int nG, int nB) = Render(BuildSpotScene(outerAngle: 2f));

        int wide = wR + wG + wB;
        int narrow = nR + nG + nB;

        wide.ShouldBeGreaterThan(narrow * 3,
            "a surface inside the cone must be dominated by the spot and one outside it must " +
            "fall to ambient; if the two agree, the cone axis or its cosine comparison is not " +
            "reaching the light pass");
    }

    [Fact]
    public void A_spot_aimed_away_lights_nothing()
    {
        // The sign test. -l versus l inside SpotFactor is one character, it
        // compiles either way, and getting it wrong lights the hemisphere
        // BEHIND the lamp - which looks perfectly plausible until you notice
        // the cone drawn in the viewport points the other way.
        (int r, int g, int b) = Render(BuildSpotScene(outerAngle: 30f, aimAtWall: false));
        (int aR, int aG, int aB) = Render(BuildSpotScene(outerAngle: 2f));

        (r + g + b).ShouldBeLessThan((aR + aG + aB) * 2,
            "a spot pointing away from the wall must leave it at ambient");
    }

    [Fact]
    public void A_rect_light_lights_the_face_it_faces_and_not_the_one_behind_it()
    {
        // ONE-SIDEDNESS. Without it a ceiling panel also lights the room above,
        // which is invisible from below and wrong everywhere else - so it is
        // the kind of omission that ships.
        (int fR, int fG, int fB) = Render(BuildRectScene(facingWall: true));
        (int bR, int bG, int bB) = Render(BuildRectScene(facingWall: false));

        (fR + fG + fB).ShouldBeGreaterThan((bR + bG + bB) * 3,
            "a rect light turned away from the wall must not light it");
    }

    [Fact]
    public void A_rect_light_with_no_extent_matches_a_point_light_of_the_same_intensity()
    {
        (int rR, int rG, int rB) = Render(BuildRectScene(facingWall: true, width: 0.001f, height: 0.001f));
        (int pR, int pG, int pB) = Render(BuildRectScene(facingWall: true, asPoint: true));

        // Not exact: a rect light still carries its one-sided cosine term, and
        // the surface is square-on to it, so the two differ by whatever that
        // term is at normal incidence - which is one. A handful of codes of
        // slack absorbs the representative point's own rounding.
        Close(rR, pR).ShouldBeTrue($"red {rR} against {pR}");
        Close(rG, pG).ShouldBeTrue($"green {rG} against {pG}");
        Close(rB, pB).ShouldBeTrue($"blue {rB} against {pB}");

        static bool Close(int a, int b) => System.Math.Abs(a - b) <= 6;
    }

    // --- Fixtures ------------------------------------------------------------

    // A wall filling the view with its +z face at z = 0.25, and one lamp in
    // front of it. Square-on so N, L and V agree and the shading is a number
    // rather than a gradient.
    private Scene BuildScene(out SceneNode lamp)
    {
        OpenGLRenderer renderer = _fixture.Renderer;

        var scene = new Scene("shapes");
        scene.Camera.Position = new Vector3(0f, 0f, 3f);
        scene.Camera.LookAt(Vector3.Zero);

        var (vertices, indices) = Primitives.Cube();
        Mesh mesh = renderer.CreateMesh(vertices, indices, VertexAttribute.StandardLayout);

        Texture white = renderer.CreateTexture(
            [255, 255, 255, 255], 1, 1, TextureFormat.Rgba8, TextureColorSpace.Linear,
            TextureFilter.Nearest, TextureWrap.Clamp);

        var material = new Material(renderer.DefaultShader);
        material
            .SetVector3("uBaseColor", new Vector3(0.8f, 0.8f, 0.8f))
            .SetFloat("uRoughness", 0.5f)
            .SetFloat("uMetallic", 0f)
            .SetFloat("uAmbientOcclusion", 1f)
            .SetVector3("uEmissive", Vector3.Zero)
            .SetFloat("uShadingModel", 0f)
            .SetTexture("uDiffuse", 0, white);

        var wall = scene.Root.CreateChild("Wall");
        wall.LocalTransform = new Transform
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            Scale = new Vector3(8f, 8f, 0.5f),
        };
        wall.MeshRenderer = new MeshRenderer(mesh, material);

        lamp = scene.Root.CreateChild("Lamp");
        lamp.LocalPosition = new Vector3(0f, 0f, 1.25f);
        return scene;
    }

    private Scene BuildSpotScene(float outerAngle, bool aimAtWall = true)
    {
        Scene scene = BuildScene(out SceneNode lamp);

        // The direction the light TRAVELS: toward the wall is -z.
        lamp.LocalTransform = lamp.LocalTransform with
        {
            Rotation = Light.RotationForDirection(aimAtWall ? -Vector3.UnitZ : Vector3.UnitZ),
        };

        lamp.Light = new Light
        {
            Kind = LightKind.Spot,
            Color = Vector3.One,
            Intensity = 40f,
            Range = 3f,
            InnerAngle = System.Math.Max(outerAngle - 1f, 0f),
            OuterAngle = outerAngle,
        };

        return scene;
    }

    private Scene BuildRectScene(
        bool facingWall, float width = 1f, float height = 1f, bool asPoint = false)
    {
        Scene scene = BuildScene(out SceneNode lamp);

        lamp.LocalTransform = lamp.LocalTransform with
        {
            Rotation = Light.RotationForDirection(facingWall ? -Vector3.UnitZ : Vector3.UnitZ),
        };

        lamp.Light = new Light
        {
            Kind = asPoint ? LightKind.Point : LightKind.Rect,
            Color = Vector3.One,
            Intensity = 40f,
            Range = 3f,
            Width = width,
            Height = height,
        };

        return scene;
    }

    // --- Rendering -----------------------------------------------------------

    private (int R, int G, int B) Render(Scene scene)
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

            // The fixture is shared by every class in the collection: a pipeline
            // left selected here silently changes what another test renders.
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
            TextureTarget.Texture2D, ((OpenGLTexture)target.ColorTexture!).Handle, 0);

        var pixel = new byte[4];
        fixed (byte* p = pixel)
            gl.ReadPixels(x, y, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, p);

        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        gl.DeleteFramebuffer(fbo);

        return (pixel[0], pixel[1], pixel[2]);
    }
}
