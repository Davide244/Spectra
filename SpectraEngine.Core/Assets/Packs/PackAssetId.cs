using System;
using System.IO.Hashing;
using System.Text;

namespace SpectraEngine.Core.Assets.Packs;

/// <summary>
/// Turns a content path into the 128-bit key the entry table is sorted and
/// searched by.
/// </summary>
/// <remarks>
/// <para><b>Identity is the normalized content-relative SOURCE path</b>, the exact
/// string <see cref="ContentRoot.NormalizeRelativePath"/> already produces.
/// <c>Textures/wall_brick.png</c> is the key in the editor, where it resolves to a
/// loose PNG, and in the shipped game, where it resolves to a pack entry holding
/// BC7 KTX2 bytes. That single decision is what makes "a bug never reproduces in
/// only one mount mode" structural rather than aspirational, and it is why
/// <see cref="AssetManager"/>'s caches, <c>MaterialParser</c>'s texture paths and
/// every existing <c>.spectramat</c> are untouched by the pack arc.</para>
/// <para><b>The id is case-insensitive, and it has to be.</b> Normalisation
/// settles separators and segments but not case: it is
/// <see cref="StringComparer.OrdinalIgnoreCase"/> on the caches keyed by its
/// output that finishes the job, and a hash cannot take a comparer. Fold nothing
/// and <c>Textures/Wall.png</c> and <c>textures/wall.png</c> are one asset in the
/// editor and two ids in a pack, so a lookup spelled the way the material file
/// spells it misses the entry the cooker wrote and the shipped game shows a
/// magenta placeholder for content that is present. That is exactly the
/// reproduces-in-only-one-mount-mode failure the source-path identity exists to
/// make impossible.</para>
/// <para><b>The fold is ASCII only, deliberately.</b> It is applied to the UTF-8
/// bytes, where every byte of a multi-byte sequence has its high bit set, so no
/// non-ASCII character can be touched by construction. The alternative,
/// <c>ToUpperInvariant</c>, matches the comparer over more of Unicode and makes
/// the id depend on the host's globalization mode and casing tables, which for a
/// cooked artifact means a pack whose keys differ between the machine that wrote
/// it and the machine that reads it, silently. A path differing only in the case
/// of a non-ASCII character therefore gets two ids; that miss is visible and
/// local, and a pack that is unreadable somewhere else is neither.</para>
/// <para><b>Content addressing was considered and rejected as the identity.</b> It
/// makes patch-by-name structurally impossible and every log line unreadable.
/// Dedup, which is content addressing's real benefit, is recovered at cook time
/// instead by pointing duplicate payloads at one extent.</para>
/// <para><b>The hash is not a checksum of the content and never becomes one.</b>
/// It hashes the path, so an id survives a recook that changes every byte of the
/// payload, which is exactly what a patch pack needs.</para>
/// </remarks>
public static class PackAssetId
{
    // Long enough for every real content path; anything past it takes the heap
    // rather than a wrong answer.
    private const int StackLimit = 512;

    /// <summary>
    /// The id of <paramref name="contentPath"/>, normalising it first. Use this
    /// wherever the caller has a path a person or a file wrote.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The path is empty, rooted, or escapes the content root.
    /// </exception>
    public static UInt128 From(string contentPath) =>
        FromNormalized(ContentRoot.NormalizeRelativePath(contentPath));

    /// <summary>
    /// The id of a path that has already been through
    /// <see cref="ContentRoot.NormalizeRelativePath"/>.
    /// </summary>
    /// <remarks>
    /// Normalisation is idempotent, so this is an optimisation rather than a
    /// second meaning of identity. It exists because the asset manager's cache
    /// keys are already normalised and re-normalising per lookup would allocate a
    /// string on a path that runs whenever content is resolved.
    /// </remarks>
    public static UInt128 FromNormalized(string normalizedPath)
    {
        ArgumentNullException.ThrowIfNull(normalizedPath);

        // A pack cooked on one machine has to be searchable by another, so the id
        // is a function of the path's UTF-8 bytes and nothing else: no culture, no
        // encoding preference, no casing table.
        int byteCount = Encoding.UTF8.GetByteCount(normalizedPath);
        if (byteCount <= StackLimit)
        {
            Span<byte> utf8 = stackalloc byte[StackLimit];
            int written = Encoding.UTF8.GetBytes(normalizedPath, utf8);
            Span<byte> key = utf8[..written];
            FoldAsciiCase(key);
            return XxHash128.HashToUInt128(key);
        }

        byte[] heap = Encoding.UTF8.GetBytes(normalizedPath);
        FoldAsciiCase(heap);
        return XxHash128.HashToUInt128(heap);
    }

    // Uppercase, because that is the direction StringComparer.OrdinalIgnoreCase
    // folds internally; either direction would do as long as it is one direction
    // forever, since changing it renumbers every id in every pack ever cooked.
    private static void FoldAsciiCase(Span<byte> utf8)
    {
        for (int i = 0; i < utf8.Length; i++)
        {
            byte b = utf8[i];
            if (b is >= (byte)'a' and <= (byte)'z')
                utf8[i] = (byte)(b - ('a' - 'A'));
        }
    }
}
