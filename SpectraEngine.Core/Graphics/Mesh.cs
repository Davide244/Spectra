using System;

namespace SpectraEngine.Core.Graphics;

public abstract class Mesh : IDisposable
{
    public uint IndexCount { get; protected set; }

    public abstract void Draw();

    public abstract void Dispose();
}
