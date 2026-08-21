# Roblox Onboarding — the engineering plan

> How a Roblox developer becomes productive in Spectra.
> Companion to `ROADMAP.md`, which owns the engine arcs (editor interaction `E*`, persistence/entities `P*`, foundations `F*`, shaders `S*`, rendering `R*`, Uno hosting `H*`). This document owns one arc — call it **`O*`** — and references roadmap milestones by id instead of restating them.
> Sizes are relative (**S / M / L**), never calendar. Status is marked explicitly: **exists**, **planned**, **decision needed**. Nothing here was built or run — another workflow holds the tree — so every claim about what exists was read out of source, and every claim about what will happen is labelled as a prediction.

---

## 1. The pillar, and the alignments that already exist

**A Roblox developer should be able to switch to Spectra by learning C# (and maybe Lua) plus a small, enumerable set of data-model differences — not by relearning what a game engine is.** That is the whole target. It is not "add a scripting language"; it is that the tree, the transform, the part, the property, the event and the Play button all mean what they already mean to them, and that where they do not, the difference is one sentence long and written down before it is discovered.

This reads as a continuation rather than a pivot because the alignment is already unusually deep, and it is structural rather than cosmetic:

- **The scene graph is the DataModel.** `Scene.Root` is a single spine of `SceneNode`s with `Parent`/`Children`, and payloads (`Brush`, `MeshRenderer`) are optional fields on one concrete node type — which is exactly Roblox's model, where node type is data and behaviour is attached. There is no ECS and none is planned.
- **`SceneNode.Id` is a `Guid` assigned at construction.** That is a *better* instance identity than Roblox has: stable across rename and reparent, and already the identity that serialization and undo are designed around.
- **Brush resize already has `Part.Size` semantics.** A brush edit changes local plane extents; the node transform stays rigid (rotation + translation, no scale). Roblox parts work the same way — `Part.Size` is not a scale.
- **A rigid node transform is a `CFrame`.** Position + rotation, no scale, is precisely the type Roblox transform code is written in.
- **The change events an editor and a script layer need already exist**: `Scene.NodeAdded`, `NodeRemoved`, `NodeTransformChanged`, plus a dynamic `SceneBvh`, `Scene.Raycast`, `QueryFrustum`, a `SelectionSet`, and `Camera.ScreenPointToRay` for screen picking.
- **The headline capability has no Studio equivalent.** The static world compiles asynchronously and chunked, and a single-brush edit costs ~0.05–0.09 ms independent of world size. Studio's `UnionOperation` is baked, capped, and cannot update while you drag. Spectra's carved world can.

What does **not** exist today, verified in the tree, and therefore shapes everything below: no scripting runtime of any kind (no `lua`/`luau` reference anywhere in the solution); no `Script` or `Entity` payload on `SceneNode`; no settable `Parent`, no `Destroy`, no `FindFirstChild`, no `IsA`/`ClassName`, no attributes, no tags, no `Clone`; no `CFrame` and no per-node signals; no scene serializer (the only `Serialize` hits in Core are D3D12 root signatures and the compiled-shader blob writer); and no per-part appearance — `Scene.StaticWorldMaterial` is one `Material` for the entire carved world and `VertexAttribute.StandardLayout` is 8 floats (position, normal, uv) with no colour channel, so two adjacent brushes cannot currently be different colours by any route. That last gap is owned by `ROADMAP.md` `F1`/`E7`, not by this plan, but it is the single most visible thing a migrating developer hits, so it is named here too.

This document also answers an open sign-off in `ROADMAP.md` §11 item 12 — *"Is C#-plus-rebuild acceptable for gameplay logic, or is a scripting VM eventually needed?"* The answer below is **a VM, and it is Luau**, which means `P4`'s `Entity` base class must be designed knowing a VM is coming.

---

## 2. The scripting decision

### 2.1 The recommendation

**Hybrid, weighted Luau-first.** Embed the real Luau VM as the gameplay scripting language; keep compiled C# as the second, engine-facing language for systems, importers, entity types and editor tooling.

Concretely, five commitments:

1. **Vendor `luau-lang/luau` (MIT) and own the interop.** One shared library per RID built from Luau.VM + Luau.Compiler (+ optionally Luau.CodeGen), bound with `[LibraryImport]` — a source generator, which the AOT rule explicitly permits, and the same shape as the Silk.NET native interop already used throughout `Graphics/`. Do **not** take a NuGet dependency: `nuskey8/luau-dotnet` was archived in June 2026 pointing at NuLua, and `NuLua.Luau` is a self-described v0.1.0 preview with one maintainer. Read their source as prior art — see (2), where it is not optional — but ship our own binding.

2. **The native shim is the ERROR BOUNDARY, not a P/Invoke convenience.** This is the correction that changes the shape of the work. Luau is C++: it raises errors as C++ exceptions (or `longjmp` under `LUA_USE_LONGJMP`), and `lua_error` is declared `l_noret`. **A C++ exception or a `longjmp` cannot legally unwind through a managed frame on CoreCLR or NativeAOT — that is undefined behaviour, not a leak.** Therefore:
   - No managed callback may raise into the VM. A `[UnmanagedCallersOnly]` callback (the interrupt watchdog, every `__index`/`__newindex` dispatch, every host function) returns `{status, message}` **by return value**; a native trampoline inspects it and issues `lua_error` from a native frame.
   - No raising API may unwind into managed code. Every inbound entry point that can raise (`luaL_check*`, `lua_gettable` with metamethods, `lua_pushlstring`/`lua_newuserdata` on OOM) gets a native `try`/`catch` wrapper that converts to a return code.
   - "Hand-write ~60 `[LibraryImport]` entry points against the raw C API" is therefore **not** the binding story, and the interop milestone is **large**, not medium.

3. **The VM is render-thread-only.** All `Scene` state is render-thread-only and lock-free by design; the Luau state object is not thread-safe either. Off-thread work never resumes on a pool thread — the host function returns immediately, the coroutine yields, and the render thread resumes it when the result lands. That is the same harvest pattern already proven three times in this codebase (`ProcessStaticWorldCompilation`, `AssetManager.PumpPendingUploads`, `ShaderHotReloader`). Parallel Luau (`task.desynchronize`) must **error** with a pointed message rather than silently no-op.

4. **Signals dispatch deferred only.** `Scene` carries an explicit, unenforced re-entrancy rule in its own source: *"handlers must not mutate the scene graph (add, remove, or reparent nodes) from inside an event — membership events fire in the middle of the ownership walk, and a structural edit there would corrupt the traversal."* A Luau handler that reparents a node inside `NodeAdded` would corrupt `SetOwner`'s walk. Deferring every script callback makes the engine-safe behaviour and the Roblox-forward behaviour the *same* behaviour, and converts a prose contract into a structural guarantee. Roblox has publicly stated `SignalBehavior.Default` will become equivalent to `Deferred`; use their documented re-entrancy depth limit of **10** rather than inventing a budget, and map onto their named resumption points rather than pre-declaring divergence.

5. **Infinite-loop protection ships in the first interop milestone, not later.** The VM runs on the render thread, and the render thread drives shutdown and the main loop's exit condition. One `while true do end` freezes the whole editor with no way out but killing the process. The `lua_callbacks(L)->interrupt` watchdog is the defence — and per (2), it must return a status rather than raise.

### 2.2 Why the AOT constraint does not block this

The engine mandates AOT compatibility: no reflection, no `dynamic`, no .NET runtime codegen; P/Invoke and compile-time source generators are the sanctioned escape hatches. It looks like that rules out "load and run a script the user edited". It does the opposite — it *decides* the question in Luau's favour.

Compiling a `.luau` file at runtime is **native codegen inside a native library**. It never touches `Reflection.Emit`, `MetadataUpdater`, or `Assembly.LoadFile`. So a NativeAOT-published Spectra game can still load, compile and run editable scripts. A compiled-C#-only model fundamentally cannot: NativeAOT forbids dynamic assembly loading and all runtime IL generation, so a C#-only game's behaviour is frozen at publish time — no mods, no user content, no post-ship script patch. Roblox developers live in a world where the game *is* the scripts; that asymmetry is the decisive argument.

**One honesty note on that argument**, because it is load-bearing and it is an inference rather than a stated requirement: nobody has asked for modding, user content, or post-ship patching. If the answer is "shipped games are frozen and that is fine", the argument weakens considerably (Luau still wins on familiarity and on Play/Stop teardown, but it stops being decisive). This is the first item in §5.

### 2.3 Rejected alternatives

- **Pure compiled C#.** The editor-time tension is softer than it looks — the editor is a JIT app, so Roslyn, collectible `AssemblyLoadContext` and Hot Reload are all legal *there*. It still loses. An incremental build plus assembly swap is seconds, not Roblox-instant. ALC unload is *cooperative*: one lingering delegate pins the context forever — and scripts in this codebase will subscribe to `Scene.NodeAdded`/`NodeRemoved`/`NodeTransformChanged` **by design**, so leaking the context is the default failure mode, not the exception. `MetadataUpdater.ApplyUpdate` cannot add types or change signatures and is feature-gated off under trimming/AOT. And the shipped AOT game gets zero script mutability. *Not fully rejected*: C# stays as the engine-facing language, and the compiled-game-assembly path is real work that should exist.
- **Managed Lua (`Lua-CSharp`).** Genuinely the best managed option — MIT, source-generated `[LuaObject]` bindings, no IL generation, AOT-clean by construction. But it is **Lua 5.2, not Luau**. A Roblox developer's muscle memory breaks on line one (`continue`, `+=`, string interpolation, type annotations, `--!strict`, the native `vector` type), and the entire Luau tooling ecosystem — `luau-lsp` with real type checking, `luau-analyze` — becomes unavailable. Keep it documented as the fallback if native Luau distribution ever becomes untenable on a platform we must ship to.
- **`NLua`/`KeraLua`.** Reflection-based interop. Banned outright by the AOT rule.
- **`luau-dotnet` / `NuLua.Luau` as a dependency.** Archived and preview respectively; the lineage churned once in two months. Making the engine's most important subsystem depend on that is a worse risk than owning ~1500 lines of interop we must own a cross-platform build for anyway (Linux is non-optional given the OpenGL/Uno-on-Linux path). Prototyping *against* NuLua to move fast and swapping later is a legitimate velocity call — see §5.
- **Writing a managed Luau interpreter.** Enormous, and the type checker and codegen are the parts we most want and least want to reimplement.

### 2.4 What a Roblox developer will still find worse than Studio

Ranked, to be documented rather than discovered:

1. **No physics, at all.** `Touched`, `Anchored`, `AssemblyLinearVelocity`, `Humanoid`, `Animator`, `TweenService` — none exist. Existing gameplay code does not partially port; it does not port. The BSP is already a query structure (`ContainsPoint`, `Raycast`), so a minimal trigger/overlap service answering `Touched`-shaped queries is closer than it looks and would disproportionately reduce this — `P8` in `ROADMAP.md` is that work under a different name.
2. **No multiplayer.** No `LocalScript` boundary, no `RemoteEvent`, no `Players`, no replication, no FilteringEnabled mental model. This is a Roblox developer's second question.
3. **No per-part colour or material yet** (verified: one world material, no vertex colour channel). Their *second action* after making a part is to colour it. Owned by `ROADMAP.md` `F1`/`E7`.
4. **No debugger initially.** Studio has breakpoints, stepping, watch and a call stack. `luau-lsp` with generated definitions covers a lot via autocomplete and `--!strict`, but the first misbehaving script means print-debugging. The C API has the primitives (`lua_singlestep`, `lua_breakpoint`, `lua_getlocal`), so this is scope, not capability.
5. **Stop is slower than Studio's instant Stop**, unless the transform-only restore path lands (see `O9`).
6. **Deferred-only signals diverge from Roblox's current `Immediate` default.** Code that fires an event and reads the result on the next line behaves differently by one resumption point.
7. **No dynamic child indexing.** `workspace.Baseplate.Part` is impossible without reflection or `dynamic`. `FindFirstChild`, tags and `Guid`-based refs are collectively *better*, but this belongs at the top of the migration guide, not buried in it.
8. **Two languages is a cliff Studio does not have.** Outgrowing Luau means a .NET SDK, a C# project, and an edit-build-reload loop. The transition is a step function, not a ramp.

---

## 3. Milestones

Ten milestones, in dependency order. `O0`–`O3` are pure C# engine work with no Luau dependency and can start immediately. `O4` onward is the Luau arc.

---

### O0 — Prove one AOT publish of the engine as it stands — **S**

**Scope.** Publish `SpectraEngine.Executable` with NativeAOT, run the demo on all three backends, record every trim/AOT warning as an inventory. Add `<IsAotCompatible>true</IsAotCompatible>` to `SpectraEngine.Core` so the library is analysed on *build*, not only when an exe publishes.

**Corrected premise — the tree is better off than a first read suggests, and worse in a specific place.** `<PublishAot>true</PublishAot>` is *already set* on `SpectraEngine.Executable/SpectraEngine.Executable.csproj` (line 5) and on `SpectraShade.Compiler.CLI/SpectraShade.Compiler.CLI.csproj` (line 8). So the SDK's AOT/trim analysers have been running on those projects' builds all along — the constraint is partly machine-enforced already, and `IsAotCompatible` on Core is the missing third piece.

**The concrete predicted failure.** Silk.NET 2.23 discovers its windowing and input platforms by reflection, which trimming removes; under NativeAOT it requires explicit `GlfwWindowing.RegisterPlatform()` and `GlfwInput.RegisterPlatform()` calls. **Neither string appears anywhere in the solution** (verified by grep over all `.cs`). `Engine.cs:97` calls `Window.Create(options)` and `Engine.cs:109` calls `_window.CreateInput()`. Prediction: the publish *compiles clean and then dies at `Window.Create` with no platform registered.* Known first fix: add both registration calls before window creation. Secondary suspects, in order: `Silk.NET.Assimp` (native + managed wrapper, historically needs trimming hints — referenced in Core but no importer code exists yet, so it may simply be droppable), `Silk.NET.Direct3D.Compilers`, and `Serilog`'s configuration surface in the executable.

**Depends on** — nothing. **Ship first.**

**Risk** — MEDIUM-HIGH that it does not work on first attempt, and that is exactly why it goes first. Every argument in §2 rests on the AOT premise. If AOT turns out to be unreachable for the engine as it stands, the compiled-C# option's main disadvantage evaporates and §2 deserves re-litigating **before a line of interop is written**.

**Deliverable** — a yes/no and a warning inventory. No scripting code at all.

---

### O1 — The value-type layer: `Vector3` policy and `CFrame` — **M**

**Without this, no Roblox code ports at all.** `part.CFrame = CFrame.new(0,10,0) * CFrame.Angles(0, math.pi/2, 0)` is beginner-level Roblox and today has no landing surface — there is no `CFrame` in the tree and no decision about how `Vector3` crosses into script. This is why it is second, not late.

**Scope.**
- A `CFrame` readonly struct (`Vector3 Position`, `Quaternion Rotation`) beside `System.Numerics`: constructors; statics `Identity`, `LookAt`, `LookAlong`, `Angles(rx,ry,rz)` (radians — degrees in a radians codebase is a footgun, and this is one of the enumerable differences), `FromAxisAngle`, `FromMatrix`; `LookVector`/`RightVector`/`UpVector`; `Inverse`, `Lerp`, `ToWorldSpace`, `ToObjectSpace`, `PointToWorldSpace`/`PointToObjectSpace`, `VectorToWorldSpace`/`VectorToObjectSpace`, `Orthonormalize`, `ToMatrix()`, `TryFromMatrix`; `operator *` for `CFrame×CFrame` and `CFrame×Vector3`.
- On `SceneNode`: `LocalCFrame` (get/set) and world-space `CFrame` (get/set, back-solving `local = parent.Inverse() * world`).
- **The Luau-side policy, decided here even though it is implemented in `O5`:** bind `Vector3` to Luau's **native vector value type** (3-wide by default), which is allocation-free and is the same mechanism Roblox migrated `Vector3` onto. `CFrame` has no native analogue and must be userdata — which makes `CFrame` the real hot-path cost and the thing to benchmark, not `Vector3`.

**Why `CFrame` earns its place beyond familiarity.** It makes rigidity **structural instead of validated**. A `CFrame` cannot represent scale, so `node.CFrame = x` cannot produce the non-rigid world matrix that the snapshot's rigidity check exists to reject. Today a scale write on a brush node is accepted at the setter and rejected three stages later at snapshot time — which freezes the static world at its last good compile behind a single log line. That is the worst possible failure shape for a migrating developer reaching for `Scale`.

**Depends on** — nothing. Pure addition; the existing transform setters already early-out on equal writes and route dirtying correctly, so `CFrame` writes inherit that for free.

**Risk** — MEDIUM, and it is silent. Composition order and handedness must match Roblox exactly or every ported script is subtly mirrored. Two named traps: (a) Spectra composes row-vector (`world = local · parent.World`) while Roblox `CFrame` is column-vector, so `operator*` must be defined by its semantic contract — `Apply(a*b, v) == Apply(a, Apply(b, v))` — and **not** by transcribing a matrix product; (b) `System.Numerics.Quaternion.Concatenate`'s argument order is a known confusion and must be pinned by a test, not by memory. **Mitigation with unusual leverage: a Roblox Studio MCP is available in this environment.** Generate a ground-truth table of `CFrame` results from live Studio and snapshot-test against it — that converts the highest-risk item in this arc into a mechanical check.

---

### O2 — The familiarity API on `SceneNode`/`Scene` — **M**

**Scope.** The Roblox surface as a thin, honest layer over the graph. Do not restructure the engine to imitate Roblox and do not rename `SceneNode` to `Instance` — Roblox is already PascalCase-methods, so ~90% of the surface (`FindFirstChild`, `GetChildren`, `GetDescendants`, `Clone`, `Destroy`, `IsA`, `WaitForChild`) transfers character-for-character.

- **Settable `Parent`** delegating to the existing `AddChild`/`RemoveChild` machinery unchanged — plus three guards that do not exist today: self/cycle, `Root`, and destroyed. **`AddChild` has no cycle check today**, and `MarkWorldDirty()` recurses the whole child list, so a parent loop is an unbounded recursion. It is currently hard to reach; a settable `Parent` makes it one keystroke away. This milestone is partly a bug fix.
- **`Destroy()`** firing `Destroying` subtree-pre-order *before* detach (so handlers can still read position and ancestry), then detaching through `RemoveChild`, then setting a `Destroyed` flag that makes further mutation throw. It must **not** destroy GPU meshes — those are renderer-owned shared assets with no refcount.
- **Query surface**: `FindFirstChild(name, recursive)`, `FindFirstChildOfClass`, `FindFirstChildWhichIsA`, `FindFirstAncestor*`, `GetChildren`, `GetDescendants`, `IsAncestorOf`/`IsDescendantOf`, `GetFullName`.
- **`IsA`/`ClassName` backed by a hand-registered `NodeClassRegistry`** (className → base className, optional factory), never by C# subclassing and never by reflection. There is exactly one concrete node type and payloads are optional fields, so there is no hierarchy for `typeof` to query — and a string class table is simultaneously Roblox's model and the FGD model `P5` needs.
- **`Clone()`** returning a **parentless** deep copy: fresh `Guid` per node, a **duplicated** `Brush` (a `Brush` instance must never be shared — the carve cache keys on brush *reference* identity, first-placement-wins, so sharing one instance across N nodes silently condemns N−1 of them to a permanent cache miss on every compile), a **shared** `Mesh`, and a **cloned** `Material` (`Material` has no `Clone` today — verified). Parentless is not cosmetic: cloning 500 parts then parenting the root once costs one structural dirty rather than 500.
  - The duplicated brush needs a **validation-free private copy constructor** lifting the already-validated immutable arrays by reference. `new Brush(planes)` runs an O(n²) duplicate-plane rejection *and* builds faces twice (once real, once as the boundedness probe) — fine once, not fine per clone or per drag frame.
- **`Size`** (full extents, `Part.Size` semantics): getter from `Brush.LocalBounds.Size`; setter producing a new `Brush`. **Build this on `E4`'s non-throwing plane-derivation API rather than inventing a second one** — but note that `E4`'s soundness argument covers *offset-only* derivation, and a diagonal-rescale `Size` setter needs its own (a strictly positive diagonal scale maps a bounded polytope to a bounded polytope and cannot make two distinct planes coincident, so the validation-free path is sound — but say so in a comment, do not assume it).
- **`LocalScale` should throw on brush-bearing subtrees**, not be rejected downstream. Failing at the write is both better engineering and exactly what a Roblox developer needs to be told: *use `Size`, not scale*.
- **`WaitForChild`** returns synchronously when the child exists and **throws a self-explaining exception** when it does not — it never blocks, because blocking on the render thread deadlocks the frame. Roblox's `WaitForChild` exists almost entirely because of replication, and Spectra has none, so cloning its infinite-yield behaviour would import Roblox's single worst debugging experience for a problem that is absent. The genuine async case gets `WaitForChildAsync`. *Note the legitimate two-language split: in Luau, `WaitForChild` **can** be a real coroutine yield, because a suspended coroutine does not block the render thread — the scheduler simply does not resume it this frame.*

**Depends on** — `O1` (the transform surface), and `ROADMAP.md` `F2` for `Scene.TryFindById(Guid)` and `NodeRenamed`. Do not build a second Guid index.

**Risk** — MEDIUM. `SceneNode` is the hottest, most-constructed type in the engine and this milestone edits its lifecycle paths. Two specific hazards: `AddChild` and `RemoveChild` order `MarkWorldDirty` against `SetOwner` **oppositely** (add dirties first so `NodeAdded` handlers read correct world matrices; remove clears the owner first), so a `Destroying` handler that reads world bounds observes stale matrices on the removal path — align the ordering deliberately here rather than inheriting the asymmetry. And per ruling `R‑9`, do not land this concurrently with `E4`/`E6`/`P7`, all of which do surgery on the same counters.

---

### O3 — Attributes, tags, and the deferred signal system — **M**

**Scope.**
- **One keyvalue mechanism for both Roblox Attributes and Source entity keyvalues.** They are the same feature wearing different hats: named typed values on a tree element, driving both property panels and gameplay logic. Building two would be a design failure. A closed `AttributeValue` readonly-struct discriminated union over Roblox's supported type set (Bool, Int, Double, String, Vector2, Vector3, Color3, CFrame, NumberRange…) **plus a `NodeRef` type Roblox lacks**, wrapping the node's existing `Guid`. Typed setter overloads so call sites never box; a lazily allocated dictionary per node so a 50k-part world pays one null field.
  - `NodeRef` is the important addition. Roblox has no attribute type for instance references and forces developers into `ObjectValue` child instances — a workaround, not a feature to copy — while the no-code entity wiring in `P4`–`P6` is unusable without it. Storing a `Guid` survives `Destroy`, serializes trivially, and is exactly what `Clone`'s internal-reference remap needs.
- **Tags** (the `CollectionService` equivalent): per-node lazy tag list, `Scene` reverse index, `GetTagged`, `TagAdded`/`TagRemoved`, and **`ObserveTag(tag, onAdded, onRemoved)` that replays already-tagged nodes before connecting** — killing the `GetTagged`-loop-next-to-every-connect boilerplate every Roblox developer writes. A small divergence that is strictly better.
- **`Signal`/`Signal<T>`** with `Connect` → handle, `Once`, `WaitAsync`, and a C# `event` accessor for `+=`. **One lazily allocated `NodeSignals?` field** on `SceneNode`, not six field-like events — six reference fields is ~48 bytes on every node whether observed or not, i.e. ~2.4 MB across the 50k-part world the benchmark guards. Per-node signals: `ChildAdded`, `ChildRemoved`, `DescendantAdded`, `DescendantRemoving`, `AncestryChanged`, `Destroying`, `Changed`, `GetPropertyChangedSignal`.
- **A scene-owned, time-ordered deferred queue** keyed on `(DueTime, Sequence)` with a monotonic sequence counter, drained once per frame — the total order is what makes the whole thing deterministic for equal edit histories, matching the determinism stance the CSG pipeline already takes. Cap cascade depth and log a diagnostic **naming the offending signal**, or the first mutual relay someone builds hangs the render thread with no clue why.
- **A render-thread dispatcher** (`Post`/`Pump`, exposed as a `SynchronizationContext`) — the same shape as the existing hot-reload queue and `PumpPendingUploads`. This is what makes `WaitForChildAsync` and deferred graph mutation from inside a handler legal, and the Uno editor's UI thread will need exactly the same queue, so design for both consumers at once.

**Signal fan-out at scale is a real hazard and must be designed for, not discovered.** `Changed`/`GetPropertyChangedSignal` want to build on `NodeTransformChanged` — but that event fires for **every owned node on every actual transform change**, deliberately (its own doc comment says *"fires for every owned node, brush-bearing or not"* because editors track cameras and props too). A naive bridge does a subscription lookup per moved node per frame on the render thread before it can decide there is nothing to queue. With thousands of movers that is a per-frame hash-probe storm in the hot loop. **Fix: a per-node has-subscribers bit checked before any map lookup**, and the `DescendantAdded`/`Removing` ancestor walk gated by a descendant-observer count on the ancestor chain — the same trick `_subtreeBrushCount` already uses.

**Depends on** — `O2`.

**Risk** — MEDIUM. The re-entrancy collision is head-on: parenting things inside `ChildAdded` is *routine* Roblox practice and is exactly what `Scene`'s stated contract forbids. Either the dispatcher's deferred-mutation queue lands with this milestone, or ship with a debug-build re-entrancy assertion that fails loudly instead of corrupting a traversal. Do not ship per-node signals with neither.

---

### O4 — Native Luau: vendoring, the shim, and the error boundary — **L**

**Scope.** Vendor `luau-lang/luau` as a submodule. CMake build of Luau.VM + Luau.Compiler behind a flat-C shim into one shared library per RID. `[LibraryImport]` bindings. A `LuauHost` owning state creation, `luaL_sandbox` + per-*script* `luaL_sandboxthread`, bytecode compile, `lua_pcall`-only invocation, the `lua_callbacks` interrupt watchdog, and a structured `IScriptOutput` sink.

**The shim's contract, restated because it is the milestone's actual content:** native trampolines invoke managed callbacks and take `{status, message}` by return value, then issue `lua_error` themselves from a native frame; every raising inbound entry point gets a native `try`/`catch` converting to a return code. No C++ exception and no `longjmp` ever crosses a managed frame. Reading `NuLua`/`luau-dotnet` source is **mandatory prior art** here — they had to solve this exact problem, which is the real reason to read them.

**Output is structured, not formatted text.** `{Severity, Message, ScriptNodeId, Line, Timestamp, StackFrames}` — the payload is what makes an Output panel double-clickable-to-line rather than a log dump. Two sinks from day one: an `ILogger`-backed one that works *today* (`ILogger` is already threaded through `Engine`, `SceneManager`, `InputManager`), and an in-memory ring buffer the future editor binds to. This decouples error surfacing from the Uno arc entirely.

**Sandboxing is mistake-containment, explicitly not an adversarial boundary.** `luaL_sandbox` makes libraries and builtins read-only and enables `safeenv`; `luaL_sandboxthread` gives each script a proxy `_G`. That is Roblox's actual model. Remove `io`, `os.execute`, `package`, `loadstring`; redirect `require` to ModuleScript-node semantics, never disk. A P/Invoke boundary into a native VM is not a hostile-code boundary, and implying otherwise invites someone to ship user-generated scripts on a false assumption.

**Depends on** — `O0` (informationally; its result may redirect everything). Otherwise independent of every engine subsystem.

**Risk** — HIGH, and it is front-loaded rather than deferred, which is correct. The cross-platform native build is the whole risk, and the part that actually bites is the one usually left out: **Luau needs C++11 for the VM and C++17 for compiler/analysis, so the shipped shared library carries a C++ runtime dependency** (`libstdc++`/`libc++`) and a static-vs-dynamic linking decision per RID. That is the Linux distribution problem, and Linux is non-optional given the OpenGL/Uno-on-Linux path. This repo has no precedent for shipping native assets at all. Secondary: including Luau.CodeGen roughly doubles the platform build complexity — see §5.

**Shippable proof.** A `.luau` file on disk prints, warns, errors with a file:line traceback, and an infinite loop is interrupted rather than hanging. **No scene access whatsoever.**

---

### O5 — Node handle bridge, generated bindings, `spectra.d.luau` — **L**

**Scope.** Nodes cross into Luau as **tagged full userdata** holding a `(slot, generation)` handle into a host-side table — never a raw pointer, never a reflection-driven wrapper. The generation counter is what makes `Destroy` safe: a stale handle fails the check and errors with *"attempt to index a destroyed node"*, which is precisely how a destroyed Roblox `Instance` behaves, with no leaked references and no dangling managed roots. Cache one handle per node so identity comparison works — `part.Parent == workspace` must be true, and Roblox developers write that constantly.

Property dispatch is a **generated switch on interned names**. One source generator emits the C# binding, the `__index`/`__newindex` switch, and the `spectra.d.luau` definition file from one source of truth — which is what makes `luau-lsp` autocomplete and `--!strict` checking work against Spectra out of the box via its `--definitions:@name=PATH` mechanism. Hand-written bindings drift from the C# API within weeks, and a drifting definition file is *worse* than none, because autocomplete then lies.

Also here: `O1`'s value types bound for real — `Vector3` on Luau's native vector, `CFrame` as userdata with arithmetic metamethods — and the globals `game`/`workspace`.

**Depends on** — `O4`, `O2`, `O1`.

**Risk** — MEDIUM technically; the design risk is larger and it is a naming lock. **Roblox's `Position` is world-space; Spectra's node transforms are local and hierarchical.** This is the highest-traffic property in all of Roblox scripting and the divergence is *silent*: code compiles, type-checks, and does the wrong thing under any non-identity parent. It must be locked before anyone writes content against it (§5). Note this is the *second* design lock of this arc, not the first — the value-type layer in `O1` is first, because without it there is nothing to name.

**Do not create a second analyzer project.** `ROADMAP.md` `P5` creates a `netstandard2.0` Roslyn incremental generator with all the same setup pain (Roslyn version pinned at or below the SDK's, `PrivateAssets=all`/`OutputItemType=Analyzer`, central package management, `TargetFramework` set globally by `Directory.Build.props`). Whichever of `O5`/`P5` lands first pays that cost once and the other reuses the project.

---

### O6 — The Command Bar — **S**

**Studio's tightest iteration loop is not Play — it is the Command Bar.** Type Luau, press Enter, it executes immediately against the live **edit-mode** scene with no Play cycle at all. Plugins run in edit mode too. Any plan that is Play-only measures itself against the wrong baseline and its honest iteration comparison against Studio is worse than it claims.

**Scope.** One editor-owned `lua_State`, `lua_pcall` against the live scene, output into the `O4` sink. Multi-line entry, history, and — because the whole point is the live graph — the selection exposed as a global so `for _, p in selection do p.CFrame = ... end` works. Edit-mode script execution becomes a first-class concept rather than an afterthought.

**Depends on** — `O4`, `O5`. Nothing else. It is one `lua_pcall`.

**Risk** — LOW. The one real hazard is that command-bar mutations must be undoable like any other edit, so route structural changes through `E1`'s command queue once that exists; before then, accept that command-bar edits are outside history and say so.

**This delivers more of the pillar per line than anything else in this arc.** Ship it as soon as `O5` exists, not after `O8`.

---

### O7 — `Instance.new` and the Part-vs-Brush runtime split — **L**

**The design that only optimises scripted brush *moves* is optimising the wrong operation.** `Instance.new("Part")` is the most common script operation in all of Roblox, and it falls off the incremental compile path **by construction**. Verified in `SceneNode.cs`: the `Brush` setter takes `if (had != has) Owner?.MarkStaticWorldDirty();` on attach or detach — because attach/detach changes the placement *count*, which shifts every later slot, so the retained snapshot cannot patch it. `MarkStaticWorldDirty` sets `_snapshotForceFull = true` and the next compile takes the O(world) full-walk path. **A spawn loop therefore full-recompiles the world on every frame it runs**, and the world-size-independence pillar does not cover it.

**The deeper point: "CSG brushes are a superset of Part plus UnionOperation" is true for AUTHORING and false at RUNTIME.** A script-spawned Part in Roblox is a dynamic object, not world geometry.

**Scope.**
- A **dynamic-part path**: a `MeshRenderer` node carrying a box mesh, outside CSG entirely. It inherits the existing BVH insertion, frustum culling and draw path with **zero new render code**, and moving it is a matrix write with no recompile.
- A rule for which one `Instance.new("Part")` yields, keyed on mode: authored/edit-mode parts are brushes; parts created during Play are dynamic. (This is a sign-off — §5.)
- The `NodeClassRegistry` factory path from `O2` becomes the construction-by-name mechanism, which deserialization needs anyway.

**Build this on `P7a`'s admission bit, not beside it.** *(This paragraph said `P7`'s counter split; the mechanism moved and got a milestone of its own.)* `ROADMAP.md` **`P7a`** — designed in [`docs/physics.md`](physics.md) §2.3a — ships `SceneNode.BrushKind { World, Part }`, the `IsStaticWorldBrush` predicate the snapshot filters on, and the counter as **two lanes in one field with one writer** (not a split producing a second counter: the *total* lane stays, because `ScaleGizmo` reads it to refuse scaling a group node with brush descendants). That is *exactly* the mechanism a Static/Movable part split needs, and `O7` must reuse it rather than build a parallel Static/Movable invariant — two independently-maintained subtree invariants of this shape is how silent corruption happens. Two consequences for this milestone specifically:

- **`P7a` is cheaper to wait for than `P7` was.** It needs only `F1` — no entity system, no `P4` — so `O7` is no longer transitively blocked on the entity arc.
- **It also gives `O7` a second, better answer for a spawned part.** A `BrushKind.Part` brush renders its own faces and moves at zero recompile cost, so *"parts created during Play are dynamic"* need not mean *"parts created during Play are `MeshRenderer` boxes"*. The `MeshRenderer` path stays right for an imported mesh; the part-brush path keeps planes, per-face materials and a hull. Which one `Instance.new("Part")` yields is still the §5 sign-off, and `physics.md` §2.3a explicitly declines to answer it — but the sign-off now has two viable answers rather than one, and the **enum spelling** collides with it: `Part` would name two engine representations behind one Luau word.

`P7a`'s own named hazard is inherited verbatim: forgetting to gate the `Brush` setter's `MarkStaticWorldDirty` (`SceneNode.cs:140`) makes a spawn loop full-recompile the world every frame it runs, exactly as the paragraph above this one describes, **while everything still renders correctly**.

**Depends on** — `O5` (so the binding surface exists to expose it), **`P7a`** (not `P7`, which no longer owns the mechanism). Independent of `O8`/`O9`.

**Risk** — HIGH. It is the first scripting feature that changes *what enters the static placement list*, and that list is guarded by the chunked-vs-monolithic equivalence oracles and the bit-identical determinism tests. Mitigating: a filtered list is just a smaller list, so no oracle should need changing — but per §12 of `ROADMAP.md`, show it, do not assume it, and add a `CsgBench` scenario that spawns and destroys N dynamic parts per frame and re-asserts the *world-size independent* verdict line.

---

### O8 — Script payload, lifecycle, and the frame-phase pump points — **M**

**Scope.**
- **A script is a NODE with a `Script` payload** (`SceneNode.Script`), not a component bolted onto the node it drives — so `script.Parent` is literally the parent node, exactly as in Roblox. This falls out for free: the BVH only tracks nodes carrying `MeshRenderer` or `Brush`, so a script-only node is automatically invisible to the spatial index and irrelevant to the static world. The setter is the simplest of the payload setters — assign, notify the owner — and must **not** call the spatial-component hook or `MarkStaticWorldDirty`. **`Script` carries exactly one axis: `bool IsModule`** (Roblox's `ModuleScript` versus everything else). `Script.Disabled`. Module `require` caching keyed on `SceneNode.Id` (stable across reparent and rename), so a moved module does not re-execute — matching Roblox.
  - *Superseded, 2026-08-21 — this bullet previously specified `ScriptKind { Server, Client, Module }`, shipping Server + Module and "reserving Client so the naming never has to change".* **That reservation is spent, not honoured.** `docs/realms.md` §6.2 and R11 collapse run location into the node's `Realm`: a client script is a script node declared `Realm = "Client"`, a server script is one that resolves to `Server`, and there is no second script *type* to select — so `ScriptKind` loses both `Server` and `Client` and what remains is the `IsModule` flag above. `docs/networking.md` §8 `N15` and its item 4 record the same correction from the other side. Design authority: `realms.md`.
  - *Note against ruling `R‑6`, with the count stated exactly:* `SceneNode` carries **two** payloads today — `Brush` (`SceneNode.cs:116`) and `MeshRenderer` (`:82`) — and `Entity` (`ROADMAP.md` `P4`–`P9`) is designed but unbuilt, so this milestone proposes the **fourth designed payload and the third one nobody has written yet**, not a fourth existing field. `docs/data-model.md` §3 and §6 are the counting authority; keep them in step. That is consistent with `R‑6`'s intent (composition, not subclassing, no polymorphic node types), but the payload count is a real cost on the hottest type in the engine and should be a conscious acceptance, not a drift — which is exactly the argument `docs/physics.md` §2.6 then uses to refuse a *fifth* by keeping physics bodies in a side table behind a `PhysicsFlags` bit.
- **Three pump points, not one.** This is the correction that matters. The frame body today is: `_sceneManager.Update(...)` at `Engine.cs:216` → `ProcessStaticWorldCompilation(...)` at `:222` → `PumpPendingUploads()` at `:227` → `BuildRenderView(...)` at `:262` → `Present(...)` at `:277`. A single `Step` call cannot serve every phase: `PostSimulation` (Heartbeat) is the workhorse where essentially all real Roblox gameplay code lives, and if it runs *after* the compile pump then **every scripted brush move written in Heartbeat is exactly one frame late** — the precise failure that placing the step early was supposed to prevent. So:
  - `PreSimulation` and `PostSimulation` both drain **between `:216` and `:222`**, so a scripted brush move rides the same ~0.05–0.09 ms incremental path as an editor drag with no extra frame of latency.
  - `PreRender` drains **immediately before `:262`**, so camera writes land the same frame.
  - `PreAnimation` is deliberately not shipped — there is no animation system, and a signal that fires at a semantically meaningless time is worse than an absent one, because people build ordering assumptions on it.
  - Document that Spectra's phases are **compile-relative rather than physics-relative**, since there is no physics. Ship Roblox's current names with `Heartbeat`/`Stepped`/`RenderStepped` as working aliases — a 2026 developer sees the new names in autocomplete, but every tutorial and every existing codebase uses the old ones, and Roblox itself keeps both working.
- **Scripts receive the clamped `deltaTime`** (`MaxDeltaTime = 0.1` at `Engine.cs:22`, applied at `:209`), never `rawDelta`. The engine already establishes that a long CSG stall must not teleport the fly camera; scripted movers inherit exactly that reasoning.
- `task.wait`/`spawn`/`defer`/`delay`/`cancel` on the `O3` queue; per-script thread creation with `luaL_sandboxthread` and `lua_setthreaddata` so `script` and `script.Parent` resolve.

**Depends on** — `O5`, `O3`, `O7`.

**Risk** — MEDIUM. Verify *empirically* that a script-driven brush move lands in the same-frame incremental path and does not trip the structural fallback. Secondary: per-script `lua_State` plus a `lua_ref` per connection means thousands of registry entries and threads at scale — cost it rather than asserting it is fine.

---

### O9 — Play / Stop — **M**, and honestly gated

**Scope.** `EditorMode { Edit, Play, Paused }`. On Play: a fresh `lua_State` (one `lua_close` and one new state is a deterministic, total teardown of every coroutine, ref and connection — which is *precisely* the thing a collectible ALC cannot guarantee) plus a state capture. On Stop: restore, discard the VM.

**The plan this replaces, and why.** An earlier design had Play/Stop **serialize** the authored graph using the scene file format. That plan has no substrate: there is **no scene serializer in the tree** — verified, the only `Serialize` hits in Core are D3D12 root-signature serialization and the compiled-shader blob writer — and `ROADMAP.md`'s `P2` schedules the first one. Worse, its stated benefit inverts the risk: *"Play/Stop continuously exercises the save/load path so serialization bugs surface on every playtest"* means **every serializer bug becomes destruction of the user's authored scene on Stop.** Studio restores from an in-memory snapshot specifically so that stopping a playtest cannot eat your place file. Making Play/Stop the serializer's first consumer is the highest-blast-radius way to test it.

**So: an in-memory structural snapshot of the node graph and brush plane arrays.** It is cheap precisely because `Brush` is immutable — the snapshot holds brush *references*, not deep copies.

**And Stop must not pay a cold full recompile.** Restoring the graph by adding and removing nodes bumps the graph-structure version, which routes through `MarkStaticWorldDirty` → `_snapshotForceFull = true` and breaks the carry pairing (the compile's `orderStable` check compares the carry's structure version against the current one). Every Stop would then pay an O(world) re-walk plus a cold full carve — never the incremental path — and it would grow with world size. Studio's Stop is instantaneous; this would be felt immediately as *worse than Studio*. **Mitigation: if Play performed no STRUCTURAL brush edits, restore transforms in place through the `LocalTransform` setters**, which route through the node-scoped brush dirtying (the incremental path) and never touch `MarkStaticWorldDirty`. Detect the structural case and fall back only then.

**Depends on** — `O8`, and `ROADMAP.md` `P11a`. **`O9` is the scripting half of `P11a`, not a second play/stop.** `P11a` owns mode switching, the history barrier and diff-restore of the graph; `O9` adds VM teardown/recreation and the structural-vs-transform-only restore discrimination. They should land together or `O9` after, never in parallel.

**Risk** — MEDIUM, plus a scheduling dependency outside this arc. Also inherits `ROADMAP.md` §11 sign-off 5 (diff-restore vs fresh scene) unchanged — that decision is upstream of this milestone, not made by it.

**One honest correction to a premise.** *"Studio does not hot-apply script edits during a playtest"* is Studio's **default**, not an invariant — Studio Settings has an "Always Save Script Changes" option that persists edits made while play-testing. The conclusion still holds (a fresh state per Play is a faithful reproduction of the default loop); the premise just needs stating as a default rather than a guarantee.

---

### Deferred, named so they are not forgotten

- **`luau-lsp` integration** — wire the `O5`-generated `spectra.d.luau` via `--definitions:@name=PATH`, plus `luau-analyze` in CI so type errors fail the build. **No editor dependency at all** — it ships against VS Code immediately and gives external-editor users a real workflow. Do this the moment `O5` lands; it is nearly free.
- **Output panel** — the ring-buffer sink already exists from `O4`; only the panel is blocked, on `H1`.
- **Luau debugger** — `lua_singlestep`/`lua_breakpoint`/`lua_getlocal`. **L**, self-contained, and it has one trap worth writing down now: a breakpoint halts the render thread, which drives shutdown and the main loop's exit condition, so a naive stop-the-world breakpoint freezes the editor exactly like the infinite loop `O4` defends against. The debugger must break into a pump loop that keeps presenting frames, not a blocking wait.
- **A minimal trigger/overlap service** giving scripts a `Touched` equivalent. The per-cell BSP already answers `ContainsPoint` and `Raycast`; `ROADMAP.md` `P8` is this work.
- **The compiled-C# game-assembly path** — a game project template, and a registration *scope* object that owns and reverses every engine-event subscription. That constraint must be decided before the C# extension API has users; retrofitting it later breaks every extension written against it.

---

## 4. How this interleaves with `ROADMAP.md`'s critical path

`ROADMAP.md`'s shortest path to a usable editor is `F2 → E1 → E2 → E3`, `F1 → E7`, `E4`, `E6`, `P2`, `P11a`. This arc's relationship to it:

**Runs fully in parallel with the editor arc — start now:**
- **`O0`** touches only project files and (predicted) two registration calls in `Engine.cs`. Zero collision. It should go first regardless of anything else here, because two shipped projects already claim AOT and nobody has proven it.
- **`O4`** (native Luau + shim) is entirely outside `SpectraEngine.Core`. It shares no file with any roadmap milestone. It is the longest pole in this arc and it is unblocked today — start it early precisely because nothing gates it.
- **`O6`** (Command Bar) once `O4`/`O5` exist.

**Must be sequenced against the editor arc, not run beside it:**
- **`O1`, `O2`, `O3`** all edit `SceneNode`'s lifecycle, counters and setters — the same surgery as `E4` (brush resize), `E6` (structural commands), `P7a` (`BrushKind` and the two-lane counter) and `P7` (entity brush ownership). Ruling `R‑9` gives `P7`/`P7a` a quiet tree; extend that to this group. Sequence: `F2` first (it is small and everything wants its Guid index and `NodeRenamed`), then either the `E` group or the `O1`–`O3` group, not both.
- **`O2`'s `Size`** should be built on `E4`'s derivation API, so `E4` first.
- **`O5`'s binding generator** and **`P5`'s entity generator** are the same analyzer project. Whichever lands first creates it.
- **`O7`** must be built on **`P7a`**'s `BrushKind` bit and two-lane counter, so `P7a` first — **not `P7`**, which no longer owns that mechanism and which drags in the whole entity arc. Do not build a parallel Static/Movable invariant.

**Genuinely blocked, and honest about it:**
- **`O9`** waits on `P11a`, which waits on `E6`. It does *not* wait on `P2` and must not be allowed to acquire that dependency.
- The **Output panel** waits on `H1`; the `ILogger` sink in `O4` is exactly what keeps error surfacing from being blocked on the Uno arc.

**Where this arc changes a roadmap decision:** §11 item 12 asked whether a scripting VM would eventually be needed and noted it *"must be decided before `P4` hardens"*. §2 answers yes. So `P4`'s `Entity` base class should be shaped knowing that (a) signals will be deferred through one queue, which is what `P4` already plans, (b) `[Action]`-dispatch and property access will need to be reachable by *name* from a script, which the generated switches in `P5` provide for free, and (c) `Script` is a payload, so a node can carry both an `Entity` and a `Script`.

---

## 5. Decisions that need you

Each of these blocks something concrete. They were all open when this document was written; an item that a later document has since answered is **marked SETTLED in place, with the answer and its owner**, rather than deleted — a question that silently disappears reads as an oversight, and the record of what decided it is the useful part.

1. **Must a shipped, AOT-published game be able to load and run editable scripts (mods, user content, post-ship patching)?** This is the single argument that makes Luau *decisive* rather than merely preferred — and nobody has actually asked for it. If shipped games are frozen and that is fine, the case weakens to familiarity plus teardown, which is still a good case but a different one. Answer before `O4`.

2. **Does the AOT constraint bind the EDITOR, or only shipped games?** If editor = JIT, the compiled-C# reload path is legal there and C# stays a real second language. If the editor must also be AOT-published, that path is impossible and Luau becomes mandatory rather than preferred. `CLAUDE.md` does not distinguish, and the distinction is load-bearing.

3. **`Position`: Roblox parity (world-space, with `LocalPosition` beside it) or engine clarity (local, with `WorldPosition` beside it)?** The pillar argues parity; engineering argues clarity; the wrong choice fails *silently* under any non-identity parent. Must be locked in `O5`, before any content is written against it.

4. **What does `Instance.new("Part")` produce during Play — a dynamic `MeshRenderer` node outside CSG, or a brush?** Brush means a full-world recompile per spawning frame. Dynamic means script-spawned parts do not carve, which is a real semantic difference from authored parts. Blocks `O7`.

5. **SETTLED 2026-08-21 — multiplayer is in the plan, on server-authoritative state replication with client prediction.** *(Was: is multiplayer in the eventual plan, and on what model? It decides whether `ScriptKind.Client`, `LocalScript` and a `RemoteEvent`/`Players` surface are real designs or placeholders.)* [`docs/networking.md`](networking.md) answers the model and owns the `N*` arc; `RemoteEvent`/`Players` are real designs there (`N15`). The `ScriptKind.Client` half of the question is answered *against* the reservation: [`docs/realms.md`](realms.md) §6.2 puts run location on the node's `Realm`, so a client script is a node declared `Realm = "Client"` and `ScriptKind` collapses to `bool IsModule` — see the correction under `O8` above. **Nothing in this list still turns on it.**

6. **Own the interop from day one, or prototype against `NuLua.Luau` and swap?** Owning it is the risk-correct call (the lineage archived once already, two months ago) but it front-loads a cross-platform native build *before any scripting value is demonstrable*. This is a velocity-versus-risk call, not a technical one.

7. **Ship Luau.CodeGen (the native JIT) in `O4`, or interpreter-only first?** CodeGen sidesteps the .NET AOT rule entirely — it is native codegen inside a native library — and makes `--!native`/`@native` work exactly as a Roblox developer expects. It also roughly doubles the native build's per-RID complexity for performance nobody has yet demonstrated a need for.

8. **Where does script source live — `.luau` files on disk, or embedded in the scene file?** Files are git-friendly, `luau-lsp`-friendly and hot-reload-friendly and are almost certainly correct; embedding is the `.rbxl` mental model a Roblox developer arrives with. If files, the `Script` payload holds a path and `O9`'s snapshot must decide whether it captures the path or the content.

9. **Is the sandbox adversarial or mistake-containing?** Everything above is designed for mistake-containment and says so. An adversarial boundary (running scripts from strangers) needs a separate process and syscall filtering, not `luaL_sandbox` — a completely different architecture, decided now or not at all.

10. **Is 1 Spectra unit = 1 Roblox stud?** Every number in every Roblox tutorial and forum post the developer copies depends on it, and it must be settled before `Size` ships or content exists. This overlaps `ROADMAP.md` §11 item 13 (the default editing grid) — answer them together, not separately.
