using System.Numerics;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// Pairs a <see cref="ShaderProgram"/> with the per-surface parameters used to
/// draw it. The owning shader must already be in use before <see cref="Apply"/>
/// is called.
/// </summary>
public sealed class Material
{
    public Material(ShaderProgram shader)
    {
        Shader = shader;
    }

    public ShaderProgram Shader { get; }

    public Vector3 BaseColor { get; set; } = new(0.8f, 0.8f, 0.8f);

    /// <summary>Uploads this material's parameters to the (already bound) shader.</summary>
    public void Apply()
    {
        Shader.SetUniform("uBaseColor", BaseColor);
    }
}
