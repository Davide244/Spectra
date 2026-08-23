using Silk.NET.OpenGL;
using System;

namespace SpectraEngine.Core.Graphics.OpenGL;

internal sealed class OpenGLTexture : Texture
{
    private readonly GL _gl;
    public uint Handle { get; }
    private bool _disposed;

    private OpenGLTexture(
        GL gl, uint handle, int width, int height, TextureFormat format, TextureColorSpace colorSpace)
    {
        _gl = gl;
        Handle = handle;
        Width = width;
        Height = height;
        Format = format;
        ColorSpace = colorSpace;
    }

    internal static unsafe OpenGLTexture Create(
        GL gl,
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        TextureFormat format,
        TextureColorSpace colorSpace,
        TextureFilter filter,
        TextureWrap wrap)
    {
        TextureColorSpace resolved = TextureFormatInfo.Resolve(format, colorSpace);

        uint handle = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, handle);

        (InternalFormat internalFormat, PixelFormat pixelFormat) = GlFormats(format, resolved);

        // Decoded image rows are tightly packed, but GL assumes 4-byte row
        // alignment by default and would then read past each row (skewing the
        // image) whenever the stride is not a multiple of 4 — which R8 hits at
        // any odd width and RGB8 hits at any width not divisible by 4. RGBA8 is
        // always 4-aligned, so it keeps the faster default.
        if (pixelFormat != PixelFormat.Rgba)
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
        return new OpenGLTexture(gl, handle, width, height, format, resolved);
    }

    internal void Bind(int unit)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + unit);
        _gl.BindTexture(TextureTarget.Texture2D, Handle);
    }

    // Only the INTERNAL format carries the colour space; the pixel format
    // describes the bytes being handed over, which are the same either way. The
    // sRGB internal formats make the driver decode on every sample -- including
    // the samples GenerateMipmap takes below, which is why the mip chain of an
    // sRGB texture is an average of light rather than of display codes.
    private static (InternalFormat Internal, PixelFormat Pixel) GlFormats(
        TextureFormat format, TextureColorSpace colorSpace)
    {
        bool srgb = colorSpace == TextureColorSpace.Srgb;
        return format switch
        {
            TextureFormat.Rgba8 => (srgb ? InternalFormat.Srgb8Alpha8 : InternalFormat.Rgba8, PixelFormat.Rgba),
            TextureFormat.Rgb8 => (srgb ? InternalFormat.Srgb8 : InternalFormat.Rgb8, PixelFormat.Rgb),
            // No SR8 exists; TextureFormatInfo.Resolve has already forced linear.
            TextureFormat.R8 => (InternalFormat.R8, PixelFormat.Red),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }

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
