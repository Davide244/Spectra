using Silk.NET.OpenGL;

namespace SpectraEngine.Core.Graphics.OpenGL;

/// <summary>
/// An sRGB-encoded offscreen buffer that the frame is drawn into and then
/// copied from, for the case where the window's own framebuffer cannot encode.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> The D3D backends get display encoding by
/// naming an sRGB format on the back-buffer view, and the runtime either honours
/// it or fails loudly. OpenGL instead needs the <i>window's</i> framebuffer to
/// have been created sRGB-capable, which is a GLFW window hint
/// (<c>GLFW_SRGB_CAPABLE</c>) that Silk.NET 2.23 does not expose: there is no
/// field for it on <c>WindowOptions</c>, and setting it directly does not
/// survive, because Silk calls <c>glfwDefaultWindowHints</c> itself immediately
/// before creating the window. Measured on an RTX 4070 Ti, the resulting default
/// framebuffer reports <c>GL_LINEAR</c>, so <c>glEnable(GL_FRAMEBUFFER_SRGB)</c>
/// is silently a no-op.
/// </para>
/// <para>
/// <b>Leaving it uncorrected was not an option.</b> Before R2 the engine was
/// wrong twice in opposite directions -- sRGB albedo fed to lighting as linear,
/// linear output written raw to a display buffer -- and the two errors partly
/// cancelled. Fixing only the first, which is what sRGB textures do on their
/// own, breaks the cancellation and leaves OpenGL rendering visibly darker than
/// both D3D backends. One backend that disagrees with the other two is worse
/// than three that are equally wrong.
/// </para>
/// <para>
/// <b>Why a blit and not a shader.</b> Encoding in the fragment shader would
/// have to be repeated in every shader that ever writes to the screen, including
/// ones a game author writes, and it happens at the wrong point: after it, alpha
/// blending and an MSAA resolve would both operate on display codes instead of
/// on light. A render target with an sRGB format puts the conversion in the
/// hardware, after blending, exactly where the D3D backends have it. The copy
/// out is a fixed-function <c>glBlitFramebuffer</c> with encoding disabled, so
/// the already-encoded bytes are moved rather than converted again.
/// </para>
/// <para>
/// <b>This is a down payment on R3, not a detour.</b> When offscreen render
/// targets become a first-class concept, this is the first one, and the copy
/// becomes the resolve pass R4 owns.
/// </para>
/// </remarks>
internal sealed class OpenGLSrgbTarget
{
    private uint _fbo;
    private uint _color;
    private uint _depth;
    private int _width;
    private int _height;

    /// <summary>False once the driver has refused to complete the framebuffer.</summary>
    public bool Usable { get; private set; } = true;

    /// <summary>
    /// Points rendering at the sRGB buffer, resizing it first if the window
    /// changed. Returns false if it could not be made ready, in which case the
    /// caller draws to the window as before.
    /// </summary>
    public bool Begin(GL gl, int width, int height)
    {
        if (!Usable || width <= 0 || height <= 0)
            return false;

        if (_fbo == 0 || width != _width || height != _height)
        {
            if (!Allocate(gl, width, height))
                return false;
        }

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        return true;
    }

    /// <summary>
    /// Copies the finished frame to the window and leaves the window bound.
    /// </summary>
    public void Present(GL gl, int width, int height)
    {
        // Encoding OFF for the copy. The frame in _color is already made of
        // display codes; the two ways a driver could read this blit are "move
        // the bytes" and "decode then re-encode", and only the first is
        // specified when the enable is off. Measured identical on NVIDIA either
        // way, and GlColorSpaceTests reads a pixel back rather than trusting
        // that this generalises.
        gl.Disable(EnableCap.FramebufferSrgb);

        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
        gl.BlitFramebuffer(
            0, 0, width, height,
            0, 0, width, height,
            ClearBufferMask.ColorBufferBit,
            // Source and destination are the same size, so the filter never
            // actually interpolates; Nearest states that and cannot introduce
            // a filtered tap in the wrong colour space if that ever changes.
            BlitFramebufferFilter.Nearest);

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.Enable(EnableCap.FramebufferSrgb);
    }

    public void Dispose(GL gl)
    {
        if (_fbo != 0) gl.DeleteFramebuffer(_fbo);
        if (_color != 0) gl.DeleteRenderbuffer(_color);
        if (_depth != 0) gl.DeleteRenderbuffer(_depth);
        _fbo = _color = _depth = 0;
        _width = _height = 0;
    }

    private bool Allocate(GL gl, int width, int height)
    {
        Dispose(gl);

        _fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

        // Renderbuffers, not textures: nothing samples this, and a renderbuffer
        // is the cheaper resource. R3 will want textures instead, which is the
        // one line that changes when it does.
        _color = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _color);
        gl.RenderbufferStorage(
            RenderbufferTarget.Renderbuffer, InternalFormat.Srgb8Alpha8, (uint)width, (uint)height);
        gl.FramebufferRenderbuffer(
            FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            RenderbufferTarget.Renderbuffer, _color);

        // The window's own depth buffer belongs to the window's framebuffer, so
        // drawing into this one needs its own or nothing would depth-test.
        _depth = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depth);
        gl.RenderbufferStorage(
            RenderbufferTarget.Renderbuffer, InternalFormat.Depth24Stencil8, (uint)width, (uint)height);
        gl.FramebufferRenderbuffer(
            FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment,
            RenderbufferTarget.Renderbuffer, _depth);

        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);

        if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
        {
            Dispose(gl);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            Usable = false;
            return false;
        }

        _width = width;
        _height = height;
        return true;
    }
}
