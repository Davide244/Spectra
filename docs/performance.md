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
| `--props=<count>` | Scatters N part brushes **sharing one brush instance**, for measuring cost against *repetition* |
| `--shadows=false` | Isolates what shadows cost |
| `--pipeline=<name>` | `deferred` or `forward`, for the A/B |
| `--vsync` | Paces Present to the display. **A frame time under vsync measures the monitor** — leave it off for every measurement; it exists so a demo run can reproduce the editor's pacing |

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

> **Baseline caveat (2026-08-26):** every D3D11 number in this section was
> measured while commit 0666406's leftover per-program texture-bind skip cache
> was live. That cache was never reset when BeginPass or ClearState nulled the
> context's SRV slots, so from the second frame on, D3D11 passes sampled null
> SRVs (zeros, with no error and no debug-layer message) instead of textures.
> The fix moved the cache to the context with a reset at both clear sites
> (`D3D11BindCache`). CPU-side numbers (per-draw, per-batch) are only mildly
> distorted; the per-pixel integrated numbers are the suspect ones and should
> be re-measured on the fixed build before being cited for new decisions.
> First post-fix sample on the development machine: 0.23 ms/frame for the
> default deferred demo with shadows, validation off.

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

### 3a. Hosting changes the number: the editor is capped ON PURPOSE now

History first, because the numbers moved twice. Measured 2026-08-28, same demo
scene, same machine, before the cap:

| host | backend | frame |
|---|---|---|
| standalone window | D3D12 | 1.42 ms (703 fps) |
| Avalonia viewport | D3D12 | **16.69 ms (60 fps)** |
| Avalonia viewport | D3D11 | 0.58 ms (1,715 fps) |

D3D12 was already pinned by accident of its chain (`FlipDiscard` with no
`ALLOW_TEARING` flag into a DWM-composited child honours the refresh rate
regardless of the sync interval), while D3D11's bitblt chain ran unthrottled —
~1,700–2,700 presents a second, each one a full back-buffer copy into DWM's
redirection surface. That was a pinned core, GPU copy bandwidth taken from the
compositor that draws the shell's own chrome, and every per-frame allocation
multiplied by the frame rate into gen0 pressure whose pauses stop the UI
thread too — measurable as "the editor feels sluggish" while the engine's own
FPS counter reads absurdly fast.

**Both backends are capped in the editor now, deliberately, via
`Renderer.VSync`** (set by `EditorSession`; both D3D backends present with
sync interval 1, GL re-applies its context swap interval on the render
thread). Measured after: 60 fps flat, ~1% CPU, 0.1 MB/s allocated at rest.
The demo stays uncapped because it is the measurement instrument — `--vsync`
is the opt-in — so the rule is now symmetric and simple: **no measurement
taken in the editor viewport means anything, on either backend; measure in
the standalone demo, without `--vsync`.**

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

**Measured 2026-08-28, and the headline is that it was worth measuring**, because
the arithmetic this entry used to carry (`0.85 µs × 1,000 = 0.85 ms`) understated
it by more than four times. Every earlier measurement of this engine contained
**no duplicate draws at all**: the static world's chunk meshes are unique
geometry by construction, and `--parts` gives every scattered brush its own
randomized extents, so nothing repeated a mesh and there was nothing to collapse.
`--props=<count>` is the fixture that fixes that: N part brushes sharing **one**
`Brush` instance, which `PartBrushMeshCache` resolves to one GPU mesh, so the
draw list carries N items differing only in world matrix.

D3D11, validation off, 1080p, RTX 4070 Ti:

| props | visible | frame | Geometry | Shadows | ViewBuild | casters |
|---|---|---|---|---|---|---|
| 0 | 1 | 0.30 ms | 0.04 | 0.06 | — | 150 |
| 500 | 151 | 0.96 ms | 0.15 | 0.35 | — | 862 |
| 2,000 | 562 | 2.25 ms | 0.45 | 1.00 | 0.15 | 2,141 |
| 8,000 | 2,134 | **5.24 ms** | **1.53** | **2.26** | 0.52 | 3,362 |

#### What it actually bought, and the correction that came with it

**Landed for the shadow pass 2026-08-28**, and the first thing to record is that
the projection above this paragraph was **wrong by roughly ten times**, in a way
worth naming because it is easy to repeat.

Measured, D3D11, validation off, shadow batching on:

| props | frame before | frame after | Shadows before | Shadows after | draws saved/frame |
|---|---|---|---|---|---|
| 2,000 | 2.25 ms | **1.98 ms** | 1.00 ms | **0.75 ms** | 1,987 |
| 8,000 | 5.24 ms | **5.12 ms** | 2.26 ms | **1.95 ms** | 3,209 |

So removing **3,209 draws a frame bought about 0.3 ms**, not 3.7. That is roughly
**0.1 µs per shadow draw removed**, against the 0.85 µs this document quotes for
a general draw.

**The error was reading a phase total as a submission cost.** "Geometry plus
Shadows is 3.79 ms" is true and says nothing about how much of it is *draws*.
Three things sit inside those phases that instancing cannot touch:

- **The per-cascade view build and cull**, which is inside the `Shadows` phase
  and is proportional to the caster count either way.
- **The GPU's actual work.** Instancing submits the same triangles; it does not
  rasterise fewer of them.
- **How cheap a shadow draw already was.** `DrawShadowCasters` binds no material
  and writes two uniforms, so it never cost the 0.85 µs a shaded draw does.

#### The geometry pass, batched 2026-08-28, and this is where it paid

The prediction two paragraphs below this one turned out to be the right one: a
geometry draw binds a material and a texture table, so removing one is worth far
more than removing a depth-only shadow draw.

Measured, D3D11, validation off, `--props=2000`, 560 of 562 visible props
collapsing to 2 draws:

| | Geometry phase | frame |
|---|---|---|
| unbatched | 1.80 - 2.62 ms | 3.94 - 3.96 ms |
| batched | **0.21 - 0.24 ms** | **3.15 - 3.23 ms** |

About **nine times on the phase** and roughly **0.8 ms on the frame**, against
the 0.3 ms the whole shadow pass gave for six times as many removed draws. The
difference is entirely what a draw costs: `DrawShadowCasters` binds no material
and writes two uniforms, while every geometry draw it replaces was writing the
view, the projection and a full PBR parameter set.

The frame improves by less than the phase does, which is the same lesson in the
other direction: a phase total is not the frame.

**What it does NOT buy: `ViewBuild` (0.52 → 0.72 ms).** Culling still visits every
node, and the batching pass *adds* to it. Instancing removes draw *submission*,
not draw *selection*; §4.1 is the entry that attacks the other one. At 8,000
props the batching pass costs about 0.2 ms to save about 0.3 ms, which is a
thinner margin than it looks and is the number to watch if batching ever seems
not to pay.

**Where it will pay properly** is content with more distinct batches and more
expensive draws than a depth-only pass: the geometry pass, where every draw binds
a material and a texture table.

**Costs, which the roadmap entry also understated (see `R12`).** The missing
dependency is the shader language: `uModel` is a `cbuffer` uniform that every
draw rewrites, SpectraShade has **no** per-instance vertex input and no
`InstanceId` (a repo-wide search finds zero occurrences of either), and all three
backends hardcode `InputSlot = 0, PerVertexData`. So this is a compiler feature
plus a codegen change in both `GlslGenerator` and `HlslGenerator`, *then* the
renderer work. **Blocks on:** nothing, but it is not the small change it reads
as.

### 6.2 Sorting by pipeline state

Draws are submitted in emission order, so the same shader and material can be
bound repeatedly. Sorting by (shader, material, mesh) collapses redundant binds.

*Partly pre-empted without a sort (2026-08-26):* D3D12 now tracks last-bound
root signature, PSO, topology and root CBVs on the renderer and skips redundant
re-sets (`BindRootSignature`/`BindPipelineState`/`BindTopology`/`BindRootCbv`),
and D3D11 skips redundant SRV/sampler binds through the context-level
`D3D11BindCache`; draws that repeat the previous state no longer pay it, in
emission order. A sort still helps by MAKING draws repeat state (and by
batching texture-table changes, which are still re-staged per draw on D3D12).

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

36 bytes per pixel across five attachments. **Only `custom` is dead** (written
as zeros by the geometry pass, no sampler declared for it in the light pass).
**`emissive` is NOT: an earlier revision of this section claimed both were
"read by nothing", and that was never true.** The light pass has sampled
`uEmissive` at every non-sky pixel since the commit that created the G-buffer
(3d8ae0e; `DeferredLight.spectrashade`, the `total + uEmissive.Sample(...)`
line). Executing the old plan as written breaks emissive materials or samples
an unbound slot.

So the free cut is `custom` alone: 36 → 28 bytes/px, roughly half the saving
this section used to promise. Dropping `emissive` too is a real option but a
FEATURE decision, not a free one: it means either giving up emissive surfaces
or moving emission out of the G-buffer (e.g. a separate forward pass over
emissive geometry).

Measured at 1080p on the UHD 770: deferred costs 3.1 ms more than forward,
which is almost exactly the round-trip bandwidth of that G-buffer. Cutting 8 of
36 bytes/px is worth roughly 0.7 ms; the full 16 (with the emissive feature
decision made) roughly 1.3 ms.

**Buys:** ~0.7 ms at 1080p on integrated, ~1.3 ms if emissive goes too.
**Costs:** nothing for `custom`; the emissive feature question for `emissive`.
**Blocks on:** nothing.

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

## 9b. Stop making garbage: allocation and the collector

**The heap size is not the measurement, and looking at it is how this went
unnoticed.** The demo's managed heap sits between 4 and 20 MB and never grows,
which reads as healthy in any memory graph. It is flat because gen0 collects as
fast as the engine fills it, and the actual rate is **320 to 400 MB/s, with 18
to 23 gen0 collections a second**. A gen0 collection stops every thread,
including the render thread, so that is a pause roughly every third frame at 60
fps: exactly the stutter a smoothed average frame time cannot show.

The periodic stats line reports it now, split by where it came from:

```
memory: 402.1 MB/s allocated (27.2 on the render thread), 22.6/s gen0, 1 gen1, 1 gen2, 4 MB heap
```

**93% of it is the static-world recompile, and the number per compile is
stable.** Two samples from one run: 555 compiles/s against 320 MB/s, and 672
against 402, which is **0.59 MB per incremental recompile** both times. The
frame loop's own share is 24 to 28 MB/s at about 1,450 fps, so roughly **18 KB
per frame**.

Two honest caveats before anyone optimises this. The demo is a deliberate
stress case: `PillarA` bobs as a WORLD brush specifically to force an
incremental recompile every frame, and the engine's own guidance is that a brush
which moves under simulation should be a `BrushKind.Part` and recompile nothing.
A normal scene does not recompile 670 times a second. And the compile runs on
the thread pool, so its allocation costs a worker rather than the frame, right
up until the collection it triggers stops the frame anyway.

Where to look first, in order of what the numbers say:

- **`CsgIncrementalCompiler`'s per-compile arrays.** 0.59 MB per edit, and the
  compile already carries paged copy-on-write state between runs; the temporary
  buffers are the obvious pooling candidate. A 2026-08 audit found the larger
  share is not scratch but the identity cascade seeded by re-snapping every
  resident brush's surfaces per compile (snap results are memoized within one
  compile and never carried in `CsgWorldCarry`), so carrying snap results is
  the bigger lever than pooling the containers.
- **18 KB per frame on the render thread, largely attributed and fixed
  (2026-08-26).** The largest slice was `CreateMesh` materialising CPU copies
  (positions/normals/indices) for every rebuilt chunk mesh, which nothing ever
  read for chunks or part meshes; CPU mirrors are now opt-in per creation
  (`MeshCpuAccess`) and both hot call sites pass `None`, which also removed a
  permanent second copy of all world geometry from the managed heap. The next
  slices are the per-launch `PagedArray` placement-page clone (~17 KB per
  clone at the demo's 234 brushes, ~74 KB per touched page past 1,024 brushes
  because pages are 1,024 slots of a 72-byte struct) and small per-launch
  scratch (Traverse's stack, footprint arrays, the Task.Run closure).
- **`GCSettings.LatencyMode` / server GC** change when the pauses happen, not
  how much garbage there is. Reach for them after the rate, not instead of it.

## 9c. What a clean cook costs: the map bake

Offline rather than per frame, and it belongs here because §9's streaming answer
blocks on the cook pipeline: whatever ships that will be cooking maps, and how
much of that cost is the *writer* rather than the compile decides whether the
format is worth the machinery.

`dotnet run -c Release --project Benchmarks/CsgBench -- bake` splits a clean cook
in two and times each half **directly**, never one by subtraction from a total:

- **COMPILE**, the cache-free `CsgWorld.Build` (the overload `ScmapBake` calls:
  no previous world, no carve cache, because a bake must be a pure function of
  its source) plus `BspFlattener.Flatten` over every cell.
- **SERIALIZE**, `ScmapBuilder` staging (`AddNode` per brush, `AddChunk` per
  cell) plus `Build`, which emits `STRT`/`ASTB`/`META`/`NODE`/`CHDR`/`CMSH`/
  `CBSP`. The bake's own glue is counted on this side deliberately: it is
  bookkeeping paid only because a file is being written, so putting it here can
  only make the writer look worse than it is.

Timing the halves independently is not fussiness. Compile cost swings by two
orders of magnitude across these content sets, so a serialize time taken as
total-minus-compile would be flattered by exactly the content that compiles
slowest.

Measured 2026-09-03, i9-13900K (24 cores / 32 threads), Windows 11, median of 3
timed reps after 2 warmups, tiered compilation off:

| content | brushes | cells | surfaces | compile ms | serialize ms | file MiB | share |
|---|---|---|---|---|---|---|---|
| grid k=4 | 64 | 8 | 186 | 2.0 | 0.03 | 0.04 | 1.6% |
| grid k=8 | 512 | 8 | 818 | 8.5 | 0.26 | 0.17 | 3.0% |
| grid k=13 | 2,197 | 8 | 2,238 | 20.8 | 0.80 | 0.52 | 3.9% |
| open 1k | 1,000 | 369 | 6,388 | 12.1 | 2.56 | 1.34 | 21.1% |
| open 10k | 10,000 | 3,666 | 63,622 | 210.1 | 20.98 | 13.24 | 10.0% |
| open 50k | 50,000 | 17,992 | 319,016 | 1,310.4 | 98.84 | 66.74 | 7.5% |

**The verdict: the serializer is a small fraction of the compile, at 16 to 22%
worst case over five runs.** The ceiling is 33%, and the claim worth holding is
the plain form of it: *the compile must stay at least three times the writer*.
Two things break that and only one is a defect: a writer that got slower, and a
compile that got faster without the writer following, which is a real success and
still wants a look. A clean cook is legitimately O(world) and nothing here argues
otherwise; the incremental story belongs to the cook cache and its own no-op-cook
test.

**The two content sets bracket the answer on purpose, and the grid alone would
have proved nothing.** A grid is a solid block, so the union skin is just its
outer shell: 2,197 brushes produce 2,238 surfaces and 8 cells, which is a heavy
compile against almost no bytes and gives a flattering 3.9%. The openworld sets
are where the writer has real work (319,016 surfaces and a 67 MiB file) and are
the ones the verdict is decided on.

### The defect this found on its first run

**A share can stay healthy while one of its halves is quadratic**, as long as the
compile beside it is growing too, and that is exactly what was happening.
`ScmapBuilder.AddChunk` refused a duplicate cell by scanning every cell already
added, which is O(cells²) over a cook: invisible on any hand-written fixture and
precisely what a chunked open world produces most of. Measured, per cell:

| cells | before (µs/cell) | after (µs/cell) |
|---|---|---|
| 369 | 0.2 | 0.11 |
| 3,666 | 1.5 | 0.19 |
| 17,992 | 7.0 | 0.25 |

Per-cell cost rising in step with the cell count is the signature. At 50k parts
the staging pass was 126 ms against the emit's 85 ms, already the larger half of
the writer, and on track to dominate the whole cook. A `HashSet<ChunkCoord>`
beside the list took it to 4.5 ms (28x), and the 50k share from 18.1% to 7.5%.

So the scenario prints **two** verdicts, because the first structurally cannot
see this one: the share, and the per-cell staging cost measured across the two
largest worlds. A pure per-call O(cells) term makes the per-cell cost track the
cell count's own growth, so both numbers are printed together: today 0.7 to
1.6x against a 4.91x bigger world, against 4.7x for the linear scan. The ceiling
is 2.5x, between the two.

**Emit throughput is flat and is the honest floor of the format**: 540 to 890
MiB/s across the openworld sets, which is a `MemoryStream` and `MemoryMarshal`
copy of vertex and index arrays that are already laid out. There is nothing to
win there without changing what the file is.

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
