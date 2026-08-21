# Spectra Engine — Physics

> The physics decision: which engine, how it meets a scene graph whose static geometry is already convex, and what "networked physics" means for a project that chose state replication with client prediction rather than deterministic lockstep.
>
> **Companion documents:** [`CLAUDE.md`](../CLAUDE.md) holds the pillars this design must not break; [`ROADMAP.md`](../ROADMAP.md) holds the `F/E/P/S/R/H` arcs; [`docs/networking.md`](networking.md) owns the fixed tick (`N2`), prediction (`N16`) and interpolation (`N17`); [`docs/realms.md`](realms.md) owns admission and liveness; [`docs/formats-and-pipeline.md`](formats-and-pipeline.md) owns `.scmap`/`.smodel`; [`docs/roblox-onboarding.md`](roblox-onboarding.md) owns Luau and Play/Stop; [`docs/roblox-to-spectra.md`](roblox-to-spectra.md) is the concept mapping this arc changes the most rows in.
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

Everything good follows:

- **Physics needs no CSG output.** No dependency on `CsgWorld`, on `ChunkMesh`, or on the *result* of the background compile.
- **A brush whose faces are entirely carved away still contributes its solid.** Under a compiled-surface design that brush vanishes from collision — an invisible-but-solid pillar becoming walkable. Here the question never arises.
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
2. **A per-node "do not carve" flag is refused by [`docs/realms.md`](realms.md).** `realms.md:667` states it directly — *"Do not add a third subtree invariant"* — because admission would stop being a subtree property and become a per-node walk on the CSG snapshot hot path, which `O7` already names as how silent corruption happens.
3. **Removing the brush from the placement list removes it from physics**, because physics consumes that exact list by design.

**The fix uses a mechanism that already exists: a collision-only volume is a `P7` entity-owned brush** with a `func_clip`-shaped classname. `P7` brushes already leave the carve, already compile to entity-local `BrushModel`s, and under this design already become static or kinematic bodies carrying their own hulls. Zero new invariants, zero new geometry code, and the level designer draws it exactly like any other brush.

**A consequence that must be stated rather than discovered:** [`docs/realms.md`](realms.md) R15 requires any node admitted to the static-world carve to be `Shared`, and physics inheriting the placement list inherits that restriction. So **there can be no server-only collision geometry on the brush path** — no server-side clip volume, no geometry solid on the server and absent on the client. Route those through the same entity-brush mechanism, or accept and document the limitation. Related and correct: a `Dormant` brush leaves the placement list and therefore loses collision, which is the right behaviour, and `realms.md`'s `Dormant`/`Active` axis maps cleanly onto `b3Body_Disable`/`b3Body_Enable`.

### 2.4 Physics updates at HARVEST, in the same slot as the mesh swap

Collision syncs inside `Scene.ProcessStaticWorldCompilation`'s success branch — the render-thread harvest where `ReplaceStaticWorld` swaps GPU meshes — not at compile launch.

The competing proposal was to sync at launch, on the argument that collision-behind-render is the *"player falling through the floor of a room they can see"* failure that [`docs/formats-and-pipeline.md`](formats-and-pipeline.md) §4.5 names as the worst kind. That argument does not survive: at harvest, collision is built from the same snapshot that produced the meshes being swapped in, so collision and rendered geometry are **exactly matched**. Both lag the live scene equally, and the player collides with precisely what they see. The failure being feared is a comparison between collision and the *live scene*, which nobody experiences. Harvest also wins on the fault path — when a compile faults and the previous world is kept, harvest-sync leaves collision consistent with what is rendered, while launch-sync would advance collision past it.

Physics inherits the rigidity gate for free: when `SnapshotBrushPlacements` returns null on a non-rigid brush transform there are no placements, so physics skips and the existing loud one-time log covers both subsystems. And physics **never** re-walks the scene graph and never defines its own admission predicate — it consumes the one list, which is what makes it inherit R15, `Dormant` exclusion and `P7`'s future `IsStaticWorldBrush` counter split automatically.

> **Coupling to record now:** when `P7` removes entity-owned brushes from the static placement list, they vanish from physics in the same instant. **The counter split and the kinematic-body path are one change, not two** — otherwise a door stops being solid the moment it becomes a door.

### 2.5 What happens to the BSP and the BVH

After this lands there are **four** spatial structures, not two: `BrushBroadphase` (sort-and-sweep over brush AABBs feeding the carve), `CsgWorld`'s per-cell BSP (`ContainsPoint` at `CsgWorld.cs:603`, `Raycast` at `:617`), `SceneBvh`, and Box3D's broadphase.

- **`SceneBvh` is never redundant.** It indexes *nodes* — including nodes with no collision at all — and serves frustum culling and editor picking, neither of which physics can answer. It does, however, need new work regardless of backend: today it exposes only `Raycast` (`SceneBvh.cs:496`) and `QueryFrustum` (`:585`). There is **no box or sphere overlap query**, so `GetPartBoundsInBox`/`InRadius` is new work.
- **`CsgWorld.Raycast`/`ContainsPoint` are demoted.** Once physics holds the static world, the two answer the same question about the same solid through different code at different tolerances. **Ruling: physics is the sole authority for every query whose answer feeds simulation or gameplay.** `CsgWorld`'s queries are demoted *in their own XML docs* to "the compiled authored static world only — no dynamic bodies, no model colliders, no character", and kept for the editor, for cook-time verification, and as the reference side of an oracle. The demotion argument does not depend on Box3D: the moment one dynamic body exists, a gameplay ray that consults only the static world is already wrong.
- **Pin it with a divergence oracle, and run it in Release.** N seeded rays over a fixture map; `CsgWorld.Raycast` and `b3World_CastRayClosest` under a static-only filter must agree on hit/miss and on distance within a stated tolerance. **Release, not Debug** — `CsgIncrementalCompiler.VerifyTrustedDiff` is already `[Conditional("DEBUG")]` by design, and `realms.md:535` names the consequence of repeating that pattern: *a dev build throws and a shipping build silently compiles corrupt geometry*.

### 2.6 Dynamic bodies on the scene graph

**No fifth `SceneNode` payload.** `O8` already wrote down that the payload count "is now a real cost on the hottest type in the engine and should be a conscious acceptance, not a drift", and adding a reference field for something 99% of nodes never carry is that drift. Instead:

- A packed **`PhysicsFlags` byte** on `SceneNode` (`CanCollide`, `CanQuery`, `CanTouch`, `Anchored`, `Massless`, `HasBody`), fitting existing padding, read as a bit test in the hot path.
- A **side table** in the physics world keyed by `SceneNode.Id` holding the body handle, collision group and material, probed only when `HasBody` is set.
- **A dense int index in Box3D's `userData`** (stored as index+1 so 0 means none). `b3World_GetBodyEvents` returns moved bodies in one contiguous array; draining is one array walk with an integer index per entry — no hash probe, no pinning, no handle table. A `GCHandle` per body would allocate, pin, and make teardown ordering fragile.

The claim that a node→body reference field is needed because "both directions are hot" does not hold. Write-back (native→managed) uses the physics side's own dense array, which already holds the `SceneNode`. The only hot node→body direction is `BuildRenderView` substituting an interpolated matrix, and the `HasBody` bit gates that probe.

**Transform authority is per body kind, and only DYNAMIC bodies are written back.**

| Kind | Authority | Mechanism |
| --- | --- | --- |
| Static | Authored | Placement snapshot only |
| Kinematic (platforms, `P7` brush entities, the character presence body) | The scene | `b3Body_SetTargetTransform(target, fixedDt, wake)` each tick — velocity-based, which is what makes riders and friction behave |
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

`P7` already compiles brush entities in entity-local space into a `BrushModel`, so the same local-space brush set becomes the compound hull set, built once at load and never rebuilt — a door opening stays a matrix write, exactly as `P7` promises for rendering. The silent failure to watch for is a platform whose hulls are *rebuilt* per frame instead of *transformed* per frame: it renders identically and destroys the frame budget, which is `P7`'s own documented hazard wearing a physics costume.

### 3.5 Ownership and its exploit surface

**Automatic network ownership is a slow, hysteretic, server-side policy over the `N13` interest grid** — never a per-tick recompute, never a new spatial structure. Suggested shape: `sv_ownership_rate 2` (Hz, not the sim tick), `sv_ownership_radius 96` (well inside `sv_interestradius`), `sv_ownership_hysteresis 24`, `sv_ownership_mindwell 1.0`, ties broken by `NetId` ascending so the policy carries no wall clock. Transfers move the **whole joint-connected set**, because a mechanism solved by two authorities is not a mechanism.

**The handoff has three parts and skipping any one produces a distinctive bug:**

1. Authority passes **through the server** — `revoke(old)` → server simulates → `grant(new)` — so there is never a window with nobody simulating and never one with two owners simulating.
2. The grant carries **full absolute state**, not a delta. Without it the new owner starts from an interpolated pose 100 ms in the past and the body visibly jumps on every handoff.
3. A **monotone per-`NetId` `OwnershipEpoch`** on the reliable channel, with the server dropping any client-to-server body update whose epoch is not current. Omitting this lets a delayed packet from the *previous* owner rewrite the body after handoff — simultaneously a correctness bug and a trivially weaponisable exploit.

**A client-owned body is a client-authoritative transform**, and gets the mandatory validation envelope [`docs/networking.md`](networking.md) already requires, pinned by the same `(ownership × magnitude × scope)` matrix test: stale-epoch rejection, ownership and interest scope, linear and angular speed clamps, a teleport clamp, and a **swept solidity test against the static hulls with a skin width** — never a bit-exact containment test. (The skin is belt-and-braces here rather than compensation: hulls remove the `SimdPlane` cross-ISA divergence source that motivated it for the CSG path.)

**And the honest line, which belongs in the docs verbatim because Roblox says it about its own system:** *"Roblox cannot verify physics calculations when a client has ownership over a BasePart. Clients can exploit this and send bad data to the server, such as teleporting the BasePart, making it go through walls or fly around."* Client-owned bodies are unverifiable **in principle**, not merely unverified in v1. So `sv_physics_clientownership` defaults to **0** for anything competitive — which means the responsiveness benefit that motivates auto-ownership is unavailable exactly where responsiveness competition is fiercest. Say so, rather than letting a developer discover it. Relatedly, Roblox documents that an owner can fire `Touched` the server never saw, which is why trigger events here are authored **server-side** and never accepted from an owner.

The permanent brush refusal [`docs/networking.md`](networking.md) §4.4 already ships survives untouched and gains a second reason: a brush is a static hull, and a static hull has no owner because it does not simulate.

### 3.6 Two corrections owed to `networking.md`, both cheap now

1. **The purity contract is unsatisfiable as literally written.** §4.4 says `ApplyInput` *"MUST be a pure function of (captured state, cmd, dt)"*, and any mover that queries geometry violates that — and it must query geometry. Amend to **"pure with respect to a pinned world revision"**: `ApplyInput` may issue read-only queries, and the design guarantees the queried world is identical between the original prediction and the replay, which is what the static-world generation counter and the platform pose ring exist for. Do this **before `N16` hardens**, or the first implementation either quietly breaks the stated contract or the double-replay test is written so as to hide it.
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

The engineering reason for refusing a *dynamic brush* is correct and must be preserved: a dynamic body writes a transform every tick, a transform write on a brush node dirties its cells, and a dirty cell launches a background compile — so a single dynamic brush launches a background CSG compile **every tick, forever, while everything still renders correctly**. That is `P7`'s named silent hazard arriving from a new direction, and a code-review convention will not hold once scripts can set the property.

But the conclusion "therefore `Anchored = false` throws" takes away a pillar to protect an implementation detail. `CLAUDE.md`'s open-world pillar says brushes are placed like Roblox *parts*, and [`docs/roblox-to-spectra.md`](roblox-to-spectra.md) sells brush-as-part as the onboarding story. A Roblox developer selecting a wall and unchecking `Anchored` is performing the most basic action in their vocabulary.

**Ruling:** at **edit time**, `Anchored = false` on a brush node is an editor command that swaps the brush node for an `O7` dynamic part — one incremental recompile, one `IEditorCommand`, undoable, and Play/Stop already captures and restores exactly this kind of authored state. At **script runtime** it is either the same conversion with a documented one-recompile cost or a refusal whose message *names the conversion* — that is the sign-off in §7. What must not ship is an unconditional throw with no route forward.

### 4.3 Touch and triggers are one pass with two consumers

`P8`'s `trigger_multiple`/`trigger_once`/`trigger_teleport` and Luau `.Touched` are **two faces of one per-tick overlap diff**: the diff emits `(sensor, visitor, began|ended)` pairs, the entity layer turns them into `OnStartTouch`/`OnEndTouch`/`OnTrigger` outputs, and the script layer turns them into `Touched`/`TouchEnded` signals delivered through `O3`'s deferred queue. A trigger volume is an entity-owned brush with `CanCollide = false, CanTouch = true` — so it costs zero world surfaces, zero recompile and zero new geometry code, and a designer's no-code trigger and a scripter's `.Touched` can never disagree about whether contact happened.

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

Trigger volumes are drawn *always*, not on selection, for the reason `realms.md:700` already establishes for ghosted dormant subtrees: an invisible gameplay volume is indistinguishable from a bug, and this repo has already had exactly that mistake reported as a brush bug (commit `d4701d6`).

### 5.3 Physics never runs in a Team Edit session

Two peers stepping independent solvers drift, and `T9`'s document-digest convergence check would report that drift as a data conflict no user caused. Write this into the `T`-arc's invariants, not only here.

### 5.4 An edit-mode simulation tool is deliberately deferred

"Drop these props onto the floor" is a real level-design verb, and Roblox Studio's lack of it is evidence rather than proof. If it lands it must be an explicit, opt-in `Simulate Selection` tool that steps a **scratch** world and commits one `SetTransformCommand` — undoable, never ambient. It is called out here because retrofitting a scratch world into a design that assumes none exists in Edit mode is the expensive version.

---

## 6. Milestones

**Prefix `Y`**, checked free against `F/E/P/S/R/H` (ROADMAP), `O0`–`O9` (roblox-onboarding), `D0`–`D22` (formats-and-pipeline), `C0`–`C12` (console), `N0`–`N22` and `T0`+ (networking). No existing id is renumbered. *(The review that produced this document proposed `G` on identical single-letter reasoning; the swap is a find-replace confined to this file — see §7.)*

**Where this slots into `ROADMAP.md`:** as a new parallel track — **Arc Y — Physics** — beside Rendering, Shader authoring, Entities and Hosting. **It is not on the critical path and it does not shorten it.** `ROADMAP.md` §4's sequence to a usable editor (`F1, F2, E1–E4, E6, E7, P2, P11a`) is unchanged. `N2`'s fixed tick is the hard prerequisite for anything that steps, and `N16` already ships its one predicted mover with **no physics engine at all**, so the Box3D decision blocks nothing currently scheduled.

| id | milestone | scope | depends on | risk | size |
| --- | --- | --- | --- | --- | --- |
| **Y0** | Query flags and BVH overlap queries | `PhysicsFlags` byte on `SceneNode` with the brush-node `Anchored` conversion path; `QueryParams` with `RespectCanCollide`; **`SceneBvh` gains box and sphere overlap queries** (it has only `Raycast` and `QueryFrustum` today); `Scene.GetPartBoundsInBox`/`InRadius`; `Scene.Raycast` starts honouring `CanQuery`; the 64-group `CollisionGroups` registry. **No native code, no physics engine.** | `F2` | LOW. One sharp edge: `CanQuery = false` on a *static world brush* cannot be honoured by the per-cell BSP (it is derived from the carve, and excluding a brush would change compiled output the determinism oracles compare) — refuse it on world brushes with a named message; it is legal on dynamic parts and entity brushes. `CanCollide = false` on a world brush **is** honoured, for free, because hulls come from placements | S–M |
| **Y1** | Native gate: vendor, build, bind, publish AOT | Vendor Box3D at a **pinned commit** (not a tag — `v0.1.0` is the only one and `main` has moved); per-RID CMake build reusing the vendoring machinery the Luau decision already requires; Spectra's own `[LibraryImport]` layer over the 60–90 entry points actually used, with `DisableRuntimeMarshalling`; struct-ABI dump-and-diff in CI; **decide float vs double here** (ABI-affecting) and model `B3Pos` as distinct from `B3Vec3` regardless; call `b3SetLengthUnitsPerMeter` once. **Deliverable: a throwaway NativeAOT console on win-x64 AND linux-x64 that creates a world, adds a hull, steps 1000 times and prints a state hash — plus a `CastMover` microbenchmark (§3.2)** | nothing | **HARD GATE**, same posture as `N0`/`O0`/`D0` and for the same reason: this project has been burned by a library that published clean under NativeAOT and then crashed the native binary. A failure here changes the dependency and every milestone after it | M |
| **Y2** | `SpectraEngine.Physics` and the `IScenePhysics` seam | Core-owned interface, `SceneManager.PhysicsFactory`, `NullScenePhysics`; world lifecycle; `workerCount = 1` with no task callbacks; the dense `PhysicsBody` arrays behind the int-index `userData`; the refcounted `Brush → hull` cache with explicit render-thread release; `phys_*` cvars coordinated with `C0` | `Y1` | LOW–MEDIUM. Boundary test mirroring `EditingAssemblyBoundaryTests` (no Silk.NET type, no `IWindow`, so `N1` can host it). Real hazard is native lifetime ordering at shutdown — everything released on the render thread before world destruction, no finalizers, plus a 10k create/destroy leak test | M |
| **Y3** | The static world as convex hulls | Per-chunk static bodies at `ChunkCoord.MinCorner`, one hull per **owned** placement in cell-local coordinates, hulls from `Brush.LocalFaces`; **harvest-time sync** in `ProcessStaticWorldCompilation`'s success branch and in `RebuildStaticWorld`'s synchronous path; dirty-cell-only, affected-shape-only rebuild; `phys_draw_hulls` through `DebugDraw` | `Y2` | MEDIUM, and verification **is** the milestone: hull count equals placement count after every edit sequence including undo/redo; a `CsgBench`-style scenario showing a one-brush edit's collision cost is flat at 1k/10k/50k parts with half at +8,000 units; `StaticWorldCompileCount` unchanged by physics existing; origin-invariance of local hull geometry. Sharpest edge is the refcount when one `Brush` backs many placements — a leak or double-free there is silent until it is an access violation | L |
| **Y4** | Query unification and the divergence oracle | One gameplay-facing `Raycast`/`OverlapAABB`/`ShapeCast` on the seam, returning `SceneNode` plus the engine-resolved per-face `MaterialRef`; demote `CsgWorld.Raycast`/`ContainsPoint` in their own XML docs; **Release-configuration** divergence oracle; audit and reroute every gameplay-shaped caller | `Y3` | MEDIUM. This is where the four-spatial-structures hazard is closed or shipped. The oracle must be Release — repeating `VerifyTrustedDiff`'s DEBUG-only shape means divergence is only ever caught by developers. Second hazard: if `N16` ships before `Y4`, the reroute is a behaviour change to a validated, prediction-critical path | M |
| **Y5** | Character mover — the one `IPredictedMover` | `ICharacterCollisionSource` plus the brush-hull implementation; sweep-and-slide, step probe, ground snap, slope limit, air control, jump, `CharacterState`; `CaptureState`/`RestoreState` as a struct copy; the kinematic **presence body**; `PredictionContext` replacing the bare `dt`. **This is `N16`'s mover** | `Y0`, `N2` (hard), `Y3`/`Y4` for the backend source | **HIGH — and the risk is feel, not code** (§4.4). The double-replay purity test must be written **first**, and run **with a moving platform in the scene**. Box3D's mover carries an experimental banner upstream; the seam plus a dual-source agreement test is the mitigation | L |
| **Y6** | Dynamic bodies, transform authority, render interpolation | Body creation from primitives and from `.smodel` `COLL` hulls; mass, materials, velocities, impulses, sleep and wake; dynamic-only write-back; `PosePrev`/`PoseCurr` and the `BuildRenderView` substitution driven by `O8`'s `PreRender(alpha)`; `SetPhysicsTransformCommand` for gizmo drags during Play | `Y4`, `O7`, `N2`, `O8` | MEDIUM–HIGH. Three quiet hazards: writing kinematic move events back injects accumulating solver drift into authored transforms with no failing test; the render-pose overlay must never reach a transform setter; and **every awake body writes a transform every tick**, which is `O3`'s fan-out storm and `N13`'s interest churn arriving together — the has-subscribers gate and cell hysteresis must exist *before* this lands. Ruling `R‑9` applies: not concurrent with `E4`/`E6`/`P7` | L |
| **Y7** | Kinematic brush entities, moving platforms, clip volumes | `P7` brush entities as kinematic bodies carrying entity-local hulls, driven per tick by target transforms; the `func_clip` collision-only volume (§2.3); platform pose ring shared with `N17`; the pose-plus-velocity and closed-form replication paths | `P7`, `Y6` | **HIGH**, and the reason is a coupling nobody had written down: the instant `P7` removes entity-owned brushes from the static placement list they vanish from physics — a door that becomes a door stops being solid. **The counter split and the kinematic path are one change.** Second hazard: hulls rebuilt per frame instead of transformed per frame render identically and destroy the frame budget | L |
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
10. **Does the dedicated server still need `CBSP` once physics ships?** Dropping it gives a smaller server pack and one fewer resident structure, but it removes one of the artifacts the `.scmap` bake oracle compares — and it must be decided before `D12` ships the format.

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
- **The cost of `net_strictlocalclient` with physics.** Two `Scene`s now imply two physics worlds and two static hull sets on top of two background compiles and two sets of chunk GPU meshes — a line item `networking.md` §4.1 has not priced.

**Open technical questions with cheap answers nobody has fetched:**

- Does `b3CreateHullShape` copy the hull or reference it? The mesh equivalent says explicitly that the mesh is *not* cloned; the hull one is silent. This design keeps the hull alive for the shape's lifetime, which is correct either way, but the answer decides whether the cache can be dropped for single-use brushes. Ten minutes in `src/shape.c`.
- Is Box3D's global allocator thread-safe? The doc says only that it should be set at startup. This design sidesteps it by keeping every native allocation on the render thread — but the stated justification is weaker than it looks, since Box3D allocates during its own multithreaded step and `b3CreateHull` touches no world. Resolve it by reading `src/hull.c`, because a large paste or a first load builds many hulls in one frame and the natural home for that work is the background compile task.
- Why does `windows-arm64` CI build with SIMD disabled when NEON exists and is benchmarked? It matters if win-arm64 ships, and it may mean the arm64 determinism hashes only ever cover the scalar path.
- What does a compound hull's `materialIndex` index, given that shape-level materials are documented as ignored for compound shapes? Both cannot be complete, and it is the one place per-hull surface materials might be reachable.
- How does prefab instancing (`P10`) interact with hull sharing? If it constructs fresh `Brush` objects per instance, the reference-identity hull cache degrades to N identical hulls — and `CsgCompileCache` degrades identically, so this is a pre-existing question physics merely makes more expensive. Answer it once, for both.
