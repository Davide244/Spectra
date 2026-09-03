using SpectraEngine.Core;
using SpectraEngine.Core.Assets.Audio;
using SpectraEngine.Core.Audio;
using System;
using System.Buffers.Binary;

namespace Spectra.Kitchen.Audio;

/// <summary>
/// Writes a <c>.saudio</c>: the 48-byte header <see cref="SaudioFormat"/>
/// declares, an optional seek table, then interleaved PCM16.
/// </summary>
/// <remarks>
/// <para><b>The layout comes from <see cref="SaudioFormat"/> and is not spelled
/// again here.</b> A writer that computes an offset from its own running cursor
/// and a reader that recomputes it from a literal agree exactly until one of
/// them is edited, and then disagree as a read into the middle of somebody
/// else's bytes rather than as an exception.</para>
/// <para><b>Every reserved byte is zero-filled deliberately.</b> A managed array
/// arrives zeroed, so this costs nothing and buys the thing the pack writer
/// already learned the hard way: an unzeroed field picks up whatever was in the
/// buffer and turns the byte-identity oracle red in a way that is very hard to
/// bisect, because the bytes differ in a field nothing reads.</para>
/// <para><b>The seek table rides with the streaming flag or not at all.</b> The
/// reader refuses either one without the other, so this is where the pair is
/// kept honest: a caller asking for streaming gets a table, and a caller who did
/// not ask gets neither.</para>
/// </remarks>
public static class SaudioWriter
{
    /// <summary>
    /// Writes one cooked sound.
    /// </summary>
    /// <param name="format">Rate and channel count of <paramref name="pcm"/>.</param>
    /// <param name="pcm">Interleaved PCM16. Length must be a whole number of frames.</param>
    /// <param name="loop">The region to repeat, or <see cref="LoopRegion.None"/>.</param>
    /// <param name="positional">Whether the sound is meant to be placed in the world.</param>
    /// <param name="framesPerSeekEntry">
    /// Frames between seek points; zero writes a resident file with no seek table
    /// and no streaming flag.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The inputs describe a file this format cannot hold. Thrown rather than
    /// clamped, because every one of these is a cooker bug and a clamped one
    /// ships a sound that is merely wrong.
    /// </exception>
    public static byte[] Write(
        AudioFormat format,
        ReadOnlySpan<short> pcm,
        LoopRegion loop,
        bool positional,
        int framesPerSeekEntry = 0)
    {
        if (pcm.Length % format.Channels != 0)
        {
            throw new ArgumentException(
                $"{pcm.Length} interleaved samples is not a whole number of {format.Channels}-channel frames.",
                nameof(pcm));
        }

        long frames = pcm.Length / format.Channels;
        if (frames <= 0 || frames > SaudioFormat.MaxFrameCount)
        {
            throw new ArgumentException(
                $"A .saudio holds between 1 and {SaudioFormat.MaxFrameCount} sample frames, not {frames}.",
                nameof(pcm));
        }

        if (loop.IsLooping && loop.EndFrame > frames)
        {
            throw new ArgumentException(
                $"The loop ends at frame {loop.EndFrame} and the sound is {frames} frames long.",
                nameof(loop));
        }

        if (framesPerSeekEntry < 0)
        {
            throw new ArgumentException(
                "A seek stride is a positive number of frames, or zero for no seek table.",
                nameof(framesPerSeekEntry));
        }

        bool streaming = framesPerSeekEntry > 0;
        long entryCount = streaming ? (frames + framesPerSeekEntry - 1) / framesPerSeekEntry : 0;
        int tableBytes = streaming
            ? SaudioFormat.SeekTableHeaderSize + checked((int)entryCount) * SaudioFormat.SeekTableEntrySize
            : 0;

        int dataOffset = SaudioFormat.HeaderSize + tableBytes;
        int payloadBytes = checked((int)SaudioFormat.PcmByteLength(frames, format.Channels));
        var file = new byte[dataOffset + payloadBytes];

        SaudioFlags flags = SaudioFlags.None;
        if (streaming) flags |= SaudioFlags.Streaming;

        // Positional intent is a claim only a MONO file can make good on: OpenAL
        // plays a stereo buffer unpositioned whatever any flag says, so setting
        // the bit on one would put a promise in the file that no driver keeps.
        // The rule warns about the pairing; this refuses to record it.
        if (positional && format.Channels == 1) flags |= SaudioFlags.PositionalIntent;

        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(SaudioFormat.MagicOffset), SaudioFormat.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(
            file.AsSpan(SaudioFormat.VersionOffset), (ushort)EngineInfo.AudioFormatVersion);

        file[SaudioFormat.CodecOffset] = (byte)SaudioCodec.PcmS16;
        file[SaudioFormat.FlagsOffset] = (byte)flags;

        BinaryPrimitives.WriteUInt32LittleEndian(
            file.AsSpan(SaudioFormat.SampleRateOffset), (uint)format.SampleRate);

        file[SaudioFormat.ChannelsOffset] = (byte)format.Channels;
        file[SaudioFormat.ChannelLayoutOffset] = (byte)SaudioFormat.LayoutFor(format.Channels);

        // ReservedOffset stays zero. Not asserted on read - a v2 that spends
        // those two bytes raises the format version, which the reader refuses
        // before it ever looks at them.

        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(SaudioFormat.FrameCountOffset), (ulong)frames);
        BinaryPrimitives.WriteUInt64LittleEndian(
            file.AsSpan(SaudioFormat.LoopStartOffset), (ulong)(loop.IsLooping ? loop.StartFrame : 0));
        BinaryPrimitives.WriteUInt64LittleEndian(
            file.AsSpan(SaudioFormat.LoopEndOffset), (ulong)(loop.IsLooping ? loop.EndFrame : 0));

        BinaryPrimitives.WriteUInt32LittleEndian(
            file.AsSpan(SaudioFormat.SeekTableOffsetOffset),
            streaming ? SaudioFormat.HeaderSize : 0u);

        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(SaudioFormat.DataOffsetOffset), (uint)dataOffset);

        if (streaming)
        {
            int at = SaudioFormat.HeaderSize;
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at), (uint)entryCount);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at + 4), (uint)framesPerSeekEntry);

            int frameBytes = format.Channels * SaudioFormat.PcmBytesPerSample;
            for (long entry = 0; entry < entryCount; entry++)
            {
                long offset = dataOffset + entry * framesPerSeekEntry * frameBytes;
                BinaryPrimitives.WriteUInt64LittleEndian(
                    file.AsSpan(at + SaudioFormat.SeekTableHeaderSize +
                        checked((int)(entry * SaudioFormat.SeekTableEntrySize))),
                    (ulong)offset);
            }
        }

        // Written sample by sample rather than by casting the span, so the file
        // is little-endian on every host rather than on the hosts that happen to
        // be. The reader's own cast documents the mirror of this.
        Span<byte> payload = file.AsSpan(dataOffset, payloadBytes);
        for (int i = 0; i < pcm.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(payload[(i * 2)..], pcm[i]);

        return file;
    }
}
