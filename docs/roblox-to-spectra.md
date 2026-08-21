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
| `Workspace` | `Scene.Workspace` well-known node | **planned** | Today spatial content sits directly under `Scene.Root`. |
| `Lighting` | `Scene.Lighting` node with attributes | **planned** | Needs no bespoke type — it is a node the forward pipeline reads. Nothing reads it today. |
| `ReplicatedStorage` / `ServerStorage` | a single `Storage` node | **deliberate difference** | The two exist only to express a replication boundary. Spectra is single-process; one container is the honest answer. |
| `RunService` (`Heartbeat`, `PreSimulation`, `PreRender`) | engine frame signals | **planned** | The render loop already has all the phase points; nothing is exposed. Roblox's current names plus the legacy aliases are the plan. |
| `Players`, `Humanoid`, `Terrain`, `StarterPlayer` | — | **deliberate difference** | Not provided. A hollow `Humanoid` would be worse than none. |

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
| `:WaitForChild(name)` | sync return if present, throw if absent; `WaitForChildAsync` for the real case | **deliberate difference** | Roblox's `WaitForChild` exists because of replication. There is no replication here, so the silent infinite yield would be imported for nothing. |
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
| `Script` / `ModuleScript` | a `Script` payload on a node, running Luau | **planned** | Nothing exists: no VM, no `Script` type, no scripting project in the solution. |
| `LocalScript` | — | **deliberate difference (for now)** | No client/server split exists, so the distinction would be theatre. |
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
8. **No networking.** No client/server split, no `RemoteEvent`, no replication, no `Players`. Half of Roblox architecture knowledge is inapplicable, and this is unanswered rather than answered "no".
9. **No model import.** `Silk.NET.Assimp` is referenced; no importer code exists. Meshes come from `Primitives`.
10. **Thin rendering feature set.** Forward and wireframe only: no shadows, no post-processing, no transparency pass, no offscreen render targets, no PBR, no instancing.
11. **Sharp edges worth knowing.** `AddChild` has no cycle guard (a parent loop recurses to stack exhaustion). A non-rigid write on a brush node is accepted at the setter and rejected three stages later, freezing the static world at its last good compile with only a log line. `Scene.Raycast` reports no hit for a ray that starts inside a solid, so a camera inside a wall cannot click-select it.
