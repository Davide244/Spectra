using System;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// Renderer-owned 2D texture resource. Subclasses hold the GPU handle and apply
/// the configured sampling state. Lifetime is owned by the creating renderer.
/// </summary>
public abstract class Texture : IDisposable
{
    public int Width { get; protected set; }
    public int Height { get; protected set; }
    public TextureFormat Format { get; protected set; }

    /// <summary>
    /// How the sampler interprets this texture's bytes. This is the <i>resolved</i>
    /// space, not the requested one: a format with no sRGB variant reports
    /// <see cref="TextureColorSpace.Linear"/> however it was asked for.
    /// </summary>
    public TextureColorSpace ColorSpace { get; protected set; }

    /// <summary>
    /// Removes this texture from the creating renderer's tracking list; the
    /// renderer hands it over at creation time and <see cref="Renderer.DestroyTexture"/>
    /// invokes it exactly once. Unsynchronized on purpose: resource creation
    /// and destruction both happen on the render thread.
    /// </summary>
    internal Action? Unregister { get; set; }

    public abstract void Dispose();
}
