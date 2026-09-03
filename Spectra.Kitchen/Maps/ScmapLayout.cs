using System;
using System.Collections.Generic;
using SpectraEngine.Core.Maps.Compiled;

namespace Spectra.Kitchen.Maps;

/// <summary>One section's four-character code and the size it claims its body will be.</summary>
/// <param name="Kind">The section's four-character code.</param>
/// <param name="BodySize">Bytes the body occupies, padding excluded.</param>
public readonly record struct ScmapSectionSize(uint Kind, long BodySize);

/// <summary>
/// Where every section of a <c>.scmap</c> lands, computed whole before a byte is
/// written.
/// </summary>
/// <remarks>
/// <para><b>This class exists to make one named hazard structurally impossible
/// rather than merely tested for.</b> The hazard is a blob landing at a
/// non-16-aligned offset because the layout pass and the write pass disagreed
/// about a size including its padding: a one-byte disagreement puts every later
/// section somewhere other than where the table says it is, and the symptom is
/// arbitrary. It is not an exception, because a section table full of plausible
/// offsets parses; it is a chunk mesh read out of the middle of the string blob.</para>
/// <para><b>The defence is that exactly ONE function knows what a section costs</b>
/// (<see cref="PaddedSectionSize"/>), and both passes call it: the layout pass to
/// place the next section, and the write pass to know how many zero bytes to put
/// after the body. There is no second expression of the arithmetic anywhere, so
/// there is nothing to fall out of step.</para>
/// <para><b>The defence is not sufficient on its own, which is why the writer
/// asserts.</b> The layout is computed from DECLARED sizes and the bodies are
/// written by their producers, and those two are separate statements: a producer
/// that declares one length and emits another is exactly the way a two-pass writer
/// goes wrong, and it is the shape the compiled map needs, because a chunk mesh
/// blob wants to be streamed rather than materialised whole. So
/// <c>ScmapWriter</c> checks the stream's position against this layout at every
/// section boundary and refuses, naming the section that disagreed, rather than
/// producing a file that reads back as different numbers than it was written
/// from.</para>
/// </remarks>
public sealed class ScmapLayout
{
    private readonly uint[] _kinds;
    private readonly long[] _bodySizes;
    private readonly long[] _offsets;

    private ScmapLayout(uint[] kinds, long[] bodySizes, long[] offsets, long totalSize)
    {
        _kinds = kinds;
        _bodySizes = bodySizes;
        _offsets = offsets;
        TotalSize = totalSize;
    }

    /// <summary>
    /// What one section occupies in the file: its body plus the zero bytes that
    /// carry the next section to a 16-byte boundary.
    /// </summary>
    /// <remarks>
    /// The one function. Everything that needs to know a section's cost, in either
    /// pass, calls this.
    /// </remarks>
    public static long PaddedSectionSize(long bodySize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bodySize);
        return ScmapFormat.AlignUp(bodySize, ScmapFormat.PayloadAlignment);
    }

    /// <summary>
    /// Places every section, in the order given, after the header and the section
    /// table.
    /// </summary>
    /// <remarks>
    /// Order is the caller's and is preserved exactly: a compiled map's section
    /// order is part of its byte identity, so a layout that sorted would make the
    /// file a function of a comparison rather than of what the cook emitted.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A section code appears twice.</exception>
    public static ScmapLayout Compute(IReadOnlyList<ScmapSectionSize> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);

        var kinds = new uint[sections.Count];
        var bodySizes = new long[sections.Count];
        var offsets = new long[sections.Count];

        long cursor = ScmapFormat.AlignUp(
            ScmapFormat.SectionTableOffset + ((long)sections.Count * ScmapFormat.SectionSize),
            ScmapFormat.PayloadAlignment);

        for (int i = 0; i < sections.Count; i++)
        {
            ScmapSectionSize section = sections[i];
            ArgumentOutOfRangeException.ThrowIfNegative(section.BodySize);

            for (int j = 0; j < i; j++)
            {
                if (kinds[j] != section.Kind) continue;

                throw new InvalidOperationException(
                    $"Section '{ScmapFormat.DescribeFourCc(section.Kind)}' was placed twice. A section names " +
                    "one region of the file, so a reader would have to choose, and choosing silently is how " +
                    "half a map comes from one copy and half from the other.");
            }

            kinds[i] = section.Kind;
            bodySizes[i] = section.BodySize;
            offsets[i] = cursor;
            cursor += PaddedSectionSize(section.BodySize);
        }

        return new ScmapLayout(kinds, bodySizes, offsets, cursor);
    }

    /// <summary>How many sections this layout places.</summary>
    public int Count => _kinds.Length;

    /// <summary>Total bytes in the file, the last section's padding included.</summary>
    public long TotalSize { get; }

    /// <summary>The four-character code of section <paramref name="index"/>.</summary>
    public uint KindAt(int index) => _kinds[index];

    /// <summary>The declared body size of section <paramref name="index"/>, padding excluded.</summary>
    public long BodySizeAt(int index) => _bodySizes[index];

    /// <summary>The absolute offset of section <paramref name="index"/>. Always 16-byte aligned.</summary>
    public long OffsetAt(int index) => _offsets[index];

    /// <summary>What section <paramref name="index"/> occupies, its padding included.</summary>
    public long PaddedSizeAt(int index) => PaddedSectionSize(_bodySizes[index]);
}
