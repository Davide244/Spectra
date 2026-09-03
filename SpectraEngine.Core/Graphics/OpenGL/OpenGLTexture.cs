using Silk.NET.OpenGL;
using System;

namespace SpectraEngine.Core.Graphics.OpenGL;

internal sealed class OpenGLTexture : Texture
{
    private readonly GL _gl;
    private readonly TextureFilter _filter;

    /// <summary>The GL texture name. Stable across <see cref="ReallocateStorage"/>.</summary>
    public uint Handle { get; }

    private bool _disposed;

    private OpenGLTexture(
        GL gl, uint handle, int width, int height, TextureFormat format,
        TextureColorSpace colorSpace, TextureFilter filter)
    {
        _gl = gl;
        Handle = handle;
        Width = width;
        Height = height;
        Format = format;
        ColorSpace = colorSpace;
        _filter = filter;
    }

    internal static unsafe OpenGLTexture Create(GL gl, in TextureUploadDesc desc)
    {
        TextureFormat format = desc.Format;
        TextureColorSpace resolved = TextureFormatInfo.Resolve(format, desc.ColorSpace);
        TextureFilter filter = desc.Filter;
        int width = desc.Width;
        int height = desc.Height;

        uint handle = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, handle);

        (InternalFormat internalFormat, PixelFormat pixelFormat, PixelType pixelType) = GlFormats(format, resolved);
        bool compressed = TextureFormatInfo.IsBlockCompressed(format);

        // Decoded image rows are tightly packed, but GL assumes 4-byte row
        // alignment by default and would then read past each row (skewing the
        // image) whenever the stride is not a multiple of 4 — which R8 hits at
        // any odd width and RGB8 hits at any width not divisible by 4. RGBA8 is
        // always 4-aligned, so it keeps the faster default. Compressed uploads
        // do not consult the unpack alignment at all.
        if (!compressed && pixelFormat != PixelFormat.Rgba)
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

        UploadLevels(gl, desc, internalFormat, pixelFormat, pixelType, compressed);

        // A supplied chain is never regenerated: it is what the cooker produced,
        // and a compressed one cannot be regenerated at all (GenerateMipmap has
        // no path that re-encodes blocks).
        bool wantsMipmaps = filter == TextureFilter.LinearMipmap;
        bool generate = wantsMipmaps && !desc.HasSuppliedMipChain && !compressed;
        if (generate)
            gl.GenerateMipmap(TextureTarget.Texture2D);

        // A texture with only SOME of its levels defined is INCOMPLETE, and an
        // incomplete texture samples as black with no error from the driver.
        // GenerateMipmap fills the whole chain so the default max level of 1000
        // is fine there; a supplied chain that stops at 8x8 has to say so.
        if (desc.HasSuppliedMipChain)
        {
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBaseLevel, 0);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, desc.MipCount - 1);
        }

        bool hasChain = generate || desc.HasSuppliedMipChain;
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)MinFilter(filter, hasChain));
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)MagFilter(filter));

        int wrapMode = desc.Wrap == TextureWrap.Repeat ? (int)GLEnum.Repeat : (int)GLEnum.ClampToEdge;
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, wrapMode);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, wrapMode);

        gl.BindTexture(TextureTarget.Texture2D, 0);
        return new OpenGLTexture(gl, handle, width, height, format, resolved, filter);
    }

    /// <summary>
    /// Hands every declared level to GL, tightly packed.
    /// </summary>
    /// <remarks>
    /// The tight repack lives in <see cref="TextureUploadLayout.TightLevel"/>
    /// and returns a slice with no copy when the file was already tight, which
    /// is every uncompressed upload the engine makes today.
    /// </remarks>
    private static unsafe void UploadLevels(
        GL gl,
        in TextureUploadDesc desc,
        InternalFormat internalFormat,
        PixelFormat pixelFormat,
        PixelType pixelType,
        bool compressed)
    {
        // Copied out of the `in` parameter once: a helper cannot return a span
        // derived from a ref-struct passed by reference, and slicing here keeps
        // every level's bytes rooted in the caller's payload.
        ReadOnlySpan<byte> payload = desc.Payload;
        TextureFormat format = desc.Format;

        for (int level = 0; level < desc.MipCount; level++)
        {
            TextureMipDesc mip = desc.Mips[level];
            ReadOnlySpan<byte> bytes = TextureUploadLayout.TightLevel(payload, format, mip, out byte[]? repacked);

            fixed (byte* p = bytes)
            {
                if (compressed)
                {
                    gl.CompressedTexImage2D(
                        TextureTarget.Texture2D, level, internalFormat,
                        (uint)mip.Width, (uint)mip.Height, 0, (uint)bytes.Length, p);
                }
                else
                {
                    gl.TexImage2D(
                        TextureTarget.Texture2D, level, internalFormat,
                        (uint)mip.Width, (uint)mip.Height, 0, pixelFormat, pixelType, p);
                }
            }

            // Named rather than discarded, so the repacked buffer is provably
            // alive across the fixed block above.
            GC.KeepAlive(repacked);
        }
    }

    /// <summary>
    /// Creates a texture with storage but no pixel data: the colour attachment
    /// of a render target, which the GPU fills rather than the CPU.
    /// </summary>
    /// <remarks>
    /// Mipmaps are deliberately not generated. There is nothing to generate them
    /// from at creation, and a render target's contents change every frame, so a
    /// chain would be stale the moment it was built.
    /// </remarks>
    internal static unsafe OpenGLTexture CreateEmpty(
        GL gl,
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

        (InternalFormat internalFormat, PixelFormat pixelFormat, PixelType pixelType) = GlFormats(format, resolved);
        gl.TexImage2D(TextureTarget.Texture2D, 0, internalFormat,
            (uint)width, (uint)height, 0, pixelFormat, pixelType, null);

        // Never LinearMipmapLinear here: with no mip chain that filter samples a
        // level that does not exist and the texture reads as black.
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)MagFilter(filter));
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)MagFilter(filter));

        int wrapMode = wrap == TextureWrap.Repeat ? (int)GLEnum.Repeat : (int)GLEnum.ClampToEdge;
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, wrapMode);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, wrapMode);

        gl.BindTexture(TextureTarget.Texture2D, 0);
        return new OpenGLTexture(gl, handle, width, height, format, resolved, filter);
    }

    /// <summary>
    /// Reallocates this texture's storage at a new size, <b>keeping the same GL
    /// name and the same wrapper</b>. What a render-target resize needs.
    /// </summary>
    /// <remarks>
    /// Identity is the whole point. Every material that sampled this texture
    /// holds this object; replacing it on resize would leave each of them
    /// pointing at something destroyed, which is a black viewport at best.
    /// Sampler state lives on the texture object and survives, so only the
    /// storage is respecified.
    /// </remarks>
    internal unsafe void ReallocateStorage(int width, int height)
    {
        (InternalFormat internalFormat, PixelFormat pixelFormat, PixelType pixelType) = GlFormats(Format, ColorSpace);

        _gl.BindTexture(TextureTarget.Texture2D, Handle);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, internalFormat,
            (uint)width, (uint)height, 0, pixelFormat, pixelType, null);
        _gl.BindTexture(TextureTarget.Texture2D, 0);

        Width = width;
        Height = height;
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
    private static (InternalFormat Internal, PixelFormat Pixel, PixelType Type) GlFormats(
        TextureFormat format, TextureColorSpace colorSpace)
    {
        bool srgb = colorSpace == TextureColorSpace.Srgb;
        return format switch
        {
            TextureFormat.Rgba8 =>
                (srgb ? InternalFormat.Srgb8Alpha8 : InternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte),
            TextureFormat.Rgb8 =>
                (srgb ? InternalFormat.Srgb8 : InternalFormat.Rgb8, PixelFormat.Rgb, PixelType.UnsignedByte),
            // No SR8 exists; TextureFormatInfo.Resolve has already forced linear.
            TextureFormat.R8 => (InternalFormat.R8, PixelFormat.Red, PixelType.UnsignedByte),
            // The pixel type matters even with a null data pointer: the driver
            // validates the (format, type) pair against the internal format, and
            // UnsignedByte against RGBA16F is rejected on some drivers.
            TextureFormat.Rgba16Float => (InternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.Float),
            // Sampled with an ordinary sampler2D, which returns the depth in .r.
            // GL_TEXTURE_COMPARE_MODE stays at its GL_NONE default, so this is a
            // plain texture read and not a shadow comparison.
            TextureFormat.Depth32Float =>
                (InternalFormat.DepthComponent32f, PixelFormat.DepthComponent, PixelType.Float),

            // The block-compressed family. The pixel format and type are unused
            // by glCompressedTexImage2D and are filled in with the shape the
            // blocks decode to, so a future glTexSubImage path over one of these
            // has something honest to start from rather than a zero.
            //
            // S3TC's RGBA DXT1 rather than its RGB one, because DXGI's
            // BC1_UNORM is the alpha-carrying form and a mismatch here would
            // make one backend drop the alpha bit silently.
            TextureFormat.Bc1 => (
                srgb ? InternalFormat.CompressedSrgbAlphaS3TCDxt1Ext : InternalFormat.CompressedRgbaS3TCDxt1Ext,
                PixelFormat.Rgba, PixelType.UnsignedByte),
            TextureFormat.Bc3 => (
                srgb ? InternalFormat.CompressedSrgbAlphaS3TCDxt5Ext : InternalFormat.CompressedRgbaS3TCDxt5Ext,
                PixelFormat.Rgba, PixelType.UnsignedByte),
            // RGTC has no sRGB form in the API at all; Resolve has already
            // forced these two to linear.
            TextureFormat.Bc4 => (InternalFormat.CompressedRedRgtc1, PixelFormat.Red, PixelType.UnsignedByte),
            TextureFormat.Bc5 => (InternalFormat.CompressedRGRgtc2, PixelFormat.RG, PixelType.UnsignedByte),
            // Unsigned BPTC float: the half-float family the cooker targets. The
            // signed variant is a different internal format and would need its
            // own TextureFormat member, since the two decode the same bits
            // differently.
            TextureFormat.Bc6H => (
                InternalFormat.CompressedRgbBptcUnsignedFloat, PixelFormat.Rgb, PixelType.HalfFloat),
            TextureFormat.Bc7 => (
                srgb ? InternalFormat.CompressedSrgbAlphaBptcUnorm : InternalFormat.CompressedRgbaBptcUnorm,
                PixelFormat.Rgba, PixelType.UnsignedByte),

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
