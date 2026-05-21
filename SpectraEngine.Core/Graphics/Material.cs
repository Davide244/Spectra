using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// Pairs a <see cref="ShaderProgram"/> with the per-surface parameters used to
/// draw it. Parameters are stored as typed maps and pushed to the shader in
/// <see cref="Apply"/>; the owning shader must already be in use before that
/// call (the engine does this in its draw loop).
/// </summary>
public sealed class Material
{
    private readonly Dictionary<string, float> _floats = new();
    private readonly Dictionary<string, Vector2> _vec2 = new();
    private readonly Dictionary<string, Vector3> _vec3 = new();
    private readonly Dictionary<string, Vector4> _vec4 = new();
    private readonly Dictionary<string, TextureBinding> _textures = new();

    public Material(ShaderProgram shader)
    {
        Shader = shader;
    }

    public ShaderProgram Shader { get; }

    public Material SetFloat(string name, float value) { _floats[name] = value; return this; }
    public Material SetVector2(string name, Vector2 value) { _vec2[name] = value; return this; }
    public Material SetVector3(string name, Vector3 value) { _vec3[name] = value; return this; }
    public Material SetVector4(string name, Vector4 value) { _vec4[name] = value; return this; }

    /// <summary>Binds <paramref name="texture"/> to the named sampler on <paramref name="unit"/>.</summary>
    public Material SetTexture(string name, int unit, Texture texture)
    {
        _textures[name] = new TextureBinding(unit, texture);
        return this;
    }

    /// <summary>Uploads this material's parameters to the (already bound) shader.</summary>
    public void Apply()
    {
        foreach (var (name, value) in _floats) Shader.SetUniform(name, value);
        foreach (var (name, value) in _vec2) Shader.SetUniform(name, value);
        foreach (var (name, value) in _vec3) Shader.SetUniform(name, value);
        foreach (var (name, value) in _vec4) Shader.SetUniform(name, value);
        foreach (var (name, binding) in _textures) Shader.SetTexture(name, binding.Unit, binding.Texture);
    }

    private readonly record struct TextureBinding(int Unit, Texture Texture);
}
