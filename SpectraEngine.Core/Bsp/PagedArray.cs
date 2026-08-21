using System;
using System.Collections;
using System.Collections.Generic;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// An immutable, page-structured array of <typeparamref name="T"/> supporting
/// O(pages touched) copy-on-write replacement — the storage that keeps the
/// incremental static-world compile's per-placement carry (carved surfaces,
/// welded surfaces, carve neighbours, weld candidates) from costing an
/// O(world) array copy on every one-brush edit (open-world pillar). A derived
/// instance shares every untouched page with its ancestor; only pages
/// containing a replaced slot are cloned, plus the page table itself
/// (<c>count / PageSize</c> references — bytes, not megabytes, at any world
/// size).
/// </summary>
/// <remarks>
/// Immutable after construction, like every other compiled CSG artifact:
/// sharing pages between the previous world's carry and the next one is safe
/// because neither can write. Also serves as the read-only backing of the
/// world's ordered per-cell lists (<c>ChunkGrid.OrderedChunks</c>,
/// <c>CsgWorld.ChunkMeshes</c>), hence the <see cref="IReadOnlyList{T}"/>
/// face for their public exposure.
/// </remarks>
internal sealed class PagedArray<T> : IReadOnlyList<T>
{
    // 1024 slots per page: small enough that an edit's handful of touched
    // slots clones kilobytes, large enough that the page table stays tiny
    // (49 pages at 50k placements).
    private const int PageShift = 10;
    private const int PageSize = 1 << PageShift;
    private const int PageMask = PageSize - 1;

    private readonly T[][] _pages;

    private PagedArray(T[][] pages, int count)
    {
        _pages = pages;
        Count = count;
    }

    /// <summary>Number of slots.</summary>
    public int Count { get; }

    /// <summary>Reads slot <paramref name="index"/>.</summary>
    public T this[int index] => _pages[index >> PageShift][index & PageMask];

    /// <summary>
    /// Packs a flat array into pages. O(n) — used by the full compile path,
    /// where an O(n) copy is noise next to the compile itself.
    /// </summary>
    public static PagedArray<T> From(IReadOnlyList<T> source)
    {
        int count = source.Count;
        var pages = new T[(count + PageSize - 1) >> PageShift][];
        for (int p = 0; p < pages.Length; p++)
        {
            int start = p << PageShift;
            var page = new T[Math.Min(PageSize, count - start)];
            for (int i = 0; i < page.Length; i++)
                page[i] = source[start + i];
            pages[p] = page;
        }
        return new PagedArray<T>(pages, count);
    }

    /// <summary>
    /// Derives a copy with the given slots replaced, cloning only the pages
    /// those slots live in (plus the page table). <paramref name="replacements"/>
    /// slot indices must be valid; duplicate indices are allowed (last write
    /// wins, though callers never produce duplicates).
    /// </summary>
    public PagedArray<T> WithReplacements(IReadOnlyList<(int Index, T Value)> replacements)
    {
        var pages = (T[][])_pages.Clone();
        foreach ((int index, T value) in replacements)
        {
            int p = index >> PageShift;
            // Clone-on-first-touch: reference equality with the ancestor's
            // page marks "not yet cloned in this derivation".
            if (ReferenceEquals(pages[p], _pages[p]))
                pages[p] = (T[])_pages[p].Clone();
            pages[p][index & PageMask] = value;
        }
        return new PagedArray<T>(pages, Count);
    }

    /// <summary>
    /// Copies <paramref name="length"/> slots starting at
    /// <paramref name="sourceIndex"/> into <paramref name="destination"/> —
    /// one <see cref="Array.Copy(Array, int, Array, int, int)"/> per touched
    /// page, for the rare splices that must re-pack (cell insertions or
    /// removals in an ordered list).
    /// </summary>
    public void CopyTo(int sourceIndex, T[] destination, int destinationIndex, int length)
    {
        while (length > 0)
        {
            int page = sourceIndex >> PageShift;
            int offset = sourceIndex & PageMask;
            int run = Math.Min(length, PageSize - offset);
            Array.Copy(_pages[page], offset, destination, destinationIndex, run);
            sourceIndex += run;
            destinationIndex += run;
            length -= run;
        }
    }

    public IEnumerator<T> GetEnumerator()
    {
        foreach (T[] page in _pages)
        {
            foreach (T item in page)
                yield return item;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
