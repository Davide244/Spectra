using SpectraEngine.Core.Graphics;
using StbImageSharp;
using System;
using System.Buffers;
using System.IO;
using System.Threading;

namespace SpectraEngine.Core.Assets;

/// <summary>
/// Decodes image files (PNG/JPG/TGA/BMP) into <see cref="DecodedImage"/>.
/// </summary>
/// <remarks>
/// Pure CPU and free of GPU state, so every member here may be called from any
/// thread — the async asset path runs it on the thread pool. StbImageSharp is
/// fully managed (no native library, no reflection), which keeps the engine's
/// AOT constraint intact.
/// </remarks>
public static class ImageDecoder
{
    // How long an editor may plausibly hold a write lock on a file it is saving.
    private const int ReadRetryDelayMs = 20;
    private const int ReadAttempts = 3;

    /// <summary>
    /// Reads and decodes the file at <paramref name="absolutePath"/>. Any thread.
    /// </summary>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="InvalidDataException">The bytes are not a supported image.</exception>
    public static DecodedImage DecodeFile(string absolutePath)
        => Decode(ReadAllBytesWithRetry(absolutePath), absolutePath);

    /// <summary>
    /// Decodes an in-memory image file. <paramref name="originForErrors"/> only
    /// labels exception messages. Any thread.
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes are not a supported image.</exception>
    public static DecodedImage Decode(byte[] fileBytes, string originForErrors = "<memory>")
    {
        ArgumentNullException.ThrowIfNull(fileBytes);

        // ColorComponents.Default keeps the file's own channel count, so a
        // single-channel mask does not silently cost 4x the VRAM of an R8 upload.
        ImageResult result;
        try
        {
            result = ImageResult.FromMemory(fileBytes, ColorComponents.Default);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Could not decode image '{originForErrors}': {ex.Message}", ex);
        }

        if (result is null || result.Data is null)
            throw new InvalidDataException($"Could not decode image '{originForErrors}': no pixel data.");

        int channels = (int)result.Comp;
        TextureFormat format;
        switch (result.Comp)
        {
            case ColorComponents.Grey:
                format = TextureFormat.R8;
                break;
            case ColorComponents.RedGreenBlue:
                format = TextureFormat.Rgb8;
                break;
            case ColorComponents.RedGreenBlueAlpha:
                format = TextureFormat.Rgba8;
                break;
            default:
                // Grey+alpha has no GPU format in TextureFormat; re-decode as
                // RGBA rather than dropping the alpha channel on the floor.
                result = ImageResult.FromMemory(fileBytes, ColorComponents.RedGreenBlueAlpha);
                channels = 4;
                format = TextureFormat.Rgba8;
                break;
        }

        byte[] pixels = result.Data;
        // Image files store rows top-down; the renderer's upload contract is
        // bottom-up. The flip is done here rather than through stb's
        // stbi_set_flip_vertically_on_load, which is process-global state and
        // would race between concurrent background decodes.
        FlipRowsInPlace(pixels, result.Width, result.Height, channels);

        return new DecodedImage(pixels, result.Width, result.Height, channels, format);
    }

    // Swaps row i with row (height - 1 - i) through a pooled scratch row; the
    // buffer is large (a 2K RGBA texture is 16 MB), so no extra copy of it.
    private static void FlipRowsInPlace(byte[] pixels, int width, int height, int channels)
    {
        int stride = width * channels;
        if (stride == 0 || height < 2) return;

        byte[] scratch = ArrayPool<byte>.Shared.Rent(stride);
        try
        {
            Span<byte> row = scratch.AsSpan(0, stride);
            for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
            {
                Span<byte> topRow = pixels.AsSpan(top * stride, stride);
                Span<byte> bottomRow = pixels.AsSpan(bottom * stride, stride);
                topRow.CopyTo(row);
                bottomRow.CopyTo(topRow);
                row.CopyTo(bottomRow);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    // Mirrors ShaderHotReloader's read-with-retry: a hot-reload fires while the
    // editor still holds the file open, and one transient sharing violation
    // must not drop the reload.
    private static byte[] ReadAllBytesWithRetry(string path)
    {
        for (int attempt = 0; attempt < ReadAttempts; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buffer = new byte[fs.Length];
                fs.ReadExactly(buffer);
                return buffer;
            }
            catch (IOException) when (attempt < ReadAttempts - 1)
            {
                Thread.Sleep(ReadRetryDelayMs);
            }
        }
        throw new IOException($"Could not read '{path}' after {ReadAttempts} attempts.");
    }
}
