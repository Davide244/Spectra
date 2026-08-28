using SpectraEngine.Core.Scene;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Core.Graphics.OpenGL;

/// <summary>
/// Deferred shading: rasterise every surface's properties into the G-buffer
/// once, then light the whole screen in a second pass.
/// </summary>
/// <remarks>
/// <para>
/// <b>The point is that geometry cost and light cost stop multiplying.</b> The
/// forward pipeline shades every light on every fragment of every object, so
/// the bill is draws times lights; here the geometry pass knows nothing about
/// lights at all and the light pass runs once per screen pixel, whatever was
/// drawn into it.
/// </para>
/// <para>
/// <b>Every surface is drawn with one program, not with its own.</b> A
/// material's shader returns a shaded colour, which is exactly what a G-buffer
/// pass must not produce; what the material contributes is its parameters,
/// through <see cref="Material.ApplyTo"/>. See
/// <c>Renderer.EnsureGBufferShader</c> for what that costs.
/// </para>
/// <para>
/// The honest trade against forward: no hardware MSAA (the G-buffer stores
/// properties, and averaging two normals or two material ids is meaningless),
/// no blended transparency (one surface per pixel, by construction), and about
/// 36 bytes of bandwidth per pixel per frame. The forward pipeline stays in the
/// rotation for those cases and for A/B comparison.
/// </para>
/// </remarks>
public sealed class DeferredPipeline : IOpenGLRenderPipeline
{
    private OpenGLRenderer? _renderer;

    public string Name => "Deferred";

    /// <summary>Ambient light level, added to every surface regardless of the lights.</summary>
    /// <remarks>
    /// Higher than the forward path's, on purpose. It is the only stand-in the
    /// engine has for sky light and bounce, and with a real shadow term a
    /// surface the sun cannot see now has nothing else at all: too low a value
    /// makes every shadow a black hole rather than a shadow. It goes away when
    /// image-based lighting arrives and gives the sky an actual colour.
    /// </remarks>
    public float Ambient { get; set; } = 0.18f;

    public void Initialize(OpenGLRenderer renderer) => _renderer = renderer;

    public void Execute(in OpenGLRenderContext context)
    {
        OpenGLRenderer renderer = context.Renderer;
        if (context.Scene is null) return;

        GBuffer? gbuffer = renderer.EnsureGBuffer();
        if (gbuffer is null) return;

        ShaderProgram surfaceShader = renderer.EnsureGBufferShader();
        Camera camera = context.Scene.Camera;

        // Shadows FIRST, so the light pass reads a map from this frame rather
        // than the last one. It is also its own pass into its own target, so it
        // has to happen outside the geometry pass either way.
        int shadowLight = renderer.RenderShadowMap(context.Scene, context.View);

        // Outside the pass: it compiles the instanced twin on the first frame
        // that wants one, and a program created inside an open pass is a state
        // change in the middle of a recorded list.
        renderer.PrepareGeometryInstancing();

        // DEPTH ONLY, and the colour attachments are deliberately not cleared.
        // The depth buffer is the coverage mask: the light pass returns the sky
        // wherever depth is still 1, so no attachment is ever read at a pixel
        // this frame did not write. Clearing them anyway would be five
        // full-screen writes per frame for a result nothing looks at, and on
        // D3D12 it is slower still, because a clear to a value other than the
        // one the resource was created with takes the unoptimised path and says
        // so once per attachment per frame.
        using (renderer.Profiler.Measure(SpectraEngine.Core.Diagnostics.FramePhase.Geometry))
        {
        renderer.BeginPass(gbuffer.Targets, PassClear.DepthOnly);
        try
        {
            // From the PASS, not the window: the two are the same only while
            // every pass goes to the back buffer.
            if (renderer.PassAspectRatio is { } aspect)
                camera.AspectRatio = aspect;

            renderer.DrawGeometry(context.View, camera, surfaceShader);
        }
        finally
        {
            renderer.EndPass();
        }
        }

        renderer.DrawDeferredLightPass(gbuffer, context.View, camera, Ambient, shadowLight);
    }

    public void Dispose() { }
}
