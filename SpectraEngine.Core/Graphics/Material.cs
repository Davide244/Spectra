using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// Pairs a <see cref="ShaderProgram"/> with the per-surface parameters used to
/// draw it. Parameters are stored as typed maps and pushed to the shader in
/// <see cref="Apply"/>.
/// </summary>
/// <remarks>
/// <para>
/// Materials are usually loaded from a <c>.spectramat</c> file through
/// <see cref="Assets.AssetManager.LoadMaterial"/> (see
/// <see cref="Assets.MaterialParser"/> for the format); building one in code
/// with the fluent setters is equally valid and is what the built-in fallback
/// material does.
/// </para>
/// <para>
/// <b>Shader binding order.</b> <see cref="Apply"/> assumes the owning shader is
/// the one the caller is about to draw with, but deliberately does not call
/// <see cref="ShaderProgram.Use"/> itself: the OpenGL pipelines bind first and
/// then set uniforms, while the D3D pipelines stage uniforms into a constant
/// buffer that <c>Use</c> flushes, so they set first and bind afterwards. That
/// divergence lives in the pipelines on purpose — do not "fix" it here.
/// </para>
/// <para>
/// <b>Every parameter the shader reads must be set here.</b> <see cref="Apply"/>
/// pushes exactly what this material declares and nothing else, and uniform
/// state survives between draws on every backend (OpenGL keeps it on the program
/// object; the D3D backends keep a persistent constant-buffer shadow). A
/// parameter this material omits therefore keeps whatever the previously drawn
/// material left in it — and on the first draw of a frame keeps the backend's
/// zero initialisation, which for a colour means the surface renders black.
/// Materials built by <see cref="Assets.AssetManager"/> are seeded with the
/// built-in shader's parameters before the file's own are applied, so an
/// omission in a <c>.spectramat</c> is safe; a material built in code against a
/// custom shader has to set that shader's parameters itself.
/// </para>
/// <para>
/// <b>Cost.</b> Applying a material is allocation-free: every map is enumerated
/// through a struct enumerator, and a texture binding resolves with a field
/// read. It runs once per draw call, every frame.
/// </para>
/// </remarks>
public sealed class Material
{
    private readonly Dictionary<string, float> _floats = new();
    private readonly Dictionary<string, Vector2> _vec2 = new();
    private readonly Dictionary<string, Vector3> _vec3 = new();
    private readonly Dictionary<string, Vector4> _vec4 = new();
    private readonly Dictionary<string, TextureBinding> _textures = new();

    /// <summary>
    /// Creates a material drawn with <paramref name="shader"/>. Null is allowed
    /// and means "not drawable yet" — the fallback material is built before a
    /// renderer (and therefore before any shader) exists, and a material whose
    /// shader failed to resolve must still be a usable object rather than a null
    /// reference waiting to be dereferenced in the draw loop.
    /// </summary>
    public Material(ShaderProgram? shader)
    {
        Shader = shader;
    }

    /// <summary>
    /// The program this material draws with, or null when none could be
    /// resolved — pipelines skip such an item instead of throwing mid-frame.
    /// Assigned by the asset manager once a renderer is attached.
    /// </summary>
    public ShaderProgram? Shader { get; internal set; }

    /// <summary>Human-readable name, used in logs and (later) editor UI.</summary>
    public string Name { get; set; } = "unnamed";

    /// <summary>
    /// Content-root-relative path this material was loaded from, or null when it
    /// was built in code.
    /// </summary>
    public string? SourcePath { get; internal set; }

    /// <summary>Number of scalar/vector parameters this material pushes.</summary>
    public int ParameterCount => _floats.Count + _vec2.Count + _vec3.Count + _vec4.Count;

    /// <summary>Number of sampler slots this material binds.</summary>
    public int TextureCount => _textures.Count;

    public Material SetFloat(string name, float value) { _floats[name] = value; return this; }
    public Material SetVector2(string name, Vector2 value) { _vec2[name] = value; return this; }
    public Material SetVector3(string name, Vector3 value) { _vec3[name] = value; return this; }
    public Material SetVector4(string name, Vector4 value) { _vec4[name] = value; return this; }

    /// <summary>
    /// Binds <paramref name="texture"/> to the named sampler on
    /// <paramref name="unit"/>. The exact instance is pinned — use the
    /// <see cref="SetTexture(string, int, Assets.TextureAsset)"/> overload for
    /// anything the asset manager may swap underneath you.
    /// </summary>
    public Material SetTexture(string name, int unit, Texture texture)
    {
        _textures[name] = new TextureBinding(unit, texture, null);
        return this;
    }

    /// <summary>
    /// Binds an asset handle to the named sampler on <paramref name="unit"/>.
    /// Resolved at <see cref="Apply"/> time, so the material follows the handle
    /// through its placeholder-to-loaded swap and through hot-reloads instead of
    /// pinning whichever texture happened to be current here.
    /// </summary>
    public Material SetTexture(string name, int unit, Assets.TextureAsset asset)
    {
        _textures[name] = new TextureBinding(unit, null, asset);
        return this;
    }

    /// <summary>Reads back a scalar parameter set on this material.</summary>
    public bool TryGetFloat(string name, out float value) => _floats.TryGetValue(name, out value);

    /// <summary>Reads back a 2-vector parameter set on this material.</summary>
    public bool TryGetVector2(string name, out Vector2 value) => _vec2.TryGetValue(name, out value);

    /// <summary>Reads back a 3-vector parameter set on this material.</summary>
    public bool TryGetVector3(string name, out Vector3 value) => _vec3.TryGetValue(name, out value);

    /// <summary>Reads back a 4-vector parameter set on this material.</summary>
    public bool TryGetVector4(string name, out Vector4 value) => _vec4.TryGetValue(name, out value);

    /// <summary>
    /// Reads back a sampler binding: the unit it occupies and the texture it
    /// currently resolves to. Render thread only — resolving an asset handle
    /// reads <see cref="Assets.TextureAsset.Texture"/>.
    /// </summary>
    public bool TryGetTexture(string name, out int unit, [MaybeNullWhen(false)] out Texture texture)
    {
        if (_textures.TryGetValue(name, out TextureBinding binding))
        {
            unit = binding.Unit;
            texture = binding.Resolve();
            return true;
        }

        unit = 0;
        texture = null;
        return false;
    }

    /// <summary>
    /// Drops every sampler binding. Used when the textures behind them are about
    /// to be destroyed (asset-manager teardown), so nothing keeps resolving to a
    /// disposed GPU object.
    /// </summary>
    internal void ClearTextures() => _textures.Clear();

    /// <summary>
    /// Uploads this material's parameters to the (already bound, or about to be
    /// bound — see the type's remarks) shader. No-op without a shader.
    /// </summary>
    /// <remarks>
    /// Only what this material declares is written; a parameter it never set
    /// keeps the value the previous draw left in the shader. See the type's
    /// remarks for why every material has to carry a full parameter set.
    /// </remarks>
    public void Apply()
    {
        if (Shader is not { } shader) return;

        foreach (var (name, value) in _floats) shader.SetUniform(name, value);
        foreach (var (name, value) in _vec2) shader.SetUniform(name, value);
        foreach (var (name, value) in _vec3) shader.SetUniform(name, value);
        foreach (var (name, value) in _vec4) shader.SetUniform(name, value);
        foreach (var (name, binding) in _textures) shader.SetTexture(name, binding.Unit, binding.Resolve());
    }

    // Exactly one of Direct/Asset is set. A struct, and Resolve is a field read,
    // so following an asset handle costs nothing per draw call.
    private readonly record struct TextureBinding(int Unit, Texture? Direct, Assets.TextureAsset? Asset)
    {
        public Texture Resolve() => Asset is not null ? Asset.Texture : Direct!;
    }
}
