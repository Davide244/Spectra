using SpectraEngine.Core.Audio;
using System;
using System.Runtime.InteropServices;

namespace SpectraEngine.Core.Assets.Audio;

/// <summary>
/// What <see cref="SaudioReader"/> found in a <c>.saudio</c>: the codec, the
/// shape of the sound, its loop points and where its payload sits in the file's
/// own bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="DataOffset"/> is into the WHOLE FILE, not into a payload
/// slice</b>, exactly as <c>SimageInfo.Mips</c> is, so a caller reads straight
/// over the mapped span with no copy and no arithmetic of its own. Slicing off a
/// payload region here would put the same offset arithmetic in every caller and
/// leave each one free to get it slightly wrong.
/// </para>
/// <para>
/// <b>A class rather than a ref struct, holding numbers rather than a span.</b>
/// It carries no bytes, so it may outlive the span it describes - which is what
/// lets a background read hand it to the render thread beside the
/// <c>ContentBlob</c> whose reference keeps the mapping alive.
/// </para>
/// </remarks>
public sealed class SaudioInfo
{
    internal SaudioInfo(
        int formatVersion,
        SaudioCodec codec,
        SaudioFlags flags,
        AudioFormat format,
        SaudioChannelLayout layout,
        long frameCount,
        LoopRegion loop,
        int dataOffset,
        int dataLength,
        int framesPerSeekEntry,
        long[] seekTable)
    {
        FormatVersion = formatVersion;
        Codec = codec;
        Flags = flags;
        Format = format;
        ChannelLayout = layout;
        FrameCount = frameCount;
        Loop = loop;
        DataOffset = dataOffset;
        DataLength = dataLength;
        FramesPerSeekEntry = framesPerSeekEntry;
        SeekTable = seekTable;
    }

    /// <summary>The <c>.saudio</c> version this file was cooked under.</summary>
    public int FormatVersion { get; }

    /// <summary>How the payload is encoded.</summary>
    public SaudioCodec Codec { get; }

    /// <summary>What the file declares about how it is meant to be played.</summary>
    public SaudioFlags Flags { get; }

    /// <summary>Rate and channel count, in the runtime's own vocabulary.</summary>
    public AudioFormat Format { get; }

    /// <summary>How the channels in a frame are arranged.</summary>
    public SaudioChannelLayout ChannelLayout { get; }

    /// <summary>Total decoded sample frames.</summary>
    public long FrameCount { get; }

    /// <summary>
    /// The region the sound repeats, in sample frames, or
    /// <see cref="LoopRegion.None"/>.
    /// </summary>
    public LoopRegion Loop { get; }

    /// <summary>Byte offset of the payload from the start of the file.</summary>
    public int DataOffset { get; }

    /// <summary>Bytes of payload.</summary>
    public int DataLength { get; }

    /// <summary>
    /// Sample frames between two seek entries, or 0 when there is no seek table.
    /// </summary>
    public int FramesPerSeekEntry { get; }

    /// <summary>
    /// Byte offsets of each seekable point, from the start of the file; empty
    /// for a resident sound.
    /// </summary>
    /// <remarks>
    /// <b>A seek table is what makes "start the track at 1:30" not a linear
    /// decode</b>, and for <see cref="SaudioCodec.PcmS16"/> it is derivable
    /// arithmetic that the file carries anyway. That is the point: the moment a
    /// codec whose decoder is forward-only arrives, the table is already in the
    /// format and already validated, rather than being the thing that forces a
    /// format change at exactly the moment somebody is trying to add music.
    /// </remarks>
    public long[] SeekTable { get; }

    /// <summary>True when the file asks to be played through a buffer queue.</summary>
    public bool IsStreaming => (Flags & SaudioFlags.Streaming) != 0;

    /// <summary>True when the file declares it is meant to be placed in the world.</summary>
    public bool IsPositional => (Flags & SaudioFlags.PositionalIntent) != 0;

    /// <summary>Seconds of audio.</summary>
    public double Duration => Format.FramesToSeconds(FrameCount);

    /// <summary>
    /// The PCM payload inside <paramref name="file"/>, as interleaved samples.
    /// </summary>
    /// <remarks>
    /// <para><b>A reinterpretation, not a copy</b>: the samples are read straight
    /// out of whatever <paramref name="file"/> is, which for a mounted pack is a
    /// memory-mapped view. Whoever holds this span must also hold the
    /// <c>ContentBlob</c> it came from, because unmapping under a live span is an
    /// access violation with no managed stack rather than an exception.</para>
    /// <para><b>PCM in this format is little-endian by definition</b>, and this
    /// cast is therefore a host-endianness assumption written down in one place.
    /// A big-endian host would read every sample byte-swapped, which is noise
    /// rather than a failure - named here rather than guarded because .NET has no
    /// big-endian target this engine builds for, and this is where the swap goes
    /// the day one exists.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The file is not PCM16.</exception>
    public ReadOnlySpan<short> Pcm(ReadOnlySpan<byte> file)
    {
        if (Codec != SaudioCodec.PcmS16)
        {
            throw new InvalidOperationException(
                $"A {Codec} payload is encoded, not PCM; decode it before asking for samples.");
        }

        return MemoryMarshal.Cast<byte, short>(file.Slice(DataOffset, DataLength));
    }
}
