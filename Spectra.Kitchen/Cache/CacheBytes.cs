using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Spectra.Kitchen.Cache;

/// <summary>
/// The little-endian, length-prefixed primitives both cache files are written
/// with.
/// </summary>
/// <remarks>
/// <b>One implementation, because two would drift and the drift is silent.</b> A
/// cache file that a slightly different reader parses to slightly different
/// numbers does not fail: it produces a key that never matches, which reads as a
/// cache that simply does not work.
/// </remarks>
internal static class CacheBytes
{
    public static void U32(List<byte> into, uint value)
    {
        Span<byte> span = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        into.AddRange(span);
    }

    public static void U64(List<byte> into, ulong value)
    {
        Span<byte> span = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(span, value);
        into.AddRange(span);
    }

    // Two explicit halves rather than a reinterpret of the struct, so the file is
    // the same on a big-endian machine instead of being silently reversed there.
    public static void U128(List<byte> into, UInt128 value)
    {
        U64(into, (ulong)value);
        U64(into, (ulong)(value >> 64));
    }

    public static void Str(List<byte> into, string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        U32(into, (uint)utf8.Length);
        into.AddRange(utf8);
    }
}

/// <summary>
/// A bounds-checked cursor over a cache file's bytes.
/// </summary>
/// <remarks>
/// Every overrun throws <see cref="InvalidDataException"/>, which both cache files
/// catch and answer by starting empty. A cache is derived data: the only correct
/// response to one that does not parse is to rebuild it, never to fail the cook
/// that found it.
/// </remarks>
internal ref struct CacheReader(ReadOnlySpan<byte> bytes)
{
    private readonly ReadOnlySpan<byte> _bytes = bytes;
    private int _at;

    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(sizeof(uint)));

    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(sizeof(ulong)));

    public UInt128 U128()
    {
        ulong low = U64();
        ulong high = U64();
        return ((UInt128)high << 64) | low;
    }

    public byte U8() => Take(1)[0];

    public string Str()
    {
        uint count = U32();
        return count == 0 ? string.Empty : Encoding.UTF8.GetString(Take(checked((int)count)));
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || _at + count > _bytes.Length)
            throw new InvalidDataException("Cache file ends inside a record.");

        ReadOnlySpan<byte> span = _bytes.Slice(_at, count);
        _at += count;
        return span;
    }
}
