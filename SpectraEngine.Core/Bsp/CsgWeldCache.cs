using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// Reused/welded counters for one compile's per-cell weld stage:
/// <see cref="Reused"/> brushes kept their previous snapped+welded surface
/// array verbatim, <see cref="Welded"/> were snapped and welded fresh. A first
/// compile (no previous weld cache) reports all welded.
/// </summary>
public readonly record struct CsgWeldStats(int Reused, int Welded)
{
    /// <summary>Total brushes the weld stage processed (reused + welded).</summary>
    public int Total => Reused + Welded;
}

/// <summary>
/// Per-brush snap+weld results from one static-world compile, consumable by
/// the next: each brush's carved surface array (keyed by reference identity)
/// maps to the snapped+welded surfaces the per-cell weld produced for it,
/// together with the carved surface arrays of every candidate brush that weld
/// could have drawn vertices from. When a later compile reproduces all of
/// those references, it reuses the welded surfaces without re-welding — the
/// weld-stage analogue of <see cref="CsgCompileCache"/>, and the piece that
/// keeps a one-brush edit from re-welding the whole world.
/// </summary>
/// <remarks>
/// <b>Why reference identity is the right key.</b> A fresh carve always
/// allocates a new surface array, so the only way the current compile's carved
/// array for a brush can BE a cached key is an unbroken chain of
/// <see cref="CsgCompileCache"/> hits — and a carve-cache hit has already
/// validated, bitwise, that the brush, its placement matrix, and its ordered
/// carver sequence are unchanged. One reference comparison therefore inherits
/// the carve cache's whole equality contract for free. (Two placements can
/// never share one carved array: both would have to hit the same carve-cache
/// entry in one compile, which the entry's per-carver precedence flags make
/// mutually exclusive — identically-placed duplicates carve each other with
/// opposite precedence.)
///
/// <para>
/// <b>The equality contract</b> (a reuse requires ALL of):
/// <list type="bullet">
/// <item><description><b>Carve identity:</b> the same carved surface array
/// reference — same brush, placement, and carver sequence, per the above, and
/// so the same snapped weld input (snapping is per-vertex and
/// deterministic).</description></item>
/// <item><description><b>Candidate identity:</b> the same number of candidate
/// brushes and, element-wise in ascending placement-index order, the same
/// carved surface array reference for each. The candidate set (see
/// <see cref="ChunkWelder"/>) provably contains every vertex within weld
/// tolerance of the brush's edges, so identical candidate arrays mean an
/// identical passing vertex set at every edge — and the weld output is a pure
/// function of exactly that set. Index SHIFTS between compiles are harmless
/// (relative order is compared, not indices); any brush entering or leaving
/// the candidate set, moving, or re-carving changes the list's length or an
/// element and forces a fresh weld.</description></item>
/// </list>
/// As with the carve cache, every ambiguity resolves toward a miss: a false
/// miss is wasted work, a false hit would be silent stale geometry.
/// </para>
/// <para>
/// <b>Ownership and threading:</b> immutable after construction; a compile
/// consumes a previous cache read-only and produces a fresh one rebuilt from
/// the current placements only (entries whose brush left the world are pruned
/// by construction). Like the carve cache it retains the previous compile's
/// welded surfaces for as long as it lives — the memory price of making the
/// next edit cheap.
/// </para>
/// </remarks>
public sealed class CsgWeldCache
{
    private readonly Dictionary<Polygon[], Entry> _entries;

    private CsgWeldCache(Dictionary<Polygon[], Entry> entries) => _entries = entries;

    /// <summary>Number of brushes with a cached weld result.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Looks up the cached weld for the brush whose current carved surfaces
    /// are <paramref name="carvedSurfaces"/> — the carve-identity half of the
    /// equality contract. The caller must still validate the candidate half
    /// via <see cref="CandidatesMatch"/> before reusing the entry (split so
    /// <c>ChunkWelder</c> can memoize that check across the many brushes that
    /// share one candidate set — dense cells would otherwise pay it
    /// quadratically).
    /// </summary>
    internal bool TryGet(Polygon[] carvedSurfaces, [NotNullWhen(true)] out Entry? entry) =>
        _entries.TryGetValue(carvedSurfaces, out entry);

    /// <summary>
    /// The candidate-identity half of the equality contract (see the class
    /// remarks): element-wise reference equality between the entry's recorded
    /// candidate carve arrays and the current compile's, in the shared
    /// ascending order. Pure — safe to memoize per
    /// (<see cref="Entry.CandidateCarves"/>, <paramref name="candidateIndices"/>)
    /// reference pair.
    /// </summary>
    internal static bool CandidatesMatch(Entry entry, int[] candidateIndices, Polygon[][] perBrushSurfaces)
    {
        Polygon[][] records = entry.CandidateCarves;
        if (records.Length != candidateIndices.Length)
            return false;

        for (int k = 0; k < records.Length; k++)
        {
            if (!ReferenceEquals(records[k], perBrushSurfaces[candidateIndices[k]]))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Builds the cache one compile produces: one entry per placement, keyed
    /// by that placement's carved surface array. Entries are exactly the
    /// current compile's outputs, so brushes that left the placement set since
    /// the previous compile are pruned by construction.
    /// </summary>
    internal static CsgWeldCache FromEntries(Polygon[][] perBrushSurfaces, Entry[] entries)
    {
        // Explicit reference comparer: array equality happens to be reference
        // equality by default, but this cache's contract REQUIRES it, so say
        // so. TryAdd is pure paranoia — distinct placements cannot share a
        // carved array (see the class remarks) — mirroring the carve cache's
        // deterministic first-placement-wins stance.
        var map = new Dictionary<Polygon[], Entry>(entries.Length, ReferenceEqualityComparer.Instance);
        for (int i = 0; i < entries.Length; i++)
            map.TryAdd(perBrushSurfaces[i], entries[i]);
        return new CsgWeldCache(map);
    }

    /// <summary>
    /// The cached weld of one brush: the carved candidate arrays the weld
    /// could have drawn vertices from (in ascending candidate order — the
    /// order <see cref="CandidatesMatch"/> compares in; SHARED between every
    /// entry with the same candidate set, so a dense cell stores its
    /// candidate list once, not once per brush) and the snapped+welded
    /// surfaces the weld produced. Immutable after construction; the welded
    /// array is shared verbatim between the cache and every compile result
    /// that reused it (safe — <see cref="Polygon"/> is immutable and
    /// downstream phases never mutate their input arrays).
    /// </summary>
    internal sealed class Entry(Polygon[][] candidateCarves, Polygon[] welded)
    {
        public readonly Polygon[][] CandidateCarves = candidateCarves;
        public readonly Polygon[] Welded = welded;
    }
}
