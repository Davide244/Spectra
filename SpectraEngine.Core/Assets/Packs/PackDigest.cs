using System;
using System.Buffers.Binary;
using System.IO.Hashing;

namespace SpectraEngine.Core.Assets.Packs;

/// <summary>
/// The trailing 16-byte content digest: <c>XxHash128</c> over everything from
/// <see cref="PackHeader.EntryTableOffset"/> to end of file, with the digest bytes
/// themselves excluded.
/// </summary>
/// <remarks>
/// <para><b>Say plainly what this is: corruption detection and a dedup/patch-diff
/// key, NOT tamper resistance.</b> XxHash128 is not a cryptographic hash and
/// nothing here is signed, so anyone who can rewrite a payload can rewrite the
/// digest over it. If the threat model ever includes a hostile mod pack, hashing
/// does nothing at all and the answer is signing, which is a different design with
/// key management attached.</para>
/// <para><b>The header is outside the digest on purpose.</b> The digest lives
/// inside the header's own <see cref="PackHeader.TotalFileSize"/> accounting, so
/// covering the header would make the value depend on itself. Truncation is caught
/// by <c>TotalFileSize</c>, which is what that field is for.</para>
/// <para><b>Writer and reader share this one definition</b>, including the
/// incremental form: a writer streams a pack it never holds whole, and a reader
/// may want to verify without materialising a multi-gigabyte region, so the two
/// halves of one hash must not be two implementations.</para>
/// </remarks>
public static class PackDigest
{
    /// <summary>The digest of one contiguous region.</summary>
    public static UInt128 Compute(ReadOnlySpan<byte> region) => XxHash128.HashToUInt128(region);

    /// <summary>
    /// Writes <paramref name="digest"/> into the file's trailing
    /// <see cref="PackFormat.DigestSize"/> bytes.
    /// </summary>
    /// <remarks>
    /// Little-endian, so the tail bytes reinterpreted in place as a
    /// <see cref="UInt128"/> compare equal to a freshly computed one. XxHash's own
    /// canonical byte form is big-endian, which would compare unequal against
    /// exactly that in-place read, so the conversion happens here once rather than
    /// at each of the two call sites.
    /// </remarks>
    public static void Write(Span<byte> destination, UInt128 digest) =>
        BinaryPrimitives.WriteUInt128LittleEndian(destination, digest);

    /// <summary>Reads a digest previously written by <see cref="Write"/>.</summary>
    public static UInt128 Read(ReadOnlySpan<byte> source) =>
        BinaryPrimitives.ReadUInt128LittleEndian(source);

    /// <summary>
    /// Accumulates the digest of a region delivered in pieces, for a producer or
    /// consumer that never has the whole of it in memory at once.
    /// </summary>
    public sealed class Accumulator
    {
        private readonly XxHash128 _hash = new();

        /// <summary>Appends the next piece of the region, in order.</summary>
        public void Append(ReadOnlySpan<byte> bytes) => _hash.Append(bytes);

        /// <summary>
        /// The digest of everything appended so far. Does not reset, so it may be
        /// read and then appended to.
        /// </summary>
        public UInt128 Finish()
        {
            Span<byte> canonical = stackalloc byte[PackFormat.DigestSize];
            _hash.GetCurrentHash(canonical);

            // XxHash128 emits its canonical big-endian form; Compute's UInt128 is
            // the same value read big-endian, so the two agree by construction
            // rather than by the two call sites happening to pick the same order.
            return BinaryPrimitives.ReadUInt128BigEndian(canonical);
        }
    }
}
