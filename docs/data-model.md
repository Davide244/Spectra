# The Spectra data model

> **How to read this page.** Every row marked **EXISTS** carries a `file:line` citation and was read out of the tree on 2026-08-21. Every row marked **PLANNED** names the document that designs it and exists **nowhere in the code**. There is no third category: if it is not cited, it is not built.
>
> Line numbers are for the branch `VSIXTest` at the time of writing. Citations point at declarations, not at every use.

---

## 1. Orientation: three ideas

Everything below follows from three decisions. They are worth reading first, because most of the model's oddities are consequences of them rather than choices in their own right.

### The scene graph is the single spine. There is no ECS.

One concrete class — `SceneNode` (`SpectraEngine.Core/Scene/SceneNode.cs:14`) — holds a name, a local transform, a parent and children, and *optional payload fields*. There is no component registry, no archetype table, no node subclassing, no polymorphic node types. World geometry is not a parallel system: a brush is a field on a node (`SceneNode.cs:116`).

**Consequence:** every new capability is a nullable field on `SceneNode` or it does not exist. That is cheap to reason about and cheap to traverse, and it makes the payload count a real, watched cost on the hottest type in the engine — two fields today, and each addition is a deliberate act.

### Derived data is never authored.

Brushes are the authored truth for static world geometry. The carved surfaces, the per-cell BSP trees, the chunk meshes and the GPU buffers are all *recomputed* from brush placements (`Scene.cs:335`, documented as a build artifact). Nothing writes into them; you change the world by editing brush nodes, and the engine notices.

**Consequence:** derived state may be discarded at any point without losing information, and the whole compile can run on a background thread reading nothing but an immutable snapshot (`Scene.cs:1011`). It also means the save format may contain no derived data at all — a rule `docs/formats-and-pipeline.md:213` carries into `.smap`.

### Identity is a `Guid`, everywhere.

`SceneNode.Id` (`SceneNode.cs:60`) is assigned at construction and never changes — not on rename, not on reparent, not on moving between scenes. A second constructor (`SceneNode.cs:44`) exists solely so a node can be *recreated under its old id*.

**Consequence:** references survive destruction. Undo of a delete rebuilds the node with the same id, so editor commands address nodes by `Guid` rather than by object reference (`SpectraEngine.Editing/Commands/IEditorCommand.cs:11`), and `Scene.TryFindById` (`Scene.cs:186`) is the resolver. The same property is what the planned save format, collaborative editing and entity wiring all lean on.

---

## 2. The spine as it is today

### 2.1 Identity

| Member | Where | Notes |
| --- | --- | --- |
| `SceneNode.Id` — `Guid` | `SceneNode.cs:60` | Immutable. Stable across rename, reparent, scene change. |
| `new SceneNode(name)` | `SceneNode.cs:30` | Mints a fresh `Guid.NewGuid()`. |
| `new SceneNode(name, id)` | `SceneNode.cs:44` | Re-uses an identity — the undo-of-delete and deserialization door. |
| `SceneNode.Name` — `string` | `SceneNode.cs:62` | Mutable, not unique, not an identity. |
| `Scene.TryFindById` | `Scene.cs:186` | O(1), allocation-free. Backed by `_nodesById` (`Scene.cs:176`), maintained from the membership events. |
| `Scene.NodeCount` | `Scene.cs:193` | The size of that index — i.e. the graph's node count, root included. |

**Non-obvious:** the id index is written with the indexer, not `Add` (`Scene.cs:171`), because it runs inside the ownership walk where a throw would leave the graph half-owned. If two live nodes ever shared an id, the most recently added wins rather than crashing an edit. De-indexing is identity-checked (`Scene.cs:143`) so a stale duplicate cannot unmap the live node.

### 2.2 Hierarchy

| Member | Where |
| --- | --- |
| `Parent` (read-only) | `SceneNode.cs:64` |
| `Children` — `IReadOnlyList<SceneNode>` | `SceneNode.cs:75` |
| `AddChild` / `CreateChild` / `RemoveChild` | `SceneNode.cs:247` / `:287` / `:293` |
| `Traverse()` — pre-order, explicit stack | `SceneNode.cs:313` |
| `Scene.Root` | `Scene.cs:60` |
| `Scene.Nodes` — `Root.Traverse()` | `Scene.cs:1392` |
| `SceneNode.Owner` — owning `Scene`, internal | `SceneNode.cs:73` |

**Non-obvious invariants:**

- **Sibling order is load-bearing.** Traversal order drives `BrushPlacement` order, which drives carve order, which the determinism oracles pin.
- **`Owner` propagates per subtree, not per node.** `SetOwner` (`SceneNode.cs:343`) early-outs when the owner is unchanged, which is exactly why a reparent *within* one scene raises no membership events — the nodes never left.
- **`AddChild` invalidates cached world matrices *before* announcing the node** (`SceneNode.cs:265`), because `NodeAdded` handlers read `WorldMatrix`.
- **There is no cycle guard.** `AddChild` does not check ancestry; a parent loop recurses to stack exhaustion in `MarkWorldDirty`.

### 2.3 Transform

| Member | Where |
| --- | --- |
| `Transform` struct — `Position`, `Rotation`, `Scale` | `SpectraEngine.Core/Scene/Transform.cs:5` |
| `Transform.Model` — S·R·T | `Transform.cs:20` |
| `LocalTransform` | `SceneNode.cs:155` |
| `LocalPosition` / `LocalRotation` / `LocalScale` | `SceneNode.cs:171` / `:183` / `:195` |
| `WorldMatrix` (cached) | `SceneNode.cs:225` |
| `WorldPosition` | `SceneNode.cs:239` |

**Non-obvious invariants:**

- **Every transform setter early-outs on exact equality** (`SceneNode.cs:162`, `:176`, `:184`, `:196`). A no-op write invalidates nothing, dirties no static world, and raises no `NodeTransformChanged`. This is what makes absolute-value commands free to replay.
- **`WorldMatrix` is cached and lazily recomputed** (`SceneNode.cs:229`): `local * Parent.WorldMatrix`. The dirty flag is propagated *eagerly* down the whole subtree by `MarkWorldDirty` (`SceneNode.cs:390`) — cheap for shallow trees, revisit for deep ones.
- **Brush placements must be rigid.** `Scene.DescribeNonRigidDefect` (`Scene.cs:1409`) rejects a snapshot whose brush node has non-finite elements, projective components, scale, shear, or a reflection, at tolerance `1e-4f` (`Scene.cs:1398`). Rigidity is enforced at the snapshot — the single point where node transforms enter the brush pipeline — not trusted.
- **Rigidity is a *subtree* property.** A scale written anywhere above a brush makes that brush's placement non-rigid and rejects the whole snapshot (`SceneNode.cs:212`). A tool about to write `LocalScale` must consult `SubtreeBrushCount`, not `node.Brush is not null`.

### 2.4 Payload bookkeeping

| Member | Where |
| --- | --- |
| `SubtreeBrushCount` | `SceneNode.cs:222` |
| `AdjustSubtreeBrushCount` (private) | `SceneNode.cs:361` |

Maintained incrementally on the **whole ancestor chain** by the `Brush` setter and by reparenting, so reading it is O(1). Its job: a transform edit can decide in O(1) whether it affects the static world at all (`SceneNode.cs:379`). Moving a camera or a brushless prop must not launch a recompile; moving a group node with brush descendants must.

### 2.5 Events

| Event | Where |
| --- | --- |
| `Scene.NodeAdded` | `Scene.cs:98` |
| `Scene.NodeRemoved` | `Scene.cs:108` |
| `Scene.NodeTransformChanged` | `Scene.cs:118` |

**Non-obvious contract (`Scene.cs:85`): handlers must not mutate the graph.** Membership events fire *in the middle of* the ownership walk, and a structural edit there corrupts the traversal. Observe and record; defer structural edits until after the event returns. This is a stated contract, not an enforced guard.

Further rules: a subtree attach raises `NodeAdded` once per node, pre-order; a cross-scene move raises `NodeRemoved` on the source and `NodeAdded` on the destination, per node; a reparent within one scene raises **nothing**; `Owner` is already repointed when a handler runs; and `NodeTransformChanged` fires for every owned node, brush-bearing or not.

Two internal hooks cover what the public events do not: `OnNodeSpatialComponentChanged` (`Scene.cs:155`) for payload edits on an already-owned node, and `OnNodeSubtreeMoved` (`Scene.cs:157`) for in-scene reparents.

### 2.6 Spatial queries

| Member | Where |
| --- | --- |
| `Scene.Raycast` → `SceneRaycastHit` | `Scene.cs:208`; hit type at `SceneBvh.cs:30` |
| `Scene.QueryFrustum` | `Scene.cs:219` |
| `Scene.TryGetWorldBounds` | `Scene.cs:235` |
| `Scene.BuildRenderView` | `Scene.cs:269` |
| `SceneBvh` (internal dynamic AABB tree) | `SceneBvh.cs:60` |
| `SceneBvh.IsSpatial` | `SceneBvh.cs:146` |
| `Ray3` (unit-direction contract) | `Ray3.cs:16` |
| `Frustum` (six planes) | `Frustum.cs:16` |
| `Aabb` | `Bsp/Aabb.cs:8` |
| `Camera` | `Camera.cs:14`; `ScreenPointToRay` `:213`, `ScreenRectToFrustum` `:263`, `GetFrustum` `:203` |

**Non-obvious:** a node is *spatial* — i.e. tracked by the BVH — iff it carries a `MeshRenderer` **or** a `Brush` (`SceneBvh.cs:146`). That test is ancestry-blind: there is no enable flag anywhere on this path today. `QueryFrustum` does not clear the results list; `Frustum.Intersects` (`Frustum.cs:76`) is conservative (false positives possible, false negatives not).

### 2.7 Selection

`SelectionSet` (`SelectionSet.cs:20`) is scene-owned (`Scene.cs:68`), created before the root is claimed so it can subscribe to `NodeRemoved` for auto-deselection (`SelectionSet.cs:49`, `:263`). Surface: `Items` (`:58`), `Count` (`:61`), `SelectionChanged` (`:70`), `Contains` (`:73`), `Select`/`Add`/`Toggle`/`Deselect` (`:80`/`:98`/`:114`/`:132`), the batched `Apply` and its `SetRange`/`AddRange`/`ToggleRange` wrappers (`:185`, `:158`, `:166`, `:173`), and `Clear` (`:253`).

### 2.8 Threading

**Every member of `Scene`, `SceneNode`, `SelectionSet`, `SceneBvh` and `RenderView` is render-thread-only** (`Scene.cs:35`). The compile-state fields are deliberately unsynchronized (`Scene.cs:428`) because scene edits are single-threaded. The only work that leaves the render thread is the background CSG compile, and it reads nothing but its immutable snapshot; `Task.Run`/task completion provide the happens-before edges.

---

## 3. The payload model

A **payload** is an optional, nullable field on `SceneNode` that gives the node a job beyond being a named transform. It is composition, not subclassing: there is one node class and no `IPayload` interface. Assigning a payload notifies the owning scene through the internal hooks, which is how the BVH and the static world stay in step.

**Two payloads exist today.**

### `MeshRenderer` — `SceneNode.cs:82`

`MeshRenderer` (`Scene/MeshRenderer.cs:10`) is a `Mesh` + `Material` pair, both read-only after construction (`:18`, `:20`). GPU lifetime belongs to the renderer that created the mesh, never to the component. `Mesh` (`Graphics/Mesh.cs:14`) also keeps a CPU-side copy — `Positions` (`:19`), `Normals` (`:22`), `Indices` (`:25`), `LocalBounds` (`:28`) — so debug drawing and raycasts need no GPU round trip. `Material` (`Graphics/Material.cs:47`) holds an optional `ShaderProgram` plus typed parameter and texture-slot dictionaries.

The setter is reference-compared and, on change, calls `Owner?.OnNodeSpatialComponentChanged` (`SceneNode.cs:92`): the node just became spatial, stopped being spatial, or changed its bounds.

### `Brush` — `SceneNode.cs:116`

**A brush is not a mesh. It is a convex solid defined as an intersection of half-spaces** — one outward-facing plane per face (`Bsp/Brush.cs:20`). Planes and clipped face polygons live in the brush's *local* frame; the node's world transform places it.

Why half-spaces rather than triangles:

1. **CSG is exact and cheap on planes.** Carving one solid out of another is plane classification and polygon splitting (`Polygon.Classify` `Polygon.cs:94`, `Polygon.Split` `:153`), not mesh boolean surgery.
2. **Precision is position-independent.** Splits run in each brush's own local frame, so a brush 10 km from the origin has the same floating-point accuracy as one at the origin (`Brush.cs:13`). This is the numerical half of the open-world pillar.
3. **Resize is a plane edit, not a node scale.** `Brush.WithScaledExtents` (`Brush.cs:308`) maps each half-space exactly under a diagonal map — `n' = normalize(S⁻¹n)`, `d' = d / ‖S⁻¹n‖` — so wedges and cut corners come through correctly, and node transforms stay rigid.
4. **The solid is validated at construction.** Duplicate same-facing planes are rejected (`Brush.cs:350`), and a two-seed probe rejects unbounded plane sets (`Brush.cs:372`).

Brush surface: `Transform` (`:128`, for standalone use only — node-attached brushes ignore it), `LocalPlanes` (`:131`), `LocalFaces` (`:140`), `FaceSurfaces` (`:148`), `LocalBounds` (`:151`), `WorldBounds` (`:154`), `CreateBox` (`:170`, `:184`), `WithFaceMaterial` (`:231`), `WithFaceSurface` (`:246`).

**A `Brush` is immutable after construction.** Every mutator returns a new instance, and that is load-bearing: `Brush` reference identity is the carve cache's validity key (`Brush.cs:219`), so swapping the successor onto the node is precisely what invalidates that brush's cached carve — and only that brush's.

**The brush setter does three things** (`SceneNode.cs:119`): it maintains `SubtreeBrushCount` on the ancestor chain when a brush is attached or detached; it dirties the static world — conservatively via `MarkStaticWorldDirty` for attach/detach, since the placement count changes and every later slot shifts, or node-scoped via `MarkBrushSubtreeDirty` for a brush-for-brush swap; and it notifies the spatial index.

### `FaceSurface` — the per-face payload

Each brush plane carries a `FaceSurface` (`Bsp/FaceSurface.cs:86`), index-aligned with `LocalPlanes`: a `MaterialRef` (`:136`) plus Hammer-style texture axes — `UAxis`/`VAxis` (`:142`, `:148`), `UOffset`/`VOffset` (`:151`, `:154`), `UScale`/`VScale` (`:157`, `:160`).

The UV convention (`FaceSurface.cs:22`) is `u = dot(p, UAxis)/UScale + UOffset`. Scales are **world units per repeat**; offsets are in **repeats**, added after the division; rotation is not stored separately — it is expressed by rotating the axes about the face normal, so there is no "rotate then scale" ambiguity.

**Zero axes mean world-aligned** (`IsWorldAligned` `:168`): the axes are derived at UV time from the face's world normal by the dominant-axis rule (`ResolveAxes` `:239`). That is Hammer's default and is bit-identical to the projection the engine used before per-face surfaces existed, so no existing geometry's UVs moved. Explicit axes get full **texture lock** through `Transformed` (`:206`): the axes rotate with the brush and the translation folds into the offsets, so a dragged brush keeps its texture glued to the surface.

What this buys: per-face materials with no per-face asset object anywhere. **The payload is a pure value** — floats and an interned id, no GPU handle, no asset-manager state — which is exactly what lets it ride through the entire background compile and become a real material only at upload time.

### `MaterialRef` — the interning seam

`MaterialRef` (`Assets/MaterialRef.cs:26`) is a 4-byte id (`:38`); id 0 is the engine default (`:29`, `:41`). `MaterialRegistry` (`:82`) is a process-wide, **append-only**, lock-guarded intern table: `Intern(path)` (`:110`) and `TryGetPath` (`:134`), keyed case-insensitively with backslashes folded. Ids are never reused or revoked, which is what makes one safe to hold inside an immutable CSG artifact for the life of the process. Resolution to a real `Material` happens once, on the render thread, through `AssetManager.ResolveMaterial` (`Assets/AssetManager.cs:574`), which degrades to `DefaultMaterial` (`:220`) rather than throwing.

---

## 4. Derived data: brushes to pixels

Authored truth is exactly this: the set of brush nodes, their brushes, and their nodes' world transforms. **Everything in this section is recomputed and disposable.**

### 4.1 The snapshot

`Scene.SnapshotBrushPlacements` (`Scene.cs:1264`) captures one `BrushPlacement` (`Bsp/BrushPlacement.cs:20`) per brush node — the brush reference plus the node's world matrix at that instant. Two paths: a **fast path** (`Scene.cs:1294`) that re-visits only nodes which reported an edit and patches their slots by paged copy-on-write, costing O(edit neighbourhood); and a **full walk** (`Scene.cs:1345`) for the first snapshot, structural edits and external dirtying, costing O(world). Rigidity is validated here (`Scene.cs:1409`); a defective snapshot is rejected whole and logged once (`Scene.cs:948`) rather than per frame.

### 4.2 The compile chain

Run entirely on a thread-pool thread by `Scene.CompileStaticWorld` (`Scene.cs:1011`), reading only the snapshot and the immutable previous world:

| Stage | What it produces | Where |
| --- | --- | --- |
| **Carve** | Per-brush visible exterior surfaces — each brush's faces minus every overlapping brush's volume | `Csg.CarvePerBrush`, called at `CsgWorld.cs:467` |
| **Bucket** | The sparse chunk partition: each placement into the cells its weld-band-inflated AABB touches | `ChunkGrid.Build` (`ChunkGrid.cs:167`), called at `CsgWorld.cs:527` |
| **Snap + weld** | Per-cell vertex snapping to a grid and T-junction welding | `ChunkWelder.Weld`, `CsgWorld.cs:534` |
| **Per-cell BSP** | One solid-leaf tree per cell over its residents' welded surfaces | `ChunkBspBuilder.Build`, `CsgWorld.cs:544` |
| **Per-cell mesh** | One `ChunkMesh` per geometry-owning cell, split into one `ChunkSubmesh` per face material | `ChunkMeshBuilder.Build`, `CsgWorld.cs:553` |
| **GPU upload** | One `Mesh` per (chunk, material), materials resolved here | `Scene.CreateChunkSubmeshes` (`Scene.cs:1192`) — **render thread only** |

### 4.3 The compiled artifacts

| Type | Where | Holds |
| --- | --- | --- |
| `CsgWorld` | `Bsp/CsgWorld.cs:16` | `Placements` (`:138`), `Surfaces` (`:148`, lazily materialized), `SurfaceCount` (`:162`), `Chunks` (`:180`), `ChunkMeshes` (`:191`), `DirtyCells` (`:212`) |
| `ChunkCoord` | `Bsp/ChunkCoord.cs:14` | Unbounded integer cell, `CellSize` pinned at 32 (`:25`), lexicographic X→Y→Z canonical order (`:51`) |
| `ChunkGrid` | `Bsp/ChunkGrid.cs:23` | Dictionary-keyed sparse cells; `WeldBand` (`:37`), `OwnerCell` (`:131`), `ComputeFootprint` (`:141`) |
| `WorldChunk` | `Bsp/WorldChunk.cs:30` | `OwnedBrushIndices` (`:47`), `ResidentBrushIndices` (`:54`), `Surfaces` (`:61`), `WeldedSurfaces` (`:72`), `Bsp` (`:85`) |
| `BspTree` / `BspNode` | `Bsp/BspTree.cs:14` / `BspNode.cs:10` | Solid-leaf tree; `IsLeaf`/`IsSolid`/`Plane`/`Front`/`Back` (`BspNode.cs:26`–`:38`) |
| `ChunkMesh` | `Bsp/ChunkMesh.cs:75` | `Coord` (`:85`), `Submeshes` (`:95`), `RenderBounds` (`:107`) |
| `ChunkSubmesh` | `Bsp/ChunkMesh.cs:39` | `MaterialRef` + self-contained zero-based vertex/index arrays |
| `Polygon` | `Bsp/Polygon.cs:13` | Vertices, `Surface` plane (`:77`), `Face` payload (`:85`), `Bounds` (`:91`), `Epsilon = 1e-4f` (`:16`) |

**The BSP is a query structure only** (`CsgWorld.ContainsPoint` `:603`, `Raycast` `:617`) — never the render path. Point queries route to the containing cell; rays walk occupied cells front-to-back by 3D-DDA.

### 4.4 What is a cache and can be thrown away

All of it. Specifically:

- `Scene.StaticWorld` (`Scene.cs:335`) is null until the first compile lands, and null when the scene has no brush nodes.
- The GPU side — `_staticWorldChunkMeshes` / `_staticWorldChunkList` (`Scene.cs:343`), exposed as `StaticWorldChunkMeshes` (`:355`) — is rebuilt or spliced per swap in `ReplaceStaticWorld` (`:1041`) and `ApplyChunkMeshDelta` (`:1127`), create-before-destroy so a `CreateMesh` throw leaves the last good world intact and renderable.
- The four validation caches — `CompileCache` (`CsgWorld.cs:224`), `WeldCache` (`:254`), `BspCache` (`:301`), `MeshCache` (`:338`) — are pure accelerators. Dropping them costs a full recompile, never correctness. The synchronous `RebuildStaticWorld` (`Scene.cs:759`) is deliberately cache-free and always compiles fresh.
- `CsgWorldCarry` (`CsgWorld.cs:108`) is the incremental compiler's paged copy-on-write input; a faulted compile restores it from the published world (`Scene.cs:866`) and re-covers the dropped dirty cells.
- `_lastSnapshotFootprints` (`Scene.cs:495`) and `_pendingDirtyCells` (`:500`) are the dirty-cell tracker. The dirtying rule: an unchanged brush reference *and* placement matrix dirties nothing; any change dirties the **union of old and new footprints**, because a brush leaving a cell strands stale geometry there.

**Authored vs derived, stated once:** brush nodes and their transforms are authored. `CsgWorld`, `ChunkGrid`, `WorldChunk`, `BspTree`, `ChunkMesh`, every GPU buffer, the BVH and the `RenderView` are derived.

### 4.5 The frame view

`RenderView` (`Graphics/RenderView.cs:39`) is the per-frame draw list: `Items` (`:48`) for mesh nodes and `WorldItems` (`:60`) for static-world chunks, each entry a `RenderItem` (`:15`) of mesh + optional material + world matrix. It is engine-owned and reused; `Clear` (`:99`) keeps capacity so steady-state builds allocate nothing. World chunk vertices are already in world space, hence the identity matrix. Culling is **per chunk** against the chunk's true render AABB (`Scene.cs:309`) — owned surfaces overhang their cell — and a surviving chunk emits one item per material it wears.

---

## 5. Editing: how changes are expressed

| Concept | Where | Notes |
| --- | --- | --- |
| `IEditorCommand` | `Editing/Commands/IEditorCommand.cs:31` | `Name`, `Do(Scene)`, `Undo(Scene)`, `RollBack(Scene)` |
| `ICoalescingCommand` | `Commands/ICoalescingCommand.cs:21` | `TryAbsorb` — a whole drag collapses to one entry |
| `SetTransformCommand` | `Commands/SetTransformCommand.cs:25` | Absolute before/after position + rotation |
| `SetLocalTransformCommand` | `Commands/SetLocalTransformCommand.cs:18` | Absolute before/after full `Transform` |
| `SetBrushCommand` | `Commands/SetBrushCommand.cs:32` | Brush swap — retexture and resize ride this |
| `CompositeCommand` | `Commands/CompositeCommand.cs:18` | Undo runs children in reverse |
| `UndoStack` | `Undo/UndoStack.cs:41` | Bounded ring (`:51`), default 256 (`:44`); transactions at `:184`/`:205`/`:242` |

Two rules make this work, both stated at `IEditorCommand.cs:11` and `:19`:

- **Address by `Guid`, never by reference**, and treat a lookup miss as a no-op rather than an error — a node may legitimately be absent while a stretch of history sits undone.
- **Capture absolute before/after values, never deltas or scene snapshots.** Absolute values are idempotent, which is concretely free here: the transform setters early-out on exact equality and the CSG caches key on exact values, so replaying a value the scene already has costs a comparison and dirties nothing. Deltas would drift under coalescing; snapshots would make every edit O(scene).

Every command's `NodeId` is a `Guid` (`SetTransformCommand.cs:87`, `SetLocalTransformCommand.cs:44`, `SetBrushCommand.cs:57`).

---

## 6. What the designs add — all PLANNED, none in the tree

Nothing in this section exists in the code. Each row names the document that owns it.

| Concept | What it is | Why | Owner |
| --- | --- | --- | --- |
| `NodeRealm` — `Inherit \| Shared \| Server \| Client` | A declared, inherited byte on `SceneNode` saying **who holds this node**. | Deletes Roblox's container zoo: audience becomes data on the node, so the tree can be organised around the game instead of around replication. **Name is LOCKED to `Realm`.** | `docs/realms.md` §2.2 |
| `NodeState` — `Inherit \| Active \| Dormant` | Whether the node is **live**: carved, in the BVH, drawn, queried, ticking. | There is no way to make a brush inert today — `SnapshotFullWalk` admits every brush unconditionally (`Scene.cs:1349`) and `IsSpatial` is ancestry-blind (`SceneBvh.cs:146`) — so a parked template would carve the world. Ship both axes or neither. | `docs/realms.md` §2.1–2.2 |
| `RealmSet` — `[Flags] Inert \| Server \| Client \| Shared` | The **resolved** value, and it is a *set*, not an enum value. | Resolution is an intersection: `ToSet(declared) & parentEffective`. `Server ∩ Client` is empty, and the empty set has a name — `Inert` — rather than being promoted to the ancestor's realm, which would turn a dragged client HUD subtree into server code holding authority. | `docs/realms.md` §2.3, §3 R1–R3 |
| `SceneNode.EffectiveRealm` / `IsLive` / `IsVisibleTo(mask)` | Cached resolved fields, maintained on declaration change and reparent only — never on the transform path. | `IsVisibleTo` is one AND and one compare: the entire enforcement primitive. | `docs/realms.md` §2.4 |
| `Script` payload | A **node** with a `Script` payload — so `script.Parent` is literally the parent node — plus `ScriptKind { Server, Client, Module }` and `Disabled`. | Matches Roblox character-for-character, and falls out free: a script-only node carries no `MeshRenderer` or `Brush`, so the BVH and static world ignore it automatically. | `docs/roblox-onboarding.md` O8 |
| `Entity` payload | Source-style entity base: `OnSpawn`/`OnActivate`/`OnRemove`, `AcceptInput`, `FireOutput`, connection tuples, `EntityWorld`, `EntityCatalog`. **`targetname` IS `SceneNode.Name`** — one identity, one field. | The no-code gameplay core. Tick runs on the render thread after `SceneManager.Update` and before `ProcessStaticWorldCompilation`, so an entity that moves a brush gets its cells dirtied the same frame. | `ROADMAP.md` P4–P9 |
| Attributes | A closed `AttributeValue` readonly-struct union (Bool, Int, Double, String, Vector2, Vector3, Color3, CFrame, NumberRange…) **plus a `NodeRef` wrapping the node's `Guid`**, in a lazily allocated per-node dictionary. | **One** keyvalue mechanism serves both Roblox attributes and Source entity keyvalues — they are the same feature. `NodeRef` is the addition Roblox lacks and the entity wiring is unusable without it. | `docs/roblox-onboarding.md` O3 |
| Tags | Per-node lazy tag list, a `Scene` reverse index, `GetTagged`, `TagAdded`/`TagRemoved`, and `ObserveTag` which **replays already-tagged nodes before connecting**. | The `CollectionService` equivalent, minus the `GetTagged`-loop-beside-every-connect boilerplate. The reverse index must take an audience mask or it enumerates content the caller cannot see. | `docs/roblox-onboarding.md` O3; `docs/roblox-to-spectra.md:92` |
| `NetId` — packed `(index, generation)` `uint` | A **second, session-scoped** identity for gameplay replication. 20 index bits, generation above. | 16-byte Guids in every gameplay packet cost 64 KB/s per client in identifiers alone at 200 objects / 20 Hz. Authored nodes derive their index from the `.scmap` pre-order position, so both sides agree with zero spawn traffic. | `docs/networking.md` §3.3 |
| Physics body payload | **Not designed anywhere.** | There is no physics engine and no document owns a body payload; rigid-body physics, rollback, proximity ownership and lag compensation are explicitly deferred (`networking.md:556`, `roblox-onboarding.md:69`). What *is* reserved is the per-instance state history ring (`networking.md:556`), because interpolation needs it and retrofitting it changes every instance's layout. | — (deferral only) |

Also planned and adjacent to the model, named so they are not mistaken for existing: `.smap` authored JSON with zero derived data (`formats-and-pipeline.md` §2.6), `.scmap` baked binary with fixed 80-byte `NODE` records (`§2.7`), prefab `PrefabInstance` payload (`ROADMAP.md` P10), `Light` payload and a typed `Scene.Environment` (`roblox-to-spectra.md:33`), and cvars as the settings surface (`docs/console.md` §5).

---

## 7. Identity and references

**One identity today, two in the design.**

### `Guid` is the universal reference

`SceneNode.Id` is the only identity the engine has, and everything that must survive time uses it.

**Why commands address by `Guid` rather than by object reference.** Undo of a delete cannot resurrect the original object — it constructs a new `SceneNode` under the old id (`SceneNode.cs:44`). Any object reference a command captured would point at a corpse. So commands store the id and resolve through `Scene.TryFindById` at execution time, and a miss is a no-op because a node may legitimately be absent while history sits undone.

**How references survive undo.** The id outlives the instance; `_nodesById` is rebuilt from `NodeAdded`/`NodeRemoved`, so a resurrected node re-registers under the same key and every recorded command behind the delete still resolves.

**How references will survive save/load.** The `Guid` is written verbatim: `.smap` as lowercase `"D"`-format hex (`formats-and-pipeline.md:215`), `.scmap` as 16 big-endian RFC 4122 bytes at offset 0 of the `NODE` record (`formats-and-pipeline.md:301`), so the binary bytes match the text spelling character-for-character. A node record is kept for **every** authored node, including brushes whose geometry dissolved into chunks, because the id is what entity I/O, undo, prefab overrides and Luau lookups all resolve through (`formats-and-pipeline.md:318`).

One consequence already written down: instantiating a project template must **re-GUID every node in every `.smap`** (`realms.md:125`), or every project made from that template shares node ids.

### Where a second identity appears, and why (PLANNED)

`NetId` (`networking.md:233`) exists only because gameplay packets cannot afford 16 bytes of identity per object per tick. Two identity spaces, on purpose (`networking.md:227`): **collaborative editing addresses nodes by `Guid`** — edits are human-rate, and an edit legitimately names a node the receiver has not created yet — while **gameplay addresses objects by `NetId`**, with the `Guid` riding along exactly once in the interest-enter message.

**NetIds are allocated only to `Shared`-realm nodes** (`networking.md:246`), by ordinal in pre-order *restricted to the `Shared` subset*. Three reasons, each a bug if written otherwise: a `Server`- or `Client`-realm node never replicates, so it has no baseline, no delta and no id to burn; restricting the ordinal makes a client-target and a server-target cook of one map number identically, where an unrestricted ordinal would renumber everything after the first stripped record; and per-player content is therefore **not** a fourth audience — it is a per-client interest filter over `Shared` nodes, which is what `NodeRealm.Owner` was deleted for (`realms.md:241`).

**`MaterialRef.Id` is a third id and it must never leave the process** — not to disk (`formats-and-pipeline.md:294`), not onto the wire (`networking.md:258`). It is an index into a per-process append-only table; peer A interning `wall` first and peer B interning `floor` first silently mis-textures the world. Materials travel as a path or a session-table index.

---

## 8. Summary table

| Concept | Exists today | Where it lives | Purpose |
| --- | --- | --- | --- |
| `SceneNode` | **Yes** | `Scene/SceneNode.cs:14` | The single spine: name, transform, hierarchy, optional payloads |
| `SceneNode.Id` (`Guid`) | **Yes** | `SceneNode.cs:60` | Universal, lifetime-stable reference |
| `SceneNode(name, id)` | **Yes** | `SceneNode.cs:44` | Resurrect a node under its old identity |
| `Transform` (pos/rot/scale) | **Yes** | `Scene/Transform.cs:5` | Local pose; `Model` composes S·R·T |
| `WorldMatrix` (cached) | **Yes** | `SceneNode.cs:225` | Derived world pose; eagerly invalidated down the subtree |
| Transform equality early-out | **Yes** | `SceneNode.cs:162`, `:176`, `:184`, `:196` | A no-op write dirties nothing and raises nothing |
| `SubtreeBrushCount` | **Yes** | `SceneNode.cs:222` | O(1) "does this edit touch the static world" |
| `Scene` | **Yes** | `Scene/Scene.cs:40` | Owns root, camera, selection, BVH, id index, derived world |
| `Scene.TryFindById` | **Yes** | `Scene.cs:186` | Guid → live node, O(1) |
| `NodeAdded`/`NodeRemoved`/`NodeTransformChanged` | **Yes** | `Scene.cs:98`, `:108`, `:118` | Change notification; handlers must not mutate the graph |
| `SelectionSet` | **Yes** | `Scene/SelectionSet.cs:20` | Editor selection, auto-deselects on removal |
| `SceneBvh` + `SceneRaycastHit` | **Yes** | `Scene/SceneBvh.cs:60`, `:30` | Dynamic AABB tree over spatial nodes |
| `Camera`, `Ray3`, `Frustum`, `Aabb` | **Yes** | `Camera.cs:14`, `Ray3.cs:16`, `Frustum.cs:16`, `Bsp/Aabb.cs:8` | View, picking and culling primitives |
| `MeshRenderer` payload | **Yes** | `SceneNode.cs:82`; type at `Scene/MeshRenderer.cs:10` | Draw this node with this mesh + material |
| `Mesh` (GPU + CPU copy) | **Yes** | `Graphics/Mesh.cs:14` | Renderer-owned geometry with CPU positions/indices/bounds |
| `Material` | **Yes** | `Graphics/Material.cs:47` | Shader + typed parameters + texture slots |
| `Brush` payload | **Yes** | `SceneNode.cs:116`; type at `Bsp/Brush.cs:20` | Convex solid as half-spaces; the authoring primitive |
| `Brush.WithScaledExtents` | **Yes** | `Bsp/Brush.cs:308` | Resize by editing plane offsets, never node scale |
| `FaceSurface` | **Yes** | `Bsp/FaceSurface.cs:86` | Per-face material + Hammer texture axes, as a pure value |
| `MaterialRef` / `MaterialRegistry` | **Yes** | `Assets/MaterialRef.cs:26`, `:82` | Interned material id safe to carry through a background compile |
| `AssetManager` | **Yes** | `Assets/AssetManager.cs:49` | Typed caches; sync `Load*` vs async `Request*`; owns every GPU resource |
| `BrushPlacement` | **Yes** | `Bsp/BrushPlacement.cs:20` | Brush + captured world matrix — the snapshot unit |
| Rigidity validation | **Yes** | `Scene.cs:1409` | Rejects scale/shear/mirror/NaN brush placements at the snapshot |
| `CsgWorld` | **Yes** | `Bsp/CsgWorld.cs:16` | The compiled static world (derived, never authored) |
| `ChunkCoord` / `ChunkGrid` / `WorldChunk` | **Yes** | `ChunkCoord.cs:14`, `ChunkGrid.cs:23`, `WorldChunk.cs:30` | Sparse unbounded 32-unit cell partition |
| `BspTree` / `BspNode` | **Yes** | `BspTree.cs:14`, `BspNode.cs:10` | Per-cell solid-leaf tree — **queries only**, never rendering |
| `ChunkMesh` / `ChunkSubmesh` | **Yes** | `Bsp/ChunkMesh.cs:75`, `:39` | Per-cell render arrays, split per material |
| Incremental compile caches | **Yes** | `CsgWorld.cs:224`, `:254`, `:301`, `:338` | Pure accelerators; disposable without loss |
| `RenderView` / `RenderItem` | **Yes** | `Graphics/RenderView.cs:39`, `:15` | Reusable per-frame draw list |
| `IEditorCommand` | **Yes** | `Editing/Commands/IEditorCommand.cs:31` | The only sanctioned way to change a scene |
| `ICoalescingCommand` | **Yes** | `Commands/ICoalescingCommand.cs:21` | One drag → one undo entry |
| `SetTransformCommand` / `SetLocalTransformCommand` / `SetBrushCommand` / `CompositeCommand` | **Yes** | `:25` / `:18` / `:32` / `:18` in `Editing/Commands/` | Absolute-value, Guid-addressed edits |
| `UndoStack` | **Yes** | `Editing/Undo/UndoStack.cs:41` | Bounded ring history with gesture transactions |
| `NodeRealm` (audience) | **No** | `docs/realms.md` §2.2 | Declared, inherited "who holds this node" |
| `NodeState` (liveness) | **No** | `docs/realms.md` §2.2 | Declared, inherited "is this node live" |
| `RealmSet` / `EffectiveRealm` / `IsLive` | **No** | `docs/realms.md` §2.3–2.4 | Set-valued resolution; the empty set is `Inert` |
| `Script` payload | **No** | `docs/roblox-onboarding.md` O8 | A script is a node, so `script.Parent` is the parent node |
| `Entity` payload | **No** | `ROADMAP.md` P4–P9 | Source-style entity I/O; `targetname` **is** `SceneNode.Name` |
| Attributes (`AttributeValue`, `NodeRef`) | **No** | `docs/roblox-onboarding.md` O3 | One typed keyvalue bag for both Roblox attributes and entity keyvalues |
| Tags + reverse index + `ObserveTag` | **No** | `docs/roblox-onboarding.md` O3 | `CollectionService` equivalent with the boilerplate removed |
| `NetId` | **No** | `docs/networking.md` §3.3 | Session-scoped 4-byte gameplay identity, `Shared` nodes only |
| Physics body payload | **No — and not designed** | nothing owns it | Deferred: `networking.md:556`, `roblox-onboarding.md:69` |
| `PrefabInstance` payload | **No** | `ROADMAP.md` P10 | `{prefab, seed, overrides}` with derived child GUIDs |
| `Light` payload / `Scene.Environment` | **No** | `docs/roblox-to-spectra.md:33` | Lights are spatial nodes; global settings are a typed struct, not a node |
| `.smap` / `.scmap` node records | **No** | `docs/formats-and-pipeline.md` §2.6, §2.7 | Authored JSON with zero derived data; baked binary with 80-byte `NODE` records |
| Signals (`Signal<T>`, `Destroying`) | **No** | `docs/roblox-to-spectra.md:93` | Per-node events; scene-level events exist, per-node ones do not |
| Settable `Parent`, `Clone`, `Destroy`, `FindFirstChild`, `IsA` | **No** | `docs/roblox-to-spectra.md:82`–`:89` | The familiarity API; today only `AddChild`/`RemoveChild`/`Traverse` exist |
