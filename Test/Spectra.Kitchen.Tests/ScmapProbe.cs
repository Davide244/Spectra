using System;
using System.Collections.Generic;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Maps.Compiled;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// A compiled map read back into ordinary objects, so a test can assert about it
/// after the bytes have gone out of scope.
/// </summary>
/// <remarks>
/// <c>ScmapDocument</c> is a <c>ref struct</c> on purpose: a document provably
/// cannot outlive the mapping its spans point into, which is what stops an
/// unmapped view being read with no managed stack to blame. That same guarantee
/// keeps it out of a lambda, a field or a collection, which is most of what a test
/// wants to do with one, so the tests copy through here exactly once and assert
/// against the copy.
/// </remarks>
internal sealed class ScmapProbe
{
    public required ScmapHeader Header { get; init; }
    public required ScmapMeta Meta { get; init; }
    public required string SceneName { get; init; }
    public required List<string> Strings { get; init; }
    public required List<AssetRow> Assets { get; init; }
    public required List<ScmapNodeRecord> Nodes { get; init; }
    public required List<string> NodeNames { get; init; }
    public required List<ScmapChunkRecord> Chunks { get; init; }
    public required List<ScmapSpawn> Spawns { get; init; }
    public required int SkippedSections { get; init; }
    public required int InvalidDeclaredStates { get; init; }
    public required List<CellGeometry> Geometry { get; init; }
    public required bool HasBrushSource { get; init; }
    public required List<BrushCopy> Brushes { get; init; }

    /// <summary>
    /// Triangles across every cell that owns render geometry, copied from the
    /// document's own count rather than recomputed from the arrays above.
    /// </summary>
    /// <remarks>
    /// The double-geometry guard is graded on this number, so a second expression
    /// of it here would be a test measuring its own arithmetic rather than the
    /// reader's.
    /// </remarks>
    public required int TriangleCount { get; init; }

    /// <summary>One asset-table row, resolved through the string table.</summary>
    public readonly record struct AssetRow(PackEntryKind Kind, string Path, ulong ContentHash);

    /// <summary>One cell's baked geometry, copied out of the mapping.</summary>
    public sealed record CellGeometry(
        int X,
        int Y,
        int Z,
        List<SubmeshCopy> Submeshes,
        int BspNodeCount,
        int BspRootIndex,
        bool HasBsp);

    /// <summary>One submesh's arrays, copied.</summary>
    public sealed record SubmeshCopy(uint AssetIndex, float[] Vertices, uint[] Indices);

    /// <summary>One kept brush's planes and faces, copied.</summary>
    public sealed record BrushCopy(
        uint NodeIndex,
        System.Numerics.Plane[] Planes,
        ScmapFaceRecord[] Faces);

    public static ScmapProbe Read(ReadOnlySpan<byte> file, string source = "fixture.scmap")
    {
        ScmapDocument document = ScmapReader.Read(file, source);

        var strings = new List<string>(document.Strings.Count);
        for (int i = 0; i < document.Strings.Count; i++) strings.Add(document.Strings.GetString(i));

        var assets = new List<AssetRow>(document.Assets.Length);
        for (int i = 0; i < document.Assets.Length; i++)
        {
            assets.Add(new AssetRow(
                document.Assets[i].AssetKind,
                document.AssetPath(i),
                document.Assets[i].ContentHash));
        }

        var nodes = new List<ScmapNodeRecord>(document.Nodes.Length);
        var nodeNames = new List<string>(document.Nodes.Length);
        for (int i = 0; i < document.Nodes.Length; i++)
        {
            nodes.Add(document.Nodes[i]);
            nodeNames.Add(document.NodeName(i));
        }

        var chunks = new List<ScmapChunkRecord>(document.Chunks.Length);
        var geometry = new List<CellGeometry>(document.Chunks.Length);
        for (int i = 0; i < document.Chunks.Length; i++)
        {
            ScmapChunkRecord cell = document.Chunks[i];
            chunks.Add(cell);

            var submeshes = new List<SubmeshCopy>();
            if (cell.MeshSize != 0)
            {
                ScmapChunkMesh mesh = document.ChunkMesh(i);
                for (int s = 0; s < mesh.Submeshes.Length; s++)
                {
                    submeshes.Add(new SubmeshCopy(
                        mesh.Submeshes[s].AssetIndex,
                        mesh.Vertices(s).ToArray(),
                        mesh.Indices(s).ToArray()));
                }
            }

            int bspNodes = 0;
            int bspRoot = 0;
            if (cell.BspSize != 0)
            {
                ScmapChunkBsp bsp = document.ChunkBsp(i);
                bspNodes = bsp.Nodes.Length;
                bspRoot = bsp.RootIndex;
            }

            geometry.Add(new CellGeometry(
                cell.X, cell.Y, cell.Z, submeshes, bspNodes, bspRoot, cell.BspSize != 0));
        }

        var brushes = new List<BrushCopy>();
        if (document.HasBrushSource)
        {
            ScmapBrushSource kept = document.BrushSource();
            for (int i = 0; i < kept.Brushes.Length; i++)
            {
                ScmapBrushRecord record = kept.Brushes[i];
                brushes.Add(new BrushCopy(
                    record.NodeIndex,
                    kept.Planes.Slice((int)record.PlaneStart, (int)record.PlaneCount).ToArray(),
                    kept.Faces.Slice((int)record.PlaneStart, (int)record.PlaneCount).ToArray()));
            }
        }

        var spawns = new List<ScmapSpawn>(document.Spawns.Length);
        for (int i = 0; i < document.Spawns.Length; i++) spawns.Add(document.Spawns[i]);

        return new ScmapProbe
        {
            Header = document.Header,
            Meta = document.Meta,
            SceneName = document.SceneName,
            Strings = strings,
            Assets = assets,
            Nodes = nodes,
            NodeNames = nodeNames,
            Chunks = chunks,
            Spawns = spawns,
            SkippedSections = document.SkippedSectionCount,
            InvalidDeclaredStates = document.InvalidDeclaredStateCount,
            Geometry = geometry,
            HasBrushSource = document.HasBrushSource,
            Brushes = brushes,
            TriangleCount = document.TriangleCount,
        };
    }
}
