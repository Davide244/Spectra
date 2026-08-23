using System;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// One draw command in a <see cref="RenderView"/>: the mesh to draw, the
/// material to draw it with, and the world matrix placing it. A plain value —
/// items carry no scene-graph references, so pipelines consuming them never
/// need to walk (or even know about) the graph.
/// </summary>
/// <param name="Mesh">The geometry to draw.</param>
/// <param name="Material">The material to draw it with; items without one are skipped.</param>
/// <param name="World">The model (local→world) matrix for this draw.</param>
public readonly record struct RenderItem(Mesh Mesh, Material? Material, Matrix4x4 World);

/// <summary>
/// A reusable per-frame draw list: the flat, frustum-culled set of mesh draws
/// for one camera, plus the frustum-culled chunks of the derived static world
/// carried in their own list (<see cref="WorldItems"/> — world-chunk meshes
/// are already in world space, so each entry draws with an identity model
/// matrix, and a chunk contributes one entry per material it wears, each with
/// that material already resolved). Built once per frame
/// by <c>Scene.BuildRenderView</c> and handed to whichever backend pipeline
/// executes — pipelines iterate <see cref="Items"/> and
/// <see cref="WorldItems"/> identically instead of walking the scene graph
/// themselves.
/// </summary>
/// <remarks>
/// <b>Threading:</b> render thread only, like the <see cref="Scene.Scene"/>
/// that fills it — built during the frame's update phase and consumed by the
/// pipeline in the same frame, so no synchronization is needed.
/// <para>
/// <b>Allocation discipline:</b> the instance is engine-owned and reused every
/// frame; <see cref="Clear"/> keeps both lists' capacity, so steady-state
/// rebuilds allocate nothing once the lists have grown to the scene's size.
/// </para>
/// </remarks>
public sealed class RenderView
{
    private readonly List<RenderItem> _items = [];
    private readonly List<RenderItem> _worldItems = [];

    /// <summary>
    /// The frustum-culled draw list, in the scene spatial index's emission
    /// order — deterministic and stable across builds of an unchanged scene.
    /// </summary>
    public IReadOnlyList<RenderItem> Items => _items;

    /// <summary>
    /// The frustum-culled static-world chunk draws, in ascending chunk-cell
    /// order and, within a chunk, ascending material id: one item per
    /// (visible chunk, material), each carrying that piece's GPU mesh, the
    /// material its faces resolved to at upload time, and an identity world
    /// matrix (chunk vertices are in world space). Culling is per CHUNK and
    /// uses the chunk's true render AABB — the union of its owned surfaces,
    /// which may extend past the cell — so a chunk with any potentially
    /// visible geometry is never dropped. Empty until the first compile lands.
    /// </summary>
    public IReadOnlyList<RenderItem> WorldItems => _worldItems;

    /// <summary>
    /// Mesh-renderer items emitted into <see cref="Items"/> this build (i.e.
    /// the survivors of culling). NOT the length of <see cref="Items"/>, which
    /// also carries part-brush draws — see <see cref="PartBrushesVisible"/>.
    /// The two populations are counted apart so neither can read as larger
    /// than its own total.
    /// </summary>
    public int VisibleCount { get; internal set; }

    /// <summary>Total mesh-bearing spatial nodes registered in the scene, culled or not.</summary>
    public int TotalCount { get; internal set; }

    /// <summary>
    /// World chunks that survived chunk culling this build. NOT the length of
    /// <see cref="WorldItems"/> — a chunk expands to one item per material it
    /// wears (see <see cref="WorldMaterialBatchesVisible"/>).
    /// </summary>
    public int WorldChunksVisible { get; internal set; }

    /// <summary>Total chunks of the compiled static world with render geometry, culled or not.</summary>
    public int WorldChunksTotal { get; internal set; }

    /// <summary>
    /// Per-material world draws emitted this build — the length of
    /// <see cref="WorldItems"/>, i.e. how many draw calls the visible chunks
    /// cost. Equal to <see cref="WorldChunksVisible"/> for a single-material
    /// world; the gap between them is what per-material batching costs.
    /// </summary>
    public int WorldMaterialBatchesVisible { get; internal set; }

    /// <summary>Per-material world draws across every uploaded chunk, culled or not.</summary>
    public int WorldMaterialBatchesTotal { get; internal set; }

    /// <summary>
    /// Part brushes that survived culling and contributed draws this build.
    /// Their draws land in <see cref="Items"/> alongside mesh renderers, not in
    /// <see cref="WorldItems"/> — a part is drawn per node under its own world
    /// matrix, which is precisely what makes it free to move.
    /// </summary>
    public int PartBrushesVisible { get; internal set; }

    /// <summary>Total nodes carrying a part brush, culled or not.</summary>
    public int PartBrushesTotal { get; internal set; }

    // ---- lights ------------------------------------------------------------

    /// <summary>
    /// How many lights a single pass can carry.
    /// </summary>
    /// <remarks>
    /// <b>A hard cap, and the ceiling is real.</b> Every light is a uniform-array
    /// element shaded by every fragment, so the cost is N times the pixel count
    /// whether or not a light reaches a given surface. The correct answer is
    /// clustered or tiled shading, which needs storage buffers that SpectraShade
    /// does not have, so this is a genuine ceiling rather than a tuning knob:
    /// past it, lights are dropped and the drop is visible as popping when the
    /// camera moves. <c>LightsDropped</c> says when it is happening.
    /// </remarks>
    public const int MaxLights = 8;

    private readonly RenderLight[] _lights = new RenderLight[MaxLights];
    private readonly float[] _lightKeys = new float[MaxLights];

    /// <summary>Lights for this pass, nearest first. Only the first <see cref="LightCount"/> are valid.</summary>
    public ReadOnlySpan<RenderLight> Lights => _lights.AsSpan(0, LightCount);

    /// <summary>How many of <see cref="Lights"/> are filled.</summary>
    public int LightCount { get; private set; }

    /// <summary>
    /// Lights the scene had that did not fit. Non-zero means the picture is
    /// missing light it should have had, so it is reported rather than absorbed.
    /// </summary>
    public int LightsDropped { get; private set; }

    /// <summary>
    /// Offers a light for inclusion, keeping the nearest <see cref="MaxLights"/>.
    /// Build-side use only.
    /// </summary>
    /// <param name="light">The flattened light.</param>
    /// <param name="sortKey">
    /// Distance from the viewer, or a negative value to mean "always keep this
    /// one" — which is what a directional light passes, since a sun has no
    /// position to be far from.
    /// </param>
    /// <remarks>
    /// An insertion sort into a fixed buffer: no list, no comparer, no
    /// allocation, because <c>RenderViewTests</c> asserts a per-frame delta of
    /// exactly zero bytes. At eight elements it is also simply faster than
    /// anything cleverer.
    /// <para>
    /// Ties keep the earlier offer, which makes the result a function of the
    /// scene's light order rather than of float comparison luck. Two lamps at
    /// exactly equal distance is ordinary content, not a pathological case.
    /// </para>
    /// </remarks>
    internal void OfferLight(in RenderLight light, float sortKey)
    {
        if (LightCount == MaxLights && sortKey >= _lightKeys[MaxLights - 1])
        {
            LightsDropped++;
            return;
        }

        int insertAt = LightCount;
        while (insertAt > 0 && _lightKeys[insertAt - 1] > sortKey)
            insertAt--;

        int last = LightCount == MaxLights ? MaxLights - 1 : LightCount;
        if (LightCount == MaxLights)
            LightsDropped++;
        else
            LightCount++;

        for (int i = last; i > insertAt; i--)
        {
            _lights[i] = _lights[i - 1];
            _lightKeys[i] = _lightKeys[i - 1];
        }

        _lights[insertAt] = light;
        _lightKeys[insertAt] = sortKey;
    }

    /// <summary>Appends one draw to the list (build-side use only).</summary>
    internal void Add(in RenderItem item) => _items.Add(item);

    /// <summary>Appends one world-chunk draw to <see cref="WorldItems"/> (build-side use only).</summary>
    internal void AddWorldChunk(in RenderItem item) => _worldItems.Add(item);

    /// <summary>
    /// Resets the view for the next build. Retains both item lists' capacity —
    /// this is what keeps per-frame rebuilds allocation-free.
    /// </summary>
    internal void Clear()
    {
        _items.Clear();
        _worldItems.Clear();
        VisibleCount = 0;
        TotalCount = 0;
        WorldChunksVisible = 0;
        WorldChunksTotal = 0;
        WorldMaterialBatchesVisible = 0;
        WorldMaterialBatchesTotal = 0;
        PartBrushesVisible = 0;
        PartBrushesTotal = 0;
        LightCount = 0;
        LightsDropped = 0;
    }
}
