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
