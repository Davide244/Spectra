using System;

namespace SpectraEngine.Core.Maps.Compiled;

/// <summary>
/// Whole-file properties of a <c>.scmap</c>, in the header's <c>Flags</c> word.
/// </summary>
/// <remarks>
/// Each one says what the cook PUT IN the file, never what a runtime should do
/// with it. A flag that meant "re-carve on load" would be a policy stored in an
/// artifact, and the double-geometry hazard is exactly what happens when a loader
/// takes such a hint: baked chunks plus a live carve draws every wall twice, with
/// z-fighting that reads as a depth-precision bug rather than as a map loader
/// deciding something.
/// </remarks>
[Flags]
public enum ScmapFlags : uint
{
    /// <summary>Nothing optional was written.</summary>
    None = 0,

    /// <summary>
    /// A <c>BRSH</c> section is present: authored brush planes, kept so a brush
    /// can be re-carved at runtime.
    /// </summary>
    HasBrushSource = 1u << 0,

    /// <summary>A <c>LUAS</c> section is present: Luau source, which is the ground truth.</summary>
    HasScriptSource = 1u << 1,

    /// <summary>Debug information was kept rather than stripped.</summary>
    HasDebugInfo = 1u << 2,

    /// <summary>
    /// The file was laid out region-major for streaming. Reserved: the chunk grid
    /// is a compile partition rather than a residency one, and nothing sets this.
    /// </summary>
    Streamable = 1u << 3,
}

/// <summary>
/// Per-section properties, in a section-table record's <c>Flags</c> half-word.
/// </summary>
[Flags]
public enum ScmapSectionFlags : ushort
{
    /// <summary>Stored as written.</summary>
    None = 0,

    /// <summary>
    /// The section's bytes are compressed, so <c>Size</c> and
    /// <c>UncompressedSize</c> differ. Reserved and never set by this engine's
    /// cook: compression and a mapped zero-copy read are mutually exclusive, and
    /// geometry is where the bytes are.
    /// </summary>
    Compressed = 1 << 0,
}
