using System.Numerics;
using System.Runtime.InteropServices;

namespace SpectraEngine.Core.Assets.Models;

/// <summary>
/// One drawable range of a cooked model, exactly as its forty bytes sit in a
/// <c>SUBM</c> section.
/// </summary>
/// <remarks>
/// <para><b>A submesh is an index RANGE, never its own buffer.</b> That is the
/// one place this format deliberately differs from the compiled map's chunk
/// meshes, which do split their arrays: a chunk's submeshes are uploaded and
/// destroyed independently per cell, while a model's LODs must share one vertex
/// and index buffer or an LOD switch stops being a draw-range change and becomes
/// GPU resource churn.</para>
/// <para><b><see cref="MaterialNameOffset"/> names a logical asset path, not a
/// pack entry index and not a file offset.</b> One material is then stored once
/// however many submeshes wear it, a material can be recooked without rewriting
/// every model that names it, and the reference mechanism is the same one every
/// other cooked asset uses: a path interned through <c>MaterialRegistry</c> into
/// a <c>MaterialRef</c>, which is what makes a model submesh and a chunk submesh
/// the same shape at the draw call.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct SmodelSubmesh
{
    /// <summary>First index of this range within <c>IBUF</c>.</summary>
    public readonly uint IndexStart;

    /// <summary>How many indices this range covers.</summary>
    public readonly uint IndexCount;

    /// <summary>
    /// Offset into the <c>NAME</c> blob of this submesh's material path, or
    /// <see cref="SmodelFormat.NameOffsetAbsent"/> when it names none and the
    /// loader should fall back to the engine's default material.
    /// </summary>
    public readonly uint MaterialNameOffset;

    /// <summary>Per-submesh flags. None defined in v1; written zero.</summary>
    public readonly uint Flags;

    /// <summary>Model-local minimum corner of this submesh's bounds.</summary>
    public readonly Vector3 BoundsMin;

    /// <summary>Model-local maximum corner of this submesh's bounds.</summary>
    public readonly Vector3 BoundsMax;

    /// <summary>Builds one submesh record. Every field is assigned.</summary>
    public SmodelSubmesh(
        uint indexStart,
        uint indexCount,
        uint materialNameOffset,
        Vector3 boundsMin,
        Vector3 boundsMax,
        uint flags = 0)
    {
        IndexStart = indexStart;
        IndexCount = indexCount;
        MaterialNameOffset = materialNameOffset;
        Flags = flags;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
    }

    /// <summary>Whether this submesh names a material at all.</summary>
    public bool HasMaterial => MaterialNameOffset != SmodelFormat.NameOffsetAbsent;
}
