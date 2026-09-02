using System.Numerics;
using Silk.NET.OpenGL;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.OpenGL;
using SpectraEngine.Core.Scene;
using Texture = SpectraEngine.Core.Graphics.Texture;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// The depth-tested world-line lane: a line behind geometry is hidden, and the
/// same line in front of it is drawn.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the whole reason the lane exists</b>, and it cannot be checked any
/// other way. The engine's overlay draws depth-OFF by design - correct for gizmo
/// handles, which must never be hidden by what they manipulate - and a ground
/// grid on that lane draws straight through walls. The failure renders a
/// picture: no error, no debug-layer message, and a viewport that looks busy
/// rather than broken until somebody notices the floor showing through a
/// building.
/// </para>
/// <para>
/// <b>Both directions, deliberately.</b> A test that only checked "hidden
/// behind" passes just as happily when the lane draws nothing at all.
/// </para>
/// </remarks>
[Collection(GlRendererCollection.Name)]
public sealed class WorldLineGlTests
{
    private const int ProbeSize = 64;

    // Bright green: unlike the sky, unlike the wall, and unlike anything the
    // ambient term produces from a grey albedo.
    private static readonly Vector3 LineColor = new(0f, 1f, 0f);

    private readonly GlRendererFixture _fixture;

    public WorldLineGlTests(GlRendererFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("Deferred")]
    [InlineData("Forward")]
    public void A_world_line_behind_geometry_is_hidden_and_in_front_of_it_is_drawn(string pipeline)
    {
        // Both pipelines, because they flush the lane from DIFFERENT passes and
        // with different depth mechanisms: forward hardware-tests inside its
        // own scene pass, deferred draws AFTER the light pass and compares
        // against the sampled G-buffer depth in the shader. Only one of them
        // can be right by accident.
        (int bR, int bG, int bB) = Render(pipeline, lineZ: -4f);
        (int fR, int fG, int fB) = Render(pipeline, lineZ: 1.5f);

        bG.ShouldBeLessThan(200,
            $"[{pipeline}] a world line four units behind the wall must be occluded by it; " +
            "if it is not, the lane is drawing with depth testing off and a ground grid " +
            "would show through every building in the level");

        fG.ShouldBeGreaterThan(bG + 40,
            $"[{pipeline}] the same line in FRONT of the wall must be drawn; without this half " +
            "the test passes when the lane draws nothing at all " +
            $"(behind {bR},{bG},{bB} against in-front {fR},{fG},{fB})");
    }

    [Fact]
    public void A_world_line_lying_exactly_on_a_surface_is_drawn_rather_than_rejected()
    {
        // The case the whole feature exists for: a grid at y = 0 over a floor
        // whose top is also at y = 0 is coplanar by construction, and a strict
        // Less comparison rejects it. GL defaults to Less, so getting this wrong
        // makes one backend disagree with the other two about whether the grid
        // is visible at all.
        (int _, int onG, int _) = Render("Deferred", lineZ: WallFrontZ);
        (int _, int behindG, int _) = Render("Deferred", lineZ: -4f);

        onG.ShouldBeGreaterThan(behindG + 40,
            "a line exactly on the surface must win the depth tie; a strict Less comparison " +
            "rejects exactly the case the grid exists for");
    }

    [Theory]
    [InlineData("Deferred")]
    [InlineData("Forward")]
    public void A_world_line_behind_a_STATIC_WORLD_brush_is_hidden_by_it(string pipeline)
    {
        // The demo's walls are compiled static-world chunks, not mesh nodes, and
        // they take a different route through DrawGeometry. A test that only
        // ever put a mesh in front of the line proves nothing about the geometry
        // most of a level is made of.
        // FIRST: the wall is actually on screen. Without this the test cannot
        // tell "the line is drawing through the wall" from "the static world
        // never compiled and the line is over sky", and those want opposite
        // fixes.
        (int wallR, int wallG, int wallB) = RenderAgainstBrush(pipeline, lineZ: null);
        wallR.ShouldBeGreaterThan(wallB,
            $"[{pipeline}] the compiled brush wall is not covering the centre pixel " +
            $"(got {wallR},{wallG},{wallB}); the rest of this test would be measuring sky");

        (int _, int bG, int _) = RenderAgainstBrush(pipeline, lineZ: -190f);
        (int _, int fG, int _) = RenderAgainstBrush(pipeline, lineZ: 1.5f);

        bG.ShouldBeLessThan(200,
            $"[{pipeline}] a world line thirty units behind a compiled world brush must be " +
            "occluded by it");

        fG.ShouldBeGreaterThan(bG + 40,
            $"[{pipeline}] and the same line in front of it must still be drawn");
    }

    [Theory]
    [InlineData("Deferred")]
    [InlineData("Forward")]
    public void A_faded_world_line_blends_toward_the_surface_instead_of_toward_black(string pipeline)
    {
        // THE oracle the old lane could not pass, and the reason it was
        // rebuilt. Lines used to render as opaque overwrites whose "fade" was
        // a colour lerp toward black - which over a lit red wall REPLACED the
        // wall pixel with a near-black one, so a fading line gained contrast
        // instead of losing it and then vanished in one frame at a cull
        // threshold. A real fade is alpha: the wall's own colour must survive
        // underneath in proportion.
        (int wallR, int wallG, int _) = Render(pipeline, lineZ: WallFrontZ, opacity: 0f);
        (int fullR, int fullG, int _) = Render(pipeline, lineZ: WallFrontZ);
        (int fadedR, int fadedG, int _) = Render(pipeline, lineZ: WallFrontZ, opacity: 0.3f);

        fullG.ShouldBeGreaterThan(wallG + 60,
            $"[{pipeline}] the control is broken: the full-strength line is not visible over " +
            $"the wall at all (wall {wallR},{wallG} against line {fullR},{fullG})");

        fadedG.ShouldBeGreaterThan(wallG + 10,
            $"[{pipeline}] a 30% line must still be visible");
        fadedG.ShouldBeLessThan(fullG - 10,
            $"[{pipeline}] a 30% line must be dimmer than a full one - if these match, the " +
            "opacity is not reaching the shader");

        // The discriminator: under alpha the wall's red shows through a faded
        // line; under the old overwrite model this pixel was black albedo plus
        // a whisper of green emissive, and red read near zero.
        fadedR.ShouldBeGreaterThan(wallR / 2,
            $"[{pipeline}] the wall's red must survive under a 30% line " +
            $"(wall red {wallR}, faded-line red {fadedR}); losing it means the line is " +
            "REPLACING the pixel rather than blending over it");
    }

    [Theory]
    [InlineData("Deferred")]
    [InlineData("Forward")]
    public void The_distance_fade_dims_a_line_per_pixel_along_its_length(string pipeline)
    {
        // The falloff is computed in the fragment stage from world distance,
        // so ONE line must be bright where it is near the fade centre and gone
        // where it is not - no segments, no per-segment culls.
        var fade = (Center: new Vector3(0f, 0f, WallFrontZ), Start: 0.25f, End: 1.2f);

        (int _, int nearG, int _) = Render(pipeline, lineZ: WallFrontZ, fade: fade);
        (int _, int farG, int _) = Render(pipeline, lineZ: WallFrontZ, fade: fade, sampleX: (ProbeSize / 2) + 24);

        // The control: without the fade, the same distant sample is bright -
        // so the dimming below is the fade's doing, not the sampling position.
        (int _, int farControlG, int _) = Render(pipeline, lineZ: WallFrontZ, sampleX: (ProbeSize / 2) + 24);

        farControlG.ShouldBeGreaterThan(farG + 30,
            $"[{pipeline}] the unfaded control must be brighter at the distant sample than the " +
            $"faded line is (control {farControlG} against faded {farG}); if they match, the " +
            "fade metadata is not reaching the shader");

        nearG.ShouldBeGreaterThan(farG + 40,
            $"[{pipeline}] one line must dim along its own length as it leaves the fade window " +
            $"(near {nearG} against far {farG}); a flat brightness means the falloff is not per pixel");
    }

    // The wall's +z face, from the scale below.
    private const float WallFrontZ = 0.25f;

    private (int R, int G, int B) Render(
        string pipeline, float lineZ, float opacity = 1f,
        (Vector3 Center, float Start, float End)? fade = null, int? sampleX = null)
    {
        OpenGLRenderer renderer = _fixture.Renderer;

        string restore = renderer.CurrentPipelineName;
        RenderTarget probe = renderer.CreateRenderTarget(new RenderTargetDesc(ProbeSize, ProbeSize));
        var view = new RenderView();

        try
        {
            renderer.TrySelectPipeline(pipeline).ShouldBeTrue();
            renderer.ProbeTarget = probe;

            Scene scene = BuildWall();

            // Horizontal, through the middle of the view, wide enough that the
            // centre pixel lands on it whatever the projection does.
            renderer.WorldLines.Clear();
            renderer.WorldLines.Line(
                new Vector3(-4f, 0f, lineZ), new Vector3(4f, 0f, lineZ), LineColor);
            renderer.WorldLines.Opacity = opacity;
            if (fade is { } f)
            {
                renderer.WorldLines.FadeCenter = f.Center;
                renderer.WorldLines.FadeStart = f.Start;
                renderer.WorldLines.FadeEnd = f.End;
            }

            scene.BuildRenderView(scene.Camera, view);
            renderer.Render(scene, view, 1.0 / 60.0);

            // A short COLUMN, not one pixel. The line is one pixel wide and
            // lands on whichever row the projection rounds to, so a single
            // centre sample is a coin flip between "the lane is broken" and
            // "the line is one row up" - which is exactly the kind of flaky
            // pixel assertion that gets deleted rather than believed.
            return BrightestGreen(probe, sampleX ?? ProbeSize / 2, ProbeSize / 2, radius: 3);
        }
        finally
        {
            renderer.WorldLines.Clear();
            renderer.ProbeTarget = null;
            renderer.DestroyRenderTarget(probe);

            while (renderer.CurrentPipelineName != restore)
                renderer.NextPipeline();
        }
    }

    private (int R, int G, int B) RenderAgainstBrush(string pipeline, float? lineZ)
    {
        OpenGLRenderer renderer = _fixture.Renderer;

        string restore = renderer.CurrentPipelineName;
        RenderTarget probe = renderer.CreateRenderTarget(new RenderTargetDesc(ProbeSize, ProbeSize));
        var view = new RenderView();

        try
        {
            renderer.TrySelectPipeline(pipeline).ShouldBeTrue();
            renderer.ProbeTarget = probe;

            Scene scene = BuildBrushWall();

            renderer.WorldLines.Clear();
            if (lineZ is { } z)
            {
                renderer.WorldLines.Line(
                    new Vector3(-40f, 0f, z), new Vector3(40f, 0f, z), LineColor);
            }

            scene.BuildRenderView(scene.Camera, view);
            renderer.Render(scene, view, 1.0 / 60.0);

            return BrightestGreen(probe, ProbeSize / 2, ProbeSize / 2, radius: 3);
        }
        finally
        {
            renderer.WorldLines.Clear();
            renderer.ProbeTarget = null;
            renderer.DestroyRenderTarget(probe);

            while (renderer.CurrentPipelineName != restore)
                renderer.NextPipeline();
        }
    }

    // The same wall, as a compiled STATIC WORLD brush rather than a mesh.
    private Scene BuildBrushWall()
    {
        OpenGLRenderer renderer = _fixture.Renderer;

        var scene = new Scene("brushwall");
        scene.Camera.Position = new Vector3(0f, 0f, 3f);
        scene.Camera.LookAt(Vector3.Zero);

        // The compiled world draws through StaticWorldMaterial when a face
        // names none; without it the chunks render as nothing and this test
        // would be measuring sky - which is exactly what the guard above
        // caught the first time it was written.
        Texture white = renderer.CreateTexture(
            [255, 255, 255, 255], 1, 1, TextureFormat.Rgba8, TextureColorSpace.Linear,
            TextureFilter.Nearest, TextureWrap.Clamp);

        var material = new Material(renderer.DefaultShader);
        material
            .SetVector3("uBaseColor", new Vector3(0.9f, 0.05f, 0.05f))
            .SetFloat("uRoughness", 0.9f)
            .SetFloat("uMetallic", 0f)
            .SetFloat("uAmbientOcclusion", 1f)
            .SetVector3("uEmissive", Vector3.Zero)
            .SetFloat("uShadingModel", 0f)
            .SetTexture("uDiffuse", 0, white);

        scene.StaticWorldMaterial = material;

        var wall = scene.Root.CreateChild("Wall");
        wall.Brush = SpectraEngine.Core.Bsp.Brush.CreateBox(
            new Vector3(-8f, -8f, -0.25f), new Vector3(8f, 8f, 0.25f));

        var lamp = scene.Root.CreateChild("Lamp");
        lamp.LocalPosition = new Vector3(0f, 0f, 1.25f);
        lamp.Light = new Light
        {
            Kind = LightKind.Point,
            Color = Vector3.One,
            Intensity = 20f,
            Range = 4f,
        };

        // A SUN, so the frame runs a shadow pass. That pass sets a slope-scaled
        // raster bias and narrows the viewport per cascade, and a scene without
        // one skips all of it - so a test with only a point light silently
        // measures a simpler frame than any real level renders.
        var sun = scene.Root.CreateChild("Sun");
        sun.LocalTransform = sun.LocalTransform with
        {
            Rotation = Light.RotationForDirection(Vector3.Normalize(new Vector3(0.3f, -1f, -0.4f))),
        };
        sun.Light = new Light { Kind = LightKind.Directional, Intensity = 1f };

        // The static world is DERIVED and has to be compiled before it can be
        // drawn; without this the scene renders as empty sky and the test would
        // report the line as visible for the wrong reason.
        scene.RebuildStaticWorld(renderer);
        return scene;
    }

    // A wall filling the view with its +z face at z = 0.25, lit hard enough to
    // be clearly not-green.
    private Scene BuildWall()
    {
        OpenGLRenderer renderer = _fixture.Renderer;

        var scene = new Scene("wall");
        scene.Camera.Position = new Vector3(0f, 0f, 3f);
        scene.Camera.LookAt(Vector3.Zero);

        var (vertices, indices) = Primitives.Cube();
        Mesh mesh = renderer.CreateMesh(vertices, indices, VertexAttribute.StandardLayout);

        Texture white = renderer.CreateTexture(
            [255, 255, 255, 255], 1, 1, TextureFormat.Rgba8, TextureColorSpace.Linear,
            TextureFilter.Nearest, TextureWrap.Clamp);

        var material = new Material(renderer.DefaultShader);
        material
            // RED, so a green line over it is unmistakable in one channel.
            .SetVector3("uBaseColor", new Vector3(0.9f, 0.05f, 0.05f))
            .SetFloat("uRoughness", 0.9f)
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

        var lamp = scene.Root.CreateChild("Lamp");
        lamp.LocalPosition = new Vector3(0f, 0f, 1.25f);
        lamp.Light = new Light
        {
            Kind = LightKind.Point,
            Color = Vector3.One,
            Intensity = 20f,
            Range = 4f,
        };

        return scene;
    }

    private (int R, int G, int B) BrightestGreen(RenderTarget target, int x, int y, int radius)
    {
        (int R, int G, int B) best = (0, 0, 0);

        for (int offset = -radius; offset <= radius; offset++)
        {
            (int R, int G, int B) sample = ReadPixel(target, x, y + offset);
            if (sample.G > best.G)
                best = sample;
        }

        return best;
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
