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
/// One cell on its way into a <c>CHDR</c> record, carrying its own geometry.
/// </summary>
/// <remarks>
/// <b>The offsets are NOT here, deliberately.</b> A cell's blob offsets are a
/// function of where the sorted directory put it, so letting a caller pass them
/// would let a caller pass a lie: a directory entry pointing into the wrong part of
/// <c>CMSH</c> is a cell rendering somebody else's geometry, and no reader can tell
/// the difference. <see cref="ScmapBuilder"/> sorts the cells, lays the two blob
/// sections out in that same order, and fills the offsets in itself, so the
/// directory and the blobs cannot disagree about order at all.
/// </remarks>
/// <param name="Coord">The cell.</param>
/// <param name="RenderBounds">
/// The cell's TRUE render bounds, never the cell cube: a border-spanning brush is
/// owned by exactly one cell and its surfaces routinely overhang, so culling
/// against the cube makes the overhang vanish while it is plainly visible.
/// </param>
/// <param name="Submeshes">
/// The cell's owned render geometry, one entry per distinct material. Null or
/// empty is legal and common: a resident-only cell owns no render geometry, so the
/// compile produces no artifact for it and the directory says <c>MeshSize</c> zero.
/// </param>
/// <param name="BspNodes">
/// The cell's flat solid-leaf tree. Null means the cell has no tree at all and
/// <c>BspSize</c> is zero; an EMPTY array is a different thing, a tree that is one
/// bare leaf, and it still gets a blob so that <paramref name="BspRootIndex"/>
/// survives.
/// </param>
/// <param name="BspRootIndex">
/// The root's child code: an index into <paramref name="BspNodes"/>, or one of
/// <c>FlatBspNode</c>'s two leaf codes.
/// </param>
public readonly record struct ScmapChunkSource(
    ChunkCoord Coord,
    Aabb RenderBounds,
    ScmapSubmeshSource[]? Submeshes = null,
    FlatBspNode[]? BspNodes = null,
    int BspRootIndex = FlatBspNode.EmptyLeaf);

/// <summary>
/// One cell's geometry for one material, on its way into a <c>CMSH</c> submesh.
/// </summary>
/// <param name="AssetIndex">
/// The <c>ASTB</c> row this submesh's material sits at, or
/// <c>ScmapFormat.NoAssetIndex</c> when the surfaces name none. NEVER a
/// <c>MaterialRef.Id</c>: an id is per-process interning order and means nothing
/// in a file.
/// </param>
/// <param name="Vertices">Interleaved vertex data, in the engine's standard 8-float layout.</param>
/// <param name="Indices">
/// Index data, zero-based at this submesh's own first vertex rather than at the
/// cell's, which is what lets the loader hand it straight to
/// <c>Renderer.CreateMesh</c> with no slicing.
/// </param>
public readonly record struct ScmapSubmeshSource(uint AssetIndex, float[] Vertices, uint[] Indices);

/// <summary>
/// One authored brush plane's surface, on its way into a 48-byte <c>BRSH</c> face
/// record.
/// </summary>
/// <param name="AssetIndex">
/// The <c>ASTB</c> row this face's material sits at, or
/// <c>ScmapFormat.NoAssetIndex</c> when it names none.
/// </param>
/// <param name="UAxis">Brush-local U axis, or zero for world-aligned.</param>
/// <param name="VAxis">Brush-local V axis, or zero for world-aligned.</param>
/// <param name="UOffset">U offset, in repeats.</param>
/// <param name="VOffset">V offset, in repeats.</param>
/// <param name="UScale">World units per U repeat.</param>
/// <param name="VScale">World units per V repeat.</param>
public readonly record struct ScmapFaceSource(
    uint AssetIndex,
    Vector3 UAxis,
    Vector3 VAxis,
    float UOffset,
    float VOffset,
    float UScale,
    float VScale);

/// <summary>
/// One authored brush kept in <c>BRSH</c>, by the node it hangs on.
/// </summary>
/// <remarks>
/// <b>A part brush is here whatever the cook was asked for; a world brush only
/// under <c>--keep-brush-source</c>.</b> A part's planes live nowhere else, so
/// dropping them ships a level whose parts are invisible; a world brush's geometry
/// is already in the chunks, which is exactly why re-carving it draws every wall
/// twice. <c>ScmapBrushSource.IsReCarvable</c> is the predicate that tells the two
/// apart at load, and it reads the node's payload kind rather than this table.
/// </remarks>
/// <param name="NodeIndex">The <c>NODE</c> record this brush hangs on.</param>
/// <param name="Planes">The brush-local planes, exactly as authored.</param>
/// <param name="Faces">
/// One face per plane, index-aligned. A different count is refused: one
/// <c>FaceSurface</c> per plane is the invariant the whole per-face material path
/// rests on, and a mismatch is an indexing bug rather than a rendering one.
/// </param>
public readonly record struct ScmapBrushSourceEntry(
    int NodeIndex,
    Plane[] Planes,
    ScmapFaceSource[] Faces);

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
