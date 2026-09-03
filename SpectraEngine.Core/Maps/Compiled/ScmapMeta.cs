using System.Runtime.InteropServices;

namespace SpectraEngine.Core.Maps.Compiled;

/// <summary>
/// The 48-byte fixed preamble of the <c>META</c> section: the scene's metadata
/// and the constants the compile was run with.
/// </summary>
/// <remarks>
/// <para><b>The three floats exist to be VALIDATED, not to be read.</b> A runtime
/// that chunks on a different cell size mis-routes every point and ray query
/// against a directory built for another lattice, and a runtime that welds on a
/// different grid meets hairline seams exactly where two cells touch. Neither
/// failure looks like a version problem: the first reads as sporadic collision
/// bugs and the second as a lighting artifact. So a load refuses on mismatch and
/// names both numbers, which is the same doctrine the format version follows and
/// for the same reason: a compiled map is a build output that can always be
/// regenerated.</para>
/// <para><b>The spawn array follows this preamble</b>, which is why the preamble
/// is padded to 48 rather than stopping at the 32 bytes its fields need: the array
/// then starts 16-byte aligned inside a section that is itself 16-byte aligned, so
/// it can be cast in place.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct ScmapMeta
{
    /// <summary>Index into <c>STRT</c> of the scene's name.</summary>
    public readonly uint SceneNameString;

    /// <summary>How many <see cref="ScmapSpawn"/> records follow this preamble.</summary>
    public readonly uint SpawnCount;

    /// <summary>The chunk cell size the world was compiled on. Must equal <c>ChunkCoord.CellSize</c>.</summary>
    public readonly float CellSize;

    /// <summary>The cross-cell weld band the compile used. Must equal <c>ChunkGrid.WeldBand</c>.</summary>
    public readonly float WeldBand;

    /// <summary>The vertex snap grid the compile used. Must equal <c>VertexSnapper.GridSize</c>.</summary>
    public readonly float SnapGrid;

    /// <summary>Cells per region edge. Zero: the region index is reserved and never written.</summary>
    public readonly uint RegionSize;

    /// <summary>Luau debug level the bytecode was compiled at. Zero until scripts are cooked.</summary>
    public readonly uint BytecodeDebugLevel;

    /// <summary>Cook switches that changed what was written. None defined in v1; written zero.</summary>
    public readonly uint CookFlags;

    /// <summary>Reserved; written zero.</summary>
    public readonly ulong Reserved0;

    /// <summary>Reserved; written zero.</summary>
    public readonly ulong Reserved1;

    /// <summary>
    /// Builds the preamble, stamping the compile constants from the engine that is
    /// doing the compiling.
    /// </summary>
    /// <remarks>
    /// The constants are taken rather than passed, because a cooker that could
    /// pass its own numbers could pass numbers the compile did not use, and the
    /// resulting file would pass its own validation and still be baked on the
    /// wrong lattice.
    /// </remarks>
    public ScmapMeta(uint sceneNameString, uint spawnCount, uint bytecodeDebugLevel = 0, uint cookFlags = 0)
    {
        SceneNameString = sceneNameString;
        SpawnCount = spawnCount;
        CellSize = ScmapFormat.EngineCellSize;
        WeldBand = ScmapFormat.EngineWeldBand;
        SnapGrid = ScmapFormat.EngineSnapGrid;
        RegionSize = 0;
        BytecodeDebugLevel = bytecodeDebugLevel;
        CookFlags = cookFlags;
        Reserved0 = 0;
        Reserved1 = 0;
    }
}
