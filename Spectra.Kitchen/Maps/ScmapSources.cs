using System;
using System.Numerics;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Maps.Compiled;
using SpectraEngine.Core.Scene;

namespace Spectra.Kitchen.Maps;

/// <summary>
/// One node on its way into a <c>NODE</c> record.
/// </summary>
/// <param name="Id">The authored node id, written as its RFC 4122 bytes.</param>
/// <param name="Name">
/// The authored node name, which is also its target name. Interned into
/// <c>STRT</c>; an empty name is index 0 rather than an error.
/// </param>
/// <param name="ParentIndex">
/// The index this node's parent was added at, or -1 for a root. Must be less than
/// this node's own index, which is what the pre-order walk gives for free.
/// </param>
/// <param name="LocalTransform">
/// The AUTHORED local transform, never a composed world matrix. A world matrix is
/// derived, and replaying the same composition reproduces bit-identical matrices,
/// which is exactly what the compile cache's exact-equality keying and the bake
/// oracle depend on; a baked matrix breaks that oracle in a way that looks like a
/// floating-point mystery.
/// </param>
/// <param name="PayloadKind">What the node carries.</param>
/// <param name="PayloadFlags">
/// The flag bits only. The realm and state fields are two-bit enums living inside
/// the same half-word and are passed separately, because a two-bit field spelled
/// as flags is how a value of 2 gets written into bit 1.
/// </param>
/// <param name="DeclaredRealm">The DECLARED realm, never the effective one.</param>
/// <param name="DeclaredState">The DECLARED state, never the effective one.</param>
/// <param name="PayloadIndex">
/// Index into whatever table the kind names. Unused for a baked brush, whose
/// geometry lives in the chunks rather than in a table of its own.
/// </param>
public readonly record struct ScmapNodeSource(
    Guid Id,
    string Name,
    int ParentIndex,
    Transform LocalTransform,
    ScmapPayloadKind PayloadKind,
    ScmapPayloadFlags PayloadFlags = ScmapPayloadFlags.None,
    ScmapNodeRealm DeclaredRealm = ScmapNodeRealm.Inherit,
    ScmapNodeState DeclaredState = ScmapNodeState.Inherit,
    uint PayloadIndex = 0);

/// <summary>
/// One cell on its way into a <c>CHDR</c> record.
/// </summary>
/// <param name="Coord">The cell.</param>
/// <param name="RenderBounds">
/// The cell's TRUE render bounds, never the cell cube: a border-spanning brush is
/// owned by exactly one cell and its surfaces routinely overhang, so culling
/// against the cube makes the overhang vanish while it is plainly visible.
/// </param>
/// <param name="MeshOffset">Offset of this cell's mesh blob within <c>CMSH</c>.</param>
/// <param name="MeshSize">
/// Bytes of mesh blob. Zero is legal and common: a resident-only cell owns no
/// render geometry, so the compile produces no artifact for it.
/// </param>
/// <param name="BspOffset">Offset of this cell's BSP blob within <c>CBSP</c>.</param>
/// <param name="BspSize">Bytes of BSP blob; zero when the cell has no tree.</param>
public readonly record struct ScmapChunkSource(
    ChunkCoord Coord,
    Aabb RenderBounds,
    uint MeshOffset = 0,
    uint MeshSize = 0,
    uint BspOffset = 0,
    uint BspSize = 0);

/// <summary>
/// One asset on its way into an <c>ASTB</c> record.
/// </summary>
/// <param name="Kind">What kind of asset it is, in the pack's own vocabulary.</param>
/// <param name="ContentPath">
/// The normalised content-relative LOGICAL path, which is the identity every other
/// cooked format already uses. Never a pack entry index and never a file offset.
/// </param>
/// <param name="ContentHash">
/// Low 64 bits of the cooked payload's hash, or zero when the bake does not know
/// it. Advisory: a mismatch against the resident pack warns rather than failing,
/// because a texture recooked on its own is the normal case a patch pack exists
/// for.
/// </param>
public readonly record struct ScmapAssetSource(
    PackEntryKind Kind,
    string ContentPath,
    ulong ContentHash = 0);

/// <summary>One spawn point on its way into a <c>META</c> spawn record.</summary>
/// <param name="Position">Where a player enters.</param>
/// <param name="Rotation">Which way they face.</param>
public readonly record struct ScmapSpawnSource(Vector3 Position, Quaternion Rotation);
