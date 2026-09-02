using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using SpectraEngine.Core.Assets.Sources;
using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;

namespace SpectraEngine.Core.Assets.Packs;

/// <summary>
/// The same <c>.spack</c>, read through <see cref="RandomAccess"/> instead of a
/// mapping: the fallback for any platform where mapping misbehaves.
/// </summary>
/// <remarks>
/// <para><b>It answers identically to <see cref="PackSource"/> and that is the
/// whole specification.</b> Both mount through one validation sequence, both
/// binary-search one entry table, both apply one tombstone rule; the only
/// difference is where the bytes come from. A test that opens the same file
/// through both and compares is what keeps that true, because everything about a
/// second reader fails quietly: it does not throw, it hands back different
/// content.</para>
/// <para><b>Its blobs are copies, and they hold a reference anyway.</b> There is
/// no access-violation hazard here, so the reference buys nothing on this path
/// except the property that "a blob from a pack holds a reference" is a rule
/// rather than a rule with an exception, which is what stops a caller being
/// correct only against whichever source it was tested with.</para>
/// <para><b>Reads go through <see cref="RandomAccess"/> on the handle rather than
/// through the stream's own position</b>, because an <see cref="IContentSource"/>
/// is asked for bytes from the render thread, a background decode and a tool
/// thread at once, and a shared file position is exactly the per-call state the
/// contract forbids.</para>
/// </remarks>
public sealed class StreamPackSource : PackSourceBase
{
    private readonly SafeFileHandle _file;
    private readonly long _length;

    private PackEntry[] _entries = [];
    private byte[] _nameTable = [];

    /// <summary>
    /// Mounts <paramref name="packPath"/>, validating it before it can answer
    /// anything.
    /// </summary>
    /// <exception cref="PackMountException">The pack is refused.</exception>
    public StreamPackSource(ILogger logger, string packPath, int priority = PackMountBand.Base)
        : this(logger, packPath, priority, new FileStream(packPath, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
    }

    private StreamPackSource(ILogger logger, string packPath, int priority, FileStream stream)
        : base(logger, packPath, priority, PackHandle.OverStorage(packPath, stream, stream.Length))
    {
        _file = stream.SafeFileHandle;
        _length = stream.Length;

        try
        {
            Mount();
        }
        catch
        {
            // A constructor that threw produced no object for anybody to dispose,
            // so the file it already opened has to be closed here.
            Handle.RequestUnmount();
            throw;
        }
    }

    /// <inheritdoc/>
    protected override long FileLength => _length;

    /// <inheritdoc/>
    protected override ReadOnlySpan<PackEntry> Entries => _entries;

    /// <inheritdoc/>
    protected override ReadOnlySpan<byte> NameTable => _nameTable;

    /// <inheritdoc/>
    protected override void ReadRaw(long offset, Span<byte> destination)
    {
        int filled = 0;
        while (filled < destination.Length)
        {
            int read = RandomAccess.Read(_file, destination[filled..], offset + filled);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"'{PackPath}' ended after {offset + filled} bytes, with {destination.Length - filled} still " +
                    "to read.");
            }

            filled += read;
        }
    }

    /// <inheritdoc/>
    protected override void LoadTables(in PackHeader header)
    {
        // Materialised rather than sliced: there is no view to slice. The table is
        // the one thing this source copies whole, and it is the reason a mapped
        // mount is preferred wherever mapping works.
        _entries = new PackEntry[header.EntryCount];
        if (_entries.Length > 0)
        {
            ReadRaw((long)header.EntryTableOffset, MemoryMarshal.AsBytes(_entries.AsSpan()));
        }

        if (header.HasNameTable)
        {
            _nameTable = new byte[header.NameTableLength];
            ReadRaw((long)header.NameTableOffset, _nameTable);
        }
    }

    /// <inheritdoc/>
    protected override UInt128 ComputeDigest(long offset, long length)
    {
        const int ChunkSize = 1 << 20;

        var accumulator = new PackDigest.Accumulator();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            long remaining = length;
            long cursor = offset;

            while (remaining > 0)
            {
                int chunk = (int)Math.Min(remaining, ChunkSize);
                Span<byte> window = buffer.AsSpan(0, chunk);
                ReadRaw(cursor, window);
                accumulator.Append(window);
                cursor += chunk;
                remaining -= chunk;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return accumulator.Finish();
    }

    /// <inheritdoc/>
    protected override bool TryReadPayload(in PackEntry entry, [NotNullWhen(true)] out ContentBlob? blob)
    {
        blob = null;
        if (!Handle.TryAddRef()) return false;

        // Tracked rather than inferred from blob being null, because a failure
        // below disposes a blob that has ALREADY taken the reference, and a
        // finally that released it again would be an over-release.
        bool referenceHandedOver = false;
        try
        {
            switch (entry.EntryCodec)
            {
                case PackCodec.None:
                {
                    ContentBlob stored = ContentBlob.RentUnderPack(
                        Handle, (int)entry.StoredSize, out Span<byte> destination);
                    referenceHandedOver = true;

                    try
                    {
                        ReadRaw((long)entry.PayloadOffset, destination);
                    }
                    catch
                    {
                        stored.Dispose();
                        throw;
                    }

                    blob = stored;
                    return true;
                }

                case PackCodec.Deflate:
                {
                    ContentBlob inflated = ContentBlob.RentUnderPack(
                        Handle, (int)entry.UncompressedSize, out Span<byte> destination);
                    referenceHandedOver = true;

                    byte[] compressed = ArrayPool<byte>.Shared.Rent((int)entry.StoredSize);
                    try
                    {
                        Span<byte> window = compressed.AsSpan(0, (int)entry.StoredSize);
                        ReadRaw((long)entry.PayloadOffset, window);
                        Inflate(window, destination, PackPath);
                    }
                    catch
                    {
                        inflated.Dispose();
                        throw;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(compressed);
                    }

                    blob = inflated;
                    return true;
                }

                default:
                    Logger.LogWarning(
                        "Entry in {Source} uses codec {Codec}, which this reader does not implement.",
                        this, entry.EntryCodec);
                    return false;
            }
        }
        finally
        {
            if (!referenceHandedOver) Handle.Release();
        }
    }

    /// <inheritdoc/>
    public override string ToString() => $"pack (streamed) @ {PackPath}";
}
