namespace SpectraEngine.Core.Graphics;

/// <summary>Pixel format for <see cref="Texture"/> data uploads.</summary>
public enum TextureFormat
{
    /// <summary>8 bits per channel, four channels (RGBA).</summary>
    Rgba8,

    /// <summary>8 bits per channel, three channels (RGB).</summary>
    Rgb8,

    /// <summary>8 bits, single channel (red / luminance / mask).</summary>
    R8,
}

/// <summary>Magnification and minification filtering applied when sampling.</summary>
public enum TextureFilter
{
    /// <summary>Point sampling — sharp pixels, no blending. Good for pixel art.</summary>
    Nearest,

    /// <summary>Bilinear interpolation between the four nearest texels.</summary>
    Linear,

    /// <summary>Linear with trilinear mipmap interpolation; requires mipmaps.</summary>
    LinearMipmap,
}

/// <summary>Wrap behaviour when UV coordinates fall outside [0,1].</summary>
public enum TextureWrap
{
    /// <summary>Tile the texture infinitely.</summary>
    Repeat,

    /// <summary>Clamp to the nearest edge pixel.</summary>
    Clamp,
}
