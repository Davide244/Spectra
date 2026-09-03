using SpectraEngine.Core.Audio;
using System;
using System.Buffers.Binary;
using System.IO;

namespace Spectra.Kitchen.Audio;

/// <summary>
/// Reads a RIFF/WAVE file into interleaved PCM16 at the file's own rate.
/// </summary>
/// <remarks>
/// <para><b>WAV is the authored format because it is the one every tool writes
/// and nothing has to be verified to read.</b> Vorbis and Opus are both
/// plausible sources and both would drag a decoder whose NativeAOT posture is
/// inferred rather than measured into the cooker, which this arc has a standing
/// rule against; see <c>docs/formats-and-pipeline.md</c> 2.4. A DAW exports WAV,
/// and the cooked side is where compression belongs anyway.</para>
/// <para><b>It lives in the KITCHEN and not in Core, and that is the same
/// division every other authored format already follows.</b> A source-format
/// reader runs at cook time; a shipped game reads <c>.saudio</c> and opens no
/// WAV ever, so a parser in Core would be a parser inside every game binary for
/// a file none of them touch.</para>
/// <para><b>Loop points come from the <c>smpl</c> chunk, and its ends are
/// INCLUSIVE.</b> Every DAW that writes loop points writes them there, and the
/// chunk's <c>end</c> field names the last frame that plays, while
/// <see cref="LoopRegion"/> is half-open. Off by one in that conversion is a
/// single dropped or repeated frame per pass round the loop, which is a click
/// once a bar, forever, and is not visible in any waveform anybody would think
/// to look at.</para>
/// <para><b>Every refusal is an <see cref="InvalidDataException"/> naming what
/// was wrong</b>, which is the same type <c>ImageDecoder</c> refuses with and
/// the same type the rule catches, so a broken sound and a broken picture reach
/// a build log the same way.</para>
/// </remarks>
public static class WaveDecoder
{
    // WAVE_FORMAT tags, from the RIFF specification. EXTENSIBLE wraps one of the
    // other two in a GUID whose first two bytes are the real tag, which is what a
    // multichannel or high-bit-depth export from a modern DAW writes.
    private const ushort FormatPcm = 0x0001;
    private const ushort FormatIeeeFloat = 0x0003;
    private const ushort FormatExtensible = 0xFFFE;

    // smpl loop types. Only forward is expressible: AudioLoopCursor plays a
    // region in one direction and has no other mode, so an alternating or
    // backward loop would have to be silently played forward.
    private const uint LoopTypeForward = 0;

    /// <summary>
    /// Decodes <paramref name="file"/>, or refuses it saying which rule it broke.
    /// </summary>
    /// <param name="file">The whole file.</param>
    /// <param name="originForErrors">Path or label naming the file in messages.</param>
    /// <exception cref="InvalidDataException">The bytes are not a WAV this cooker can read.</exception>
    public static DecodedAudio Decode(ReadOnlySpan<byte> file, string originForErrors = "<memory>")
    {
        if (file.Length < 12 || !Matches(file, 0, "RIFF") || !Matches(file, 8, "WAVE"))
        {
            throw Refuse(
                originForErrors,
                "it does not open with a RIFF/WAVE header, so it is not a WAV file at all.");
        }

        bool haveFormat = false;
        ushort tag = 0;
        int channels = 0;
        int sampleRate = 0;
        int bitsPerSample = 0;

        ReadOnlySpan<byte> data = default;
        bool haveData = false;
        long loopStart = 0;
        long loopEnd = 0;
        bool haveLoop = false;
        bool loopRefused = false;

        int at = 12;
        while (at + 8 <= file.Length)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(file[(at + 4)..]);

            // A chunk claiming more bytes than the file holds is truncation, and
            // a decoder that clamped it would hand back a sound that is merely
            // short - which sounds exactly like an author's mistake and is not
            // one.
            if (size > (uint)(file.Length - at - 8))
            {
                throw Refuse(
                    originForErrors,
                    $"its '{Fourcc(file, at)}' chunk claims {size} bytes and only " +
                    $"{file.Length - at - 8} are left in the file.");
            }

            ReadOnlySpan<byte> body = file.Slice(at + 8, (int)size);

            if (Matches(file, at, "fmt "))
            {
                ReadFormat(body, originForErrors, out tag, out channels, out sampleRate, out bitsPerSample);
                haveFormat = true;
            }
            else if (Matches(file, at, "data"))
            {
                data = body;
                haveData = true;
            }
            else if (Matches(file, at, "smpl"))
            {
                haveLoop = TryReadLoop(body, out loopStart, out loopEnd, out loopRefused);
            }

            // Chunks are word-aligned: an odd body is followed by one pad byte
            // that is NOT counted in the size. Walking without it puts the next
            // chunk id one byte late, which reads as garbage of a plausible size
            // rather than as a failure.
            at += 8 + (int)size + ((int)size & 1);
        }

        if (!haveFormat) throw Refuse(originForErrors, "it has no 'fmt ' chunk, so nothing states its format.");
        if (!haveData) throw Refuse(originForErrors, "it has no 'data' chunk, so it carries no samples.");

        short[] samples = Widen(data, tag, bitsPerSample, originForErrors);
        if (samples.Length < channels)
        {
            throw Refuse(
                originForErrors,
                $"its data chunk holds {samples.Length} samples, which is less than one {channels}-channel frame.");
        }

        // Trailing samples that do not complete a frame are dropped rather than
        // refused: a file whose data chunk is a byte long is a broken exporter,
        // and half a frame is inaudible. What must not happen is carrying them,
        // because every length below is frames * channels and a remainder would
        // shift one channel by one sample for the whole sound.
        int frames = samples.Length / channels;
        if (samples.Length != frames * channels) Array.Resize(ref samples, frames * channels);

        LoopRegion loop = LoopRegion.None;
        if (haveLoop)
        {
            // The smpl end is INCLUSIVE and LoopRegion is half-open, so the +1 is
            // the whole conversion. Bounds are checked here rather than trusted:
            // a loop past the end of the data is a file a DAW can legitimately
            // write after an edit, and LoopRegion's own constructor would throw
            // rather than say which number was wrong.
            long end = loopEnd + 1;
            if (loopStart >= 0 && end > loopStart && end <= frames)
                loop = new LoopRegion(loopStart, end);
            else
                loopRefused = true;
        }

        return new DecodedAudio(sampleRate, channels, samples, loop, loopRefused);
    }

    private static void ReadFormat(
        ReadOnlySpan<byte> body,
        string origin,
        out ushort tag,
        out int channels,
        out int sampleRate,
        out int bitsPerSample)
    {
        if (body.Length < 16)
            throw Refuse(origin, $"its 'fmt ' chunk is {body.Length} bytes and the smallest legal one is 16.");

        tag = BinaryPrimitives.ReadUInt16LittleEndian(body);
        channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
        sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
        bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);

        if (tag == FormatExtensible)
        {
            // The real tag is the first two bytes of the SubFormat GUID, 24 bytes
            // into the extension. Reading the outer tag alone would refuse every
            // 24-bit and every multichannel export from a modern DAW, which is
            // most of what a person would actually hand this cooker.
            if (body.Length < 40)
            {
                throw Refuse(
                    origin,
                    $"its 'fmt ' chunk says WAVE_FORMAT_EXTENSIBLE and is {body.Length} bytes; the subformat " +
                    "it needs to name lives at byte 24 of the extension.");
            }

            tag = BinaryPrimitives.ReadUInt16LittleEndian(body[24..]);
        }

        if (tag is not (FormatPcm or FormatIeeeFloat))
        {
            throw Refuse(
                origin,
                $"its format tag is 0x{tag:X4}; this cooker reads uncompressed PCM (0x0001) and IEEE float " +
                "(0x0003), which is what a DAW exports.");
        }

        if (channels is not (1 or 2))
        {
            throw Refuse(
                origin,
                $"it has {channels} channels; OpenAL's PCM16 buffers are mono or stereo, so a cooked sound is " +
                "one or the other.");
        }

        if (sampleRate <= 0)
            throw Refuse(origin, $"its sample rate is {sampleRate}.");
    }

    // Every supported bit depth widened to PCM16, which is the one representation
    // everything downstream works in. The shifts rather than divisions are not an
    // optimisation: an arithmetic shift of a negative sample is exactly the
    // truncation toward negative infinity that keeps a symmetric waveform
    // symmetric, where a division truncates toward zero and puts a DC step at
    // every zero crossing.
    private static short[] Widen(ReadOnlySpan<byte> data, ushort tag, int bitsPerSample, string origin)
    {
        if (tag == FormatIeeeFloat)
        {
            return bitsPerSample switch
            {
                32 => WidenFloat32(data),
                _ => throw Refuse(
                    origin,
                    $"it is IEEE float at {bitsPerSample} bits a sample; this cooker reads 32-bit float."),
            };
        }

        return bitsPerSample switch
        {
            8 => WidenPcm8(data),
            16 => WidenPcm16(data),
            24 => WidenPcm24(data),
            32 => WidenPcm32(data),
            _ => throw Refuse(
                origin,
                $"it is {bitsPerSample}-bit PCM; this cooker reads 8, 16, 24 and 32."),
        };
    }

    // 8-bit WAV samples are UNSIGNED with 128 as silence, alone among the depths.
    // Reading them as signed puts the whole file half a scale off, which is a
    // click at the start and a permanent DC offset rather than anything that
    // fails.
    private static short[] WidenPcm8(ReadOnlySpan<byte> data)
    {
        var samples = new short[data.Length];
        for (int i = 0; i < data.Length; i++) samples[i] = (short)((data[i] - 128) << 8);
        return samples;
    }

    private static short[] WidenPcm16(ReadOnlySpan<byte> data)
    {
        var samples = new short[data.Length / 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(i * 2)..]);

        return samples;
    }

    private static short[] WidenPcm24(ReadOnlySpan<byte> data)
    {
        var samples = new short[data.Length / 3];
        for (int i = 0; i < samples.Length; i++)
        {
            int at = i * 3;

            // Sign-extended by placing the three bytes in the TOP of an int and
            // shifting back down arithmetically. Assembling them in the low bytes
            // and masking would make every negative sample a large positive one.
            int value = (data[at] << 8) | (data[at + 1] << 16) | (data[at + 2] << 24);
            samples[i] = (short)(value >> 16);
        }

        return samples;
    }

    private static short[] WidenPcm32(ReadOnlySpan<byte> data)
    {
        var samples = new short[data.Length / 4];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)(BinaryPrimitives.ReadInt32LittleEndian(data[(i * 4)..]) >> 16);

        return samples;
    }

    private static short[] WidenFloat32(ReadOnlySpan<byte> data)
    {
        var samples = new short[data.Length / 4];
        for (int i = 0; i < samples.Length; i++)
        {
            float value = BinaryPrimitives.ReadSingleLittleEndian(data[(i * 4)..]);

            // NaN maps to silence rather than to whatever a cast produces, and
            // the clamp is what stops a mastering chain's overshoot from wrapping
            // to full-scale opposite polarity - which is not quiet distortion, it
            // is a bang.
            if (float.IsNaN(value)) value = 0f;
            double scaled = Math.Clamp(value, -1.0, 1.0) * short.MaxValue;
            samples[i] = (short)Math.Round(scaled, MidpointRounding.AwayFromZero);
        }

        return samples;
    }

    // The FIRST forward loop, and only that. LoopRegion is one region, so a file
    // with several is expressing something the runtime cannot play; taking the
    // first is the only answer that does not silently pick a different one from
    // run to run.
    private static bool TryReadLoop(ReadOnlySpan<byte> body, out long start, out long end, out bool refused)
    {
        start = 0;
        end = 0;
        refused = false;

        if (body.Length < 36) return false;

        uint loopCount = BinaryPrimitives.ReadUInt32LittleEndian(body[28..]);
        if (loopCount == 0) return false;

        for (uint i = 0; i < loopCount; i++)
        {
            int at = 36 + (int)i * 24;
            if (at + 24 > body.Length) break;

            uint type = BinaryPrimitives.ReadUInt32LittleEndian(body[(at + 4)..]);
            if (type != LoopTypeForward)
            {
                // Alternating and backward loops are dropped rather than played
                // forward, and said out loud by the rule: a ping-pong loop played
                // one way is a sound that is merely wrong, and the author has no
                // way to tell from listening that the engine ignored half of what
                // they asked for.
                refused = true;
                continue;
            }

            start = BinaryPrimitives.ReadUInt32LittleEndian(body[(at + 8)..]);
            end = BinaryPrimitives.ReadUInt32LittleEndian(body[(at + 12)..]);
            refused = false;
            return true;
        }

        return false;
    }

    private static bool Matches(ReadOnlySpan<byte> file, int at, string fourcc) =>
        file.Length >= at + 4 &&
        file[at] == fourcc[0] && file[at + 1] == fourcc[1] &&
        file[at + 2] == fourcc[2] && file[at + 3] == fourcc[3];

    private static string Fourcc(ReadOnlySpan<byte> file, int at)
    {
        Span<char> text = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            byte value = file[at + i];
            text[i] = value is >= 0x20 and < 0x7F ? (char)value : '?';
        }

        return new string(text);
    }

    private static InvalidDataException Refuse(string origin, string because) =>
        new($"'{origin}' is not a WAV this cooker can read: {because}");
}
