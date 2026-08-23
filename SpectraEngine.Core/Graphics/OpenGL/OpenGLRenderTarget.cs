using Silk.NET.OpenGL;
using System;

namespace SpectraEngine.Core.Graphics.OpenGL;

/// <summary>
/// An FBO with a texture colour attachment and an optional depth renderbuffer.
/// </summary>
/// <remarks>
/// <para>
/// Colour is a <b>texture</b> and depth is a <b>renderbuffer</b>, and the
/// asymmetry is the point: colour is what something else samples, depth is only
/// ever written here. A renderbuffer is the cheaper resource for the second
/// case, and making depth sampleable needs work in the shader language that
/// does not exist yet (see <see cref="RenderTargetDesc"/>).
/// </para>
/// </remarks>
internal sealed class OpenGLRenderTarget : RenderTarget
{
    private readonly GL _gl;
    private readonly OpenGLTexture _color;
    private uint _fbo;
    private uint _depth;
    private bool _disposed;

    internal uint Framebuffer => _fbo;

    internal OpenGLRenderTarget(GL gl, in RenderTargetDesc desc)
    {
        desc.Validate();

        _gl = gl;
        Desc = desc;

        // Created through the ordinary texture path with no pixels, so the
        // attachment IS a texture in every sense that matters downstream: same
        // sampler state, same colour-space handling, same type a material binds.
        _color = OpenGLTexture.CreateEmpty(
            gl, desc.Width, desc.Height, desc.ColorFormat, desc.ColorSpace, desc.Filter, desc.Wrap);

        Allocate(desc.Width, desc.Height);
    }

    public override Texture ColorTexture => _color;

    public override void Resize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width == Width && height == Height) return;
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), $"Render target size must be positive; got {width}x{height}.");

        // Reallocates the texture's storage behind the same wrapper and the same
        // GL name, so every material already sampling it stays valid.
        _color.ReallocateStorage(width, height);
        Allocate(width, height);
    }

    private void Allocate(int width, int height)
    {
        if (_fbo == 0)
            _fbo = _gl.GenFramebuffer();

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.FramebufferTexture2D(
            FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _color.Handle, 0);

        if (Desc.Depth)
        {
            if (_depth == 0) _depth = _gl.GenRenderbuffer();
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depth);
            _gl.RenderbufferStorage(
                RenderbufferTarget.Renderbuffer, InternalFormat.Depth24Stencil8, (uint)width, (uint)height);
            _gl.FramebufferRenderbuffer(
                FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment,
                RenderbufferTarget.Renderbuffer, _depth);
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
        }

        GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        if (status != GLEnum.FramebufferComplete)
        {
            Dispose();
            throw new InvalidOperationException(
                $"OpenGL refused a {width}x{height} render target: framebuffer status {status}.");
        }

        Width = width;
        Height = height;
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_fbo != 0) _gl.DeleteFramebuffer(_fbo);
        if (_depth != 0) _gl.DeleteRenderbuffer(_depth);
        _fbo = _depth = 0;

        // The attachment is this target's to free: it was never handed to the
        // asset manager and nothing else can be holding it as an owner.
        _color.Dispose();
    }
}
