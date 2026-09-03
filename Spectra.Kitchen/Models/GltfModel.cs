using System.Collections.Generic;
using System.Numerics;

namespace Spectra.Kitchen.Models;

/// <summary>
/// One drawable piece of a read glTF: the engine's own eight-float interleaved
/// vertices, its indices, and which material it wears.
/// </summary>
/// <remarks>
/// <para><b>One submesh per (node, mesh primitive) pair, with the node's
/// accumulated transform already BAKED into the positions and normals.</b> A
/// <c>.smodel</c> has one vertex buffer and no hierarchy section, so the
/// hierarchy has to be spent somewhere and the only place it can be spent is
/// here. A mesh referenced by two nodes therefore becomes two submeshes, which
/// is what makes the second copy appear where the file says it does.</para>
/// <para><b>Bounds are of the BAKED positions</b>, so they mean what the model's
/// own bounds mean and no consumer has to know a transform was applied.</para>
/// </remarks>
public sealed class GltfSubmesh
{
    internal GltfSubmesh(
        string name,
        int materialIndex,
        float[] vertices,
        uint[] indices,
        Vector3 boundsMin,
        Vector3 boundsMax)
    {
        Name = name;
        MaterialIndex = materialIndex;
        Vertices = vertices;
        Indices = indices;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
    }

    /// <summary>The node and primitive this came from, for a diagnostic.</summary>
    public string Name { get; }

    /// <summary>Index into <see cref="GltfModel.Materials"/>, or -1 for none.</summary>
    public int MaterialIndex { get; }

    /// <summary>Interleaved position, normal and UV0: eight floats per vertex.</summary>
    public float[] Vertices { get; }

    /// <summary>Three indices per triangle, zero-based within this submesh.</summary>
    public uint[] Indices { get; }

    /// <summary>Minimum corner of the baked positions.</summary>
    public Vector3 BoundsMin { get; }

    /// <summary>Maximum corner of the baked positions.</summary>
    public Vector3 BoundsMax { get; }

    /// <summary>Vertices in <see cref="Vertices"/>.</summary>
    public int VertexCount => Vertices.Length / 8;
}

/// <summary>
/// A glTF material, reduced to the two things a cook can act on.
/// </summary>
/// <param name="Name">
/// The material's name, which is the key an authored <c>.spectramat</c> is
/// matched by. Empty when the file named none.
/// </param>
/// <param name="BaseColorImageUri">
/// The URI of the base colour image, exactly as the file wrote it, or null when
/// the material names none or names an embedded one. Carried for a DIAGNOSTIC
/// and nothing else: it is what makes "author a material for this" actionable
/// rather than a scolding, and it is deliberately not resolved to a content path,
/// because a cook that resolved it would be inventing an asset reference the
/// cooked format has no field to carry.
/// </param>
public readonly record struct GltfMaterial(string Name, string? BaseColorImageUri);

/// <summary>
/// What <see cref="GltfReader"/> made of one file.
/// </summary>
public sealed class GltfModel
{
    internal GltfModel(
        IReadOnlyList<GltfSubmesh> submeshes,
        IReadOnlyList<GltfMaterial> materials,
        Vector3 boundsMin,
        Vector3 boundsMax,
        IReadOnlyList<string> dropped)
    {
        Submeshes = submeshes;
        Materials = materials;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
        Dropped = dropped;
    }

    /// <summary>Every drawable piece, in scene walk order.</summary>
    public IReadOnlyList<GltfSubmesh> Submeshes { get; }

    /// <summary>The file's material table, index-aligned with what it declared.</summary>
    public IReadOnlyList<GltfMaterial> Materials { get; }

    /// <summary>Minimum corner over every submesh.</summary>
    public Vector3 BoundsMin { get; }

    /// <summary>Maximum corner over every submesh.</summary>
    public Vector3 BoundsMax { get; }

    /// <summary>
    /// What the file carried that the cook did not, each named once.
    /// </summary>
    /// <remarks>
    /// <b>Dropping is not refusing, and it is not silence either.</b> A vertex
    /// colour set, a tangent, a second UV, a skin: none of them makes the model
    /// unusable and none of them survives into a v1 <c>.smodel</c>, so the honest
    /// answer is to carry the geometry and say what was left behind. Silence here
    /// would make "my vertex colours do nothing in the engine" a question with no
    /// answer anywhere in a build log.
    /// </remarks>
    public IReadOnlyList<string> Dropped { get; }
}
