using Silk.NET.OpenGL;
using System;

namespace SpectraEngine.Core.Graphics.OpenGL;

internal sealed class OpenGLTexture : Texture
{
    private readonly GL _gl;
    public uint Handle { get; }
    private bool _disposed;

    private OpenGLTexture(GL gl, uint handle, int width, int height, TextureFormat format)
    {
        _gl = gl;
        Handle = handle;
        Width = width;
        Height = height;
        Format = format;
    }

    internal static unsafe OpenGLTexture Create(
        GL gl,
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        TextureFormat format,
        TextureFilter filter,
        TextureWrap wrap)
    {
        uint handle = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, handle);

        (InternalFormat internalFormat, PixelFormat pixelFormat) = GlFormats(format);

        // Single-channel (R8) needs alignment of 1; the default of 4 reads past
        // the row for non-multiple-of-4 widths and corrupts the upload.
        if (pixelFormat == PixelFormat.Red)
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

        fixed (byte* p = pixels)
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, internalFormat,
                (uint)width, (uint)height, 0, pixelFormat, PixelType.UnsignedByte, p);
        }

        bool wantsMipmaps = filter == TextureFilter.LinearMipmap;
        if (wantsMipmaps)
            gl.GenerateMipmap(TextureTarget.Texture2D);

        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)MinFilter(filter, wantsMipmaps));
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)MagFilter(filter));

        int wrapMode = wrap == TextureWrap.Repeat ? (int)GLEnum.Repeat : (int)GLEnum.ClampToEdge;
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, wrapMode);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, wrapMode);

        gl.BindTexture(TextureTarget.Texture2D, 0);
        return new OpenGLTexture(gl, handle, width, height, format);
    }

    internal void Bind(int unit)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + unit);
        _gl.BindTexture(TextureTarget.Texture2D, Handle);
    }

    private static (InternalFormat Internal, PixelFormat Pixel) GlFormats(TextureFormat format) => format switch
    {
        TextureFormat.Rgba8 => (InternalFormat.Rgba8, PixelFormat.Rgba),
        TextureFormat.Rgb8 => (InternalFormat.Rgb8, PixelFormat.Rgb),
        TextureFormat.R8 => (InternalFormat.R8, PixelFormat.Red),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static GLEnum MinFilter(TextureFilter filter, bool mipmaps) => filter switch
    {
        TextureFilter.Nearest => GLEnum.Nearest,
        TextureFilter.Linear => GLEnum.Linear,
        TextureFilter.LinearMipmap => mipmaps ? GLEnum.LinearMipmapLinear : GLEnum.Linear,
        _ => GLEnum.Linear,
    };

    private static GLEnum MagFilter(TextureFilter filter) => filter switch
    {
        TextureFilter.Nearest => GLEnum.Nearest,
        _ => GLEnum.Linear,
    };

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gl.DeleteTexture(Handle);
    }
}
