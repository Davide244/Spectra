using System;
using System.Collections.Generic;
using System.Numerics;
using Spectra.Kitchen.Diagnostics;
using SpectraEngine.Core;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Maps;
using SpectraEngine.Core.Maps.Compiled;
using SpectraEngine.Core.Scene;

namespace Spectra.Kitchen.Maps;

/// <summary>
/// Turns an authored map into a compiled one: bind, compile, bake.
/// </summary>
/// <remarks>
/// <para><b>The compile is CACHE-FREE, deliberately.</b>
/// <c>CsgWorld.Build(placements)</c> is the overload that carries no previous
/// world, no carve cache and no dirty-cell set: the incremental compiler exists to
/// make an EDIT cheap by carrying state across compiles, and a bake must be a pure
/// function of its source. Handing it a cache would make a cooked map depend on
/// what the cooking process had compiled before it, which is the same class of
/// leak the string table and the cook scheduler already refuse.</para>
/// <para><b>The placements come from the SCENE, through the engine's own
/// capture.</b> Traversal order is placement order is the order the carve breaks
/// its overlap ties in, so a cook that walked the graph itself would be a second
/// expression of that list and the two would drift exactly where nothing fails.
/// <c>Scene.CaptureStaticWorldPlacements</c> is the same walk a synchronous
/// rebuild performs, rigidity refusal included, which is what makes the bake oracle
/// - baked arrays element-identical to a fresh compile of the same source - a
/// statement about one function rather than about two that agree today.</para>
/// <para><b>The document and the scene are walked TOGETHER, in lockstep.</b> The
/// binder adds nodes in document order and recurses children in order, so the two
/// pre-orders are the same sequence; walking both at once is what lets a node
/// record carry facts that live only in the document (a model path, a per-brush
/// <c>keepSource</c>) beside geometry that lives only in the scene (planes the
/// <c>Brush</c> constructor has normalised). The walk asserts the ids match at
/// every step, because a divergence would otherwise attribute one node's brush to
/// another node's transform, silently.</para>
/// <para><b>A material becomes an asset index in ONE place</b>, and never a
/// <c>MaterialRef.Id</c>. Ids are per-process interning order, so a cook that wrote
/// one produces a file that loads perfectly in the test that wrote it and
/// mis-textures the entire world the moment a second map interns first. Rows are
/// claimed in the node walk's own order, which makes the table a pure function of
/// the map rather than of when the bake happened to look something up.</para>
/// </remarks>
public static class ScmapBake
{
    /// <summary>
    /// Bakes <paramref name="document"/>, or reports why it cannot be baked and
    /// returns null.
    /// </summary>
    /// <param name="document">The authored map.</param>
    /// <param name="sourceMapDigest">The bundle's digest, stamped into the header.</param>
    /// <param name="keepBrushSource">
    /// Whether the cook was asked to keep every brush's authored planes. Part
    /// brushes are kept whatever this says: their planes live nowhere else.
    /// </param>
    /// <param name="report">Where a diagnostic goes. Called on the cooking thread.</param>
    /// <param name="sourcePath">The map's content path, for a message.</param>
    public static byte[]? Bake(
        MapDocument document,
        UInt128 sourceMapDigest,
        bool keepBrushSource,
        Action<CookDiagnostic> report,
        string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(report);

        var scene = new SpectraEngine.Core.Scene.Scene(document.Scene.Name);

        // No report and no asset manager: a bake resolves no models and needs no
        // GPU, and a report here would name every mesh node in the level as
        // unresolved on every cook. What a mesh node carries is read from the
        // DOCUMENT below instead, which is where it was authored.
        MapSceneBinder.ApplyTo(document, scene);

        IReadOnlyList<BrushPlacement>? placements =
            scene.CaptureStaticWorldPlacements(out string? defect);

        if (placements is null)
        {
            report(CookDiagnostic.Error(CookDiagnosticCodes.MapBrushNonRigid, defect!, sourcePath));
            return null;
        }

        // Cache-free. See the class remarks: a bake is a pure function of its
        // source, and the incremental overloads exist to make an edit cheap.
        CsgWorld world = CsgWorld.Build(placements);

        var builder = new ScmapBuilder(document.Scene.Name);
        var assets = new AssetTable(builder);

        if (!WriteNodes(document, scene, builder, assets, keepBrushSource, report, sourcePath)) return null;

        WriteChunks(world, builder, assets);

        // Informational in the file: a load never gates on it, because the authored
        // map is not present at runtime and there is nothing a runtime could do
        // about which grammar the bake read.
        return builder.Build(sourceMapDigest, (uint)document.FormatVersion);
    }

    // The lockstep walk. Everything a node record needs is gathered here, in one
    // pre-order pass, so there is one traversal order in this file and nothing to
    // keep in step with anything else.
    private static bool WriteNodes(
        MapDocument document,
        SpectraEngine.Core.Scene.Scene scene,
        ScmapBuilder builder,
        AssetTable assets,
        bool keepBrushSource,
        Action<CookDiagnostic> report,
        string sourcePath)
    {
        var seen = new HashSet<Guid>();
        var pending = new Stack<(MapNode Mapped, SceneNode Node, int Parent)>();

        // Pushed in reverse so the first child is popped first: the record order is
        // pre-order, and pre-order is what makes ParentIndex < SelfIndex hold and a
        // one-pass loader legal.
        PushChildren(pending, document.Nodes, scene.Root, -1);

        while (pending.Count > 0)
        {
            (MapNode mapped, SceneNode node, int parent) = pending.Pop();

            if (mapped.Id != node.Id)
            {
                // Unreachable while the binder builds the graph in document order,
                // and checked anyway: a divergence attributes one node's brush to
                // another node's transform, and the level merely renders wrongly.
                throw new InvalidOperationException(
                    $"'{sourcePath}' node '{mapped.Name}' bound to scene node '{node.Name}' with a different " +
                    "id. The compiled map's node records would carry one node's payload under another's " +
                    "transform.");
            }

            if (!seen.Add(mapped.Id))
            {
                report(CookDiagnostic.Error(
                    CookDiagnosticCodes.MapNodeIdDuplicate,
                    $"Two nodes in '{sourcePath}' claim id {mapped.Id}. Every command, every wire and every " +
                    "script reference resolves through that id, so a duplicate resolves by traversal order and " +
                    "presents as an edit landing on the wrong object.",
                    sourcePath));

                return false;
            }

            if (mapped.Brush is { } authored && authored.Planes.Count != authored.Faces.Count)
            {
                report(CookDiagnostic.Error(
                    CookDiagnosticCodes.MapFaceCountMismatch,
                    $"Node '{mapped.Name}' in '{sourcePath}' has {authored.Planes.Count} brush planes and " +
                    $"{authored.Faces.Count} faces. One face per plane is the invariant the whole per-face " +
                    "material path rests on, so a mismatch is an indexing bug rather than a wrong surface.",
                    sourcePath));

                return false;
            }

            int index = builder.AddNode(new ScmapNodeSource(
                node.Id,
                node.Name,
                parent,
                node.LocalTransform,
                PayloadKindOf(node, mapped),
                PayloadFlagsOf(node),
                PayloadIndex: PayloadIndexOf(mapped, assets)));

            if (node.Brush is { } brush && KeepsSource(node, mapped, keepBrushSource))
                builder.AddBrushSource(BrushSourceOf(index, brush, assets));
            else if (node.Brush is { } unkept)
                ClaimFaceMaterials(unkept, assets);

            PushChildren(pending, mapped.Children, node, index);
        }

        return true;
    }

    private static void PushChildren(
        Stack<(MapNode, SceneNode, int)> pending, List<MapNode> mapped, SceneNode node, int parent)
    {
        if (mapped.Count != node.Children.Count)
        {
            throw new InvalidOperationException(
                $"Node '{node.Name}' has {node.Children.Count} scene children and {mapped.Count} document " +
                "children. The bake walks both graphs in lockstep, and a shape difference would silently " +
                "misalign every record after it.");
        }

        for (int i = mapped.Count - 1; i >= 0; i--)
            pending.Push((mapped[i], node.Children[i], parent));
    }

    private static ScmapPayloadKind PayloadKindOf(SceneNode node, MapNode mapped)
    {
        if (node.Brush is not null)
        {
            // The engine's own admission answer, and the ONLY place it is turned
            // into the cooked flag. StaticWorldBrush is BakedIntoChunks; the two
            // names mean almost opposite things and neither may take the other's
            // spelling.
            return node.IsStaticWorldBrush ? ScmapPayloadKind.StaticWorldBrush : ScmapPayloadKind.PartBrush;
        }

        return mapped.Mesh is not null ? ScmapPayloadKind.MeshInstance : ScmapPayloadKind.None;
    }

    private static ScmapPayloadFlags PayloadFlagsOf(SceneNode node) =>
        node.Brush is { Operation: BrushOperation.Subtractive }
            ? ScmapPayloadFlags.SubtractiveBrush
            : ScmapPayloadFlags.None;

    // A mesh instance names its model through the asset table, which is the table
    // its payload kind names. A brush's PayloadIndex stays zero: the link from a
    // brush to its node runs the other way, through the BRSH record's own
    // nodeIndex, and two directions is two things to keep in step.
    private static uint PayloadIndexOf(MapNode mapped, AssetTable assets) =>
        mapped is { Brush: null, Mesh: { } mesh } && mesh.Model.Length > 0
            ? assets.Model(mesh.Model)
            : 0;

    // A part brush is kept whatever the cook was asked for: its planes live nowhere
    // else, so a map that dropped them would ship a level whose parts are invisible
    // with nothing reporting it. A world brush is kept when the cook or the brush
    // itself asks, and it is exactly the one that must never be carved again.
    private static bool KeepsSource(SceneNode node, MapNode mapped, bool keepBrushSource) =>
        !node.IsStaticWorldBrush || keepBrushSource || mapped.Brush is { KeepSource: true };

    private static ScmapBrushSourceEntry BrushSourceOf(int nodeIndex, Brush brush, AssetTable assets)
    {
        var planes = new Plane[brush.LocalPlanes.Count];
        var faces = new ScmapFaceSource[brush.LocalPlanes.Count];

        for (int i = 0; i < planes.Length; i++)
        {
            planes[i] = brush.LocalPlanes[i];

            FaceSurface face = brush.FaceSurfaces[i];
            faces[i] = new ScmapFaceSource(
                assets.Material(face.Material),
                face.UAxis,
                face.VAxis,
                face.UOffset,
                face.VOffset,
                face.UScale,
                face.VScale);
        }

        return new ScmapBrushSourceEntry(nodeIndex, planes, faces);
    }

    // A brush whose source is not kept still claims its face materials, so the
    // asset table is a function of the MAP rather than of which brushes a
    // particular cook happened to keep. Without it, --keep-brush-source would
    // renumber every row and a chunk submesh baked from the same surfaces would
    // point somewhere else.
    private static void ClaimFaceMaterials(Brush brush, AssetTable assets)
    {
        for (int i = 0; i < brush.FaceSurfaces.Count; i++) assets.Material(brush.FaceSurfaces[i].Material);
    }

    private static void WriteChunks(CsgWorld world, ScmapBuilder builder, AssetTable assets)
    {
        var meshes = new Dictionary<ChunkCoord, ChunkMesh>(world.ChunkMeshes.Count);
        for (int i = 0; i < world.ChunkMeshes.Count; i++)
            meshes[world.ChunkMeshes[i].Coord] = world.ChunkMeshes[i];

        // OrderedChunks is sorted by ChunkCoord.CompareTo, which is the directory's
        // own canonical order; the dictionary above is a lookup and is never
        // enumerated, because a dictionary's iteration order would leak the runtime
        // string hash seed's cousin into the file.
        IReadOnlyList<WorldChunk> cells = world.Chunks.OrderedChunks;
        for (int i = 0; i < cells.Count; i++)
        {
            WorldChunk cell = cells[i];
            meshes.TryGetValue(cell.Coord, out ChunkMesh? mesh);

            FlatBspNode[]? nodes = null;
            int root = FlatBspNode.EmptyLeaf;
            if (cell.Bsp is { } tree) nodes = BspFlattener.Flatten(tree, out root);

            builder.AddChunk(new ScmapChunkSource(
                cell.Coord,

                // The cell's TRUE render bounds where there is geometry to bound.
                // A cell with no mesh is never culled - its directory entry says
                // MeshSize zero - so its box is the cell cube rather than a
                // fabricated one, which is finite, deterministic and obviously not
                // a claim about anything drawn.
                mesh?.RenderBounds ?? cell.Coord.Bounds,
                SubmeshesOf(mesh, assets),
                nodes,
                root));
        }
    }

    // The submesh sort, and the whole reason the file's order is not the compile's.
    // A ChunkMesh is in ascending MATERIAL ID, which is per-process interning order;
    // the file is in ascending ASSET INDEX, which is a total order over a value key
    // the map itself decides. Two compiles of one cell therefore emit the same
    // submeshes in the same order however the process that compiled them had
    // interned its materials.
    private static ScmapSubmeshSource[]? SubmeshesOf(ChunkMesh? mesh, AssetTable assets)
    {
        if (mesh is null || mesh.Submeshes.Count == 0) return null;

        var submeshes = new ScmapSubmeshSource[mesh.Submeshes.Count];
        for (int i = 0; i < submeshes.Length; i++)
        {
            ChunkSubmesh submesh = mesh.Submeshes[i];
            submeshes[i] = new ScmapSubmeshSource(
                assets.Material(submesh.Material), submesh.Vertices, submesh.Indices);
        }

        Array.Sort(submeshes, static (a, b) => a.AssetIndex.CompareTo(b.AssetIndex));
        return submeshes;
    }

    /// <summary>
    /// The one route from a runtime reference to an <c>ASTB</c> row.
    /// </summary>
    /// <remarks>
    /// <b>Nothing else may put a row in the asset table.</b> A material arrives as
    /// a <c>MaterialRef</c>, whose id is per-process interning order, and it leaves
    /// as a row index the file itself decides; the lookup between them is keyed on
    /// the id and is never enumerated. A row claimed twice is the same row, which
    /// is what makes the index a function of first reference in the node walk.
    /// </remarks>
    private sealed class AssetTable(ScmapBuilder builder)
    {
        private readonly Dictionary<int, uint> _materials = [];

        public uint Material(MaterialRef material)
        {
            // The default material names no path, so it has no row. The file says
            // so with a sentinel rather than with row 0, which is a real asset:
            // the engine's answer to a face that names no material is already
            // Scene.StaticWorldMaterial.
            if (material.IsDefault) return ScmapFormat.NoAssetIndex;

            if (_materials.TryGetValue(material.Id, out uint existing)) return existing;

            uint index = builder.AddMaterial(material);
            _materials[material.Id] = index;
            return index;
        }

        public uint Model(string path) =>
            builder.AddAsset(new ScmapAssetSource(PackEntryKind.Model, path));
    }
}
