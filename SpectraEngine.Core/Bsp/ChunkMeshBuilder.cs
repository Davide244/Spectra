using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using SpectraEngine.Core.Assets;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// The per-cell mesh stage of a static-world compile (W4): every cell that
/// owns render geometry gets its own <see cref="ChunkMesh"/> — one
/// <see cref="ChunkSubmesh"/> per face material over the cell's OWNED
/// snapped+welded surfaces, in the engine's standard vertex layout, plus the
/// render AABB culling tests — and cells whose owned inputs are unchanged since
/// the previous compile reuse their artifact verbatim (via
/// <see cref="CsgMeshCache"/>). The union of the per-cell triangle sets equals
/// the monolithic <see cref="CsgWorld.BuildMesh"/> of the same world, triangle
/// for triangle — that equivalence is this stage's contract, pinned by the
/// oracle tests, and the per-material split preserves it because it only
/// partitions the emission, never changes what is emitted. Ownership keeps the
/// rest trivially true: every placement's welded surfaces appear under exactly
/// one owner cell, so nothing is meshed (or later drawn) twice or dropped.
/// </summary>
/// <remarks>
/// <b>Incrementality.</b> Reuse is validation-driven, not dirty-set-driven,
/// exactly like the weld and BSP stages: a cell re-meshes precisely when some
/// OWNED brush's welded surface array is not reference-identical to the
/// previous compile's (see <see cref="CsgMeshCache"/>). The recorded
/// <see cref="CsgWorld.DirtyCells"/> set would NOT be a safe scope here: a
/// long brush can span far beyond the edit that re-carves it (an overlapping
/// neighbour moving at one end changes its carved — and welded — surfaces),
/// yet its owner cell, which holds ALL of its render surfaces, need not be in
/// the dirty set at all. Welded-array identity catches that case by
/// construction, making stale render geometry impossible instead of argued.
///
/// <para>
/// <b>Render bounds.</b> Each artifact's <see cref="ChunkMesh.RenderBounds"/>
/// is the union AABB of the cell's owned welded surfaces — the true extent of
/// what the cell draws, never the cell's own box (owned surfaces of
/// border-spanning brushes stick out of it).
/// </para>
/// </remarks>
internal static class ChunkMeshBuilder
{
    /// <summary>
    /// Builds (or reuses) every geometry-owning cell's render mesh. Returns
    /// the artifacts in ascending cell order (the grid's canonical
    /// enumeration). Pure CPU work over immutable inputs — background-thread
    /// safe; fresh cells build in parallel, which cannot affect the result
    /// (each artifact is a pure function of its own cell's owned surfaces).
    /// </summary>
    /// <param name="produceCache">
    /// Whether to assemble <paramref name="nextCache"/> for the next compile
    /// (the caching build overloads); artifact reuse only ever happens through
    /// a weld-cache hit chain, so a cache-free compile has nothing to gain
    /// from producing one.
    /// </param>
    internal static IReadOnlyList<ChunkMesh> Build(
        ChunkGrid chunks,
        Polygon[][] weldedPerBrush,
        CsgMeshCache? previousCache,
        bool produceCache,
        out CsgMeshCache? nextCache,
        out CsgMeshStats stats)
    {
        // Only cells with owned render geometry produce an artifact:
        // resident-only cells have nothing to draw, and a cell whose owned
        // brushes were all carved away would yield an empty mesh. Ascending by
        // construction — OrderedChunks is the canonical cell order.
        var geometryCells = new List<WorldChunk>();
        foreach (WorldChunk chunk in chunks.OrderedChunks)
        {
            if (chunk.WeldedSurfaces.Count > 0)
                geometryCells.Add(chunk);
        }

        var meshes = new ChunkMesh[geometryCells.Count];
        CsgMeshCache.Entry[]? entries = produceCache ? new CsgMeshCache.Entry[geometryCells.Count] : null;

        // Reuse pass: cells whose owned welded arrays all validate keep their
        // previous artifact (and carry their entry forward unchanged); the
        // rest are collected for the fresh builds below.
        var needsBuild = new List<int>();
        int reused = 0;
        for (int c = 0; c < geometryCells.Count; c++)
        {
            WorldChunk chunk = geometryCells[c];
            if (previousCache is not null &&
                previousCache.TryGet(chunk.Coord, out CsgMeshCache.Entry? cached) &&
                CsgMeshCache.OwnersMatch(cached, chunk, weldedPerBrush))
            {
                meshes[c] = cached.Mesh;
                if (entries is not null)
                    entries[c] = cached;
                reused++;
                continue;
            }
            needsBuild.Add(c);
        }

        // Fresh builds. Each cell writes only its own slots, so the parallel
        // loop shares no mutable state; a single cell (the common
        // one-brush-edit case) skips the parallel machinery entirely.
        if (needsBuild.Count == 1)
        {
            BuildCell(needsBuild[0]);
        }
        else if (needsBuild.Count > 1)
        {
            Parallel.For(0, needsBuild.Count, i => BuildCell(needsBuild[i]));
        }

        stats = new CsgMeshStats(reused, needsBuild.Count);
        nextCache = entries is not null ? CsgMeshCache.FromEntries(geometryCells, entries) : null;
        return meshes;

        void BuildCell(int c)
        {
            WorldChunk chunk = geometryCells[c];
            ChunkMesh mesh = BuildArtifact(chunk);
            meshes[c] = mesh;
            if (entries is not null)
            {
                // The validation record: the owned welded arrays in the order
                // they were concatenated (see CsgMeshCache.OwnersMatch).
                IReadOnlyList<int> owned = chunk.OwnedBrushIndices;
                var ownedWelded = new Polygon[owned.Count][];
                for (int k = 0; k < ownedWelded.Length; k++)
                    ownedWelded[k] = weldedPerBrush[owned[k]];
                entries[c] = new CsgMeshCache.Entry(ownedWelded, mesh);
            }
        }
    }

    /// <summary>
    /// Builds one geometry-owning cell's render artifact from its attached
    /// welded surfaces — the per-cell core of the mesh stage, factored out so
    /// the incremental compile re-meshes an edit's neighbourhood cells through
    /// the identical code path (bit-identical arrays for identical input).
    /// </summary>
    internal static ChunkMesh BuildArtifact(WorldChunk chunk)
    {
        // The mesh input is the cell's owned welded surfaces, already
        // concatenated in ascending placement-index order — the same
        // per-polygon emission the monolithic BuildMesh performs, so each
        // triangle comes out bit-identical to its monolithic counterpart.
        IReadOnlyList<Polygon> surfaces = chunk.WeldedSurfaces;
        ChunkSubmesh[] submeshes = BuildSubmeshes(surfaces);

        // True render bounds: the union of the owned surfaces' boxes, NOT
        // the cell box — owned surfaces of border-spanning brushes stick
        // out of the cell, and culling by cell bounds would clip them from
        // view (see the class remarks). Computed over ALL owned surfaces,
        // including any the material split dropped as degenerate: the box is
        // the cell's extent, not a function of what happened to emit.
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int s = 0; s < surfaces.Count; s++)
        {
            Aabb b = surfaces[s].Bounds;
            min = Vector3.Min(min, b.Min);
            max = Vector3.Max(max, b.Max);
        }

        return new ChunkMesh(chunk.Coord, submeshes, new Aabb(min, max));
    }

    /// <summary>
    /// Splits one cell's ordered surface list into per-material geometry (see
    /// <see cref="ChunkSubmesh"/>): one vertex/index array set per distinct
    /// face material, emitted in ASCENDING material id.
    /// </summary>
    /// <remarks>
    /// Determinism is by construction. Material ids are a total order over a
    /// value key, and surfaces keep their emission order inside a group (the
    /// sort key is the pair (material id, emission position), which no two
    /// surfaces share), so the same cell always yields the same submeshes in
    /// the same order with the same bytes.
    /// <para>
    /// The uniform-material cell — every default brush world, and the
    /// overwhelming majority of authored cells — skips the split entirely and
    /// emits the exact array pair the pre-material builder did, which is what
    /// keeps the monolithic-equivalence and bit-identity oracles meaningful
    /// and costs a mixed cell nothing it would not have paid anyway.
    /// </para>
    /// <para>
    /// A group that emits no index (all of its surfaces degenerate below a
    /// triangle) produces no submesh: an empty GPU buffer plus its draw call
    /// would be pure overhead, and dropping it here keeps "a submesh always
    /// draws something" true for every consumer downstream.
    /// </para>
    /// </remarks>
    internal static ChunkSubmesh[] BuildSubmeshes(IReadOnlyList<Polygon> surfaces)
    {
        if (surfaces.Count == 0)
            return [];

        MaterialRef uniform = surfaces[0].Face.Material;
        bool isUniform = true;
        for (int i = 1; i < surfaces.Count; i++)
        {
            if (surfaces[i].Face.Material != uniform)
            {
                isUniform = false;
                break;
            }
        }

        if (isUniform)
        {
            (float[] vertices, uint[] indices) = CsgWorld.BuildMeshArrays(surfaces);
            return indices.Length == 0 ? [] : [new ChunkSubmesh(uniform, vertices, indices)];
        }

        // Mixed cell. Sort (material id, emission position) pairs packed into
        // one long — ascending by id, ties broken by the original position, so
        // a plain Array.Sort (which is not stable) still reproduces emission
        // order inside every group.
        var keys = new long[surfaces.Count];
        for (int i = 0; i < surfaces.Count; i++)
            keys[i] = ((long)surfaces[i].Face.Material.Id << 32) | (uint)i;
        Array.Sort(keys);

        var submeshes = new List<ChunkSubmesh>();
        var group = new List<Polygon>();
        int groupId = (int)(keys[0] >> 32);
        for (int k = 0; k <= keys.Length; k++)
        {
            // The k == Length pass closes the final group; its id can never
            // match a real one, hence the sentinel.
            int id = k < keys.Length ? (int)(keys[k] >> 32) : int.MinValue;
            if (id != groupId)
            {
                (float[] vertices, uint[] indices) = CsgWorld.BuildMeshArrays(group);
                if (indices.Length > 0)
                    submeshes.Add(new ChunkSubmesh(group[0].Face.Material, vertices, indices));
                if (k == keys.Length)
                    break;
                group.Clear();
                groupId = id;
            }
            group.Add(surfaces[(int)(keys[k] & 0xFFFFFFFFL)]);
        }

        return [.. submeshes];
    }
}
