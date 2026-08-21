using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// Hit/miss counters for one cache-consuming carve: <see cref="Hits"/> brushes
/// reused their previous carve result verbatim, <see cref="Misses"/> were
/// carved fresh. A first compile (no previous cache) reports all misses.
/// </summary>
public readonly record struct CsgCacheStats(int Hits, int Misses)
{
    /// <summary>Total brushes the carve processed (hits + misses).</summary>
    public int Total => Hits + Misses;
}

/// <summary>
/// Per-brush carve results from one static-world compile, consumable by the
/// next: each source <see cref="Brush"/> (keyed by reference identity) maps to
/// the world-space surfaces the carve produced for it, together with every
/// input that carve depended on. When a later compile finds those inputs
/// bitwise-reproduced, it reuses the surfaces without re-carving the brush —
/// turning a one-brush edit from a full re-carve into a re-carve of just the
/// edited brush and its overlap neighbours.
/// </summary>
/// <remarks>
/// <b>Why per-brush reuse is sound.</b> In
/// <see cref="Csg.Carve(IReadOnlyList{BrushPlacement})"/> the surfaces emitted
/// for one brush are a pure function of exactly three inputs: the brush's
/// immutable local geometry, its placement matrix, and the ordered sequence of
/// carvers clipped against it — for each carver its local geometry, its
/// placement matrix, and its precedence flag (<c>carverIndex &lt; brushIndex</c>,
/// which resolves same-facing coplanar faces). The carve clips carvers in the
/// broadphase's sweep-discovery order, so "the ordered sequence of carvers"
/// means exactly that order — reproducing those three inputs reproduces the
/// output bit for bit.
/// <para>
/// <b>The equality contract</b> (a hit requires ALL of):
/// <list type="bullet">
/// <item><description><b>Brush identity:</b> the same <see cref="Brush"/>
/// reference. A brush is immutable after construction — planes, faces, AND the
/// per-face <see cref="FaceSurface"/> payloads alike — so reference identity
/// implies identical carve inputs, payload included; a value-equal but distinct
/// brush is deliberately a miss. This is also what makes retexturing safe:
/// <see cref="Brush.WithFaceMaterial"/> returns a NEW instance, so a material
/// change necessarily misses here and the surfaces are re-emitted carrying the
/// new payload. There is no path by which a cached carve can hand back polygons
/// wearing a material the brush no longer has.</description></item>
/// <item><description><b>Placement:</b> element-wise <c>==</c> on the
/// <see cref="Matrix4x4"/>. Plain float equality is sufficient here: NaN never
/// reaches a carve (scene snapshots reject non-finite transforms), and the one
/// pair float <c>==</c> conflates, <c>-0.0 == 0.0</c>, can at worst flip the
/// sign bit of a zero in an output vertex — indistinguishable under the IEEE
/// equality that every determinism check (and the rasteriser) uses.</description></item>
/// <item><description><b>Carver sequence:</b> the same length, and element-wise
/// the same carver brush reference, value-equal carver matrix, and equal
/// precedence flag, compared <em>in carve order</em> (the broadphase's
/// discovery order, exactly as the carve consumes them).</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Why the carver comparison is ordered rather than an unordered set:</b>
/// clip order changes the fragment decomposition bit-wise (clipping a face by
/// carver A then B splits along A's planes first; B-then-A splits differently,
/// covering the same area with different polygons). The clip order is the
/// broadphase's discovery order, which can reshuffle between compiles even for
/// a brush whose neighbour SET is unchanged (moving one distant brush can
/// permute the sweep), so a set-equal carver collection could still demand a
/// different clip sequence than the cached surfaces were produced under — an
/// unordered comparison would reuse them anyway, silently diverging from what
/// a from-scratch compile of the same placements emits. The ordered comparison
/// turns exactly that case into a miss. That asymmetry is deliberate
/// throughout: a false hit is silent wrong geometry, a false miss is only
/// wasted work, so every ambiguity resolves toward a miss.
/// </para>
/// <para>
/// <b>Duplicate brush references:</b> one <see cref="Brush"/> instance may back
/// several placements. The dictionary can hold only one entry per instance
/// (first placement wins at store time), but that is safe: a lookup validates
/// the full contract above, so whichever occurrence matches — if any — is
/// guaranteed the exact carve it asks for, and the others simply miss.
/// </para>
/// <para>
/// <b>Ownership and threading:</b> immutable after construction, so concurrent
/// reads (including the carve's parallel per-brush workers) are safe. A compile
/// consumes a previous cache read-only and produces a fresh one; the same
/// instance may therefore seed any number of later compiles. Entries whose
/// brush left the placement set are pruned automatically — the produced cache
/// is rebuilt from the current placements only, never carried over.
/// </para>
/// </remarks>
public sealed class CsgCompileCache
{
    private readonly Dictionary<Brush, Entry> _entries;

    private CsgCompileCache(Dictionary<Brush, Entry> entries) => _entries = entries;

    /// <summary>Number of brushes with a cached carve result.</summary>
    public int Count => _entries.Count;

    /// <summary>True when this cache holds a carve result for <paramref name="brush"/> (by reference identity).</summary>
    public bool Contains(Brush brush) => _entries.ContainsKey(brush);

    /// <summary>
    /// Looks up a cached entry for placement <paramref name="index"/> and
    /// validates the full equality contract (see the class remarks) against
    /// the current placements. <paramref name="carverIndices"/> must be in
    /// carve order — the broadphase discovery order the carve clips in.
    /// </summary>
    internal bool TryGetValid(
        IReadOnlyList<BrushPlacement> placements, int index, int[] carverIndices,
        [NotNullWhen(true)] out Entry? entry)
    {
        entry = null;
        BrushPlacement placement = placements[index];
        if (!_entries.TryGetValue(placement.Brush, out Entry? cached))
            return false;

        // Matrix4x4.op_Equality is element-wise float ==; see the class
        // remarks for why that is the right comparison here.
        if (cached.Placement != placement.Transform)
            return false;

        CarverRecord[] records = cached.Carvers;
        if (records.Length != carverIndices.Length)
            return false;

        for (int k = 0; k < records.Length; k++)
        {
            int o = carverIndices[k];
            BrushPlacement carver = placements[o];
            if (!ReferenceEquals(records[k].Brush, carver.Brush) ||
                records[k].Transform != carver.Transform ||
                records[k].CarverWins != (o < index))
            {
                return false;
            }
        }

        entry = cached;
        return true;
    }

    /// <summary>
    /// Builds the cache one compile produces: one entry per placement, keyed by
    /// brush reference. Entries are exactly the current compile's outputs, so
    /// brushes that left the placement set since the previous compile are
    /// pruned by construction.
    /// </summary>
    internal static CsgCompileCache FromEntries(IReadOnlyList<BrushPlacement> placements, Entry?[] entries)
    {
        // Explicit reference comparer: Brush happens to use reference equality
        // by default today, but this cache's contract REQUIRES it, so say so.
        var map = new Dictionary<Brush, Entry>(placements.Count, ReferenceEqualityComparer.Instance);
        for (int i = 0; i < entries.Length; i++)
        {
            // TryAdd: with duplicate brush references the first placement's
            // entry wins deterministically (see the class remarks).
            map.TryAdd(placements[i].Brush, entries[i]!);
        }
        return new CsgCompileCache(map);
    }

    /// <summary>
    /// One carver as the cached carve consumed it: the carving brush, the
    /// placement matrix it carved at, and whether it won same-facing coplanar
    /// precedence over the carved brush.
    /// </summary>
    internal readonly struct CarverRecord(Brush brush, Matrix4x4 transform, bool carverWins)
    {
        public readonly Brush Brush = brush;
        public readonly Matrix4x4 Transform = transform;
        public readonly bool CarverWins = carverWins;
    }

    /// <summary>
    /// The cached carve of one brush: the inputs it depended on and the
    /// world-space surfaces it produced. Immutable after construction; the
    /// surface array is shared verbatim between the cache and every compile
    /// result that reused it (safe — <see cref="Polygon"/> is immutable and
    /// downstream phases never mutate their input arrays).
    /// </summary>
    internal sealed class Entry
    {
        public readonly Matrix4x4 Placement;
        public readonly CarverRecord[] Carvers;
        public readonly Polygon[] Surfaces;

        private Entry(Matrix4x4 placement, CarverRecord[] carvers, Polygon[] surfaces)
        {
            Placement = placement;
            Carvers = carvers;
            Surfaces = surfaces;
        }

        /// <summary>
        /// Captures the entry for placement <paramref name="index"/> after a
        /// fresh carve. <paramref name="carverIndices"/> must be in carve
        /// order — the exact order the surfaces were clipped in.
        /// </summary>
        public static Entry Create(
            IReadOnlyList<BrushPlacement> placements, int index, int[] carverIndices, Polygon[] surfaces)
        {
            var carvers = new CarverRecord[carverIndices.Length];
            for (int k = 0; k < carvers.Length; k++)
            {
                int o = carverIndices[k];
                carvers[k] = new CarverRecord(placements[o].Brush, placements[o].Transform, o < index);
            }
            return new Entry(placements[index].Transform, carvers, surfaces);
        }
    }
}
