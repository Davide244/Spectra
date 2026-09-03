using System;
using System.Buffers.Binary;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// Builds <c>.saudio</c> bytes from the format specification, touching none of
/// the engine's own types.
/// </summary>
/// <remarks>
/// <para><b>Every offset here is spelled out rather than taken from
/// <c>SaudioFormat</c>.</b> A reader checked against its own constants proves
/// the two agree rather than that either is right, and every failure in this
/// format is a misinterpreted buffer rather than an exception - so a second
/// opinion is the only thing that can catch a layout drift. It is the same
/// argument <c>HandBuiltSmodel</c> makes one format over.</para>
/// <para><b>The files it produces are VALID, and the tests damage them.</b> One
/// field patched per test is what makes each refusal its own case with its own
/// message, instead of one fixture that is wrong in six ways and passes on
/// whichever check happens to run first.</para>
/// </remarks>
internal static class HandBuiltSaudio
{
    public const int HeaderSize = 48;
    public const int SeekTableHeaderSize = 8;
    public const int SeekTableEntrySize = 8;

    public const int MagicOffset = 0x00;
    public const int VersionOffset = 0x04;
    public const int CodecOffset = 0x06;
    public const int FlagsOffset = 0x07;
    public const int SampleRateOffset = 0x08;
    public const int ChannelsOffset = 0x0C;
    public const int ChannelLayoutOffset = 0x0D;
    public const int FrameCountOffset = 0x10;
    public const int LoopStartOffset = 0x18;
    public const int LoopEndOffset = 0x20;
    public const int SeekTableOffsetOffset = 0x28;
    public const int DataOffsetOffset = 0x2C;

    // "SAUD" little-endian, spelled as the four characters rather than as a
    // number, so a transposition in the magic is visible here.
    public const uint Magic = 'S' | ('A' << 8) | ('U' << 16) | ((uint)'D' << 24);

    /// <summary>A valid resident sound: no streaming flag, no seek table.</summary>
    public static byte[] Resident(
        int frames = 16,
        int channels = 1,
        int sampleRate = 48_000,
        long loopStart = 0,
        long loopEnd = 0,
        byte flags = 0)
    {
        int payload = frames * channels * 2;
        var file = new byte[HeaderSize + payload];

        WriteHeader(file, frames, channels, sampleRate, loopStart, loopEnd, flags, 0, HeaderSize);
        FillPayload(file, HeaderSize, payload);
        return file;
    }

    /// <summary>A valid streaming sound: the streaming flag and a matching seek table.</summary>
    public static byte[] Streaming(
        int frames = 64,
        int channels = 1,
        int sampleRate = 48_000,
        int framesPerEntry = 16)
    {
        int entries = (frames + framesPerEntry - 1) / framesPerEntry;
        int tableBytes = SeekTableHeaderSize + entries * SeekTableEntrySize;
        int dataOffset = HeaderSize + tableBytes;
        int payload = frames * channels * 2;

        var file = new byte[dataOffset + payload];
        WriteHeader(file, frames, channels, sampleRate, 0, 0, flags: 1, HeaderSize, dataOffset);

        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(HeaderSize), (uint)entries);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(HeaderSize + 4), (uint)framesPerEntry);

        int frameBytes = channels * 2;
        for (int entry = 0; entry < entries; entry++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(
                file.AsSpan(HeaderSize + SeekTableHeaderSize + entry * SeekTableEntrySize),
                (ulong)(dataOffset + (long)entry * framesPerEntry * frameBytes));
        }

        FillPayload(file, dataOffset, payload);
        return file;
    }

    /// <summary>Where the first seek entry's <c>u64</c> sits, for a test that wants to move one.</summary>
    public static int SeekEntryOffset(int index) =>
        HeaderSize + SeekTableHeaderSize + index * SeekTableEntrySize;

    private static void WriteHeader(
        byte[] file,
        int frames,
        int channels,
        int sampleRate,
        long loopStart,
        long loopEnd,
        byte flags,
        int seekTableOffset,
        int dataOffset)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(MagicOffset), Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(VersionOffset), 1);
        file[CodecOffset] = 0;                                   // PcmS16
        file[FlagsOffset] = flags;
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(SampleRateOffset), (uint)sampleRate);
        file[ChannelsOffset] = (byte)channels;
        file[ChannelLayoutOffset] = (byte)(channels - 1);        // 0 Mono, 1 Stereo
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(FrameCountOffset), (ulong)frames);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(LoopStartOffset), (ulong)loopStart);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(LoopEndOffset), (ulong)loopEnd);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(SeekTableOffsetOffset), (uint)seekTableOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(DataOffsetOffset), (uint)dataOffset);
    }

    // A ramp rather than zeros, so a test that reads the payload back can tell
    // "the right bytes" from "a buffer nobody wrote".
    private static void FillPayload(byte[] file, int at, int bytes)
    {
        for (int i = 0; i < bytes / 2; i++)
            BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(at + i * 2), (short)(i * 37 - 4000));
    }
}
