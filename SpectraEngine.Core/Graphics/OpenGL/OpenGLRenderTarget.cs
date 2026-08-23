using Silk.NET.OpenGL;
using System;

namespace SpectraEngine.Core.Graphics.OpenGL;

/// <summary>
/// An FBO with an optional texture colour attachment and an optional sampleable
/// depth texture.
/// </summary>
/// <remarks>
/// <para>
/// Both attachments are <b>textures</b>, because both are sampled: colour by
/// whatever reads the result, depth by the deferred light pass reconstructing
/// world position and by the shadow pass comparing against it.
/// </para>
/// <para>
/// <b>A depth-only FBO must say so twice.</b> Leaving colour attachment 0
/// unattached is not enough: the draw and read buffers still name it, and GL
/// reports the framebuffer incomplete. Both are set to <c>GL_NONE</c> below,
/// which is the whole of what makes a shadow map work on this backend.
/// </para>
/// </remarks>
internal sealed class OpenGLRenderTarget : RenderTarget
{
    private readonly GL _gl;
    private readonly OpenGLTexture? _color;
    private readonly OpenGLTexture? _depth;
    private uint _fbo;
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
        if (desc.Color)
        {
            _color = OpenGLTexture.CreateEmpty(
                gl, desc.Width, desc.Height, desc.ColorFormat, desc.ColorSpace, desc.Filter, desc.Wrap);
        }

        if (desc.Depth)
        {
            // Nearest and clamp: depth is read as data, and interpolating
            // between two depths produces a value that is at neither surface.
            _depth = OpenGLTexture.CreateEmpty(
                gl, desc.Width, desc.Height, TextureFormat.Depth32Float, TextureColorSpace.Linear,
                TextureFilter.Nearest, TextureWrap.Clamp);
        }

        Allocate(desc.Width, desc.Height);
    }

    public override Texture? ColorTexture => _color;

    public override Texture? DepthTexture => _depth;

    public override void Resize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width == Width && height == Height) return;
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), $"Render target size must be positive; got {width}x{height}.");

        // Reallocates the texture's storage behind the same wrapper and the same
        // GL name, so every material already sampling it stays valid.
        _color?.ReallocateStorage(width, height);
        _depth?.ReallocateStorage(width, height);
        Allocate(width, height);
    }

    private void Allocate(int width, int height)
    {
        if (_fbo == 0)
            _fbo = _gl.GenFramebuffer();

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

        if (_color is not null)
        {
            _gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, _color.Handle, 0);
        }
        else
        {
            // Both, not just the draw buffer. A framebuffer with no colour
            // attachment whose read buffer still names attachment 0 is
            // INCOMPLETE_READ_BUFFER, which is a completeness failure at
            // creation rather than an error at the first read.
            _gl.DrawBuffer(DrawBufferMode.None);
            _gl.ReadBuffer(ReadBufferMode.None);
        }

        if (_depth is not null)
        {
            // DepthAttachment, not DepthStencilAttachment: the format carries no
            // stencil, and attaching a depth-only texture to the combined point
            // leaves the framebuffer incomplete.
            _gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                TextureTarget.Texture2D, _depth.Handle, 0);
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

    /// <summary>
    /// Attaches the other targets of a multi-target pass as colour attachments
    /// 1..N-1 of this framebuffer, and enables all N draw buffers.
    /// </summary>
    /// <remarks>
    /// <b>The draw-buffer list is the part that is easy to miss.</b> A
    /// framebuffer defaults to writing attachment 0 only, so attaching three
    /// textures and emitting three fragment outputs produces one populated
    /// surface and two untouched ones, with no error from anything.
    /// </remarks>
    internal void BindExtraColorTargets(GL gl, ReadOnlySpan<RenderTarget> targets)
    {
        if (targets.Length <= 1) return;

        Span<GLEnum> buffers = stackalloc GLEnum[targets.Length];
        buffers[0] = GLEnum.ColorAttachment0;

        for (int i = 1; i < targets.Length; i++)
        {
            var extra = (OpenGLRenderTarget)targets[i];
            var attachment = (FramebufferAttachment)(GLEnum.ColorAttachment0 + i);
            gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer, attachment,
                TextureTarget.Texture2D, ((OpenGLTexture)extra.ColorTexture!).Handle, 0);
            buffers[i] = GLEnum.ColorAttachment0 + i;
        }

        gl.DrawBuffers((uint)targets.Length, buffers);

        GLEnum status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
        {
            throw new InvalidOperationException(
                $"OpenGL refused a {targets.Length}-attachment pass: framebuffer status {status}.");
        }
    }

    /// <summary>
    /// Detaches the extra attachments and restores single-target drawing, so
    /// this framebuffer is left exactly as an ordinary pass expects it.
    /// </summary>
    internal void UnbindExtraColorTargets(GL gl, ReadOnlySpan<RenderTarget> targets)
    {
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

        for (int i = 1; i < targets.Length; i++)
        {
            var attachment = (FramebufferAttachment)(GLEnum.ColorAttachment0 + i);
            gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer, attachment, TextureTarget.Texture2D, 0, 0);
        }

        Span<GLEnum> single = [GLEnum.ColorAttachment0];
        gl.DrawBuffers(1, single);
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_fbo != 0) _gl.DeleteFramebuffer(_fbo);
        _fbo = 0;
        _depth?.Dispose();

        // The attachment is this target's to free: it was never handed to the
        // asset manager and nothing else can be holding it as an owner.
        _color?.Dispose();
    }
}
