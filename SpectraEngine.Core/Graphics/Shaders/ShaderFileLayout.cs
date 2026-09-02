namespace SpectraEngine.Core.Graphics.Shaders;

/// <summary>
/// The fixed byte geometry of a .specshadecomp file, stated once.
/// </summary>
/// <remarks>
/// <b>Two expressions of one layout diverge.</b> The reader used to compute the
/// data section's start twice: from the stream position after the entry table in
/// one path, and from the literal <c>8 + count * 12</c> in the other. Those agree
/// exactly as long as the header never grows, and the header is planned to grow.
/// A divergence there is not an exception, it is a seek to the wrong offset
/// followed by a stage length read out of the middle of somebody else's bytes,
/// so the two paths take their arithmetic from here and there is nowhere for the
/// second copy to live.
/// <para>
/// The writer takes its header and entry buffer sizes from the same constants,
/// so a field added to either region without updating them is a buffer that no
/// longer fits what is written into it rather than a file the reader misparses.
/// </para>
/// </remarks>
internal static class ShaderFileLayout
{
    /// <summary>Magic, format version, stage flags and pipeline count.</summary>
    public const int HeaderSize = 8;

    /// <summary>One pipeline entry: backend, format, stages, reserved, offset, size.</summary>
    public const int EntrySize = 12;

    /// <summary>
    /// The fixed part of one vertex input record: location, span, component
    /// count, rate and the byte length of the name that follows it.
    /// </summary>
    public const int VertexInputRecordSize = 15;

    /// <summary>
    /// Where the data section begins, i.e. the origin every entry's
    /// <see cref="ShaderPipelineEntry.DataOffset"/> is measured from.
    /// </summary>
    public static long DataSectionStart(int pipelineCount) =>
        HeaderSize + (pipelineCount * (long)EntrySize);
}
