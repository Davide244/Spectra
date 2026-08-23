using System.Collections.Generic;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// A full-screen shader invocation: the program, plus the values to give it.
/// </summary>
/// <remarks>
/// <para>
/// <b>It stages values rather than setting them, and that is not fussiness.</b>
/// The three backends disagree about when a uniform may be written: OpenGL's
/// <c>glUniform</c> acts on the currently active program, so <c>Use()</c> must
/// come first, while both D3D backends write into CPU-side constant shadows that
/// <c>Use()</c> then flushes, so <c>Use()</c> must come last. There is no single
/// order that works everywhere, which is why the draw itself stays per-backend
/// and only the intent lives here.
/// </para>
/// <para>
/// The tempting <c>Use(); set values; Use();</c> is worse than either: on D3D12
/// the first call clears the pending texture table, so the second stages a
/// descriptor table whose source slot has fallen back to the white placeholder,
/// and the pass samples white.
/// </para>
/// </remarks>
public sealed class PostPass
{
    private readonly Dictionary<string, float> _floats = [];
    private readonly Dictionary<string, (int Unit, Texture Texture)> _textures = [];

    public PostPass(ShaderProgram shader)
    {
        Shader = shader;
    }

    /// <summary>The program this pass runs.</summary>
    public ShaderProgram Shader { get; }

    /// <summary>Stages a scalar uniform.</summary>
    public PostPass SetUniform(string name, float value)
    {
        _floats[name] = value;
        return this;
    }

    /// <summary>Stages a texture on a sampler unit.</summary>
    public PostPass SetTexture(string name, int unit, Texture texture)
    {
        _textures[name] = (unit, texture);
        return this;
    }

    /// <summary>
    /// Replays the staged values onto the program. Each backend calls this at
    /// the point in its own sequence where writing uniforms is legal.
    /// </summary>
    internal void ApplyTo(ShaderProgram shader)
    {
        foreach ((string name, float value) in _floats)
            shader.SetUniform(name, value);
        foreach ((string name, (int unit, Texture texture)) in _textures)
            shader.SetTexture(name, unit, texture);
    }
}
