using System.Collections.Generic;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// One occupied cell of a <see cref="ChunkGrid"/>: which brush placements live
/// here, in two distinct roles, plus the carved surfaces of the brushes this
/// cell owns.
/// <para>
/// <b>Owner vs. resident.</b> Every brush is OWNED by exactly one cell — the
/// cell containing the center of its weld-band-inflated world AABB — and that
/// cell alone holds the brush's render surfaces, so nothing is ever drawn (or
/// meshed) twice. The same brush is RESIDENT in every cell its inflated AABB
/// touches (the owner cell included): solidity queries and weld bands in any of
/// those cells need the brush's geometry, footprint diffing dirties all of
/// them. The per-chunk pipeline stages consume the distinction: snap/weld runs
/// per owner cell (W2, see <see cref="ChunkWelder"/>), the cell's BSP tree is
/// built from its RESIDENTS (W3, see <see cref="ChunkBspBuilder"/> — queries
/// need every boundary the cell can reach), and the per-cell render mesh (W4,
/// see <see cref="ChunkMeshBuilder"/>) is owner-only like the weld, so nothing
/// is drawn twice.
/// </para>
/// </summary>
/// <remarks>
/// Immutable once the <see cref="CsgWorld"/> build that created it returns
/// (the mutators are internal and only called during <c>ChunkGrid.Build</c>
/// and the assembly's weld and BSP attachments), so any number of threads may read it —
/// the same deep-immutability contract as every other compiled CSG artifact.
/// </remarks>
public sealed class WorldChunk
{
    private readonly List<int> _ownedBrushIndices = [];
    private readonly List<int> _residentBrushIndices = [];
    private readonly List<Polygon> _surfaces = [];
    private readonly List<Polygon> _weldedSurfaces = [];

    internal WorldChunk(ChunkCoord coord) => Coord = coord;

    /// <summary>This cell's coordinates in the grid.</summary>
    public ChunkCoord Coord { get; }

    /// <summary>
    /// Placement indices (into the compile's placement list) owned by this
    /// cell. Ascending by construction — the grid build visits placements in
    /// index order — so enumeration is deterministic.
    /// </summary>
    public IReadOnlyList<int> OwnedBrushIndices => _ownedBrushIndices;

    /// <summary>
    /// Placement indices resident in this cell: every brush whose inflated
    /// world AABB touches it, a superset of <see cref="OwnedBrushIndices"/>.
    /// Ascending by construction, so enumeration is deterministic.
    /// </summary>
    public IReadOnlyList<int> ResidentBrushIndices => _residentBrushIndices;

    /// <summary>
    /// The carved (pre-snap, world-space) surfaces of the brushes this cell
    /// owns, concatenated in placement order — the raw inputs the per-cell
    /// snap+weld (<see cref="ChunkWelder"/>) consumes for this cell.
    /// </summary>
    public IReadOnlyList<Polygon> Surfaces => _surfaces;

    /// <summary>
    /// The snapped+welded surfaces of the brushes this cell owns, concatenated
    /// in placement order — the per-cell counterpart of the flat post-weld
    /// <see cref="CsgWorld.Surfaces"/>, holding the same <see cref="Polygon"/>
    /// instances (each placement's welded surfaces appear once here, under its
    /// owner cell, and once in the flat list). Attached by the world assembly
    /// after the per-cell weld; the union over all cells equals the global
    /// snap+weld of the full carve, bit for bit — the W2 oracle.
    /// </summary>
    public IReadOnlyList<Polygon> WeldedSurfaces => _weldedSurfaces;

    /// <summary>
    /// This cell's solid-leaf BSP tree, built from the complete welded surface
    /// sets of ALL its resident brushes (see <see cref="ChunkBspBuilder"/> for
    /// the closure argument). Attached by the world assembly after the
    /// per-cell weld; non-null — and immutable, like everything else here —
    /// once the <see cref="CsgWorld"/> build that created the chunk returns.
    /// Routed queries (<see cref="CsgWorld.ContainsPoint"/> /
    /// <see cref="CsgWorld.Raycast"/>) consult it for points and ray segments
    /// inside this cell only; its answers outside the cell's weld-band
    /// neighbourhood are not meaningful.
    /// </summary>
    public BspTree Bsp { get; private set; } = null!;

    internal void AttachBsp(BspTree tree) => Bsp = tree;

    internal void AddResident(int placementIndex) => _residentBrushIndices.Add(placementIndex);

    internal void AddOwned(int placementIndex, Polygon[] surfaces)
    {
        _ownedBrushIndices.Add(placementIndex);
        _surfaces.AddRange(surfaces);
    }

    internal void AddWeldedSurfaces(Polygon[] welded) => _weldedSurfaces.AddRange(welded);
}
