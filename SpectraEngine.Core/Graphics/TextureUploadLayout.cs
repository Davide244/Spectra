using System;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// The payload rearrangements the backends share on the way from a
/// <see cref="TextureUploadDesc"/> to a GPU resource.
/// </summary>
/// <remarks>
/// Three backends and one set of rules. Each of these was written twice before
/// this file existed - the RGB expansion on both D3D backends, the tight repack
/// nowhere at all because nothing had a padded pitch yet - and the second copy
/// is where a fix does not land.
/// </remarks>
internal static class TextureUploadLayout
{
    /// <summary>The bytes one level actually occupies when its rows are packed tight.</summary>
    internal static int TightLevelSize(TextureFormat format, in TextureMipDesc mip) =>
        TextureFormatInfo.RowCount(format, mip.Height) * TextureFormatInfo.TightRowPitch(format, mip.Width);

    /// <summary>
    /// One level's bytes with no padding between rows, copying only when the
    /// declared pitch is not already the tight one.
    /// </summary>
    /// <remarks>
    /// <b>OpenGL has nowhere to put a padded pitch for a compressed level.</b>
    /// <c>glCompressedTexImage2D</c> takes a byte COUNT rather than a stride,
    /// and the unpack state that would express one
    /// (<c>GL_UNPACK_COMPRESSED_BLOCK_*</c>) is a different mechanism per
    /// format that no other path here uses. Repacking is one rule for every
    /// format and every level, and it costs nothing in the common case: a
    /// tightly written file returns a slice of the payload with no copy at all.
    /// </remarks>
    internal static ReadOnlySpan<byte> TightLevel(
        ReadOnlySpan<byte> payload, TextureFormat format, in TextureMipDesc mip, out byte[]? repacked)
    {
        int tightPitch = TextureFormatInfo.TightRowPitch(format, mip.Width);
        int rows = TextureFormatInfo.RowCount(format, mip.Height);

        if (mip.RowPitch == tightPitch)
        {
            repacked = null;
            return payload.Slice(mip.Offset, tightPitch * rows);
        }

        repacked = new byte[tightPitch * rows];
        for (int row = 0; row < rows; row++)
        {
            payload.Slice(mip.Offset + row * mip.RowPitch, tightPitch)
                .CopyTo(repacked.AsSpan(row * tightPitch, tightPitch));
        }
        return repacked;
    }

    /// <summary>
    /// Rewrites an <see cref="TextureFormat.Rgb8"/> payload as RGBA8, every
    /// level tightly packed into ONE buffer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No API here has a 24-bit texture format, so this is not an optimisation
    /// that could be skipped. One contiguous buffer rather than an array per
    /// level so the whole upload still pins under a single <c>fixed</c>: D3D11
    /// wants a pointer per subresource and D3D12 wants one base pointer, and
    /// pinning a dozen separate arrays to serve either is a dozen chances to
    /// let one go early.
    /// </para>
    /// <para>
    /// Alpha is filled with 255. An RGB image has no coverage information, and
    /// leaving the channel at zero makes every texel fully transparent, which
    /// renders as nothing at all on any surface that reads alpha.
    /// </para>
    /// </remarks>
    internal static byte[] ExpandRgbToRgba(
        ReadOnlySpan<byte> payload, ReadOnlySpan<TextureMipDesc> mips, out TextureMipDesc[] expandedMips)
    {
        expandedMips = new TextureMipDesc[mips.Length];

        int total = 0;
        for (int level = 0; level < mips.Length; level++)
        {
            TextureMipDesc mip = mips[level];
            int pitch = mip.Width * 4;
            expandedMips[level] = new TextureMipDesc(mip.Width, mip.Height, total, pitch);
            total += pitch * mip.Height;
        }

        var expanded = new byte[total];
        for (int level = 0; level < mips.Length; level++)
        {
            TextureMipDesc source = mips[level];
            TextureMipDesc destination = expandedMips[level];
            for (int y = 0; y < source.Height; y++)
            {
                int sourceRow = source.Offset + y * source.RowPitch;
                int destinationRow = destination.Offset + y * destination.RowPitch;
                for (int x = 0; x < source.Width; x++)
                {
                    expanded[destinationRow + x * 4 + 0] = payload[sourceRow + x * 3 + 0];
                    expanded[destinationRow + x * 4 + 1] = payload[sourceRow + x * 3 + 1];
                    expanded[destinationRow + x * 4 + 2] = payload[sourceRow + x * 3 + 2];
                    expanded[destinationRow + x * 4 + 3] = 255;
                }
            }
        }

        return expanded;
    }

    /// <summary>
    /// Packs a software-built mip chain into one buffer with tight pitches, so
    /// it can be uploaded through the same per-mip path a cooked chain takes.
    /// </summary>
    internal static byte[] Flatten(
        TextureFormat format,
        System.Collections.Generic.IReadOnlyList<(byte[] Pixels, int Width, int Height)> levels,
        out TextureMipDesc[] mips)
    {
        mips = new TextureMipDesc[levels.Count];

        int total = 0;
        for (int level = 0; level < levels.Count; level++)
        {
            (_, int width, int height) = levels[level];
            int pitch = TextureFormatInfo.TightRowPitch(format, width);
            mips[level] = new TextureMipDesc(width, height, total, pitch);
            total += pitch * TextureFormatInfo.RowCount(format, height);
        }

        var packed = new byte[total];
        for (int level = 0; level < levels.Count; level++)
            levels[level].Pixels.CopyTo(packed.AsSpan(mips[level].Offset));

        return packed;
    }
}
