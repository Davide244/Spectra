# Spectra Engine — Roadmap

> One dependency-ordered plan for the whole vision: *a Roblox-style edit experience with Source-grade robustness, on a scene-graph spine, with more rendering and custom shaders.*
> Sizes are relative (**S / M / L**), not calendar estimates. Every milestone is independently shippable and independently verifiable.
> Read `CLAUDE.md` first — it holds the architecture decisions and the pillars this roadmap must never break.
>
> **Companion documents — eight, and this is the whole set.** Five own an arc of milestone ids that interleaves with the arcs below and is referenced here by id rather than restated; the other three are a survey, a mapping and a rule set, and own no milestones. **Start with the first one.**
>
> | Document | What it owns | Arc |
> | --- | --- | --- |
> | [`docs/data-model.md`](docs/data-model.md) | **The orientation page — read it first.** What `SceneNode`, `Scene` and the payloads actually are today, with a `file:line` on every row, and one table separating what exists from what is only designed. It is the counting authority for the payload set. | — (survey) |
> | [`docs/roblox-onboarding.md`](docs/roblox-onboarding.md) | The scripting decision (Luau), the script payload, attributes, tags, signals, Play/Stop. | `O0`–`O9` |
> | [`docs/roblox-to-spectra.md`](docs/roblox-to-spectra.md) | The concept mapping written for a Roblox developer, marked row by row with what exists. | — (mapping) |
> | [`docs/formats-and-pipeline.md`](docs/formats-and-pipeline.md) | Every file format and the cook: `.spack`, `.smap`/`.scmap`, `.smodel`, `.simage`, `.saudio`, `.sentdef`, `game.spectraproj`, and the `scook` rules. | `D0`–`D22` |
> | [`docs/console.md`](docs/console.md) | The console as the engine's control surface: typed cvars, commands, binds, cfg files. | `C*` |
> | [`docs/networking.md`](docs/networking.md) | Server-authoritative replication, the fixed tick, interest management, prediction — plus collaborative editing. | `N*`, `T*` |
> | [`docs/realms.md`](docs/realms.md) | Audience and liveness as node properties (`Realm`/`State`), replacing Roblox's container folders. Rules `R1`–`R17` — **its own rules, not the `R*` rendering arc below**. | — (rules `R1`–`R17`) |
> | [`docs/physics.md`](docs/physics.md) | The physics engine choice (Box3D), hulls from brushes, the character mover, networked bodies — **and the world/part brush split (§2.3a), which this document schedules as `P7a`.** | `Y0`–`Y16`; designs `P7a` |
>
> Milestone-id prefixes are a shared namespace: `F`/`E`/`P`/`S`/`R`/`H` are this document's arcs, and `O`, `D`, `C`, `N`/`T` and `Y` belong to the companions above. **`R` is the one overloaded letter** — here it is the rendering arc (`R1`, `R9`, `R10`, …), this document's cross-arc rulings are written with a dash (`R‑1`, `R‑3`, …), and `realms.md`'s rules are always cited document-qualified (`realms.md R15`).

---

## 1. What the engine already delivers

This is a continuation, not a wish list. The hard parts of the pillar are done and guarded by tests.

- **Instant world edits, independent of world size.** Brush edits mark the world dirty, the render thread snapshots `BrushPlacement`s (validating rigidity), a background task carves → snaps → welds → builds per-cell BSP + mesh arrays, and the render thread swaps in only the chunks whose artifacts changed. Steady-state edits go through `CsgWorld.Build(placements, dirtyCells, previousWorld)` → `CsgIncrementalCompiler` with paged copy-on-write carry, so a one-part edit costs **~0.05–0.1 ms at 1k, 10k and 50k parts**, half of them 8,000 units from the origin. The `CsgBench openworld` verdict line says *world-size independent* and must keep saying it.
- **Open world is real, not aspirational.** Sparse dictionary-keyed 32-unit cell grid (`ChunkCoord`/`ChunkGrid`): negative and distant cells cost the same as the origin. No sealed world, no PVS, no map extents, no leaks-as-blockers. Brush-local frames keep CSG precision position-independent.
- **A scene graph that is genuinely the spine.** `SceneNode` with stable `Guid Id`, O(1) subtree-brush counting, equality-early-outing transform setters, attach/detach vs. brush-swap fast paths; `Scene` with `NodeAdded`/`NodeRemoved`/`NodeTransformChanged`, a graph-structure version, a dynamic `SceneBvh`, `Raycast`, `QueryFrustum`, a `SelectionSet` with auto-deselect on removal, and screen-ray picking via `Camera.ScreenPointToRay`.
- **Three render backends, live.** OpenGL, D3D11 and D3D12, each with forward and wireframe pipelines fed by one shared `RenderView` draw list (frustum-culled scene items + chunk-culled world items), plus a depth-off `DebugDraw` line path that works identically on all three and debug-layer message drains on both D3D backends.
- **Its own shader language.** SpectraShade compiles `.spectrashade` source files to GLSL and HLSL at runtime, per backend, with hot reload on save; the `.specshadecomp` container is a versioned, hand-rolled binary format; an LSP and a VS extension already exist.
- **Asset loading, in part.** `AssetManager`, `ContentRoot`, `ImageDecoder`/`DecodedImage`, `TextureAsset` and an asset-aware `Material.SetTexture` overload have landed; textures load from a content root that mirrors into the build output.
- **A test and oracle discipline that is the real asset here.** Chunked-vs-monolithic equivalence oracles (mesh, BSP, weld), bit-identical determinism tests, origin-invariance tests, incremental-compile tests, a GPU-free `FakeRenderer` that records exact vertex/index arrays, and a real-driver GL fixture. Every milestone below inherits the obligation to keep these green.

## 2. Ground truth: the asset/material arc is landing in stages

The plan was drafted assuming a large asset arc — asset manager, texture loading, `.spectramat` material assets, per-face brush materials with Hammer-style texture axes, per-material chunk submeshes, model import — was already done. It was not; it is landing stage by stage while this roadmap is being written. Status as verified against the working tree (**re-verify before depending on any row — this arc is still in flight**):

| Stage | Status |
| --- | --- |
| Asset manager, texture loading | ✅ landed (`Assets/AssetManager.cs`, `ContentRoot.cs`, `ImageDecoder.cs`, `TextureAsset.cs`) |
| `.spectramat` material assets | ✅ landed (`Assets/MaterialDefinition.cs`, `MaterialParser.cs`, `Assets/Materials/*.spectramat`, never-null default material) |
| Per-face brush materials + texture axes | ✅ landed (`Bsp/FaceSurface.cs`; `Brush.FaceSurfaces` at `Brush.cs:148`, `WithFaceMaterial`/`WithFaceSurface`) |
| Per-material chunk submeshes | ✅ landed (`ChunkSubmesh` at `Bsp/ChunkMesh.cs:39`, `ChunkMesh.Submeshes` at `:95`; `Scene.StaticWorldMaterial` demoted to the fallback for faces naming none) |
| Model import | ✅ landed (`Assets/ModelImporter.cs` over `Silk.NET.Assimp`, `ModelData`, `AssetManager.Models.cs`, `Scene/ModelInstantiator.cs`) |

**Updated 2026-08-21: this table is no longer a list of gaps — every row above has landed**, re-verified against the working tree at the files cited. `docs/formats-and-pipeline.md` §1 flagged the staleness first and its consequence stands: **`F1` (materials) was the highest-fanout item in the roadmap, and it is now real**, so texturing in the editor, the shader parameter manifest binding, exact tangents for PBR, transparent-face submesh splitting and the map format's `faces` records all bind to a schema that exists rather than one they have to guess. Any milestone below that still reads as though `F1` were pending is describing the past; check the tree, not the prose.

*(The mangled comment markers previously seen in `Engine.cs` were a transient mid-edit artifact and are gone — the tree reads clean.)*

---

## 3. Cross-arc rulings

These are the places where two arcs claimed the same work. Each is decided here so it gets built once.

**R‑1. Offscreen render targets belong to the rendering arc (as `R3`), not to the editor and not to the shader arc.**
Three arcs claimed them: rendering (shadows, post), shader authoring (material preview thumbnails), editor (Uno viewport). They are one subsystem — `RenderTargetDesc` / `RenderTarget` with `Texture ColorTexture`/`DepthTexture` whose *object identity survives a resize*, plus `BeginPass(target, clear)`/`EndPass()` — and everyone else consumes it. **But note the schedule consequence: on Windows the Uno viewport does not need them at all** (composition swapchain + `ISwapChainPanelNative` renders straight to the panel). Offscreen targets are on the critical path only for **Linux hosting**, shadows, post-processing and preview thumbnails. That takes the single largest rendering milestone *off* the path to a usable Windows editor.

**R‑2. The shader parameter manifest belongs to the shader arc (as `S3`), and it lives in `SpectraEngine.Core`, not in the compiler assembly.**
The editor property grid is a *consumer* of the manifest, never its author. Putting it in Core means the editor reads a shipped shader's properties without hosting the lexer/parser, and means it is testable from the engine's own test projects before any editor exists. The editor arc must not invent a parallel "shader property descriptor".

**R‑3. Per-face brush materials are their own foundation milestone (`F1`), owned by nobody's arc.**
Rendering wants its texture axes (exact tangents, free), persistence wants its face records, the editor wants a face-texturing tool, shaders want a material asset to bind a manifest to. It is built once, first, and **faces are keyed by PLANE index, never by `LocalFaces` index** — the editor's resize API guarantees plane count/order stability but cannot guarantee face-array stability (a face can clip away entirely). Two arcs independently derived this requirement; it is now binding.

**R‑4. `ViewDrawer` (`F3`) comes before the manifest-driven uniform binder (`S3`).**
Both rewrite the same hardcoded `SetUniform` blocks in all six pipeline files. Collapse the six copies into one first, then teach that one place to walk Frame/Object manifest entries. Doing it the other way means writing the binder six times and then merging six divergent files.

**R‑5. Undo lives in the editor as inverse commands over the live graph — not as operations over a map document.**
The persistence arc proposed that the map format's mutation vocabulary *is* the undo vocabulary. Rejected as an implementation, kept as a constraint: undo captures **absolute before/after values addressed by `Guid`**, because the transform setters early-out on equality and `CsgCompileCache` keys on exact matrix equality, so undo restores the exact prior matrix and re-hits the carve cache — a document-replay model would not. The constraint that survives: **every editor mutation must be expressible in the map schema**, enforced by a test (author → mutate → save → load → compare), so the two data models cannot drift.

**R‑6. `SceneNode.Entity` is a third payload alongside `Brush` and `MeshRenderer` — not a subclass, not an ECS, not a parallel list.**
This keeps the graph-is-the-spine decision intact and keeps serialization free of polymorphic node types.

**R‑7. `Scene.NodeRenamed` must NOT bump `_graphStructureVersion`.**
`NodeAdded`/`NodeRemoved` do, which forces the static-world snapshot onto its O(world) full-walk path. A rename changes no traversal order. Pin it with a test asserting zero dirty cells after a rename-only edit.

**R‑8. Serialize everything that touches the D3D12 PSO creation path.**
`R1` (PSO key), `R2` (sRGB RTV), `R3` (offscreen formats), `R10` (blend), `R11` (MSAA) all mutate it, and two landing concurrently conflict badly. One branch, in order.

**R‑9. `P7` (entity brush ownership) and `P7a` (`BrushKind`) must not land concurrently with `E4`/`E6`, nor with each other.**
All of them do surgery on `SceneNode`'s counters and the brush snapshot path. `P7a` extended this ruling by inheritance rather than by a new argument: it touches the same counter, the same `Brush` setter and the same fifteen snapshot-path sites, and it is the milestone `P7` now depends on. `P7`/`P7a` are the riskiest edits in the roadmap; give each a quiet tree.

**R‑10. The Uno host comes *after* the editing layer works in the existing Silk window.**
Do not block "a person can build a level" on `ISwapChainPanelNative` interop. `EditorInputFrame` (`E1`) is the seam that makes re-hosting a swap rather than a rewrite.

---

## 4. The critical path

**The shortest sequence to an editor a person can build a level in — place, texture, manipulate, save, play:**

```
F2 (node identity)      →  E1 (editing spine + translate gizmo + undo)
                        →  E2 (editor camera)          [tiny, enormous felt payoff]
                        →  E3 (multi-select)
F1 (materials + faces)  →  E7 (face texturing tool)
                        →  E4 (brush resize)
                        →  E6 (duplicate / delete / group)
F2 + F1                 →  P2 (.spectramap save + load)
                        →  P11a (play / stop)
```

Everything else is off the path. In particular: **the Uno shell, offscreen render targets, shadows, PBR, the type checker, the entity system and prefabs are all off the critical path.** A person can build, texture, save and walk a level with `F1, F2, E1–E4, E6, E7, P2, P11a` and nothing else.

**Parallel tracks (make it look and feel better, never block the path):**
- **Rendering** — `F3 → R1 → R2 → R3 → R4/R5 → R6/R7 → R8 → R9 → R10 → R11 → R12`. Independent after `F3`; only `R9` (tangents) reaches back into `F1`.
- **Shader authoring** — `F4 → S2 → S3 → S4 → S5 → S6 → S8 → S9`. `S3` needs `F1` and `F3` landed first.
- **Entities & no-code logic** — `P4 → P5 → P6 → P7 → P8 → P9 → P10`, with **`P7a` (`BrushKind`) landing before `P7`** on its own — it needs only `F1`, not `P4`, so it may run any time the tree is quiet of `E4`/`E6` (ruling R‑9). Needs `F2`; `P9` needs `P2`; `P8` also needs `physics.md` `Y0` for the BVH overlap queries.
- **Hosting** — `H1 → H2 → H3`. Needs `E1`; `H3` needs `R3`.

---

## 5. Phase 0 — Foundations

Everything downstream is cheaper if these land first. Total: one medium (`F1`) and three small.

### F1 — Material assets, per-face brush materials, per-material chunk submeshes
`.spectramat` material asset (shader reference + parameter values + texture references by content-relative path), per-face material assignment on `Brush` **keyed by plane index**, Hammer-style per-face texture axes (u/v axis, shift, scale, rotation), and per-material submesh splitting in `ChunkMeshBuilder`/`ChunkMesh` so a chunk can draw more than one material.
- **Unlocks** — editor texturing (`E7`), manifest↔material binding (`S3`), exact tangents for PBR (`R9`), transparent-face splitting (`R10`), face records in the map format (`P3`).
- **Depends on** — nothing. The already-landed `AssetManager`/`TextureAsset` work is its base.
- **Touches** — `Bsp/Brush.cs`, `Bsp/ChunkMeshBuilder.cs`, `Bsp/ChunkMesh.cs`, `Scene/StaticWorldChunkMesh.cs`, `Graphics/Material.cs`, `Assets/AssetManager.cs`, `Graphics/RenderView.cs` (world items stop sharing one `StaticWorldMaterial`).
- **Risk** — **HIGH.** This changes the shape of chunk mesh output, which is exactly what the chunked-vs-monolithic equivalence oracles and the bit-identical determinism tests compare. They are structural rather than golden-file so they should survive, but any snapshot of vertex arrays needs deliberate regeneration — verify correctness *before* re-baselining or a bug gets laundered into the oracle. Second risk: per-material submeshes multiply draw calls per chunk; measure against `CsgBench openworld` before merge.
- **Size** — **L.**

### F2 — Node identity seam
`SceneNode(name, Guid id)` (the code comment already reserves this "for the serialization arc"), a `Name` setter raising a new `Scene.NodeRenamed`, `Scene.TryFindById(Guid)` backed by a dictionary maintained from the existing add/remove raise helpers, and `EngineInfo.MinimumReadableMapVersion`.
- **Unlocks** — literally all of persistence, and Guid-addressed undo commands in `E1`.
- **Depends on** — nothing. **Ship first.**
- **Touches** — `Scene/SceneNode.cs`, `Scene/Scene.cs`, `EngineInfo.cs`.
- **Risk** — **LOW**, with two sharp edges: `NodeRenamed` must not bump `_graphStructureVersion` (ruling R‑7), and duplicate GUIDs on load must be a named, loud error rather than a dictionary overwrite.
- **Size** — **S.**

### F3 — Collapse the six draw bodies into one `ViewDrawer`
~325 of the 635 lines across the six pipeline files are copies. Extract the byte-identical `DrawView` and near-identical `DrawRenderable` into `Graphics/ViewDrawer.cs`; add `readonly record struct PassConstants(View, Projection, LightDirection, CameraPosition)` built once per pass; add `virtual Renderer.ClipSpaceCorrection` (identity on GL, `GlToD3dClipZ` on both D3D backends) so the remap stops being named in four pipeline files; express the **documented `Use()`/`SetUniform` seam exactly once** as `ShaderProgram.BindForDraw()` (GL override binds-then-pushes; D3D pushes-then-binds); move the six duplicated `LightDirection` properties onto `Scene`, where they belong.
- **Unlocks** — every rendering milestone, and `S3`'s manifest-driven binder. Also the only place a second pass (shadow, post, preview) can be expressed at all.
- **Depends on** — nothing.
- **Touches** — new `Graphics/ViewDrawer.cs` + `PassConstants.cs`; `Graphics/Renderer.cs`, `ShaderProgram.cs`, all three `*ShaderProgram.cs`, all six pipeline files, `Scene/Scene.cs`.
- **Risk** — **MEDIUM.** Touches all three backends with no new feature to verify against, so a regression's only symptom is "looks slightly wrong". The D3D12 path is the subtle one: `D3D12ShaderProgram.Use()` has frame-sensitive cbuffer-slice logic, so moving the call site must not change how many times per frame it runs per program. Keep the diff strictly mechanical; verify with per-backend before/after screenshots in both pipelines.
- **Size** — **M** (≈350 lines deleted, ≈150 added).
- **Explicitly deferred** — switching GL to `glProgramUniform*` (which would erase the seam entirely) raises the minimum GL version and touches every overload. Separate change, separate verification.

### F4 — Diagnostics contract
`DiagnosticCode` (`SS####`, reserved ranges: 0xxx lexer, 1xxx parser, 2xxx analyzer/types, 3xxx linker, 4xxx codegen), a non-throwing `TryCompile(source, targets, out CompileResult)` on `IShaderCompiler` with the throwing `Compile` kept as a wrapper, `ShaderHotReloader` consuming it and exposing structured diagnostics via an event, and **corrections to `FEATURES.md`/`LANGUAGE.md`, which currently claim imports, bitwise operators and `const`/`in`/`out` work when none of them do.**
- **Unlocks** — an errors panel, inline squiggles and per-error doc links in any editor; the hot reloader currently has only `ex.Message`.
- **Depends on** — nothing.
- **Touches** — `SpectraShade.Compiler/Diagnostic.cs` (+ new `DiagnosticCode.cs`), `SpectraShadeCompiler.cs`, `Syntax/Parser.cs`, `Analysis/SemanticAnalyzer.cs`, `Core/Graphics/Shaders/IShaderCompiler.cs`, `Core/Graphics/ShaderHotReloader.cs`, CLI, LSP sync handler, both docs.
- **Risk** — **LOW.** Mostly additive; the only lasting hazard is careless code assignment, hence the reserved ranges and never reusing a retired number.
- **Size** — **S.**

---

## 6. Arc E — Editor interaction (the critical path)

Lives in a **new `SpectraEngine.Editing` assembly** referencing Core, so a shipped game binary carries no gizmo/undo/PIE code. Core gains only small, legitimate additions (public node-bounds accessor, `TryGetNode(Guid)`, `InsertChild(index, child)`, `ContainsBrushes`, `ValidateBrushRigidity`, `Brush` offset/clone APIs, batched `SelectionSet.SetRange`).

**Pinned design decisions:**
- Gizmo hit-testing is **analytic and screen-space** (project handles to pixels, measure point-to-segment distance against a pixel tolerance). No GPU ID buffer — the engine has no render targets, a readback would stall the render thread, and the analytic path is fully unit-testable headlessly with a synthetic camera.
- The editing layer consumes a host-agnostic **`EditorInputFrame`** value struct and never references Silk.NET. This is the single decision that makes Uno hosting non-invasive later.
- Snapping applies to the **absolute target**, not the accumulated delta (`applied = snap(start + raw) - start`), so parts *land* on the grid instead of preserving an off-grid offset forever.
- Brush resize produces a **new immutable `Brush`** via plane-offset derivation and assigns it to `node.Brush`; node scale is never touched. This rides the existing brush-swap fast path, so a resize drag costs exactly what a move drag costs.

### E1 — Editing spine, translate gizmo, transform undo
`EditorSession` + thread-safe `EditorCommandQueue` (drained once per frame, before the static-world pump). `TranslateGizmo`: 3 axis arrows, 3 plane handles, 1 screen-space centre handle, constant screen size, drawn into the already-depth-off `DebugDraw`. Drag machine Idle→Hover→Dragging→Commit|Cancel with grab-anchor capture, ray-vs-axis closest approach, a near-parallel guard, Esc/right-click/capture-loss cancel. `IEditCommand` + `EditHistory` (bounded, `Changed` event) with one command type: `TransformNodesCommand` (Guid + before/after `Transform`). Effective-selection rule (drop nodes with a selected ancestor) computed at grab; world-delta → parent-space per node.
- **Unlocks** — every other editor milestone; the four seams (input contract, command queue, gizmo framework, history) exist exactly once.
- **Depends on** — `F2`.
- **Risk** — **MEDIUM.** The near-parallel axis-drag singularity must be guarded or parts teleport. Applying a world delta under a non-uniformly-scaled parent composes to shear, which `Scene.DescribeNonRigidDefect` rejects — **freezing all world compilation with only a log line** — so rigidity must be validated at gesture time, not discovered at compile time. 1-pixel `GL_LINES` gizmos look cheap on high-DPI; usability is preserved by generous pixel tolerances, appearance is deferred.
- **Size** — **L** (the largest editor milestone; four seams at once).

### E2 — Editor camera: orbit, pan, zoom-to-cursor, frame selection
RMB look (existing fly behaviour retained), MMB/Space pan proportional to pivot distance, wheel dollies **toward the point under the cursor**, Alt+LMB orbits the selection pivot, F frames the selection.
- **Unlocks** — the difference between a tech demo and something that feels like an editor.
- **Depends on** — `E1`.
- **Risk** — **LOW.** Pitch clamp interacts with orbit past vertical (standard, acceptable).
- **Size** — **S.** *Highest felt-quality-per-line ratio in the entire roadmap — do it early.*

### E3 — Multi-select: box select, modifiers, batched selection
`Camera.ScreenRectToFrustum`, marquee drawn as **unprojected world-space debug lines** (zero backend work), click-vs-drag threshold, Shift add / Ctrl toggle, opt-in fully-contained mode, and `SelectionSet.SetRange` raising **exactly one** `SelectionChanged` — today selecting 500 nodes raises 500 events, which would thrash any UI binding.
- **Depends on** — `E1`. Independent of `E2`.
- **Risk** — **LOW–MEDIUM.** `Frustum.Intersects` is conservative, so intersect-mode can pick up corner-region false positives; documented, with fully-contained as the exact mode.
- **Size** — **M.**

### E7 — Face texturing tool *(critical path; sequence right after `F1`)*
Face picking (ray → brush → plane index), per-face material assignment, and a Hammer-style texture-axis manipulator: shift, scale, rotate, justify (fit/centre/align to world axes), and align-to-face. Undo entries capture before/after face records.
- **Unlocks** — the "texture" verb in *place, texture, manipulate, save, play*.
- **Depends on** — `F1`, `E1`.
- **Risk** — **MEDIUM.** Texture-axis manipulation is the classic place where "looks right on axis-aligned faces, wrong on angled ones" hides; test against a rotated brush explicitly.
- **Size** — **M.**

### E4 — Brush resize as a first-class part operation
`Brush.WithPlaneOffsets` / `WithFaceOffset` / `TryWithPlaneOffsets` (non-throwing, for the drag path) plus internal `CloneShape()`. One camera-facing handle per plane at the face-polygon centroid, back-facing handles culled; drag constrained to the face normal; absolute snapping; Alt = symmetric; minimum-extent clamp. On commit, re-centre the planes and compensate `LocalPosition` as one compound undo entry.
- **Key optimisation, and it is provable rather than heuristic:** offset-only derivation **skips the boundedness probe** (which costs a second full `BuildFaces` pass). Boundedness of `{x : nᵢ·x + dᵢ ≤ 0}` depends only on the recession cone `{x : nᵢ·x ≤ 0}`, a function of the **normals alone** — so if the source was bounded, every offset-only derivative is bounded. Duplicate-plane rejection likewise cannot newly trigger. The only remaining failure (offsets pushed past each other) is already caught by the existing "planes clip every face away" check, and the tool clamps rather than throws.
- **Depends on** — `E1`; **requires `F1`'s plane-index face keying** (ruling R‑3).
- **Risk** — **MEDIUM.** A per-frame new `Brush` is a guaranteed carve-cache miss for that brush — correct, and identical in cost to a move drag, but confirm against the `openworld` benchmark verdict line before merge.
- **Size** — **M–L.** *This is the milestone that makes brushes feel like Roblox parts.*

### E5 — Rotate gizmo, angle snapping, local/world space
Front-face-culled rings, ray-vs-ring-plane angle with ±π unwrapping, absolute angle snapping (15° default, Ctrl inverts), rotation about the selection pivot with per-node parent-space conversion, `GizmoSpace {World, Local}`. Scale-mode routing: brush nodes → `E4` resize; mesh nodes → `LocalScale`; **a group containing brush descendants refuses non-uniform scale with a user-facing message** rather than producing shear that freezes world compilation.
- **Depends on** — `E1`, `E4`.
- **Risk** — **MEDIUM.** Quaternion composition against `Scale·Rotation·Translation` with row-vector `world = local · parent.World` is where sign/order bugs live — test, do not eyeball. Absolute value capture is what prevents rotate-undo-rotate drift.
- **Size** — **M.**

### E6 — Structural edits: duplicate, delete, group/ungroup
`SceneNode.Clone(deep)` (fresh Guid, `Brush.CloneShape()`, `MeshRenderer` shared by reference — it is immutable and its resources are renderer-owned), `SceneNode.InsertChild(index, child)`, and `AddNodesCommand`/`RemoveNodesCommand`/`ReparentNodesCommand` recording parent Guid + sibling index. Ctrl+D, Alt+drag duplicate, Delete, group/ungroup. Undo/redo refused while a drag is in progress.
- **`InsertChild` is not optional:** `AddChild` only appends, and a re-added node landing at a different sibling index changes traversal order → carve order → **different-but-valid geometry**, breaking the bit-identical determinism oracles.
- **Depends on** — `E1`, `E3`, `E4` (`CloneShape`).
- **Risk** — **MEDIUM.** Structural edits bump `_graphStructureVersion`, forcing one full-walk validated compile — correct and already handled, but a bulk duplicate of thousands of parts produces one O(world) compile; measure it. Deleted subtrees held in history keep shared GPU meshes alive.
- **Size** — **M.**

### E8 — Advanced snapping: vertex / edge / face / surface placement
`SnapSolver` gathering candidates from a **bounded BVH box query** around the moving selection (never a world walk), priority vertex > edge > face-plane > grid with pixel-space acceptance radii, plus Roblox-style surface placement (drop flush against geometry under the cursor, optionally aligning up to the surface normal). Snap indicator via `DebugDraw`; settings on `EditorSettings` so a UI can bind them.
- **Unlocks** — brushes that butt together exactly, so no T-junctions and no CSG seams.
- **Depends on** — `E1`, `E4`.
- **Risk** — **MEDIUM.** The feature most likely to feel wrong on the first attempt: radii, priority and **hysteresis** need play-testing, not unit tests. A snap that engages and disengages every frame is worse than no snap.
- **Size** — **M**, plus an unusually large share of tuning time.

---

## 7. Arc P — Persistence & entities

**Pinned:** `.spectramap` is **text (JSON)** and is the single authoritative authored artifact; a binary `.spectramapb` is a later, purely derived build output. Codecs are hand-rolled `Utf8JsonReader`/`Utf8JsonWriter` — not `JsonSerializer` source-gen — because `Brush` has a validating constructor and no parameterless ctor (so DTOs would be needed anyway), because unknown-member *preservation* is explicit reader code rather than an attribute, and because `Utf8JsonReader` is a ref struct with zero trim/AOT surface. **The map contains zero derived data**: no carved surfaces, no BSP, no chunk meshes. Loading authors nodes and calls `Scene.RebuildStaticWorld`.

### P2 — `.spectramap` v1: geometry-only round trip *(critical path)*
Header (magic, `MapFormatVersion` + `MinimumReadableMapVersion`), scene name, camera, node graph (Guid, name, local transform, children), brushes as authored plane lists with an **optional, open, preserved-when-unrecognised** `faces` array, and asset references as content-root-relative POSIX paths.
- **Two pinned invariants.** (1) Every float is written with **shortest-round-trip** formatting and `save → load → save` **byte identity is a test** — a one-ULP perturbation of a plane offset shifts the carve, the snap grid, the weld band and the per-cell BSP, and the determinism guarantees silently stop meaning anything across a save. (2) Unknown members are skipped on read and re-emitted verbatim on save, so an older engine opens a newer map and re-saves it without destroying data.
- **Depends on** — `F2`. `faces` fills in with `P3` once `F1` lands.
- **Risk** — **MEDIUM.** `Brush`'s constructor rejects duplicate/unbounded plane sets, so a hand-edited or merged map surfaces as an `ArgumentException` from deep inside `Brush` — the reader must catch and re-report with node name and file offset, or the first bad map is undebuggable.
- **Size** — **M.**

### P3 — Face materials and texture axes in the format
Fills in `P2`'s open `faces` records with the `F1` schema; a map referencing a missing asset **loads**, logging one error per missing asset and substituting a loud magenta placeholder.
- **Depends on** — `P2`, `F1`.
- **Risk** — **LOW** once `F1` exists. Asset *decode* may be off-thread; `CreateTexture`/`CreateMesh` must be marshalled to the render thread, exactly as the chunk-mesh pump already does.
- **Size** — **S–M.**

### P11a — Play / stop *(critical path)*
`EditorMode {Edit, Play, Paused}`; capture authored state on Play, **diff-restore** on Stop (assign only what differs — the transform setters early-out on equality, so untouched nodes dirty nothing and the incremental compiler repairs only the cells gameplay disturbed); history barrier at Play entry. Mode switches are ordinary render-thread edits and must never call the synchronous `RebuildStaticWorld`.
- **Depends on** — `E6` (`InsertChild`, structural commands).
- **Risk** — **MEDIUM.** Snapshot is cheap only because `Brush` is immutable (it holds brush *references*, not deep copies). The alternative model — spawn a fresh scene deserialized from the document and discard it on Stop — is better once entities have runtime state, but costs a full world recompile and doubles GPU residency on every Play. **This is a sign-off decision (§9.5).**
- **Size** — **M.**

### P4 — Entity runtime core
`Entity` base (node back-reference, `OnSpawn`/`OnActivate`/`OnRemove`, `SetNextThink`, `AcceptInput`, `FireOutput`), Source's connection tuple `(TargetName, InputName, ParameterOverride, Delay, TimesToFire)` with `-1` = infinite, a binary min-heap keyed on `(fireTime, monotonicSequence)` for fully deterministic ordering, `TargetNameIndex` (name → list, `!self`/`!activator`/`!caller`, trailing-`*`), `EntityWorld`, `EntityCatalog`. **`targetname` IS `SceneNode.Name`** — one identity, one field, duplicates allowed (firing at a name fires every match). `EntityWorld.Tick(dt)` runs on the **render thread**, after `SceneManager.Update` and **before** `ProcessStaticWorldCompilation`, so an entity that moves a world brush gets its dirty cells captured the same frame.
- **Depends on** — `F2` (needs `NodeRenamed` for the index).
- **Risk** — **MEDIUM.** Two named hazards: scene event handlers must not mutate the graph, so entity spawn/despawn driven by those events must be **deferred to the tick**; and zero-delay I/O cascades need a per-tick dispatch budget that trips **loudly, naming the offending targetname**, or the first mutual relay a user builds hangs the render thread with no clue why.
- **Size** — **L.**

### P5 — Entity source generator + schema export
A `netstandard2.0` Roslyn **incremental** generator emitting, per `[SpectraEntity("func_door")]` class: the string→typed keyvalue parse switch, the input dispatch switch, output declarations, a static `EntitySchema` (the FGD equivalent), and a `[ModuleInitializer]` registering into `EntityCatalog`. Plus `--export-entity-schema` so an editor can build property panels and I/O wiring UI from JSON **without referencing the game assembly**. Analyzer diagnostics for non-partial classes, duplicate classnames, unsupported property types, wrong input signatures.
- **Keyvalues are string-typed on the wire**, exactly like an FGD/VMF. This kills three problems at once: no polymorphic JSON, no coupling of the on-disk format to C# type shape, and an unknown classname loads as a placeholder that keeps its data and re-saves losslessly.
- **Depends on** — `P4`.
- **Risk** — **MEDIUM–HIGH**, mostly plumbing: `netstandard2.0` inside a `net10.0` solution whose `Directory.Build.props` sets `TargetFramework` globally; a `Microsoft.CodeAnalysis.CSharp` pin the installed SDK's Roslyn actually hosts; and incremental-generator caching (capturing `ISymbol` in the pipeline destroys it). Mitigate with Verify snapshot tests, already used by the compiler tests, and by re-running every `P4` test against generated entities.
- **Size** — **L.**

### P6 — Built-in logic entities (the no-code core)
`logic_auto`, `logic_relay`, `logic_timer`, `math_counter`, `logic_branch`, `logic_case`, `logic_compare`. Geometry-free; triggers wait for `P7`.
- **Risk** — **LOW technically**, and that is the point: this milestone *proves* `P4`/`P5` were designed right. If any of these is awkward to write, fix the attribute surface here rather than after fifty entities exist. The real risk is semantic — decide deliberately whether `logic_relay` refires while pending, whether `math_counter` fires `OnHitMax` on every hit or only on transition, and write it into XML docs.
- **Size** — **M.**

### P7a — `BrushKind`: brushes that never fuse into the world *(prerequisite for `P7`, and useful without it)*
**The declared bit that makes "participates in the fused world" something a simulation can never change and a human changes only by asking.** Designed in full — fifteen gate sites, three pinning tests, the mesh cache, the conversion command — in [`docs/physics.md`](docs/physics.md) §2.3a, which is the owner; this entry is the schedule and the acceptance criteria.

**The correction that motivates it, because it reverses the intuition:** the carve is **union-skin extraction** (`Bsp/Csg.cs:8-12`), **not** subtraction. Two overlapping world brushes *merge*; a crate dropped on the floor does **not** punch a hole in it. Nothing about a part *sitting* in the world is a problem. The damage begins only when a brush **moves under simulation**, and it is worse than a slow recompile: every tick changes the **overlap set** rather than a placement, which defeats the incremental compiler's trusted carry (`CsgIncrementalCompiler.cs:99`, `Scene.cs:996`) and bails to the fully-validated **O(world)** path *every tick, forever, while everything still renders correctly*.

**Scope.** `SceneNode.BrushKind { World, Part }` as a **plain byte field** (not a packed word — `NodeRealm`, `NodeState` and `PhysicsFlags` are all unbuilt and must not gate this), **default `World`**, **NOT inherited and with no `Inherit` value** — so `AddChild` is not a refusal site and **no reparent can rewrite world topology**. Plus `IsStaticWorldBrush`; the **two-lane** subtree counter (one `long`, total in the high half and static-world in the low half, **one** private writer, two read-only projections — `SubtreeBrushCount` keeps its meaning and its callers, because `ScaleGizmo.cs:330`/`:654` and `GizmoBrushRigidityTests` depend on the total); all fifteen gate sites; `Scene.MarkAdmissionChanged()` (`realms.md` R17); the **snapped**, refcounted, `Brush`-reference-keyed part-mesh cache; **one new arm** in `BuildRenderView`; `ConvertBrushKindCommand`; the always-drawn part outline; and the stats line.
- **Needs no entity system, no physics, no prefabs.** It is in the `P` arc because it is a scene-graph and admission change, not because it depends on `P4`.
- **What it delivers ALONE is exactly one thing:** a brush that renders its own faces, never carves, and moves at zero recompile cost. **The clip volume needs `Y0`+`Y3`, the trigger needs `Y0`/`Y8`, a falling part needs `Y6`, and a server-side volume needs the `realms.md` R15 relaxation that ships with `CanCollide` — this milestone must not advertise any of them.**
- **Named hazards, all silent:** (1) gating the counter but **not** the `Brush` setter's `MarkStaticWorldDirty` (`SceneNode.cs:140`) leaves every zero-cost claim false — this is the line, and `networking.md` §4.5 and `roblox-onboarding.md` `O7` already name it; (2) gating `OnNodeSpatialComponentChanged` (`SceneNode.cs:144`), which must stay kind-**blind** or part brushes fall out of frustum culling and editor picking; (3) missing `TryCollectChangedSlots` (`Scene.cs:1310`), which forces the O(world) walk on **every drag frame** whenever a dirty world-brush subtree holds a part-brush descendant; (4) skipping `VertexSnapper.Snap` on the part path, which cracks a part brush along **its own** edges.
- **Required verification:** the three invariant pins (a moving part → constant compile count; **200 attach/detach/swap operations on part nodes → constant compile count and `StaticWorldDirty` false throughout**, which fails against unmodified code today; assign-order safety in both orders); a randomized graph test recounting **both** counter lanes after N attach/detach/reparent/kind-flip operations; the identity-placement mesh pin (part submeshes byte-identical to `CsgWorld.Build([placement at identity]).ChunkMeshes[0].Submeshes`) and the rotated-placement tolerance oracle at 2 × `VertexSnapper.GridSize`; part-brush outlines drawn **always** (never only on selection — commit `d4701d6`'s lesson) and `WorldBrushes: N  PartBrushes: M` in the stats line; `CsgBench openworld` verdict still *world-size independent*.
- **Depends on** — `F1` (per-face materials, since the part path emits per-material submeshes). **Must not run concurrently with `E4`/`E6`** (ruling R‑9, which extends to this milestone for the same reason it covers `P7`: the same `SceneNode` counter and the same brush-snapshot surface).
- **Size** — **M–L** (modest line count, extreme care density; the gate list is the work).

### P7 — Brush entities: owned by their nearest `Entity` ancestor, excluded from the carve
**The keystone, and the highest-risk milestone in the roadmap.** A brush is owned by its **nearest `Entity` ancestor, inclusive** (`_entityDepth` counter mirroring the existing `_subtreeBrushCount` idiom); the snapshot filters on `IsStaticWorldBrush`, which **`P7a` now supplies** along with the counter work this entry used to own. Entity-owned brushes leave the carve exactly as part brushes do, and a door opening becomes a matrix write with no recompile.

> **OVERTURNED — the render mechanism, and the counter instruction.** Two claims that stood in this entry are dead, and the replacements are in [`docs/physics.md`](docs/physics.md) §2.3a.
>
> 1. **"Compiled into a `BrushModel`, attached as a plain `MeshRenderer` on the entity node — zero renderer changes, zero backend changes, zero new `RenderView` path."** This cannot be built, and the reason is eleven lines of code rather than a preference: **`MeshRenderer` holds exactly one `Mesh` and one `Material`** (`Scene/MeshRenderer.cs:12-20`) and a node holds exactly one `MeshRenderer`, so **after `F1`'s per-face materials it cannot express a multi-material brush at all**. The only existing idiom for multi-material geometry is `ModelInstantiator`'s node-per-submesh (`ModelInstantiator.cs:126`), which would inject **derived nodes into the authored graph** — selectable, reparentable, serialized — breaking the same *"derived data is never authored"* rule the static world obeys. **The replacement is `P7a`'s:** one new arm in `BuildRenderView`'s existing loop plus one engine-owned refcounted mesh cache. The surviving claim is narrower and must be stated narrowly: **no third `RenderView` list, no backend change, no new geometry code — but a new path, not zero new paths.**
> 2. **"`_subtreeBrushCount` splits into a static-world-only counter."** It does not *split*; it becomes **two lanes in one field with one writer**, and the total lane is **not** deleted — `ScaleGizmo.cs:330`/`:654` route on it to refuse scaling a group node with brush descendants, and `GizmoBrushRigidityTests` pins that. A world-only counter would silently delete that refusal.
>
> The acceptance line *"brush-model triangles identical to `CsgWorld.Build(sameplacements).BuildMesh()` at identity"* is **superseded** by `P7a`'s two-oracle pin — same intent, stated against `ChunkMeshes`/`Submeshes`, which is what the code actually emits per cell. `networking.md` §4.5 carries the same overturned mechanism and is corrected in the same commit.

- **Named hazards, all three of which are silent:** (1) forgetting the admission gating makes an animating door mark the world dirty and launch a background compile **every frame, forever** — the single most likely way to destroy the pillar while everything still renders correctly (this is `P7a`'s hazard, inherited); (2) an ownership flip that does not force a full walk corrupts the slot map, because the placement *count* changes and every later slot shifts; (3) an ownership flip that does not dirty the departing brush's footprint leaves stale geometry welded into the world.
- **Required verification — this must not merge on code review alone:** all chunked-vs-monolithic and determinism oracles green; an entity-owned brush contributes zero world surfaces; `StaticWorldCompileCount` **constant** across 100 frames of door animation; `P7a`'s two mesh oracles; `CsgBench openworld` verdict still *world-size independent*.
- **Depends on** — `P4`, and now **`P7a`** for the admission bit, the counter and the render path. Independent of `P5`/`P6`. **Must not run concurrently with `E4`/`E6`** (ruling R‑9).
- **Size** — **M** now that `P7a` carries the counter and render work it used to own (still extreme care density).

### P8 — Trigger volumes and their queries
`trigger_multiple`/`trigger_once`/`trigger_teleport`. Touch tracking diffed per tick drives `OnStartTouch`/`OnEndTouch`/`OnTrigger`.

> **OVERTURNED — the query structure.** This entry said *"queries transform into model space via the inverse of the entity node's world matrix and hit the brush model's BSP."* **There is no brush-model BSP to hit.** `P7a` gives a non-carving brush a *mesh*, not a compiled world, and `CsgWorld`'s per-cell BSP is a pure function of the static placement list — a part or entity-owned brush is by construction not in it (`CsgWorld.cs:603`, `:617`). Trigger queries therefore re-point at **`SceneBvh`'s box and sphere overlap queries**, which [`docs/physics.md`](docs/physics.md) `Y0` builds because the BVH has only `Raycast` and `QueryFrustum` today — and which the physics arc's touch pass (`Y8`) uses as well, so there is one overlap path, not two. The open question *"should a brush model build its BSP eagerly or lazily?"* (§13) is thereby **answered by deletion**: it builds none.

- **Unlocks** — the no-code loop closes: walk into a room, a door opens, no script.
- **Depends on** — `P6`, `P7`, and `Y0` for the overlap queries.
- **Risk** — **MEDIUM.** Test explicitly: an entity resting on a boundary must not chatter (mirror `ChunkCoord`'s documented boundary rule); a trigger deleted mid-touch must still deliver `OnEndTouch`; a moving trigger recomputes from its new transform the same tick. Route the touch pass through `SceneBvh`, never a triggers × movers double loop.
- **Size** — **M.**

### P9 — Entities, keyvalues and connections in the map
Two-phase load: construct all entities and parse keyvalues, *then* resolve connections and run spawn/activate. Unknown classnames become a `PlaceholderEntity` retaining classname, keyvalues and connections.
- **Depends on** — `P2`, `P5`.
- **Risk** — **MEDIUM.** The forward-compatibility test must be a *real test*: author a map with an unknown classname, load, re-save, assert byte identity — otherwise the preservation behaviour rots within two months. Connections pointing at missing names must **warn and be kept**, never dropped; a mapper who renames a door must not silently lose their wiring.
- **Size** — **M.**

### P10 — Prefabs and instancing
`.spectraprefab` shares the map grammar, rooted at one subtree. A `PrefabInstance` payload stores `{prefab, seed, overrides}`; children are **not written into the map** but expanded at load with **deterministically derived GUIDs** `hash(instanceSeed, prefabLocalId)`. Overrides restricted in v1 to transform, material reference, entity keyvalues.
- **Risk** — **MEDIUM–HIGH.** GUID derivation must use a fixed hash (FNV/xxHash into a v8-shaped Guid), never `string.GetHashCode`, which is process-randomised and would break every stored reference on the next launch. **Prefab-internal targetname scoping is a sign-off decision (§9.8)** — it bakes into every saved map and cannot be changed later without a migration.
- **Size** — **L.**

### P11b — `.spectramapb` shipping format — **SUPERSEDED, do not build**
**Replaced by `.scmap` in [`docs/formats-and-pipeline.md`](docs/formats-and-pipeline.md) §2.7 (`D*` arc).** The id is kept so existing cross-references still resolve, and the entry is kept so nobody re-derives it from scratch.

Two things were wrong with it, and the second is the reason it cannot simply be renamed. It specified a binary *mirror* of the text map containing zero derived data — but the artifact actually wanted is the **baked** one: per-cell welded meshes, per-cell BSP trees and per-cell material runs, so a shipped game runs zero CSG at load. And its pinned test — *binary-load → text-save → byte-identical to the original text map* — **is unsatisfiable for that artifact**: welding, T-junction repair and per-cell carving are not invertible, so `.scmap → .smap` is not a valid operation and must not be attempted. The replacement guard is a **bake oracle**: cook → load → assert the loaded per-cell arrays are element-identical to a fresh `CsgWorld.Build(placements)` of the same source. **`.smap` is the only editable artifact; a lost `.smap` is a lost map.**

---

## 8. Arc S — Shader & material authoring

Target story: right-click → New Shader → write a `.spectrashade` declaring `[Scope(Material)]` parameters with `[Display]`/`[Range]`/`[Color]` and defaults → save → the compiler emits a parameter manifest into the `.specshadecomp` → the property grid builds itself with zero per-shader editor code → a `.spectramat` stores values by authored parameter name → errors appear as coded, spanned diagnostics inline and as a magenta error material, while the last-good program keeps rendering.

### S2 — Parameter scopes, UI metadata, defaults in the language
`[Scope(Frame|Object|Material)]` on cbuffers/samplers/fields, plus `[Display]`, `[Tooltip]`, `[Group]`, `[Range]`, `[Color]`, `[Srgb]`, `[HideInEditor]`, plus field initializers with a restricted constant folder (literals + builtin-type constructors).
- **Why this exists:** today there is **no way to tell `uBaseColor` (a material parameter) from `uLightDir` (engine-supplied)** — they sit in adjacent cbuffers in `Lit.spectrashade` and are distinguished only by which pipeline file hardcodes a `SetUniform`. An explicit scope is the smallest thing that makes a manifest meaningful.
- **Depends on** — `F4`.
- **Risk** — **MEDIUM.** The engine-builtin-uniform whitelist hardcodes today's forward-pipeline uniform set into the compiler; keep it in one file with a comment pointing at `ViewDrawer`. Snapshot tests will churn.
- **Size** — **S–M.**

### S3 — Parameter manifest, `.specshadecomp` v2, scope-driven runtime binder
`ShaderManifest` in **`SpectraEngine.Core`** (ruling R‑2): parameter entries with authored name, per-backend emitted name, type, scope, cbuffer/binding/offset/size, default bytes and UI metadata; sampler entries; vertex inputs; source + import-closure hashes. `ShaderFormatVersion` → 2 with `manifestSize` after the header, reader branching so v1 still loads. Engine side: replace the hardcoded `SetUniform` blocks with a manifest-walking binder, and make `Material` validate against the manifest and seed unset parameters from defaults.
- **Record both authored and emitted names:** `GlslGenerator.EscapeId` already renames GLSL reserved words, so the same authored parameter can already have different names in GLSL and HLSL. **The authored name is the material's key.**
- **Depends on** — `S2`, **`F3`** (ruling R‑4), **`F1`** (`.spectramat` is what stores the values).
- **Risk** — **MEDIUM–HIGH.** Widest blast radius in the shader arc: file format + three shader-program implementations + the shared drawer. Two specific hazards: std140 and HLSL packing rules differ for arrays and `vec3` (write the offset computation once, table-driven test per type); and `D3D11ShaderProgram` currently derives its uniform layout from **pixel-shader reflection only**, so manifest-authoritative offsets may change behaviour for VS-only cbuffers — treat as a fix, verify all three backends render identically before and after.
- **Size** — **M–L.**

### S4 — Imports, module linker, dependency-aware hot reload, multi-file LSP
`import` currently **parses and is then completely ignored** — no resolver, no roots, no merge — and `FEATURES.md` claims it works. Add `module Name { }` (same member grammar as `shader`, no stage functions), an injectable `IIncludeResolver` (file-system roots for `ssc`, embedded engine stdlib under a reserved `engine/` prefix for the runtime, an unsaved-buffer overlay for the LSP), and a linker that flattens post-order with canonical-identity dedupe, cycle detection reporting the full chain, and cross-module duplicate-symbol errors. **Diamond imports are legal; cycles are an error.** Hot reload keys on the **import closure**, with watchers per directory and re-registration after each successful compile. LSP publishes each diagnostic against **its own file URI**.
- **Rejected: textual include-before-lexing** — it destroys per-file spans, so an error in a shared lighting library would be reported at a line number in the importing file.
- **Depends on** — `F4`. Land after `S3` so the manifest can record closure hashes.
- **Risk** — **MEDIUM.** Making the `shader` block optional is the riskiest single edit (`Parse()` calls `ParseShader()` unconditionally and error recovery leans on it). Cross-platform path canonicalisation is a bug farm — normalise once, at the resolver boundary.
- **Size** — **M–L.**

### S5 — Type checker phase 1: binder replacing `TypeInference`
**There is a live, severe bug today:** `HlslGenerator` lowers `a * b` to `mul(a, b)` only when the stopgap `TypeInference` recognises a matrix; when it returns `null` it emits componentwise `*`, which **compiles clean and renders wrong on HLSL while the GLSL build of the same shader is correct** — and each generator instantiates its own copy, so they can silently disagree. Fix: a `Binder` producing a typed **side table** (`Dictionary<Expression, TypeSymbol>`) over the existing syntax AST, replacing `TypeInference` in both generators in one change. Every existing snapshot output must be byte-identical **except** the intentional `mul()` corrections — those deltas are the milestone's proof of value.
- **Depends on** — `S2`, `S4` (bind the linked unit, not a single file).
- **Risk** — **HIGH**, and the easiest to under-scope: the builtin signature table is the hidden bulk (genType overloads across `Math.*` ≈ 200 entries). Keep a transition flag that falls back to today's permissive behaviour on an unbound expression plus a diagnostic counter, so a binder gap degrades rather than hard-fails.
- **Size** — **L.**

### S6 — Type checker phase 2: real diagnostics + close the documented-but-missing gaps
Constructor arity, swizzle legality, matrix/vector dimension agreement, assignment compatibility, a **pinned implicit-conversion policy** (GLSL and HLSL differ — the language must decide and both generators must emit the same casts), overload-resolution messages, `discard` only in `[Fragment]`, `Position` only in vertex/geometry. Plus the three gaps the docs already claim work: **bitwise operators** (lexed, never consumed by the precedence chain), **`const`/`in`/`out`** (lexed, never accepted), **local array declarations** (a lookahead fix — `ParseType` already handles `[N]`).
- **Risk** — **MEDIUM.** False positives are worse than missing checks: land each rule as a warning behind a switch, run the engine's own shaders clean, then promote to error.
- **Size** — **M–L.**

### S7 — Material preview thumbnails
Draws a sphere/cube with a given material into an offscreen target, driven entirely by the `S3` manifest so any custom shader previews with no per-shader editor code; plus the magenta error-material fallback when a shader failed its last compile.
- **Depends on** — `S3` and **`R3`** (the render-target subsystem, ruling R‑1). This milestone *consumes* render targets; it does not build them.
- **Size** — **S** once `R3` exists.

### S8 — Shader features and variants
`feature VertexColor;` / `feature Lighting { Unlit, Lit, LitShadowed }` lowered to **static `const` branches**, compiled on demand per requested key, never a preprocessor and never the cross product. Rationale: `#ifdef` branches are never type-checked when inactive, so a variant nobody compiles today breaks silently tomorrow; static-const branching runs every branch through the parser and the type checker on every compile and lets the backend DCE the dead side — and maps 1:1 onto Vulkan specialization constants later. **Constraint pinned with it:** features may appear only in conditions, never in declarations, types or array sizes, so the parameter manifest stays variant-invariant and the property UI does not change shape when a checkbox is toggled.
- **Depends on** — `S3` (design the variant table into the v2 header so this is not a second format migration), `S5`/`S6`.
- **Risk** — **MEDIUM–HIGH.** `Material` holds a direct `ShaderProgram` reference and hot reload deliberately preserves that object identity so every material picks up new code; the variant cache must preserve that guarantee **per (shader, key)**. And per-material variant keys multiply the static world's per-material submesh batching from `F1` — confirm before committing.
- **Size** — **L.**

### S9 — Vulkan: Vulkan-GLSL + shaderc, offline only
Vulkan-flavoured GLSL (`#version 450`, real `std140` UBO blocks replacing loose uniforms, `layout(set, binding)`) reusing the manifest's offsets, then shaderc via P/Invoke producing SPIR-V into the existing blob slot. Shipped in `ssc` and the editor **only** — the deployed runtime loads precompiled SPIR-V.
- **Sequenced last on purpose:** there is **no Vulkan renderer in the tree at all**, so SPIR-V today has no consumer and delivers zero user value. Direct SPIR-V emission remains the wrong call for a solo dev — weeks of work for output shaderc produces in an afternoon, and impossible before `S5` anyway.
- **Risk** — **MEDIUM** technically; the real risk is packaging two RIDs of a native dependency against a mandatory-AOT, Linux-capable toolchain, and that Vulkan's descriptor-set model may force a `[Binding(set, slot)]` language extension. Available value before any renderer exists: shaderc accepting every shader in the repo and `spirv-val` passing.
- **Size** — **M** plus an unpredictable packaging tail.

---

## 9. Arc R — Rendering (parallel track)

Order is forced: `F3 → R1 → R2 → R3` before any feature, because `R3` is the keystone and `R1`/`R2` are its prerequisites.

### R1 — Complete the D3D12 PSO key
Extend `PsoKey` from (layout, fill, topology) to (+ RTV format, NumRenderTargets, DSV format, sample count, depth mode, blend mode), threaded from a per-draw target state. Convert the per-program `DepthTestEnabled` flag (currently, deliberately, outside the key) into a per-draw depth mode `{TestWrite, TestNoWrite, None}`, preserving the debug-line always-on-top behaviour exactly.
- **Why now:** five downstream items each need one of those fields to vary per draw (sRGB RTVs, offscreen formats, depth-only shadow targets, MSAA, blending). **Zero visual change**, which makes it the one milestone verifiable purely by "nothing changed" plus a PSO-cache-count assertion.
- **Risk** — **LOW–MEDIUM**, nasty failure mode: a stale PSO returned for a mismatched target format is exactly what the debug layer sometimes catches and sometimes does not. Keep `PsoKey.Equals` **structural**, not hash-based. **Size** — **S.**

### R2 — Colour correctness: sRGB end to end
There is **zero colour management in the engine today** — no `srgb` or `gamma` anywhere in Core, both swap chains are `R8G8B8A8Unorm`, GL never enables `GL_FRAMEBUFFER_SRGB`. So sRGB-authored albedo is fed to lighting as if linear, and linear output is written raw to a display-sRGB buffer: **wrong space, twice.** Fix with **hardware** sRGB (sRGB texture formats on upload, sRGB backbuffer RTV / `glEnable(GL_FRAMEBUFFER_SRGB)`), never `pow(2.2)` in the shader — hardware decode happens *before* filtering, so bilinear and mip filtering are correct, which matters precisely on the tiled brush surfaces that dominate this engine's screen area. Convert the three hardcoded cornflower clear colours to linear so the backends still match.
- **Depends on** — `R1` (D3D12's flip model requires a `_UNORM` swap chain with sRGB-ness on the RTV, so the RTV format must be in the PSO key first), `F1` (albedo/emissive are sRGB; normal/roughness/metallic/AO stay linear — that classification lives in `.spectramat`).
- **Risk** — **MEDIUM, mostly social:** everything will look different, and "different" reads as "broken". Someone will want to retune the flat 0.2 ambient and the base colours; that retune is not a bug. Verify GL actually gets an sRGB-capable default framebuffer under the current GLFW window options — some drivers silently ignore the enable. **Size** — **S–M.**

### R3 — Offscreen render targets *(keystone)*
`RenderTargetDesc` + `RenderTarget` exposing plain `Texture` attachments **whose object identity survives a resize** (swap the GPU handle inside, not the wrapper — the same trick `ShaderProgram.TryReload` already uses, and without it every editor-viewport resize leaves materials pointing at destroyed textures). `BeginPass(target|null, clear)` / `EndPass()`. GL: FBO with texture attachments. D3D11: `RenderTarget|ShaderResource` texture with RTV/DSV/SRV, plus explicitly nulling SRV slots before a texture becomes an RTV. D3D12: its own RTV/DSV heaps plus RenderTarget↔PixelShaderResource barriers.
- **Attachments are plain `Texture`s on purpose:** the entire existing material/sampler path is then reused verbatim for post inputs and shadow lookups. No new binding concept enters the engine.
- **Unlocks** — shadows, post/tone mapping, FXAA, MSAA, material previews (`S7`), the Linux Uno viewport (`H3`), and eventually Hammer-style multi-viewport.
- **Depends on** — `F3`, `R1`, `R2`.
- **Risk** — **HIGH**, the largest single milestone. Three hard parts: D3D12 resource-state tracking (a missed barrier is a corrupt read the debug layer flags but a shipping build does not); resize correctness; and the fact that all six pipelines currently read `Renderer.FramebufferSize` for the viewport **and** write `camera.AspectRatio` from it, so both must become target-relative or offscreen renders are stretched. **Mitigate by shipping `BeginPass(null)` — the back buffer as a RenderTarget — first, as a pure zero-visual-change refactor,** before any real offscreen target exists. **Size** — **L.**

### R4 — Post-processing spine and tone mapping
A renderer-owned fullscreen triangle + `PostPass` helper; forward renders into an HDR target; resolve = exposure → tone map → hardware sRGB encode. This is what *completes* colour correctness by putting linear→display conversion in exactly one place.
- **Traps** — double-encoding sRGB (pick hardware, enforce it) and the GL-vs-D3D fullscreen UV/winding conventions, which show up as an upside-down or invisible image on exactly one backend. Deliberately not using `SV_VertexID`-based vertex-less draws: SpectraShade has no vertex-ID attribute. **Depends on** — `R3`, `R2`. **Size** — **M.**

### R5 — Multi-pass plumbing: array uniforms + per-frustum views
`ShaderProgram` has **no array-uniform overload on any backend today**, so cascade matrices and light arrays are simply not settable — even though SpectraShade already supports array uniforms in the language. Add `ReadOnlySpan<Matrix4x4>` / `ReadOnlySpan<Vector4>` setters, and generalise `Scene.BuildRenderView(Camera, view)` to `BuildView(in Frustum, view)` with the camera overload delegating, plus an engine-owned pool of `RenderView`s (one per pass) — which is also exactly what multi-viewport later needs.
- **Restrict the first version to `Matrix4x4[]` and `Vector4[]`** and document the `float[]` hazard: HLSL pads every array element to 16 bytes, so a naive span copy of a float array scrambles it. **Depends on** — `F3`. **Size** — **S–M.** **Risk** — LOW–MEDIUM.

### R6 — Shadow map v1: one directional light, one cascade, PCF
Depth-only target from a light-space ortho **fitted to a near slice of the camera frustum** — the world-fitted single map that a sealed-BSP engine could use is unavailable at any resolution here, because the world is unbounded. **Written from day one as an N-cascade shader with N=1**, so `R7` is a constant change plus a per-cascade pass rather than a rewrite. Needs: typeless depth textures on both D3D backends, comparison sampling on all three (this is what the `ComparisonFunc.None` comment in `D3D12Texture` becomes), and **`sampler2DShadow` added to SpectraShade** — a new type in the language plus `SamplerComparisonState` emission and a `SampleCompare` member rewrite in both generators.
- **Depends on** — `R3`, `R5`, `R1`.
- **Risk** — **HIGH**, the most cross-cutting item: the first feature needing a new render-target kind, a new sampler kind on three backends, a new language type, and a second cull pass simultaneously. Depth-bias constants are **not portable** across the GL/D3D clip-Z difference. Split shipping into "depth pass renders and can be shown as a debug quad" then "forward pass samples it". **Size** — **L.**

### R7 — Cascaded shadow maps
N=3–4 with per-slice ortho fitting, texel-snap stabilisation (mandatory — the camera is always moving in an open world), and split-boundary blending. Clamp the far cascade to a **shadow distance**, never the camera far plane, or the ortho box grows unboundedly. Atlas first (no array-slice RTV concept yet). **Depends on** — `R6`. **Size** — **M.**

### R8 — Multiple lights
A `Light` component on `SceneNode` parallel to `MeshRenderer`, collected during `BuildView` into a fixed-capacity, deterministically-ordered `RenderView.Lights` (nearest-N, ties broken by the existing spatial emission order so determinism survives).
- **State the ceiling honestly:** a global nearest-N list is wrong the moment more than N lights are on screen, and lights will pop as the camera moves. The correct answer is clustered/tiled forward shading, which needs storage buffers that **SpectraShade does not have** — blocked at the language level. Ship global-N; do not pretend it scales. **Depends on** — `R5`. **Size** — **M.**

### R9 — Metallic-roughness PBR with tangents
`StandardLayout` goes 8 → 12 floats (tangent4 with bitangent sign), and **the CSG mesh builder emits exact, seam-free tangents for the entire static world for free, because `F1`'s Hammer-style per-face u/v axes *are* the tangent frame.** That is strictly better than any derived approximation and matters precisely because tiled brush surfaces dominate the screen. Shading becomes Cook-Torrance GGX + normal mapping — **no language change needed**, every builtin required is already in the table. Screen-space derivative tangents are not an option: `Math.Ddx/Ddy` do not exist in SpectraShade.
- **Depends on** — `F1`, `R2`, `R8`.
- **Risk** — **MEDIUM–HIGH, and the risk is the vertex format, not the shading.** Changing `StandardLayout` changes every vertex array the CSG pipeline produces, so every equivalence and determinism oracle compares different data; they are structural so they should survive, but any snapshotted vertex data needs deliberate regeneration. **D3D11 bakes input layouts from the default lit shader's VS bytecode at mesh creation**, so the layout change and the shader change must land in the same commit or every D3D11 mesh fails validation. Tangent handedness is the classic silent-wrongness bug — pin the convention in a comment and test it on an asymmetric normal map. **Size** — **L.**

### R10 — Transparency and blend state
There is **no blend state anywhere today** (D3D11 never calls `OMSetBlendState`, GL never enables `GL_BLEND`, D3D12 hardcodes `BlendEnable = 0`). `BlendMode` becomes a property of **`Material`**, not of `ShaderProgram` — one shader will legitimately be used by both opaque walls and glass. `RenderView` partitions opaque from transparent **at build time** with a back-to-front sort, ties broken by the existing emission order so determinism holds; the drawer runs two dumb loops. Transparent brush faces split into their own chunk submesh (free, given `F1`).
- **Document the limitation up front:** sorted alpha is correct-ish, never correct, and per-chunk sorting for the static world is coarser still. Use a reusable scratch list and an index sort — `BuildRenderView` is allocation-free in steady state and must stay that way. **Depends on** — `R1`, `F3`, `F1`. **Size** — **M.**

### R11 — FXAA, then optionally MSAA
FXAA is one shader and zero backend state work once `R4` exists. MSAA costs work in all three backends at once (PSO sample count, three separately hardcoded rasterizer descs, three different resolve calls) and belongs on `RenderTargetDesc`, never on the swap chain — impossible on D3D12's mandatory flip model.
- **Honest tension:** FXAA smears exactly the thin high-contrast edges an editor viewport is full of — brush outlines, grid lines, selection highlights. **For the editor viewport MSAA is the right answer and FXAA is a stopgap.** If the editor lands first, expect that complaint. **Size** — FXAA **S**, MSAA **M.**

### R12 — Instancing
`VertexAttribute` gains `InputSlot` + `InputRate` (optional ctor params, source-compatible with the two existing call sites; both D3D backends already have the hardcoded per-vertex fields to flip, and D3D12's structural `PsoKey` picks instanced layouts up for free), plus a `(Mesh, Material)` batching pass in `BuildRenderView`.
- **Last on purpose.** The obvious beneficiary — thousands of Roblox-style parts — is already served by a completely different mechanism: brushes are merged into per-chunk static-world meshes by CSG, so they were never per-instance draws. The real beneficiary is high-count prop/model nodes, which do not exist in volume until model import lands. The awkward part is neither layouts nor buffers: it is that batching must preserve the deterministic emission order the `RenderView` tests assert. **Size** — **M.**

---

## 10. Arc H — Hosting the editor in Uno

**Ruling R‑10 applies: none of this blocks building a level.** The editing layer works in the existing Silk window first.

### H1 — `EngineHost` + `IRenderSurface`
Replace `IWindow` in `Renderer.Initialize/AcquireContext/ReleaseContext/Present` with an `IRenderSurface` (kind, native handle, pixel size, resized event, make/clear current, present); `WindowRenderSurface` keeps the standalone path byte-for-byte equivalent. `EngineHost` owns the render thread and exposes exactly four things to a UI thread: `SubmitInput`, `EnqueueCommand`, `RequestShutdown`, and a `FrameCompleted` event delivering **immutable** per-frame snapshots (selection ids, inspector values, compile/culling stats, and a batched scene-change list derived from the existing node events so a TreeView updates incrementally).
- **Embedded mode is engine-driven.** The render thread already owns the GL context, scene mutation and all GPU resource creation — including chunk mesh swaps inside the compile pump. Keeping that thread means the async CSG pipeline, the BVH, the selection set and the command queue all keep their existing single-threaded proofs verbatim, and decouples the viewport from XAML layout stalls.
- **Consequence to design for, not discover:** the UI is eventually consistent — an inspector text box shows local state and reconciles a frame later.
- **Depends on** — `E1`. **Risk** — **MEDIUM.** **Size** — **M–L.**

### H2 — Windows viewport: composition swapchain
Both D3D backends switch from `CreateSwapChainForHwnd` to `CreateSwapChainForComposition` plus `ISwapChainPanelNative::SetSwapChain` via hand-declared COM interop (no CsWinRT reflection — AOT). Panel size × composition scale feeds the **existing** `Renderer.SetFramebufferSize` latch, which is already exactly the right shape; only the resize *source* changes.
- **`ISwapChainPanelNative` availability and AOT behaviour under the chosen Uno flavour is unverified and is the single biggest unknown in this arc — spike it before committing.**
- **Embedded OpenGL on Windows is not supported.** There is no composition path for WGL; supporting it means `WGL_NV_DX_interop` or a per-frame copy, for a configuration nobody needs since D3D is available there. **Sign-off §9.3.** **Size** — **L.**

### H3 — Linux viewport
**Ship the readback fallback first** (render offscreen, `glReadPixels`, hand to Skia as a raster image) so Linux editing works from day one, slowly. Then, as an optimisation, zero-copy: create the engine's GL context in the GTK/Skia share group, render to an offscreen texture, fence, import as a `GRBackendTexture`, double-buffered.
- **Depends on** — `R3`, `H1`.
- **Risk** — **HIGH, the highest in the roadmap.** Whether Uno's Skia/GTK head exposes its GL context for share-group creation is unverified; if it does not, zero-copy is off the table permanently. Cross-context sharing plus fences is a classic source of driver-specific tearing. Sequencing the fallback first is the mitigation. **Size** — readback **M**, sharing **L and highly uncertain.**

---

## 11. Decisions that need sign-off before anything is built on them

1. **Does the Uno editor host the engine in-process, or run as a separate process reading `.spectramap`?** In-process gives a live graph, instant undo and no schema export; separate-process is more robust but forces a document model, a JSON entity-schema export, and no shared GPU resources — and it changes `H1`, `P5` and undo simultaneously. *(This is the highest-leverage unanswered question in the whole plan.)*
2. **SETTLED AND BUILT — the editing layer is its own assembly.** *(Was: a new `SpectraEngine.Editing` assembly, or in Core?)* `SpectraEngine.Editing` exists in the tree, references Core and nothing else, and a test asserts the boundary (no Silk.NET type, no `IWindow`); the executable is the only project that references it, which is what keeps gizmo/undo/tool code out of a shipped AOT game binary. `CLAUDE.md` carries the rule. Nothing may re-open this by adding editor code to Core.
3. **Is D3D-only embedding on Windows acceptable (no embedded OpenGL viewport)?** Accepting it makes `H2` a contained change; rejecting it means `WGL_NV_DX_interop` or a per-frame copy for a configuration D3D already covers.
4. **Is CPU readback an acceptable *shipping* path for the Linux viewport until GL sharing is proven?** Accepting unblocks Linux immediately at ~8 MB and a pipeline stall per 1080p frame; rejecting makes `H3` a research task that could stall indefinitely.
5. **Play-in-editor: diff-restore a snapshot onto the live graph, or spawn a fresh scene from the document and discard it?** Diff-restore preserves the incremental-compile pillar and is cheap now; fresh-scene means simulation state never needs rollback (a permanent tax saved once entities exist) but costs a full world recompile and doubles GPU residency on every Play.
6. **Is `.spectramap` text-JSON-authoritative, with binary as a later derived artifact?** Text gives git diffs, merges and greps for a scene-graph editor; binary-first loads faster but cannot be reviewed and will quietly become the only real format.
7. **Confirm Source-style entities as a third `SceneNode` payload with a source-generated schema — explicitly not an ECS.** This keeps the graph-is-the-spine decision and gives a far better no-code/property-panel story; it costs the data-oriented iteration performance an ECS would give at very high entity counts.
8. **How are prefab-internal targetnames scoped when a prefab is instanced twice — name prefixing or instance-scoped resolution?** Prefixing is explicit and greppable but breaks hand-typed targets inside the prefab; scoped resolution is ergonomic but surprising when someone deliberately wants to reach outside. **This bakes into every saved map and cannot be changed later without a migration.**
9. **Does the wireframe pipeline survive as a peer of forward, or become a shaded+wireframe *overlay* mode?** Overlay is what a Roblox-style editor actually wants and would delete two of the six pipeline classes; keeping it as a peer means it inherits shadows, post and blending it does not want.
10. **Accept that `R2` (sRGB) makes everything look different — brighter midtones, softer falloff — and budget a retune of the ambient and base colours?** Accepting fixes shading that is currently wrong in two places; deferring keeps the current look and blocks PBR, HDR and tone mapping.
11. **Is Vulkan still a real goal?** If yes, `R3`'s pass abstraction should stay genuinely pass-shaped (costs nothing now, a great deal later) and `S5`/`S9` gain priority; if no, `S9` is deleted and the "vulkan opt-in" carve-out becomes permanent.
12. **ANSWERED by [`docs/roblox-onboarding.md`](docs/roblox-onboarding.md) §2 — a scripting VM is needed, and it is Luau** (hybrid, Luau-first for gameplay with compiled C# staying the engine-facing language; `O0`–`O9`, with `O8` owning the `Script` payload). Binding consequence that milestone `P4` must respect: the `Entity` base class has to be designed knowing a VM is coming. The original tension is preserved below because it is why the answer went that way. *(Was:)* **Is C#-plus-rebuild acceptable for gameplay logic, or is a scripting VM eventually needed?** Mandatory AOT means an entity change is a rebuild-and-restart, which is the sharpest tension with Roblox's edit-and-see-it-instantly appeal; the no-code logic entities (`P6`) cover most gameplay without a rebuild, but a VM would change the entity base class's shape and must be decided before `P4` hardens.
13. **What is the default editing grid?** `VertexSnapper.GridSize` is 1e-4 (a welding concern, not a user grid) and `ChunkCoord.CellSize` is 32; a Roblox-like default of 1 world unit with 0.25/0.5/2/4 presets is proposed but the demo's part sizes suggest a different working scale.
14. **Multi-viewport (Hammer four-pane) — wanted at all, given the Roblox-first pillar?** Nearly free after `R3`, but it changes how the Uno shell is laid out, so answer before designing the shell.

---

## 12. Standing invariants every milestone inherits

- The `CsgBench openworld` verdict line must keep saying **world-size independent**. Any milestone touching the snapshot, slot map, footprint diff or trusted-diff contract (`F1`, `E4`, `E6`, `P7a`, `P7`) must show it, not assume it.
- The chunked-vs-monolithic equivalence oracles and the bit-identical determinism tests must stay green. **Re-baselining a snapshot is a deliberate act** — verify correctness first, or a bug is laundered into the oracle.
- Render thread owns the GL context, scene mutation and **all** GPU resource creation. Asset decode may be off-thread; `CreateTexture`/`CreateMesh` may not.
- AOT: no reflection, no `dynamic`, no runtime codegen. P/Invoke and source generators are the sanctioned escape hatches.
- `Brush` is immutable after construction, and the background compile depends on that being real, not aspirational.
- Scene event handlers must not mutate the graph. Anything reactive (entity spawn, editor response) defers to the next tick.
- Faces are identified by **plane index**. Everywhere.

---

## 13. Open questions

These are genuinely unresolved and are called out rather than guessed:

- **How does the Uno viewport consume the rendered image?** If same-process with a shared DXGI surface, `RenderTargetDesc` needs a `Shared` capability flag **from day one** — so this must be answered before `R3` is built, not after.
- **What OpenGL version does the engine actually get from Silk.NET, and what is the intended floor?** This gates `glProgramUniform` (4.1 — would erase the `Use()`/`SetUniform` seam entirely), texture arrays for cascades (3.0), and immutable textures (4.2).
- **How many shadow-casting lights should the design eventually target?** `R6`/`R7` shadow only the directional light. Point-light shadows need cube maps, and `Renderer.CreateTexture` has **no cubemap path at all** today. If "many shadowed dynamic lights" is real, the shadow atlas should be designed for it in `R6` rather than retrofitted.
- **Is there a target frame budget or hardware floor?** HDR format choice, cascade count, PCF taps and whether D3D12's current single-frame-in-flight full-fence sync becomes the bottleneck all turn on it — and every rendering milestone adds at least one pass, which with a full fence serialises against the GPU with no overlap.
- **ANSWERED by [`docs/formats-and-pipeline.md`](docs/formats-and-pipeline.md) §2.5, by splitting the question: the TEXT form keys by authored name** (survives reordering, is what a human edits and merges) **and the cooked `.smaterial` keys by offset**, resolved at cook time when the manifest is in hand. Both answers are right for their format. The durable risk it names: a `.smaterial` cooked against one shader version and loaded against another misaligns cbuffer offsets silently, which is why its `SHDR` section carries the shader hash and the loader refuses a mismatch loudly.
- **Do per-face materials support per-face *parameter overrides*, or only a whole-material reference?** Overrides mean the manifest must mark which parameters are per-face-instanceable and the submesh batching must account for them.
- **Are shader features per-material or per-render-pass?** Per-material (assumed) lets a user ship an unlit variant of their own shader; per-pass is simpler but cannot.
- **Should the engine shader stdlib (`engine/Lighting.spectrashade`) be user-overridable?** The reserved `engine/` prefix says no; if users must replace the lighting model, that is a shading-model plugin point and belongs in `S8`'s variant system, not in import shadowing.
- **`EngineInfo.ModelFormatVersion` / `TextureFormatVersion` are referenced nowhere.** Are they meant for engine-baked asset containers (the way `CompiledShaderFile` is), or aspirational placeholders? If the asset arc loads PNG and glTF directly, both should be **deleted** — a version constant that versions nothing is worse than none.
- **~~Should a brush model build its BSP eagerly or lazily?~~ ANSWERED by deletion** — a non-carving brush builds no BSP at all. `P7a` gives it a cached *mesh*; `CsgWorld`'s per-cell BSP is a pure function of the static placement list, so a part or entity-owned brush is by construction absent from it (`CsgWorld.cs:603`, `:617`). Collision comes from the brush's own convex hull (`docs/physics.md` §2.3a, `Y3`) and overlap queries from `SceneBvh` (`Y0`), neither of which is a BSP. See `P8`.
- **Should duplicated parts keep the same name (Roblox) or get a suffix (Hammer)?** Duplicate targetnames are allowed by design and the fire-all idiom depends on it, but accidental copy/paste duplicates are a common wiring bug — probably never block, but surface as "fires 3 entities" in the wiring UI.
- **Is a bounded undo depth acceptable, or should history be memory-budgeted?** Brush references in history are cheap and shared, but deleted subtrees held by remove-commands keep GPU meshes alive until eviction.
- **Unverified by construction:** nothing in this roadmap was built or run — another workflow held the tree. In particular, the claim that a depth-only VS consuming only `TEXCOORD0` validates against D3D11's lit-shader-derived input layout (`R6`) is a specification-level claim, not an observation. »
