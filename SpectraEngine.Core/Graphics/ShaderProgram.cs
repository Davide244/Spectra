using System;
using System.Numerics;

namespace SpectraEngine.Core.Graphics;

public abstract class ShaderProgram : IDisposable
{
    public abstract void Use();

    public abstract void SetUniform(string name, Matrix4x4 value);
    public abstract void SetUniform(string name, Vector4 value);
    public abstract void SetUniform(string name, Vector3 value);
    public abstract void SetUniform(string name, Vector2 value);
    public abstract void SetUniform(string name, float value);
    public abstract void SetUniform(string name, int value);

    public abstract void Dispose();
}
