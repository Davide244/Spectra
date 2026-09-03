using SpectraEngine.Core.Graphics;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// Hand-assembled BC7 blocks, and a two-mip fixture built from them.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no block encoder anywhere in this repo and there must not be one
/// here either.</b> Compression is the cooker's job, and a test that shipped its
/// own encoder would be proving that encoder rather than the upload path. What
/// the upload path needs is a payload whose decoded colours are known exactly,
/// which BC7's mode 6 gives for free: one subset, two RGBA endpoints, and any
/// index at all decodes to the endpoint when both endpoints are equal.
/// </para>
/// <para>
/// <b>Mode 6's endpoints are seven bits plus a shared parity bit</b>, so a
/// channel is <c>(value &lt;&lt; 1) | p</c> and every channel of one endpoint has
/// to agree about <c>p</c>. That is why the fixture colours are 255 and 1 rather
/// than 255 and 0: both are odd, so one p-bit serves all four channels and each
/// colour is reproduced to the exact byte.
/// </para>
/// </remarks>
internal static class Bc7Fixture
{
    /// <summary>Bytes in one BC7 block. Not derived here on purpose: the block layout below assumes it.</summary>
    internal const int BlockBytes = 16;

    /// <summary>The "on" channel value. Odd, so it shares a parity bit with <see cref="Off"/>.</summary>
    internal const byte On = 255;

    /// <summary>The "off" channel value. One rather than zero, so the parity bit is shared.</summary>
    internal const byte Off = 1;

    /// <summary>
    /// A mode-6 block that decodes to one flat colour, whatever its indices say.
    /// </summary>
    /// <remarks>
    /// Every channel of <paramref name="r"/>, <paramref name="g"/>,
    /// <paramref name="b"/> and <paramref name="a"/> must share a low bit; that
    /// bit becomes the endpoint's p-bit. Both endpoints are written identically
    /// so the interpolation is the identity and the indices, all zero here,
    /// cannot change the answer.
    /// </remarks>
    internal static byte[] SolidBlock(byte r, byte g, byte b, byte a)
    {
        var block = new byte[BlockBytes];
        int bit = 0;

        void Put(uint value, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (((value >> i) & 1u) != 0)
                    block[bit >> 3] |= (byte)(1 << (bit & 7));
                bit++;
            }
        }

        // The mode is unary: mode 6 is six zeros then a one, which as a 7-bit
        // little-endian field is the value 64.
        Put(0b100_0000u, 7);

        Put((uint)(r >> 1), 7);
        Put((uint)(r >> 1), 7);
        Put((uint)(g >> 1), 7);
        Put((uint)(g >> 1), 7);
        Put((uint)(b >> 1), 7);
        Put((uint)(b >> 1), 7);
        Put((uint)(a >> 1), 7);
        Put((uint)(a >> 1), 7);

        uint parity = (uint)(r & 1);
        Put(parity, 1);
        Put(parity, 1);

        // The 63 index bits stay zero, which selects endpoint 0 everywhere.
        return block;
    }

    /// <summary>The four quadrant colours, named as the picture is seen.</summary>
    internal static byte[] TopLeftBlock => SolidBlock(On, Off, Off, On);

    /// <summary>See <see cref="TopLeftBlock"/>.</summary>
    internal static byte[] TopRightBlock => SolidBlock(Off, On, Off, On);

    /// <summary>See <see cref="TopLeftBlock"/>.</summary>
    internal static byte[] BottomLeftBlock => SolidBlock(Off, Off, On, On);

    /// <summary>See <see cref="TopLeftBlock"/>.</summary>
    internal static byte[] BottomRightBlock => SolidBlock(On, On, Off, On);

    /// <summary>What mip 1 is filled with: bright in all three channels, which no quadrant is.</summary>
    internal static byte[] SecondLevelBlock => SolidBlock(On, On, On, On);

    /// <summary>Width and height of the fixture's base level.</summary>
    internal const int BaseSize = 16;

    /// <summary>Width and height of the fixture's second level.</summary>
    internal const int SecondSize = 8;

    /// <summary>
    /// A two-level BC7 payload: quadrants at 16x16, flat white at 8x8, with the
    /// declared row pitch for each level.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Block row 0 is the BOTTOM of the picture</b>, exactly as texel row 0
    /// is on the uncompressed path, so the authored top-left quadrant is written
    /// into the last block rows. That is the engine's stated convention and the
    /// thing the orientation reading then measures.
    /// </para>
    /// <para>
    /// <paramref name="padded"/> is the fixture's whole point of leverage: at the
    /// tight pitch every backend takes its fast path, and at a padded one each
    /// has to honour a declared stride it did not compute. A cooked file
    /// legitimately carries either, and the picture must not know the difference.
    /// </para>
    /// </remarks>
    internal static byte[] BuildTwoLevelPayload(bool padded, out TextureMipDesc[] mips)
    {
        const int baseBlocks = BaseSize / 4;      // 4 across, 4 down
        const int secondBlocks = SecondSize / 4;  // 2 across, 2 down

        // 256 is D3D12's own copy alignment, so it is the padding a cooked file
        // is most likely to carry and the one most likely to be mistaken for the
        // tight pitch by a backend that assumed.
        int basePitch = padded ? 256 : baseBlocks * BlockBytes;
        int secondPitch = padded ? 256 : secondBlocks * BlockBytes;

        int baseBytes = basePitch * baseBlocks;
        int secondBytes = secondPitch * secondBlocks;

        mips =
        [
            new TextureMipDesc(BaseSize, BaseSize, 0, basePitch),
            new TextureMipDesc(SecondSize, SecondSize, baseBytes, secondPitch),
        ];

        var payload = new byte[baseBytes + secondBytes];

        for (int blockRow = 0; blockRow < baseBlocks; blockRow++)
        {
            bool upperHalf = blockRow >= baseBlocks / 2;
            for (int blockColumn = 0; blockColumn < baseBlocks; blockColumn++)
            {
                bool rightHalf = blockColumn >= baseBlocks / 2;
                byte[] block = upperHalf
                    ? (rightHalf ? TopRightBlock : TopLeftBlock)
                    : (rightHalf ? BottomRightBlock : BottomLeftBlock);
                block.CopyTo(payload.AsSpan(blockRow * basePitch + blockColumn * BlockBytes));
            }
        }

        for (int blockRow = 0; blockRow < secondBlocks; blockRow++)
        {
            for (int blockColumn = 0; blockColumn < secondBlocks; blockColumn++)
            {
                SecondLevelBlock.CopyTo(
                    payload.AsSpan(baseBytes + blockRow * secondPitch + blockColumn * BlockBytes));
            }
        }

        return payload;
    }
}
