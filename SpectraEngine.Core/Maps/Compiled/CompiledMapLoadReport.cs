using System;
using System.Collections.Generic;

namespace SpectraEngine.Core.Maps.Compiled;

/// <summary>
/// What a compiled map load could not bring across, and what this build's reader
/// structurally cannot carry at all.
/// </summary>
/// <remarks>
/// <para><b>Two different kinds of miss, kept apart on purpose.</b> A PER-NODE
/// miss is a fact about this file - a mesh instance whose model row is absent, a
/// part brush whose planes are not in <c>BRSH</c> - and naming the node is what
/// makes it fixable. A FORMAT gap is a fact about the BUILD: <c>.scmap</c> v1 has
/// no light table, so a lamp's node arrives and its lamp does not, and no amount
/// of looking at the file can tell you whether the author put one there. Reporting
/// the second as though it were the first would name the wrong thing; not
/// reporting it at all is how a level quietly loses its lights.</para>
/// <para><b>The format gaps are a constant, and they are printed on every
/// load.</b> That is the same posture the entity catalogue takes when it prints
/// its class count on every run: the failure being guarded against is a shipped
/// build that silently does less than the last one, and a line nobody reads is
/// still a line somebody can be pointed at.</para>
/// </remarks>
public sealed class CompiledMapLoadReport
{
    private readonly List<string> _unboundMeshInstances = [];
    private readonly List<string> _partBrushesWithoutSource = [];
    private readonly List<string> _brushesRefused = [];

    /// <summary>
    /// What a <c>.scmap</c> at this format version cannot carry, whatever is in
    /// the file.
    /// </summary>
    /// <remarks>
    /// Each entry is a gap <c>docs/formats-and-pipeline.md</c> 2.7 names, in the
    /// order that document names them. They are append-only in spirit: an entry
    /// leaves this list on the day the section that closes it lands, and never
    /// because a load happened to look complete.
    /// </remarks>
    public static IReadOnlyList<string> FormatGaps { get; } =
    [
        "lights (no ScmapPayloadKind value and no light table, so a lamp's node arrives without its lamp)",
        "spawns (scene.spawn is a preserved .smap member, so META always writes a spawn count of zero)",
        "entities and their connections (ENTT/ECON are claimed and empty)",
        "scripts (SCPT/LUAB/LUAS are claimed and empty)",
        "mesh-instance submesh indices (MeshSource.SubmeshIndex has no table to name)",
        "standalone brush transforms (BRSH carries planes and faces; a node-attached brush ignores it)",
        "static-world collision (a baked world has no placement list, so the character mover has no plane " +
            "sets: a compiled map is a level you can look at, not one you can walk in)",
    ];

    /// <summary>Nodes that named a mesh this loader did not attach.</summary>
    public IReadOnlyList<string> UnboundMeshInstances => _unboundMeshInstances;

    /// <summary>Part brushes whose planes were not in <c>BRSH</c>, so they draw nothing.</summary>
    public IReadOnlyList<string> PartBrushesWithoutSource => _partBrushesWithoutSource;

    /// <summary>Brushes whose planes this engine's <c>Brush</c> refused to build.</summary>
    public IReadOnlyList<string> BrushesRefused => _brushesRefused;

    /// <summary>Nodes rebuilt from the file.</summary>
    public int NodesLoaded { get; internal set; }

    /// <summary>Asset-table rows interned into this process's material registry.</summary>
    public int MaterialsInterned { get; internal set; }

    /// <summary>Cells whose baked geometry became GPU meshes.</summary>
    public int ChunksLoaded { get; internal set; }

    /// <summary>GPU meshes created, one per (cell, material).</summary>
    public int SubmeshesUploaded { get; internal set; }

    /// <summary>Triangles the load put on the GPU.</summary>
    public int TriangleCount { get; internal set; }

    /// <summary>Cells that carry a queryable flat BSP tree.</summary>
    public int BspChunksLoaded { get; internal set; }

    /// <summary>Section records this build stepped over because it did not know the code.</summary>
    public int SkippedSections { get; internal set; }

    /// <summary>
    /// Baked world brushes whose authored planes were in <c>BRSH</c> and were
    /// deliberately NOT rebuilt.
    /// </summary>
    /// <remarks>
    /// <b>The double-geometry guard, counted.</b> A <c>--keep-brush-source</c> cook
    /// puts a world brush's planes in the file as well as its surfaces in the
    /// chunks, and a loader that helpfully rebuilt one would draw that brush twice
    /// with nothing reporting it. Non-zero here means the file offered geometry
    /// this load correctly declined; zero means it offered none.
    /// </remarks>
    public int BakedBrushSourcesSkipped { get; private set; }

    /// <summary>Whether nothing in THIS FILE was lost. Says nothing about <see cref="FormatGaps"/>.</summary>
    public bool IsComplete =>
        _unboundMeshInstances.Count == 0 && _partBrushesWithoutSource.Count == 0 && _brushesRefused.Count == 0;

    /// <summary>One sentence naming what this file lost, or null when nothing was.</summary>
    public string? Describe()
    {
        if (IsComplete) return null;

        var parts = new List<string>(3);
        if (_unboundMeshInstances.Count > 0)
            parts.Add($"{_unboundMeshInstances.Count} mesh instance(s) unbound ({Join(_unboundMeshInstances)})");
        if (_partBrushesWithoutSource.Count > 0)
            parts.Add($"{_partBrushesWithoutSource.Count} part brush(es) with no planes ({Join(_partBrushesWithoutSource)})");
        if (_brushesRefused.Count > 0)
            parts.Add($"{_brushesRefused.Count} brush(es) refused ({Join(_brushesRefused)})");

        return string.Join("; ", parts) + ".";
    }

    /// <summary>One sentence naming what this BUILD cannot carry, whatever the file holds.</summary>
    public static string DescribeFormatGaps() =>
        $".scmap v{EngineInfo.CompiledMapFormatVersion} carries no " + string.Join("; no ", FormatGaps) + ".";

    internal void BakedBrushSourceSkipped() => BakedBrushSourcesSkipped++;

    internal void MeshInstanceUnbound(string node) => _unboundMeshInstances.Add(node);

    internal void PartBrushWithoutSource(string node) => _partBrushesWithoutSource.Add(node);

    internal void BrushRefused(string node) => _brushesRefused.Add(node);

    // Bounded, because a broken map can name every node in the level and a log
    // line that scrolls a level's worth of names past somebody is a line nobody
    // reads to the end of.
    private static string Join(List<string> names) =>
        names.Count <= 5
            ? string.Join(", ", names)
            : string.Join(", ", names.GetRange(0, 5)) + $", and {names.Count - 5} more";
}
