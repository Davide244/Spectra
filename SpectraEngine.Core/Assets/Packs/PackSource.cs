using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Assets.Sources;
using System;
using System.Diagnostics.CodeAnalysis;

namespace SpectraEngine.Core.Assets.Packs;

/// <summary>
/// A mounted <c>.spack</c> read straight out of a memory-mapped view: an
/// uncompressed entry is handed to the caller as a span into the file, with no
/// copy and no decode between the two.
/// </summary>
/// <remarks>
/// <para><b>The whole file is mapped once, at mount.</b> Per-entry views are the
/// obvious alternative and they do not work: on Windows a view's offset must be a
/// multiple of the 64 KB allocation granularity rather than the 4 KB page size,
/// so per-entry mapping needs either absurd 64 KB payload alignment or an
/// offset-modulo dance at every read, and that is discovered the hard way. One
/// view costs address space rather than RAM, and both targets are 64-bit.</para>
/// <para><b>Lookup is allocation-free.</b> The entry table is reinterpreted in
/// place as <c>ReadOnlySpan&lt;PackEntry&gt;</c> and binary-searched on the
/// unsigned 128-bit id; nothing is parsed, no dictionary is built and no string is
/// materialised on the path a frame takes.</para>
/// <para><b>Every blob it hands out holds a <see cref="PackHandle"/>
/// reference</b>, including a decompressed one. Unmapping under a live span is an
/// access violation with no managed stack, so the reference travels with the blob
/// rather than with the call that opened it.</para>
/// </remarks>
public sealed class PackSource : PackSourceBase
{
    private ulong _entryTableOffset;
    private int _entryTableBytes;
    private ulong _nameTableOffset;
    private int _nameTableBytes;
    private bool _tablesLoaded;

    /// <summary>
    /// Mounts <paramref name="packPath"/>, validating it before it can answer
    /// anything.
    /// </summary>
    /// <exception cref="PackMountException">The pack is refused.</exception>
    public PackSource(ILogger logger, string packPath, int priority = PackMountBand.Base)
        : base(logger, packPath, priority, PackHandle.MapWholeFile(packPath))
    {
        try
        {
            Mount();
        }
        catch
        {
            // A constructor that threw produced no object for anybody to dispose,
            // so the view it already created has to be unmapped here or it is
            // leaked for the process's life.
            Handle.RequestUnmount();
            throw;
        }
    }

    /// <inheritdoc/>
    protected override long FileLength => Handle.RegionLength;

    /// <inheritdoc/>
    protected override ReadOnlySpan<PackEntry> Entries =>
        _tablesLoaded ? PackEntryTable.Cast(Handle.Slice(_entryTableOffset, _entryTableBytes)) : default;

    /// <inheritdoc/>
    protected override ReadOnlySpan<byte> NameTable =>
        _tablesLoaded && _nameTableBytes > 0 ? Handle.Slice(_nameTableOffset, _nameTableBytes) : default;

    /// <inheritdoc/>
    protected override void ReadRaw(long offset, Span<byte> destination) =>
        Handle.Slice((ulong)offset, destination.Length).CopyTo(destination);

    /// <inheritdoc/>
    protected override void LoadTables(in PackHeader header)
    {
        _entryTableOffset = header.EntryTableOffset;
        _entryTableBytes = (int)((long)header.EntryCount * PackFormat.EntrySize);

        if (header.HasNameTable)
        {
            _nameTableOffset = header.NameTableOffset;
            _nameTableBytes = (int)header.NameTableLength;
        }

        _tablesLoaded = true;
    }

    /// <inheritdoc/>
    protected override UInt128 ComputeDigest(long offset, long length)
    {
        // Chunked rather than one span, because a pack may legitimately be larger
        // than a span can address and the mount must not be the thing that caps it.
        const int ChunkSize = 1 << 20;

        var accumulator = new PackDigest.Accumulator();
        long remaining = length;
        long cursor = offset;

        while (remaining > 0)
        {
            int chunk = (int)Math.Min(remaining, ChunkSize);
            accumulator.Append(Handle.Slice((ulong)cursor, chunk));
            cursor += chunk;
            remaining -= chunk;
        }

        return accumulator.Finish();
    }

    /// <inheritdoc/>
    protected override bool TryReadPayload(in PackEntry entry, [NotNullWhen(true)] out ContentBlob? blob)
    {
        blob = null;

        // Taken before anything is read and owned by the blob afterwards: between
        // those two moments nothing else keeps the view alive.
        if (!Handle.TryAddRef()) return false;

        // Tracked rather than inferred from blob being null, because the failed
        // inflate below disposes a blob that has ALREADY taken the reference, and
        // a finally that released it again would be an over-release.
        bool referenceHandedOver = false;
        try
        {
            switch (entry.EntryCodec)
            {
                case PackCodec.None:
                    blob = ContentBlob.OverPack(Handle, entry.PayloadOffset, (int)entry.StoredSize);
                    referenceHandedOver = true;
                    return true;

                case PackCodec.Deflate:
                {
                    ContentBlob inflated = ContentBlob.RentUnderPack(
                        Handle, (int)entry.UncompressedSize, out Span<byte> destination);
                    referenceHandedOver = true;

                    try
                    {
                        Inflate(Handle.Slice(entry.PayloadOffset, (int)entry.StoredSize), destination, PackPath);
                    }
                    catch
                    {
                        inflated.Dispose();
                        throw;
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
}
