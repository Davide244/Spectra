# Roblox to Spectra

A concept map for developers coming from Roblox Studio. Written to be honest first and welcoming second: everything below is marked with what actually exists in the tree today.

## What this engine is

Spectra is a C#/.NET 10 game engine built on a scene graph, with Hammer-style CSG brushes as the authoring primitive for world geometry, an unbounded open world that recompiles asynchronously in 32-unit chunks, three live render backends (OpenGL, D3D11, D3D12), and its own shader language (SpectraShade) that cross-compiles to GLSL and HLSL and hot-reloads on save.

**The one-line reason a Roblox developer can move over:** the scene graph *is* the DataModel — a tree of named nodes with stable identity, where a node's rigid world transform is exactly a `CFrame` and a brush's local plane extents are exactly `Part.Size` — so the data model you already have in your head transfers almost intact, and the geometry gets better.

**The blunt part.** Today Spectra is an engine library plus a demo executable. There is no editor, no scripting runtime, no save format, no physics, no networking, and no per-part colour. The scene graph, the CSG world compiler, the renderers, and the shader toolchain are real and tested; almost everything a Roblox developer *touches* — Explorer, Properties, Play, `Instance.new`, `:Destroy()`, attributes, signals — is designed but unbuilt. This document marks each item so nothing is oversold.

**Status vocabulary**

| Status | Meaning |
| --- | --- |
| **exists** | In the tree now, working, usually test-covered. |
| **planned** | Designed and decided, not written. Treat as vapour until it lands. |
| **deliberate difference** | Spectra does not and will not match Roblox here, on purpose. |

---

## The mapping table

### Tree and services

| Roblox concept | Spectra equivalent | Status | Note |
| --- | --- | --- | --- |
| `Instance` | `SceneNode` | **exists** | One concrete class. No subclass hierarchy — `Brush` and `MeshRenderer` are optional payload fields on the node. |
| `Instance` identity | `SceneNode.Id` (`Guid`) | **exists** | Assigned at construction, stable across rename and reparent. Better than Roblox, which has no instance identity at all. |
| `DataModel` / `game` | `Scene` / `Scene.Root` | **exists** | `Scene` owns the root, camera, selection set, BVH, and the derived static world. |
| `Workspace` | `Scene.Root` itself; Luau `workspace` aliases it | **exists** (root) / **planned** (alias) | **There is no `Workspace` container.** Spatial content sits directly under `Scene.Root`, which is what `workspace.Wall` will resolve against, so that expression ports character-for-character. What decides whether a node is live world content is the `State` property below, not its ancestry. |
| `Lighting` | **three things, three homes** — a typed `Scene.Environment` settings struct for global properties, asset references on it for sky/atmosphere, and a render-arc post-effect chain | **planned** | **Corrected.** An earlier version of this row said "a `Scene.Lighting` node with attributes". A node implies a transform, a parent, a realm and a subtree brush count, none of which mean anything for fog density. Lights themselves *are* spatial `SceneNode`s carrying a `Light` payload. Nothing reads any of it today. |
| `ServerStorage`, `ServerScriptService` | `Realm = Server`, plus `State = Dormant` for the storage case | **planned** | **Corrected — this replaces a `Storage`-collapse row** that read "deliberate difference, Spectra is single-process". Multiplayer is designed (`docs/networking.md`), so that premise is dead. Note these two Roblox folders have *identical* audience and differ only in liveness — which is exactly what the two properties separate. Design: [`docs/realms.md`](realms.md). |
| `ReplicatedStorage` | nothing — `Shared` is the default | **planned** | Shared content needs no marking at all, so the container has no replacement and needs none. Roblox's "client writes persist locally but never replicate back" survives as an **authority** rule (`networking.md` §4.4), which is stronger. |
| `ReplicatedFirst` | `JoinPriority = First` on the node | **planned** | Sent ahead of the bulk world-sync channel. `game:IsLoaded()` → a `WorldReady` signal; `RemoveDefaultLoadingScreen()` → `DismissBootScreen()`. |
| `StarterGui`, `StarterPack`, `StarterPlayerScripts`, `StarterCharacterScripts` | declarative **spawn rules** in `game.spectraproj`: `{ template, phase, destination }` | **planned** | `phase` is `OnJoin` or `OnCharacterSpawn` — the distinction Roblox leaves unwritten in the tree (`StarterPlayerScripts` copies once, `StarterCharacterScripts` copies every death) becomes a word on the rule. `ResetOnSpawn` disappears into `phase`. **`StarterPack`'s destination does not exist**: tools and inventory are an unbuilt subsystem, so that one rule ports with nothing to spawn into. |
| `StarterPlayer` (the object) | split: 23 properties → a player-defaults settings block; its two script containers → spawn rules | **planned** | It was never a container; it was a property bag that also parented two containers. **There is no `StarterPlayer` node.** |
| `RunService` (`Heartbeat`, `PreSimulation`, `PreRender`) | engine frame signals | **planned** | The render loop already has all the phase points; nothing is exposed. Roblox's current names plus the legacy aliases are the plan. |
| `Players` | a **service and index**, not a tree container | **planned** | A player has no transform, no brush and no realm. Its *contents* stay real nodes: `player.Gui`, `player.Scripts`, `player.Character` live in a per-player subtree, replicated to that one client by a per-client interest filter. This is the split Roblox already makes (`Player` in `Players`, `Character` in `Workspace`), made explicit. |
| `CollectionService` | per-node tags + a scene reverse index + `ObserveTag` | **planned** | See the Instance API table. The reverse index must take an audience mask, or it enumerates content the caller cannot see. |
| `SoundService` | `Scene.Audio` settings + `Audio.Play2D(clip)`; spatial sounds are payloads on world nodes | **planned** | A sound is never parented to a service. Audio is a stub in the tree today. |
| `Teams` | nothing in v1 — a string attribute or a `NodeRef` to a team entity | **deliberate difference** | `Teams` exists largely to drive `TeamColor` on the default leaderboard, which does not exist here, and `BrickColor` is already rejected. Cheap to add later; expensive to ship an empty one. |
| `Humanoid`, `Terrain`, `Debris`, `CoreGui`, `Chat`, `TestService` | — | **deliberate difference** | Not provided. A hollow `Humanoid` would be worse than none; `Terrain` is a deliberate difference because brushes *are* the world; `Debris` is `task.delay` + `Destroy`; `CoreGui` has no analogue in an engine with no platform UI. |
| `ReplicatedScriptService` | nothing | n/a | Removed from Roblox itself in 2022 (v0.526.0); never had members. Named only to close the question. |
| — (no Roblox analogue) | `SceneNode.Realm` — `Inherit` / `Shared` / `Server` / `Client` | **planned** | The audience axis, lifted off tree location. Inherited down the subtree; `Shared` is the effective default everywhere. |
| — (no Roblox analogue) | `SceneNode.State` — `Inherit` / `Active` / `Dormant` | **planned** | The liveness axis. `Dormant` means not carved, not drawn, not queried, not ticking — a parked template you can keep next to the thing that clones it. This is `ServerStorage`'s *other* job, and it needs its own property because there is no way to make a brush inert today. |

### Parts and geometry

| Roblox concept | Spectra equivalent | Status | Note |
| --- | --- | --- | --- |
| `Part` / `BasePart` | `SceneNode` carrying a `Brush` | **exists** | `Brush.CreateBox(min, max)`; the node's world transform places it. |
| `MeshPart` | `SceneNode` carrying a `MeshRenderer` | **exists** | Meshes come from code (`Primitives`) — there is no model importer yet. |
| `UnionOperation` / `NegateOperation` | CSG carve of the whole brush set | **deliberate difference** | Every brush carves every overlapping brush, continuously and non-destructively. There is no union *object* to create or bake. |
| `Part.Shape` (Ball, Cylinder, Wedge) | convex plane sets | **deliberate difference** | Wedges and prisms are expressible; a true sphere is not a convex plane set. |
| `Part.Size` | brush local plane extents | **planned** | The semantics are settled doctrine — resize edits the plane offsets, never a scale. The `Size` property itself does not exist; today you build a new `Brush` and assign it. |
| `Part.CFrame` | the node's rigid world transform | **exists** (transform) / **planned** (`CFrame` type) | `LocalTransform` / `LocalPosition` / `LocalRotation` / `WorldMatrix` exist. A `CFrame` struct does not. |
| node scale | `SceneNode.LocalScale` | **deliberate difference** | Exists, and must never be used on a brush node — see below. Roblox has no scale concept at all. |
| `Anchored` | — (everything is static) | **deliberate difference** | Brushes compile into the fused world by construction. The plan is to default `Anchored = true` and make `false` throw rather than silently lie. |
| `CanCollide` / `CanQuery` | flags on the chunk BSP / BVH query path | **planned** | Cheap — the chunk builder already separates render-owned brushes from query-resident ones. |
| `Material`, `Color3`, `Transparency` | per-face material + per-vertex colour | **planned (large)** | Today the vertex layout is position/normal/uv with no colour channel, and `Scene.StaticWorldMaterial` is a single material for the entire world. Two adjacent brushes cannot be different colours by any route. |
| texture alignment | Hammer-style per-face UV axes | **planned** | The carve already emits world-space planar UVs on the dominant axis — the correct "world align" base case — but there is no per-face data to align. |

### Values

| Roblox concept | Spectra equivalent | Status | Note |
| --- | --- | --- | --- |
| `Vector3` | `System.Numerics.Vector3` | **exists** | In Luau, the plan is to bind to Luau's native `vector` type so position math stays allocation-free. |
| `CFrame` | `CFrame` readonly struct (position + quaternion) | **planned** | Load-bearing: a type that *cannot* represent scale makes rigidity structural instead of validated. Without it, no Roblox transform code ports. |
| `Color3` | `Color3` readonly struct | **planned** | Blocked behind per-part appearance. |
| `Orientation` (YXZ degrees) | `CFrame.Angles(rx, ry, rz)` in radians | **deliberate difference** | Degrees in a radians codebase is a footgun. The editor's Properties panel is planned to show degrees anyway. |
| `BrickColor` | — | **deliberate difference** | Legacy inside Roblox itself. Not coming. |
| 1 stud | 1 world unit | **planned/undecided** | Nothing in the engine forces a scale. The chunk cell is pinned at 32 units, so 1 unit = 1 stud makes a chunk 32 studs, which is the leading proposal. |

### Instance API

| Roblox concept | Spectra equivalent | Status | Note |
| --- | --- | --- | --- |
| `Instance.new("Part")` | `new SceneNode(name)` / `parent.CreateChild(name)` | **exists** (nodes) / **planned** (by class name) | Class-name construction needs a hand-registered class table; there is none. |
| `.Parent = x` | `parent.AddChild(child)` | **exists** (method) / **planned** (settable `Parent`) | `Parent` is read-only today. `AddChild` has **no cycle guard** — a parent loop recurses to stack exhaustion. |
| `:GetChildren()` | `node.Children` | **exists** | Allocation-free `IReadOnlyList`, unlike Roblox's fresh table per call. |
| `:GetDescendants()` | `node.Traverse()` | **exists** | Pre-order, explicit stack. Allocates one `Stack` per call — do not call it per frame. |
| `:FindFirstChild(name)` | — | **planned** | |
| `:WaitForChild(name)` | sync return if present, throw if absent; `WaitForChildAsync` for the real case | **deliberate difference** | Roblox's `WaitForChild` exists because the whole DataModel streams in. **Corrected:** replication *is* designed (`docs/networking.md`), so "there is no replication here" is no longer the reason. The reason is narrower and survives it — an **authored** node is present on every client from map load, forever, and only its property updates are interest-filtered, so the silent infinite yield has nothing to wait for. It stays a real API for content spawned at runtime, which is what `WaitForChildAsync` is. Note this call is also subject to the audience gate: a node your realm does not hold reads as absent, exactly as it would on a remote client. |
| `:IsA(className)` | string class table (`NodeClassRegistry`) | **planned** | Not C# subclassing and not reflection — AOT forbids the latter. |
| `:Clone()` | `SceneNode.Clone()` | **planned** | Must deep-copy the `Brush` (a `Brush` instance must never be shared between nodes) while sharing the GPU mesh. |
| `:Destroy()` | `Destroy()` with a `Destroying` signal and a destroyed lock | **planned** | Today: `node.Parent?.RemoveChild(node)`, which detaches, drops the BVH leaf, and auto-deselects — but has no lock and no event. |
| `workspace.Baseplate.Part` (dynamic child indexing) | — | **deliberate difference** | Impossible without reflection or `dynamic`, both banned by the AOT rule. Use `FindFirstChild`, tags, or a `Guid` node reference. This is the biggest thing you lose. |
| `Instance:SetAttribute` / `GetAttribute` | typed attribute union on `SceneNode` | **planned** | One mechanism will serve both Roblox attributes and level-entity keyvalues, plus a `NodeRef` type Roblox lacks. |
| `CollectionService` tags | per-node tags + a scene-wide reverse index | **planned** | Plus `ObserveTag`, which replays already-tagged nodes — the boilerplate every Roblox dev writes by hand. |
| `RBXScriptSignal` (`Connect`/`Once`/`Wait`/`Disconnect`) | `Signal<T>` with `Connect` returning `IDisposable` | **planned** | Scene-level `NodeAdded` / `NodeRemoved` / `NodeTransformChanged` events **exist** today, with a hard re-entrancy rule: handlers must not mutate the graph. |
| deferred vs immediate signals | deferred only | **deliberate difference** | Roblox's current default is Immediate. Deferred is where Roblox says it is heading, and it is the only mode compatible with the scene's re-entrancy contract. |
| `Model` + `PrimaryPart` | any `SceneNode` used as a group | **exists** (grouping) / **deliberate difference** (semantics) | Spectra composes transforms down the tree — move the parent, the children move. Roblox `Model`s do not compose, which is why `PivotTo` exists. There is no `Model` class and no `PrimaryPart`. |

### Scripts and Studio

| Roblox concept | Spectra equivalent | Status | Note |
| --- | --- | --- | --- |
| `Script` / `ModuleScript` | a `Script` payload on a node, with **one** axis: `bool IsModule` | **planned** | Nothing exists: no VM, no `Script` type, no scripting project in the solution. |
| `LocalScript` | a script node declared `Realm = Client` | **planned** | **There is no second script type.** Where the script *runs* is the node's realm, and Roblox agrees this was the mistake — it shipped `Enum.RunContext` in 2022 to unwind exactly this, motivated by *"consolidating Script and LocalScript behavior"*. We have no content, so we do it once. |
| `Script.RunContext` | `SceneNode.Realm` | **planned** | Roblox's partial fix decoupled run location and left audience coupled to the container, which is why a `RunContext = Client` script in `ServerScriptService` silently does not run. Here there is one axis and it is the node's. |
| "where does this script run?" | **a script runs on the narrowest side its node exists on, and `Shared` means server** | **planned** | Stated precisely because the tempting slogan — *"where a script exists is where it runs"* — is **false**: a `Shared` node exists on both sides and its runnable script runs on one. `Server` → server. `Client` → every client, one private instance each. `Shared` → the **server**, once. A `Shared` *module* is requirable from both sides and yields **one instance per side**, which is what a `ModuleScript` in `ReplicatedStorage` already does. There is no value meaning "runs on both". |
| C# gameplay code | compiled C# against `SpectraEngine.Core` | **exists** | This is the only way to write behaviour today: reference the engine and write C#. |
| Explorer | editor tree panel | **planned** | |
| Properties | editor property panel driven by descriptor tables (no reflection) | **planned** | |
| Output | `ILogger`-backed panel with a ring buffer | **planned** (panel) / **exists** (logging spine) | The engine already logs compile stats and snapshot defects through `Microsoft.Extensions.Logging`. |
| Command Bar | execute Luau against the live edit-mode scene | **planned** | Named explicitly because it is Studio's *tightest* loop — faster than Play — and the easiest thing to get wrong by omitting. |
| Play / Stop | clone or snapshot the edit scene, run, then discard | **planned** | Will be an **in-memory** structural snapshot, not a serializer round-trip. There is no scene serializer in the tree, and making Play/Stop its first consumer would mean a serializer bug eats your authored scene. |
| `.rbxl` place file | `.spectramap` | **planned** | No save/load of any kind exists today. |

---

## The differences that actually matter

### Brushes are real convex solids, and they carve each other

A brush is an intersection of half-spaces — one outward plane per face — carved against every other brush that overlaps it. That makes it a superset of `Part` *and* `UnionOperation`: you do not create a union, negate a part, and bake the result. You place solids, and the world is the carved result, recompiled continuously. Nothing is destructive; move a brush back and the geometry it removed comes back.

The consequence for muscle memory: **resizing is not scaling.** In Roblox, `Part.Size` writes extents, and that is exactly right — Spectra does the same thing, by rewriting the brush's local plane offsets and producing a new `Brush` for the node. A brush-for-brush swap on a node is already routed through the fast incremental path in the engine today, so resize costs the same as a move. What does not exist yet is the `Size` property that wraps it; right now you construct `Brush.CreateBox(-half, +half)` and assign it.

### Transforms are rigid, and the node transform is the CFrame

A brush node's world transform must be rotation plus translation. No scale, no shear. This is not a style preference: the CSG epsilon scheme assumes unit-length plane normals, and the compile *validates* it — a non-rigid brush placement is rejected at snapshot time, which today means the static world silently freezes at its last good compile and logs a line. So `LocalScale` on a brush node is the one write that looks harmless and is not. Size things with brush extents; place them with position and rotation.

The upside is that a rigid transform is exactly a `CFrame`, which is why shipping a `CFrame` type is a load-bearing decision rather than sugar: a type that cannot represent scale makes the invariant structural instead of validated three stages later.

One place Spectra is genuinely better: **grouping composes.** Parent a set of brush nodes under a group node, move the group, and everything moves — no `PivotTo`, no `PrimaryPart`, no `Model` class. Roblox `Model`s do not compose transforms; Spectra's graph always has.

### The world compiles asynchronously, in chunks, and the map has no edges

Brush edits mark the world dirty; the render thread snapshots placements; a background task carves, snaps, welds, builds per-cell BSP trees and mesh arrays; the render thread swaps in only the chunks whose artifacts changed. The measured cost of a one-brush edit is **~0.05–0.1 ms at 1k, 10k and 50k parts**, half of them 8,000 units from the origin — the benchmark's verdict line reads *world-size independent* and is treated as a standing invariant.

Practically: there is no baseplate, no map extents, no 2048-stud part clamp, and negative or distant chunk cells cost exactly what the origin costs. Brush geometry is stored in brush-local frames, so a brush 10 km out has the same floating-point accuracy as one at the origin. And because a single edit is sub-millisecond at any world size, a gizmo drag can write transforms every frame with the carved world updating live — which Studio cannot do with unions.

The static world is *derived data*. You never author it; you author brush nodes, and it recompiles. There is no equivalent of it in the Explorer tree, and there will not be.

### The scene graph is the single spine

There is one node type. There is no ECS, no component registry, no entity table, and none is planned. Payloads — `Brush`, `MeshRenderer`, and later `Entity` and `Script` — are optional fields on the node. That is closer to Roblox's own model than it first looks: in Roblox, behaviour is an attached `Script` child, not a subclass. `Part : BasePart : Instance` is a class table for `IsA`, not a C# inheritance chain, and in Spectra it will literally be a string table for exactly that reason (reflection is banned by the AOT rule).

### There are no service containers — audience and liveness are properties of the node

**Status: planned. None of this exists in the tree.** The full design is [`docs/realms.md`](realms.md); this section is the part you need to read the mapping table above. Read it as a design decision you will meet later, not as something you can try today.

Roblox encodes **four independent questions** in **one dimension — where in the tree the object sits**:

| the question | how Roblox asks it |
| --- | --- |
| who holds this data at all | `ServerStorage`/`ServerScriptService` vs `ReplicatedStorage` vs `Workspace` |
| when does it arrive on a client | `ReplicatedFirst` vs everything else |
| is this live world content, or a parked template | `Workspace` vs any storage container |
| is it copied per player, and when | the four `Starter*` containers |

One dimension cannot carry four values, and the costs are mechanical rather than stylistic. The axes cannot be combined — "shared, but parked" is not expressible at all, and `ServerStorage` and `ServerScriptService` have *identical* audience and differ only in liveness, which is why there are two folders with one word between them and no documentation that says so plainly. The rules are tribal knowledge, because a folder name cannot carry a rule. And the one that costs you every day: **you cannot put a server-only spawner next to the enemies it spawns.** The spawner must live in `ServerScriptService`, the enemies in `Workspace`, the templates in `ServerStorage`. Three folders, one feature, and the tree is organised for the replication system instead of for your game.

Spectra splits it into **two inherited properties you declare on the node where the exception actually is**:

- **`Realm`** — `Inherit` (the default) / `Shared` / `Server` / `Client`. Who holds it. `Shared` is what the root resolves to, so the overwhelming majority of content declares nothing.
- **`State`** — `Inherit` (the default) / `Active` / `Dormant`. Whether it is live: carved into the world, in the spatial index, drawn, queried, ticking. `Dormant` is a parked template.

Both inherit down the subtree and both resolve by **narrowing**: a child can be as narrow as its parent or narrower, never wider. A `Server` subtree cannot contain a `Shared` child, because a client that does not hold the parent has no way to compute the child's world transform — its transform is parent-relative, so the only options would be silently reparenting it (server and client then disagree about where a replicated object *is*) or shipping the hidden ancestors' names and transforms (leaking exactly what the declaration existed to hide). Both are refused.

Drag a `Client` subtree under a `Server` parent and it does **not** become server content. `Server ∩ Client` is empty, and the result has a name — **`Inert`** — meaning the subtree exists nowhere as live content, its scripts run nowhere, and the editor badges every affected node. Your *declarations* are untouched, so dragging it back restores it exactly. The alternative — clamping to the ancestor — would turn client code that ran with no authority into server code that runs *with* authority, from a mouse gesture, which is a worse version of the problem this design exists to delete.

Three consequences that will bite if you do not know them up front:

1. **A brush node is always `Shared`.** Marking a brush-bearing subtree `Server` or `Client` is refused at the setter, not excluded with a warning. Brushes carve into the one static world that every peer renders *and collides against*, so a server-only brush would publish its exact shape as the hole it leaves, and the server's collision world would disagree with every client's — you would rubber-band into a wall you cannot see. If you want a region parked, that is `State = Dormant`. If you want something drawn but never carved, that is a `MeshRenderer` node, which lives outside CSG.
2. **Server content is not secret on disk in v1.** The tree stops your own client code from reading it; it does not stop someone unpacking the game. Server node records — names, transforms, attributes, and server script source — ship in the client's pack. Do not put a secret in a server script. (There is a route to on-disk secrecy — a cook-target strip — and it is designed, not built.)
3. **What you get is orthogonality, not fewer rules.** Honestly counted, the new model is about ten rules and three of them are exceptions. Roblox's containers are eight folders you can *see*, one rule each, learned on demand, in a project that ships them non-empty. The defensible claim is not "smaller" — it is that **you can finally put the spawner next to the enemies it spawns.**

#### Porting `game.ServerStorage.Enemy:Clone()`

Be ready for a rewrite, not a shim. **There is deliberately no compatibility layer**, and the reason is not cost: the moment `game.ServerStorage` resolves to a real node, the container model is back *without its rules* — nothing would then stop a brush under it from carving — and you would be running both models at once, which is worse than either. Every future Spectra tutorial would have two right answers. There is also no codemod, because it cannot be made sound for Luau: `game[name]`, `FindFirstChild("ServerStorage")`, string-built paths and `require` chains defeat a regex and a real parser alike, and a codemod that fixes 80% and silently misses 20% fails in production instead of at your keyboard.

What you get instead is the codemod's *value* as diagnostics: `game.ServerStorage`, `game.ReplicatedStorage`, `game.StarterGui` and friends are declared as **deprecated symbols in the generated `spectra.d.luau`**, whose type-level message names the replacement — so under `--!strict` the 20% fails at the type checker. And `game:GetService(name)` **throws**, naming the replacement, rather than returning `nil`: the universal idiom binds the service on one line and indexes it ten lines later, so a `nil` would report the wrong line while the helpful message scrolled past.

```lua
-- Roblox
local enemy = game.ServerStorage.Enemy:Clone()
enemy.Parent = workspace

-- Spectra as designed: a Dormant template, sitting next to the spawner that uses it
local enemy = script.Parent.Templates.Enemy:Clone()   -- Templates.State == "Dormant"
enemy.State  = "Active"
enemy.Parent = workspace.Arena
```

**And the honest ergonomic gap:** a `Dormant` subtree is not a prefab. You clone and re-parent it by hand, it has no override mechanism, and its contents are written into the map rather than expanded from a shared definition. Prefabs are on the roadmap, explicitly *off* the critical path, so `Dormant` is the answer for the next several milestones rather than a stopgap that lands next month.

**Finally, the container mapping is the easy third of a port.** The hard two thirds are that your gameplay code references physics, `Humanoid`, `TweenService`, `DataStoreService` and per-part colour — none of which exist here. A migration guide that leads with a slick container table and buries that would be dishonest, so it is stated here instead.

### Two languages: Luau for gameplay, C# for the engine

The plan is a hybrid, weighted Luau-first:

- **Luau** — the real thing, MIT-licensed, vendored and driven through P/Invoke — for gameplay. Not "a Lua dialect": your `--!strict`, `continue`, `+=`, string interpolation, and type annotations all work. It runs on the render thread only, with deferred-only signals, an interrupt watchdog so `while true do end` cannot freeze the editor, and per-script sandboxing. Compiling a `.luau` file at runtime is *native* codegen inside a native library, so an AOT-published game can still load and run editable scripts.
- **C#** — compiled, engine-facing — for systems, importers, custom entity types, and editor tooling. This is the only language that works today.

Two honest costs. First, the boundary is a cliff, not a ramp: Studio's floor and ceiling are the same language, and here outgrowing Luau means a .NET SDK and a build step. Second, **none of the Luau half exists yet** — no VM, no bindings, no `Script` node. The design's own adversarial review flagged that the interop is harder than it looks: Luau raises errors as C++ exceptions or `longjmp`, which cannot legally unwind through managed frames, so the native shim has to be the error boundary rather than a thin P/Invoke convenience. Expect it to land as a large piece of work, not a weekend.

There is also a runtime split that has no Roblox analogue and will bite early: `Instance.new("Part")` at *runtime* should not become a brush. Attaching a brush changes the world's placement count, which forces the conservative full-walk recompile — a spawn loop would recompile the whole world every frame it runs. Authored parts are brushes; script-spawned parts during play need to be dynamic mesh nodes outside CSG.

---

## Side by side

Every Spectra snippet is labelled with whether it runs today.

### Create and parent an object

```lua
-- Roblox
local p = Instance.new("Part")
p.Size = Vector3.new(4, 1, 2)
p.Position = Vector3.new(0, 10, 0)
p.Parent = workspace
```

```csharp
// Spectra, C# — EXISTS TODAY
var node = scene.Root.CreateChild("Part");
node.LocalPosition = new Vector3(0, 10, 0);
// Size lives in the brush's local plane extents: half-extents, centred on
// the node's origin. 4 x 1 x 2 full extents => -2..2, -0.5..0.5, -1..1.
node.Brush = Brush.CreateBox(new Vector3(-2f, -0.5f, -1f), new Vector3(2f, 0.5f, 1f));
```

```csharp
// Spectra, C# — PLANNED (CreatePart, Size, settable Parent, CFrame, Workspace)
var part = scene.CreatePart("Part", size: new Vector3(4, 1, 2));
part.CFrame = new CFrame(new Vector3(0, 10, 0));
part.Parent = scene.Workspace;
```

```lua
-- Spectra, Luau — PLANNED (no VM exists)
local p = Instance.new("Part")
p.Size = Vector3.new(4, 1, 2)
p.CFrame = CFrame.new(0, 10, 0)
p.Parent = workspace
```

### Move something

```lua
-- Roblox
part.CFrame = CFrame.new(0, 10, 0) * CFrame.Angles(0, math.rad(45), 0)
```

```csharp
// Spectra, C# — EXISTS TODAY
node.LocalPosition = new Vector3(0, 10, 0);
node.LocalRotation = Quaternion.CreateFromYawPitchRoll(MathF.PI / 4f, 0f, 0f);
// Writing either one auto-marks the static world dirty and rides the
// incremental compile path. Rigid only — never write LocalScale here.
```

```csharp
// Spectra, C# — PLANNED
node.CFrame = new CFrame(new Vector3(0, 10, 0)) * CFrame.Angles(0, MathF.PI / 4f, 0);
```

Note the transform is *local* today (`LocalPosition` is relative to the parent), while Roblox's `Position` and `CFrame` are world-space. Which name gets which meaning is an open decision, and it must be locked before any content is written against it — getting it wrong is silent, not loud.

### Connect to a signal

```lua
-- Roblox
local conn = workspace.ChildAdded:Connect(function(child)
    print(child.Name .. " arrived")
end)
conn:Disconnect()
```

```csharp
// Spectra, C# — EXISTS TODAY (scene-level events only, no per-node signals)
scene.NodeAdded += node => logger.LogInformation("{Name} arrived", node.Name);
// Hard rule: a handler must NOT add, remove, or reparent nodes. Membership
// events fire in the middle of the ownership walk, and a structural edit
// there corrupts the traversal. This is a contract, not an enforced guard.
```

```csharp
// Spectra, C# — PLANNED (per-node signals, deferred delivery, disposable handle)
using var conn = node.ChildAdded.Connect(child => Console.WriteLine($"{child.Name} arrived"));
```

```lua
-- Spectra, Luau — PLANNED
local conn = workspace.ChildAdded:Connect(function(child) print(child.Name) end)
conn:Disconnect()
```

Deferred delivery is a named difference: your handler runs at the next resumption point, not before the next line of the firing function.

### Destroy something

```lua
-- Roblox
part:Destroy()
```

```csharp
// Spectra, C# — EXISTS TODAY (detach only)
node.Parent?.RemoveChild(node);
// Detaches, unwinds the subtree brush count, marks the static world dirty,
// drops the BVH leaf, and auto-deselects. But: no Destroying event, and the
// node is still usable afterwards — nothing locks it.
```

```csharp
// Spectra, C# — PLANNED
node.Destroy();   // fires Destroying subtree-pre-order, then detaches, then locks
```

Mass deletion is already cheap in a way worth knowing: 500 deletes in one frame produce one full-walk recompile total, not 500, because the compile pump snapshots once per frame.

### Read and write an attribute

```lua
-- Roblox
part:SetAttribute("Speed", 16)
local speed = part:GetAttribute("Speed")
```

```csharp
// Spectra — NOTHING EXISTS TODAY.
// There is no attribute bag on SceneNode. Keep your own
// Dictionary<Guid, T> keyed on node.Id until the feature lands.
```

```csharp
// Spectra, C# — PLANNED (typed overloads, no boxing, AOT-safe union)
node.SetAttribute("Speed", 16f);
if (node.TryGetAttribute("Speed", out var v))
    float speed = v.AsFloat();
```

---

## What Spectra does that Roblox cannot

- **True CSG.** Every brush carves every overlapping brush, live and non-destructively. No 5,000-triangle union cap, no baking, no negate-parts to manage. **Exists.**
- **An unbounded world with size-independent edits.** No baseplate, no map extents, no part-position clamp; one-brush edits at ~0.05–0.1 ms whether the world holds 1k or 50k parts. **Exists.**
- **Custom shaders in a real shader language.** SpectraShade source files compile per backend at runtime and hot-reload on save while the program's identity stays stable, so every material picks up the change. **Exists.**
- **Three render backends.** OpenGL, D3D11, D3D12, each with forward and wireframe pipelines over one shared draw list. OpenGL is the Linux path. **Exists.**
- **Native AOT-published games.** `PublishAot` is already set on the executable and on the shader compiler CLI, so the AOT analyzers run on every build of those projects. Caveat, and it is a real one: an end-to-end AOT publish has not been proven, and Silk.NET 2.23 needs explicit `GlfwWindowing.RegisterPlatform()` / `GlfwInput.RegisterPlatform()` calls under NativeAOT that are absent from the tree — expect the first publish to build and then die at window creation.
- **A tree organised for your game instead of for the replication system.** Audience and liveness are declared on the node where the exception is, so the server-only spawner, the enemies it spawns and the templates it clones can be siblings — which four Roblox containers make impossible. **Planned, not built**, and the honest framing is orthogonality rather than simplicity: it is about ten rules, three of them exceptions, against Roblox's eight visible folders.
- **Per-face materials with Hammer-style texture alignment.** The differentiator Studio has no answer for — Roblox gives you six-axis decals on box faces and no alignment tooling. **Planned, not built**, and it is the highest-fanout unbuilt item in the engine.

---

## What is not built yet

Ordered by how much it will matter to you, not by engineering size.

1. **No editor.** No Explorer, Properties, Output panel, Command Bar, gizmos, selection tools, undo/redo, or Play/Stop. The plan is an Uno Skia-Desktop host that pumps the engine in an embedded mode; none of it exists. Today you author scenes in C# code.
2. **No scripting runtime.** No Luau VM, no `Script` node, no `Instance.new`, no `task` library, no signals to connect to. C# against the engine library is the only way to write behaviour.
3. **No per-part appearance.** No `Color3`, no `Material`, no `Transparency`. The static world renders with one material and a vertex layout that has no colour channel. Colouring a part is a Roblox developer's second action, and it currently has no route at all.
4. **No physics and no character.** No `Touched`, no `Anchored`, no `AssemblyLinearVelocity`, no `Humanoid`, no `TweenService`, no walking around. Your existing gameplay code does not partially port — it does not port. The BSP already answers point and ray queries, so a capsule controller and trigger volumes are reachable, but they are not written.
5. **No Instance API surface.** No settable `Parent`, `FindFirstChild`, `WaitForChild`, `IsA`, `Clone`, `Destroy`, attributes, tags, or per-node signals.
6. **No `CFrame` or `Color3` value types.** Transform code is `System.Numerics` matrices and quaternions.
7. **No save or load.** There is no scene serializer anywhere in the tree — no place file, no prefabs, no asset round-trip. An engine without save/load is a demo, and this is the gap the Play/Stop and undo designs both have to route around.
8. **No networking, and no realms.** No client/server split, no `RemoteEvent`, no replication, no `Players` — and no `Realm`/`State` properties on `SceneNode`, so today there is no audience concept anywhere in the tree and nothing is hidden from anything. Half of Roblox architecture knowledge is inapplicable. Unlike the other entries on this list, this one is now **answered on paper**: [`docs/networking.md`](networking.md) designs the multiplayer arc and [`docs/realms.md`](realms.md) designs the replacement for the service containers. Both are design documents in the sense this page's status vocabulary means by **planned** — decided, written down in detail, and not written in code. Treat them as vapour until they land.
9. **No model import.** `Silk.NET.Assimp` is referenced; no importer code exists. Meshes come from `Primitives`.
10. **Thin rendering feature set.** Forward and wireframe only: no shadows, no post-processing, no transparency pass, no offscreen render targets, no PBR, no instancing.
11. **Sharp edges worth knowing.** `AddChild` has no cycle guard (a parent loop recurses to stack exhaustion). A non-rigid write on a brush node is accepted at the setter and rejected three stages later, freezing the static world at its last good compile with only a log line. `Scene.Raycast` reports no hit for a ray that starts inside a solid, so a camera inside a wall cannot click-select it.
