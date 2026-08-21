# Spectra Engine — Negative Brushes

> **Subtractive world geometry: a brush that removes solid instead of adding it, and what it costs to let one move under physics.**
>
> This document owns `Brush.Operation`, the composition rule the carve evaluates, the cavity-wall algorithm, and the physics-participation verdict. It is scheduled as `ROADMAP.md` `P7b`, and it depends on `P7a` (`BrushKind`), whose design lives in [`docs/physics.md`](physics.md) §2.3a.
>
> **Companion documents:** [`CLAUDE.md`](../CLAUDE.md) holds the pillars this design must not break — in particular *open world is first-class* and *BSP is a query structure only*; [`ROADMAP.md`](../ROADMAP.md) schedules `P7b`; [`docs/physics.md`](physics.md) owns `BrushKind` (§2.3a) and the static-collision decision (§2.1) that this design **falsifies and must amend before `Y3`**; [`docs/data-model.md`](data-model.md) is the counting authority for node fields and payloads; [`docs/formats-and-pipeline.md`](formats-and-pipeline.md) owns the `.smap`/`.scmap` records without which a negative brush does not survive save/load; [`docs/roblox-to-spectra.md`](roblox-to-spectra.md) owns the `NegateOperation` row this design finally answers.
>
> **Status: design only.** Nothing here was built, run, benchmarked or tested — the design pass was forbidden to compile. Every claim about current behaviour is a read of the working tree at **HEAD `9c7b41e`, 2026-08-21**, cited by file and member name; line numbers were read in that same session and will drift. Every claim about *cost* is a structural argument, never a measurement — §12 is the register of what that leaves unproven.

---

## 0. The decision, in one paragraph

**Add one byte to the immutable `Brush` value — `BrushOperation { Additive, Subtractive }` — and define the compiled solid as `⋃{additive} \ ⋃{subtractive}`, regularized, evaluated as an UNORDERED set expression.** A subtractive brush stays in the placement list, emits no skin of its own, and instead induces *cavity walls* seeded into each additive brush it cuts. Nothing under `Bsp/` learns a new concept: `BspTree`'s solid-leaf rule already derives solidity from plane orientation alone, so a cavity is representable with zero lines changed in the BSP, the queries, the snapper, the welder, the chunk grid or the mesh builder. The user asked for two things and gets both, at very different prices: a **baked** negative — a bullet hole, a crater, a breached wall — costs **one compile per destruction event** and is affordable today; a **moving** negative — a drill, a dissolving hole — is affordable only while its overlap set and its min-X rank hold, which is a condition this document reads out of the incremental compiler's actual refusal sites rather than guessing at, and which a third declared bit (`BrushMotion.Transient`, §8.3) removes entirely. The one thing refused by name is a freely tumbling negative through a dense world without that bit: it fires both fallback gates repeatedly, and each firing is a fully-validated O(world) compile *plus* an O(world-cells) render-thread rebuild, which is the open-world pillar dying in the most user-visible way there is.

---

## 1. The composition rule

### 1.1 The rule

> **RULING.** `solid = ⋃{admitted brushes with Operation == Additive} \ ⋃{admitted brushes with Operation == Subtractive}`, **regularized** (boundaries belong to the closure of the solid side), evaluated as an **unordered set expression**. Not an ordered CSG tree. Not a scoped tree. Every subtractive brush beats every additive one; subtractives compose with each other only by union.

"Admitted" is unchanged and is not this document's decision: it is `SceneNode.IsStaticWorldBrush` (`Scene/SceneNode.cs:231` — `_brush is not null && _brushKind == BrushKind.World`), consumed by the snapshot at `Scene.cs:1460` (fast path) and `Scene.cs:1511` (full walk). A subtractive brush is admitted exactly like any other world brush.

### 1.2 Why not an ordered tree

The objection is **not** mechanical, and I decline to manufacture one: placement index is stable between structural edits, and a structural edit already forces a full walk plus an untrusted carry through `Scene.MarkAdmissionChanged` (`Scene.cs:867`).

The objection is **authoring stability**. The only total order this engine has over admitted brushes is graph traversal order (`SnapshotFullWalk`, `Scene.cs:1511`). Under union, that order is consumed in exactly one place — `carverWins: o < b` at `Csg.cs:232`, read at `Csg.cs:383` — and it chooses between two *geometrically identical* coplanar faces, so a reparent is invisible. Under an ordered subtraction it would choose between *"there is a doorway here"* and *"there is a wall here"*: **dragging a node into a folder in the Explorer would silently rewrite world topology.** That is verbatim the failure `BrushKind` was written to forbid in the admission dimension (*"an admission predicate computed from mutable ancestry is how silent corruption happens"*, `physics.md` §2.3a), and it applies unchanged to a composition predicate.

An ordered rule would additionally force a **persistent, monotonic per-brush order key into `.smap`**, because a streamed open world cannot guarantee load order — a data-model commitment owned by [`docs/formats-and-pipeline.md`](formats-and-pipeline.md), not by this design. The unordered rule stores one byte per brush and nothing else.

### 1.3 Local evaluability, which is what the open-world pillar actually needs

For any point *p*, membership depends only on brushes whose volume contains *p*. Every such brush has *p* inside its world AABB, hence inside its `ChunkGrid.WeldBand`-inflated AABB, hence is **resident** in *p*'s cell — `ChunkGrid.Build` adds a resident for every cell in the inflated box (`ChunkGrid.cs:167-195`, the `AddResident(i)` call runs unconditionally for every cell in range). So the rule is decidable from one cell's resident set, which is the exact quantifier `ChunkBspBuilder`'s closure argument already ranges over. **No global pass, no map extents, no ordering.**

### 1.4 What order still decides, and why that is not a contradiction

Placement index survives in exactly the role it has today: a tie-break between *coincident* surfaces that are geometrically identical. It never decides which volume wins.

Under the repaired algorithm (§3.6) the most authoring-visible new decision — *"is this wall face removed or kept?"* — is decided by **plane facing alone** and carries no index tie-break at all. The single surviving order-sensitive decision in new code is between two *coincident duplicate cavity walls* produced by two coincident subtractive brushes, where the two candidates differ only in which negative's `FaceSurface` paints them: exactly the union case's harmlessness.

### 1.5 The one expressive loss, stated rather than hidden

Under a set model, **an additive brush placed inside a subtractive brush disappears entirely.** There is no "add it back afterwards". Hammer authors expect this (their carve is destructive); Roblox authors expect *scoped* negation — a `NegateOperation` subtracts only within its own `UnionOperation` — and will notice.

Two escapes, one free today and one a strict refinement:

- **Today, free:** a `BrushKind.Part` brush is not in the placement list (`SceneNode.IsStaticWorldBrush`, filtered at `Scene.cs:1460`), so a subtractive brush cannot remove it. *"Put a Part brush in the hole"* is the fill-a-hole affordance and needs zero engine work.
- **Later, strictly a refinement:** a per-brush `CarveScope` id compared pairwise (a subtractive brush cuts only additives sharing its scope). Still order-free, still locally evaluable — both brushes contain *p*, so both are resident. The unscoped rule **is** the scoped rule with all brushes in one scope, so scope can be added later without changing any world that does not use it. **Not decided here** (§11).

### 1.6 The correctness invariant is TWO invariants, not one

> **CORRECTION — an earlier draft of this design stated one invariant and called it sufficient.** It said: *"the surface set the carve emits is the closed, outward-oriented boundary of that regularized set — every emitted polygon has solid immediately behind its plane and air immediately in front of it,"* and proposed a single mechanical test over every emitted polygon. **That sentence contains two independent predicates and the proposed test checks only one of them.** *Orientation* is per-polygon; *closure* is a property of the whole boundary. Both defects this design's review pass found emitted **zero mis-oriented polygons** — one emitted nothing at all on the offending plane, the other left a gap between two correctly-oriented surfaces — so an orientation-only test passes on both. Since `BspTree` reads only `Polygon.Surface` and marks an exhausted back region solid (`BspTree.cs:86-87` with `:179-186`), a *missing* boundary polygon is silent: the tree simply answers from whatever plane bounded the region last. The invariant is therefore stated as two, and §10 pins them separately.

> **I1 — ORIENTATION.** Every polygon entering `localSurfaces`, face or cavity wall, has solid immediately behind its `Surface` plane and air immediately in front of it, and its winding is CCW about that normal.
>
> **I2 — CLOSURE.** The emitted set is a closed boundary: every directed plane region that separates solid from air carries **exactly one** surface — never zero, never two.

Everything downstream — `BspTree`'s solid-leaf rule, back-face culling, the future collision representation — is a consequence of **I1 and I2 together** and needs no subtraction awareness of its own.

---

## 2. Where the bit lives

### 2.1 The bit

```csharp
public enum BrushOperation : byte { Additive = 0, Subtractive = 1 }
```

**On the immutable `Brush` value**: `public BrushOperation Operation { get; }`, set at construction, default `Additive`, changed only by a successor factory `Brush.WithOperation(BrushOperation)`.

> **Naming ruling.** `BrushOperation` / `Additive` / `Subtractive`, not `BrushPolarity` / `Positive` / `Negative`: *"polarity"* collides with the physics vocabulary this engine is about to acquire, and *"operation"* names what the bit does to the union rather than describing the brush. The **user-facing verb stays "Negate"** and the UI word stays *negative brush*, matching the Roblox-onboarding pillar and Hammer/Unreal alike. Still owed and **not** decided here: the `.smap` token spelling (§2.5 proposes `"operation": "subtractive"`; `formats-and-pipeline.md` is normative) and the Luau binding name — an `O5`-class lock exactly like `BrushKind`'s.

### 2.2 Why `Brush` is the only correct home — three detectors, one key

Every mechanism that decides *"did this brush change?"* compares **`Brush` reference identity plus the placement matrix, and nothing else.** All three were re-read this session:

1. **`CsgCompileCache.TryGetValid`** keys on `placement.Brush` reference (dictionary built with `ReferenceEqualityComparer.Instance`), then validates `cached.Placement != placement.Transform` plus, per carver, `ReferenceEquals(records[k].Brush, carver.Brush) && records[k].Transform == carver.Transform && records[k].CarverWins == (o < index)`. A new `Brush` instance is a miss for the brush **and for every neighbour whose `CarverRecord` names it** — which is exactly the invalidation subtraction needs, since flipping one brush changes its neighbours' skins.
2. **`CsgIncrementalCompiler.SamePlacement`** is `ReferenceEquals(a.Brush, b.Brush) && a.Transform == b.Transform`, so an operation flip enters the changed set **C**.
3. **`Scene.CollectDirtyCells`** applies the identical test at **both** arms (fast path and full walk, over `NodeFootprint`), so a flip dirties the union of old and new footprints.

**Rejected: a `bool Subtractive` field on `BrushPlacement`.** `BrushPlacement` is `(Brush, Matrix4x4)` and nothing else. A flag there is invisible to all three detectors above: flipping it produces a cache **HIT** for the brush *and* every neighbour, `SamePlacement` reports no change, and `CollectDirtyCells` dirties **zero cells** — the world keeps the old skin forever with the compile counter still ticking and nothing at `ERR`. Release has no net: `VerifyTrustedDiff` is `[Conditional("DEBUG")]` and compares only `SamePlacement` anyway.

**Rejected: a `SceneNode.BrushOperation` read into the placement at snapshot time.** Same cache blindness, *plus* it makes one `Brush` instance mean different things on different nodes, which breaks two brush-keyed caches at once — `CsgCompileCache`'s dictionary and `PartBrushMeshCache`'s entry map (both keyed by `Brush` reference).

**Rejected: a third `BrushKind` value.** `BrushKind` lives on the node and its whole job is *admission*: `IsStaticWorldBrush => _brush is not null && _brushKind == BrushKind.World` (`SceneNode.cs:231`). A third value makes that predicate three-valued and re-admits simulated brushes to the placement list — the precise failure `physics.md` §2.3a exists to prevent. It is also carve-invisible: **nothing under `SpectraEngine.Core/Bsp/` names `BrushKind`** (the split lives entirely at the scene→placement boundary), whereas a subtractive brush must stay **in** the list and **change what the carve does**.

### 2.3 The interaction with `BrushKind` — two independent bits on two different objects

`BrushKind` answers *is this brush in the placement list*. `Operation` answers *what does it do to the list it is in*. All four combinations are meaningful and all four are legal:

| | `Additive` | `Subtractive` |
| --- | --- | --- |
| **`World`** | every brush authored so far | **the carving hole — the feature** |
| **`Part`** | today's part brush: `PartBrushMeshCache` + the part arm of `BuildRenderView` | **legal and inert, deliberately** |

**(Part, Subtractive) is not a nonsense state to refuse.** It is the *flying projectile* of physics tier 1 (§8.1), and it keeps a `SetBrushKindCommand` round trip lossless. It requires exactly **one word** of change to landed `BrushKind` code: `Scene.UpdatePartBrushMembership` (`Scene.cs:173-179`) currently admits any `node.Brush is not null && node.BrushKind == BrushKind.Part` into `_partBrushNodes`, which would make the part-mesh cache build and upload **the outward skin of a hole** — a solid block where the author asked for a void. Gate it on `Additive`.

> **CORRECTION — the mechanism by which that one word suffices is not the one an earlier draft named.** The draft said it is sufficient *"because `BuildRenderView`'s part arm only draws what `_partBrushMeshes.TryGet` returns"*. The conclusion holds; the stated mechanism is wrong, which is the same class of error this project keeps paying for. `TryGet` is not what stops a formerly-additive part drawing: the cache is populated from `_partBrushNodes` by `Scene.ProcessPartBrushMeshes` (`Scene.cs:509-523`) and entries are removed **only** by the mark-and-sweep in `PartBrushMeshCache.EndPump`. The two load-bearing facts are (i) `Brush.WithOperation` returns a **new instance**, so the flipped brush is a different cache key and is never `Acquire`d, and (ii) `ProcessPartBrushMeshes`' early-out is `_partBrushNodes.Count == 0 && _partBrushMeshes.Count == 0` (`Scene.cs:511`) — an **`&&`**, so the sweep still runs when the last part node leaves the set and the stale mesh is destroyed rather than stranded. A future edit turning that `&&` into `||`, or a `WithOperation` that mutated in place, breaks the claim while `TryGet` still "only draws what the cache returns".

Gating `_partBrushNodes` is **not** sufficient for the *outline*: `PartBrushOverlay` walks `Scene.PartBrushNodes` (`SpectraEngine.Editing/Viewport/PartBrushOverlay.cs:88`), so gating membership also silences the outline — the opposite of what an invisible brush needs.

> **RULING.** Keep `_partBrushNodes` gated on `Additive` for the **mesh** pump, and give subtractive brushes of **either kind** their own **kind-blind** always-on outline set (§9). A (Part, Subtractive) brush is therefore outlined and labelled *"not carving (Part)"* rather than silently absent.

### 2.4 Authoring surface — no new command type

The Negate verb is `new SetBrushCommand(node.Id, brush, brush.WithOperation(BrushOperation.Subtractive))`. `SetBrushCommand` already stores absolute before/after `Brush` references, which makes undo bit-exact, and `CLAUDE.md` already documents a brush swap as *"what invalidates that brush's cached carve"*. Bulk conversion batches into one `CompositeCommand`; N flips in one frame already cost **one** compile, because the pump launches at most one compile per frame and only on a version change.

### 2.5 Persistence

A negative brush that does not survive save/load is not a feature. `Operation` rides the `brush` record in `.smap` (`"operation": "subtractive"`, omitted iff additive) and a `PayloadFlags` bit in the `.scmap` `NODE` record. Both are specified normatively in [`docs/formats-and-pipeline.md`](formats-and-pipeline.md) §2.6/§2.7, which this document must not restate; the split of *where* each bit goes follows the code exactly — `BrushKind` is a **node** member because it is a `SceneNode` field, `Operation` is a **brush** member because it is on the `Brush` value.

---

## 3. The algorithm, stage by stage

### 3.1 `Brush.cs` — the bit and its propagation

Add `Operation` as a get-only property, assigned in the full constructor via a new optional parameter and defaulted `Additive` by the convenience constructors. Add `Brush WithOperation(BrushOperation)` shaped exactly like `WithFaceSurface` (`Brush.cs:246`): **return `this` on an equal write** — the same early-out discipline `WithScaledExtents` uses at `Brush.cs:318`, and the right answer for the carve cache, since nothing changed and the cached carve stays valid.

Prefer a **private copy constructor** that shares `_localPlanes`, `_localFaces`, `_faceSurfaces` and `LocalBounds` verbatim (all immutable; `Polygon` is deeply immutable by construction) over re-running `BuildFaces` + `RejectUnboundedVolume`: O(1) instead of a full re-clip, and provably identical geometry because no plane moved. **The copy constructor copies the current `Transform` value**, exactly as both existing successor factories do (`Brush.cs:254`, `:336`) — `Brush.Transform` is a mutable `{ get; set; }` (`Brush.cs:128`) used by the standalone `Csg.Carve(IReadOnlyList<Brush>)` / `ToPlacements` path, and dropping it would silently change behaviour for a flipped brush in tests and tools.

**Two existing `new Brush(...)` sites must thread `Operation` through or the bug is silent:** `WithFaceSurface` (`Brush.cs:254`) and `WithScaledExtents` (`Brush.cs:336`). If either drops it, **resizing or retexturing a hole turns it into a solid block.** That is the highest-value small test in the whole set (§10).

No validation changes. A subtractive brush is still a bounded convex solid, so `RejectDuplicatePlanes` (`Brush.cs:355`) and `RejectUnboundedVolume` run unchanged, and an unbounded subtractive brush is refused exactly as an unbounded additive one is.

### 3.2 Do not try to encode the sign geometrically

A `Brush` is by definition the intersection of half-spaces (`Brush.cs:8-12`); the complement of a convex solid is not convex and has no `Brush` representation. Negating a box's plane normals is a **no-op on the plane set** — `CreateBox` emits +X/−X, +Y/−Y, +Z/−Z each with `d = −halfExtent` (`Brush.cs:189-197`), so the negated set is the same set. And *"scale by −1"* is not a negative brush: `WithScaledExtents` refuses non-positive factors in as many words (`Brush.cs:339-348`), because a negative factor inverts every outward normal and turns the brush inside out. Both shortcuts produce either an exception or an identical brush.

### 3.3 `Polygon.Flipped()` — one new primitive, both channels, no other caller

```csharp
public Polygon Flipped() => new Polygon(reversedVertexArray, new Plane(-Surface.Normal, -Surface.D), Face);
```

Reversing the vertex order and negating the plane **in the same expression, in the only function in the engine that can produce a reversed polygon**, is the entire structural defence against the half-flip hazard. The two channels are genuinely independent downstream: winding drives rasterization (`CsgWorld.BuildMeshArrays` fans `(base, base+i, base+i+1)` in stored order, and all three forward pipelines cull back faces CCW-front) while `Surface` drives both the written per-vertex normal and **all** BSP solidity. With one constructor there is no code path that can flip one and not the other, so neither of the two silent wrong worlds is reachable.

**Name ruling:** `Flipped`, not `Reversed` — *"reversed"* names only the vertex order, which is precisely the half of the job that must never be done alone. `Polygon` stays deeply immutable: `Flipped` builds a new instance, for the reason `Polygon`'s own class doc gives (mutation would tear under the parallel carve).

### 3.4 `Csg.CarverInFrame` — two new fields, no behaviour change

Add `bool Subtractive` (from `carver.Brush.Operation`) and `Matrix4x4 Combined` — the carver-local→carved-local matrix `CarverInFrame.Build` already computes at `Csg.cs:454` and currently discards. `Wins` keeps its exact current meaning and construction: `carverWins: o < b` at `Csg.cs:232` is **untouched**, which is what keeps `CsgCompileCache`'s recorded `CarverWins` meaning what it means. Cost: `CarveScratch.Carvers` grows by 68 bytes per entry; it is worker-local, geometrically grown, and bounded by the max neighbour count. The carver's list position `k` is a loop variable at every site that needs it and is **not** stored.

### 3.5 `Csg.CarveSingle` — skin suppression, wall seeding, and emission order

**Skin suppression.** Gate the seed loop `foreach (Polygon face in placement.Brush.LocalFaces)` (`Csg.cs:241`) on `placement.Brush.Operation == BrushOperation.Additive`. A subtractive brush emits **no outward skin of its own, ever**.

> **INVARIANT.** A subtractive brush's carved array is **always length 0** — an invariant, not a case. That is a fully supported shape everywhere downstream: `Csg.Concatenate` copies a zero-length array fine, `ChunkGrid.AddOwned` → `WorldChunk._surfaces.AddRange([])` is a no-op, and — the one that matters — `ChunkMeshBuilder.Build`'s `if (chunk.WeldedSurfaces.Count > 0)` filter (`ChunkMeshBuilder.cs:71-75`, whose own comment says *"a cell whose owned brushes were all carved away would yield an empty mesh"*) stops a subtractive-only cell ever reaching `BuildArtifact`'s `float.MaxValue`/`float.MinValue` bounds seed. **The degenerate inverted `(MaxValue, MinValue)` RenderBounds is unreachable, not merely guarded.**

**Wall seeding, attributed to the CUT ADDITIVE brush's slot.** For each additive placement *P* (index *b*) and each of its carvers *k* with `carvers[k].Subtractive`, and only when `carvers[k].Bounds.Intersects(P.Brush.LocalBounds)`:

- **(a)** `Polygon local = face.Transformed(carvers[k].Combined)` for each face of the subtractive brush *N*. `Polygon.Transformed` (`Polygon.cs:267`) also maps the `FaceSurface` payload, which is what makes the material rule (§5) correct with zero extra plumbing.
- **(b)** `Polygon wall = local.Flipped()` — **the flip happens HERE, at seed construction.**
- **(c)** Clip the wall to inside(*P*) by the loop `Brush.BuildFaces` already runs: for each plane of `P.Brush.LocalPlanes`, `wall.Split(plane, out _, out Polygon? inside); wall = inside;`. A wall that clips away entirely contributes nothing. **This step is load-bearing twice over:** it is what makes the wall exist only where solid is actually removed, and — because `Polygon.Split` reports a Coplanar polygon on the FRONT side (`Polygon.cs:184-185`, `front = this; back = null`) — it is what kills a wall coincident with one of *P*'s **own** planes, with no special case anywhere.
- **(d)** Push the survivor through the **same** `current`/`next` carver ping-pong the ordinary faces use (`Csg.cs:246-255`), tagged as a wall seed with origin list position *k*, and **skipping carver *k* itself**.
- **(e)** Append survivors to `localSurfaces`, which the existing exact-size world-space emission at `Csg.cs:263-266` then transforms once.

Everything reads only other placements' *immutable* local geometry, exactly as `CarverInFrame.Build` already reads `carverBrush.LocalPlanes` and `.LocalBounds` — so **per-brush purity survives verbatim**, and the `Parallel.For` at `Csg.cs:165-191` and the incremental re-carve are both unchanged.

> **RULING — emission order inside `localSurfaces`, which an earlier draft left unstated.** All face seeds first, in `LocalFaces` order (today's loop, byte for byte); then all wall seeds, in **carver list order**, and within a carver in the subtractive brush's `LocalFaces` order. This is not a style preference: emission order is output-visible in three places — `Csg.Concatenate` copies each brush's array verbatim in slot order (`Csg.cs:274-288`); `BspTree.BuildFromSurfaces` consumes the list positionally and `ChooseSplitterIndex` strides by index over it, so tree shape is a function of order; and `CsgWorld.BuildMeshArrays` packs vertices and fan indices in array order, so the vertex buffer is too. The engine treats exactly this class of ordering as forbidden to leave loose (`Csg.cs:141-155`, `BrushBroadphase.cs:24-33`). **Appending walls after faces also makes §3.8's non-regression proof positional rather than argued:** a brush with no subtractive carver emits the identical array in the identical order.

> **RULING — return `Array.Empty<Polygon>()` rather than `new Polygon[0]`** when `localSurfaces.Count == 0`. `CsgMeshCache.OwnersMatch` is element-wise **reference** equality over the owned welded arrays, so a fresh zero-length array is a guaranteed miss for the negative's owner cell on every re-carve. Note this is **one** cell, not many: `ChunkGrid.Build` gives a placement's carved array to exactly one owner cell (`ChunkGrid.cs:190-191`, `if (coord == owner)`), so the review-pass claim that it misses "in every cell it is resident in" is **rejected** — residency does not carry owned surfaces. The ruling also changes behaviour for an *additive* brush that is fully annihilated today, which is why it is stated as a ruling and not slipped in. **UNVERIFIED:** whether `ChunkWelder` preserves reference identity for an empty input array, which is what the owner check actually compares.

### 3.6 `Csg.CarveFragment` — the repaired rule table

> **CORRECTION — this table is materially smaller than the one an earlier draft of this design specified, and two of that draft's eleven rows are RETRACTED as wrong rather than merely redundant.** The draft added rows (G) and (H) — *"WALL seed, ADDITIVE carver, coincident: drop the wall when same-facing, keep it when opposite-facing"* — and offered them as the derived mirror of rows (C)/(D), with the sanity check *"for every coincident plane, exactly one surface is emitted, chosen by facing alone."*
>
> **Row (G) opens solids, and here is a fully-determined counterexample.** All boxes axis-aligned, `z ∈ [0,1]`, placement order *P*, *P′*, *N*. *P* (additive, index 0) spans `x ∈ [0,10]`, `y ∈ [−2,2]`. *P′* (additive, index 1) spans `x ∈ [3,7]`, `y ∈ [0,2]` — an ordinary overlapping detail block, entirely inside *P*. *N* (subtractive, index 2) spans `x ∈ [4,6]`, `y ∈ [−2,0]` — a notch cut in *P*'s underside. The true solid needs a ceiling on `y = 0` over `x ∈ [4,6]`, normal −y, solid above. Under the draft's rules: **(a)** the wall from *N*'s +y face, flipped to `(0,−1,0)` at `y=0` and clipped to inside(*P*), meets additive carver *P′* whose bottom plane is `(0,−1,0)` at `y=0` — same plane, same facing — and row (G) drops it unconditionally; **(b)** *P′*'s own bottom face survives its encounter with *N* by row (D), then meets carver *P*, is buried inside it, and is dropped by the ordinary fall-through at `Csg.cs:395-401`. **No surface exists on `y = 0`.** A probe at `(5, 0.5, 0.5)` is in front of both surviving side walls and behind nothing, so `BspTree`'s back-edge rule reports solid wall material as **empty** — `CsgWorld.ContainsPoint` false, a visible hole up into the wall, and no exception anywhere (nothing in `ChunkGrid`, `ChunkWelder`, `TJunctionWelder`, `ChunkBspBuilder`, `ChunkMeshBuilder` or `BspTree` throws on a non-closed surface set).
>
> The draft's justification for row (G) — *"from the carver's side its face survives whole by row (D)"* — is a **premise about the carver, not a theorem**, and it fails for three independent reasons: the carver's face may be buried in a third additive brush (above); `Brush.LocalFaces` is explicitly **not** plane-indexed, so a carver can legally carry a plane with **no face at all** (`Brush.cs:133-140`, and `RejectUnboundedVolume` accepts such a plane); and the two verdicts are computed in **two different local frames** whose `Combined` matrices carry cancellation error comparable to `OffsetEpsilon` at the +8,000-unit distances the open-world pillar advertises.
>
> **The repair deletes rows (G) and (H) entirely** and replaces their job with a theorem about the clip (§3.7) — which is stronger, needs no code, and is immune to all three failure modes.

Two mechanical facts make the table small. First, `Polygon.Split` reports a Coplanar polygon on the **front** side (`front = this; back = null`, `Polygon.cs:184-185`), and `CarveFragment`'s generic path emits `front` and keeps `back` as `remaining` — so *the generic path already means "this carver removes nothing, keep the fragment whole and stop"* for any fragment coplanar with a carver plane. Second, the existing coplanar branch's `continue` (`Csg.cs:391-393`) already means *"drop the footprint"*: it skips the coincident plane, lets the remaining planes carve, and drops whatever survives to the loop end.

| Seed | Carver | Verdict |
| --- | --- | --- |
| **FACE** | **ADDITIVE** | **Today's code, unchanged, character for character** — `CoplanarOrientation` + `removeFootprint = orientation < 0 \|\| carver.Wins`, else generic keep-outside. |
| **FACE** | **SUBTRACTIVE** | **Bypass `CoplanarOrientation` entirely.** Per plane: if `Split` classifies the fragment **Coplanar** *and* `Dot(fragment.Surface.Normal, plane.Normal) > 0` → **drop footprint** (`continue`). Everything else → **the generic path, unchanged**. |
| **WALL** | its **ORIGIN** subtractive carver | **Skip the carver. Mandatory** — see below. |
| **WALL** | **ADDITIVE** | `carver.Wins` → the generic path; else **skip the carver**. |
| **WALL** | any other **SUBTRACTIVE** | As FACE/SUBTRACTIVE, with one addition: **Coplanar and opposite-facing** → keep whole **iff** `seedOriginListPosition < carverListPosition`, else drop footprint. |

Every row is either today's code or one condition. Reading them out:

- **The subtraction itself needs no new code.** A face meeting a non-coplanar subtractive carver takes the generic keep-outside path: the part inside the negative is dropped. That is the whole of it.
- **The flush through-cut** (a hole cut exactly through a slab's full thickness) is FACE/SUBTRACTIVE + Coplanar + same-facing: the negative's plane coincides with the face's and its interior is behind the face, so it removes the material the face bounded — drop the footprint. Both the slab's ±Y faces hit this simultaneously and the hole opens through both surfaces.
- **The flush rest** (a negative sitting *on* a wall face, removing nothing) is FACE/SUBTRACTIVE + Coplanar + opposite-facing: the generic path returns `front = this`, the whole face is emitted, and the loop stops. **This is where unmodified code fails**, because `CoplanarOrientation` returns −1 for that pair and today's interior-interface rule deletes the face footprint under a negative that removes nothing — an open solid.
- **The origin skip is mandatory, not an optimisation.** Without it, the wall meets its own negative's plane as Coplanar-and-opposite and the wall/wall tie-break's strict `<` compares the origin position against itself, deleting the wall.
- **Two coincident negatives** produce two identical walls; exactly one must emit. The tie-break is **carver list position**, not raw placement index, because list position is a pure function of the ordered carver sequence `CsgCompileCache` already records element-wise, whereas a carver's raw placement index is not recorded. It decides only *which negative's `FaceSurface` paints the shared patch*, never a topology.

> **CORRECTION — the coincidence predicate for subtractive carvers is NOT `Csg.CoplanarOrientation`, and this is a repair rather than a refinement.** `CoplanarOrientation` accepts `|ΔD| < OffsetEpsilon = 1e-3` and `dot > 1 − NormalEpsilon = 1e-4` (`Csg.cs:24-25`, `:406-414`), while `Polygon.Split` classifies at `Polygon.Epsilon = 1e-4` (`Polygon.cs:16`) — **ten times tighter in offset, and per-vertex rather than per-plane.** In the band where they disagree the rule's own premise ("the fragment lies ON the carver's plane") is false, and both new coplanar rows then decide about a plane the geometry is not on. Worked instance: a slab spanning `y ∈ [−2,4]` and a negative whose +y plane sits at `y = 4 − δ` with `δ = 5e-4`. `CoplanarOrientation` says coincident, so the slab's top-face footprint is dropped; but the replacement wall's vertices are `δ` from the slab's top plane with `δ > Polygon.Epsilon`, so the wall survives its clip at `y = 4 − δ` and the boundary is **open in a δ-tall ring** all round the cavity mouth. The normal band is worse: `dot > 1 − 1e-4` is ≈ 0.81°, so over a face of half-extent *L* the two planes diverge by up to ~0.008·*L* and the opening scales with face size rather than with epsilon.
>
> **Using `Split`'s own Coplanar classification instead is exactly right, not merely tighter:** it is the same tolerance that decides whether the replacement wall survives its clip, so the two decisions cannot disagree; and it is **fragment-local**, so the tilted case degrades into an ordinary split rather than a whole-footprint verdict. In the `(1e-4, 1e-3)` band the generic path then produces the correct answer on its own — the face is kept whole and the wall sits δ below it, closing the boundary.
>
> **`CoplanarOrientation` and `OffsetEpsilon` are untouched for additive carvers**, which is what keeps row 1 and the bit-identity oracles unchanged.

### 3.7 The wall/face partition theorem — what replaces rows (G) and (H)

> **THEOREM.** Let *W* be a cavity-wall fragment that survives step 3.5(c), seeded into cut brush *P*, on directed plane (π, **n**). Then:
>
> 1. **Every point of *W* is in the open interior of *P*.** A wall coincident with any plane of *P* is killed at the clip, because `Polygon.Split` reports Coplanar on the front side and the clip keeps `back`, which is null.
> 2. **Any additive brush *P′* ≠ *P* carrying a face on the same directed plane has that face deleted at exactly *W*'s points.** Those points are in `int(P)`, so no plane of *P* passes through them, so no coplanar branch fires for *P* as a carver of *P′*, so the ordinary keep-outside path applies and the buried part is dropped.
> 3. ***P* is guaranteed to be in *P′*'s carver list.** *P′*'s face point lies in `int(P)` and on `∂P′`, so the two closed solids intersect, so their AABBs intersect (`Aabb.Intersects` is inclusive), so the pair is discovered by the sweep — and `BrushBroadphase` records every pair into **both** lists (`result[i][…] = j` and `result[j][…] = i` in the replay loop).
>
> **Therefore exactly one surface exists on (π, **n**) at every point: the wall inside *P*, the face outside *P*.** The two regions are complementary by construction — the wall is clipped *to* `int(P)` and the face is clipped *out of* it — so there is no gap and no duplicate.

This is why a coincident additive carver may be met with *"keep whole"* rather than a facing-dependent drop: the wall lies on that carver's **boundary**, so no point of it is strictly inside, so the carver can remove nothing — the identical argument the flush-rest row uses. Deduplication is not a coplanar decision at all.

**What the theorem does not buy, stated honestly.** The partition boundary is *P*'s silhouette on π, evaluated in *P*'s frame for the wall and in *P′*'s frame for the face. Cross-frame rounding can therefore leave a hairline mismatch at that boundary. This is the **same class and magnitude** of mismatch two overlapping *additive* brushes' shared edge already has today — the arithmetic is `Matrix4x4.Invert` plus a plane transform in both cases — and it is what `VertexSnapper` and `TJunctionWelder` exist to absorb. It is **not** the whole-face flip the retracted rows (G)/(H) could produce. §10 pins it with the flush fixtures repeated at the bench's +8,000-unit offset.

### 3.8 Non-regression, positional rather than argued

With no subtractive brush anywhere in the world: `seedIsWall` is false everywhere (no wall seeds are produced), `carver.Subtractive` is false everywhere, the wall-seeding block is skipped by one predictable branch per brush, and the FACE/ADDITIVE row is today's code character for character. Because walls are **appended after** the face seeds (§3.5), the emitted array is positionally identical, not merely equivalent. `BrushBroadphase`, `ChunkGrid`, `ChunkWelder`, `VertexSnapper`, `TJunctionWelder`, `ChunkBspBuilder`, `ChunkMeshBuilder`, `BspTree` and every cache are untouched. **The existing bit-identity oracles and the `CsgBench openworld` verdict line therefore cannot move** — a claim about code paths, not a hope. (§7.5 is the separate, and more serious, point that the verdict is structurally *blind* to this feature.)

### 3.9 `CsgCompileCache` — zero changes, and that is load-bearing

Every new decision is already determined by the recorded inputs. `Operation` rides on the `Brush` reference. Wall geometry is a function of (carver brush, carver transform, carved transform) via `Combined`, all three recorded. The wall/wall tie-break is carver **list position**, a function of the recorded ordered sequence. There is no wall/face tie-break to record, because §3.7 replaced it with a theorem.

**Deliberately rejected: storing the carver's raw placement index in `CarverRecord`.** It would be strictly stronger (never a false hit) but would turn every index shift into a miss, and inserting a node early in the graph shifts every later slot in `SnapshotFullWalk` — *"add one brush"* would go from mostly-hits to a full world re-carve, a real regression on the gesture the bench's `add` column measures.

### 3.10 Snap, weld, chunk grid, per-material split — no change anywhere

This is a **consequence** of the cut-brush attribution, not luck. A cavity wall is inside the cut brush's convex solid, therefore inside `ComputeBounds(_localFaces)` = `Brush.LocalBounds` (`Brush.cs:114`), therefore inside `BrushPlacement.WorldBounds` and inside `ChunkGrid.InflatedBounds`. So the cut brush's owner cell, residency footprint, weld candidate set and `RenderBounds` union are all its *existing* ones, already sound, with no formula changes.

`VertexSnapper` and `TJunctionWelder` are orientation-agnostic and payload-agnostic by construction — the snapper carries `Surface` and `Face` verbatim and only moves vertices; the T-junction pass only ever grows a vertex list and never reads a normal — so a flipped polygon passes both unchanged and unnoticed, which is exactly what is wanted.

> **Do NOT add a subtraction flag to any of them, and do NOT introduce per-polygon cell assignment or clip-to-cell.** The per-brush bucketing in `ChunkGrid.Build` is what makes ownership single-valued and *"nothing is drawn twice"* trivially true; a flag would be dead weight that puts the bit-identity oracles at risk for nothing.

One genuine bonus, checked rather than assumed: `ChunkWelder.ComputeCandidateSets` gives brush *i* the residents of *i*'s own footprint cells, and a brush is resident in its own footprint, so **the wall and the cut brush's remaining faces are in the same array and the same candidate universe** — the cavity mouth gets T-junction repair with no new plumbing. (Verified as far as the candidate set; that the weld output is crack-free in practice is **UNVERIFIED**, §12.)

### 3.11 Why the flip belongs at seed construction

A competing proposal ruled *"reverse at emission, never before the clip loops"*, on the grounds that `Polygon.Surface` is preserved verbatim through splits and the coplanar tests read it every iteration, so an early reverse flips every coplanar verdict. The observation is correct and the ruling is backwards. **A wall is not a positive face that gets reversed; it is born as the boundary of the removed region**, and the entire rule table above is derived in the flipped convention — solid behind `Surface`, air in front, the same convention every other polygon in the engine obeys (`Brush.cs:8-12`). Flipping late would mean running a wall through the clip loops under the *unflipped* convention, where its coplanar verdicts read inverted — which is the very error the proposal warns about, relocated. One convention, no mid-loop reinterpretation; **I1** is the thing to test.

---

## 4. Solid-leaf classification and query semantics

> **Zero lines change in `BspNode`, `BspTree`, `BspRaycastHit`, `CsgWorld.ContainsPoint`, `CsgWorld.Raycast` or the 3D-DDA cell walk.** This is the single most valuable fact about the feature and it was verified line by line this session.

**Why cavities are already representable.** Solid/empty is set at build time and never derived at query time. `BspTree.BuildFromSurfaces` seeds `BuildNode(polygons, solidIfEmpty: false)` (`BspTree.cs:70-73`); an exhausted list becomes `BspNode.Leaf(solidIfEmpty)` (`:86-87`); the front recursion always passes `false` and the back recursion always `true` (`:179`, `:180`, `:184`, `:185`), and the comment at `:75-83` pins this as test-locked semantics. So a region is SOLID exactly when it exhausted the polygon list having last descended a **back** edge — a pure statement about oriented planes. A cavity wall's `Surface` normal points **into** the cavity (out of the remaining solid), so the cavity interior lands in FRONT of every wall plane → `Leaf(false)` → empty, and the surrounding shell stays behind → solid. **No third leaf state, no new node kind, no operation flag anywhere under `Bsp/`.** `BspTree` reads only `Surface` — never `Polygon.Face`, never a `Brush`, never a placement index — so there is nothing there that can be told a lie.

**The plane-identity check that had to be done.** Could a flipped wall plane be bit-identical to an additive face plane in the same cell, so that `BspTree`'s coplanar-consume branch (`surface.Normal.Equals(splitter.Normal) && surface.D == splitter.D`, `BspTree.cs:153`) consumes the wrong one? Only if the two are coincident — and §3.7's theorem guarantees a wall and a coincident additive face are never both present at the same point. Note also that `ChooseSplitterIndex`'s candidate dedup is deliberately **sign-sensitive** (`BspTree.cs:207-211`: *"a plane and its flipped twin bound solid on opposite sides and are genuinely different splitters"*), written for an unrelated purpose. That is exactly the property subtraction needs, and someone already wrote it down.

**Ray reporting is already correct, with no new code.** `TraceSegment` reports the entry normal as the crossed splitter plane oriented toward the incoming side. A ray whose origin sits inside a cavity is in an empty leaf, so the `ContainsPoint(origin)` fast-hit does not fire; the first empty→solid crossing is the cavity wall, and the reported normal points back into the cavity — the correct outward-from-solid normal. A ray approaching from outside the shell enters at the shell's outer face and never sees the cavity. The two degenerate reports are unchanged.

**The "unbounded reversed brush fills space with solid" hazard is UNREACHABLE BY CONSTRUCTION, not by a guard.** Flipped planes enter the world **only** as wall fragments already clipped to the interior of an additive convex solid (§3.5(c)). A subtractive brush in open air produces zero polygons (§3.5's invariant), so its planes are never in any tree, and `BspTree` never sees an inward-facing plane set with nothing bounding it. The pin is one line: a lone subtractive brush in an empty scene ⇒ `SurfaceCount == 0` and `ContainsPoint` false everywhere.

**Cell routing survives untouched.** `CsgWorld.ContainsPoint` routes through `Chunks.TryGet(ChunkCoord.FromPosition(point), …)`; an unoccupied cell is air by construction. A subtractive brush **is** resident in its footprint cells (`ChunkGrid.Build`'s `AddResident` runs unconditionally), so a cell containing only a subtractive brush exists in the grid with an empty owned contribution → `BuildFromSurfaces([])` → `Leaf(false)` = empty, identical to what an absent cell answers. The `RaycastIntervalEpsilon <= ChunkGrid.WeldBand` invariant is a statement about which brushes are resident within the overshoot band, and residency comes from the inflated AABB, which a subtractive brush produces exactly like an additive one.

**`SceneBvh` stays operation-blind, exactly as it stays kind-blind.** `SceneBvh.IsSpatial` is `node.MeshRenderer is not null || node.Brush is not null` (`SceneBvh.cs:146`) and must **not** learn about `Operation`, for the identical reason `physics.md` §2.3a gate 4 gives for parts: gating it drops the brush out of frustum culling **and** out of editor picking — and picking is the only way to select an invisible brush and move it. `RaycastBrush` is an exact convex slab test over `brush.LocalPlanes` and hits a subtractive brush precisely, which is what makes the feature usable at all. `Scene.Raycast` has no filter parameter and gains none here.

Two existing conventions inherit and must be **documented rather than repaired**:

- An invisible subtractive brush protruding from a wall **takes the click** — what you want while authoring, but a large negative over a room makes everything behind it unpickable until an editor-level *ignore negatives* pick modifier exists.
- `RaycastBrush` returns false when the ray *starts* inside the brush, so a camera standing inside a large hole cannot select the hole it is standing in — which, for an invisible object, reads as a broken tool rather than a convention.

**What the query surface still cannot answer.** `BspRaycastHit` is `(Point, Normal, Distance)` and nothing else, and `CsgWorld.Raycast` reconstructs the hit from the crossed splitter without ever touching a `Polygon`. So a drill cannot learn *what* it is boring through, or whether it is looking at a cavity wall or an outer face. Adding surface identity to the hit is a separate feature and is **not decided here** — but it is worth naming, because *"the drill sparks differently on metal"* is exactly the kind of effect the request gestures at.

**Doc work owed the same day.** `CsgWorld.ContainsPoint`/`Raycast` need **no third exclusion clause** alongside the part-brush demotion — a subtractive brush IS in the placement list and IS honoured, so the compiled world's queries are correct about holes — but their remarks should state the composition rule once, so a gameplay caller reads it in the right place.

---

## 5. Materials on cut faces

> **RULING.** A cavity wall wears the **subtractive brush's own per-plane `FaceSurface`** — material and texture axes together, carried verbatim from the negative's face through the flip, the clips and the transform. **You texture the inside of a hole by texturing the negative brush's faces.**

**Mechanism, all of which already exists.** `Brush.BuildFaces` gives plane *i*'s seed quad `faceSurfaces[i]` at construction; `Polygon.Split` propagates the payload to both sides unchanged (and says so in its own comment); `Polygon.Transformed` maps it through the matrix (`Polygon.cs:267-273`); and `Polygon.Flipped` passes `Face` through verbatim. So the wall seed already carries the negative brush's plane-*i* payload, and nothing is ever reattached post-hoc, so there is no path by which a wall wears the wrong plane's material. **Zero new plumbing.**

**The two-transform composition is exact for the rigid placements that are the only ones reaching a carve.** The wall is built in the cut brush *P*'s local frame by `Combined = N.Transform * Invert(P.Transform)` (`Csg.cs:454`) and pushed to world by `P.Transform` (`Csg.cs:263-266`), so the payload is acted on by `Combined` and then by `P.Transform` — net `N.Transform`. `FaceSurface.Transformed` is a genuine group action for rigid transforms (world-aligned faces return `this`; explicit axes apply `U' = R·U`, `o' = o − dot(t, U')/s`, and the composition checks out because `R` is orthogonal). **A cavity wall's world-space UVs equal what the negative brush's own face would have had** — the texture is glued to the negative brush and moves with it, which is precisely the authoring behaviour a moving hole wants.

**Two behaviours that must be stated rather than discovered.**

1. **A world-aligned cavity wall resolves to the SAME UV projection as the outward face it replaces, so it renders MIRRORED when seen from inside.** `FaceSurface.ResolveAxes` picks the dominant axis from `MathF.Abs` of the normal's components and is therefore **sign-insensitive**, so flipping the plane does not change the projection. Text or directional detail reads mirrored inside a hole. That is Hammer-consistent; it is a behaviour, not an accident, and repairing it would mean making `ResolveAxes` sign-sensitive, which would break the pinning test that keeps the pre-payload projection bit-identical.
2. **A cavity introduces the negative's material into the cut cell**, which drops that cell off `ChunkMeshBuilder.BuildSubmeshes`' uniform-material fast path and adds one submesh and one draw call for that cell. `ChunkMeshDelta` is cell-level, not (cell, material)-level, so the **whole cell artifact** is replaced — correct, but a real per-cut-cell cost that is invisible to anyone reading only the carve.

> **CORRECTION — RETRACTED: "UVs do not jump at the cavity mouth."** An earlier draft claimed sign-insensitivity buys exact texture continuity where the wall meets the cut brush's remaining face. It does not. A cavity mouth joins a wall on `∂N` to a cut face on `∂P`, and those planes are **non-coplanar by construction** — if they were coincident, §3.7 guarantees only one of them exists. `ResolveAxes` picks a projection from the dominant component of the face normal, so two non-parallel planes generally get different `(uAxis, vAxis)` pairs and the UVs are discontinuous across the mouth exactly as they are across any brush corner today. The sign-insensitivity claim is true and useful; *continuity across a cut* is not what it buys.

**REJECTED: Hammer's "cut faces inherit the cut solid's texture".** It sounds like the friendly default and it is not even well-defined here: a wall piece lands inside some additive brush, and that brush has six or more faces each with its own `FaceSurface`, with no natural correspondence to the negative's face that produced the piece. Implementing it would make a wall polygon's payload depend on *which* positive it landed in — a **decomposition-dependent material that changes when a neighbour moves** — and would require cross-brush payload copying at emission time. The chosen rule makes the wall a property of the negative brush alone: stable, authorable with the existing face-selection gesture, previewable on the outline, and cache-clean (it rides the immutable `Brush`, so changing it is a new instance and therefore a carve-cache miss by construction).

**The ergonomic mitigation, entirely in the editor over an existing API.** A one-click *"Match cut faces to &lt;node&gt;"* action that reads the target brush's `FaceSurfaces` and writes them onto the negative brush through `Brush.WithFaceSurface` inside a `SetBrushCommand`. No engine change, exact undo. **Not decided:** whether it is a context action, an inspector button, or a drag gesture.

**One default worth setting deliberately.** `Brush.CreateBox(min, max)` gives every face `FaceSurface.Default` — world-aligned, engine default material — and `Scene.ResolveWorldMaterial` degrades a default reference to `StaticWorldMaterial`. So a freshly drawn negative brush cuts holes whose walls wear the same default material the surrounding world does, which is the least surprising possible first experience and costs nothing.

---

## 6. The complete degenerate-case table

| Case | Verdict | Why, and what it costs |
| --- | --- | --- |
| **Subtractive fully inside an additive** (a sealed void) | Correct: a sealed cavity, `ContainsPoint` false inside it, render and queries agreeing. **No special case.** | The positive still emits its full outward skin; every face of the negative becomes a wall. The walls are meshed and uploaded and are **permanently occluded**, so a cell full of decorative internal voids pays real GPU cost for geometry nobody can see. It is also completely invisible from outside, which is exactly why the always-on outline (§9) is a correctness affordance rather than decoration. **Not decided:** whether the editor should warn. |
| **Subtractive fully containing an additive** (annihilation) | The pair contributes **zero** surfaces. Fully supported downstream. | Every face of the positive is buried and dropped by the existing loop; the negative's wall seeds clip to nothing because its boundary does not pass through the positive's interior. The positive still occupies a placement slot, is still resident, still owns a cell with an empty array, and `ChunkMeshBuilder`'s `WeldedSurfaces.Count > 0` filter simply produces no artifact. **But a brush that visibly does nothing is indistinguishable from a CSG bug**, so this MUST be surfaced (§9). |
| **Subtractive in empty space** | Zero surfaces, zero wall seeds, zero tree contribution. Its cell exists in the grid with no owned surfaces → `Leaf(false)` = empty, identical to what an absent cell answers. | This is the corrected form of the worst hazard the reading pass flagged: *"reversed planes make surrounding space solid"* is unreachable, because reversed planes only ever enter the world already clipped to the interior of an additive solid. **A world containing only subtractive brushes compiles to air everywhere.** |
| **Subtractive overlapping subtractive**, general (non-coplanar) | The removal is their union. **No tie-break needed at all** — each wall keeps the part outside the other through the ordinary keep-outside path. Symmetric, no duplicates, no gaps. | |
| **Back-to-back negatives** sharing a plane | Both walls drop. | Coplanar + same-facing against the other's plane: the other negative removes the solid behind this wall. By symmetry both drop, which is right — each one's far side is the other's air. |
| **Coincident same-facing negatives** | Exactly one wall emits, resolved by carver **list position**. | Detected as Coplanar + opposite-facing on a WALL seed. The tie-break decides only which negative's `FaceSurface` paints the shared patch, never a topology — the one place ordering still shows, and harmless for exactly the reason the union tie-break has been harmless since day one. |
| **Negative resting flush ON a wall face** (coplanar, opposite-facing) — the common authoring near-miss | The face survives **entire**. Derived from facing alone, so the answer cannot depend on traversal order. | **Where unmodified code fails silently:** today `CoplanarOrientation` returns −1 and the interior-interface rule deletes the wall's face footprint under a negative that removes nothing — an open solid, precisely what `Brush.RejectDuplicatePlanes`' comment exists to prevent, and it would flip `ContainsPoint`/`Raycast` through the opening with no exception anywhere. Worked check: wall top plane `((0,1,0), −2)`, negative bottom plane `((0,−1,0), +2)` → `dot = −1` → the generic path returns `front = this`. |
| **Hole cut exactly through a slab's full thickness** (coplanar, same-facing, both faces) | A clean through-hole with no cap; the four side planes produce the barrel. | Both the slab's ±Y faces drop their footprints. The negative's own ±Y wall seeds die at the clip-to-inside step, because they classify Coplanar against *P*'s own planes and `Split` returns `front = this, back = null`. **Must be a first-class test** — see §10. |
| **Cavity wall coincident with an additive carver's plane** (either facing) | The wall is **kept whole**; the coincident additive face is deleted by ordinary burial. Exactly one surface. | This is §3.7's theorem, and it is what replaced the retracted rows (G)/(H). The wall lies on the carver's boundary, so no point of it is strictly inside and the carver removes nothing. |
| **Subtractive straddling a chunk boundary** (static) | No new mechanism, no formula changes. | A cavity wall is inside the cut brush's convex solid, hence inside its `LocalBounds`, `WorldBounds` and `InflatedBounds` — so owner cell, residency, weld candidate set and `RenderBounds` are the cut brush's existing ones. Walls overhang their owner cell exactly as a large additive brush's surfaces already do, which is why culling tests `ChunkMesh.RenderBounds` and not `ChunkCoord.Bounds`. Snap displacement (≤ ~8.7e-5 at `GridSize = 1e-4`) is absorbed by `WeldBand = 2e-4`. |
| **Subtractive brush crossing a chunk boundary while moving** | **No fallback is forced** — there is no footprint gate anywhere in `CsgIncrementalCompiler.TryBuild`. But it is the fastest driver of `ChunkGrid`'s overlay compaction. | Named because it is the case tiers 2 and 3 depend on and an earlier draft never discussed it. The footprint change produces Added/Removed residency deltas and a fresh `WorldChunk` per affected cell, i.e. new overlay entries every crossing tick — see §7.4. |
| **Sliver loss at a grazing cut** | Not fixable in a principled way at this layer. Detected by the oracle, pinned by the sweep test. | `Polygon.Split` drops any side with fewer than three vertices. Under union that sliver was an interior fragment and its loss was harmless; a dropped **cavity wall** sliver is a gap in the boundary, whose practical effect is a BSP leaf whose solid/empty verdict is decided by rounding. Subtraction manufactures more of it, because near-tangent negatives are exactly the geometry that produces slivers and `Aabb.Intersects` counting touching means a merely tangent negative is already a carver. **Two distinct sites:** the carver loop, and the clip-to-inside step at §3.5(c), which an earlier draft never named. `TJunctionWelder` also skips edges shorter than Epsilon, so the thinnest cuts get no repair precisely where the crack would be. |
| **Snap separation at cavity-mouth corners** | Mechanism read from code; **frequency UNMEASURED.** | `VertexSnapper` rounds each component onto a 1e-4 lattice while preserving the pre-snap `Surface`, and every weld test is a strict comparison against `Polygon.Epsilon = 1e-4` — the same number. Two logically equal vertices about one ULP apart that straddle a half-step round to different lattice integers and end up a full epsilon apart, failing to weld. Subtraction manufactures more coincident-vertex pairs than union does, because **every cavity-mouth corner is a triple-plane intersection reached by two different clip sequences**. |
| **Subtractive brush on a Part node** | Legal, inert, **must not render**, **must still be outlined**, labelled *"not carving (Part)"*. | §2.3. Without the one-word `UpdatePartBrushMembership` gate the part-mesh cache builds and uploads the outward skin of a hole. The outline must come from the kind-blind subtractive set, not from `_partBrushNodes`. This combination is the flying projectile of §8.1. |
| **Subtractive brush resized or retextured** | `WithScaledExtents` and `WithFaceSurface` must both carry `Operation` through. | If either drops it, resizing a hole turns it into a solid block — the highest-value small test in the set. `WithScaledExtents`' `if (scale == Vector3.One) return this;` early-out stays correct and stays a cache hit. Mirroring is still not expressible and must not be smuggled in through a negative factor: **a subtractive brush is a perfectly ordinary outward-facing convex solid that happens to be flagged subtractive, never an inside-out one.** |

---

## 7. Chunking and the incremental compile — the honest cost

### 7.1 Dirty cells: unchanged, and correct for free

An operation flip produces a new `Brush` reference via `WithOperation`, so `Scene.CollectDirtyCells` sees it at **both** arms and dirties the union of old and new footprints. `ChunkGrid.ComputeFootprint` needs no change: a subtractive brush's footprint is its own inflated AABB exactly like an additive one's, and the cavity walls it induces live inside the **cut** brush's already-dirtied footprint.

This matters more than it looks, because `VerifyTrustedDiff` is `[Conditional("DEBUG")]` and checks only `SamePlacement` anyway — **Release has no net at all**, so a bit any of the three detectors could not see would produce silently stale geometry in a shipped build and a clean exception only in a debug run.

### 7.2 The trusted carry: unchanged, and operation-neutral

`CsgIncrementalCompiler.TryBuild`'s gates are AABB-and-index properties and `Operation` changes none of them: the placement-**count** gate (`:100`), the new-overlap-pair refusal `if (newNeighborSet.Count != surviving) return false` (`:198-199`), the single-hop rank gate (`:206-209`) and the two-hop rank gate (`:226-230`), all resting on `RankRelationStable`'s `oldRelation != 0 && newRelation != 0 && oldRelation == newRelation` over `min.X` (`:566-576` — the **exact-tie refusal is real**). A flip enters the changed set through `SamePlacement` and is patched like any brush swap. The scoping sets need no change either: **R** = C ∪ carried neighbours is already the right scope for a subtractive brush, because the brushes it cuts **are** its broadphase neighbours by construction. The re-carve goes through the identical `Csg.CarveSingle`, so patched output stays bit-identical to a from-scratch compile for every gesture the gates accept.

### 7.3 What a static negative costs to move once

Exactly what an additive brush costs, plus the same two fallback triggers an additive brush already has. Dragging a doorway negative around **inside one wall**, without reaching a second brush and without crossing a min-X rank, patches indefinitely at neighbourhood cost. Reaching a second wall costs **one** validated compile, then patching resumes. That is an authoring gesture at authoring frequency and it is affordable today, with no new mechanism.

**The validated fallback is unchanged and still O(world)**, including the half that is easy to miss: a fallback world carries `ChunkMeshDelta = null` and `PatchBaseId = 0`, so `Scene.ReplaceStaticWorld`'s delta gate fails and the **full per-cell rebuild runs on the render thread** — a per-artifact loop, a full stale sweep, and a Clear-and-re-add of the entire chunk-mesh map and list. Profiling only the background compile attributes none of it. It also lands on a **lazily-cached** patched world (`CsgWorld` constructs patched worlds with `lazyCaches: true` and null caches), so the fallback additionally materializes the compile, weld, BSP and mesh caches O(world) on first touch.

### 7.4 Two world-proportional terms in the *happy* path, and one of them is bigger than an earlier draft said

> **CORRECTION — an earlier draft called `ChunkGrid.Patch`'s overlay clone "a small constant … negligible at drag rates" and filed it as *flagged, not solved*. It is neither small nor constant: it is Θ(cells) amortized, and the code says so itself.** `ChunkGrid.Patch` clones the parent overlay on **every** patch (`ChunkGrid.cs:309-311`), and the overlay grows monotonically across a **chain** of patches until the threshold at `:315` (`overlay.Count > Math.Max(64, previous._base.Count / 8)`) triggers a compaction that rebuilds a flat dictionary over **every** chunk plus a full `ComputeCellBounds` (`:316-324`). The comment at `:305-308` names it: *"the one amortized O(cells) step, paid every ~base/8 edits."* So the mean clone is ~cells/16 **per patch** — a world-proportional per-tick term — and at ~20k cells that is a ~2,500-entry dictionary clone every tick with a 20k-entry rebuild every ~2,500 ticks.
>
> The second term is `PagedArray.WithReplacements`, which clones a page table of `count/1024` references roughly six times per compile.
>
> **Consequence that must be stated because a later tier depends on it:** this term alone is predicted to fail §8.3's *"per-tick median at 50k ≤ 1.5× the per-tick median at 1k"* acceptance gate, **for a reason that has nothing to do with subtraction**. A moving negative brush is simply the first workload that multiplies these constants by a tick rate.

### 7.5 The `openworld` verdict survives — and is structurally blind to this feature

**Does the verdict survive? Yes, by mechanism.** The verdict is `editMedians[^1] / editMedians[0]` over a MOVE of one **isolated additive** part by 0.3 on X, with the edit site deliberately chosen isolated. Every path that scenario touches is byte-for-byte unchanged (§3.8). The line cannot move.

**But that is not evidence about this feature, and pretending otherwise would be exactly the fake gate this repo has been burned by.** An isolated part has **no** overlap neighbours: `prevNeighbors` is empty, `newNeighborSet` is empty, `surviving == 0 == count`, and **neither rank gate ever executes** — the measured gesture is the one gesture that *cannot* fall back. A subtractive brush is by definition never isolated.

> **CORRECTION — the existing `openworld` harness also never CHAINS, which makes the whole family of chain-only costs invisible to the standing pillar gate today, before subtraction exists.** `Benchmarks/CsgBench/Program.cs:690` is `editedWorld = CsgWorld.Build(edited, dirtyCells, world);` inside the burst loop, and the comment above it states the intent plainly: *"Every burst iteration derives from the same previous world, so each one is the identical measurement."* A single step off a fixed base never grows the `ChunkGrid` overlay, never triggers compaction, never accumulates `PagedArray` page-table churn, and never lets a fallback land on a lazily-cached patched world. **A moving negative brush is by definition a chain.**

**Three instruments ship in the same commit as the feature, not as follow-ups:**

1. **`Scene.StaticWorldFallbackCount`**, counted **at `Scene.ReplaceStaticWorld`'s gate**, not from `ChunkMeshDelta is null`. The gate is two conjuncts — `world.ChunkMeshDelta is { } delta && world.PatchBaseId == published.Id` (`Scene.cs:1189`) — and a patched world whose base is not what is published *also* takes the full O(world-cells) rebuild. Counting only the first conjunct is a lower bound on the cost the instrument exists to surface. Without this counter, a moving subtractive brush degrades a large world with **nothing at `ERR` and every counter looking healthy**.
2. **A `negative` CsgBench scenario** at the existing 1k/10k/50k scattered worlds: one subtractive brush cutting one wall, measuring the move of the negative **inside** its wall over N ticks, and **chaining** (`world_n = Build(…, world_{n−1})`), which the scenario must state in its own header. Report three numbers per size: **(a)** the patched-tick median, **(b)** the fallback-tick median, **(c)** the fallback **rate** as a fraction of ticks. **Gate (a)** on world-size independence at the existing 1.5× band — that is the pillar claim and it is a real one. **Report (b) and (c) without a threshold**, and say so in the verdict line in the bench's own voice, because the rate is geometry-dependent and a threshold on it would be a gate that passes by choosing friendly test geometry — the same honesty the bench already applies to its `add` column.
3. **Two compile-stats figures the design already owed:** surfaces-per-brush and **max-carvers-per-brush**. §8.1 guarantees the second grows monotonically with destruction, and nothing measures it.

### 7.6 A doc correction this must land with

`SpectraEngine.Core/Scene/BrushKind.cs` states that a simulated brush *"would bail to the fully-validated O(world) compile **every tick, forever**"*, and `physics.md` §2.3a cites `CsgIncrementalCompiler.cs:99` and a `Scene.cs` line for it. **Both citations are the wrong gates** — `:99`/`:100` is the placement-**count** gate and the `Scene.cs` one is the graph-**structure**-version gate, and neither fires for a brush that merely moves — and **"every tick, forever" is too strong**. The gates that actually fire are `:198-199` (a new overlap pair) and `:206-209` / `:226-230` (a min-X rank crossing or exact tie); a brush whose overlap set and local rank order both hold **patches indefinitely at neighbourhood cost**. The conclusion `BrushKind` draws is still right for the general case, but the precise condition is the whole basis of §8.2 and §8.3, and the docs must be accurate about it before anyone relies on it. *(The `.cs` comment is out of this document's edit scope — `physics.md` §2.3a is amended in the same commit; the source comment is owed at `P7b`.)*

---

## 8. Physics participation — the verdict

**The honest axis is not *"is it moving"*. It is overlap-set stability and min-X rank stability** — the same axis `BrushKind` was drawn on, sharpened by reading the gates that actually fire. **A subtractive brush is not more expensive to move than an additive one; the gates are identical.** It is more *tempting* to move, which is why §7.5's instrumentation is part of the feature.

### 8.0 The prerequisite that is not optional

> **RULING — `physics.md` §2.1 must be amended before `Y3`, and this is a prerequisite of the physics arc rather than an editorial follow-up.** §2.1's whole mechanism is *"one hull shape per **owned** placement"*, built from `Brush.LocalFaces`, consuming `BrushPlacement`s and **never** carve output — and §2.4 states that *"physics never defines its own admission predicate — it consumes the one list."* A subtractive brush **is** in that list and looks like an ordinary bounded convex solid. **So a physics build that does not learn about `Operation` gives every negative brush a SOLID collision hull: a hole you can see through, that `CsgWorld.ContainsPoint` reports as empty, and that the player cannot walk through.** That is the render/collide divergence §2.3's decal refusal and this document's §10.5 both exist to prevent.
>
> §2.1 additionally carries a bullet that **inverts** under subtraction: *"A brush whose faces are entirely carved away still contributes its solid. Under a compiled-surface design that brush vanishes from collision — an invisible-but-solid pillar becoming walkable. Here the question never arises."* Under subtraction the question arises and the sign flips: an **annihilated** additive brush MUST vanish from collision, and a hull-per-placement design cannot express that.
>
> **The distinction that keeps the amendment small: physics must NOT learn `BrushKind`, but it MUST learn `Operation`.** `BrushKind` is an admission predicate and physics correctly inherits it by consuming the list. `Operation` is not an admission predicate — it is a property of the solid the list denotes, and the list's *semantics* changed under it.
>
> **The good news the amendment should carry: an EXACT convex decomposition is available from the authored planes alone**, with no CSG output and no decomposition library. For convex *P* and convex *N* with outward planes h₁…h_m, `P \ N` is the disjoint union over *k* of `P ∩ {h_k ≥ 0} ∩ {h₁ ≤ 0} ∩ … ∩ {h_(k−1) ≤ 0}` — *m* convex pieces, each a plane set, structurally the same recursion `CarveFragment` already walks. A doorway costs at most six hulls where it used to cost one; multiple negatives compound multiplicatively over the ones that actually overlap. **The pieces are plane sets handed straight to the hull builder's point computation, never `Brush` instances** — the negated h_i planes are routinely same-facing-coincident with a plane of *P* in exactly the flush-cut case, which `Brush.RejectDuplicatePlanes` throws on, and empty pieces are common.
>
> Whether §2.1 takes that decomposition, a per-cut-chunk trimesh, or refuses collision on cut geometry is **physics.md's decision**, not this one's — but it must state one, and `Y3` inherits a hard dependency on the answer.

### 8.1 Tier 1 — BAKE ON CONTACT. Ships with `P7b`. Zero new machinery.

**This is the 90% case and it is what "neat effects" usually means.**

The projectile is a **(Part, Subtractive)** brush while it flies. `BrushKind.Part` means it leaves the placement list entirely, so its per-tick transform writes signal literally nothing: the `Brush` setter's dirty arm fires only for a world brush, and `OnLocalTransformChanged` tests the static-world lane. **Cost while flying: zero compiles, provably.**

On contact, one command flips it to `BrushKind.World` (or spawns a World subtractive brush at the contact pose and deletes the flier). That routes through `Scene.MarkAdmissionChanged` → structure-version bump, static-world version bump, `_snapshotForceFull` → one full-walk snapshot and one **validated O(world) compile**. The brush then never moves again, so every subsequent tick is free.

**N bakes landing in the same frame cost ONE compile between them**, because the pump launches at most one compile per frame and only on a version change. A shotgun blast making thirty holes is one compile. That coalescing is already load-bearing for bulk `BrushKind` conversion; this reuses it rather than inventing a batcher.

> **CORRECTION — RETRACTED: "exactly ONE compile per destruction EVENT, at any projectile rate."** The per-event count is right; the *rate* clause is false and it hides the pillar failure. The bake changes the placement **count**, so `CsgIncrementalCompiler.TryBuild` refuses at `:100` and the compile takes the fully validated path — which then materializes four caches O(world) on a lazily-cached carry (§7.3), fails `ReplaceStaticWorld`'s delta gate, and runs the O(world-cells) render-thread rebuild. With one compile in flight, **destruction-event throughput is capped at one validated O(world) compile per event and the impact-to-hole latency equals one, both growing with world size.** At 50k parts and an automatic weapon, holes appear at whatever rate a 50k validated compile completes and queue behind each other. **No number for that exists anywhere in this repo** — the bench's `add` column is the only place it is even printed, and no value is recorded in `docs/` or `CLAUDE.md`. The honest statement is: one compile per event, event *rate* bounded by the validated compile time, and §7.5's `negative` scenario is what turns the bound into a number.

**What the user gets:** bullet holes, craters, blast damage, a door blown open, a mined block, a breached wall, a melted floor tile — permanent, fully carved, fully queryable geometry with correct BSP solidity and correct materials on the cut faces. This is Roblox's Negate-then-Union, made incremental and per-event.

**Two costs that grow, named and not solved.** Every baked negative stays in the placement list forever, so a heavily-destroyed world's placement count climbs monotonically **and each baked negative remains a carver of what it cut** — which is quadratic, not linear, in that brush's carve (§9.3). **Not deciding:** consolidation, a bake budget, or garbage-collecting negatives whose cut brushes were deleted.

### 8.2 Tier 2 — A MOVING HOLE WITH A STABLE OVERLAP SET. Affordable today, conditionally, and the condition is checkable in code.

The condition, read from the gates: the incremental path carries a moving subtractive brush indefinitely as long as **(i)** no NEW overlap pair forms (`:198-199`) and **(ii)** its `min.X` rank neither crosses nor exactly ties that of any surviving neighbour or any member of a surviving neighbour's carried list (`:206-209`, `:226-230`, `:566-576`).

A drill boring into one wall: tick 0 forms a new pair as the AABBs *touch* — `Aabb.Intersects` is inclusive, so this fires **before** anything visibly meets — costing one fallback. Then, while it advances inside that same wall and reaches no second brush, the overlap set is constant; boring along ±Y or ±Z keeps `min.X` constant and **every tick patches**; boring along ±X patches too, except on the ticks where its `min.X` actually crosses a neighbour's or a two-hop member's — **one fallback per crossing, not one per tick**.

> **CORRECTION — RETRACTED: "per patched tick the cost is bounded by the SIZE OF THE CUT BRUSH, not by the world."** The re-mesh half is; the bound as stated is the wrong quantity. `CsgIncrementalCompiler`'s scoping is: `weldCells` = the union of `ChunkGrid.ComputeFootprint` over every brush in the re-carve list; `reweld` = the union of `ResidentsAfter(cell)` over every cell in `weldCells`; `affectedCells` = `weldCells` ∪ the footprints of every brush in `weldList`. **The rebuilt-cell set is therefore the two-hop residency closure of the cells the cut brush touches** — every cell touched by every brush resident in any cell the cut brush touches. In the Roblox-density world this engine targets, a 500-unit slab covers ~256 cells whose residents number in the hundreds. The honest bound is **cut-brush footprint × local density**, world-size independent only under bounded local density — which is the right defence and is true asymptotically, but is not the sentence the draft wrote.

The re-mesh term is real and separate: `ChunkGrid.Build` gives a placement's entire carved array to ONE owner cell, and a cell whose owned welded input changed rebuilds its whole artifact from `chunk.WeldedSurfaces` and re-uploads every submesh. **A moving hole in a 500-unit slab re-meshes and re-uploads the entire slab every tick.** The authoring guidance follows directly and belongs in the docs: **cut small brushes** — and, from §8.1's quadratic, **subdivide a wall that will be shot at.**

**The free bound, which is a motion quantiser and not a compiler budget.** The pipeline cannot express a partial carve — every compile produces a complete world — and `ProcessStaticWorldCompilation` bounds only *concurrency* (one in flight) plus progressive results, which bounds nothing about the work. So the budget goes at the source: a policy that **quantises a simulated subtractive brush's position and rotation before the node transform is written**. The in-between ticks then cost **nothing at all**, because the transform setters early-out on exact equality and a no-op write dirties no cells. A drill dirtying the world at 10 Hz instead of 240 Hz is a 24× reduction with no new pipeline stage. **The trade-off must be stated:** quantising to a lattice makes exact `min.X` ties *more* likely, and `RankRelationStable` refuses an exact tie — so quantise onto a grid offset by a half-quantum from the authored world grid, or the mitigation buys fallbacks.

### 8.3 Tier 3 — THE TRANSIENT CARVE TAIL. New declared bit, own gate, lands separately.

The insight, and the source proves the premise: **both fallback gates protect exactly one thing — clip ORDER, i.e. the fragment decomposition.** `CsgIncrementalCompiler`'s own remarks say a permutation there *"would make some carried fragmentation differ bitwise from a from-scratch compile while remaining semantically identical (same solid union, same queries)."* **Order never changes the solid.** So: make a declared class of brushes occupy a **canonical** position in every carver list instead of a sweep-derived one, and there is nothing left for the gates to defend.

- **The bit:** `SceneNode.BrushMotion { Static = 0, Transient = 1 }` — non-inherited, declared and stamped, default `Static`, exactly the `BrushKind` discipline. Independent of both `BrushKind` and `Brush.Operation`. **It lives on the node, not on `Brush`, and the asymmetry with `Operation` is principled:** `Operation` changes the SOLID, so it must be visible to every change detector and therefore rides `Brush` identity; `Motion` changes only the DECOMPOSITION, cannot be flipped by a simulation, and goes through an admission-shaped door, so it needs no detector visibility at all. A motion flip calls a sibling of `Scene.MarkAdmissionChanged`: the structure-version bump makes the carry untrusted, so the very next compile takes the fully validated path, whose cache validation compares the ordered carver sequence element-wise and therefore misses every reordered list. Sound with no new field anywhere.
- **Broadphase:** `FindOverlaps(bounds, transientSlots)`. With `transientSlots.Count == 0` it runs today's exact code — **bit-identical output**, so every existing test and the `openworld` bench are untouched. Otherwise two passes: pass 1 is today's sweep with transient indices excluded from `order` and hence from `active`; pass 2 appends, for each brush, its overlapping transients in **ascending placement index**, and builds each transient's own list entirely by ascending index. Every non-transient's list becomes `[stable sweep prefix] ++ [canonical transient tail]`.
- **Why both gates disappear:** a transient's motion cannot perturb the non-transient sweep, because transients are not in it — so every carried prefix is **provably** unchanged, not merely trusted. A new (P, T) pair APPENDS into P's index-ordered tail rather than being inserted at a sweep-history-dependent position, so the new-pair trigger has nothing to defend. T's position in any list is index-derived, not rank-derived, so the min-X rank gates have nothing to defend. The patched lists are therefore **equal** to what a from-scratch compile of the same placements would build — strictly stronger than what the compiler calls trusted today.
- **Two gate corrections without which the tail buys nothing.** (i) The **new-pair gate must exclude pairs in which either member is transient**, or `if (newNeighborSet.Count != surviving) return false` still refuses the drill's very first tick and every tick after. (ii) The **two-hop rank gate must skip transient members of `carry.CarveNeighbors[j]`**, because a transient's position in *j*'s list is index-derived and its min-X rank is therefore irrelevant — leaving it in means a static brush is refused for crossing a transient it does not even overlap. Both are one condition each, and both are load-bearing.
- **Incremental compiler:** partition `changed` into transient and static; static changes take today's gates verbatim, so a mixed edit stays conservative. For each changed transient *T*, take the residents of *T*'s old and new bounds cells — `AddResidentsOfBoundsCells` already computes exactly this set — and rewrite each of their tails: drop all transients, re-append the currently-overlapping ones ascending. *T*'s own list is rebuilt ascending from the same candidate set. **This widens R beyond `C ∪ carried neighbours`**, because a brush *T* newly overlaps is not in *T*'s carried list; the widened set is the already-computed `overlapCandidates`, so it costs nothing new and it is what makes the patch equal to from-scratch.
- **What bounds it:** per-tick cost is proportional to the transient's **footprint**, so the one way to make it catastrophic is a huge transient — a 320-unit brush covers ~1000 cells and would re-weld and re-mesh all of them every tick. The `BrushMotion` setter therefore **refuses** `Transient` when `ChunkGrid.ComputeFootprint(placement).Length` exceeds a cap (propose 27 = 3×3×3 cells, roughly 64 units), naming the fix. Because a footprint can grow later via a resize, the snapshot additionally **logs once per node** when a transient outgrows the cap — a log, never an abort, because aborting a whole snapshot on a background thread is worse than what it prevents.
- **The gate it must ship with, or it does not land:** an `openworld --transient` bench sweeping one transient subtractive brush through a corridor of parts for 200 ticks at 1k/10k/50k, **chaining** (§7.5), printing the per-tick median AND the fallback count. Acceptance: **fallback count zero after the first tick, and per-tick median at 50k ≤ 1.5× the per-tick median at 1k.** *(§7.4 predicts the overlay-clone term alone may fail this for reasons unrelated to subtraction; if it does, the fix is in `ChunkGrid.Patch`, and that is a finding, not a reason to weaken the gate.)*

### 8.4 K simultaneous movers — priced, because a fallback is GLOBAL

Every performance statement above is written for one brush, and K is strictly worse than K × one, for a mechanical reason: `CsgIncrementalCompiler.TryBuild` returns `false` **for the whole compile** at `:199`, `:209` and `:229`, not per brush. So **one mover forming a new pair or crossing a min-X rank makes every other mover in that tick pay the validated O(world) compile too**, and P(patch) decays as the product over movers. At K ≈ 10 in a grid-aligned world — where `RankRelationStable`'s exact-tie clause fires constantly — essentially every tick is a fallback. Two further K-specific costs: two movers whose AABBs come to touch **each other** form a new pair (inclusive `Intersects`, so before they visibly meet); and the gate evaluation itself is O(K · |neighbours| · |two-hop|) per tick before any geometry is touched. Tier 3 fixes the fallback half in principle, but its 27-cell cap is **per transient with no aggregate budget**, and `ProcessStaticWorldCompilation` launches one compile per frame — so K transients share one compile whose cost is the union of K neighbourhoods, and nothing caps K.

### 8.5 Refused, by name

- **(a) A freely tumbling subtractive brush through a dense world, without the tail.** In a Roblox-style field of part-sized brushes a moving brush gains a leading-edge partner and loses a trailing one every few ticks, and grid-aligned worlds manufacture exact `min.X` ties, so both gates fire repeatedly; each firing costs a fully validated compile *plus* the O(world-cells) render-thread rebuild, and with one compile in flight the visible hole lags the brush by whole frames **with the lag growing with world size** — the pillar dying in the most user-visible way possible. The honest product answer is that a tumbling negative becomes a tier-1 **bake on contact**, which is what the user wants from a grenade anyway: a hole where it hit, not a continuous swept subtraction through the air.
- **(b) A dynamic body ON a subtractive brush.** A hole has no solid to collide with, so a dynamic body on one would be simulating a proxy volume with undefined contact semantics; `physics.md` §2.3a's refusal of a dynamic body on a World brush node **stands unchanged**. The supported composition for *"a rock falls and gouges a crater"* is two nodes and one transform: a Part brush with a real dynamic body, and a World+Subtractive(+Transient) brush parented to it or driven from it. **The transient negative is kinematically driven, never simulated** — and that is a real answer rather than a dodge, because it delivers the drill, the moving hole and the crater while costing the design nothing it was not already committed to.
- **(c) Deriving "this brush is moving" from recent compile history** instead of declaring `BrushMotion`: an admission-class predicate computed from mutable state is exactly what `BrushKind` forbids, and it would make fallback behaviour depend on how recently the user dragged something.

---

## 9. The editor story — an invisible solid must still be visible to its author

### 9.1 The always-on outline is a correctness affordance, not decoration

A subtractive brush renders nothing: its own skin is suppressed (§3.5) and `Scene.BuildRenderView` emits `RenderItem`s only for `MeshRenderer` nodes and for (Part, Additive) brushes. So it MUST be outlined **always, never only on selection** — the same non-negotiable `physics.md` §2.3a imposes on part outlines, and commit `d4701d6`'s lesson that an unmarked always-on discrepancy gets reported as an engine bug.

**Mechanism.** A `HashSet<SceneNode> _subtractiveBrushNodes` maintained from exactly the four places `_partBrushNodes` already is — `OnNodeAdded` (`Scene.cs:132`), `OnNodeRemoved` (`:149`), `OnNodeSpatialComponentChanged` (`:167`) and `MarkAdmissionChanged` (`:872`) — and it must be **kind-blind**, so both (World, Subtractive) and (Part, Subtractive) are outlined. Exposed as `Scene.SubtractiveBrushNodes` and consumed by a sibling of `PartBrushOverlay` (`PartBrushOverlay.cs:88` is the exact shape to copy, and its `DrawBrushEdges` is already `public static`), drawing one closed `DebugDraw.Polyline` per face of `Brush.LocalFaces` through the existing depth-off line pass that already works on all three backends.

**Visual language, and it must not be colour alone.** A part brush is solid-rendered plus an outline; a subtractive brush is **outline only, never solid at any time**, in a distinct colour, **plus an inward chevron on each face** — a two-line tick from the face centre along the negated normal. The chevron is what survives a greyscale screenshot and a colourblind user, and it is what distinguishes *a hole* from *a part whose mesh has not uploaded yet*. A (Part, Subtractive) brush additionally labels *"not carving (Part)"*, so inert is never mistaken for broken.

### 9.2 The over-carve annotation is not optional

An additive brush entirely inside a subtractive one emits a zero-length array and **vanishes**, which is indistinguishable from a CSG bug. Cheapest honest surfacing: `CsgWorld` reports the count of admitted **additive** placements whose carved array is empty (subtractive ones are empty by invariant and must not be counted), the stats line prints it, and the node inspector says *"fully carved away"* for a selected node in that state.

### 9.3 Stats

> **CORRECTION — there is no `WorldBrushes` counter anywhere in the tree, and the periodic line is not the editor's.** `physics.md` §2.3a and `ROADMAP.md` `P7a` both promise *"`WorldBrushes: N  PartBrushes: M`"*; what landed is `SceneManager`'s line in **Core**, whose brush half reads `"{PartsVisible} of {PartsTotal} part brush(es)"` from `RenderView.PartBrushesVisible`/`PartBrushesTotal` (`Scene/SceneManager.cs:585-607`), with no world-brush count at all. So the additions below are **new `Scene`/`RenderView` surface plus new format arguments in Core**, not an addition to an existing pair of counters in the editor.

Four figures join that line: `SubtractiveBrushes: K`, `Fallbacks: F` (§7.5 item 1), the over-carve count (§9.2), and the two compile-stats figures of §7.5 item 3 (surfaces-per-brush, max-carvers-per-brush).

### 9.4 Commands

**None new.** Negate is `SetBrushCommand`; kind conversion is the existing `SetBrushKindCommand` (note: `physics.md` §2.3a and three other documents still call it `ConvertBrushKindCommand`, which is the stale name — `SpectraEngine.Editing/Commands/SetBrushKindCommand.cs` is what exists); bulk operations batch into `CompositeCommand`.

---

## 10. The test pins

In priority order. **(1), (2) and (5) are the three that would have caught the defects this design's own review pass found**, and the third of them is new.

1. **The Monte-Carlo semantic oracle.** Over randomized scenes of additive and subtractive boxes and wedges, sample N points and assert `CsgWorld.ContainsPoint(p)` equals the direct predicate `(∃ additive P: p inside all P.LocalPlanes) && (∄ subtractive N: p inside all N.LocalPlanes)` computed straight from the placements. **The generator is the test.** Every rule this design adds fires only when a fragment is *coplanar* with a carver plane, and float-random boxes reach that with probability effectively zero — so the generator must place brushes on a **coarse snap lattice** (the editor's own 0.25/0.5/1/2/4 ladder), **deliberately share plane offsets**, include an **additive brush embedded in another additive brush** (which no current fixture has, and which is the ordinary Roblox-style overlapping-parts case *and* the shape of the retracted row (G)'s counterexample), and **probe within `Polygon.Epsilon` of every pair of near-coincident planes** rather than uniformly. Without those four properties it is a test of the unchanged path.
2. **The bit-identity non-regression.** A world with no subtractive brush compiles bit-identically to today over the existing `CsgCarveTests` fixtures — §3.8's positional argument made executable.
3. **`Operation` survives `WithScaledExtents` and `WithFaceSurface`.** A resized or retextured hole is still a hole. Cheapest test in the set, highest silent-failure cost.
4. **The flush-coplanar fixture set**, which is where a suite validated only on non-flush geometry passes while shipping a wall-deleting bug: a subtractive brush resting flush ON a wall face (the wall's face must survive **entire**; unmodified code deletes it); a hole cut exactly through a slab's full thickness (both faces drop simultaneously, no cap); two coincident subtractive brushes (exactly one wall); and the **embedded-detail-block fixture** of §3.6's correction (*P* `x∈[0,10] y∈[−2,2]`, *P′* `x∈[3,7] y∈[0,2]`, *N* `x∈[4,6] y∈[−2,0]`) — assert exactly one surface on `y = 0` over `x ∈ [4,6]` and assert `ContainsPoint(5, 0.5, 0.5)` is **true**, in **both** orderings of *P* and *P′* in the placement list.
5. **The closure test — new, and the one that separates a correct build from the two silent wrong worlds.** Orientation alone is not enough (§1.6). Assert **I1** mechanically over every emitted polygon (winding CCW about its own `Surface.Normal`; a point an epsilon behind the plane is inside the compiled solid and a point an epsilon in front is outside), **and separately assert I2**: per cell, either an **edge-manifold count** (every directed edge appears exactly once in each direction over the welded surface set) or a **ray-parity check** (every ray from outside crosses the surface set an even number of times). A surface-**count** assertion does not substitute: in the epsilon band of §3.6's correction the count goes *up* while the boundary opens.
6. **The epsilon-band sweep.** Translate a subtractive brush across a wall face across the whole `(1e-4, 1e-3)` window, **in both signs of δ** and in **both** the same-facing and opposite-facing configurations, asserting **closure** at every step — not surface count. Sweeping only up to `Polygon.Epsilon`, or only on the side where the negative protrudes, misses the entire failure window.
7. **Every flush fixture repeated at the `openworld` bench's +8,000-unit offset**, with the same surface count and the same closure verdict. §3.7's partition boundary is evaluated in two different local frames whose `Combined` matrices carry cancellation error at that distance; fixtures near the origin cannot see it.
8. **The lone-negative pins:** a subtractive brush alone in an empty scene ⇒ `SurfaceCount == 0` and `ContainsPoint` false everywhere; a world containing only subtractive brushes compiles to air.
9. **The instrument pins:** `Scene.StaticWorldFallbackCount` increments on a real fallback and stays zero across a patched drag; the `negative` bench scenario chains and prints (a)/(b)/(c).

---

## 11. What this document does NOT decide

- **The `.smap` token and Luau binding spelling** for `Operation` and (if it ships) `BrushMotion`. `formats-and-pipeline.md` is normative for the file token; the Luau name is an `O5`-class lock.
- **Whether scoped negation (`CarveScope`) is ever wanted.** It is a strict refinement and can be added later without changing any world that does not use it (§1.5).
- **Whether `BspRaycastHit` gains surface identity**, so a drill can learn what it is cutting.
- **The editor's *ignore negatives* pick modifier**, and any click-through affordance for a negative that steals clicks (§4).
- **The shape of the over-carve annotation** — badge, lens, or log line.
- **Whether a sealed void should warn.**
- **Whether subtractive outlines are suppressed in Play mode.** The same open question already recorded for part outlines, where commit `d4701d6`'s lesson argues against suppression and nothing has ruled.
- **Whether more than one compile per frame should be allowed for transients.**
- **Whether `BrushMotion = Transient` should ever be legal on an ADDITIVE World brush.** The mechanism supports it; nothing has decided whether it should be offered.
- **Consolidation, a bake budget, or garbage collection of baked negatives** whose cut brushes were deleted (§8.1).
- **Which representation `physics.md` §2.1 adopts** for cut geometry — the exact convex decomposition, a per-cut-chunk trimesh, or a refusal (§8.0). This document supplies the decomposition and the constraint; the choice is physics.md's.

---

## 12. Unbuilt, unmeasured, and retracted

**Nothing here was built, run, benchmarked or tested.** Every cost statement is a structural reading of source at HEAD `9c7b41e`, 2026-08-21. Specifically **UNVERIFIED**:

- **The absolute cost of a validated fallback at 1k / 10k / 50k.** No number exists anywhere in this repo. §8's entire tiering turns on *"one fallback is affordable, N per second is not"*, and that ordering is a judgement about a quantity nobody has measured.
- **The fallback rate for any real trajectory.** The gate predicates are read from code; no trajectory was simulated.
- **Sliver-and-crack frequency**, and whether `ChunkWelder.WeldCell` and `TJunctionWelder` produce a crack-free cavity mouth in practice. What *was* verified is that the wall and the cut brush's own faces land in the same array and the same candidate universe — not that the weld output is crack-free.
- **The always-on outline's per-frame cost at spawn scale.** `physics.md` records the equivalent claim for part outlines as reasoning rather than measurement; this design inherits that liability and does not discharge it.
- **Whether `ChunkWelder` preserves reference identity for an empty input array** (§3.5's `Array.Empty` ruling).
- **Determinism across `Vector<float>.Count`.** `Polygon.ClassifyInto` routes to `SimdPlane` only when `_vertices.Length >= Vector<float>.Count * 2` (`Polygon.cs:124`) and `SimdPlane`'s tail falls back to `Plane.DotCoordinate`, so a vertex's arithmetic path depends on its lane index and on the host's vector width. **Pre-existing**, and no test or gate covers it — but subtraction multiplies the number of near-tolerance classifications, and under union a last-ulp disagreement at the ±Epsilon boundary moved an interior fragment while under subtraction it decides between a wall and a face at a cavity mouth. Named, not solved.
- **Line numbers.** `Scene.cs` and `SceneNode.cs` were re-read this session but are under active edit; anchor by member name, not by line.

**Claims retracted by this document** (each argued at its site rather than deleted): the eleven-row coplanar rule table, and rows (G) and (H) specifically, together with the *"exactly one surface per coincident plane, chosen by facing alone"* sanity check (§3.6); the use of `Csg.CoplanarOrientation` as the coincidence predicate for subtractive carvers (§3.6); the single-sentence correctness invariant (§1.6); *"UVs do not jump at the cavity mouth"* (§5); *"one compile per destruction event **at any projectile rate**"* (§8.1); *"per patched tick the cost is bounded by the size of the cut brush"* (§8.2); *"the overlay clone is negligible at drag rates"* (§7.4); *"a cut brush's added carve work is bounded per (P, N) pair"* and the parallel-load-balance argument's generality (§8.1, §7.5 item 3); the `TryGet`-based explanation of why one word suffices in `UpdatePartBrushMembership` (§2.3); and *"the stats line gains a counter beside the existing `WorldBrushes`/`PartBrushes` pair"* (§9.3).

**One review finding rejected with evidence:** that a subtractive brush's freshly-allocated empty carved array misses `CsgMeshCache`/`CsgBspCache` *"in every cell it is RESIDENT in"*. `CsgMeshCache.OwnersMatch` compares the cell's **owned** welded arrays, and `ChunkGrid.Build` gives a placement's array to exactly one owner cell (`if (coord == owner) chunk.AddOwned(…)`). The miss is in **one** cell. The `Array.Empty` ruling stands on its own merits (§3.5).

---

## 13. Reconciliation ledger — what else changed, and where

| Document | Change | Why |
| --- | --- | --- |
| [`ROADMAP.md`](../ROADMAP.md) | New milestone **`P7b`**; companion table grows to nine; `P7a` gains `P7b` as a dependent; §12's standing gate cites the chain-blindness of `openworld`. | This document owns an arc of one milestone and must be findable from the index. |
| [`docs/physics.md`](physics.md) | §2.1 gains the **prerequisite** correction of §8.0 (subtractive brushes in the placement list; the inverted "entirely carved away" bullet; the convex decomposition and its plane-set-not-`Brush` constraint); §2.3a gains the negative-brush interaction and the `UpdatePartBrushMembership` word; §2.3a's *"does NOT decide"* and §7 item 10 lose the enum-spelling sign-off (now decided). | §2.1's premise is falsified by subtraction, and a physics build against the current text gives every hole a solid hull. |
| [`docs/data-model.md`](data-model.md) | `Brush.Operation` recorded as a **field on the `Brush` payload**, not a new payload and not a node field; the planned-additions and summary tables gain rows. | It is the counting authority, and the `Operation`/`BrushKind` split is exactly the kind of thing that miscounts. |
| [`docs/formats-and-pipeline.md`](formats-and-pipeline.md) | `.smap` gains `"kind"` (node member) and `"operation"` (brush member); the reserved-key list grows; `.scmap` `PayloadKind` **2** is renamed `PartBrush` and **3** `BrushModel` is retired; the cooked `IsStaticWorldBrush` flag is renamed `BakedIntoChunks`. | Without a route, a negative brush and a part brush do not survive save/load — that is data loss, not a gap. |
| [`docs/roblox-to-spectra.md`](roblox-to-spectra.md) | The `UnionOperation`/`NegateOperation` row gains a real answer instead of *"deliberate difference"*. | Negation now exists; the row as written is wrong. |
