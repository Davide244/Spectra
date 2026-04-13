using System;

namespace SpectraEngine.Core.Graphics;

public abstract class ShaderProgram : IDisposable
{
    public abstract void Use();

    public abstract void Dispose();
}
