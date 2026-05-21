using SpectraEngine.Core.Graphics.Shaders;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace SpectraEngine.Core.Graphics;

public abstract class ShaderProgram : IDisposable
{
    public abstract void Use();

    /// <summary>
    /// Replaces this program's compiled code with <paramref name="blob"/>,
    /// preserving the object identity so all references (materials etc.) keep
    /// working. On failure the old code stays in place and <paramref name="error"/>
    /// carries the message. Backends that don't support reload return false.
    /// </summary>
    public abstract bool TryReload(PipelineBlob blob, [NotNullWhen(false)] out string? error);

    public abstract void SetUniform(string name, Matrix4x4 value);
    public abstract void SetUniform(string name, Vector4 value);
    public abstract void SetUniform(string name, Vector3 value);
    public abstract void SetUniform(string name, Vector2 value);
    public abstract void SetUniform(string name, float value);
    public abstract void SetUniform(string name, int value);

    /// <summary>Binds <paramref name="texture"/> to texture unit <paramref name="unit"/> and assigns it to the named sampler.</summary>
    public abstract void SetTexture(string name, int unit, Texture texture);

    public abstract void Dispose();
}
