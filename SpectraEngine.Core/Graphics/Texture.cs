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

    public abstract void Dispose();
}
