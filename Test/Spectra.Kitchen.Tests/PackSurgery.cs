using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// Damages a written pack in one named way, so a verifier has something to find.
/// </summary>
/// <remarks>
/// <para><b>Every offset here comes from <see cref="HandParsedPack"/>, which
/// takes them from the format spec rather than from the engine's own types.</b>
/// That is the same second-opinion argument one layer on: a fixture that located
/// a payload through <c>PackEntry</c> would move with any field the struct
/// reordered, and the corruption it thinks it planted would land somewhere else
/// while the test still passed.</para>
/// <para><b>Re-stamping the digest is the interesting operation.</b> Any edit to
/// a pack breaks the trailing digest, so without a re-stamp EVERY corruption test
/// is the same test: the digest catches it first and nothing past the mount ever
/// runs. Rewriting the digest over the damage is what separates "a bit rotted on
/// disk" from "these bytes were never valid", which are the two failures the
/// verifier is claimed to tell apart.</para>
/// </remarks>
internal static class PackSurgery
{
    /// <summary>Flips one byte inside the first entry's payload and leaves the digest alone.</summary>
    public static void CorruptFirstPayload(string packPath)
    {
        byte[] bytes = File.ReadAllBytes(packPath);
        HandParsedPack.Header header = HandParsedPack.ReadHeader(bytes);
        List<HandParsedPack.Entry> entries = HandParsedPack.ReadEntries(bytes, header);

        int at = (int)entries[0].PayloadOffset;
        bytes[at] ^= 0xFF;

        File.WriteAllBytes(packPath, bytes);
    }

    /// <summary>Rewrites the trailing digest to a value the file's bytes do not hash to.</summary>
    public static void CorruptDigest(string packPath)
    {
        byte[] bytes = File.ReadAllBytes(packPath);
        BinaryPrimitives.WriteUInt128LittleEndian(
            bytes.AsSpan(bytes.Length - HandParsedPack.DigestSize), UInt128.MaxValue);

        File.WriteAllBytes(packPath, bytes);
    }

    /// <summary>
    /// Makes the first entry's payload something no deflate decoder will take,
    /// then re-stamps the digest so the file passes every check but that one.
    /// </summary>
    /// <remarks>
    /// One byte, and a specific one: <c>0x07</c> is <c>BFINAL = 1</c> followed by
    /// <c>BTYPE = 11</c>, which RFC 1951 reserves and every decoder refuses. An
    /// arbitrary flip would corrupt the stream MOST of the time, which is not a
    /// property to build a test on.
    /// </remarks>
    public static void MakeFirstPayloadUndecodable(string packPath)
    {
        byte[] bytes = File.ReadAllBytes(packPath);
        HandParsedPack.Header header = HandParsedPack.ReadHeader(bytes);
        List<HandParsedPack.Entry> entries = HandParsedPack.ReadEntries(bytes, header);

        bytes[(int)entries[0].PayloadOffset] = 0x07;
        RestampDigest(bytes, header);

        File.WriteAllBytes(packPath, bytes);
    }

    /// <summary>
    /// Swaps two adjacent entry records, then re-stamps the digest: a table that
    /// is intact, internally consistent and no longer searchable.
    /// </summary>
    public static void SwapFirstTwoEntries(string packPath)
    {
        byte[] bytes = File.ReadAllBytes(packPath);
        HandParsedPack.Header header = HandParsedPack.ReadHeader(bytes);

        int first = (int)header.EntryTableOffset;
        int second = first + HandParsedPack.EntrySize;

        byte[] held = bytes[first..second];
        Array.Copy(bytes, second, bytes, first, HandParsedPack.EntrySize);
        Array.Copy(held, 0, bytes, second, HandParsedPack.EntrySize);

        RestampDigest(bytes, header);
        File.WriteAllBytes(packPath, bytes);
    }

    private static void RestampDigest(byte[] bytes, HandParsedPack.Header header)
    {
        ReadOnlySpan<byte> region = HandParsedPack.DigestedRegion(bytes, header);

        // Big-endian in, little-endian out: XxHash128's canonical form is
        // big-endian and the file stores the same value the other way round, so a
        // fixture that skipped the turn would write a digest that never matches
        // and every test using it would pass for the wrong reason.
        Span<byte> canonical = stackalloc byte[HandParsedPack.DigestSize];
        XxHash128.Hash(region, canonical);

        BinaryPrimitives.WriteUInt128LittleEndian(
            bytes.AsSpan(bytes.Length - HandParsedPack.DigestSize),
            BinaryPrimitives.ReadUInt128BigEndian(canonical));
    }
}
