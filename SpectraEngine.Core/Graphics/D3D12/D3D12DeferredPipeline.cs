using Silk.NET.Direct3D12;
using SpectraEngine.Core.Scene;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Core.Graphics.D3D12;

/// <summary>
/// Deferred shading on D3D12. Mirrors <c>OpenGL.DeferredPipeline</c>
/// step-for-step; see it for what the two passes are and what they cost.
/// </summary>
/// <remarks>
/// The extra work on this backend is invisible from here: the geometry pass
/// binds five attachments, so every pipeline state built during it is compiled
/// against all five formats plus the sampled depth format, and each attachment
/// is transitioned into and back out of <c>RenderTarget</c> around the pass.
/// Both live in <c>D3D12Renderer.BeginPassCore</c> where the target is known.
/// </remarks>
public sealed unsafe class D3D12DeferredPipeline : ID3D12RenderPipeline
{
    private D3D12Renderer? _renderer;

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

    public void Initialize(D3D12Renderer renderer) => _renderer = renderer;

    public void Execute(in D3D12RenderContext context)
    {
        D3D12Renderer renderer = _renderer!;
        renderer.CurrentFillMode = FillMode.Solid;

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

        // Outside the pass, beside the instanced-variant compile and for the
        // same reason: a program created inside an open pass is a state change
        // in the middle of a recorded list.
        renderer.PrepareWorldLines(gbuffer: true);


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

        // The world-line lane, AFTER the light pass, alpha-blended over the lit
        // result: the only picture a translucent line can blend toward is the
        // finished one, and the depth test happens in the shader against the
        // G-buffer's depth, sampled as an ordinary texture. It used to draw
        // INTO the G-buffer as an opaque five-attachment overwrite, which is a
        // model that cannot fade at all - see FlushWorldLinesDeferred.
        renderer.FlushWorldLinesDeferred(camera, gbuffer);
    }

    public void Dispose() { }
}
