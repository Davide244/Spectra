using System.Numerics;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// One light, flattened for upload: everything a shader needs and nothing that
/// points back into the scene graph.
/// </summary>
/// <remarks>
/// <para>
/// The four <see cref="Vector4"/>s are the shape the GPU wants, not an accident
/// of convenience. <c>vec4</c> is one of only two element types the engine can
/// fill an array uniform with, because it is one of only two whose managed
/// stride already matches HLSL's 16-byte array stride; see
/// <c>ShaderProgram.SetUniform</c>. Packing the light this way here means the
/// upload is a straight copy with no per-frame repacking.
/// </para>
/// <para>
/// <b>FOUR parallel arrays and not a struct array.</b> A struct array generates
/// valid-looking text and then silently does nothing on OpenGL, which is the
/// worst available failure: no error, no warning, and a picture that is merely
/// unlit. Four is also the minimum: the bitangent of an area light is recovered
/// by a cross product rather than stored, which is what keeps this at four
/// rather than five.
/// </para>
/// <para>
/// <b>The TYPE moved out of <c>PositionRange.w</c>.</b> That field encoded
/// exactly two kinds - "w is zero, so this is directional" - and a third kind
/// had nowhere to go. It now lives in <see cref="ColorIntensity"/>'s w, which
/// was documented as unused, so the range keeps a field of its own and a spot
/// light can have both.
/// </para>
/// </remarks>
/// <param name="PositionRange">
/// xyz is the world position, or for a directional light the direction the
/// light TRAVELS (normalised). w is the range, and is ignored for a directional
/// light.
/// </param>
/// <param name="ColorIntensity">
/// Linear RGB in xyz, already multiplied by intensity. w is the
/// <see cref="RenderLightType"/>, as a float, because that is the only numeric
/// type a <c>vec4</c> carries.
/// </param>
/// <param name="Axis">
/// xyz is the direction the light faces: a spot's cone axis, an area light's
/// surface normal. w carries the shape's first scalar - cos(outer half-angle)
/// for a spot, half-height for a rect, radius for a disc.
/// </param>
/// <param name="Tangent">
/// xyz is the area light's first in-plane axis (its "right"), unused by point
/// and directional lights. w carries the shape's second scalar - cos(inner
/// half-angle) for a spot, half-width for a rect.
/// </param>
public readonly record struct RenderLight(
    Vector4 PositionRange,
    Vector4 ColorIntensity,
    Vector4 Axis = default,
    Vector4 Tangent = default)
{
    /// <summary>What kind of light this is.</summary>
    public RenderLightType Type => (RenderLightType)(int)ColorIntensity.W;

    /// <summary>True when this light has no position, only a direction.</summary>
    public bool IsDirectional => Type == RenderLightType.Directional;
}

/// <summary>
/// The light kinds a shader can tell apart, as the integers it compares
/// against.
/// </summary>
/// <remarks>
/// <b>The numbers are a contract with two shader files</b> (<c>DeferredLight</c>
/// and <c>Lit</c>) and are therefore append-only: inserting a value renumbers
/// every kind after it, and a shader comparing against the old number lights
/// the wrong shape with no error anywhere. They match
/// <see cref="Scene.LightKind"/>'s declaration order, and a test pins that they
/// keep matching.
/// </remarks>
public enum RenderLightType
{
    /// <summary>Parallel rays from infinitely far away.</summary>
    Directional = 0,

    /// <summary>Radiates in every direction from a point.</summary>
    Point = 1,

    /// <summary>A cone from a point, with a soft edge between two half-angles.</summary>
    Spot = 2,

    /// <summary>A one-sided rectangle that emits from its whole surface.</summary>
    Rect = 3,

    /// <summary>A one-sided disc that emits from its whole surface.</summary>
    Disc = 4,
}
