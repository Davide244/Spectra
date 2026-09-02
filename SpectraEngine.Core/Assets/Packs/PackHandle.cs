using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace SpectraEngine.Core.Assets.Packs;

/// <summary>
/// The refcounted lifetime of one mounted pack, and the only thing that makes a
/// span into its mapped view legal to hold.
/// </summary>
/// <remarks>
/// <para><b>Unmapping a view while a span into it is alive is an access
/// violation, not an exception</b>: no managed stack, no catch block, no log
/// line, just a dead process. That is why this exists at all, and why it landed
/// with the reader rather than after it. The count starts at one, held by the
/// mount itself; every <see cref="Sources.ContentBlob"/> a pack hands out takes
/// another; <see cref="RequestUnmount"/> drops the mount's own and the storage is
/// released when, and only when, the count reaches zero.</para>
/// <para><b>Every path that carries a span to another thread must hold a
/// reference</b>, the asset manager's upload queue included: a blob decoded on
/// the thread pool and uploaded on the render thread outlives the stack frame
/// that opened it, so the reference travels with the blob rather than with the
/// call.</para>
/// <para><b><see cref="TryAddRef"/> refuses once an unmount has been asked
/// for.</b> Refusing is what makes a deferred unmount finite: if a new reader
/// could still take a reference, a busy pack would never reach zero and the
/// unmount would be indefinitely postponed instead of merely deferred.</para>
/// <para><b>The state transitions take a lock and the reads do not.</b>
/// <see cref="Slice"/> runs whenever content is resolved and only ever reads the
/// base pointer, which the caller's own held reference is what keeps valid; the
/// lock guards the counter, which is touched once per blob rather than once per
/// read.</para>
/// <para><b>There is deliberately no finalizer.</b> A mount is an explicit
/// start-up and shutdown operation, like every other resource the engine owns,
/// and a finalizer here would buy only the case where somebody forgot to unmount
/// — which the process exit already cleans up — while adding a cleanup path that
/// runs on a thread nobody chose.</para>
/// </remarks>
public sealed unsafe class PackHandle
{
    private readonly object _gate = new();

    private MemoryMappedViewAccessor? _view;
    private MemoryMappedFile? _mapping;
    private IDisposable? _storage;
    private byte* _origin;
    private bool _pointerAcquired;

    private int _references = 1;
    private bool _unmountRequested;

    private PackHandle(string name, long regionLength)
    {
        Name = name;
        RegionLength = regionLength;
    }

    /// <summary>What this handle is over, for log lines and exception messages.</summary>
    public string Name { get; }

    /// <summary>Bytes in the mapped region, which is the whole file.</summary>
    public long RegionLength { get; }

    /// <summary>
    /// Whether a mapped view is still present. False for a handle over a stream,
    /// which has no view, and false once the last reference has gone.
    /// </summary>
    public bool IsMapped => _origin is not null;

    /// <summary>References outstanding, the mount's own included.</summary>
    public int ReferenceCount
    {
        get { lock (_gate) return _references; }
    }

    /// <summary>Whether the storage has been released, i.e. the count reached zero.</summary>
    public bool IsReleased
    {
        get { lock (_gate) return _references == 0; }
    }

    /// <summary>
    /// Whether <see cref="RequestUnmount"/> has been called. The storage may
    /// still be alive: an unmount waits for the last reference.
    /// </summary>
    public bool UnmountRequested
    {
        get { lock (_gate) return _unmountRequested; }
    }

    /// <summary>
    /// Maps the whole of <paramref name="path"/> once, read-only.
    /// </summary>
    /// <remarks>
    /// <b>The whole file, never a view per entry.</b> On Windows a view's offset
    /// must be a multiple of the allocation granularity, which is 64 KB rather
    /// than the 4 KB page size, so per-entry views would need either absurd
    /// 64 KB payload alignment or an offset-modulo dance at every read. Mapping
    /// the whole file costs address space rather than RAM, and both targets are
    /// 64-bit.
    /// </remarks>
    internal static PackHandle MapWholeFile(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = stream.Length;

        MemoryMappedFile? mapping = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            // A zero-length file cannot be mapped at all, on any platform, so this
            // runs before the mapping rather than at mount with the other checks.
            PackFormat.RequireMinimumFileSize(path, length);

            mapping = MemoryMappedFile.CreateFromFile(
                stream, mapName: null, capacity: 0, MemoryMappedFileAccess.Read,
                HandleInheritability.None, leaveOpen: false);

            view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            byte* origin = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref origin);

            var handle = new PackHandle(path, length)
            {
                _mapping = mapping,
                _view = view,
                // The view is created at offset 0, so PointerOffset is zero; it is
                // added anyway because that is the contract, and a mapping that
                // ever gains an offset would otherwise read from the wrong place.
                _origin = origin + view.PointerOffset,
                _pointerAcquired = true,
            };

            return handle;
        }
        catch
        {
            view?.Dispose();
            mapping?.Dispose();
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A handle over something other than a mapping: the stream fallback's file,
    /// released when the last reference goes exactly as a view would be.
    /// </summary>
    /// <remarks>
    /// The fallback's blobs are copies and carry no access-violation hazard, and
    /// they hold a reference anyway. One rule ("a blob from a pack holds a
    /// reference") is the rule; two rules that differ by which source answered is
    /// how a call site ends up correct against the one it was tested with.
    /// </remarks>
    internal static PackHandle OverStorage(string name, IDisposable storage, long length)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return new PackHandle(name, length) { _storage = storage };
    }

    /// <summary>
    /// Takes a reference, or answers false because the pack is unmounting or
    /// already gone.
    /// </summary>
    public bool TryAddRef()
    {
        lock (_gate)
        {
            if (_unmountRequested || _references == 0) return false;

            _references++;
            return true;
        }
    }

    /// <summary>Takes a reference, refusing loudly when there is nothing to reference.</summary>
    /// <exception cref="ObjectDisposedException">The pack is unmounting or unmounted.</exception>
    public void AddRef()
    {
        if (TryAddRef()) return;

        throw new ObjectDisposedException(
            nameof(PackHandle), $"Pack '{Name}' is unmounted, so no further reference can be taken.");
    }

    /// <summary>
    /// Drops one reference, releasing the storage when it was the last.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Released more times than referenced, which for a mapped pack is one step
    /// away from a live span over freed address space.
    /// </exception>
    public void Release()
    {
        bool last;
        lock (_gate)
        {
            if (_references == 0)
            {
                throw new InvalidOperationException(
                    $"Pack '{Name}' was released more times than it was referenced.");
            }

            last = --_references == 0;
        }

        if (last) ReleaseStorage();
    }

    /// <summary>
    /// The bytes at <paramref name="offset"/>. Valid only while the caller holds
    /// a reference.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The storage is already gone.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The window leaves the region.</exception>
    public ReadOnlySpan<byte> Slice(ulong offset, int length)
    {
        byte* origin = _origin;
        if (origin is null)
        {
            throw new ObjectDisposedException(
                nameof(PackHandle), $"Pack '{Name}' has no mapped view to read from.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ulong region = (ulong)RegionLength;
        if (offset > region || (ulong)length > region - offset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                $"A window of {length} bytes at {offset} leaves pack '{Name}', which is {RegionLength} bytes.");
        }

        return new ReadOnlySpan<byte>(origin + offset, length);
    }

    /// <summary>
    /// Drops the mount's own reference. The storage goes when the last blob does,
    /// which may be now or may be after a background decode finishes with it.
    /// Idempotent.
    /// </summary>
    internal void RequestUnmount()
    {
        bool last;
        lock (_gate)
        {
            if (_unmountRequested) return;

            _unmountRequested = true;
            last = --_references == 0;
        }

        if (last) ReleaseStorage();
    }

    /// <inheritdoc/>
    public override string ToString() => Name;

    // Reached exactly once, by whichever caller took the count to zero: the
    // counter is under the lock and only one decrement can produce the zero.
    private void ReleaseStorage()
    {
        // Nulled first, so a Slice racing an unmount it should not have raced
        // throws instead of reading address space that is about to be unmapped.
        _origin = null;

        if (_pointerAcquired)
        {
            _pointerAcquired = false;
            _view!.SafeMemoryMappedViewHandle.ReleasePointer();
        }

        _view?.Dispose();
        _view = null;

        // Created with leaveOpen: false, so this closes the FileStream under it.
        _mapping?.Dispose();
        _mapping = null;

        _storage?.Dispose();
        _storage = null;
    }
}
