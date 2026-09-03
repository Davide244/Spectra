using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Assets.Sources;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Maps.Compiled;

namespace SpectraEngine.Core.Scene;

public sealed partial class Scene
{
    // Set once, at adoption, and cleared only by an explicit release. Everything
    // below reads it as "this world arrived baked", which is the one fact the
    // refusals need.
    private CompiledStaticWorld? _compiledStaticWorld;

    // Reported rather than logged where they happen: MarkStaticWorldDirty and the
    // three node-scoped marks carry no logger and never will - they are called
    // from a property setter on the hot editing path. So the refusal is counted
    // and named here, and ProcessStaticWorldCompilation, which runs every frame
    // and does have a logger, says it out loud when the number moves.
    private int _reportedRebuildRefusals;
    private int _reportedDirtyRefusals;

    /// <summary>
    /// The static world this scene ADOPTED from a compiled map, or null when the
    /// world is compiled live (or absent).
    /// </summary>
    /// <remarks>
    /// <para><b>Non-null is the whole guard.</b> While it is set, this scene's
    /// chunks already contain every world brush's baked surfaces, so a carve on
    /// top of them would draw every wall twice - and there is no exception, no log
    /// line and nothing on a debug layer, only z-fighting, which reads as a depth
    /// precision problem rather than as a map loader deciding something. So
    /// <see cref="RebuildStaticWorld"/>, <see cref="RebuildStaticWorldIfDirty"/>,
    /// <see cref="ProcessStaticWorldCompilation"/> and every automatic dirty mark
    /// refuse while it is set, and each refusal is counted and named.</para>
    /// <para><b><see cref="StaticWorld"/> is null whenever this is not.</b> The
    /// two are alternatives, never layers: one is the output of a compile that ran
    /// in this process and the other is a compile that ran in a cook, and a scene
    /// holding both would be a scene whose queries and whose picture could
    /// disagree.</para>
    /// </remarks>
    public CompiledStaticWorld? CompiledStaticWorld => _compiledStaticWorld;

    /// <summary>True when this scene's static world arrived baked.</summary>
    public bool HasCompiledStaticWorld => _compiledStaticWorld is not null;

    /// <summary>
    /// How many times a synchronous rebuild was refused because the world arrived
    /// baked.
    /// </summary>
    /// <remarks>
    /// <b>The measurement, not a statistic.</b> Zero is the number a correct load
    /// leaves behind; anything else names a caller that would have drawn the level
    /// twice.
    /// </remarks>
    public int RefusedStaticWorldRebuilds { get; private set; }

    /// <summary>
    /// How many automatic dirty marks were refused because the world arrived
    /// baked.
    /// </summary>
    /// <remarks>
    /// A brush edit against an adopted world is not a small mistake to absorb: the
    /// baked chunks cannot be patched, so honouring the mark would either recompile
    /// the whole level from source it does not have or leave the world describing
    /// an edit it never made.
    /// </remarks>
    public int RefusedStaticWorldDirtyMarks { get; private set; }

    /// <summary>The most recent refusal, in words, or null when nothing has been refused.</summary>
    public string? StaticWorldGuardMessage { get; private set; }

    /// <summary>
    /// Installs a compiled map's baked chunks as this scene's static world,
    /// creating one GPU mesh per (cell, material) straight from the mapped bytes
    /// and attaching each cell's flat BSP tree over the same mapping. Runs no CSG.
    /// Render thread only.
    /// </summary>
    /// <param name="renderer">Where the GPU meshes are created.</param>
    /// <param name="document">The compiled map, already validated by its reader.</param>
    /// <param name="assetMaterials">
    /// The <c>ASTB</c> remap: one entry per asset-table row, in table order, so a
    /// submesh's <c>AssetIndex</c> resolves to the material this PROCESS interned.
    /// A row that is not a material carries <see cref="MaterialRef.Default"/>.
    /// </param>
    /// <param name="file">
    /// The map's bytes. <b>The adopted world owns them from here.</b> The BSP
    /// nodes are a window into this blob, and on a mounted pack that window is a
    /// memory-mapped view whose unmapping under a live span is an access violation
    /// with no managed stack - so the reference that keeps the mapping alive rides
    /// with the world that reads it, and is released only by
    /// <see cref="ReleaseCompiledStaticWorld"/>.
    /// </param>
    /// <param name="report">Where per-cell counts are recorded.</param>
    /// <remarks>
    /// <b>Create every mesh before destroying anything</b>, the same atomicity
    /// stance every other swap in this class takes: a <c>CreateMesh</c> throw rolls
    /// the new meshes back and leaves whatever was rendering intact.
    /// </remarks>
    public CompiledStaticWorld AdoptCompiledStaticWorld(
        Renderer renderer,
        scoped in Maps.Compiled.ScmapDocument document,
        ReadOnlySpan<MaterialRef> assetMaterials,
        ContentBlob file,
        CompiledMapLoadReport report)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(report);

        // A background compile launched before the load may still be running. Wait
        // it out and drop its result: parked in _inFlightCompile it would be
        // harvested by a later frame and swapped in OVER the baked chunks, which is
        // the double-geometry hazard arriving by the back door. Its dirty cells are
        // deliberately NOT folded back the way a synchronous rebuild folds them -
        // there will be no further compile to cover them, and re-dirtying would
        // leave the scene permanently claiming an edit that can never be handled.
        if (_inFlightCompile is not null)
        {
            try { _inFlightCompile.Wait(); }
            catch (AggregateException) { /* superseded - its failure is irrelevant now */ }
            _inFlightCompile = null;
            _inFlightDirtyCells = null;
        }

        ReadOnlySpan<Maps.Compiled.ScmapChunkRecord> cells = document.Chunks;

        var replacement = new List<StaticWorldChunkMesh>(cells.Length);
        var compiled = new CompiledStaticWorldChunk[cells.Length];
        var created = new List<StaticWorldSubmesh[]>(cells.Length);
        int submeshCount = 0;
        int triangles = 0;
        int trees = 0;

        try
        {
            for (int i = 0; i < cells.Length; i++)
            {
                Maps.Compiled.ScmapChunkRecord cell = cells[i];
                var coord = new ChunkCoord(cell.X, cell.Y, cell.Z);
                var bounds = new Aabb(cell.BoundsMin, cell.BoundsMax);

                int cellTriangles = 0;
                if (cell.MeshSize != 0)
                {
                    Maps.Compiled.ScmapChunkMesh mesh = document.ChunkMesh(i);
                    var submeshes = new StaticWorldSubmesh[mesh.Submeshes.Length];

                    for (int s = 0; s < submeshes.Length; s++)
                    {
                        Maps.Compiled.ScmapSubmeshEntry entry = mesh.Submeshes[s];

                        // The remap, and the reason it exists: AssetIndex is a row
                        // of this FILE's table and a MaterialRef.Id is this
                        // PROCESS's interning order, and the two agree only by
                        // coincidence. A submesh naming no row wears the default,
                        // which is what Scene.StaticWorldMaterial already answers
                        // for.
                        MaterialRef material = entry.NamesAsset && entry.AssetIndex < (uint)assetMaterials.Length
                            ? assetMaterials[(int)entry.AssetIndex]
                            : MaterialRef.Default;

                        // Straight from the mapping: no copy, no CPU shadow. A
                        // chunk is culled by the bounds above and queried through
                        // the tree below, so a retained mirror would be a second
                        // copy of all the world's geometry that nothing reads.
                        Mesh gpuMesh = renderer.CreateMesh(
                            mesh.Vertices(s), mesh.Indices(s), VertexAttribute.StandardLayout,
                            MeshCpuAccess.None);

                        submeshes[s] = new StaticWorldSubmesh(
                            material, gpuMesh, ResolveWorldMaterial(material));

                        cellTriangles += (int)(entry.IndexCount / 3);
                    }

                    created.Add(submeshes);
                    submeshCount += submeshes.Length;
                    replacement.Add(new StaticWorldChunkMesh(coord, bounds, Artifact: null, submeshes));
                }

                triangles += cellTriangles;

                FlatBspTree? tree = null;
                if (cell.BspSize != 0)
                {
                    Maps.Compiled.ScmapChunkBsp bsp = document.ChunkBsp(i);

                    // Over the mapping, never rehydrated: a 50k-part world's
                    // per-cell trees would be tens of thousands of GC objects to
                    // allocate and chase, which is the entire cost the flat form
                    // exists to remove.
                    var nodes = new MappedBspNodes(
                        file,
                        document.ChunkBspBlobFileOffset + (int)cell.BspOffset
                            + Maps.Compiled.ScmapFormat.ChunkBspHeaderSize,
                        bsp.Nodes.Length);

                    tree = new FlatBspTree(nodes.Memory, bsp.RootIndex);
                    trees++;
                }

                compiled[i] = new CompiledStaticWorldChunk(coord, bounds, tree, cellTriangles);
            }
        }
        catch
        {
            foreach (StaticWorldSubmesh[] submeshes in created)
                DestroyChunkSubmeshes(renderer, submeshes);
            throw;
        }

        // Commit. Everything the scene was drawing goes, whichever lane produced
        // it: a compiled world REPLACES a static world rather than layering over
        // one, which is the same statement the guard makes from the other side.
        foreach (KeyValuePair<ChunkCoord, StaticWorldChunkMesh> stale in _staticWorldChunkMeshes)
            DestroyChunkSubmeshes(renderer, stale.Value.Submeshes);

        _staticWorldChunkMeshes.Clear();
        _staticWorldChunkList.Clear();
        foreach (StaticWorldChunkMesh chunk in replacement)
        {
            _staticWorldChunkMeshes.Add(chunk.Coord, chunk);
            _staticWorldChunkList.Add(chunk);
        }

        // Z-order, exactly as the compiled swap sorts: the cluster boxes below
        // only reject anything because a run of consecutive entries is a compact
        // block of space rather than a line.
        _staticWorldChunkList.Sort(static (a, b) => a.Coord.MortonKey.CompareTo(b.Coord.MortonKey));
        RebuildChunkClusters();

        StaticWorld = null;
        _staticWorldCarry = null;

        // Marked handled, because the graph replacement that precedes every load
        // legitimately dirties on its way OUT of whatever was there before, and
        // there is no compile that could ever handle it now. Left unmarked, a
        // scene would report StaticWorldDirty forever while every pump refused.
        _handledStaticWorldVersion = _staticWorldVersion;
        _compiledStaticWorld?.Dispose();
        _compiledStaticWorld = new CompiledStaticWorld(document.Source, compiled, file);

        report.ChunksLoaded = replacement.Count;
        report.SubmeshesUploaded = submeshCount;
        report.TriangleCount = triangles;
        report.BspChunksLoaded = trees;

        return _compiledStaticWorld;
    }

    /// <summary>
    /// Drops an adopted compiled world: destroys its GPU meshes and releases the
    /// map's bytes, leaving the scene with no static world and the guard lifted.
    /// Render thread only.
    /// </summary>
    /// <remarks>
    /// <b>Call this before replacing the graph from an authored map.</b> A scene
    /// that loads a <c>.smap</c> over an adopted world would otherwise find every
    /// rebuild refused, which reads as an editor that stopped responding to edits
    /// - the same silent inertness a latched suspension once caused. Doing it
    /// automatically inside the graph replacement was rejected: releasing a
    /// mapping is a lifetime decision and the one caller who wants it should be
    /// the one who says so.
    /// </remarks>
    public void ReleaseCompiledStaticWorld(Renderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);

        if (_compiledStaticWorld is null) return;

        foreach (KeyValuePair<ChunkCoord, StaticWorldChunkMesh> chunk in _staticWorldChunkMeshes)
            DestroyChunkSubmeshes(renderer, chunk.Value.Submeshes);

        _staticWorldChunkMeshes.Clear();
        _staticWorldChunkList.Clear();
        RebuildChunkClusters();

        _compiledStaticWorld.Dispose();
        _compiledStaticWorld = null;
    }

    // Refuses one call, names it, and counts it. Returns true when the caller must
    // stand down, so every guarded site reads the same way.
    private bool RefuseForCompiledWorld(string what, bool isRebuild)
    {
        if (_compiledStaticWorld is null) return false;

        if (isRebuild) RefusedStaticWorldRebuilds++;
        else RefusedStaticWorldDirtyMarks++;

        StaticWorldGuardMessage =
            $"{what} was refused: '{_compiledStaticWorld.Source}' arrived baked, and its chunks already hold " +
            "every world brush's surfaces. Carving them again draws every wall twice, which reads as depth " +
            "precision rather than as a map loader. Call ReleaseCompiledStaticWorld first if this scene is " +
            "meant to be authored.";

        return true;
    }

    // Said out loud from the one static-world method that has a logger, and only
    // when the numbers have moved: the marks that raise these run from property
    // setters on the editing hot path, so logging at the site would be a line per
    // frame for as long as somebody held a brush.
    private void ReportCompiledWorldGuard(ILogger logger)
    {
        if (RefusedStaticWorldRebuilds == _reportedRebuildRefusals
            && RefusedStaticWorldDirtyMarks == _reportedDirtyRefusals)
        {
            return;
        }

        _reportedRebuildRefusals = RefusedStaticWorldRebuilds;
        _reportedDirtyRefusals = RefusedStaticWorldDirtyMarks;

        logger.LogWarning(
            "Static world guard: {Rebuilds} rebuild(s) and {Marks} dirty mark(s) refused on the compiled map " +
            "'{Source}'. {Why}",
            RefusedStaticWorldRebuilds, RefusedStaticWorldDirtyMarks,
            _compiledStaticWorld?.Source ?? "(released)", StaticWorldGuardMessage);
    }
}
