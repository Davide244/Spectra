using System.Numerics;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// One light, flattened for upload: everything a shader needs and nothing that
/// points back into the scene graph.
/// </summary>
/// <remarks>
/// <para>
/// The two <see cref="Vector4"/>s are the shape the GPU wants, not an accident
/// of convenience. <c>vec4</c> is one of only two element types the engine can
/// fill an array uniform with, because it is one of only two whose managed
/// stride already matches HLSL's 16-byte array stride; see
/// <c>ShaderProgram.SetUniform</c>. Packing the light this way here means the
/// upload is a straight copy with no per-frame repacking.
/// </para>
/// </remarks>
/// <param name="PositionRange">
/// For a point light, xyz is the world position and w the range. For a
/// directional light, xyz is the direction the light TRAVELS (normalised) and w
/// is zero, which is also how a shader tells the two apart.
/// </param>
/// <param name="ColorIntensity">Linear RGB in xyz, already multiplied by intensity. w is unused.</param>
public readonly record struct RenderLight(Vector4 PositionRange, Vector4 ColorIntensity)
{
    /// <summary>True when this is a directional light, by the w == 0 convention above.</summary>
    public bool IsDirectional => PositionRange.W == 0f;
}
