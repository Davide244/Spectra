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

    /// <summary>One asset-table row, resolved through the string table.</summary>
    public readonly record struct AssetRow(PackEntryKind Kind, string Path, ulong ContentHash);

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
        for (int i = 0; i < document.Chunks.Length; i++) chunks.Add(document.Chunks[i]);

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
        };
    }
}
