using SpectraEngine.Core.Audio;
using System;
using System.Buffers.Binary;

namespace SpectraEngine.Core.Assets.Audio;

/// <summary>
/// Reads a <c>.saudio</c>: the 48-byte header described by
/// <see cref="SaudioFormat"/>, its optional seek table, and where the payload
/// sits.
/// </summary>
/// <remarks>
/// <para>
/// <b>It validates and REFUSES, naming what was wrong and what was expected.</b>
/// Nearly everything this reader checks has a failure that plays a sound rather
/// than raising anything: a rate that disagrees with the payload plays the whole
/// asset at the wrong pitch, a channel count that disagrees swaps the ears every
/// frame, a loop end past the end of the sound asks the fill loop for frames
/// that are not there. And the two length fields have a worse failure than any
/// of those - the bytes normally arrive as a span into a memory-mapped view,
/// where a length nobody bounded is a read past the end of the mapping, which on
/// Windows is an access violation with no managed stack, no catch block and
/// nothing in the log naming the file. So the answer to every uncertainty here
/// is a message, not a guess.
/// </para>
/// <para>
/// <b>A span in, no streams.</b> A mounted pack hands out a span into a mapped
/// view, and wrapping one in a <c>MemoryStream</c> copies the whole file to read
/// forty-eight bytes of header - the copy the container exists to avoid. Every
/// field is read through <see cref="BinaryPrimitives"/> rather than by
/// reinterpreting a struct, because this format fixes little-endian and a struct
/// cast on a big-endian host produces an enormous plausible frame count instead
/// of a failure.
/// </para>
/// <para>
/// <b>A cooked artifact versions the STRICT way</b>: exact match or refuse, with
/// both numbers in the message and the word recook in it, because a cooked file
/// is a build output that can always be regenerated and the bytes past the
/// header only mean anything under the version that wrote them.
/// </para>
/// </remarks>
public static class SaudioReader
{
    // Anything a driver could plausibly be asked for, and a bound rather than a
    // policy: an unbounded rate turns a garbage header into a duration and a
    // seek stride computed from a number nobody wrote. 768 kHz is four times the
    // highest rate any consumer format uses.
    private const uint MaxSampleRate = 768_000;

    // The bits SaudioFlags defines today. A file setting anything else was
    // written by a cooker this build does not understand, which under strict
    // cooked-artifact versioning is a refusal rather than a mask.
    private const byte KnownFlags = (byte)(SaudioFlags.Streaming | SaudioFlags.PositionalIntent);

    /// <summary>
    /// Whether <paramref name="file"/> opens with the <c>SAUD</c> magic. Cheap,
    /// and says nothing about whether the rest of the file is readable.
    /// </summary>
    public static bool LooksLikeSaudio(ReadOnlySpan<byte> file) =>
        file.Length >= 4 &&
        BinaryPrimitives.ReadUInt32LittleEndian(file) == SaudioFormat.Magic;

    /// <summary>
    /// Parses <paramref name="file"/>, or refuses it saying which rule it broke.
    /// </summary>
    /// <param name="file">The whole file. Offsets in the result are relative to its start.</param>
    /// <param name="originForErrors">Path or label naming the file in messages.</param>
    /// <exception cref="SaudioFormatException">
    /// The bytes are not a <c>.saudio</c> this engine can play.
    /// </exception>
    public static SaudioInfo Read(ReadOnlySpan<byte> file, string originForErrors = "<memory>")
    {
        // Magic before length, so a file that is not a cooked sound at all is
        // told that rather than being told it is short: the two have completely
        // different answers, and "recook it" is only one of them.
        if (!LooksLikeSaudio(file))
        {
            throw Refuse(
                originForErrors,
                "it does not start with the 'SAUD' magic, so it is not a cooked sound at all.");
        }

        if (file.Length < SaudioFormat.HeaderSize)
        {
            throw Refuse(
                originForErrors,
                $"it is {file.Length} bytes, which is shorter than the {SaudioFormat.HeaderSize}-byte header.");
        }

        int version = BinaryPrimitives.ReadUInt16LittleEndian(file[SaudioFormat.VersionOffset..]);
        if (version != EngineInfo.AudioFormatVersion)
        {
            throw Refuse(
                originForErrors,
                $"it was cooked for audio format version {version} and this engine reads version " +
                $"{EngineInfo.AudioFormatVersion}; recook it.");
        }

        var codec = (SaudioCodec)file[SaudioFormat.CodecOffset];
        if (codec != SaudioCodec.PcmS16)
        {
            throw Refuse(originForErrors, codec switch
            {
                // Named one by one rather than lumped into "unsupported": these
                // three are numbers the format has already spent, so the answer
                // is "this build has no decoder" and not "recook it as something
                // else", which is the answer for a number nothing defines.
                SaudioCodec.Vorbis or SaudioCodec.Opus or SaudioCodec.ImaAdpcm =>
                    $"its codec is {codec} ({(byte)codec}), which the .saudio format reserves and this engine " +
                    "has no decoder for; cook it as PcmS16.",

                _ => $"its codec byte is {(byte)codec}, which is not a codec the .saudio format defines.",
            });
        }

        byte flagBits = file[SaudioFormat.FlagsOffset];
        if ((flagBits & ~KnownFlags) != 0)
        {
            throw Refuse(
                originForErrors,
                $"its flag byte is 0x{flagBits:X2} and this engine only defines 0x{KnownFlags:X2}; it was " +
                "written by a newer cooker, so recook it.");
        }

        var flags = (SaudioFlags)flagBits;

        uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(file[SaudioFormat.SampleRateOffset..]);
        if (sampleRate is 0 or > MaxSampleRate)
        {
            throw Refuse(
                originForErrors,
                $"its sample rate is {sampleRate}; a .saudio is between 1 and {MaxSampleRate} frames a second.");
        }

        int channels = file[SaudioFormat.ChannelsOffset];
        if (channels is not (1 or 2))
        {
            throw Refuse(
                originForErrors,
                $"it declares {channels} channels; PCM16 in OpenAL is mono or stereo, and 5.1 and 7.1 are " +
                "reserved rather than carried.");
        }

        var layout = (SaudioChannelLayout)file[SaudioFormat.ChannelLayoutOffset];
        if (SaudioFormat.ChannelsFor(layout) != channels)
        {
            // Two fields describing one thing, and the whole reason the layout is
            // separate from the count is that they stop agreeing the moment a
            // multi-channel arrangement arrives. A disagreement now is a writer
            // bug, and honouring either one of them silently picks a side.
            throw Refuse(
                originForErrors,
                $"it declares channel layout {layout} ({(byte)layout}) and {channels} channels, which do not " +
                "describe the same frame.");
        }

        long frameCount = ReadI64(file, SaudioFormat.FrameCountOffset, originForErrors, "FrameCount");
        if (frameCount <= 0 || frameCount > SaudioFormat.MaxFrameCount)
        {
            throw Refuse(
                originForErrors,
                $"it declares {frameCount} sample frames; a .saudio holds between 1 and " +
                $"{SaudioFormat.MaxFrameCount}.");
        }

        long loopStart = ReadI64(file, SaudioFormat.LoopStartOffset, originForErrors, "LoopStart");
        long loopEnd = ReadI64(file, SaudioFormat.LoopEndOffset, originForErrors, "LoopEnd");
        LoopRegion loop = ReadLoop(loopStart, loopEnd, frameCount, originForErrors);

        uint seekTableOffset = BinaryPrimitives.ReadUInt32LittleEndian(file[SaudioFormat.SeekTableOffsetOffset..]);
        uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(file[SaudioFormat.DataOffsetOffset..]);

        long payloadBytes = SaudioFormat.PcmByteLength(frameCount, channels);
        if (dataOffset < SaudioFormat.HeaderSize || dataOffset + payloadBytes > file.Length)
        {
            throw Refuse(
                originForErrors,
                $"its {frameCount}-frame payload occupies {payloadBytes} bytes at offset {dataOffset}, and the " +
                $"file is {file.Length} bytes.");
        }

        if (dataOffset % SaudioFormat.PcmBytesPerSample != 0)
        {
            // The payload is read as shorts straight out of the mapped view, so
            // an odd offset would put every sample across a sample boundary. That
            // is a slow read on the platforms that tolerate it and undefined on
            // the ones that do not; either way it is not a thing a cooker should
            // ever emit.
            throw Refuse(
                originForErrors,
                $"its payload starts at byte {dataOffset}, which is not a multiple of the " +
                $"{SaudioFormat.PcmBytesPerSample}-byte PCM16 sample it is read as.");
        }

        (int framesPerEntry, long[] seekTable) = ReadSeekTable(
            file, flags, seekTableOffset, (int)dataOffset, payloadBytes, frameCount, channels, originForErrors);

        return new SaudioInfo(
            version,
            codec,
            flags,
            new AudioFormat((int)sampleRate, channels),
            layout,
            frameCount,
            loop,
            (int)dataOffset,
            (int)payloadBytes,
            framesPerEntry,
            seekTable);
    }

    // The loop is the field this format exists to carry, and every one of its
    // failures is silent: a region that ends before it starts reads zero frames
    // and asks for zero again, which is a hang inside the fill loop rather than
    // a silent sound, and a region past the end of the sound reads frames that
    // are not there. LoopRegion's own constructor refuses both, so the answer
    // here is to say which number was wrong before handing it one.
    private static LoopRegion ReadLoop(long start, long end, long frameCount, string origin)
    {
        if (end == 0)
        {
            if (start != 0)
            {
                throw Refuse(
                    origin,
                    $"it declares a loop starting at frame {start} and no loop end, which is a loop nothing " +
                    "can play; a sound with no loop writes 0 for both.");
            }

            return LoopRegion.None;
        }

        if (start < 0 || end <= start)
        {
            throw Refuse(
                origin,
                $"its loop is [{start}, {end}), which contains no frames; a loop region holds at least one.");
        }

        if (end > frameCount)
        {
            throw Refuse(
                origin,
                $"its loop ends at frame {end} and the sound is {frameCount} frames long.");
        }

        return new LoopRegion(start, end);
    }

    // The table is streaming's half of the format, and the flag and the table are
    // two statements about one thing: a table nothing reads is dead weight, and a
    // streaming flag with no table is a seek that has to linear-decode after
    // promising not to. Refusing a disagreement is what keeps the pair honest.
    private static (int FramesPerEntry, long[] Table) ReadSeekTable(
        ReadOnlySpan<byte> file,
        SaudioFlags flags,
        uint tableOffset,
        int dataOffset,
        long payloadBytes,
        long frameCount,
        int channels,
        string origin)
    {
        bool streaming = (flags & SaudioFlags.Streaming) != 0;

        if (tableOffset == 0)
        {
            if (streaming)
            {
                throw Refuse(
                    origin,
                    "it is flagged streaming and carries no seek table, so starting it part-way through would " +
                    "have to decode from the beginning.");
            }

            return (0, []);
        }

        if (!streaming)
        {
            throw Refuse(
                origin,
                $"it carries a seek table at byte {tableOffset} and is not flagged streaming; a seek table is " +
                "streaming's, and one nothing reads is a claim about the file that is not true.");
        }

        if (tableOffset < SaudioFormat.HeaderSize ||
            tableOffset + SaudioFormat.SeekTableHeaderSize > file.Length)
        {
            throw Refuse(
                origin,
                $"its seek table starts at byte {tableOffset}, which is outside the {file.Length}-byte file.");
        }

        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(file[(int)tableOffset..]);
        uint framesPerEntry = BinaryPrimitives.ReadUInt32LittleEndian(
            file[((int)tableOffset + 4)..]);

        if (framesPerEntry == 0)
        {
            throw Refuse(
                origin,
                "its seek table declares 0 frames per entry, so no entry describes any part of the sound.");
        }

        long expected = (frameCount + framesPerEntry - 1) / framesPerEntry;
        if (entryCount != expected)
        {
            // A table whose length does not cover the sound is a table that
            // belongs to a different sound, which presents as a seek landing at
            // the wrong moment rather than as anything that fails.
            throw Refuse(
                origin,
                $"its seek table has {entryCount} entries and a {frameCount}-frame sound at {framesPerEntry} " +
                $"frames an entry needs {expected}.");
        }

        long tableBytes = SaudioFormat.SeekTableHeaderSize + (long)entryCount * SaudioFormat.SeekTableEntrySize;
        if (tableOffset + tableBytes > dataOffset)
        {
            throw Refuse(
                origin,
                $"its seek table needs {tableBytes} bytes at offset {tableOffset} and the payload starts at " +
                $"{dataOffset}, so the two overlap.");
        }

        var table = new long[entryCount];
        long payloadEnd = dataOffset + payloadBytes;
        long previous = long.MinValue;

        for (int i = 0; i < table.Length; i++)
        {
            int at = (int)tableOffset + SaudioFormat.SeekTableHeaderSize + i * SaudioFormat.SeekTableEntrySize;
            long offset = ReadI64(file, at, origin, $"seek entry {i}");

            if (offset < dataOffset || offset >= payloadEnd)
            {
                throw Refuse(
                    origin,
                    $"its seek entry {i} points at byte {offset}, which is outside the payload " +
                    $"[{dataOffset}, {payloadEnd}).");
            }

            if (offset <= previous)
            {
                throw Refuse(
                    origin,
                    $"its seek entry {i} is at byte {offset} and entry {i - 1} is at {previous}; the table must " +
                    "ascend, or a seek walks backwards through the sound.");
            }

            if ((offset - dataOffset) % (channels * SaudioFormat.PcmBytesPerSample) != 0)
            {
                // Landing mid-frame swaps the channels for the whole rest of the
                // stream: the left ear plays the right channel and no length is
                // wrong anywhere.
                throw Refuse(
                    origin,
                    $"its seek entry {i} is at byte {offset}, which is not a whole number of " +
                    $"{channels}-channel frames from the payload at {dataOffset}.");
            }

            previous = offset;
            table[i] = offset;
        }

        return ((int)framesPerEntry, table);
    }

    // Read as unsigned and refused when it does not fit a long. Every consumer
    // of these fields does signed arithmetic with them, and a u64 cast blindly
    // to a long is negative for half its range, which turns a bounds check into
    // a check that passes.
    private static long ReadI64(ReadOnlySpan<byte> file, int at, string origin, string field)
    {
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(file[at..]);
        if (value > long.MaxValue)
            throw Refuse(origin, $"its {field} is {value}, which is not a number of frames anything wrote.");

        return (long)value;
    }

    private static SaudioFormatException Refuse(string origin, string because) =>
        new($"'{origin}' is not a .saudio this engine can read: {because}");
}
