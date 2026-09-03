using SpectraEngine.Core.Projects;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// A real project folder in a temporary directory, scaffolded the way the editor
/// scaffolds one.
/// </summary>
/// <remarks>
/// Deliberately a real <see cref="ProjectLayout"/> on a real filesystem rather
/// than an in-memory stand-in: the cook's whole input is a folder, and the parts
/// most likely to be wrong (path normalisation, separator direction, walk order)
/// are exactly the parts a fake would paper over.
/// </remarks>
internal sealed class TempProject : IDisposable
{
    private readonly List<IDisposable> _open = [];

    public TempProject(string name = "TestGame")
    {
        Root = Path.Combine(Path.GetTempPath(), $"spectra_cook_{Guid.NewGuid():N}");
        Layout = ProjectLayout.Create(Root, name);
    }

    /// <summary>The project folder.</summary>
    public string Root { get; }

    /// <summary>The opened project.</summary>
    public ProjectLayout Layout { get; }

    /// <summary>Where a cook writes when nothing overrides it.</summary>
    public string CookedPath => Layout.CookedPath;

    /// <summary>Writes an asset at a content-relative path, creating folders as needed.</summary>
    public byte[] WriteAsset(string contentPath, byte[] bytes)
    {
        string full = Path.Combine(
            Layout.AssetsPath, contentPath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return bytes;
    }

    /// <summary>Writes a text asset.</summary>
    public byte[] WriteAsset(string contentPath, string text) =>
        WriteAsset(contentPath, Encoding.UTF8.GetBytes(text));

    /// <summary>Deterministic bytes, so a comparison failure names a byte rather than a length.</summary>
    public static byte[] Bytes(int length, byte seed = 0)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)((i * 31) + seed);
        return bytes;
    }

    /// <summary>
    /// A real, decodable PNG: 8-bit truecolour, deterministic from
    /// <paramref name="seed"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>A fixture whose extension says PNG has to be one now.</b> Before
    /// <c>ImageRule</c> existed a <c>.png</c> fell through to the raw copy and any
    /// bytes at all would do; a cook now decodes it, and a fixture of random bytes
    /// is a build error rather than a stand-in. Tests whose subject really is the
    /// raw-copy floor name a file with no rule instead.</para>
    /// <para><b>Asymmetric on both axes, for the reason
    /// <c>TextureOrientationProbe</c> gives</b>: a fixture that is the same
    /// picture flipped, mirrored or transposed makes four different bugs look
    /// identical.</para>
    /// </remarks>
    public static byte[] Png(int width = 8, int height = 8, byte seed = 0, int channels = 3)
    {
        // Colour type 0 is greyscale and 2 is truecolour, which is what decides
        // whether the decoder reports one channel or three - and therefore whether
        // the cook chooses BC4 or BC7.
        byte colourType = channels switch
        {
            1 => 0,
            3 => 2,
            _ => throw new ArgumentOutOfRangeException(
                nameof(channels), channels, "This writer emits greyscale or truecolour."),
        };

        var scanlines = new byte[height * (1 + width * channels)];
        for (int y = 0, at = 0; y < height; y++)
        {
            scanlines[at++] = 0;  // filter type 0: none, so the bytes below are the pixels
            for (int x = 0; x < width; x++)
            {
                scanlines[at++] = (byte)(seed + x * 17);
                if (channels == 1) continue;

                scanlines[at++] = (byte)(seed + y * 29);
                scanlines[at++] = (byte)(seed + x * 7 + y * 3);
            }
        }

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(scanlines);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;   // bit depth
        header[9] = colourType;
        header[10] = 0;  // deflate
        header[11] = 0;  // adaptive filtering
        header[12] = 0;  // no interlace

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>
    /// A real, decodable 16-bit PCM WAV, deterministic from
    /// <paramref name="seed"/>, optionally carrying a <c>smpl</c> loop.
    /// </summary>
    /// <remarks>
    /// <para><b>Written from the RIFF specification rather than through
    /// <c>SaudioWriter</c> or any engine type.</b> A cooker checked against a
    /// fixture built by its own code proves the two agree rather than that either
    /// is right, and the failures in this area are all misread buffers rather
    /// than exceptions.</para>
    /// <para><b>The loop arguments are the <c>smpl</c> chunk's own, so
    /// <paramref name="loopEnd"/> is INCLUSIVE</b> - the last frame that plays -
    /// exactly as a DAW writes it. A fixture that quietly used a half-open end
    /// would hide the one conversion this whole lane can get wrong.</para>
    /// <para><b>A sine rather than noise</b>: a resampler's output over random
    /// samples is indistinguishable from its output over anything else, while a
    /// tone stays a tone, so a test that cares whether the audio survived can
    /// look at it.</para>
    /// </remarks>
    public static byte[] Wav(
        int frames = 64,
        int sampleRate = 48_000,
        int channels = 1,
        int seed = 0,
        long loopStart = -1,
        long loopEnd = -1,
        uint loopType = 0)
    {
        var samples = new short[frames * channels];
        for (int frame = 0; frame < frames; frame++)
        {
            for (int channel = 0; channel < channels; channel++)
            {
                // One cycle every 32 frames, and a different phase per channel so
                // a stereo file is not two copies of one signal - which would
                // make a channel swap invisible.
                double phase = (frame + seed + channel * 8) * (2 * Math.PI / 32);
                samples[frame * channels + channel] = (short)(Math.Sin(phase) * 12000);
            }
        }

        byte[] data = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), samples[i]);

        var fmt = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt, 1);                       // WAVE_FORMAT_PCM
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(8), (uint)(sampleRate * channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), (ushort)(channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), 16);

        using var body = new MemoryStream();
        body.Write(Encoding.ASCII.GetBytes("WAVE"));
        WriteRiffChunk(body, "fmt ", fmt);
        WriteRiffChunk(body, "data", data);

        if (loopEnd >= 0)
        {
            var smpl = new byte[36 + 24];
            BinaryPrimitives.WriteUInt32LittleEndian(smpl.AsSpan(28), 1);        // one loop
            BinaryPrimitives.WriteUInt32LittleEndian(smpl.AsSpan(36 + 4), loopType);
            BinaryPrimitives.WriteUInt32LittleEndian(smpl.AsSpan(36 + 8), (uint)Math.Max(0, loopStart));
            BinaryPrimitives.WriteUInt32LittleEndian(smpl.AsSpan(36 + 12), (uint)loopEnd);
            WriteRiffChunk(body, "smpl", smpl);
        }

        byte[] payload = body.ToArray();

        using var file = new MemoryStream();
        file.Write(Encoding.ASCII.GetBytes("RIFF"));

        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)payload.Length);
        file.Write(size);
        file.Write(payload);
        return file.ToArray();
    }

    // Id, size, body, then the pad byte an odd body needs. The pad is NOT counted
    // in the size, and a writer that forgets it produces a file whose next chunk
    // id lands one byte late - which reads as garbage of a plausible size rather
    // than as anything that fails.
    private static void WriteRiffChunk(Stream wav, string id, byte[] body)
    {
        wav.Write(Encoding.ASCII.GetBytes(id));

        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)body.Length);
        wav.Write(size);
        wav.Write(body);

        if ((body.Length & 1) != 0) wav.WriteByte(0);
    }

    // Length, type, data, then a CRC32 over the type AND the data - the one part
    // of the format a hand-written writer usually gets wrong, because a CRC over
    // the data alone produces a file every decoder rejects with no clue why.
    private static void WriteChunk(Stream png, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.Write(length);

        byte[] typed = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type).CopyTo(typed, 0);
        data.CopyTo(typed, 4);
        png.Write(typed);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.HashToUInt32(typed));
        png.Write(crc);
    }

    /// <summary>Registers something to close before the folder is deleted.</summary>
    public T Track<T>(T disposable) where T : IDisposable
    {
        _open.Add(disposable);
        return disposable;
    }

    public void Dispose()
    {
        // Mapped packs first: a mapped view keeps its file open, and a directory
        // holding one cannot be deleted on Windows.
        for (int i = _open.Count - 1; i >= 0; i--) _open[i].Dispose();

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Failing a test on its own cleanup helps nobody; a leaked mapping
            // shows up as the assertion it actually broke.
        }
    }
}
