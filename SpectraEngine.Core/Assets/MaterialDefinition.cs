using SpectraEngine.Core.Graphics;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Core.Assets;

/// <summary>Type of a scalar/colour/vector parameter declared in a material file.</summary>
public enum MaterialParameterKind
{
    /// <summary>A single scalar, uploaded through <c>SetUniform(string, float)</c>.</summary>
    Float,

    /// <summary>Two components.</summary>
    Vector2,

    /// <summary>Three components — also what a three-component <c>color</c> produces.</summary>
    Vector3,

    /// <summary>Four components — also what a four-component <c>color</c> produces.</summary>
    Vector4,
}

/// <summary>
/// One scalar/colour/vector parameter parsed from a <c>.spectramat</c> file.
/// </summary>
/// <remarks>
/// The value is always carried as a <see cref="Vector4"/> and narrowed by
/// <see cref="Kind"/> when it is pushed into a <see cref="Material"/>: a single
/// storage shape keeps the parsed definition a flat, copyable struct list
/// instead of a polymorphic object graph (which AOT and the allocation budget
/// would both rather avoid). Unused components are zero.
/// </remarks>
public readonly record struct MaterialParameter(string Name, MaterialParameterKind Kind, Vector4 Value)
{
    /// <summary>The value as a scalar (component X).</summary>
    public float AsFloat => Value.X;

    /// <summary>The value as a 2-vector (components X, Y).</summary>
    public Vector2 AsVector2 => new(Value.X, Value.Y);

    /// <summary>The value as a 3-vector (components X, Y, Z).</summary>
    public Vector3 AsVector3 => new(Value.X, Value.Y, Value.Z);

    /// <summary>The value as a 4-vector.</summary>
    public Vector4 AsVector4 => Value;
}

/// <summary>
/// One texture binding parsed from a <c>.spectramat</c> file: which sampler to
/// fill, which content-relative image file to fill it from, and how to sample it.
/// </summary>
/// <param name="Name">Sampler name in the shader, e.g. <c>uDiffuse</c>.</param>
/// <param name="TexturePath">Content-root-relative path of the image file, as written in the file.</param>
/// <param name="Unit">Texture unit, assigned by declaration order within the file (first slot is 0).</param>
/// <param name="Filter">Sampling filter; <see cref="TextureFilter.LinearMipmap"/> unless the file says otherwise.</param>
/// <param name="Wrap">Wrap mode; <see cref="TextureWrap.Repeat"/> unless the file says otherwise.</param>
/// <param name="ColorSpace">
/// How to read the file's bytes; <see cref="TextureColorSpace.Srgb"/> unless the
/// line says <c>data</c>. See <see cref="MaterialParser"/> for why that is the
/// default.
/// </param>
public readonly record struct MaterialTextureSlot(
    string Name,
    string TexturePath,
    int Unit,
    TextureFilter Filter,
    TextureWrap Wrap,
    TextureColorSpace ColorSpace);

/// <summary>
/// The parsed contents of a <c>.spectramat</c> file — a pure CPU-side value with
/// no GPU or asset-manager dependencies. See <see cref="MaterialParser"/> for the
/// file format and for how it is produced.
/// </summary>
/// <remarks>
/// A definition is always complete enough to build a material from: anything the
/// parser could not make sense of is reported through <see cref="Warnings"/>
/// rather than thrown, so one bad line never costs a whole material. Immutable
/// after construction, so it may be read from any thread.
/// </remarks>
public sealed class MaterialDefinition
{
    internal MaterialDefinition(
        string origin,
        string? shaderName,
        IReadOnlyList<MaterialTextureSlot> textures,
        IReadOnlyList<MaterialParameter> parameters,
        IReadOnlyList<string> warnings)
    {
        Origin = origin;
        ShaderName = shaderName;
        Textures = textures;
        Parameters = parameters;
        Warnings = warnings;
    }

    /// <summary>Where this definition was parsed from; only used to label messages.</summary>
    public string Origin { get; }

    /// <summary>
    /// Shader named by the file's <c>shader</c> key, or null when it did not name
    /// one — in which case the engine's built-in lit shader is used.
    /// </summary>
    public string? ShaderName { get; }

    /// <summary>Texture slots in declaration order; the index is also the texture unit.</summary>
    public IReadOnlyList<MaterialTextureSlot> Textures { get; }

    /// <summary>Scalar/colour/vector parameters in declaration order.</summary>
    public IReadOnlyList<MaterialParameter> Parameters { get; }

    /// <summary>
    /// Everything the parser could not use, one message per problem, each
    /// prefixed with the origin and line number. Empty for a clean file.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Finds a parameter by name (ordinal, case-sensitive — uniform names are).</summary>
    public bool TryGetParameter(string name, out MaterialParameter parameter)
    {
        for (int i = 0; i < Parameters.Count; i++)
        {
            if (Parameters[i].Name == name)
            {
                parameter = Parameters[i];
                return true;
            }
        }

        parameter = default;
        return false;
    }

    /// <summary>Finds a texture slot by sampler name (ordinal, case-sensitive).</summary>
    public bool TryGetTextureSlot(string name, out MaterialTextureSlot slot)
    {
        for (int i = 0; i < Textures.Count; i++)
        {
            if (Textures[i].Name == name)
            {
                slot = Textures[i];
                return true;
            }
        }

        slot = default;
        return false;
    }
}
