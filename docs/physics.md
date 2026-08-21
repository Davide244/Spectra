# Spectra Engine — Physics

> The physics decision: which engine, how it meets a scene graph whose static geometry is already convex, and what "networked physics" means for a project that chose state replication with client prediction rather than deterministic lockstep.
>
> **This document also owns the world/part brush split (§2.3a).** `SceneNode.BrushKind` is a scene-graph feature that needs no physics engine, but it exists because of physics — a brush that simulates cannot be in the fused world — so its design lives here and other documents cite it as `physics.md` §2.3a.
>
> **Companion documents:** [`CLAUDE.md`](../CLAUDE.md) holds the pillars this design must not break; [`ROADMAP.md`](../ROADMAP.md) holds the `F/E/P/S/R/H` arcs; [`docs/negative-brushes.md`](negative-brushes.md) owns `Brush.Operation` (`P7b`) and **amends §2.1 of this document, which is a prerequisite of `Y3` rather than a follow-up**; [`docs/networking.md`](networking.md) owns the fixed tick (`N2`), prediction (`N16`) and interpolation (`N17`); [`docs/realms.md`](realms.md) owns admission and liveness; [`docs/formats-and-pipeline.md`](formats-and-pipeline.md) owns `.scmap`/`.smodel`; [`docs/roblox-onboarding.md`](roblox-onboarding.md) owns Luau and Play/Stop; [`docs/roblox-to-spectra.md`](roblox-to-spectra.md) is the concept mapping this arc changes the most rows in.
>
> **Status: design only.** Nothing here was built, run or measured. Every external claim carries its source and its date so a reader can re-check it; every internal claim carries the file it was read from. Sizes are relative (**S / M / L**), never calendar estimates.

---

## 0. The decision, in one paragraph

**Adopt Box3D** (`github.com/erincatto/box3d`), vendored at a pinned commit and bound by Spectra's own `[LibraryImport]` layer, with **Jolt behind a Spectra-owned C++ shim as the named fallback**. The user proposed it, and on the evidence the user was right about existence, right about immaturity, right that a C library costs a NativeAOT build almost nothing — and wrong about the reason they gave. "Network-friendliness" in Box3D's own documentation means *lockstep-grade* determinism (same inputs → same outputs, across ISAs, invariant to thread count), which is precisely the property [`docs/networking.md`](networking.md) §4.2 decided **not** to buy. What this engine needs is cheap rewind and fast re-simulation, and Box3D's FAQ disclaims exactly that in writing. The conclusion survives anyway, for a different reason: the only predicted subsystem [`docs/networking.md`](networking.md) schedules is one kinematic character mover whose entire state is an engine-owned value struct, and Box3D's character mover is *stateless by design* — so rewind is a struct copy and the missing snapshot API is never called. The bet is sequenced to stay revocable: the first milestones are query-only and write-free, and every native call in them has a direct Jolt equivalent, so a swap is a binding rewrite rather than a design rewrite.

---

## 1. The engine choice, the evidence, and the maturity risk

### 1.1 What was verified, and when

Everything in this table was fetched from primary sources on **2026-08-21** and can be re-checked at the URL given. Nothing here is asserted from model memory; Box3D post-dates the assistant's knowledge cutoff, which is why the user's information was newer and correct.

| Claim | Evidence | Source |
| --- | --- | --- |
| The repository exists and is Erin Catto's | `full_name` `erincatto/box3d`, description *"Box3D is a 3D physics engine for games"*, 6,121 stars, `language` **C**, `archived: false`, default branch `main` | `api.github.com/repos/erincatto/box3d` |
| Licence is MIT | `license.spdx_id` = `MIT`; SPDX headers in-tree | same |
| It is genuinely new | `created_at` **2026-05-10**, announced **2026-06-30** (*"I still consider Box3D to be alpha software"*) | repo metadata; `box2d.org/posts/2026/06/announcing-box3d/` |
| Exactly one release exists | `v0.1.0`, `published_at` **2026-06-30T18:34:47Z**, total count **1** | `api.github.com/repos/erincatto/box3d/releases` |
| It is actively maintained | `pushed_at` **2026-08-19** — two days before this review | repo metadata |
| Rollback is disclaimed, in writing | *"Box3D does not have rollback determinism. There is no mechanism to set a world back to a prior state and then resume simulation expecting identical results."* | `docs/faq.md` |
| Determinism is cross-platform and thread-count-invariant | *"floating-point contraction is disabled (`-ffp-contract=off`) and IEEE 754 arithmetic is relied upon consistently"*; *"A simulation using two threads will give the same result as eight threads."* | `docs/faq.md` |
| Spatial queries are read-only and thread-safe | *"you can call read-only functions from multiple threads. For example, all the spatial query functions are read-only."* | `docs/faq.md` |
| The character mover is stateless | *"A capsule that exists outside the rigid body simulation and is driven entirely by application code."* Documented API: `b3World_CastMover`, `b3World_CollideMover`, `b3Body_CollideMover`, `b3SolvePlanes`, `b3ClipVector` | `docs/character.md` |
| …and experimental | *"The character mover API is experimental."* | `docs/character.md` |
| No world snapshot/restore exists | No `b3World_Save`, `b3World_Restore` or `b3World_Snapshot` in the public header | `include/box3d/box3d.h` |
| Length units are settable, and tolerances scale with them | `B3_API void b3SetLengthUnitsPerMeter( float lengthUnits );` and `B3_API float b3GetLengthUnitsPerMeter( void );`; `#define B3_LINEAR_SLOP ( 0.005f * b3GetLengthUnitsPerMeter() )`; `#define B3_SPECULATIVE_DISTANCE ( 4.0f * B3_LINEAR_SLOP )` | `include/box3d/constants.h` |
| Hull and shape limits are hard numbers | `#define B3_MAX_HULL_VERTICES 128`, `B3_MAX_HULL_FACES 128`, `B3_MAX_HULL_EDGES 128`; `B3_MAX_SHAPES ( 1 << B3_SHAPE_POWER )`; `B3_TIME_TO_SLEEP 0.5f` | `include/box3d/constants.h` |
| `b3Body_SetTransform` is a teleport | *"Set the world transform of a body. This acts as a teleport and is fairly expensive."* | `include/box3d/box3d.h` |
| `b3Body_SetTargetTransform` is velocity-based and approximate | *"Set the velocity to reach the given transform after a given time step. The result will be close but maybe not exact. This is meant for kinematic bodies."* | `include/box3d/box3d.h` |
| `b3World_RebuildStaticTree` is not the public knob for chunk churn | *"This is for internal testing"* | `include/box3d/box3d.h` |
| The snapshot machinery exists but is private to replay | `b3World_StartRecording` seeds a snapshot; `b3RecPlayer` carries a keyframe ring with a byte budget and an in-place restore; *"The main use is for debugging."* | `docs/recording.md` (review pass) |
| Rollback exposure has been asked for, and answered by the author | Issue **#134**, *"Expose world snapshot/restore API for rollback and simulation branching"*, opened 2026-08-18. Exactly one comment, author `erincatto`, **2026-08-19T17:45:46Z**: *"There needs to be some optimizations to make this reasonable. The current snapshot embeds all collision meshes. There should be some sort of context to keep the data size more reasonable."* | `api.github.com/repos/erincatto/box3d/issues/134/comments` |

That last row matters more than its size suggests. It is **not** a refusal — it is the maintainer naming the blocker. And `docs/recording.md` says what the blocker is: a seed snapshot embeds, among other things, *the interned shape geometry*. For Spectra that is the static world's hulls, which by construction do not change inside a rollback window. The "context" the author says is required is exactly the one this engine would want. Treat the feature as **absent, with arrival plausible rather than unknown**.

### 1.2 Why Box3D wins on this engine's specifics

- **The C API *is* the API**, not a veneer over C++. Handle-based (`b3WorldId`/`b3BodyId`/`b3ShapeId`), blittable value structs, no C++ types leaking. `[LibraryImport]` with `DisableRuntimeMarshalling` covers it with no shim assembly, which is the cleanest AOT posture in the field.
- **Zero mandatory reverse P/Invoke on the step path.** `b3WorldDef`'s task callbacks are optional (*"If task callbacks are provided then Box3D will use the user provided task system. Otherwise Box3D will create threads and use an internal scheduler"*), and the friction, restitution and debug-shape callbacks are optional too. With `workerCount = 1` the step path is pure forward P/Invoke. This is the exact failure surface that burned this project once already, and here it is structurally absent.
- **Convex hulls are first-class**, which is what makes brushes a gift rather than a chore: `b3CreateHull(points, count, maxVertexCount)` and `b3CloneAndTransformHull(hull, transform, scale)` — the second being the direct analogue of `Brush.WithScaledExtents`.
- **Boundary-only double precision** (`BOX3D_DOUBLE_PRECISION`: world positions double, everything below the body float) matches the open-world pillar's shape rather than fighting it.
- **A stateless character mover**, which — see §3 — is the single property that makes `IPredictedMover.ApplyInput`'s purity contract satisfiable at all.
- **Small native payload**: ~1.06 MB `box3d.dll` on win-x64 (review-pass measurement of a shipped nupkg payload, 2026-08-13 build), against ~1.92 MB for the Jolt C wrapper.

### 1.3 The honest maturity assessment

**Box3D is one release old and its bus factor is one.** `v0.1.0` is the only tag. The author calls it alpha. The API is already breaking — recent commits bump `b3HullData` and `b3CompoundData` versions. The character mover this design leans on carries a literal experimental banner. Windows ARM64 CI builds with `-DBOX3D_DISABLE_SIMD=TRUE` despite NEON existing and being benchmarked, unexplained, which may mean the arm64 determinism hashes are only ever checked on the scalar path.

Against that: the maintenance discipline is unusual for a 0.1. The CI matrix runs ubuntu-gcc, ubuntu-clang under TSan, ubuntu-clang under MSan, macOS, Windows MSVC, Windows ClangCL and `windows-11-arm`, plus a RelWithDebInfo job that exists specifically so determinism golden hashes are checked at `-O2` and not only at `-O0`. Adoption is real and relevant: s&box (Facepunch), Esoterica, and Glenn Fiedler's 1000-player space game — Fiedler being the most-cited author in game networking, which is where the "network-friendly" reputation actually comes from. And the same author has shipped Box2D for roughly twenty years, with a real tagged 3.x line (v3.0.0 2024-08-12, v3.1.1 2025-06-04) whose API idiom Box3D inherits directly. *Box2D v3's maturity says nothing about Box3D's* — they are different libraries and conflating them produces a false read — except that this author has done this before and finished.

**Do not take either third-party C# binding as a dependency.** `Happypig375/Box3D` is ClangSharp autogen on a *daily* workflow that tracks alpha `main`; `Miguel249/Box3D.NET` is a two-week-old single-author 0.4.0. Both are prior art to read, not packages to install — this project has already been burned by a recommended-then-archived Luau binding and by Riptide, which published clean under NativeAOT and then crashed the native binary on its default API through a reflective assembly scan. Steal one technique from `Box3D.NET`: regenerate `sizeof`/`_Alignof`/`offsetof` for every bound struct from the C compiler in CI and diff them, so an upstream ABI bump is a red build rather than a corrupted stack.

### 1.4 Did the user's preference survive? Yes — but retire the justification

**It survived.** Every factual claim in the proposal checks out, including the self-deprecating one. What does not survive is the *reason*: Box3D's documented network-friendliness is lockstep determinism, and [`docs/networking.md`](networking.md) chose server-authoritative state replication with client prediction precisely so it would never need that. Evaluated on the criterion that actually applies — rewind cost and snapshot size — Box3D scores **worst** of the shortlist on paper and **fine** in practice, because this engine's rewind window contains one stateless mover and no solver state at all.

That distinction is worth writing down rather than smoothing over. If it is left implicit, the first future milestone that wants a predicted crate will cite "Box3D is network-friendly" and get a nasty surprise.

### 1.5 The candidates that were rejected, and why

| Candidate | Verdict | The disqualifying fact |
| --- | --- | --- |
| **Jolt Physics** (`jrouwe/JoltPhysics`) | **Named fallback** | Best rollback story in the field on paper — `SaveState`/`RestoreState` with a `StateRecorderFilter` for capping snapshot size — but the review grepped both C wrappers' full source trees and `SaveState`, `RestoreState` and `StateRecorder` appear **zero times** in `amerkoleci/joltc` (all ~1,270 entry points) and zero times in `SecondHalfGames/JoltC`. The feature that justifies choosing Jolt does not cross the C boundary in any existing binding, and `StateRecorder` is a C++ abstract class, so exposing it means subclassing in C++. Also: two upstreams instead of one, ~2× the native payload, an engine-spawned job system to reconcile with a render-thread-owned scene, and a binding maintainer whose README prices feature requests at a sponsorship. |
| **PhysX 5** | Reject | No official C API, and the one credible .NET route — `Cysharp/MagicPhysX` — is **archived** (last push 2025-05-14). That is this project's exact prior failure mode repeating. |
| **Bullet3** | Reject | Last release **3.25, 2022-04-24**; three commits in the last ten months; the in-tree C headers are the PyBullet *robotics client* protocol, not a games physics C API. |
| **Rapier** | Reject | The only C/FFI binding (`aecsocket/rapier-ffi`) was last pushed **2023-09-15**. Using it means owning a Rust toolchain and a hand-written FFI forever, on top of the C toolchain Luau already requires. Its snapshot story is whole-world serde — the wrong granularity for per-tick rollback. |
| **Box2D v3** | Not a candidate | 2D. Listed only so nobody reads its genuine maturity as evidence about Box3D's. |

### 1.6 What would change the answer

Three concrete triggers, in the order they are likely to fire:

1. **A milestone acquires a predicted rigid body.** That is the one thing this design cannot deliver on Box3D today. The gate is `Y13` (§6): re-check issue #134, and *measure* whether a hand-rolled per-body restore diverges tolerably. A crate resting on a floor and a five-box stack are very different verdicts and only an experiment separates them.
2. **The alpha breaks concretely** — an ABI change the CI diff catches that cannot be absorbed, or a mover regression that makes character feel unfixable.
3. **The native gate (`Y1`) fails.** If a pinned Box3D build cannot `dotnet publish -p:PublishAot=true` and step a world on win-x64 and linux-x64, the dependency choice changes before anything is built on it.

The fallback stays affordable because of sequencing, not optimism: `Y0`–`Y5` use only `b3CreateHull`, `b3CloneAndTransformHull`, `b3CreateHullShape`, `b3World_CastMover`, `b3World_CollideMover`, `b3SolvePlanes`, `b3ClipVector` and `b3World_CastRayClosest`, every one of which has a direct Jolt equivalent.

---

## 2. Integration: brushes, chunks, the BSP, and the tick

### 2.1 Static collision is built from AUTHORED brushes, never from carved surfaces

This is the decision the rest of the design falls out of, and its justification is one sentence already in the codebase. `Bsp/Csg.cs:8-12`: carving *"removes the parts of each brush's faces that are buried inside other brushes, leaving only the visible exterior skin of the solid union."* The carve is **union-skin extraction**. It changes which surfaces are visible; it does not change the solid. So the union of the authored convex brushes and the volume bounded by the compiled surface set are the same solid, and a physics representation built from `BrushPlacement`s loses nothing.

> **AMENDED 2026-08-21 — the sentence above is FALSIFIED by negative brushes, and this is a prerequisite of `Y3`, not an editorial follow-up.** [`docs/negative-brushes.md`](negative-brushes.md) adds `Brush.Operation { Additive, Subtractive }` (milestone `P7b`), under which the compiled solid is `⋃{additive} \ ⋃{subtractive}`. A cut brush's region is then **no longer a union of authored convex hulls**, so *"the union of the authored convex brushes and the volume bounded by the compiled surface set are the same solid"* stops being true and the *"a physics representation built from `BrushPlacement`s loses nothing"* conclusion stops following from it.
>
> **The failure is concrete and severe.** §2.4 states that *"physics never defines its own admission predicate — it consumes the one list."* A subtractive brush **is** in that list and looks like an ordinary bounded convex solid. So a physics build that does not learn about `Operation` gives every negative brush a **solid collision hull**: a hole you can see through, that `CsgWorld.ContainsPoint` reports as empty, and that the player cannot walk through. That is exactly the render/collide divergence §2.3's nodraw refusal exists to prevent, arriving from the other direction.
>
> **The distinction that keeps the amendment small: physics must NOT learn `BrushKind`, but it MUST learn `Operation`.** `BrushKind` is an admission predicate, and physics correctly inherits it by consuming the list — §2.4's sentence stands unchanged for it. `Operation` is not an admission predicate; it is a property of the solid the list *denotes*, and the list's semantics changed under it.
>
> **The route that costs no new machinery: an EXACT convex decomposition from the authored planes alone**, no CSG output and no decomposition library. For convex *P* and convex *N* with outward planes h₁…h_m, `P \ N` is the disjoint union over *k* of `P ∩ {h_k ≥ 0} ∩ {h₁ ≤ 0} ∩ … ∩ {h_(k−1) ≤ 0}` — *m* convex pieces, each a plane set, structurally the same recursion `Csg.CarveFragment` already walks. A doorway costs at most six hulls where it used to cost one; multiple negatives compound multiplicatively over the ones that actually overlap. **The pieces are plane sets handed straight to the point computation that feeds `b3CreateHull`, never `Brush` instances** — the negated h_i planes are routinely same-facing-coincident with a plane of *P* in exactly the flush-cut case, which `Brush.RejectDuplicatePlanes` (`Brush.cs:355`) throws on, and empty pieces are common.
>
> **Whether §2.1 adopts that, a per-cut-chunk trimesh, or a refusal of collision on cut geometry is this document's call and it is not yet made — but `Y3` must not be built until it is.** `negative-brushes.md` §8.0 supplies the decomposition and the constraint and explicitly declines to choose.

Everything good follows:

- **Physics needs no CSG output.** No dependency on `CsgWorld`, on `ChunkMesh`, or on the *result* of the background compile.
- ~~**A brush whose faces are entirely carved away still contributes its solid.**~~ **INVERTED by `P7b`, and it flips from a feature into a bug.** This bullet read: *"Under a compiled-surface design that brush vanishes from collision — an invisible-but-solid pillar becoming walkable. Here the question never arises."* Under subtraction the question arises and the sign reverses: an additive brush entirely inside a subtractive one is **annihilated** — it emits a zero-length surface array and is genuinely not part of the solid — so it **MUST** vanish from collision, and a hull-per-placement design cannot express that. The invisible-but-solid pillar and the annihilated brush are the same record with opposite correct answers, and only `Operation` plus the containment test distinguishes them. Any decomposition adopted above must handle it; a refusal must name it.
- **Hull-vs-hull is cheaper and better behaved than triangle soup**, and it avoids inheriting the weld and T-junction repair tolerances as collision geometry.
- **Rigidity is enforced by the same invariant twice.** Box3D's body transform is position plus rotation only, which is exactly `Scene.DescribeNonRigidDefect`'s brush rule; and `Brush.WithScaledExtents` rebuilds planes rather than scaling a node, which maps onto rebuilding a hull rather than scaling a shape.

One API shape to note: **`b3CreateHull` takes points, not planes.** Brushes feed it through `Brush.LocalFaces` (the eagerly-clipped `Polygon` vertices), never `Brush.LocalPlanes`. `Brush`'s constructor has already clipped the half-spaces and rejected unbounded volumes, so a hull that reaches the binding is provably closed.

The hull cap is a real number — `B3_MAX_HULL_VERTICES 128` — so the binding must **refuse loudly** on a brush whose unique vertex count exceeds it, naming the node, rather than accept a silently simplified hull. A simplified collision hull is a player clipping through a wall that renders correctly, which is the worst class of bug this engine can ship: it looks like a network problem or a CSG problem and is neither. The refusal must be a *pre*-check on the input count; whether `b3HullData` exposes a readable vertex count for a post-check is unverified. The chosen `maxVertexCount` goes in the cook key, because changing it changes collision geometry.

### 2.2 One static body per occupied chunk, and what it actually guarantees

**One static body per occupied `ChunkCoord`, positioned at `ChunkCoord.MinCorner`, carrying one hull shape per *owned* placement in cell-local coordinates.** `ChunkGrid.OwnerCell` (`Bsp/ChunkGrid.cs:131`) is public, static, and takes a bare `BrushPlacement`, so physics buckets placements with no grid and no compiled world; owner cells are exclusive, so coverage is exact with no double representation. `ResidentBrushIndices` must **not** be used — it is deliberately non-exclusive.

The reason is precision, not tidiness. Box3D's large-world design keeps only body positions in double and everything below the body — shapes, manifolds, broadphase, AABBs — in float. A body at the cell origin with hulls expressed locally makes static-world precision **position-independent**, which is the same argument `Brush`'s class doc already makes for brush-local frames (*"a brush 10 km from the world origin has the same FP precision as one at the origin"*), restated in Box3D's own idiom.

Two claims must **not** be written down, because they are false:

- **Hull coordinates are not bounded by ±32 units.** `OwnerCell` assigns a placement to the cell containing the centre of its inflated AABB (`ChunkGrid.cs:131-132`), so a 500-unit floor slab owned by one cell has locals out to ±250. The precision argument survives unchanged — locals are bounded by *the brush's own extent*, which is what makes them position-independent — but the ±32 figure is not a guarantee.
- **Per-brush shapes only buy O(1) edits if the implementation is O(1).** Destroying the whole chunk body and rebuilding every owned placement in it is O(cell), which throws away the reason per-brush shapes were chosen over a baked compound in the first place. Destroy and create only the affected shapes on a surviving body.

At `B3_MAX_SHAPES = 1 << 22` (~4.19 M), one hull shape per placement is comfortable at the 50k-part `openworld` benchmark size.

Hulls are cached per `Brush` **reference identity** — the key `CsgCompileCache` already uses, valid for the reason `Brush.cs:68-79` states: a brush is immutable after construction, so reference identity implies identical geometry. The cache is **refcounted**, because `Csg.Carve` explicitly permits one `Brush` instance to back many placements. Ownership is explicit and render-thread — no finalizers, no `GCHandle`, no `ConditionalWeakTable` — matching the rule `AssetManager` already states ("the manager owns everything it hands out"). A finalizer would call into a native allocator from the GC thread with unspecified ordering against world teardown, which is an access violation rather than an exception.

### 2.3 Collision-only geometry: the hole, and the fix

**Every design that reached this document could turn a solid brush into a non-solid one (`CanCollide = false`) and none could express the inverse: invisible but solid.** That is the clip brush, the playtest blocker, the invisible wall — the single most-used physics primitive in Hammer-style level design and ubiquitous in Roblox. It has to be in the design, not discovered when a level designer asks for it.

The three obvious fixes each fail for a different reason, and the reasons are worth recording:

1. **A nodraw material does not work.** The carve is union-skin extraction, so a clip brush poking out of a wall contributes its own exterior faces to the union skin; making those faces invisible punches a see-through hole into the solid interior.
2. ~~**A per-node "do not carve" flag is refused by [`docs/realms.md`](realms.md).**~~ **AMENDED 2026-08-21 — this objection does not survive inspection, and §2.3a is the flag.** The argument as written was that admission "would become a per-node walk on the CSG snapshot hot path". It would not: the snapshot walk **already** tests `node.Brush is not null` per node — `SnapshotFullWalk` at `Scene.cs:1351` and `TryCollectChangedSlots` at `Scene.cs:1310` both do exactly that — so a declared kind bit adds **one bit test on a cache line that is already being read**, and the walk's shape does not change. What R15 actually forbids is *a third independently-maintained subtree invariant*, and §2.3a does not add one: the existing `_subtreeBrushCount` becomes **two lanes in one field with one writer**, which is strictly harder to desynchronise than what exists today. The original bullet's other half is still true and is why the flag must be **declared and stamped, never derived**: an admission predicate computed from mutable ancestry is how silent corruption happens.
3. **Removing the brush from the placement list removes it from physics**, because physics consumes that exact list by design. This one stands, and it is a *feature* under §2.3a — a part brush leaves the carve and keeps its own hull and its own body.

**The fix is a declared bit on the node: `BrushKind.Part` (§2.3a).** A collision-only volume is a `Part` brush with `CanCollide = true` and nothing drawn. It leaves the carve by declaration rather than by entity ownership, so it needs neither `P4`'s entity runtime nor `P7`. *(This replaces the earlier fix, which routed clip volumes through a `P7` entity-owned brush with a `func_clip`-shaped classname. That route also worked, but it made the most-used primitive in Hammer-style level design wait for the riskiest milestone in the roadmap, and it rested on `P7`'s `BrushModel`-as-`MeshRenderer` render mechanism, which is dead — see §2.3a, "What a part brush is downstream".)*

**A consequence that must be stated rather than discovered, now NARROWED rather than deleted:** [`docs/realms.md`](realms.md) R15 requires any node admitted to the static-world carve to be `Shared`, and physics inheriting the placement list inherits that restriction. **There can be no server-only *solid* geometry on the brush path** — no server-side clip volume, no wall solid on the server and absent on the client. The split does **not** repair this, and the reason is §3.1: the client calls `b3World_Step` zero times and predicts its mover against its own static hulls, so it cannot discover a server-only solid, and `IServerAuthority.Validate` ([`docs/networking.md`](networking.md) §4.4) tests movement against exactly that world — the player rubber-bands into a wall they cannot see. The mirror case is equally broken with the sign flipped: a *client-only* solid blocks a player where the authority believes the space free, so no correction ever fires and the server's model of that player is simply wrong. What the split *does* unlock is narrower and real: **server-only trigger and query volumes become expressible once `CanCollide` exists** (`Y0` for the flag, `Y8` for the touch pass), because a volume no predicted mover consults cannot make the two sides disagree. See `realms.md` R15 for the exact relaxation and its sequencing — it does **not** ship with `P7a`. Related and correct: a `Dormant` brush leaves the placement list and therefore loses collision, which is the right behaviour, and `realms.md`'s `Dormant`/`Active` axis maps cleanly onto `b3Body_Disable`/`b3Body_Enable`.

### 2.3a `BrushKind` — the world/part split

> **Numbering.** This is `2.3a` and not `2.4` because five documents cite `§2.4`–`§2.9` of this file by number. It is a first-class section; the letter is a cross-reference courtesy, not a status.

#### The correction that reframes the whole problem: the carve is UNION, not subtraction

The question this section answers arrived as *"should physics brushes be excluded from CSG, so a rolling brush stops destroying the world topology beneath it?"* — and the premise is wrong in a way that changes the answer.

**CSG here is union-skin extraction.** `Bsp/Csg.cs:8-12`, in the file's own words: carving *"removes the parts of each brush's faces that are buried inside other brushes, leaving only the visible exterior skin of the solid union."* Two overlapping world brushes **merge**; a crate resting on the floor does **not** punch a hole in it. Nothing about a part *sitting* in the world damages topology, and §2.1 already leans on the same fact for physics.

**The destruction begins only when a brush MOVES under simulation — and it is worse than a slow recompile.** Each tick changes the **overlap set**, not merely a placement: new carve pairs appear and old ones vanish. That is precisely the structural change the incremental compiler cannot carry, so a simulated brush bails to the fully-validated **O(world)** path *while everything still renders correctly*. That is the open-world pillar dying silently, and §4.2 names the same hazard arriving from the `Anchored` direction.

> **CORRECTED 2026-08-21 — the two gates this paragraph cited are the wrong gates, and "every tick, forever" is too strong.** It read: *"…the incremental compiler cannot carry — `CsgIncrementalCompiler.TryBuild` trusts the carry only when the placement count and the traversal order are stable (`CsgIncrementalCompiler.cs:99`, `Scene.cs:996`) — so a simulated brush bails … **every tick, forever**."* `CsgIncrementalCompiler.cs:99-100` is the placement-**COUNT** gate and the `Scene.cs` one is the graph-**STRUCTURE**-version gate; **neither fires for a brush that merely moves**, because moving changes neither the count nor the traversal order. The gates that actually fire are `if (newNeighborSet.Count != surviving) return false` (`:198-199` — a new overlap pair) and `RankRelationStable` at `:206-209` / `:226-230` (a `min.X` rank crossing, or an exact tie, which `:566-576` also refuses). A brush whose overlap set and local rank order both hold **patches indefinitely at neighbourhood cost**.
>
> **The conclusion is unchanged and the split still stands** — a brush tumbling through a dense field gains a leading-edge partner and loses a trailing one every few ticks, and grid-aligned worlds manufacture exact ties, so both gates fire repeatedly in the general case. But the *precise* condition is the whole basis of [`negative-brushes.md`](negative-brushes.md) §8.2 and §8.3, which build a moving-hole story on top of it, so the docs must be accurate about it before anyone relies on it. **The same wrong pair of citations is in the `BrushKind.cs` XML comment** (*"every tick, forever"*) and is owed a fix at `P7b`.

**So the split's job is exact: make "participates in the fused world" a property that a SIMULATION can never change, and a HUMAN changes only by asking.**

#### The bit

```csharp
public enum BrushKind : byte { World, Part }   // no Inherit value, ever
```

- **A plain field on `SceneNode`, not a bit in a packed flags word.** `NodeRealm`, `NodeState` and `PhysicsFlags` are all designed and unbuilt (`realms.md` and this document are design-only). `P7a` must not be gated on a packing scheme owned by two unlanded designs. Ship the field; let whichever of the three lands first define the word and absorb it.
- **Default `World`.** A brush that says nothing is world geometry, which is what every existing brush in the tree and in every future `.smap` means. It is also the safe default for the reason in *"Two contact defects"* below: a part face coplanar and same-facing with a world face z-fights permanently, and defaulting to `Part` would make that the beginner's first experience.
- **NOT inherited, and there is no `Inherit` value.** This is the design's strongest move and it stands unchanged. A non-inherited bit means `AddChild` is **not** a refusal site for kind and **no reparent can rewrite world topology** — dragging a brush into a folder can never silently add it to or remove it from the carve. Contrast `NodeRealm`/`NodeState`, which are inherited *and* therefore need `realms.md` R7, R15 and R17 to keep admission honest.
- **The initial value is chosen by the creation ROUTE and then STAMPED onto the node**, never re-derived from context — the same discipline `realms.md` §7.5 item 7 already establishes. **But no route table ships with `P7a`**: the only creation route that exists today is `node.Brush = …` in C#, and a setter has no notion of its caller. Route defaults (block tool → `World`; `Instance.new("Part")` during Play → `Part`; duplicate inherits its source's kind) are properties of **tools**, and they get written when those tools exist.
- **`SceneNode.IsStaticWorldBrush => _brush is not null && _brushKind == BrushKind.World`** is the one predicate the snapshot path asks.

#### One field, two lanes, one writer — and the total lane is NOT deleted

The instruction that circulated as *"rename `_subtreeBrushCount` to `_subtreeStaticWorldBrushCount`; a second counter is forbidden"* is wrong on inspection, and shipping it would delete a refusal.

`ScaleGizmo` routes on `SceneNode.SubtreeBrushCount` — `ScaleGizmo.cs:330` (`node.Brush is null && node.SubtreeBrushCount > 0`), `:654`, documented at `:76` — to refuse a node scale on a **group** node that has brush *descendants*, and `GizmoBrushRigidityTests.Resizing_a_group_node_with_brush_children_is_refused_outright` pins it (`GizmoBrushRigidityTests.cs:113` asserts the count itself). Under a world-only counter, a group holding only part brushes reports `0`, the refusal silently disappears, and a node scale lands above a part brush — which the rigidity rule below forbids outright.

**So:**

```csharp
private long _subtreeBrushCounts;   // high 32 bits: TOTAL   low 32 bits: STATIC-WORLD
private static void AdjustSubtreeBrushCounts(SceneNode node, int totalDelta, int worldDelta);  // the ONLY writer
public int SubtreeBrushCount            => (int)(_subtreeBrushCounts >> 32);   // meaning unchanged, callers unchanged
public int SubtreeStaticWorldBrushCount => (int)(_subtreeBrushCounts & 0xFFFFFFFF);
```

One field, one private ancestor-chain writer (today's `AdjustSubtreeBrushCount`, `SceneNode.cs:361`), two read-only projections. **Packing them is what makes "two independently-maintained subtree invariants" structurally impossible** while still answering both questions — which is how `realms.md` R15's *"do not add a third subtree invariant"* is honoured rather than argued with. Pin it with a **randomized graph test**: N random attach / detach / reparent / kind-flip operations, then recount both lanes recursively and assert equality.

#### The complete gate list — fifteen sites, each with a verdict

Enumerating this is the section's load-bearing content. The first pass at this design gated **one of the eight sites in `SceneNode` alone**, and the ungated `Brush` setter is where every zero-cost claim actually dies. Line numbers are reads of the tree at 2026-08-21.

**`Scene/SceneNode.cs`**

| # | Site | Verdict |
| --- | --- | --- |
| 1 | `Brush` setter, counter adjust (`:131`) | **Test the kind.** Total lane always ±1; world lane ±1 **only when** `_brushKind == World`. |
| 2 | `Brush` setter, attach/detach `Owner?.MarkStaticWorldDirty()` (`:140`) | **Test the kind.** A part-brush attach or detach must signal **nothing**. **This is the line that falsifies every "zero cost" claim made without it** — `networking.md` §4.5 and `roblox-onboarding.md` `O7` both already name it as the severe trap, because `MarkStaticWorldDirty` sets `_snapshotForceFull` and forces the O(world) full walk. |
| 3 | `Brush` setter, swap `Owner?.MarkBrushSubtreeDirty(this)` (`:142`) | **Test the kind.** `SetBrushCommand` on a part brush — which is the resize path — signals nothing. |
| 4 | `Brush` setter, `Owner?.OnNodeSpatialComponentChanged(this)` (`:144`) | **Must NOT test the kind.** `SceneBvh.IsSpatial` (`SceneBvh.cs:146`) indexes brush nodes and `ComputeWorldBounds` (`:278`) unions the brush's `LocalBounds`, so gating this would drop part brushes out of **frustum culling** and out of **editor picking** (`RaycastBrush`, `SceneBvh.cs:634`). It maintains no counter — it is one call to `Bvh.OnSpatialComponentChanged`. |
| 5 | `OnLocalTransformChanged`: `if (_subtreeBrushCount > 0) Owner?.MarkBrushSubtreeDirty(this)` (`:379-380`) | **Test the WORLD lane.** This is the only site the first design pass found. |
| 6 | `OnLocalTransformChanged`: `Owner?.OnNodeTransformChanged(this)` (`:384`) | **Kind-blind**, as already documented there — it fires for every owned node, brush or not. |
| 7 | `AddChild`'s two `MarkStaticWorldDirty()` calls (`:256-259` old chain, `:271-274` new chain) | **Test the WORLD lane** of the moved child; the counter adjustments move **both** lanes. |
| 8 | `RemoveChild`'s `MarkStaticWorldDirty()` (`:298-302`) | Same as 7. |
| 9 | **NEW** `BrushKind` setter | The one admission write — see below. |

**`Scene/Scene.cs`**

| # | Site | Verdict |
| --- | --- | --- |
| 10 | `SnapshotFullWalk`: `if (node.Brush is { } brush)` (`:1351`) | → `if (node.IsStaticWorldBrush)`. Rigidity validation and `DescribeBrushNodeDefect` therefore stop seeing part brushes; the rigidity rule below keeps them rigid by a different route. |
| 11 | `TryCollectChangedSlots`: `if (node.Brush is not { } brush)` (`:1310`) | → `if (!node.IsStaticWorldBrush)`, keeping the existing `_snapshotSlots.ContainsKey(node)` safety net inside the negative branch. **Missing this one is its own silent pillar-killer**, and neither the design pass nor the review pass enumerated it: a dirty *world*-brush subtree containing a *part*-brush descendant would fall into `if (!_snapshotSlots.TryGetValue(node, out slot)) return FullWalkRequired` and take the O(world) walk **on every drag frame**. |
| 12 | `OnNodeAdded` / `OnNodeRemoved` / `OnNodeSubtreeMoved` structure bumps (`:128`, `:137`, `:159`) | **Ship `P7a` with these UNCHANGED and kind-blind.** Over-bumping is safe; under-bumping is corruption. The narrowing is a separate step — see below. |
| 13 | `OnNodeSpatialComponentChanged` (`:155`) | **Kind-blind**, per gate 4. |
| 14 | public `MarkStaticWorldDirty()` (`:724`) and internal `MarkBrushSubtreeDirty` (`:734`) | **Bodies unchanged, kind-blind.** They are *told*; they do not *decide*. Their callers are the gate. |
| 15 | **NEW** `Scene.MarkAdmissionChanged()` | Bumps `_graphStructureVersion` **and** does what `MarkStaticWorldDirty` does (`_staticWorldVersion++`, `_snapshotForceFull = true`). Required by `realms.md` R17, whose hole is verified live at `CsgIncrementalCompiler.cs:99` with `VerifyTrustedDiff` `[Conditional("DEBUG")]` behind it. |

#### The invariant, in one checkable sentence

> After **any** sequence of writes to nodes whose `BrushKind` is `Part` — attach, swap or detach `Brush`, write any transform, reparent, add to or remove from the scene — `Scene.StaticWorldDirty` must still be `false` and `Scene.StaticWorldCompileCount` must be unchanged. The **only** write on a part node permitted to disturb either is the `BrushKind` setter itself.

Pin it with **three** tests, not one:

- **(a)** 200 ticks of a moving part brush; compile count constant.
- **(b)** 200 attach / detach / swap operations on part-brush nodes; compile count constant **and** `StaticWorldDirty` false throughout. **This is the one that fails against unmodified code** — it is gate 2.
- **(c)** Assign-order safety: `new SceneNode(…)`, then `BrushKind = Part`, then `Brush = …` dirties nothing; and the **reverse** order (`Brush` first) is either refused or equally clean. **Pick refusal-free** — make `BrushKind` constructor-settable *and* have the `Brush` setter read the *current* `_brushKind`, so the natural order (`Brush` first, kind second) costs exactly one dirty plus one admission bump and never corrupts. Do not leave the order as an unpinned convention.

#### The `BrushKind` setter is the one admission write, and it is conditional and idempotent

Early-out on an equal write — the exact-equality discipline every transform setter already uses (`SceneNode.cs:170-207`). On a **real** change:

1. Move ±1 between the two counter lanes along the whole ancestor chain — **if and only if this node carries a brush**.
2. Call `Scene.MarkAdmissionChanged()` — **if and only if this node carries a brush**.
3. Release or acquire this node's entry in the part-mesh cache.

A kind flip on a **brushless** node changes nothing anywhere and must signal nothing: it is a stamp for a brush that may arrive later.

#### The structure-version bump may be narrowed — but as its own step, with its own Release-configuration oracle

With gates 1–15 in place, spawning parts during Play costs **zero compiles**, because nothing marks the world dirty. But `OnNodeAdded`/`OnNodeRemoved` still bump `_graphStructureVersion` for a part node, and `SnapshotBrushPlacements` takes its fast path only when `_snapshotStructureVersion == _graphStructureVersion` (`Scene.cs:1267-1268`) — so **a script spawning parts WHILE the user drags a world brush forces one O(world) full walk per drag frame.** Say that out loud until it is fixed.

The narrowing is exact and O(1): those three sites may skip the bump when the node's subtree **world** lane is zero, because the version's documented meaning (`Scene.cs:120-125`) is *"the brush snapshot's TRAVERSAL ORDER may have changed"*, and a subtree contributing no placement cannot perturb the placement list's order — and if it later gains a world brush, **gate 2 forces a full walk at that instant**.

**This is the only gate in the set whose safety rests on an argument rather than on conservatism, so it does not ride in on the same commit as the rest.** It lands second, behind a test shaped like `realms.md`'s pin 5 and run in the **Release** configuration: interleave non-admitted add/remove with real brush edits in one frame, and assert the incrementally compiled world is element-identical to a from-scratch compile.

#### What a part brush is downstream

**It renders its own faces.** A part brush is not in `CsgWorld`, so nothing else draws it.

- **One engine-owned mesh cache**, keyed by `Brush` **reference identity** — the same key `CsgCompileCache` and the `Y2` hull cache already use, valid because a `Brush` is immutable after construction — **refcounted**, because one `Brush` instance may back many nodes, and released on the render thread like every other GPU resource.
- **One new arm in `Scene.BuildRenderView`'s existing loop** (`Scene.cs:280-285`): `else if (node.Brush is { } b && node.BrushKind == BrushKind.Part)`, emitting one `RenderItem` per cached submesh with `node.WorldMatrix`. **No third `RenderView` list, no backend change, no new geometry code — but a new path, not zero new paths.** State the claim narrowly.
- **This overturns `P7`'s planned mechanism**, and the reason is eleven lines of code rather than a preference: `MeshRenderer` holds exactly **one** `Mesh` and **one** `Material` (`Scene/MeshRenderer.cs:12-20`) and a node holds exactly one `MeshRenderer`, so after `F1`'s per-face materials it **cannot express a multi-material brush at all**. The only existing idiom for multi-material geometry is `ModelInstantiator`'s node-per-submesh (`ModelInstantiator.cs:126`), which would inject **derived nodes into the authored graph**, where they are selectable, reparentable and serialized — breaking the same *"derived data is never authored"* rule the static world obeys. `ROADMAP.md` `P7` and `networking.md` §4.5 are amended in the same commit.

**The part path SNAPS.** `VertexSnapper.Snap(brush.LocalFaces)` runs before `ChunkMeshBuilder.BuildSubmeshes`, in the brush's **local** frame. This is not cosmetic: `LocalFaces` is exactly the raw seed-quad clipping output the snapper exists for (`VertexSnapper.cs:10-16` — *"each brush face computes its corners by clipping its own seed quad, so the same logical corner emerges from a different sequence of `Vector3.Lerp` calls per face"*), so **without it a part brush cracks along its own edges**, with no world contact involved. Cost: one `Polygon[]` per `Brush` instance at cache fill (~6 polygons, ~24 vertices), refcounted, never per frame. UVs are computed **after** snapping in both paths (`BuildMeshArrays` reads the vertex as given, `CsgWorld.cs:826+`), so they follow automatically.

**The determinism pin — and the claim it replaces.** *"Triangles bit-identical to the world path by construction, because both go through `CsgWorld.BuildMeshArrays`"* is **retracted**: the two paths share only that emitter, and the world path also snaps *and welds*. The honest statement is **one shared emitter, different upstream stages**, pinned twice:

- **Identity placement, bit-for-bit.** For a single brush `B` on a node whose world matrix is exactly identity with `BrushKind = Part`, the part cache's `ChunkSubmesh[]` equals `CsgWorld.Build([new BrushPlacement(B, Matrix4x4.Identity)]).ChunkMeshes[0].Submeshes` element for element and byte for byte. **No lattice precondition is needed** (both paths now snap the same inputs) and **no one-cell size precondition is needed either**: `ChunkGrid.OwnerCell` (`ChunkGrid.cs:131`) assigns a placement's **entire** welded surface set to the single cell containing its inflated-AABB centre — §2.2 says the same thing about a 500-unit slab — so a brush is never *partitioned* across cells, only its *residency* is. `TJunctionWelder.Weld` is a pass-through for a lone convex brush (*"polygons with no T-junctions are reused as-is"*, `TJunctionWelder.cs:56`), and a convex solid's faces share edge endpoints exactly.
- **Rotated placement, explicitly a tolerance.** For a rigid non-identity placement, the part submeshes transformed by the node matrix agree with the world path's within **2 × `VertexSnapper.GridSize`** — *not* bit-for-bit, because the two populations snap in **different frames**.

This pin **supersedes `ROADMAP.md` `P7`'s existing acceptance line** (*"brush-model triangles identical to `CsgWorld.Build(sameplacements).BuildMesh()` at identity"*) — same intent, now stated against `ChunkMeshes`/`Submeshes`, which is what the code actually emits per cell.

**Two contact defects, named as costs of the feature rather than as things that cannot happen.**

- **(a) Part-versus-world seams get no T-junction repair and no shared lattice.** Parts snap in local space, world surfaces in world space, and `TJunctionWelder` never sees the two populations together — so a part flush-mounted into a wall may show **hairline cracks** along the contact.
- **(b) A part face coplanar AND same-facing with a world face z-fights permanently.** `Csg.CarveFragment`/`CoplanarOrientation` (`Csg.cs:376-414`) arbitrate coincidence by carver precedence **within the compiled population only**, and a part is not in it. The first design pass used this correctly to pick `World` as the default and then never listed it as a cost.

**Both are why the editor's part cue is non-negotiable rather than nice.** Two `P7a` acceptance criteria, both deliverable through machinery that already works on all three backends:

- **Part-brush outlines drawn ALWAYS** — never only on selection. Commit `d4701d6`'s lesson is explicit and this repo has already paid it once: an unmarked always-on discrepancy gets reported as an engine bug. Draw them through the existing depth-off `DebugDraw` line pass, the same one §5.2 uses for trigger volumes and for the same reason.
- **`WorldBrushes: N  PartBrushes: M`** in the periodic stats line.

Every richer affordance — the Explorer gutter badge, the highlight lens, the disabled segmented control, the convert dialog — needs a UI shell that does not exist, and belongs in `realms.md` §7.5 with that dependency stated.

**Its own hull, its own body, and a collision-time asymmetry that must be written down.** A part brush's hull comes from `Brush.LocalFaces` exactly as §2.1 specifies, refcounted in the same `Brush → hull` cache (`Y2`); it becomes a body of its own — static while `Anchored`, kinematic while scripted, dynamic under `Y6` — never a shape deposited on a per-chunk static body. **Do not "optimise" it onto the chunk body: that re-couples exactly what the split decouples.** The asymmetry: world-brush collision is built **at harvest**, from the same snapshot that produced the meshes being swapped in (§2.4), so world rendering and world collision match and lag together; a part brush's mesh and body are both **live** and lag nothing. **The invariant is therefore "rendered and collided match WITHIN a kind", not across the boundary** — a part resting on a world brush the user is dragging sees a floor one compile stale.

#### Refusal sites, and the convert flow

**Hard refusal, never silent exclusion.** The precedent is `realms.md` R15's deciding argument, and it applies unchanged: a map that behaves differently depending on a property someone can flip by accident produces a brush that visibly does nothing, which is indistinguishable from a CSG bug. Three sites refuse, each naming the conversion as the route forward:

1. **Creating a dynamic body on a `World` brush node** (`Y6`) — refused, naming `BrushKind = Part`. This is §4.2's `Anchored = false` gesture, and it is the refusal the whole split exists to make expressible.
2. **`BrushKind = World` on a node that carries a live body** — refused; destroy the body first.
3. **A non-`Shared` node carrying a collision-bearing brush of either kind** — `realms.md` R15, migrated to the **total** lane at `P7a` and relaxed only when `CanCollide` exists.

**Brush node transforms stay RIGID for both kinds — the invariant does not fork, and node scale is illegal on a part brush.** Three verified reasons, not audit anxiety:

- `LocalScale` accepts **negative** components (`SceneNode.cs:195-207`, no guard), and a mirrored part inverts winding on a mesh built from outward-facing `LocalFaces` — which is precisely why `Brush.WithScaledExtents`'s `ThrowIfUnusableScale` (`Brush.cs:339-348`) refuses anything `<= 0` in as many words (*"a negative factor turns it inside out"*).
- A non-uniform node scale would demand an **inverse-transpose for normals baked into the cached part mesh**, which no draw path in this engine has ever needed.
- The two resize routes would **mean different things**: `WithScaledExtents` rebuilds planes and hands the existing `FaceSurface[]` through untouched (`Brush.cs:336`), so world-units-per-repeat are preserved and the texture is not stretched — while a node scale stretches baked UVs.

The user does nothing different, because the editor already works this way: `ScaleGizmo` measures the brush, computes a factor, calls `Brush.WithScaledExtents` and swaps the successor on through `SetBrushCommand` — identical handles, identical drag, identical numbers, different storage. It is also exactly Roblox `Part.Size` semantics, so the onboarding story is unharmed. **Mirroring is not expressible and must not be smuggled in through a negative factor**; if it is ever wanted it is a separate `Brush.Mirrored(axis)` (negate one component of every plane normal and its offset, reverse each face's vertex order) behind its own command. Consequence: **`BrushKind = World` needs no rigidity re-check at conversion, because the invariant never lapsed.**

**`ConvertBrushKindCommand`, and the round trip is LOSSY — the command says so.** The UV bake is **mandatory inside the command, not optional**: world-path vertices reach `BuildMeshArrays` in **world** space and part-path vertices in **brush-local** space, and the UV is `dot(p, axis)/scale + offset` over the vertex as given (`CsgWorld.cs:826+`), so an unbaked conversion visibly **jumps the texture** by an amount set by the node's world matrix. Bake via `FaceSurface.ResolveAxes(worldNormal, …)` → `FaceSurface.Transformed(inverse(node.WorldMatrix))` → `Brush.WithFaceSurface`.

What is lost is **not coordinates but a semantic**: zero axes mean *world-aligned*, a distinct documented default in which the projection re-derives from the rotated normal (`FaceSurface.cs:53-66`, and `Transformed` returns `this` unchanged for a world-aligned face at `:208`). After the bake the brush carries explicit axes forever, and Part→World does not restore it. **This is not repairable in general and must not be papered over:** a moving part's UVs live in a cached vertex buffer and cannot re-derive per frame, so world alignment is simply **not expressible on a part**. Therefore:

- **Undo is exact and lossless.** `SetBrushCommand` stores absolute before/after `Brush` references, so an undone conversion restores the original surfaces **bit-for-bit**.
- The loss bites only on convert-and-convert-**back** as two *forward* edits, and the command's undo label states it.
- The one-click repair is an existing-shaped write of zero axes: *"Reset face alignment to World"*.

**Batching a bulk convert is an ERGONOMICS rule, not a correctness one.** The compile pump launches at most one compile per frame, and only when `_staticWorldVersion != _handledStaticWorldVersion` (`Scene.cs:931-942`); both admission signals are a counter bump plus a force-full flag. So **N conversions in one frame already produce ONE full-walk compile.** The real hazard of a per-node loop is N undo entries and N status lines. Batch into one `CompositeCommand` for that reason, and say so.

#### The query demotion moves forward to `P7a`

This was carried as an open question — *"can a part brush appear in `CsgWorld.ContainsPoint`/`Raycast`?"* — and it is not one. `CsgWorld` is a **pure function of the placement list**; a part brush is not in it; `ContainsPoint` (`CsgWorld.cs:603`) routes through `Chunks`, which is built from that list. There is no ordering choice to make.

So on the day part brushes ship, both queries take their §2.5 XML-doc demotion — *"the compiled authored static world only — **no part brushes**, no dynamic bodies, no character"* — and **the complementary sentence goes on `Scene.Raycast`/`SceneBvh` in the other direction: those are kind-BLIND and see both populations**, which is exactly what keeps editor picking correct (gate 4). That asymmetry is the thing a gameplay author must read.

The cost of doing it now is nil — the only callers in the engine tree are `SceneManager`'s three startup sanity assertions (`Scene/SceneManager.cs:400-402`), plus the `CsgBench` harness. The cost of skipping it is that the first gameplay caller written between `P7a` and `Y4` is silently wrong about every part in the map.

#### What `P7a` actually costs, and what it actually delivers

**`P7a` is:** the `BrushKind` field, `IsStaticWorldBrush`, the two-lane counter, all fifteen gates, the snapped part-mesh cache, the one `BuildRenderView` arm, `ConvertBrushKindCommand`, the always-drawn outline, and the stats line. **It needs no entity system, no physics, no prefabs** — that claim survives intact, and it is why the split is not gated on `P4`/`P7`.

**What it delivers alone is exactly one thing: a brush that renders its own faces, never carves, and moves at zero recompile cost.** Everything else needs a milestone that does not exist yet, and the pitch must say so:

| Wanted | Actually needs |
| --- | --- |
| Invisible clip volume | `Y0` (`CanCollide`) + `Y3` (static hulls) |
| Trigger volume | `Y0` (`CanTouch`) or `Y8` (the touch pass) |
| A part that falls | `Y6` (dynamic bodies) |
| A **server-only** volume | the `realms.md` R15 relaxation, which ships with `CanCollide` — **not** with `P7a` |

**Ruling `R‑9` extends to `P7a`** (`ROADMAP.md` §3): it touches the same `SceneNode` counter and the same brush-snapshot surface as `P7`, so it must not land concurrently with `E4`/`E6`. And `ROADMAP.md` §12's standing gate applies unchanged — **the `CsgBench openworld` verdict line must still read *world-size independent***.

#### Negative brushes are an ORTHOGONAL bit, and this section owes them one word

[`docs/negative-brushes.md`](negative-brushes.md) (milestone `P7b`) adds `Brush.Operation { Additive, Subtractive }` — on the **immutable `Brush` value**, not on the node — and the two bits do not interact except in one place.

**`BrushKind` answers *is this brush in the placement list*. `Operation` answers *what does it do to the list it is in*.** All four combinations are legal and all four are meaningful: `(World, Additive)` is every brush authored so far; `(World, Subtractive)` is the carving hole; `(Part, Additive)` is today's part brush; and **`(Part, Subtractive)` is legal and inert on purpose** — it is the flying projectile of `negative-brushes.md` §8.1's bake-on-contact tier, and refusing it would make a `SetBrushKindCommand` round trip lossy. A third `BrushKind` value would have been the tempting shape and is refused there for this section's own reason: it makes `IsStaticWorldBrush` three-valued and re-admits simulated brushes to the placement list.

**The one word owed:** `Scene.UpdatePartBrushMembership` (`Scene.cs:173-179`) admits any `node.Brush is not null && node.BrushKind == BrushKind.Part` into `_partBrushNodes`, which for a subtractive part would make the part-mesh cache build and upload **the outward skin of a hole** — a solid block where the author asked for a void. It becomes `node.Brush is { Operation: BrushOperation.Additive } && node.BrushKind == BrushKind.Part`. That gate is for the **mesh pump only**: the always-on outline for a subtractive brush must come from its own **kind-blind** set, because `PartBrushOverlay` walks `Scene.PartBrushNodes` and gating membership would silence exactly the outline an invisible brush needs. See `negative-brushes.md` §2.3 and §9.

**One claim in this section is contradicted by the tree and is corrected there rather than here:** the stats line promised as *"`WorldBrushes: N  PartBrushes: M`"* does not exist in that shape — `SceneManager`'s periodic line reads `"{PartsVisible} of {PartsTotal} part brush(es)"` (`Scene/SceneManager.cs:585-607`) and there is no world-brush counter anywhere in the tree. Likewise `ConvertBrushKindCommand` shipped as **`SetBrushKindCommand`** (`SpectraEngine.Editing/Commands/SetBrushKindCommand.cs`); this section and three others still use the old name.

#### What this section does NOT decide

- ~~**The enum spelling.**~~ **DECIDED by the project owner, 2026-08-21: `BrushKind { World, Part }`.** See §7 item 10 for the reasoning and the residual wart. This bullet previously offered `World | Object` as the collision-avoiding alternative and sent the question to §7; it is closed and the alternative is not revived.
- **What `Instance.new("Part")` produces in EDIT mode.** `O7` keys the rule on mode; this section supplies only the mechanism.
- **Whether the conditional structure-version bump ships at all**, or the mixed spawn-during-drag full walk is simply accepted.
- **Whether the editor auto-suggests conversion** when a world brush is dropped onto a moving entity, or stays silent until a body is created. The refusal *at body creation* is settled; the author-time nag is an ergonomics call.
- **Whether anchored, never-moved part brushes should later be batchable** into the per-chunk static body, and what invalidates the batch when one moves. Deferring is safe; deciding now would change the `Y3` body-management shape.
- **Whether `Brush.Mirrored(axis)` is wanted at all**, and if so whether it belongs to the resize tool or to a separate transform command.
- **Whether the always-drawn part outline should be suppressed in Play mode**, and if so how a designer then tells a part from world geometry at runtime. The `d4701d6` lesson argues against suppression; nothing has decided it.
- **Whether the part path needs its own LOD/impostor story** at `O7` spawn scale (tens of thousands of instances), or whether *"the same story as `MeshRenderer` nodes"* is sufficient.
- **Whether `CanQuery = false` should be legal on a part brush but refused on a world brush**, as `Y0` currently proposes for the per-cell BSP reason. `Y0`'s wording predates this split.

### 2.4 Physics updates at HARVEST, in the same slot as the mesh swap

Collision syncs inside `Scene.ProcessStaticWorldCompilation`'s success branch — the render-thread harvest where `ReplaceStaticWorld` swaps GPU meshes — not at compile launch.

The competing proposal was to sync at launch, on the argument that collision-behind-render is the *"player falling through the floor of a room they can see"* failure that [`docs/formats-and-pipeline.md`](formats-and-pipeline.md) §4.5 names as the worst kind. That argument does not survive: at harvest, collision is built from the same snapshot that produced the meshes being swapped in, so collision and rendered geometry are **exactly matched**. Both lag the live scene equally, and the player collides with precisely what they see. The failure being feared is a comparison between collision and the *live scene*, which nobody experiences. Harvest also wins on the fault path — when a compile faults and the previous world is kept, harvest-sync leaves collision consistent with what is rendered, while launch-sync would advance collision past it.

Physics inherits the rigidity gate for free: when `SnapshotBrushPlacements` returns null on a non-rigid brush transform there are no placements, so physics skips and the existing loud one-time log covers both subsystems. And physics **never** re-walks the scene graph and never defines its own admission predicate — it consumes the one list, which is what makes it inherit R15, `Dormant` exclusion and `P7a`'s `IsStaticWorldBrush` predicate (§2.3a) automatically. **Physics therefore never learns what a `BrushKind` is**, and that is the point: the world/part split is one predicate at one call site in `Scene`, and every consumer of the placement list gets it for free. **The exact converse holds for `Brush.Operation`, and the asymmetry is the whole of §2.1's amendment:** kind is *admission*, so consuming the list inherits it; operation is a property of the solid the list **denotes**, so consuming the list does **not** inherit it and physics must read the bit itself, or every hole gets a solid hull.

**The asymmetry harvest-time sync creates, stated rather than discovered (§2.3a).** Harvest matching is a guarantee *within* the world-brush population only. A **part** brush's mesh and body are both live and lag nothing, because neither is derived from the compile. So the invariant is **"rendered and collided match WITHIN a kind"**, never across the boundary: a part resting on a world brush the user is dragging sees a floor **one compile stale**, and will visibly sink into or hover above it for those frames. Do **not** repair this by depositing part hulls onto the per-chunk static body — that re-couples exactly what the split decouples, and buys a frame of visual agreement at the price of the pillar.

> **Coupling to record now:** when a brush leaves the static placement list — `BrushKind = Part` (§2.3a) or `P7` entity ownership — it vanishes from physics in the same instant. **The admission change and the body path are one change, not two** — otherwise a door stops being solid the moment it becomes a door, and a converted part falls through the world.

### 2.5 What happens to the BSP and the BVH

After this lands there are **four** spatial structures, not two: `BrushBroadphase` (sort-and-sweep over brush AABBs feeding the carve), `CsgWorld`'s per-cell BSP (`ContainsPoint` at `CsgWorld.cs:603`, `Raycast` at `:617`), `SceneBvh`, and Box3D's broadphase.

- **`SceneBvh` is never redundant.** It indexes *nodes* — including nodes with no collision at all — and serves frustum culling and editor picking, neither of which physics can answer. **It is also deliberately `BrushKind`-BLIND** (§2.3a gate 4): it indexes world brushes and part brushes alike, which is what keeps editor picking and frustum culling correct for both. It does, however, need new work regardless of backend: today it exposes only `Raycast` (`SceneBvh.cs:496`) and `QueryFrustum` (`:585`). There is **no box or sphere overlap query**, so `GetPartBoundsInBox`/`InRadius` is new work — and `P8`'s trigger queries now depend on it, since §2.3a killed the brush-model BSP they were going to use.
- **`CsgWorld.Raycast`/`ContainsPoint` are demoted — and the demotion moves forward to `P7a`, ahead of any physics engine.** Once physics holds the static world, the two answer the same question about the same solid through different code at different tolerances. **Ruling: physics is the sole authority for every query whose answer feeds simulation or gameplay.** `CsgWorld`'s queries are demoted *in their own XML docs* to "the compiled authored static world only — **no part brushes**, no dynamic bodies, no model colliders, no character", and kept for the editor, for cook-time verification, and as the reference side of an oracle. The demotion argument does not depend on Box3D and does not even depend on `Y3`: **a part brush is not in the placement list, so `CsgWorld` cannot see one** — which means the doc change is owed on the day part brushes ship (§2.3a, "The query demotion moves forward to `P7a`"). By `Y4` the sentence merely gains dynamic bodies and the character.
- **Pin it with a divergence oracle, and run it in Release.** N seeded rays over a fixture map; `CsgWorld.Raycast` and `b3World_CastRayClosest` under a static-only filter must agree on hit/miss and on distance within a stated tolerance. **Release, not Debug** — `CsgIncrementalCompiler.VerifyTrustedDiff` is already `[Conditional("DEBUG")]` by design, and `realms.md` R17 names the consequence of repeating that pattern: *a dev build throws and a shipping build silently compiles corrupt geometry*.

### 2.6 Dynamic bodies on the scene graph

**No physics-body `SceneNode` payload — this arc adds a bit, not a reference field.** The exact count, since documents have miscounted it before: `SceneNode` carries **two** payloads today, `Brush` and `MeshRenderer` (`data-model.md` §3, the counting authority), and four more are designed and unbuilt — `Entity` (`P4`–`P9`), `Script` (`O8`), `PrefabInstance` (`P10`) and `Light`. `O8` already wrote down that the payload count "is a real cost on the hottest type in the engine and should be a conscious acceptance, not a drift", and adding a reference field for something 99% of nodes never carry is that drift whatever ordinal it would land on. Instead:

- A packed **`PhysicsFlags` byte** on `SceneNode` (`CanCollide`, `CanQuery`, `CanTouch`, `Anchored`, `Massless`, `HasBody`), fitting existing padding, read as a bit test in the hot path.
- A **side table** in the physics world keyed by `SceneNode.Id` holding the body handle, collision group and material, probed only when `HasBody` is set.
- **A dense int index in Box3D's `userData`** (stored as index+1 so 0 means none). `b3World_GetBodyEvents` returns moved bodies in one contiguous array; draining is one array walk with an integer index per entry — no hash probe, no pinning, no handle table. A `GCHandle` per body would allocate, pin, and make teardown ordering fragile.

The claim that a node→body reference field is needed because "both directions are hot" does not hold. Write-back (native→managed) uses the physics side's own dense array, which already holds the `SceneNode`. The only hot node→body direction is `BuildRenderView` substituting an interpolated matrix, and the `HasBody` bit gates that probe.

**Transform authority is per body kind, and only DYNAMIC bodies are written back.**

| Kind | Authority | Mechanism |
| --- | --- | --- |
| Static | Authored | Placement snapshot only |
| Kinematic (platforms, scripted `BrushKind.Part` brushes, `P7` brush entities, the character presence body) | The scene | `b3Body_SetTargetTransform(target, fixedDt, wake)` each tick — velocity-based, which is what makes riders and friction behave |
| Dynamic | Physics | Drained from `b3World_GetBodyEvents` after the step |

Move events for kinematic bodies are **discarded**. `b3Body_SetTargetTransform` is documented *"The result will be close but maybe not exact"*, so echoing the achieved pose back would inject accumulating solver drift into an authored value, and would fight scripted posing the way `DemoBobAnimation` already has to be careful about. The `if (body.Kind != BodyKind.Dynamic) continue;` line is load-bearing and has no test that fails without it — write one.

**Render-tick interpolation is a render-only overlay.** `PosePrev`/`PoseCurr` live on the physics side; `Scene.BuildRenderView` substitutes an interpolated matrix where it currently reads `node.WorldMatrix` (`Scene.cs:285`). It must **never** pass through `SceneNode`'s transform setters, for three reasons at once: it would re-dirty the BVH every frame with a value that is not an edit; it would break `IPredictedMover.ApplyInput`'s pinned purity; and a script reading `node.Position` inside a tick must see the simulated value, not a display lerp. Accepted and documented cost: editor picking uses BVH bounds, i.e. the tick pose, so a pick during Play can be up to one tick stale.

### 2.7 Where physics sits in the fixed tick

```
:275   _inputManager.Update(dt);
:276a  SpectraConsole.Drain(in frame);              // C0's pinned drain
:276b  SpectraNet.ReceivePump(scene);               // RECEIVE
:276c  SpectraPhysics.Reconcile();                  // REPLAY — mover only. No step,
                                                    // no entity tick, no O8 phase, no script
:284   editor?.Update(dt);
       ─── FIXED TICK LOOP, 0..MaxTicksPerFrame at sv_tickrate (N2) ───
         [O8 PreSimulation]                         // entity logic writes platform TARGETS
         physics.PushKinematicTargets(fixedDt);     // b3Body_SetTargetTransform — PER TICK
         mover.ApplyInput(cmd, ctx, fixedDt);       // server: owned movers; client: local only
         physics.Step(fixedDt);                     // b3World_Step — SERVER ONLY (§3.1)
         physics.DrainBodyEvents();                 // dynamic bodies -> node transforms
         physics.DrainContactAndSensorEvents();     // MUST be inside the loop — see below
         touchTracker.Diff(scene, sink);            // ONE pass -> entity outputs AND Luau signals
:290     _sceneManager.Tick(fixedDt, _renderView);
         [P4 EntityWorld.Tick(fixedDt)]             // sees this tick's touches, same tick
         [O8 PostSimulation]
       ─── end tick loop ───
:296   scene.ProcessStaticWorldCompilation(...);    // ONCE PER FRAME — and physics's static
                                                    // hull swap happens HERE, in this slot
:296b  SpectraNet.SendPump(scene);
:301   _assetManager.PumpPendingUploads();          // ONCE PER FRAME
       [O8 PreRender(alpha)] + physics.PublishRenderPoses(alpha)
:352   viewScene.BuildRenderView(...);   :359 Render   :367 Present
```

Order inside the tick is forced, not stylistic: entity logic must write platform targets *before* the step (a door decides where it is this tick), the movers must resolve against a consistent world, and events must drain before the entity phase that reacts to them.

**The silent hazard is the event drain.** Box3D's contact, sensor and body event arrays are buffered in the world after a step and overwritten by the next one. Draining them *outside* the tick loop silently discards every tick's events but the last on any catch-up frame — a trigger that fires perfectly at 144 fps and drops volleys during a hitch. The same docs add that the arrays may become invalid if bodies or shapes are destroyed, so the drain must precede any destruction and must copy ids out rather than retaining native spans — the same refcount-or-copy rule [`docs/formats-and-pipeline.md`](formats-and-pipeline.md) §8 already pins for mapped pack spans.

Conversely the **static hull swap stays outside the loop**, in the `ProcessStaticWorldCompilation` slot, for the same reason that method is already there: running it up to `MaxTicksPerFrame` times per frame is up to five body and shape churn batches of which at most one can do work.

### 2.8 The assembly seam

A new **`SpectraEngine.Physics`** assembly, with the interface (`IScenePhysics`) owned by **Core** and wired by the host through a `SceneManager.PhysicsFactory`, exactly mirroring `ISceneEditor` / `SceneManager.EditorFactory`. Exactly one adapter assembly per backend (`SpectraEngine.Physics.Box3D`) holds every `[LibraryImport]`.

The interface must live in Core because `Scene` itself calls the static-world sync from `ProcessStaticWorldCompilation`. The *implementation* must not, because it carries a native dependency and `Test/SpectraEngine.Bsp.Tests`, `SpectraShade.Compiler.Tests` and a shader-only tool build must not require `box3d.dll` to resolve. Note the argument is **not** the one that separates `SpectraEngine.Editing` — physics must ship in a game binary; gizmos must not.

Mirror `EditingAssemblyBoundaryTests` with an assertion that `SpectraEngine.Physics` names no Silk.NET type and no `IWindow`, so it can serve `N1`'s headless dedicated server. A `NullScenePhysics` serves tests, headless collaboration (`T8`), and any host that wires nothing — a scene that renders and does not simulate is a supported configuration, and it is what Edit mode is.

### 2.9 Units and precision: two decisions that lock before code

**Units.** `b3SetLengthUnitsPerMeter` exists — it is declared in `include/box3d/constants.h`, and the tolerance macros in that same file are literally scaled by it (`B3_LINEAR_SLOP = 0.005f * b3GetLengthUnitsPerMeter()`). So this is a **one-line startup call**, not a per-vector conversion at the binding boundary: call the setter once, feed world units directly, and Box3D rescales its own collision and constraint tolerances. Residual work is small and specific — the setter scales *lengths* only, so `sleepThreshold` (m/s), density (kg/m³) and gravity still need restating in world units in one `PhysicsDefaults` file, and `B3_TIME_TO_SLEEP` (0.5 s) is unit-free.

What must still be decided is **metres per world unit**, and Roblox cannot be copied because Roblox is internally inconsistent: the Creator Hub documents 1 stud = 0.28 m, while the default gravity of 196.2 studs/s² against 9.81 m/s² implies 20 studs per metre (0.05 m/stud) — a factor of 5.6 apart, both official. [`docs/roblox-to-spectra.md`](roblox-to-spectra.md):75 records the row as planned/undecided and `ROADMAP.md` §11.13 asks the same question from the grid side. Answer it once, here, before content exists — it is the same class of decision as `ChunkCoord.CellSize`, which `.scmap`'s `META` section already validates and refuses loudly on mismatch. Note the interaction: at 0.28 m/unit a 32-unit cell is 9 m; at 1 m/unit it is 32 m.

**Float or double.** Ship `BOX3D_DOUBLE_PRECISION` **off** in v1, but write the binding with `B3Pos` as a distinct type from `B3Vec3` from the first line of generated code. The flag is **ABI-affecting** — the two types diverge, and the NuGet ecosystem ships the two modes as mutually exclusive packages precisely because of it — so this is settled before the binding is written, not after. The per-chunk-body design already closes the static world's precision exposure at any distance, so the real question is the target world radius for *dynamic* bodies: float gives roughly 1 mm resolution at 8 km (the distance `CsgBench openworld` actually uses) and roughly 1.6 cm at 100 km.

---

## 3. Networked physics

### 3.1 The ruling everything else rests on: the client's physics world is never stepped

Split physics into three populations with three network treatments:

1. **Static world collision** — identical on server and client, derived from data that already replicates (`.scmap` plus `N19` world edits). Never snapshotted; if it changes, that *invalidates* the rollback window rather than being replayed.
2. **The predicted character mover** — stateless, engine-owned state, read-only queries.
3. **Dynamic rigid bodies** — server-simulated only, replicated as interpolated transforms through `N17`'s ring. **The client's physics world contains none of them.**

Consequence: **the client calls `b3World_Step` zero times.** Kinematic platforms are positioned on the client with `b3Body_SetTransform` (a teleport) rather than driven with `b3Body_SetTargetTransform` (the server's path). A world that is written but never stepped accumulates no warm-start impulses, no contact recycling, no island partition — which is the entire content of Box3D's rollback disclaimer. **There is nothing on the client to restore, so there is nothing for a snapshot API to do.**

The server does not need one either. A server-authoritative host never rewinds its own simulation; the only thing that ever wants historical physics state is lag compensation, and lag compensation rewinds *hitboxes for a query*, not the solver.

**Make it structural, because it is the ruling most likely to erode.** Two mechanisms: `Step()` throws on a client-configured world, with a test asserting it; and a standing invariant that **a thing may be predicted only if its entire state is an engine-owned value struct.** The mover passes. A rigid body does not and cannot be made to. That rule is what stops prediction creeping into the solver one convenient exception at a time — and every future feature will have a plausible argument ("just the grenade", "just my own vehicle").

Note the corollary for §2.6: the body-event write-back is a **server-only** path. On the client those same nodes are driven by `N17`'s interpolation ring.

### 3.2 The rollback loop, with the arithmetic

```csharp
void Reconcile(in ServerMoverUpdate authoritative)
{
    uint t = authoritative.LastProcessedSequence;
    if (!_ring.TryGet(t, out PredictedFrame f)) { Snap(authoritative); return; }

    // 1. Most ticks predicted correctly. Accept silently.
    if (WithinTolerance(f.State, authoritative.State)) { _ring.AckThrough(t); return; }

    // 2. Refuse to replay across a geometry change. N19 world edits and
    //    realms.md Dormant->Active transitions both land here.
    if (f.StaticRevision != _physics.StaticRevision) { Snap(authoritative); _ring.Clear(); return; }

    // 3. Cap the depth, or a latency spike becomes a 60-tick replay in one frame,
    //    which overruns, which causes more corrections. Same shape as N2's
    //    MaxTicksPerFrame: drop time rather than spiral.
    if (_currentTick - t > _netMaxReplayTicks) { Snap(authoritative); _ring.Clear(); return; }

    // 4. Restore and replay. ONLY the mover.
    _mover.RestoreState(authoritative.State);
    for (uint s = t + 1; s <= _currentTick; s++)
    {
        PredictedFrame pf = _ring[s];
        var ctx = new PredictionContext {
            Tick = s, StaticRevision = pf.StaticRevision, World = _physics,
            // Ground poses read AT TICK s from the ring — the line that makes replay
            // on a moving platform reproduce the original prediction.
        };
        _mover.ApplyInput(pf.Command, ctx, _fixedDt);
        _mover.CaptureState(ref _ring[s].State);   // rewrite history as replayed
    }
}
```

**The budget, stated honestly.** At 60 Hz and 100 ms RTT there are about six unacked ticks, so a worst case where a correction lands every frame is six replayed ticks per frame. One replayed tick is one mover step: roughly one to four `CastMover` slide iterations plus one `CollideMover`, `SolvePlanes`, `ClipVector` and a ground probe — call it five to eight broadphase-backed convex sweeps. So **roughly 30–50 sweeps per frame worst case for one predicted mover**. Ring memory is about 64 B per state × 64 ticks ≈ **4 KB per predicted mover**, sized by `max(net_interp, net_maxreplayticks)` and **shared with `N17`'s interpolation ring**, which [`docs/networking.md`](networking.md) already says to build once.

The multiplier is the point: **replay cost = (predicted things) × (latency depth) × (per-tick cost)**. At 6×, one stateless mover is free and a stepped rigid-body world is a 6× physics budget. That is the affordability answer, and it is why the count of predicted movers is bounded by the value-struct rule rather than by CPU.

**The one number missing is the only one that converts this from arithmetic into a budget: microseconds per `b3World_CastMover` against a realistic static hull population.** That measurement belongs in `Y1`'s native harness, which has to exist anyway — it is three lines in a deliverable that is already scheduled. Two honesty notes to carry with the arithmetic: it assumes 60 Hz, and a 250 ms spike makes the depth 15 ticks, which is why the cap is load-bearing rather than defensive.

### 3.3 What is predicted, what is interpolated

**Predict exactly one thing: the local player's own mover.** Everything else — remote players, all dynamic bodies, all game-rule outcomes, all entity and Luau logic — is interpolated at `now - net_interp` or waits for the server. On game-rule outcomes the reason is UX rather than correctness: a retraction (you saw the pickup, then it vanished) reads as a bug, while a 100 ms delay reads as latency.

**Replay calls only `IPredictedMover.ApplyInput`** — no `b3World_Step`, no `SceneManager.Tick`, no `EntityWorld.Tick`, no `O8` phase, no CSG recompile. Replaying a script means either making every script pure (impossible) or executing side effects N times per correction: a `logic_relay` firing six times, a sound playing six times, a counter incrementing six times. **This must be written into `N2`'s tick-phase contract while `N2` is unbuilt**, because after `O8`'s pump points harden it is a rewrite.

**A predicted frame snapshots more than the mover:** the mover state, the input command, the poses of every kinematic platform in range *at that tick*, and the static-world generation counter. The platform pose is the piece naive implementations omit, and omitting it is what makes the double-replay purity test pass while real reconciliation on an elevator rubber-bands — because a purity test on flat ground, a reconciliation test on flat ground, and a moving-platform test without prediction all pass. Only prediction plus a moving platform plus a correction reproduces it, and the symptom reads as *"the network is bad near elevators"*.

### 3.4 Moving platforms

**Riders are carried ground-relative — the mover state holds `GroundNetId`, `GroundLocalOffset`, `GroundEpoch` — never by reparenting the character node.** Reparenting at gameplay rates is a structural replication op that changes the node's interest cell and the meaning of its transform ([`docs/networking.md`](networking.md) §3.3 replicates parent-relative transforms for editing and cell-relative world transforms for gameplay — deliberately different codecs). Ground-relative carry makes the carry a pure function of (ground pose at tick, stored local offset), both of which are in the snapshot, which is exactly what makes it survive replay. Storing `GroundNetId` per tick also handles stepping off mid-window with no special case.

**Rider platforms replicate pose *and* velocity, and the client extrapolates the platform forward to the current tick before predicting the player against it.** This is the asymmetry that ruins elevators and it is not obvious: the local player is predicted at tick *now* while the platform's pose is known only at `now - net_interp`. Predicting the player against a 100 ms-old platform means correcting every single tick — permanent rubber-banding on every lift, in a system that is otherwise working. Extrapolation is safe here specifically because platform motion is scripted and smooth rather than player-driven; clamp it (`net_platform_extrapolate_max`, around 250 ms) with a snap fallback.

**For platforms driven by closed-form motion** (`P6` `logic_timer`-style loops, waypoints), replicate the motion *parameters* and have both sides evaluate at the shared tick number. Exact, zero drift, zero per-tick bandwidth, and it composes with `N2`'s fixed tick because the tick number is already a clock both sides agree on.

A non-carving brush — a `BrushKind.Part` brush (§2.3a) or a `P7` entity-owned one — already holds its geometry in **brush-local space**, so the same local plane set becomes the hull set, built once from `Brush.LocalFaces` and never rebuilt: a door opening stays a matrix write, exactly as it is for rendering. The silent failure to watch for is a platform whose hulls are *rebuilt* per frame instead of *transformed* per frame: it renders identically and destroys the frame budget, which is `P7`'s own documented hazard wearing a physics costume.

### 3.5 Ownership and its exploit surface

**Automatic network ownership is a slow, hysteretic, server-side policy over the `N13` interest grid** — never a per-tick recompute, never a new spatial structure. Suggested shape: `sv_ownership_rate 2` (Hz, not the sim tick), `sv_ownership_radius 96` (well inside `sv_interestradius`), `sv_ownership_hysteresis 24`, `sv_ownership_mindwell 1.0`, ties broken by `NetId` ascending so the policy carries no wall clock. Transfers move the **whole joint-connected set**, because a mechanism solved by two authorities is not a mechanism.

**The handoff has three parts and skipping any one produces a distinctive bug:**

1. Authority passes **through the server** — `revoke(old)` → server simulates → `grant(new)` — so there is never a window with nobody simulating and never one with two owners simulating.
2. The grant carries **full absolute state**, not a delta. Without it the new owner starts from an interpolated pose 100 ms in the past and the body visibly jumps on every handoff.
3. A **monotone per-`NetId` `OwnershipEpoch`** on the reliable channel, with the server dropping any client-to-server body update whose epoch is not current. Omitting this lets a delayed packet from the *previous* owner rewrite the body after handoff — simultaneously a correctness bug and a trivially weaponisable exploit.

**A client-owned body is a client-authoritative transform**, and gets the mandatory validation envelope [`docs/networking.md`](networking.md) already requires, pinned by the same `(ownership × magnitude × scope)` matrix test: stale-epoch rejection, ownership and interest scope, linear and angular speed clamps, a teleport clamp, and a **swept solidity test against the static hulls with a skin width** — never a bit-exact containment test. (The skin is belt-and-braces here rather than compensation: hulls remove the `SimdPlane` cross-ISA divergence source that motivated it for the CSG path.)

**And the honest line, which belongs in the docs verbatim because Roblox says it about its own system:** *"Roblox cannot verify physics calculations when a client has ownership over a BasePart. Clients can exploit this and send bad data to the server, such as teleporting the BasePart, making it go through walls or fly around."* Client-owned bodies are unverifiable **in principle**, not merely unverified in v1. So `sv_physics_clientownership` defaults to **0** for anything competitive — which means the responsiveness benefit that motivates auto-ownership is unavailable exactly where responsiveness competition is fiercest. Say so, rather than letting a developer discover it. Relatedly, Roblox documents that an owner can fire `Touched` the server never saw, which is why trigger events here are authored **server-side** and never accepted from an owner.

The permanent brush refusal [`docs/networking.md`](networking.md) §4.4 already ships survives untouched and gains a second reason: a brush is a static hull, and a static hull has no owner because it does not simulate.

### 3.6 Two corrections owed to `networking.md` — **both now applied there**

Both landed in `networking.md` §4.4 on 2026-08-21; they are kept here with their arguments because the arguments are this document's.

1. **The purity contract was unsatisfiable as literally written.** §4.4 says `ApplyInput` *"MUST be a pure function of (captured state, cmd, dt)"*, and any mover that queries geometry violates that — and it must query geometry. Amend to **"pure with respect to a pinned world revision"**: `ApplyInput` may issue read-only queries, and the design guarantees the queried world is identical between the original prediction and the replay, which is what the static-world generation counter and the platform pose ring exist for. Do this **before `N16` hardens**, or the first implementation either quietly breaks the stated contract or the double-replay test is written so as to hide it.
2. **Drop `NetInputCommand.DeltaTime`.** Under `N2`'s fixed tick it is redundant, and a client-supplied `dt` on a client-authored input packet is free speed for anyone who edits it.

---

## 4. The gameplay layer

### 4.1 The Roblox-parity surface, and where it should deliberately diverge

Box3D delivers bodies, shapes, joints, casts and contact events. **Everything a Roblox developer actually types — `Anchored`, `CanCollide`, `.Touched:Connect`, `GetPartsInPart`, a character that walks up stairs — is above that line and Spectra writes all of it.** Sizing this arc as "bind a C library" underestimates it by roughly an order of magnitude: the binding is one milestone; the gameplay layer is most of the rest.

Three places to diverge from Roblox on purpose, each because the Roblox behaviour is a limitation rather than a semantic anyone depends on:

- **`CanQuery` is decoupled from `CanCollide`.** Roblox's own docs state *"`CanCollide` must be disabled for `CanQuery` to take effect"*, which is why `RaycastParams.RespectCanCollide` exists at all. "Collidable but not raycastable" — a solid wall a targeting ray should pass through — is a routine requirement Roblox cannot express. Decoupling is free because `CanQuery` is enforced in Spectra's own BVH traversal. Keep a `RespectCanCollide` escape hatch in the query params for parity.
- **`Touched` fires for scripted moves.** Roblox's `Touched` *"only fires as a result of physical simulation and will not fire when the part's `Position` or `CFrame` is explicitly set"* — the most-reported Roblox "bug that isn't". Spectra's touch pass is a per-tick overlap diff driven from the *moved* side, so a scripted or gizmo-driven move fires it. Two anchored world brushes still never generate a pair with each other — the same limit as Roblox — but for the honest reason that neither moved.
- **Collision groups cap at 64, not 32.** Box3D's filter is `uint64` category and mask bits, so 64 named groups with a loud error on the 65th, following the same rule `F2` applies to duplicate GUIDs.

**Humanoid: ship the behaviour, refuse the name.** [`docs/roblox-to-spectra.md`](roblox-to-spectra.md):44 already rules that *"a hollow `Humanoid` would be worse than none"*, and that survives — Roblox's `Humanoid` is four unrelated things welded together (movement, health and death, avatar rig and animation, tools/nametag/state machine). Ship the movement third as `CharacterController`, with Roblox-identical member names where the meaning is identical (`WalkSpeed`, `JumpHeight`/`UseJumpPower`, `MaxSlopeAngle`, `MoveDirection`, `FloorMaterial`, `Move`, `MoveTo`, `StateChanged`), so a port is mechanical, and nothing named `Health` or `Died` exists to lie about. Two mapping notes worth recording: Roblox's `MaxSlopeAngle` defaults to **89°** (Roblox characters climb essentially anything by default), and Roblox documents **no step-height property at all** — its stair behaviour falls out of leg geometry plus the simulation. Spectra must therefore expose `MaxSlopeAngle` *and* an explicit `StepHeight`, and pick defaults that feel Roblox-like rather than copying a number that does not exist.

**Per-face surface materials are resolved engine-side, not by Box3D.** A hull shape carries exactly one Box3D surface material (`b3ShapeDef.materials` is documented as ignored for convex shapes), so a six-textured brush cannot express six frictions through the solver. The resolution is free because the data is already indexed the right way: a ray result and a mover collision plane both carry a normal and a point, the shape resolves to its `BrushPlacement` through `userData`, and `Brush.FaceSurfaces` is **plane-indexed by ruling R‑3** — so picking the plane whose normal best matches the contact normal is a short loop over about six planes and yields the exact `MaterialRef` the renderer used. Set the hull's base material friction from the brush's dominant face so the solver still gets a sane value.

### 4.2 `Anchored = false` on a brush is a CONVERSION, not an exception

The engineering reason for refusing a *dynamic brush* is correct and must be preserved: a dynamic body writes a transform every tick, a transform write on a brush node dirties its cells, and a dirty cell launches a background compile — so a single dynamic brush launches a background CSG compile **every tick, forever, while everything still renders correctly**. That is `P7a`'s named silent hazard arriving from a new direction (§2.3a gate 5), and a code-review convention will not hold once scripts can set the property.

But the conclusion "therefore `Anchored = false` throws" takes away a pillar to protect an implementation detail. `CLAUDE.md`'s open-world pillar says brushes are placed like Roblox *parts*, and [`docs/roblox-to-spectra.md`](roblox-to-spectra.md) sells brush-as-part as the onboarding story. A Roblox developer selecting a wall and unchecking `Anchored` is performing the most basic action in their vocabulary.

**Ruling:** at **edit time**, `Anchored = false` on a brush node is an editor command that converts the node — one incremental recompile, one `IEditorCommand`, undoable, and Play/Stop already captures and restores exactly this kind of authored state. At **script runtime** it is either the same conversion with a documented one-recompile cost or a refusal whose message *names the conversion* — that is the sign-off in §7. What must not ship is an unconditional throw with no route forward.

> **AMENDED by §2.3a — the conversion target changed, and it is strictly better.** This ruling was written before `BrushKind` existed and named an `O7` dynamic part (a `MeshRenderer` node carrying a box mesh) as the destination. It is now **`BrushKind = Part` on the same node, through `ConvertBrushKindCommand`** — the brush keeps its planes, its per-face materials and its texture axes, and `SceneNode.Id` never changes, so every `NodeRef`, `targetname` and undo entry pointing at it stays valid. `O7`'s `MeshRenderer` part remains the right answer for an imported mesh, not for a brush the user drew. The refusal at body creation (§2.3a) is the runtime half of the same ruling: creating a dynamic body on a `World` brush node throws with a message naming `BrushKind = Part`. Two costs carry over from §2.3a and must be in the command's undo label: the UV bake is mandatory and the world-aligned flag does not survive the round trip, and the body itself still needs `Y6`.

### 4.3 Touch and triggers are one pass with two consumers

`P8`'s `trigger_multiple`/`trigger_once`/`trigger_teleport` and Luau `.Touched` are **two faces of one per-tick overlap diff**: the diff emits `(sensor, visitor, began|ended)` pairs, the entity layer turns them into `OnStartTouch`/`OnEndTouch`/`OnTrigger` outputs, and the script layer turns them into `Touched`/`TouchEnded` signals delivered through `O3`'s deferred queue. A trigger volume is a **`BrushKind.Part` brush** with `CanCollide = false, CanTouch = true` (§2.3a) — so it costs zero world surfaces and zero recompile, and a designer's no-code trigger and a scripter's `.Touched` can never disagree about whether contact happened. *(It was specified as a `P7` entity-owned brush; the declared bit reaches the same place without `P4`. It does not cost "zero new geometry code" — it costs the one `BuildRenderView` arm and the part-mesh cache §2.3a prices, paid once for every part brush in the engine.)* **Trigger volumes are drawn always** (§5.2), which is the same rule §2.3a already imposes on every part-brush outline.

The diff is driven from the **moved** side — character movers, awake dynamic bodies, kinematic bodies that moved this tick, moved sensors — so cost is O(movers × local candidates) rather than O(touch-enabled parts). Two hazards inherited rather than new: `O3`'s fan-out storm means every pair lookup must sit behind a per-node has-subscribers bit, and boundary chatter needs the same hysteresis `P8` specifies for triggers and `N13` for interest cells. A trigger destroyed mid-touch must still deliver `TouchEnded`.

**The non-obvious reason the diff must be engine-owned rather than backend sensor events:** the character is a *mover*, not a body, and Box3D's sensor events apply only to kinematic and dynamic bodies — so backend sensors could never see the player. That is also why the character carries a **kinematic presence body** (§4.4): without one, triggers built the obvious way silently never fire for the player, which is the single most likely way this integration ships broken.

### 4.4 An honest budget for the character controller

**This is the schedule risk of the whole arc, it is `L` and not `M`, and it is not a physics-engine feature.**

Box3D's mover documents four read-only primitives and *nothing* about stairs, ground snap or slope limits. Jolt is the same shape — `CharacterVirtual` is a separate non-simulated class and stair-stepping is application-level helper code. **So the mover algorithm is Spectra's, permanently.** Write it against a four-method `ICharacterCollisionSource` (sweep capsule, gather planes, solve planes, clip velocity) with two implementations: one over brush hulls gathered from the BVH (no native dependency — capsule-versus-convex reduces to signed distances against `Brush.LocalPlanes`, the engine's native geometry form), one over Box3D's mover. The swap is then a constructor argument.

The per-tick algorithm is fixed and backend-independent: accelerate horizontal velocity toward the wish direction (ground accel or air control) → apply gravity if ungrounded → sweep-and-slide over N iterations of cast, advance, clip → if blocked and grounded, an up-forward-down step probe accepted only if the landing plane is within `MaxSlopeAngle` → gather and solve planes to depenetrate (which is what handles spawning inside geometry) → ground check with a snap within `GroundSnapDistance` so the character does not launch off ramps.

**Every failure mode in that list is a tuning failure that unit tests cannot catch:** catching on chunk seams and brush edges, launching off ramps, stepping onto ledges that should block, jitter when the depenetration solve fights the sweep. `E8` already carries the same note about snapping needing playtesting rather than unit tests; this is that hazard at three times the size, and it sits directly on the path to `N16`'s stated deliverable of two people walking around a level together. **Budget playtesting, not just implementation.**

> **And freeze the collision source before `N16` validates prediction against it.** Shipping the mover on brush hulls, tuning movement constants against that surface, validating reconciliation against it, and *then* swapping to Box3D's mover changes the physical surface — different skin widths, a different speculative contact distance (`B3_SPECULATIVE_DISTANCE = 4 × B3_LINEAR_SLOP`), a different rest offset — and therefore changes where a player can stand and which ledges block them. That invalidates every reconciliation test, every tuned constant, and any level content authored against the first surface. Either choose the source up front, or make a dual-source trajectory-agreement test (the same recorded input ring through both sources, trajectories agreeing within a tolerance) a **merge gate on `N16`** rather than a later nicety.

---

## 5. The editor story

### 5.1 There is no physics world in Edit mode

It is created on **Play** and destroyed on **Stop**. That single decision makes "Stop must not leave objects moved" *structural* rather than a restore that has to be right: nothing simulated existed at capture time, and the only state to undo is transforms `P11a`'s diff-restore already handles.

**Ordering is the whole content of this section, and getting it wrong is the failure most likely to survive review** — because it only manifests when a body is still awake at the moment Stop is pressed.

```
Play:                                     Stop:
  1. P11a captures authored state           1. lua_close                (O9's total teardown)
  2. history barrier                        2. physics.Dispose()        (ALL bodies gone first)
  3. physics = PhysicsFactory(scene)        3. P11a diff-restore through LocalTransform setters
  4. static chunks pushed from placements        -> dirties only the cells gameplay disturbed
  5. dynamic parts + P7 brush entities           -> never MarkStaticWorldDirty
  6. O9 creates the fresh lua_State              -> never the synchronous RebuildStaticWorld
```

Pin it with a test in the style the existing editing self-test already uses: Play, simulate N ticks with bodies visibly moving, Stop, assert every authored transform is restored **bit-for-bit** (absolute-value commands make exact equality the right assertion, not a tolerance) and that the compile which follows took the **incremental** path, not the full walk. `O9`'s hard requirement that Stop stay incremental at every world size is inherited, not re-argued.

A gizmo drag on a live physics body during Play is an ordinary `IEditorCommand` landing in the transaction the gesture already opened, using `b3Body_SetTransform` — a teleport is the correct primitive at gesture rate, since a target transform would have the body chase the cursor with a one-tick lag and real velocity — with both velocities zeroed, or the body inherits the drag as momentum on release.

### 5.2 Visualisation is drawn Spectra-side

Collision hulls for the selected node, **trigger volumes always**, the character capsule and its ground normal during Play — all through the existing depth-off `DebugDraw` line pass that already works identically on OpenGL, D3D11 and D3D12 and already draws gizmos, the marquee and the selection highlight. Do **not** use Box3D's own debug-draw callback: the engine already holds every hull's source geometry (it built them), so nothing is gained by asking native code to hand geometry back through a reverse P/Invoke with a debug-shape lifetime protocol.

Trigger volumes are drawn *always*, not on selection, for the reason `realms.md` §7.5 item 3 already establishes for ghosted dormant subtrees: an invisible gameplay volume is indistinguishable from a bug, and this repo has already had exactly that mistake reported as a brush bug (commit `d4701d6`).

**The same rule covers every part brush, and it arrives earlier than this milestone.** §2.3a makes always-drawn part-brush outlines a `P7a` acceptance criterion, on the same argument and through the same pass — a brush that renders but does not fuse, may crack at a contact seam and may z-fight against a coplanar world face is exactly the kind of always-on discrepancy that gets reported as an engine bug. A trigger volume *is* a part brush (§4.3), so this section's rule is a special case of that one, not a second mechanism. **What is not yet decided** is whether either overlay is suppressed in Play mode; the `d4701d6` lesson argues against suppression and nothing has ruled on it.

### 5.3 Physics never runs in a Team Edit session

Two peers stepping independent solvers drift, and `T9`'s document-digest convergence check would report that drift as a data conflict no user caused. Write this into the `T`-arc's invariants, not only here.

### 5.4 An edit-mode simulation tool is deliberately deferred

"Drop these props onto the floor" is a real level-design verb, and Roblox Studio's lack of it is evidence rather than proof. If it lands it must be an explicit, opt-in `Simulate Selection` tool that steps a **scratch** world and commits one `SetTransformCommand` — undoable, never ambient. It is called out here because retrofitting a scratch world into a design that assumes none exists in Edit mode is the expensive version.

---

## 6. Milestones

**Prefix `Y`**, checked free against `F/E/P/S/R/H` (ROADMAP), `O0`–`O9` (roblox-onboarding), `D0`–`D22` (formats-and-pipeline), `C0`–`C12` (console), `N0`–`N22` and `T0`+ (networking). No existing id is renumbered. *(The review that produced this document proposed `G` on identical single-letter reasoning; the swap is a find-replace confined to this file — see §7.)*

**One milestone this document designs does NOT carry a `Y` id: `P7a`.** §2.3a's world/part split is `ROADMAP.md` milestone **`P7a`**, in the `P` arc, because it is a scene-graph and admission change that needs no physics engine, no native code and no entity runtime — several `Y` milestones depend on it, and it depends on none of them. This document owns its design; `ROADMAP.md` owns its scheduling and its ruling `R‑9` inheritance.

**Where this slots into `ROADMAP.md`:** as a new parallel track — **Arc Y — Physics** — beside Rendering, Shader authoring, Entities and Hosting. **It is not on the critical path and it does not shorten it.** `ROADMAP.md` §4's sequence to a usable editor (`F1, F2, E1–E4, E6, E7, P2, P11a`) is unchanged. `N2`'s fixed tick is the hard prerequisite for anything that steps, and `N16` already ships its one predicted mover with **no physics engine at all**, so the Box3D decision blocks nothing currently scheduled.

| id | milestone | scope | depends on | risk | size |
| --- | --- | --- | --- | --- | --- |
| **Y0** | Query flags and BVH overlap queries | `PhysicsFlags` byte on `SceneNode` with the brush-node `Anchored` conversion path (**which is now `ConvertBrushKindCommand`**, §2.3a / §4.2); `QueryParams` with `RespectCanCollide`; **`SceneBvh` gains box and sphere overlap queries** (it has only `Raycast` and `QueryFrustum` today) — **`P8`'s trigger queries now depend on this**, because §2.3a killed the brush-model BSP they were specified against; `Scene.GetPartBoundsInBox`/`InRadius`; `Scene.Raycast` starts honouring `CanQuery`; the 64-group `CollisionGroups` registry. **No native code, no physics engine.** | `F2`; `P7a` for `BrushKind` | LOW. One sharp edge, **whose wording predates the world/part split and is now a §7 sign-off**: `CanQuery = false` on a *static world brush* cannot be honoured by the per-cell BSP (it is derived from the carve, and excluding a brush would change compiled output the determinism oracles compare) — refuse it on **`BrushKind.World`** brushes with a named message; it is legal on **part brushes** and on dynamic parts, whose queries never route through the BSP. `CanCollide = false` on a world brush **is** honoured, for free, because hulls come from placements. `Y0` is also where the `realms.md` R15 relaxation becomes *reachable* — it ships with whichever of `Y0`/`Y8` first makes `CanCollide` real, never with `P7a` | S–M |
| **Y1** | Native gate: vendor, build, bind, publish AOT | Vendor Box3D at a **pinned commit** (not a tag — `v0.1.0` is the only one and `main` has moved); per-RID CMake build reusing the vendoring machinery the Luau decision already requires; Spectra's own `[LibraryImport]` layer over the 60–90 entry points actually used, with `DisableRuntimeMarshalling`; struct-ABI dump-and-diff in CI; **decide float vs double here** (ABI-affecting) and model `B3Pos` as distinct from `B3Vec3` regardless; call `b3SetLengthUnitsPerMeter` once. **Deliverable: a throwaway NativeAOT console on win-x64 AND linux-x64 that creates a world, adds a hull, steps 1000 times and prints a state hash — plus a `CastMover` microbenchmark (§3.2)** | nothing | **HARD GATE**, same posture as `N0`/`O0`/`D0` and for the same reason: this project has been burned by a library that published clean under NativeAOT and then crashed the native binary. A failure here changes the dependency and every milestone after it | M |
| **Y2** | `SpectraEngine.Physics` and the `IScenePhysics` seam | Core-owned interface, `SceneManager.PhysicsFactory`, `NullScenePhysics`; world lifecycle; `workerCount = 1` with no task callbacks; the dense `PhysicsBody` arrays behind the int-index `userData`; the refcounted `Brush → hull` cache with explicit render-thread release; `phys_*` cvars coordinated with `C0` | `Y1` | LOW–MEDIUM. Boundary test mirroring `EditingAssemblyBoundaryTests` (no Silk.NET type, no `IWindow`, so `N1` can host it). Real hazard is native lifetime ordering at shutdown — everything released on the render thread before world destruction, no finalizers, plus a 10k create/destroy leak test | M |
| **Y3** | The static world as convex hulls | Per-chunk static bodies at `ChunkCoord.MinCorner`, one hull per **owned** placement in cell-local coordinates, hulls from `Brush.LocalFaces`; **harvest-time sync** in `ProcessStaticWorldCompilation`'s success branch and in `RebuildStaticWorld`'s synchronous path; dirty-cell-only, affected-shape-only rebuild; `phys_draw_hulls` through `DebugDraw` | `Y2` | MEDIUM, and verification **is** the milestone: hull count equals placement count after every edit sequence including undo/redo; a `CsgBench`-style scenario showing a one-brush edit's collision cost is flat at 1k/10k/50k parts with half at +8,000 units; `StaticWorldCompileCount` unchanged by physics existing; origin-invariance of local hull geometry. Sharpest edge is the refcount when one `Brush` backs many placements — a leak or double-free there is silent until it is an access violation | L |
| **Y4** | Query unification and the divergence oracle | One gameplay-facing `Raycast`/`OverlapAABB`/`ShapeCast` on the seam, returning `SceneNode` plus the engine-resolved per-face `MaterialRef`; **finish** the demotion of `CsgWorld.Raycast`/`ContainsPoint` in their own XML docs — **its part-brush half is owed at `P7a`, not here** (§2.3a), and `Y4` only adds dynamic bodies and the character to the same sentence; **Release-configuration** divergence oracle; audit and reroute every gameplay-shaped caller | `Y3` | MEDIUM. This is where the four-spatial-structures hazard is closed or shipped. The oracle must be Release — repeating `VerifyTrustedDiff`'s DEBUG-only shape means divergence is only ever caught by developers. Second hazard: if `N16` ships before `Y4`, the reroute is a behaviour change to a validated, prediction-critical path | M |
| **Y5** | Character mover — the one `IPredictedMover` | `ICharacterCollisionSource` plus the brush-hull implementation; sweep-and-slide, step probe, ground snap, slope limit, air control, jump, `CharacterState`; `CaptureState`/`RestoreState` as a struct copy; the kinematic **presence body**; `PredictionContext` replacing the bare `dt`. **This is `N16`'s mover** | `Y0`, `N2` (hard), `Y3`/`Y4` for the backend source | **HIGH — and the risk is feel, not code** (§4.4). The double-replay purity test must be written **first**, and run **with a moving platform in the scene**. Box3D's mover carries an experimental banner upstream; the seam plus a dual-source agreement test is the mitigation | L |
| **Y6** | Dynamic bodies, transform authority, render interpolation | Body creation from primitives and from `.smodel` `COLL` hulls; mass, materials, velocities, impulses, sleep and wake; dynamic-only write-back; `PosePrev`/`PoseCurr` and the `BuildRenderView` substitution driven by `O8`'s `PreRender(alpha)`; `SetPhysicsTransformCommand` for gizmo drags during Play | `Y4`, `O7`, `N2`, `O8` | MEDIUM–HIGH. Three quiet hazards: writing kinematic move events back injects accumulating solver drift into authored transforms with no failing test; the render-pose overlay must never reach a transform setter; and **every awake body writes a transform every tick**, which is `O3`'s fan-out storm and `N13`'s interest churn arriving together — the has-subscribers gate and cell hysteresis must exist *before* this lands. Ruling `R‑9` applies: not concurrent with `E4`/`E6`/`P7` | L |
| **Y7** | Kinematic part brushes and brush entities, moving platforms, clip volumes | **`BrushKind.Part` brushes as kinematic bodies carrying their own hulls** (§2.3a), driven per tick by target transforms; `P7` entity-owned brushes on the same path; the collision-only clip volume — now a `Part` brush with `CanCollide = true` and nothing drawn, **not** a `func_clip` entity (§2.3); platform pose ring shared with `N17`; the pose-plus-velocity and closed-form replication paths | `P7a`, `Y6`; `P7` only for the entity-owned half | **HIGH**, and the reason is a coupling nobody had written down: the instant a brush leaves the static placement list it vanishes from physics — a door that becomes a door stops being solid, and a converted part falls through the world. **The admission change and the kinematic path are one change.** Second hazard: hulls rebuilt per frame instead of transformed per frame render identically and destroy the frame budget. Third, from §2.3a: a part hull must **never** be deposited on the per-chunk static body, however tempting the batching looks | L |
| **Y8** | Touch tracking, triggers, `Touched`/`TouchEnded` | The moved-side per-tick diff; signals through `O3`'s deferred queue; `GetPartsInPart`/`GetTouchingParts`; `P8`'s trigger entities wired to the same diff | `Y5`, `Y7`, `P8`, `O3` | MEDIUM. `O3`'s fan-out gate is mandatory; boundary chatter needs `P8`'s hysteresis rule; a trigger destroyed mid-touch must still deliver `TouchEnded`; sensor and contact arrays are overwritten by the next step, so the drain lives **inside** the tick loop | M |
| **Y9** | Model collision: `.smodel` `COLL` end to end | Author → cook → `COLL` plane sets → `Brush` constructor → hull. Fallback when nothing is authored: one hull of the whole mesh with the vertex cap doing the simplification. `scook verify` runs `Brush`'s constructor at cook time so a degenerate hull is cook-fatal | `Y6`, `D17` | LOW–MEDIUM. `Brush`'s constructor is strict by design (it rejects fewer than four planes, duplicate same-facing planes, and unbounded volumes), so an imported hull may be refused where a physics engine would accept it. That strictness is the feature — a named cook error beats a runtime explosion — but it needs a real model corpus before it is trusted. `maxVertexCount` goes in the cook key | M |
| **Y10** | Play/Stop lifecycle and editor visualisation | The §5.1 ordering; the bit-for-bit restore test plus the incremental-path assertion; hull, trigger and capsule overlays; `Simulate Selection` **only if** signed off | `Y6`, `P11a`, `O9` | MEDIUM. The failure this milestone exists to prevent — press Stop, find objects moved — is the one most likely to survive review | S–M |
| **Y11** | Luau bindings and the mapping-doc truth pass | Bind the surface through `O5`'s generator (one source of truth → C# binding + `__index`/`__newindex` + `spectra.d.luau`); `PhysicalProperties`, `OverlapParams`, `RaycastParams`; rewrite the `Anchored`, `CanCollide`/`CanQuery`, `Touched`, `Humanoid` and "not built yet" rows in [`roblox-to-spectra.md`](roblox-to-spectra.md), and answer the `1 stud = 1 world unit` row | `O5`, `Y6`, `Y8` | MEDIUM, and it is a **naming lock** in `O5`'s sense: `Anchored`, `CanCollide`, `Touched`, `WalkSpeed` bake into every piece of content and every tutorial the moment they ship. Decide `CharacterController`-versus-`Humanoid` before this lands | M |
| **Y12** | Networked dynamic bodies, ownership, validation envelope | Owner-authoritative transform and velocity replication through `N17`'s ring; ownership policy over the `N13` interest grid with radius, hysteresis, dwell and `NetId` tiebreak; joint-connected-set transfer; the three-part handoff with `OwnershipEpoch`; the full validation envelope; `sv_physics_clientownership` defaulting off | `Y6`, `N13`, `N16`, `N17` | **HIGH — the security milestone of this arc**, as `N4` is for the transport. A missing epoch check lets a delayed packet from the previous owner rewrite a body: it reads as a physics glitch and is an exploit. Do not let this milestone quietly acquire a rollback requirement | M–L |
| **Y13** | Rollback re-evaluation gate *(a decision, not a build)* | Required before any milestone depends on **predicted rigid bodies**: is issue #134 answered, and does the exposed API carry a context that excludes interned shape geometry (which is what the author's 2026-08-19 comment says is needed)? Plus the measurement: does a hand-rolled per-body restore diverge tolerably, given that it restores none of the warm-start, contact-recycling or island state? | `Y6`; blocks nothing scheduled | This **is** the risk-register entry. If the measurement is bad and #134 is still unanswered, this is the trigger to take the Jolt fallback — affordable precisely because `Y0`–`Y5` depend on nothing Jolt lacks | S |
| **Y14** | Physics self-test, **OFF by default** | A `--physicstest` PASS/FAIL line following `EditingSelfTest`'s discipline: drop a box on a brush floor and assert rest height within tolerance in N ticks; assert the two raycasts agree on a known ray; assert hull count equals placement count; assert a chunk's body count returns after edit-then-undo | `Y3` minimum; full form after `Y5` | LOW, with one lesson to obey verbatim: `CLAUDE.md` records that leaving the editing self-test on made the scene *"visibly pop every five seconds with nobody touching the mouse — which is indistinguishable, from the outside, from a brush-only jitter bug, and was once reported as exactly that."* A falling test box in the **live** world would be worse. **Private world, off by default** | S |
| **Y15** | Convex decomposition at cook time *(deferred)* | Automatic multi-hull decomposition for models with no authored collider, emitting `hullCount > 1` into the `COLL` section the format **already supports** | `Y9` | MEDIUM, entirely dependency-shaped: a second native library with its own vendoring, per-RID build and licence audit, inside a cook that must stay deterministic. Deferred deliberately — it lands later with **zero format change**, which is the whole reason it can wait | M |
| **Y16** | Lag compensation *(deferred, and notably NOT gated on #134)* | Rewind other players' hitboxes to the shooter's view time using `N17`'s ring and cast against proxy shapes at historical poses. No solver rewind, no engine snapshot, no re-stepping — because the mover is stateless, its whole state is a Spectra struct and a ring of capsule poses needs nothing from Box3D | `Y5`, `N17` | MEDIUM, and the risk is design rather than engine: lag compensation is the feature where being shot behind cover is the *correct* behaviour. Recorded here so nobody later cites it as blocked on the engine choice | M |

### Format consequence to land with `Y3`, not after content exists

Authored-hull collision **inverts** which `.scmap` section the runtime cannot live without. [`docs/formats-and-pipeline.md`](formats-and-pipeline.md):290 currently lists `BRSH` (authored brush source) as **optional** and `CBSP` as a required load. Under this design **`BRSH` becomes mandatory** (physics needs the authored planes) and `CBSP` becomes optional derived data.

And the hulls must be baked as **plane sets, never as native hull blobs** — a baked blob would bind the file format to Box3D's internal ABI version. `.smodel`'s `COLL` section already made exactly this call, before any physics decision existed: *"collision as convex hulls expressed as plane sets is exactly `Brush`'s constructor input"* (`formats-and-pipeline.md`:151). Reuse that representation verbatim for `.scmap`, so there is one collision-hull encoding in the engine and one loader that runs `Brush`'s constructor over it — which also makes a malformed cooked hull a named `ArgumentException` at cook time rather than a physics explosion at runtime.

---

## 7. Decisions that need the user

Each is one sentence of tradeoff. The first four lock something — an ABI, a file format, a public name, a content scale — and cannot be changed cheaply afterwards.

1. **What is one Spectra world unit in metres?** 1 unit = 1 m keeps the solver in its tuned range and makes every ported Roblox constant wrong by a factor the porter must remember; 1 unit = 1 stud = 0.28 m makes porting mechanical and puts a 32-unit chunk at 9 m. *(Answers `ROADMAP.md` §11.13 and the `1 stud` row in `roblox-to-spectra.md`. Roblox cannot simply be copied — it is internally inconsistent by a factor of 5.6.)*
2. **Float or double Box3D build?** Float ships today and costs nothing, since the per-chunk-body design already closes the static world's precision exposure; double is needed only if dynamic bodies must behave far from the origin, and the flag is **ABI-affecting**, so it is decided before the binding is written rather than flipped later.
3. **At script runtime, does `Anchored = false` on a brush convert the node to a dynamic part, or refuse with a message naming the conversion?** Converting is what a Roblox developer expects and costs one incremental recompile at the moment of the call; refusing is predictable and keeps script-triggered recompiles impossible, at the cost of a "why doesn't this work" every new user hits.
4. **`CharacterController` or `Humanoid` for the movement type?** The Roblox name makes ports nearly free and turns a "deliberate difference" row into a "planned" one, but it promises `Health`, `Died`, `Animator` and tools that will not exist — which is the hollow-`Humanoid` outcome the existing ruling rejected.
5. **Which collision source does the mover ship on for `N16`?** Brush hulls make the entire prediction path independent of the physics engine choice; Box3D's mover is better sweep-and-slide quality — but the surface must be frozen before reconciliation is validated against it, or every tuned constant and every reconciliation test is invalidated by the later swap.
6. **Does `sv_physics_clientownership` default on or off?** Off is the only defensible posture for anything competitive (client-owned bodies are unverifiable in principle, by Roblox's own admission about its own system); on is what makes a hobbyist's pushed crate feel good. *(Answer together with `networking.md` §9's "is authoritative movement validation on by default".)*
7. **Milestone prefix `Y` or `G`?** Both are unused and both match the existing single-letter convention; `G` was the review's pick and `Y` is what this document uses — cheap to settle now, expensive once cross-references exist in five other docs.
8. **Is `phys_substeps` a client cvar or a server-replicated handshake value?** As a cvar it is a per-machine knob that changes simulation results, which makes the server and client disagree about what is reachable; as a handshake value beside `sv_tickrate` it is consistent but one more field in a versioned gate.
9. **Does the editor ever step physics in Edit mode?** No is what makes Stop-restore structural and keeps physics out of Team Edit; yes unlocks a genuinely valuable Studio-shaped "drop to floor" verb and changes whether the physics world is owned by the editor host or by the engine.
10. **~~What is the `BrushKind` enum spelled?~~ CLOSED by the user on 2026-08-21. It is `BrushKind { World, Part }`.** The question offered `World | Object` as the collision-avoiding alternative; it is overruled, and the two collisions it was avoiding are answered rather than dodged. **(1) The Roblox-onboarding pillar is an explicit project goal**, and a Roblox developer's model is *"everything I drop in is a Part."* **(2) The `Part`-names-two-representations collision is the DESIRED outcome, not a collision.** `O7`'s `Part` may stay `MeshRenderer`-backed for an imported mesh while a brush-backed part answers to the same word — and that is correct, because **the distinction a user needs is World versus Part, never which C# type backs it**. A vocabulary that forced them to learn the backing type would be leaking an implementation detail into the highest-traffic noun in the editor. `World | Object` avoided the collision by matching nobody's vocabulary, which is the worse trade for a naming lock that bakes into `.smap`, the Properties panel, tutorials and the Luau binding. **The residual wart is real and is recorded rather than argued away:** `BrushKind.World` on a non-`Shared` or `Dormant` node means the **role** *"world geometry"*, not the **World realm** — two senses of one word in adjacent property rows. It is tolerable because the two live on different axes with different vocabularies (`realms.md`'s axis spells its values `Shared`/`Server`/`Client`, never `World`), and because the alternative cost the pillar. A Properties panel should nonetheless label the row *"Brush kind"*, never *"World"*.
11. **Is `CanQuery = false` legal on a part brush while refused on a world brush?** `Y0`'s row says "legal on dynamic parts and entity brushes" and was written before the split existed. Legal-on-parts is consistent (a part's queries never route through the per-cell BSP, which is the whole reason world brushes must refuse it); refusing on both is one fewer asymmetry for a scripter to learn.
12. **Does the dedicated server still need `CBSP` once physics ships?** Dropping it gives a smaller server pack and one fewer resident structure, but it removes one of the artifacts the `.scmap` bake oracle compares — and it must be decided before `D12` ships the format.

---

## 8. What is speculative, unverified, or measured by nobody

**Everything in this document is design.** Nothing was built, run, profiled or measured. The frame placement, seams and API sketches are source reads of this repository on 2026-08-21; the Box3D facts are primary-source fetches on the same date, listed with their URLs in §1.1 so they can be re-checked rather than trusted.

**Verified by this document's own fetches:** repository metadata, licence, release list, the FAQ's rollback and determinism paragraphs, `docs/character.md`'s mover description and experimental banner, `constants.h`'s unit functions and hull/shape limits, issue #134's single maintainer comment, the absence of any `b3World_Save`/`Restore`/`Snapshot`, and the doc comments on `b3Body_SetTransform`, `b3Body_SetTargetTransform` and `b3World_RebuildStaticTree`.

**Quoted from the review pass's header and doc reads, not re-fetched here** (re-check before depending on them): `b3ShapeDef.materials` being ignored for convex shapes; `b3ShapeDef.enableSensorEvents` applying to both sides of a pair; `b3BodyMoveEvent` not being reported for bodies moved by the user; `docs/recording.md`'s snapshot contents and keyframe ring; `docs/large_worlds.md`'s boundary-double design and its "a few percent" cost claim; the measured nupkg payload sizes; and the CI matrix details.

**Genuinely unmeasured, by anyone:**

- **Microseconds per `b3World_CastMover`** against a realistic static hull population. The replay budget in §3.2 is arithmetic over the mover's documented algorithm, not a benchmark. This is the single number that converts it into a budget, and it belongs in `Y1`.
- **Snapshot cost for any candidate.** Jolt documents `SaveState` and a filter but publishes no bytes-per-body or milliseconds-per-snapshot figure; Rapier documents serde with no cost figure; Box3D's keyframe ring exposes a byte budget but not a per-frame size. The ~56 B/body (float) and ~68 B/body (double) figures that circulate for a hand-rolled Box3D body snapshot are **arithmetic over the setter surface, not a measurement**, and they deliberately exclude the cached state the FAQ names.
- **How badly a hand-rolled restore actually diverges.** The FAQ says internal caching is why rollback determinism is absent and says nothing about magnitude. A crate resting on a floor and a five-box stack are different verdicts; only an experiment separates them. This is `Y13`'s gate and it is a one-afternoon job nobody has run.
- **NativeAOT with Box3D.** Not verified by this review for any candidate — only claimed or absent. The one repository claiming CI-verified NativeAOT is a two-week-old single-author project asserting it about itself, and this project has already been burned by a library that published clean and crashed at runtime. `Y1`'s harness is the gate, not a formality.
- **Broadphase behaviour under a 50k-hull open world** with clusters at +8,000 units, and hull-creation cost during an editor drag. The `CsgBench openworld` verdict must still say *world-size independent* after `Y3` — **show it, do not assume it** (`ROADMAP.md` §12).
- **Ghost-collision and tuning quality under a brush-built hull world.** Box3D's own changelog still lists ghost-collision mitigation as in progress. Convex hulls are the better-behaved path than triangle soup, but no external report of Box3D under a CSG or brush world exists.
- **Touch-diff scaling.** Cost is O(movers × local candidates) and nobody has measured it with, say, 200 dynamic bodies and 50 triggers in one chunk.
- **Everything in §2.3a.** The fifteen gate sites, the two-lane counter, the part-mesh cache and the snap step are **source reads plus reasoning, not a build**. The line numbers were re-read on 2026-08-21 against the tree at `0fe3c57` and will drift. Two claims in it are load-bearing and unmeasured in particular: that the per-part draw path costs no more than an equivalent `MeshRenderer` node at `O7` spawn scale, and that the always-drawn part outline is affordable through `DebugDraw` at the same scale. **And note what §2.3a itself records: two of its own claims were false against unmodified code on first writing** (the ungated `Brush` setter, and the bit-identity pin), which is the reason its gate list is exhaustive and per-site rather than summarised.
- **The cost of `net_strictlocalclient` with physics.** Two `Scene`s now imply two physics worlds and two static hull sets on top of two background compiles and two sets of chunk GPU meshes — a line item `networking.md` §4.1 has not priced.

**Open technical questions with cheap answers nobody has fetched:**

- Does `b3CreateHullShape` copy the hull or reference it? The mesh equivalent says explicitly that the mesh is *not* cloned; the hull one is silent. This design keeps the hull alive for the shape's lifetime, which is correct either way, but the answer decides whether the cache can be dropped for single-use brushes. Ten minutes in `src/shape.c`.
- Is Box3D's global allocator thread-safe? The doc says only that it should be set at startup. This design sidesteps it by keeping every native allocation on the render thread — but the stated justification is weaker than it looks, since Box3D allocates during its own multithreaded step and `b3CreateHull` touches no world. Resolve it by reading `src/hull.c`, because a large paste or a first load builds many hulls in one frame and the natural home for that work is the background compile task.
- Why does `windows-arm64` CI build with SIMD disabled when NEON exists and is benchmarked? It matters if win-arm64 ships, and it may mean the arm64 determinism hashes only ever cover the scalar path.
- What does a compound hull's `materialIndex` index, given that shape-level materials are documented as ignored for compound shapes? Both cannot be complete, and it is the one place per-hull surface materials might be reachable.
- How does prefab instancing (`P10`) interact with hull sharing? If it constructs fresh `Brush` objects per instance, the reference-identity hull cache degrades to N identical hulls — and `CsgCompileCache` degrades identically, so this is a pre-existing question physics merely makes more expensive. Answer it once, for both.
