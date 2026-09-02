using System;
using System.Numerics;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// A solid-leaf BSP tree in its flat form: a block of <see cref="FlatBspNode"/>
/// plus the root's child code. It answers <see cref="ContainsPoint"/> and
/// <see cref="Raycast"/> identically to the live <see cref="BspTree"/> it was
/// flattened from, and it answers them by walking the block DIRECTLY.
/// </summary>
/// <remarks>
/// It takes <see cref="ReadOnlyMemory{T}"/> rather than an array because the
/// block is a plain array today and a memory-mapped view of a cooked map later;
/// nothing here may assume it owns or can mutate the storage.
///
/// There is deliberately no way to rebuild a <see cref="BspTree"/> from this:
/// rehydrating would put a per-node GC object back on the load path, which is
/// the entire cost the flat form exists to remove.
/// </remarks>
public sealed class FlatBspTree
{
    // Deferred far-side frames of one segment trace. Deep enough for the trees
    // the CSG compiler produces for a 32-unit cell; a deeper tree grows onto
    // the heap rather than being refused, because a mapped view may carry a
    // world this process did not compile.
    private const int InlineTraceDepth = 64;

    private readonly ReadOnlyMemory<FlatBspNode> _nodes;

    /// <param name="nodes">The internal nodes, in <see cref="BspFlattener"/> order.</param>
    /// <param name="rootIndex">An index into <paramref name="nodes"/>, or a leaf code.</param>
    public FlatBspTree(ReadOnlyMemory<FlatBspNode> nodes, int rootIndex)
    {
        // Only the root is checked. Validating every child index would be an
        // O(n) scan of storage that is meant to be paged in lazily; a cooked
        // container answers for the rest of the block through its own digest.
        if (rootIndex < FlatBspNode.SolidLeaf || rootIndex >= nodes.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rootIndex), rootIndex,
                $"Root is neither a node index in [0, {nodes.Length}) nor a leaf code.");
        }

        _nodes = nodes;
        RootIndex = rootIndex;
    }

    /// <summary>The root's child code: a node index, or a leaf code for a bare-leaf tree.</summary>
    public int RootIndex { get; }

    /// <summary>The node block, for a writer that has to emit it. Queries read it internally.</summary>
    public ReadOnlyMemory<FlatBspNode> Nodes => _nodes;

    /// <summary>Internal node count. Leaves are encoded in the child fields and occupy no slots.</summary>
    public int NodeCount => _nodes.Length;

    /// <summary>True when the point lies inside solid space.</summary>
    public bool ContainsPoint(Vector3 point)
    {
        ReadOnlySpan<FlatBspNode> nodes = _nodes.Span;

        int index = RootIndex;
        while (index >= 0)
        {
            ref readonly FlatBspNode node = ref nodes[index];
            index = Plane.DotCoordinate(node.Plane, point) >= 0f ? node.Front : node.Back;
        }
        return index == FlatBspNode.SolidLeaf;
    }

    /// <summary>
    /// Casts a ray against solid space. Returns true and reports the first
    /// surface entered in <paramref name="hit"/>; false if the ray stays in
    /// empty space for the whole distance.
    /// </summary>
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out BspRaycastHit hit)
    {
        hit = default;
        if (direction == Vector3.Zero || maxDistance <= 0f)
            return false;

        direction = Vector3.Normalize(direction);

        // A ray that starts inside solid space hits immediately.
        if (ContainsPoint(origin))
        {
            hit = new BspRaycastHit(origin, -direction, 0f);
            return true;
        }

        Vector3 end = origin + direction * maxDistance;
        if (TraceSegment(origin, end, direction, out hit))
        {
            hit = hit with { Distance = Vector3.Distance(origin, hit.Point) };
            return true;
        }
        return false;
    }

    // Walks the segment origin..end through the flat block, returning the point
    // where it first crosses from empty into solid space. This is
    // BspTree.TraceSegment with the recursion unrolled onto an explicit stack:
    // the near-side call is the only real recursion there (the far-side call is
    // in tail position), so one frame per crossed splitter carries the deferred
    // far side. Entry into solid is detected EXACTLY, by the leaf containing
    // each sub-segment, and the last plane crossed on the way there (oriented
    // toward the side the ray came from) is the entry surface. The sidedness is
    // transcribed index for index from the live tree: a flipped comparison or a
    // flipped crossing normal throws nothing and reports nothing, it just names
    // the wrong surface.
    private bool TraceSegment(Vector3 origin, Vector3 end, Vector3 direction, out BspRaycastHit hit)
    {
        ReadOnlySpan<FlatBspNode> nodes = _nodes.Span;

        Span<TraceFrame> frames = stackalloc TraceFrame[InlineTraceDepth];
        int depth = 0;

        int index = RootIndex;
        Vector3 a = origin;
        Vector3 b = end;
        bool hasEntry = false;
        Vector3 entryNormal = default;

        while (true)
        {
            while (index >= 0)
            {
                ref readonly FlatBspNode node = ref nodes[index];
                float da = Plane.DotCoordinate(node.Plane, a);
                float db = Plane.DotCoordinate(node.Plane, b);

                if (da >= 0f && db >= 0f)
                {
                    index = node.Front;
                    continue;
                }
                if (da < 0f && db < 0f)
                {
                    index = node.Back;
                    continue;
                }

                float t = da / (da - db);
                Vector3 mid = Vector3.Lerp(a, b, t);

                int near = da >= 0f ? node.Front : node.Back;
                int far = da >= 0f ? node.Back : node.Front;

                // The crossed plane, oriented toward the incoming side, is the
                // candidate entry surface for whatever the far side resolves to.
                Vector3 crossingNormal = da >= 0f ? node.Plane.Normal : -node.Plane.Normal;

                if (depth == frames.Length)
                {
                    var grown = new TraceFrame[frames.Length * 2];
                    frames.CopyTo(grown);
                    frames = grown;
                }
                frames[depth++] = new TraceFrame(far, mid, b, crossingNormal);

                // Resolve the near side first; the ray reaches it before the
                // plane. hasEntry and entryNormal are carried into it unchanged,
                // exactly as the recursive form passes them down.
                index = near;
                b = mid;
            }

            if (index == FlatBspNode.SolidLeaf)
            {
                // With an entry plane recorded, `a` is the crossing point on it.
                // Without one the whole ray started in solid, which Raycast's
                // own ContainsPoint check already reports; the fallback mirrors
                // its starts-inside convention.
                hit = new BspRaycastHit(a, hasEntry ? entryNormal : -direction, 0f);
                return true;
            }

            if (depth == 0)
            {
                hit = default;
                return false;
            }

            // The near side was clear to the plane; continue across it. Popping
            // in LIFO order is the recursion's own unwind order.
            TraceFrame frame = frames[--depth];
            index = frame.Child;
            a = frame.Start;
            b = frame.End;
            entryNormal = frame.EntryNormal;
            hasEntry = true;
        }
    }

    private readonly record struct TraceFrame(int Child, Vector3 Start, Vector3 End, Vector3 EntryNormal);
}
