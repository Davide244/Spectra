using System;

namespace SpectraEngine.Core.Assets.Audio;

/// <summary>
/// How the samples in a <c>.saudio</c> payload are encoded.
/// </summary>
/// <remarks>
/// <para><b>The numbers are the format and are append-only</b>, exactly as
/// <c>PackEntryKind</c>'s and <c>RuleKind</c>'s are. Inserting a value renumbers
/// every codec after it, and a file cooked before the insertion then decodes as
/// a different codec: PCM read as ADPCM is noise, and nothing anywhere reports
/// it, because the header still parses.</para>
/// <para><b>v1 writes and reads <see cref="PcmS16"/> and nothing else, and the
/// other three are declared anyway.</b> The field exists so a second codec
/// arrives without a format change - the whole reason it is a byte rather than
/// an implied constant - and a reader that names what it refused is what turns
/// "the sound is silent" into "recook it". NVorbis and Concentus are both
/// plausible and both have an <i>inferred</i> NativeAOT posture, which this arc
/// has a standing rule against; the moment one is verified, the codec number is
/// already spent for it.</para>
/// </remarks>
public enum SaudioCodec : byte
{
    /// <summary>Interleaved little-endian signed 16-bit PCM. The only codec v1 carries.</summary>
    PcmS16 = 0,

    /// <summary>Reserved: Vorbis in an Ogg stream.</summary>
    Vorbis = 1,

    /// <summary>Reserved: Opus in an Ogg stream.</summary>
    Opus = 2,

    /// <summary>Reserved: IMA ADPCM.</summary>
    ImaAdpcm = 3,
}

/// <summary>
/// What a <c>.saudio</c> declares about how it is meant to be played.
/// </summary>
/// <remarks>
/// <b>Both bits are statements about INTENT that the runtime is free to act on,
/// never facts it has to trust.</b> A resident sound with the streaming bit set
/// still plays; a stereo file with the positional bit clear still plays. What
/// the bits buy is a cook that can say the thing it knows and a runtime that can
/// pick a path without measuring the file.
/// </remarks>
[Flags]
public enum SaudioFlags : byte
{
    /// <summary>Resident, non-positional, no seek table.</summary>
    None = 0,

    /// <summary>
    /// The sound is long enough to be fed through a buffer queue rather than
    /// held whole. A file carrying this bit carries a seek table.
    /// </summary>
    Streaming = 1 << 0,

    /// <summary>
    /// The sound is meant to be placed in the world.
    /// </summary>
    /// <remarks>
    /// <b>It is only ever set on a MONO file</b>, because OpenAL plays a stereo
    /// buffer unpositioned - the classic "why is my 3D sound not 3D" report - so
    /// a stereo file claiming positional intent would be a claim no driver can
    /// honour. The cooker warns about exactly that pairing rather than setting
    /// the bit and hoping.
    /// </remarks>
    PositionalIntent = 1 << 1,
}

/// <summary>
/// How the channels in a frame are laid out.
/// </summary>
/// <remarks>
/// Redundant with the channel COUNT today and deliberately separate from it:
/// 5.1 and 7.1 are both six and eight channels under more than one arrangement,
/// so a count alone stops being an answer the moment either arrives. The reader
/// refuses a layout that disagrees with the count, which is the only way two
/// fields describing one thing stay honest.
/// </remarks>
public enum SaudioChannelLayout : byte
{
    /// <summary>One channel.</summary>
    Mono = 0,

    /// <summary>Two interleaved channels, left then right.</summary>
    Stereo = 1,
}

/// <summary>
/// The fixed byte geometry of a <c>.saudio</c> file, stated once for the cook
/// rule that writes one and the reader that reads it back.
/// </summary>
/// <remarks>
/// <para><b>Two expressions of one layout diverge</b>, which is the lesson
/// <c>SmodelFormat</c> and <c>PackFormat</c> both already record: a writer that
/// computes an offset from its own running cursor and a reader that recomputes
/// it from a literal agree exactly until one of them is edited, and then
/// disagree as a read into the middle of somebody else's bytes rather than as an
/// exception. Both sides take their arithmetic from here.</para>
/// <para><b>Loop points and the seek table's stride are SAMPLE FRAMES.</b> Bytes
/// depend on the channel count and the sample width and break the instant either
/// moves; seconds depend on the rate and cannot be sample-accurate at all, and
/// the one-sample gap a rounded loop point leaves in a sustained ambience loop is
/// a click, once a second, forever. The runtime already measures loops in frames
/// for the same reason (<c>LoopRegion</c>), so the file and the player agree by
/// construction rather than through a conversion somebody has to get right
/// twice.</para>
/// <para><b>The header is a fixed 48 bytes and the table sits at an explicit
/// offset anyway.</b> That is not the pack's reasoning - a pack carries
/// <c>EntryTableOffset</c> so a v2 header can grow without spending a version
/// number, because a pack is mounted by readers of many ages. A cooked sound
/// versions the strict way: a reader seeing a version it does not implement
/// refuses the file and says recook. <see cref="DataOffset"/> is explicit
/// because the seek table sits between the header and the payload and its size
/// is data-dependent, so there is genuinely nothing to compute it from.</para>
/// </remarks>
public static class SaudioFormat
{
    /// <summary>The cooked extension, dot included.</summary>
    public const string FileExtension = ".saudio";

    /// <summary>
    /// File magic, <c>"SAUD"</c>. Stored as a little-endian <see cref="uint"/>,
    /// so the first four bytes on disk read <c>S A U D</c> in a hex dump.
    /// </summary>
    public const uint Magic = 'S' | ('A' << 8) | ('U' << 16) | ((uint)'D' << 24);

    /// <summary>Bytes in the header, which lives at offset 0.</summary>
    public const int HeaderSize = 48;

    // --- field offsets, in the order docs/formats-and-pipeline.md 2.4 lists
    // them. Named rather than summed at each site because every one of them is
    // an offset both the writer and the reader index by, and a literal spelled
    // twice is a layout drift that reads as data rather than as a failure.

    /// <summary><see cref="Magic"/>, four bytes.</summary>
    public const int MagicOffset = 0x00;

    /// <summary>The format version, <c>u16</c>.</summary>
    public const int VersionOffset = 0x04;

    /// <summary><see cref="SaudioCodec"/>, one byte.</summary>
    public const int CodecOffset = 0x06;

    /// <summary><see cref="SaudioFlags"/>, one byte.</summary>
    public const int FlagsOffset = 0x07;

    /// <summary>Sample frames per second, <c>u32</c>.</summary>
    public const int SampleRateOffset = 0x08;

    /// <summary>Interleaved channels per frame, one byte.</summary>
    public const int ChannelsOffset = 0x0C;

    /// <summary><see cref="SaudioChannelLayout"/>, one byte.</summary>
    public const int ChannelLayoutOffset = 0x0D;

    /// <summary>Two reserved bytes, written zero.</summary>
    public const int ReservedOffset = 0x0E;

    /// <summary>Total decoded sample frames, <c>u64</c>.</summary>
    public const int FrameCountOffset = 0x10;

    /// <summary>First frame of the loop region, <c>u64</c>.</summary>
    public const int LoopStartOffset = 0x18;

    /// <summary>One past the last frame of the loop region, <c>u64</c>; 0 means no loop.</summary>
    public const int LoopEndOffset = 0x20;

    /// <summary>Byte offset of the seek table, <c>u32</c>; 0 means none.</summary>
    public const int SeekTableOffsetOffset = 0x28;

    /// <summary>Byte offset of the payload, <c>u32</c>.</summary>
    public const int DataOffsetOffset = 0x2C;

    /// <summary>
    /// Bytes in the seek table's own header: <c>u32 entryCount</c> then
    /// <c>u32 framesPerEntry</c>.
    /// </summary>
    public const int SeekTableHeaderSize = 8;

    /// <summary>Bytes in one seek-table entry: a <c>u64</c> byte offset.</summary>
    public const int SeekTableEntrySize = 8;

    /// <summary>Bytes one sample of one channel occupies under <see cref="SaudioCodec.PcmS16"/>.</summary>
    /// <remarks>
    /// Named here as well as on <c>AudioFormat</c> because this one is a
    /// statement about the FILE and that one is a statement about the runtime's
    /// buffers. They are the same number today and are not the same fact: a
    /// second codec moves this one and leaves that one alone.
    /// </remarks>
    public const int PcmBytesPerSample = sizeof(short);

    /// <summary>
    /// The largest frame count the reader will entertain, so every derived byte
    /// size stays inside an <see cref="int"/>.
    /// </summary>
    /// <remarks>
    /// Not a policy about how long a sound may be: it is what keeps
    /// <c>frames * channels * 2</c> from wrapping. A wrapped length is a
    /// negative slice length or a plausible small one, and the bytes normally
    /// arrive as a span into a memory-mapped view, where the second of those is
    /// a read past the end of the mapping - an access violation with no managed
    /// stack, no catch block, and nothing in the log naming the file. Roughly
    /// three hours of 48 kHz stereo.
    /// </remarks>
    public const long MaxFrameCount = int.MaxValue / 4;

    /// <summary>Bytes <paramref name="frames"/> of PCM16 occupy at <paramref name="channels"/> channels.</summary>
    public static long PcmByteLength(long frames, int channels) =>
        frames * channels * PcmBytesPerSample;

    /// <summary>The channel layout <paramref name="channels"/> interleaved channels means.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Neither mono nor stereo.</exception>
    public static SaudioChannelLayout LayoutFor(int channels) => channels switch
    {
        1 => SaudioChannelLayout.Mono,
        2 => SaudioChannelLayout.Stereo,
        _ => throw new ArgumentOutOfRangeException(
            nameof(channels), channels, "A .saudio carries mono or stereo; 5.1 and 7.1 are reserved."),
    };

    /// <summary>Interleaved channels <paramref name="layout"/> describes, or 0 for a layout this build has no count for.</summary>
    public static int ChannelsFor(SaudioChannelLayout layout) => layout switch
    {
        SaudioChannelLayout.Mono => 1,
        SaudioChannelLayout.Stereo => 2,
        _ => 0,
    };
}
