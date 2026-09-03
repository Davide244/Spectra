using System;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// The two things every 8-bit RGBA readback has to get right, in one place: the
/// bounds check that runs before a staging resource exists, and the row walk
/// that turns a mapped surface into the engine's picture-space order.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shared rather than repeated, because both failures are unmanaged.</b> A
/// destination too short for the region is a memcpy past the end of a buffer -
/// a corrupted heap, not an exception, surfacing somewhere unrelated much
/// later. And a row walk that assumes a tight pitch shears the picture by a few
/// texels per row on a driver that pads and is perfectly correct on one that
/// does not, which is the worst possible way to find out about it.
/// </para>
/// <para>
/// It lives outside <see cref="Renderer"/> because the D3D12 shared-target read
/// goes through <c>D3D12On11Bridge</c>'s own D3D11 device rather than through a
/// renderer at all, and a second copy of this walk there would be a second
/// place for the flip to be wrong.
/// </para>
/// </remarks>
internal static class PixelReadback
{
    /// <summary>Bytes per texel: 8-bit RGBA, which is every format this reads.</summary>
    internal const int BytesPerPixel = 4;

    /// <summary>How many bytes a <paramref name="width"/> by <paramref name="height"/> readback needs.</summary>
    internal static int ByteCount(int width, int height) => checked(width * height * BytesPerPixel);

    /// <summary>
    /// Refuses a region that leaves the target, or a destination too short for
    /// one, before any backend allocates a staging resource for it.
    /// </summary>
    internal static void ValidateRegion(
        RenderTarget target, int x, int y, int width, int height, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(target);
        ValidateSize(width, height, destination);

        if (x < 0 || y < 0 || x + width > target.Width || y + height > target.Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(x),
                $"The region {width}x{height} at ({x}, {y}) leaves a {target.Width}x{target.Height} target.");
        }
    }

    /// <summary>The target-free half of <see cref="ValidateRegion"/>, for a surface that is not one.</summary>
    internal static void ValidateSize(int width, int height, Span<byte> destination)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width), $"A readback needs a positive region; got {width}x{height}.");
        }

        int needed = ByteCount(width, height);
        if (destination.Length < needed)
        {
            throw new ArgumentException(
                $"A {width}x{height} readback needs {needed} bytes; the destination holds {destination.Length}.",
                nameof(destination));
        }
    }

    /// <summary>
    /// Copies a mapped D3D surface into <paramref name="destination"/> in
    /// picture-space row order: source row 0 is the TOP of the picture and
    /// becomes the LAST row of the destination.
    /// </summary>
    /// <param name="source">The mapped surface's first byte.</param>
    /// <param name="sourceRowPitch">Bytes between one source row and the next, from the map.</param>
    /// <param name="width">Region width in texels.</param>
    /// <param name="height">Region height in texels.</param>
    /// <param name="destination">Receives <c>width * height * 4</c> bytes, rows bottom-first.</param>
    internal static unsafe void CopyRowsBottomFirst(
        byte* source, uint sourceRowPitch, int width, int height, Span<byte> destination)
    {
        int rowBytes = width * BytesPerPixel;
        for (int row = 0; row < height; row++)
        {
            var sourceRow = new ReadOnlySpan<byte>(source + ((nuint)row * sourceRowPitch), rowBytes);
            sourceRow.CopyTo(destination.Slice((height - 1 - row) * rowBytes, rowBytes));
        }
    }
}
