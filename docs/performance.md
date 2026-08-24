# Performance

Performance is a design pillar, not a phase. The engine targets low-power
integrated GPUs and old laptops as first-class hardware, which means the
question is never "is this fast enough on the dev machine" but "what does this
cost per frame, and how does that cost scale with content".

This document is the catalogue: what the engine already does, what it measurably
costs today, and every technique worth adding, with what each one would buy
*here* rather than in general.

---

## 1. How to measure, before changing anything

Nothing in this document should be acted on without a measurement first. Two of
the three largest wins ever found in this engine were invisible to reasoning and
obvious to a profiler: a vsync call applied to the wrong thread, and a
validation layer nobody had turned off.

```bash
dotnet run --project SpectraEngine.Executable -- d3d11 --profile --debug-layer=false
```

| Switch | What it does |
|---|---|
| `--profile` | Per-phase frame timing in the periodic stats line |
| `--debug-layer=false` | Off. **Any measurement with it on is measuring validation** |
| `--adapter=<name>` | Which GPU. `--adapter=UHD` is a low-power test rig on most desktops |
| `--size=WxH` | Window size, for separating CPU cost from per-pixel cost |
| `--parts=<grid>` | Scales the demo world, for measuring cost against content |
| `--shadows=false` | Isolates what shadows cost |
| `--pipeline=<name>` | `deferred` or `forward`, for the A/B |

**Read `Present` carefully.** Where a backend blocks for the GPU or for vsync,
it blocks there, so a frame reading 1 ms of work and 15 ms of `Present` is
waiting rather than working.

**Every phase number is CPU time.** There is no GPU timing yet; see §7.

---

## 2. What the engine already does

| Technique | State | Where |
|---|---|---|
| Frustum culling, scene nodes | **Yes**, through a BVH | `SceneBvh.QueryFrustum` |
| Frustum culling, static world | **Yes**, clustered over a Z-ordered list | `Scene.CollectVisible` |
| Backface culling | **Yes**, all three backends | rasteriser state |
| Depth testing / early-Z | **Yes** | default depth state |
| Mipmapping | **Yes**, generated on upload | `D3D11Texture`, GL equivalent |
| Chunked world (32-unit cells) | **Yes** | `ChunkGrid` |
| Incremental world recompile | **Yes**, dirty cells only | `CsgIncrementalCompiler` |
| Per-chunk, per-material submeshes | **Yes** | static-world swap |
| Async world compile | **Yes**, background thread | `Scene.ProcessStaticWorldCompilation` |
| Shadow cascades | **Yes**, 4, texel-snapped | `ShadowMap` |
| Mesh buffer pooling | **D3D12 only** | `D3D12Renderer.RentMeshBuffer` |
| Draw list built from drawables only | **Yes** | `Scene.DrawableNodes` |
| Sorting / batching by state | No | — |
| Instancing | No | roadmap `R12` |
| Occlusion culling | No | — |
| Level of detail | No | — |
| Streaming / world paging | No | roadmap arc `D` |
| GPU timing | No | — |

Deliberately **not** done, and not to be added: PVS, portals, sealed-world
requirements. The open-world pillar rules them out; see `CLAUDE.md`.

---

## 3. The cost model, measured

From the demo, D3D11, validation off, on an RTX 4070 Ti unless stated.

**Per draw call:** about **0.85 µs**, linear in visible draws across a 60x
range. This is the number that decides how many objects can be on screen.

**Per total batch in the world:** about **0.22 µs**, whether visible or not.
This is the scalability defect in §4.1.

**Per pixel, integrated GPU (Intel UHD 770):** **2.9 ms per megapixel**, plus
0.36 ms of CPU. At 1080p: forward 2.4 ms, deferred 5.4 ms, deferred with
cascades 6.4 ms.

**Per brush, memory:** about **27 KB** resident, world compiled and all.

Scaling measured directly, not extrapolated:

| brushes | world | total batches | frame | ViewBuild | Shadows |
|---|---|---|---|---|---|
| 234 | 0.04 km² | 96 | 0.32 ms | 0.04 | 0.17 |
| 4,134 | 0.8 km² | 1,562 | 1.76 ms | 0.31 | 0.60 |
| 9,200 | 1.9 km² | 3,466 | 3.21 ms | 0.75 | 0.97 |
| 25,638 | 5.2 km² | 9,316 | 8.44 ms | 2.09 | 2.67 |

**5 km² and 25,000 objects at 118 fps is what this engine is good for today.**

---

## 4. Don't submit it: culling

### 4.1 A hierarchy over static-world chunks — *partly done*

**Done:** the chunk list is Z-ordered (`ChunkCoord.MortonKey`) and culled through
a bounding box per run of 64, and the draw list is built from
`Scene.DrawableNodes` rather than from the whole spatial index. At 25,638
brushes that took the frame from 8.44 ms to 4.78 ms, ViewBuild from 2.09 to 0.48
and Shadows from 2.67 to 0.20.

**Left:** the cluster array is rebuilt whole on every world swap, which cost
WorldSwap 0.36 ms. Caching chunk render bounds in a flat array parallel to the
list would make that rebuild a contiguous pass instead of a pointer chase. And
`DrawableNodes` is a linear scan, correct while drawables number in the tens or
hundreds and wanting a second BVH once they number in the thousands.

<details><summary>The original finding, kept because the reasoning still applies</summary>


`Scene.CollectVisible` walks **every chunk in the world** testing its AABB
against the frustum, and runs once for the camera plus once per shadow cascade:
five linear scans of the whole world, every frame.

Measured: at 234 brushes ViewBuild is 0.04 ms; at 25,638 it is 2.09 ms, while
the number of draws barely changes. Extrapolated to an 80 km² world it is about
73 ms of culling per frame before anything is drawn.

The world is already chunked on a grid and the culling does not use it. **An
AABB sweep of grid cells is not the fix** — a long camera frustum covers tens of
thousands of mostly-empty cells and would be slower. It wants a bounding-volume
hierarchy over chunk render bounds, rebuilt at swap time (rare) and queried per
view (every frame), exactly as `SceneBvh` already does for mesh nodes.

**Buys:** turns O(world) into O(log world + visible). **Costs:** one BVH build
per world swap. **Blocks on:** nothing.
</details>

### 4.2 Occlusion culling

Nothing is skipped for being behind something else. In a dense interior or a
city street, frustum culling alone leaves 10-100x too much submitted.

The technique that fits this engine is **GPU depth-buffer reprojection**: render
last frame's depth, downsample it into a hierarchical Z pyramid, and test each
object's bounds against it on the CPU or GPU. It needs no authored data, which
matters for an engine where levels are brush-built and constantly edited.

**Buys:** the most in exactly the scenes that are hardest — dense, enclosed,
lots of overdraw. **Costs:** a depth pyramid pass, one frame of latency, and
false positives when the camera cuts. **Blocks on:** nothing, though it pairs
naturally with §4.1.

### 4.3 Distance culling and a per-object cull distance

Small objects past some distance contribute less than a pixel. A per-object
cull distance, scaled by screen-space size, removes them for free.

**Buys:** large in open worlds, nothing in corridors. **Costs:** trivial.
**Blocks on:** nothing. This is the cheapest item in the document.

### 4.4 Shadow-caster culling that is not the camera's

Already done (`Scene.BuildShadowView` culls per cascade against the light), but
worth listing because reusing the camera's list is the classic mistake and
produces shadows that flicker as you turn.

---

## 5. Submit less of it: level of detail

### 5.1 Mesh LOD

No LOD of any kind exists. Every object draws at full detail at every distance.

**Buys:** the difference between a 60 m draw distance and a 2 km one.
**Costs:** authored or generated LOD chains, plus a selection pass.
**Blocks on:** the model pipeline (`D` arc) for authored chains; a decimator for
generated ones.

### 5.2 Hierarchical LOD (HLOD) and impostors

Merge a distant cluster of objects into one mesh, or into a billboard. This is
what makes a city's skyline cost a handful of draws instead of thousands.

**Buys:** enormous at open-world scale. **Costs:** a bake step; belongs with the
cook pipeline. **Blocks on:** `D` arc.

### 5.2b A cheaper shadow filter

The filter takes four bilinearly weighted samples on a rotated circle: sixteen
texture fetches, and the whole frame is 7.01 ms at 1080p on an Intel UHD 770.
One weighted sample would be four fetches, and the question is only how much
extra softness the other three buy.

Two things are now known that were not when this section was first written. An
earlier note here claimed a single-sample version "produced no shadow at all";
it does not reproduce against the current shader, so that observation belonged
to a bug since fixed rather than to the idea. And the saw-tooth this filter was
built to fight is **not** a filter problem at all: see §5.2c.

### 5.2c More shadow-map resolution where the camera is looking

The residual saw-tooth on a shadow edge is **resolution, not error**. A caster's
silhouette is rasterised onto the map's texel grid, so a straight edge is stored
as a staircase: steps one texel tall, and for an edge at a shallow angle to the
grid, many texels long. No filter shortens the runs, and a hardware comparison
sampler would produce the same staircase. Rotating the tap pattern per pixel
turns it from a comb into grain, which is what ships, but the error is still
there.

The measurement that pins it: cascade 0's world texel is **4.8 mm** in the demo,
and at a wall 1.3 units from the eye that is 2.5 screen pixels, so the map is
being magnified. Cutting `ShadowMap.Distance` from 60 to 15 shrinks cascade 0's
slice by about 4x and the saw-tooth very nearly disappears.

So the options are all "spend more texels near the camera":

- **A larger atlas.** 2048 to 4096 quarters the texel size and costs 64 MB plus
  4x the depth-pass fill. Blunt, and it pays for the far cascades too.
- **A better split.** Cascade 0 currently runs 0.1 to 2.24 units, and its
  bounding SPHERE is 4.9 units across for a frustum slice far smaller than that.
  Most of the near cascade is empty (visible in a dump of the atlas). Tightening
  the near plane used for the split, or bounding the slice more cleverly, buys
  texel density for nothing.
- **A fifth cascade.** Needs an atlas that is not 2x2.

Measure before choosing: the sphere bound is what makes the map stable under
camera rotation, and any scheme that replaces it has to keep that property or it
trades a saw-tooth for a shimmer.

### 5.3 Shadow LOD

Distant cascades do not need full-detail casters, and small objects need not
cast at all. Cheapest version: a per-object "casts shadows" flag plus a caster
size threshold per cascade.

**Buys:** shadows are the largest single rendering cost today. **Costs:** near
zero. **Blocks on:** nothing.

### 5.4 Staggered cascade updates

Cascade *k* updated every 2^*k* frames rather than every frame. The far cascades
change slowly because they cover ground the camera crosses slowly.

**Buys:** roughly half the shadow cost. **Costs:** a frame or two of lag on
distant moving casters. **Blocks on:** nothing.

---

## 6. Submit it in fewer calls: batching

### 6.1 Instancing

Roadmap `R12`. Every (chunk, material) pair and every mesh node is its own draw.
A world with a thousand identical crates issues a thousand draws.

**Buys:** at 0.85 µs a draw, collapsing 1,000 draws into 1 saves 0.85 ms.
**Costs:** `VertexAttribute` gains an input rate; a batching pass in
`BuildRenderView` that must preserve the deterministic emission order the view
tests assert. **Blocks on:** nothing.

### 6.2 Sorting by pipeline state

Draws are submitted in emission order, so the same shader and material can be
bound repeatedly. Sorting by (shader, material, mesh) collapses redundant binds.

**Buys:** modest on D3D11/GL, more on D3D12 where a PSO change is expensive.
**Costs:** a sort with a stable tie-break, or determinism tests break.
**Blocks on:** nothing.

### 6.3 Merging small static chunks

Chunks are 32 units and a sparse world produces many nearly-empty ones. Merging
adjacent low-triangle chunks that share a material reduces draws directly.

**Buys:** proportional to how sparse the world is. **Costs:** complicates the
dirty-cell mapping the incremental compiler depends on. **Blocks on:** care.

### 6.4 Indirect / GPU-driven draws

The endpoint: the GPU culls and builds its own draw list, and the CPU submits
one call. Correct for D3D12 and Vulkan, not available on GL 3.3.

**Buys:** removes CPU draw cost as a concept. **Costs:** large; needs compute,
bindless resources and a render graph. **Blocks on:** everything above; do not
start here.

---

## 7. Move fewer bytes: bandwidth

**This is the section that matters for integrated GPUs**, which are bandwidth
starved in a way discrete cards are not.

### 7.1 Trim the G-buffer — *do this one first*

36 bytes per pixel across five attachments. **Two of them (emissive, custom) are
written every frame and read by nothing.** Dropping them is 36 → 20 bytes/px.

Measured at 1080p on the UHD 770: deferred costs 3.1 ms more than forward, which
is almost exactly the round-trip bandwidth of that G-buffer. A 44% cut is worth
about 1.3 ms.

**Buys:** ~1.3 ms at 1080p on integrated. **Costs:** nothing; add them back when
a shading model needs them. **Blocks on:** nothing.

### 7.2 Pack the G-buffer harder

Octahedral normal encoding (2 channels instead of 3), 8-bit roughness/metallic
in one target. Standard practice, gets 20 bytes/px down toward 12.

**Buys:** another third of the deferred cost. **Costs:** encode/decode in two
shaders. **Blocks on:** §7.1.

### 7.3 Render scale / dynamic resolution

Render the scene at a fraction of the window and upscale in the resolve pass,
optionally adjusting to hold a frame-time target. On a 2.9 ms/Mpx GPU, 75% scale
is a 44% cut to every per-pixel cost in the frame.

**Buys:** the single largest lever on weak hardware, and it is a slider.
**Costs:** small, and the resolve pass already exists. **Blocks on:** nothing.

### 7.4 Texture compression (BC / ASTC)

Textures upload as raw RGBA8. BC7 is 4:1, BC1 is 8:1, and the saving is on
sampling bandwidth every frame, not just memory.

**Buys:** large in texture-heavy scenes. **Costs:** a cook step. **Blocks on:**
`D` arc.

### 7.5 Deferred on tile-based GPUs

Phone and some laptop GPUs are tile-based. Writing a fat G-buffer to memory and
reading it back defeats the architecture. The mobile-correct forms are clustered
forward, or deferred inside one render pass using subpasses (Vulkan) or
programmable blending (Metal).

**The forward path is the mobile path.** This is a better reason to keep it than
transparency.

---

## 8. Overlap the work: latency and threading

### 8.1 D3D12 frame pipelining

`D3D12Renderer.Present` calls `WaitForGpu()` every frame, so CPU and GPU never
overlap and the frame costs their **sum** instead of their **maximum**.

**Buys:** measured at ~0.4 ms today and grows with GPU load. **Costs:** per-frame
fence values, per-frame allocators and descriptor rings, and every resource
lifetime becomes a real question. The mesh buffer pool already retires through
the fence and is ready for it. **Blocks on:** care; this is where use-after-free
bugs live.

### 8.2 Parallel command recording

Record the shadow cascades and the G-buffer pass on worker threads. Natural on
D3D12/Vulkan, impossible on GL, awkward on D3D11 (deferred contexts are rarely
a win).

**Buys:** proportional to draw count. **Costs:** the render thread stops being
one thread, which touches every assumption in the engine. **Blocks on:** §8.1.

### 8.3 Parallel culling

Five view builds per frame are independent and could run concurrently.

**Buys:** most of the culling cost, on any multi-core machine. **Costs:** the
view pool must stop sharing scratch. **Blocks on:** §4.1 first — making the work
smaller beats making it parallel.

---

## 9. Don't have it resident: streaming

**The largest gap in the engine, and the one nothing else substitutes for.**

`RebuildStaticWorld` compiles the whole world and keeps all of it resident.
Measured: 27 KB per brush, 1.6 s to compile 25,638 of them. An 80 km² world
would want roughly 10 GB and 25 seconds before the first frame.

Needs: cooked chunk data instead of load-time CSG (`D` arc), residency driven by
camera position, background load and evict, and an LOD chain so distant regions
can be resident cheaply.

**Buys:** the difference between a 5 km² world and an unbounded one. **Costs:**
a subsystem. **Blocks on:** the cook pipeline.

---

## 10. Know which of the above to do: measurement

### 10.1 GPU timing

Every number in this document is CPU time. GPU cost is inferred from resolution
sweeps, which is sound but indirect. Timestamp queries on all three backends
would make per-pass GPU cost directly readable.

**Do this before optimising any shader.** Nobody has yet had a reason to look at
shading cost, and nobody should until it can be seen.

### 10.2 Draw call and triangle counters

Draws per frame, triangles per frame, state changes per frame, in the stats
line. Cheap, and it turns "the scene got slower" into "the scene got 4,000 more
draws".

### 10.3 Allocation and GC per frame

The draw path is asserted allocation-free in steady state but not measured. A
GC pause is the one performance failure a managed engine can have that a native
one cannot, and it should be visible rather than assumed.

### 10.4 A performance regression gate

The demo already runs unattended with `--profile`. A frame-time budget per
backend, failing when exceeded, would catch a regression the way the CSG
oracles catch geometry ones.

---

## 11. Suggested order

Roughly by value per unit of work, given what is measured today.

1. **Trim the G-buffer** (§7.1) — free, ~1.3 ms on integrated
2. **Render scale** (§7.3) — a slider that fixes weak hardware
3. **Chunk-culling hierarchy** (§4.1) — removes the scalability wall
4. **Shadow LOD and staggered cascades** (§5.3, §5.4) — halves the largest rendering cost
5. **GPU timing** (§10.1) — before anything shader-shaped
6. **Instancing** (§6.1) — before content scales up
7. **Distance culling** (§4.3) — cheapest remaining item
8. **D3D12 pipelining** (§8.1)
9. **Occlusion culling** (§4.2)
10. **LOD and HLOD** (§5.1, §5.2)
11. **Streaming** (§9) — the largest, and gated on the cook pipeline

Items 1 to 4 are days of work and worth more than everything below them on the
hardware this engine targets.
