# Networking: multiplayer and collaborative editing

**Status:** design. Nothing here is built. Every claim about the current tree was read out of source on 2026-08-21 and is cited by file and, where it matters, by line. Every external claim was verified against a live source on the same date. Claims that are *not* verified are labelled speculative.

**Amended 2026-08-21** to settle three of §9's open decisions — infrastructure (q1), P2P-versus-dedicated (q2) and whether shipped games need multiplayer (q4) — and to add ruling (5), the always-a-server model, with its topology matrix, shared-`Scene` mechanism and the §4.1 correction to the container model's structural claim. Every source citation added in that amendment was read out of the tree the same day.

---

## 0. The two goals, in the user's words

> "Basically one of the engine's goals is out of the box dedicated and P2P multiplayer that is almost as easy to set up and use as roblox. This is a pain point in all other engines. And also collaborative editing. Roblox has team create and this engine has the goal of replicating it. This is major for workflow."

Two goals, one substrate, five rulings that everything below obeys.

**(1) Listen server and dedicated server are one code path**, differing only in process topology. There is always exactly one authority. Game logic never knows which it is running under. "P2P" means *one peer hosts and others connect* — not an authority-free mesh, which is a cheating and state-divergence problem nobody wants to debug. **Ruling (5) extends this one leg further: singleplayer is also that same code path.**

**(2) Gameplay replication and collaborative editing are separate systems** sharing identity, schema and transport, and nothing above that. Gameplay is high-frequency, loss-tolerant, interest-filtered and authoritative. Editing is low-frequency, must-not-lose, full-fidelity and everyone-sees-everything. Forcing one mechanism to serve both produces something bad at each. This ruling is not merely a preference — §5.2 shows it is *forced by code that already exists*.

**(3) Collaborative world editing replicates brush edits, never compiled geometry.** A brush edit is tens to a few hundred bytes; the compiled chunk it produces is kilobytes, and it is derived data that the map format already refuses to store. **The justification for this ruling has been corrected — see §1.2. The conclusion stands; the reason given for it was wrong, and building a divergence alarm on the wrong reason would have produced false alarms on the default developer setup.**

**(4) Part of Roblox's ease is infrastructure, not engine.** Roblox hosts the servers and solves NAT traversal. Matching the *API* ease is an engine problem and is solvable here. Painless P2P across home routers is an operational commitment somebody must make — **and for this project it has been made: the user operates the infrastructure and will run the Spectra rendezvous and relay** (settled 2026-08-21, §7). The ruling does not dissolve, it inverts: the engine still owes a STUN client, hole punching, a relay client and the `ISessionRendezvous` seam, and still owes *self-hostable* binaries, because anyone else shipping on this engine inherits the operational question we have answered for ourselves. What changes is that P2P is a shipping mode here rather than an aspiration, so `N6` is on the critical path (§8.3) instead of off to the side.

**(5) There is always a server, and therefore there is no singleplayer code path.** Singleplayer is a server with exactly one local client and no listener socket bound. Listen/P2P is that same server with a socket bound and remote peers attached. Dedicated is that same server with no local client. Three topologies, one code path, and the authority boundary present from day one — which is what makes "add multiplayer later" stop being a rewrite, because logic that assumed direct client-side access fails on day one rather than on the day someone first hosts. Source, Quake, Unreal and Roblox all do this. **The local client is not a peer and does not replicate to itself: it shares the `Scene` instance with the server** — the server mutates it, the local client renders it, no serialization, no copy, no latency, no second code path. §4.1 says exactly which objects are shared, which are per-role, what this model can hide from a developer, and how that is caught without a duplicate implementation. Precision that matters: it is every *session* that has exactly one authority, not every *process* — a joining player's process runs no server at all, and is the only topology in which `SpectraNet.Role` lacks `Server`.

### What the whole thing looks like end to end

A developer marks a property replicated by putting the node in `Workspace` and adding `[Replicated]` to a partial property (or `replicated = true` to a Luau `Entity.define` keyvalue — the same three bits in the same `.sentdef` record either way). The shared source generator emits a dirty-bit setter, a slot-indexed writer and a schema digest; no reflection is involved anywhere. At runtime the authority ticks at a fixed rate, walks the dirty list, filters it per client against a replication grid keyed on the client's interest anchor, packs deltas into an unreliable-sequenced channel with per-field acked baselines, and sends. On the client, `ReceivePump` drains the socket queue on the render thread before `SceneManager.Update`, applies absolute values through setters that early-out on equality, and two players see each other move — the remote one interpolated 100 ms in the past, the local one predicted forward from the last acknowledged input. Meanwhile two designers open the same map: each opens an `UndoStack` transaction on grab, which requests a soft lease on `(nodeId, domain)`; the drag runs immediately and locally, its per-frame values ride the *presence* channel as absolute previews that observers apply to the real node, and on release exactly one committed `SetBrushCommand` — plane array plus per-face records, addressed by `SceneNode.Id` — crosses the reliable edit channel, is sequenced by the authority, and lands on every peer, each of which recompiles the two or three dirtied 32-unit cells locally in ~0.05–0.1 ms. Neither peer ever sees a vertex.

And a developer who never types `host`, never opens a port and ships a strictly single-player game runs all of the first half of that paragraph anyway, minus the socket: a fixed-rate authority ticking a `Scene` that one local client renders directly. They pay the tick and the authority indirection; they pay no serialization, no interest filtering and no bytes. The day they add a second player, nothing about their game code's shape changes.

---

## 1. What the engine already has

This design is unusually cheap because four of the five things it needs already exist and were built for other reasons.

### 1.1 The command system is already most of a replication log

Verified by reading `SpectraEngine.Editing/Commands/` and `Undo/UndoStack.cs`:

- **Guid addressing.** `SetTransformCommand.NodeId`, `SetLocalTransformCommand.NodeId` and `SetBrushCommand.NodeId` are `Guid`s resolved through `Scene.TryFindById` (`Scene.cs:186`), an O(1) index maintained from `OnNodeAdded`/`OnNodeRemoved`. `IEditorCommand`'s contract forbids object references outright, because undo of a delete recreates the node under the same id. A Guid is machine-independent, and **a miss is a documented no-op rather than an error** — exactly what an out-of-order remote command needs.
- **Absolute before/after values, never deltas.** `SetTransformCommand` carries `BeforePosition`/`BeforeRotation`/`AfterPosition`/`AfterRotation` (`SetTransformCommand.cs:87–103`); `SetBrushCommand` carries whole `Before`/`After` brushes. This is what makes remote application *idempotent* and last-writer-wins *convergent* rather than merely plausible. It is also what makes rollback affordable: the transform setters early-out on exact equality and `CsgCompileCache` keys on exact values, so replaying a value a peer already holds costs a comparison and dirties zero chunk cells.
- **One transaction per gesture.** `GizmoTool.BeginDrag` opens it, per-frame commands coalesce through `ICoalescingCommand.TryAbsorb`, `CommitDrag` lands one entry. That is simultaneously the natural broadcast unit and the natural remote-undo unit.
- **A cancel path that is already correct.** `UndoStack.CancelTransaction()` calls `RollBack` on every recorded command, and its contract is that the scene ends up exactly as the gesture found it, reaching even nodes that left the scene mid-gesture. **This is the conflict-rollback path**; no new rollback machinery is written anywhere in this design.

**But be honest about the fraction.** The complete command vocabulary today is `SetTransformCommand`, `SetLocalTransformCommand`, `SetBrushCommand`, `CompositeCommand` and the `ICoalescingCommand` interface. There is no add, remove, reparent or rename command. `SceneNode.Parent` is `{ get; private set; }` (`SceneNode.cs:64`). `AddChild` appends — there is no `InsertChild(index)`, so sibling order cannot even be expressed, and `E6` already identifies sibling order as determining traversal order → `BrushPlacement` order → carve order. `SceneNode.Name` is a bare `{ get; set; }` (`SceneNode.cs:62`) with no event; `Scene` raises only `NodeAdded`, `NodeRemoved` and `NodeTransformChanged` (`Scene.cs:98/108/118`), so `Scene.NodeRenamed` does not exist despite ROADMAP ruling `R‑7` discussing it as though it does.

Level building is overwhelmingly create, delete, duplicate, group, reparent and rename. **The transform-and-shape third of editing has a wire-ready log. The hierarchy-and-identity two thirds do not exist yet and must be designed wire-first inside `E6`** — which is much cheaper to do now, while `E6` is unbuilt, than to retrofit. One concrete consequence: `SetTransformCommand` writes `LocalPosition`/`LocalRotation` (`SetTransformCommand.cs:173–174`), i.e. *parent-relative* values, so an edit op is only meaningful under an agreed parent chain. Hierarchy ops are ordering-critical, not merely additive.

### 1.2 Deterministic CSG — with the premise corrected

Ruling (3)'s conclusion is right and is the design's best property. Its stated justification is factually wrong for the case that matters, and one of the reviewed designs built a divergence alarm on it.

`SpectraEngine.Core/Bsp/SimdPlane.cs:23–57` branches on `Vector.IsHardwareAccelerated` and `Vector<float>.Count`, computes the vector body as `vx*nx + vy*ny + vz*nz + offset`, and sends the tail through a **different expression**, `Plane.DotCoordinate`. `Bsp/Polygon.cs:124` gates SIMD entry on `_vertices.Length >= Vector<float>.Count * 2`. `Vector<float>.Count` is 4/8/16 by ISA, and `Vector.IsHardwareAccelerated` also differs between a JIT run and a NativeAOT publish compiled to a baseline instruction set. A 20-vertex polygon therefore takes the SIMD path with a 4-vertex scalar tail on AVX2 (20 ≥ 16) and the pure scalar path on AVX-512 (20 < 32). .NET guarantees no bit-identity between those paths, and FMA contraction differs between the two expressions.

The repo's oracles (`ChunkBspEquivalenceTests`, `ChunkMeshEquivalenceTests`, `IncrementalCompileTests`, `PlacementEquivalenceTests`) pin **same-process, same-binary** determinism. That is strictly weaker than cross-machine bit-identity.

**Restated ruling (3):** *the CSG compile is deterministic for a given binary on a given ISA; cross-machine and cross-build bit-identity is NOT guaranteed and nothing may depend on it.* Three consequences, all binding:

1. **Convergence is checked by hashing the replicated INPUTS** — the ordered placement list plus plane and face records, or a canonical `.smap` document digest — **never the compiled vertex and index arrays.** A compiled-geometry digest would fire on an AVX2 client next to an AVX-512 one, and would fire on the *default* developer configuration of an AOT-published dedicated server against a JIT dev client. A false divergence alarm is indistinguishable from real corruption.
2. **Client prediction must query geometry with a skin width** larger than the plausible divergence, and server validation must never be a bit-exact containment test.
3. If cross-machine bit-determinism is ever genuinely wanted — `D13`'s bake oracle across a two-host CI matrix, or rollback-style prediction — that is a **CSG determinism mode** pinning lane width or forcing the scalar path, priced against those arcs, not this one.

Nothing about ruling (3) as a *design* changes. A sub-ULP vertex difference between two peers is invisible: each peer compiles for its own rendering, and every authoritative query runs on the authority's world.

### 1.3 The chunk grid and BVH as spatial structures

`ChunkCoord.FromPosition`, `ChunkCoord.CellSize`, `ChunkGrid.ComputeFootprint(in BrushPlacement)` and `SceneBvh` all exist and are already used for culling and per-cell compilation. Two corrections to how they get reused:

- **`ChunkCoord.CellSize = 32.0f` is a frozen geometry invariant, not a tunable.** `ChunkGrid.WeldBand` is pinned to it as `2 * max(Polygon.Epsilon, VertexSnapper.GridSize)` (`ChunkGrid.cs:28–37`), with a comment stating the W2 weld-equivalence oracle depends on the band. Gameplay relevancy must **not** inherit that constant — see §4.3.
- **`ChunkGrid.ComputeFootprint` returns `ChunkCoord[]`** (`ChunkGrid.cs:141`) — it allocates per call. Both the edit broadcast scoping and the gameplay world-edit scoping call it per op. It needs a span or caller-buffer overload before it sits in a network path, or a busy session allocates per op per tick inside a frame body that is otherwise allocation-free in steady state.
- `SceneBvh` exposes `Raycast` and `QueryFrustum` and **no sphere query**, which is a further reason interest management gets its own structure rather than borrowing one.

### 1.4 The data-driven runtime, and what headless actually costs

`docs/formats-and-pipeline.md` §3 makes the shipped runtime data-driven, so "the dedicated server is the same binary with a flag" is the right shape. Ruling (5) sharpens it further — the flag does not select a *server*, which is always running, but selects whether a **local client** is attached (§4.1) — and raises what headless is worth: it is one cell of a topology matrix every game exercises, not a deployment feature. The cost is smaller than expected on one axis and larger on another.

**Cheaper than expected — the renderer.** `Graphics/Renderer.cs` is 231 lines and `IWindow` appears in exactly four **virtual** methods with trivial bodies: `AcquireContext` (`:76`), `ReleaseContext` (`:82`), `Present` (`:88`) and `Initialize(IWindow)` (`:144`), each of the form `window.GLContext?.…`. A `HeadlessRenderer` overrides all four and needs no new seam. **`N1` must not be sequenced behind `H1`** — take `H1`'s `IRenderSurface` when it lands, but do not wait for it, and never invent a parallel headless seam. The existence proof that this works is already in the tree: `Test/SpectraEngine.Bsp.Tests/FakeRenderer.cs` is a GPU-free `Renderer` driving the entire chunked static-world compile with no window and no context, and it throws from `CreateShader` on the principle that a caller reaching it has silently grown a real GPU dependency.

**More expensive than expected — the engine.** `Engine.Run` (`Engine.cs:105–232`) unconditionally calls `SilkPlatform.EnsureRegistered()` (`:112`), `Window.Create(options)` (`:124`), `_inputManager.Initialize(_window.CreateInput())` (`:136`), `_renderer.SetFramebufferSize(_window.FramebufferSize)` (`:143`), and then pumps `_window.DoEvents()` (`:175`) on the main thread while applying **four main-thread latches** — pending title, `ApplyPendingCursorMode()` (`:194`), `ApplyPendingWindowMode` (`:200`) and the close latch. A headless run has no OS-event thread, so the latch protocol needs a no-op owner rather than deletion. `RenderLoop` also hard-wires `_sceneManager.LoadDemoScene(_renderer, _assetManager)` (`:252`) — **there is no map-loading path**, so a dedicated server before `P2` can serve the demo scene and nothing else, and must be described that way. The loop is uncapped (`VSync=false`, `FramesPerSecond=0`, no sleep), so a headless build of it spins a core; a tick-rate cap is part of the milestone.

**And one genuinely good piece of news:** a dedicated *collaboration* server needs none of this. The collab authority needs `Scene`, `SceneNode`, `Brush`, the op codecs, `MaterialRegistry.Intern` (a static, pure string→int table) and the `.smap` codec — all renderer-free. It never calls `Scene.RebuildStaticWorld(Renderer)` or `Scene.ProcessStaticWorldCompilation(Renderer, ILogger)`, because it neither draws nor runs BSP queries. **`T8` must not be sequenced behind headless-game-server work.**

### 1.5 The shared source generator

`docs/console.md` §4.4 already rules that there is **one** generator project, `SpectraEngine.Generators` (netstandard2.0), hosting `EntityGenerator` (`P5`), `LuauBindingGenerator` (`O5`) and `ConVarGenerator` (`C0`), and restates `O5`'s "do not create a second analyzer project" as binding on all of them. Replication is a **fourth emitter inside that project**, not a fourth project and not a reflective serializer. It reuses their interned-name→dense-slot emitter and the closed `KeyvalueType` vocabulary, and it claims two bits those documents already reserved: `ConVarFlags` bit 8 (reserved by `console.md` line 151 with "Do not reuse; do not renumber", dropped at the time because "there is no networking and none is planned" — this is that later) and `.sentdef`'s keyvalue `Flags` bits 3+ (bits 0–2 are `readOnly`/`hideInEditor`/`requiresRestart`).

### 1.6 Other existing machinery this design consumes

- `Scene.RefreshStaticWorldMaterials()` is **public** (`Scene.cs:394`) and exists precisely for late-arriving materials — it is the self-heal for an edit that referenced an asset the receiver did not have yet.
- `MaterialRef.Id` comes from `MaterialRegistry.Intern` and is meaningful "for the life of the process", i.e. call-order dependent. `docs/formats-and-pipeline.md` §8 already pins that it is never written to disk. **The wire inherits that rule verbatim** — see §3.3.
- The depth-off `DebugDraw` line pass already carries gizmos, the marquee and the selection highlight on all three backends, so presence rendering costs zero new render code.
- `EditingSelfTest` and `Test/SpectraEngine.Editing.Tests/EditingAssemblyBoundaryTests.cs` (which already asserts via `GetReferencedAssemblies` that no Silk.NET assembly is referenced) are the templates for §6.3's self-tests and §3.1's boundary enforcement.
- `Brush.Transform` is `{ get; set; }` (`Brush.cs:128`) despite brush immutability being load-bearing. It is ignored by the compile — the node's world matrix places the brush — so it is harmless today, but **no peer may replicate it and no decoder may treat it as meaningful state.** Worth a one-line invariant so nobody serializes it "for completeness".

---

## 2. Architecture at a glance

```
                        ┌─────────────────────────────────────────┐
                        │  SpectraEngine.Generators (netstandard2.0)
                        │  EntityGenerator │ LuauBindingGenerator │
                        │  ConVarGenerator │ ReplicationEmitter   │  ← 4th emitter
                        └───────────────────┬─────────────────────┘
                                            │ emits slots, codecs, schema digest
                                            ▼
  ┌──────────────────────────────────────────────────────────────────────┐
  │  SpectraEngine.Core                                                   │
  │    Net/  INetTransport, NetChannel, NetId, NetWriter/Reader,          │
  │          NetHandshake, NetCrypto, NetInterestGrid                     │
  │    Records: NodeRecord / BrushRecord / FaceRecord / KeyvalueType      │
  │             ← ONE definition, three codecs (.smap, .scmap, wire)      │
  └───────────────┬──────────────────────────────────┬───────────────────┘
                  │                                  │
   ┌──────────────▼───────────────┐   ┌──────────────▼──────────────────┐
   │ Gameplay replication (N10+)  │   │ SpectraEngine.Editing/           │
   │  authority tick, dirty masks │   │   Collaboration/  (T0+)          │
   │  interest grid, baselines    │   │  CollabAuthority, leases,        │
   │  prediction seam, ownership  │   │  presence, session, drafts       │
   └──────────────┬───────────────┘   └──────────────┬──────────────────┘
                  │  Snapshot / GameInput / Event     │  EditOps / Bulk / Presence
                  └────────────────┬──────────────────┘
                                   ▼
              SpectraEngine.Net.LiteNetLib  (the ONLY assembly naming a library type)
```

Two systems, one substrate, one transport — **and a role composition, not a mode switch**: the same assemblies compose into the three topologies of §4.1, with the transport row simply absent in a solo session. Note the consequence of §9 q4 being settled: unlike `SpectraEngine.Editing`, every box on the gameplay side of this diagram — Core's `Net/`, the replication tick, and the transport adapter at the bottom — ships inside an AOT game binary, which is what makes `N0` a gate. `SpectraEngine.Editing` continues to name no transport type — the same discipline that already keeps it free of Silk.NET and `IWindow`, enforced by extending `EditingAssemblyBoundaryTests` to ban `System.Net.*`.

---

## 3. The substrate

### 3.1 Transport

**Decision: LiteNetLib 2.1.4, behind a Spectra-owned `INetTransport` in a new `SpectraEngine.Net` assembly, with the adapter isolated in `SpectraEngine.Net.LiteNetLib`. One transport for both systems.**

Verified against the GitHub API on 2026-08-21: `archived: false`, `license.spdx_id: MIT`, 3,608 stars, 8 open issues, `pushed_at: 2026-06-18`, latest release **2.1.4** published 2026-05-19. Its csproj targets `net8.0;netstandard2.1` with `IsTrimmable=true` and `EnableTrimAnalyzer=true`, uses `AllowUnsafeBlocks` behind a `LITENETLIB_UNSAFE` define, and is **pure managed with no native binaries**. It provides, on one connection, exactly the channel matrix needed: reliable-ordered, reliable-unordered, reliable-sequenced, unreliable-sequenced and raw unreliable, plus MTU discovery and LAN multicast discovery.

`IsAotCompatible` is **absent**, which is more than a formality — that property is what additionally enables the AOT analyzer, so dynamic-code patterns were never checked by the library's own build. Per this project's standing invariant ("no dependency is adopted on an inferred AOT posture — verify with an actual `dotnet publish -p:PublishAot=true` of a throwaway console app"), the spike is a **gate on `N1`**, not a formality.

**Expect it to pass.** One reviewed design claimed the risk is that "the analyser reports per-assembly, not per-namespace"; that is wrong. Trim and AOT analysis is **reachability**-based. `LiteNetLib/Utils/NetSerializer.cs` — the only reflective code, using `System.Reflection`, `BindingFlags` property enumeration and `Delegate.CreateDelegate` — is standalone over `NetDataWriter`/`NetDataReader` and references neither `NetManager` nor `NetPeer`. An adapter that never touches it leaves it unreachable, ILLink removes it, and no warning is emitted. Run the spike anyway.

**Rejected, with reasons:**

- **`System.Net.Quic` — disqualified on a hard fact, for *both* systems.** It exposes only `QuicListener`, `QuicConnection` and `QuicStream`. dotnet/runtime#53533 ("QUIC Datagram API") is **still open**, labelled `api-suggestion`, created 2021-06-01 and last touched 2026-07-25; the follow-up #123418 was closed as a duplicate. Without unreliable datagrams every gameplay packet is head-of-line blocked, which is the exact failure unreliable channels exist to prevent. Two of the reviewed designs suggested QUIC anyway for the *edit* channel, on the strength of per-stream flow control. **That is closed here.** QUIC requires Windows 11 / Server 2022+, a manually installed `libmsquic` 2.2+ on Linux, and Homebrew plus a `DYLD_FALLBACK_LIBRARY_PATH` dance on macOS. Adopting it for editing gives *the editor* — the component that must run on a designer's laptop and on a Linux collaboration host — a native dependency, plus a second crypto story, a second NAT story, a second AOT spike and a second set of channel semantics, all to buy something the rate-capped `Bulk` channel (§3.2) already provides.
- **Riptide** (MIT, not archived, 1,285 stars, pushed 2026-08-19) is healthy. The rejection is a design-shape mismatch, not a health problem: its three send modes are Unreliable, Reliable-**unordered** and Notify, and the edit channel needs gap-free ordering. It could be layered with sequence numbers. It stays a live fallback.
- **ENet-CSharp** (nxrighthere, MIT, 905 stars): last pushed 2025-07-03 — thirteen months stale — and a native P/Invoke wrapper needing per-RID binaries.
- **Valve GameNetworkingSockets** (BSD-3, 9,848 stars, pushed 2026-08-06) stays **warm as the fallback for exactly one reason: ICE.** Its README confirms peer-to-peer "NAT traversal through google WebRTC's ICE implementation" in the open-source build, and that "some features are only available on Steam, such as Steam's authentication service, signaling service, and the SDR relay service." If hand-rolled STUN + rendezvous + relay (§7) proves larger than budgeted, GNS is the only candidate that already has traversal. It is C++ with per-RID natives — the packaging tax `D17` already worries about for Assimp.
- **Hand-rolled reliable UDP:** months of subtle bugs to reproduce an MIT library. It is the fallback only if the AOT spike disqualifies everything.

```csharp
// SpectraEngine.Net/INetTransport.cs — the seam. Names no library type.
public enum NetChannel : byte
{
    /// <summary>Reliable ordered. Handshake, role changes, kicks, session state.</summary>
    Control      = 0,
    /// <summary>Reliable ordered. Collaborative edit ops. Gap-free or the world diverges.</summary>
    EditOps      = 1,
    /// <summary>Reliable ordered. Spawns, despawns, reliable remotes, world edits.</summary>
    GameReliable = 2,
    /// <summary>Unreliable SEQUENCED. Property deltas, transforms. Drop stale, never reorder.</summary>
    GameSnapshot = 3,
    /// <summary>Unreliable sequenced. Client input frames; a lost one is superseded next tick.</summary>
    GameInput    = 4,
    /// <summary>Reliable ordered, fragmented, RATE-CAPPED, yields to EditOps.
    /// Initial world sync, asset transfer, resync tails.</summary>
    Bulk         = 5,
    /// <summary>Unreliable. Editor presence: cameras, cursors, in-progress drag values.</summary>
    Presence     = 6,
}

/// <summary>(slot, generation) so a stale handle fails a check rather than
/// addressing a recycled peer — the idiom O5 uses for Luau node handles.</summary>
public readonly record struct NetPeer(ushort Slot, ushort Generation)
{
    public static readonly NetPeer None = default;
    public bool IsValid => Generation != 0;
}

public interface INetTransport : IDisposable
{
    void Start(in NetEndpointConfig config);

    /// <summary>Drains sockets and raises every event SYNCHRONOUSLY on the calling
    /// thread. Called once per FRAME from the render thread, in the slot beside
    /// AssetManager.PumpPendingUploads(). Never a callback on a socket thread.</summary>
    void Poll();

    event Action<NetPeer>? PeerConnected;
    event Action<NetPeer, NetDisconnectReason>? PeerDisconnected;
    event NetReceiveHandler? Received;

    void Send(NetPeer peer, NetChannel channel, ReadOnlySpan<byte> payload);
    void Broadcast(NetChannel channel, ReadOnlySpan<byte> payload, NetPeer except = default);
    void Disconnect(NetPeer peer, NetDisconnectReason reason);

    int PeerCount { get; }
    ref readonly NetTransportStats Stats { get; }
}
```

The adapter forces `UnsyncedEvents = false` in its constructor and a test asserts it. That single flag would deliver a peer's brush edit on a socket thread and mutate the scene graph from under a compile snapshot, corrupting the `CsgWorldCarry` pairing, the BVH, the id index and the selection set simultaneously — with a symptom that looks like a CSG bug.

A `LoopbackTransport` ships from day one. It is not a toy: it really serializes to bytes, and it is what lets a headless server and one or more clients run in one process with no sockets and no ports — the role `FakeRenderer` plays for CSG, and the thing that makes §6.3's self-tests possible in CI.

**But it is not what a singleplayer game runs on, and that distinction is load-bearing.** Under ruling (5) the local client shares the server's `Scene` and moves no bytes at all (§4.1); a loopback transport that genuinely serializes would make solo play pay the full replication cost for nothing, and a loopback transport that *didn't* serialize would be a second, untested code path pretending to be the first. So `LoopbackTransport` is the **test and diagnostic** rig — `N14`'s three contexts, `CollabSelfTest`'s two sessions, and `net_strictlocalclient` — never the production solo path. `INetTransport` is not even constructed in a singleplayer session with no listener bound.

### 3.2 Channel policy

| Channel | Delivery | Carries | Why |
|---|---|---|---|
| `Control` | reliable ordered | handshake, roles, kicks | small, must arrive, order matters |
| `EditOps` | reliable ordered | committed editor transactions | carve order is determined by op order |
| `GameReliable` | reliable ordered | spawn/despawn/reparent, world edits, reliable remotes | a create must precede its child's create; pre-order emission gives parent-before-child free |
| `GameSnapshot` | unreliable sequenced | property deltas, transforms | loss costs a resend, never correctness (§4.2) |
| `GameInput` | unreliable sequenced | client input frames | a lost frame is superseded next tick |
| `Bulk` | reliable ordered, rate-capped | join snapshot, asset transfer | must never head-of-line-block `EditOps` |
| `Presence` | unreliable | editor cameras, cursors, in-progress drag previews | 15–20 Hz, never journaled, never persisted |

Everything reliable-ordered is the classic mistake for continuously-changing state: a lost transform stalls every later transform behind it, and the stalled data is stale anyway. The `Bulk` rate cap is what makes one transport sufficient for both systems.

### 3.3 Identity and the wire schema

**Two identity spaces, deliberately.**

- **Collaborative editing addresses nodes by `SceneNode.Id` (Guid, 16 bytes).** Required, not merely convenient: an edit legitimately names a node the receiver has not created yet, and must survive delete → undo → recreate, which is the exact property `IEditorCommand`'s Guid addressing was built to guarantee. A session-scoped id would go stale precisely where the existing design was careful not to. Edits are human-rate, so 16 bytes is free.
- **Gameplay addresses objects by a session-scoped `NetId`** — a packed `(index, generation)` `uint`, allocated by the authority, with the Guid riding along **once** in the interest-enter message. Do the arithmetic before choosing: 200 replicated objects at 20 Hz is 200 × 16 × 20 = 64 KB/s *in identifiers alone* per client, 512 KB/s across eight clients on the host's uplink, before a byte of position data. With 4 bytes it is 128 KB/s. Per message: `NetId(4) + mask(1) + position(12)` makes identity 24% of the packet; with a Guid it would be 29 of 41 bytes.

```csharp
public readonly record struct NetId(uint Value)
{
    private const int IndexBits = 20;                    // ~1M live objects
    private const uint IndexMask = (1u << IndexBits) - 1;
    public static readonly NetId None = default;
    public int  Index      => (int)(Value & IndexMask);  // dense -> flat array, not a dictionary
    public uint Generation => Value >> IndexBits;        // stale packet resolves to nothing
    public bool IsValid    => Value != 0;
}
```

Authored map nodes derive their index from the `.scmap` `NODE` section's pre-order position, which `docs/formats-and-pipeline.md` §2.7 already pins to `SceneNode.Traverse()` order with `ParentIndex < SelfIndex` as an invariant. Both sides therefore agree on every authored node's id **with no negotiation and no spawn message** — the entire authored scene costs zero bytes of spawn traffic. Dynamic nodes allocate above `authoredNodeCount`. NetIds within one snapshot are written ascending as 7-bit varint deltas, so a spatially coherent interest set typically costs one byte per object.

**One vocabulary, three codecs.** `NodeRecord`, `BrushRecord`, `FaceRecord` and `KeyvalueType` are defined **once** in `SpectraEngine.Core` (`KeyvalueType` is already scheduled there by `D14`). `.smap` (JSON), `.scmap` (binary) and the wire are three codecs over the same records, and the wire reuses `.scmap`'s `BRSH` `FaceRecord` and `NODE` record layouts verbatim rather than defining a parallel schema. Pinned by a test: author a node → encode to wire → decode → save as `.smap` → assert byte-identical to a direct `.smap` save of the original. Whichever of `D11`/`D12`/`N2` reaches these records first defines them; the others consume.

**Three inherited rules, each of which is a real bug otherwise:**

1. **`MaterialRef.Id` never crosses the wire.** It is an index into the process-local, append-only `MaterialRegistry`; peer A interning `wall` first and peer B interning `floor` first makes id 1 mean different materials, and the world is silently mis-textured. Materials travel as a session-table index (the same idea as `.scmap`'s `ASTB`) or as a normalized content-relative path. **Test it by interning an unrelated material first on one side**, or the bug hides behind coincidentally matching order.
2. **`Brush.LocalFaces` never crosses the wire** — it is derived from `LocalPlanes`, under the same zero-derived-data rule `.smap` already enforces. Neither does `Brush.Transform` (§1.6).
3. **String tables intern in first-reference order**, never dictionary iteration order — the rule `.scmap`'s `STRT` is already pinned to, and for the same reason: dictionary order leaks the runtime hash seed.

**Endianness:** little-endian for every scalar via explicit `BinaryPrimitives.*LittleEndian`. The single big-endian field in the whole protocol is the Guid, written with `Guid.TryWriteBytes(dst, bigEndian: true, out _)` — RFC 4122 order, byte-identical to `.scmap`'s NODE record and character-for-character identical to the `.smap` hex spelling, so a packet dump and a `.smap` diff line up visually.

**Float fidelity is asymmetric, and it is a hard rule.** `ROADMAP.md` `P2` pins that "a one-ULP perturbation of a plane offset shifts the carve, the snap grid, the weld band and the per-cell BSP, and the determinism guarantees silently stop meaning anything across a save." So:

- **Editing transmits raw IEEE-754 bits, never quantized.** A quantizing edit wire is a lossy save round-trip on every hop.
- **Gameplay may quantize**, because its state is derived, corrected 33 ms later, and never feeds a CSG cache.

The codec takes a `NetChannel` and asserts in debug that lossy encodings are illegal on `EditOps`, so this is structural rather than a comment.

Gameplay position quantisation is **relative to the node's cell origin**, not to a world-space fixed-point grid — a world grid is a sealed-world assumption whose precision degrades with distance from the origin, which is exactly what brush-local frames exist to prevent. 13 bits per axis over a 32-unit cell is ~4 mm; rotation is smallest-three (2 + 3×10 bits); **scale is not sent for brush-bearing nodes at all**, both to save bytes and because `Scene.DescribeNonRigidDefect` rejects non-rigid brush transforms and freezes all world compilation behind one log line — a replicated scale write on a brush node would be a global, silent, network-triggered failure.

**Note what is *not* shared:** editing replicates `LocalPosition`/`LocalRotation` (parent-relative, bit-exact); gameplay replicates a cell-relative quantized world position. "One definition of a node, a brush, an entity and a property" holds. "One definition of a transform on the wire" does not, and should not. Say so rather than letting a reader infer a shared codec that cannot exist.

### 3.4 Source-generated replication metadata

A **fourth emitter** in the existing `SpectraEngine.Generators` project.

```csharp
[AttributeUsage(AttributeTargets.Class)]
public sealed class ReplicatedTypeAttribute(string wireName) : Attribute
{
    /// <summary>Stable across builds. NOT the C# type name — renaming a class
    /// must not be a wire break.</summary>
    public string WireName { get; } = wireName;
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class ReplicatedAttribute : Attribute
{
    public NetReliability Reliability { get; init; } = NetReliability.Snapshot;
    public NetRate        Rate        { get; init; } = NetRate.Normal;   // every tick / 3rd / 10th
    public NetRelevance   Relevance   { get; init; } = NetRelevance.InterestLocal;
    public float          Quantize    { get; init; }                     // diagnostic if Reliable
    /// <summary>Replicated to the owning client only — health, inventory.
    /// Prevents wallhack-by-replication.</summary>
    public bool           OwnerOnly   { get; init; }
}

// What the developer writes:
[SpectraEntity("game_pickup")]
public partial class Pickup : Entity
{
    [Replicated]                    public partial bool Available { get; set; }
    [Replicated(OwnerOnly = true)]  public partial int  Ammo      { get; set; }
}

// What the generator emits (sketch):
public partial class Pickup
{
    private bool __v_Available;
    public partial bool Available
    {
        get => __v_Available;
        // Equality early-out is the SAME idiom as SceneNode's transform setters,
        // so replaying an authoritative value the object already holds is free.
        // Enqueue only on the CLEAN->DIRTY transition, never per write.
        set { if (__v_Available == value) return; __v_Available = value; MarkDirty(0); }
    }

    internal static readonly NetPropertyTable __netProps = new(
        new NetProperty(0, "Available", NetType.Bool, NetChannel.GameSnapshot,
                        NetRate.Normal, ReplicationCondition.Always,
                        static (Entity e, ref NetWriter w) => w.WriteBool(((Pickup)e).__v_Available),
                        static (Entity e, ref NetReader r) => ((Pickup)e).__v_Available = r.ReadBool()));
}
```

Five things the emitter produces per type:

1. **Dense slots assigned by ordinal over properties sorted by declared name** — a total order over a value key, so two compiles agree and reordering source does not renumber the wire. Slot instability would silently make two builds of the same commit disagree, which is why this is not "declaration order".
2. Backing field plus dirtying setter (the property is `partial` for the same reason a `[ConVar]` property is: the generator owns the write path).
3. `WriteDelta(ref NetWriter, uint mask)` / `ApplyDelta(ref NetReader, uint mask)` — a switch on slot, one branch per set bit, no reflection, no boxing.
4. A static `NetTypeDescriptor` plus a `[ModuleInitializer]` registering it into `NetTypeCatalog`, the shape `P5` uses for `EntityCatalog`. As `console.md` §2.2 documents, a generator only sees its own compilation, so it runs in every assembly referencing Core and emits a registrar at a fixed name, called explicitly from the composition root so ILC roots the assembly.
5. A **canonical schema text** per property — `"game_pickup|0|Available|bool|snapshot|interestlocal"` — hashed with XxHash128 across all types in `WireName` order into a `public const NetSchemaDigest`. Mismatched builds refuse at handshake and **name the diverging type**, the same idiom as `.scmap`'s `VertexLayoutId`.

**`SceneNode`'s own built-in properties do not go through the generator.** There is exactly one concrete node type by design (`R‑6`; `O2` explicitly refuses subclassing), so there is nothing to enumerate. Name, Parent, LocalPosition/Rotation/Scale, Brush, MeshRenderer, attributes and tags replicate through a hand-written fixed 16-entry table with a `ushort` dirty mask that can share a word with other node flags.

**Luau parity is structural, not aspirational.** `.sentdef`'s keyvalue record is a **fixed 32 bytes** and must not grow — the obvious implementation adds a `NetSlot` field and breaks the format for every existing `.sentdef`. Instead: take three bits of the existing `Flags` u32 (bit 3 `replicated`, bit 4 `unreliable`, bit 5 `clientWritable`) and **derive the net slot as the ordinal position among that type's replicated keyvalues** — a deterministic function of the existing record, costing zero format bytes and no version bump. `Entity.define`'s keyvalue descriptors gain the matching keys. `D15`'s parity pin extends unchanged: define the same entity twice, once in C# and once in Luau, and the two `.sentdef` records must be byte-identical apart from the one-byte Origin badge, **replication bits included**. `docs/formats-and-pipeline.md` §3.2 exists to prevent a second producer of this metadata; a `[NetProperty]` attribute with its own table would be exactly that.

`ConVarFlags` bit 8 becomes `Replicated`, with Source's semantics: the authority's value overrides the client's for the session and is restored on disconnect. This also answers `console.md` §7 q1's doubt about whether `sv_cheats` is oddly named "with no multiplayer" — yes, it is the right name.

### 3.5 Security

**Encryption is the engine's job.** LiteNetLib ships none. Session keys derive via `HKDF` from the 128-bit secret embedded in the join code; payloads are sealed with `AesGcm` or `ChaCha20Poly1305`, selected by **runtime `IsSupported` feature detection at boot** — both are in-box (no vendored crypto library, matching the standing invariant) but both are documented as platform-conditional and OpenSSL-backed on Unix, which bites exactly on a minimal Linux server container. If neither is supported the session refuses to bind to anything but a private-range address and says so in one log line.

Nonce is `(channel << 56) | packetCounter`, per session, monotone, **with a hard session kill on counter rollover**. Nonce reuse under GCM is catastrophic and silent.

DTLS is rejected: .NET has no in-box implementation, so it means a native dependency with the per-RID packaging tax.

**Authentication is the join code, and that is the whole story for v1.** `SPECTRA-7K4M2Q-9XBTZR4WHN6PVLC8` — base32, 40-bit session id plus 128-bit secret. The secret never crosses the wire; the client proves knowledge with an HMAC over the server's challenge nonce, and **the challenge is answered before any per-peer session state is allocated**, because the handshake is the DoS surface and LiteNetLib's `ConnectionRequest` hook fires after a peer slot exists. It gives Roblox's "share a link" ergonomics with no accounts, no PKI and no service. It is also not authentication in any stronger sense; §9 makes that a decision rather than a footnote.

**A client may assert exactly one thing: its own input frame.** Every other client→authority message is a *request* the authority may refuse. There is deliberately **no message shape by which a client states a fact about the world** — no `SetPosition(NetId, Vector3)` exists in the client→server vocabulary, so no code path can accidentally honour one. Rate limiting is a per-peer, per-channel token bucket in both messages and bytes; a violation disconnects with `RateLimited` and logs **once per peer per window**, because per-packet logging is itself an amplification vector.

**The listen-server host is the authority and can cheat freely.** No technical mitigation is proposed, because none exists when the host owns the process. The session badge reads "Hosted by ⟨player⟩" and the stats line exposes `NetAuthorityKind { Host, Dedicated }`. In a singleplayer session the host is the only player, so the concern is empty there — which is exactly the right amount of security to spend on solo play, and a reason the model costs nothing extra to adopt. Casual and co-op play is fine on a listen server; anything competitive runs `--server` on a machine the players do not control. Making the distinction visible costs one label and buys the credibility the rest of the model needs.

**The collaborative-editing trust model is "trusted with the world, not with the process,"** expressed as three concrete refusals:

1. An edit peer may send `IEditorCommand`s and session-control messages, **and nothing else**.
2. Every asset path in an incoming command is re-normalized through `ContentRoot.NormalizeRelativePath` and rejected if it escapes the content root. A `.spectramat` reference is a relative path and a peer controls its bytes.
3. **A replicated Luau script edit transmits source text and is never auto-executed by the receiving host.** `O6` makes the Command Bar execute Luau against the live edit-mode scene and `O8` gives scripts a node payload, so a design where a replicated script node starts running is one commit away and would look like a feature. It is remote code execution with a friendly name.

Roles are `SessionRole { Owner, Edit, View }`, enforced **on the authority only** — a greyed-out button is a hint, not a permission.

---

## 4. Gameplay replication

### 4.1 Authority, topology, and the always-a-server model

```csharp
[Flags] public enum NetRole { None = 0, Server = 1 << 0, Client = 1 << 1 }

public static class SpectraNet
{
    public static NetRole Role { get; internal set; }
    public static bool IsServer => (Role & NetRole.Server) != 0;
    public static bool IsClient => (Role & NetRole.Client) != 0;
}
```

A listen host is `Server | Client`; a dedicated host is `Server`; a joined player is `Client`; **and a singleplayer game is `Server | Client` — the identical value a listen host carries.** `NetRole.None` is the pre-boot value only; assert after composition that `Role != None`, because a process that is neither is a process with no authority and no view. **`IsServer` and `IsClient` as read by a script report the calling context's side, not the process's topology** — implemented as a render-thread-scoped ambient `NetContext` set and cleared by the script/entity pump around each dispatch, keyed on `ScriptKind.Server` vs `ScriptKind.Client`. On a listen host both are true across a frame, which is exactly Roblox's `Enum.RunContext` model and exactly what makes ruling (1) true rather than aspirational. A process-level boolean would make every listen-server script answer wrong — the worst failure shape, because it is silent and only appears when someone first hosts.

#### Three topologies, one code path

Per ruling (5), the engine has no singleplayer mode to write, test or regress. It has a server, and a question about who is attached to it.

| topology | authority | local client | listener socket | remote peers | `SpectraNet.Role` | renderer |
|---|---|---|---|---|---|---|
| **Singleplayer** | this process | one, **shares the `Scene`** | **not bound** | none | `Server \| Client` | real |
| **Listen host / P2P** | this process | one, **shares the `Scene`** | bound | 1..N | `Server \| Client` | real |
| **Dedicated** | this process | none | bound | 1..N | `Server` | `HeadlessRenderer` (`N1`) |
| *(a joined player's process)* | elsewhere | one, owns a **replicated** `Scene` | none | — | `Client` | real |

The first three rows are one binary with different composition; the fourth is the same binary joining someone else's session. Two consequences worth stating in the same breath. **A dedicated server stops being a separate product** — it is the "no local client" cell of a matrix, not a build configuration. And **`N1` headless is exercised constantly rather than occasionally**: it is that cell, so a bug in it shows up in every dedicated run rather than only in a deployment nobody does until launch week.

#### What "shares the `Scene`" means against this tree

Read out of source on 2026-08-21. The local client is not a peer, has no `NetPeer`, no baseline store, no interest bitset and no transport. It renders the authority's own scene graph.

**Shared — exactly one instance, owned by the server, read by the local client:**

- **`Scene`** (`Scene.cs:40`, `sealed`) and everything hanging off it: `Root` (`:60`) and the node graph, the `_nodesById` Guid index behind `TryFindById` (`:186`), the internal `SceneBvh` (`:76`), the brush-placement snapshot, the single `_inFlightCompile`, and the compiled `StaticWorld` (`:335`) with its `StaticWorldChunkMeshes` (`:355`). **One compile serves both roles**: the server's `CsgWorld.ContainsPoint`/`Raycast` movement validation and the client's chunk draws come out of the same swap, which is the concrete saving the model buys and the reason a loopback transport that really serialized would be the wrong default.
- **`AssetManager`** — process-wide, owns every GPU resource by standing invariant.
- **`SceneManager.ActiveScene`** (`SceneManager.cs:116`) is the single field that names the shared object today, and stays so.

**Per-role — one per client, never authoritative, never replicated:**

- **The view camera.** `Scene.Camera` (`Scene.cs:62`) is `{ get; }` and scene-owned, so under this model the shared scene literally carries the local client's camera. That is tolerable only because there is at most one local client per process — and it is safe because `BuildRenderView(Camera camera, RenderView view)` (`Scene.cs:269`) takes the camera **as a parameter**, so the client→scene binding is already by argument rather than baked in. §5.1's rule that `Scene.Camera` stays a `Camera` and never a `SceneNode` is what keeps camera state structurally unable to replicate; keep it.
- **`SelectionSet`** (`Scene.Selection`, `:68`) — same shape, same reasoning; §5.1 already pins *selection is presence, never an edit*.
- **`RenderView`** (`Engine` owns `_renderView`), the `Renderer`, `InputManager`, `ISceneEditor`, and the per-client replication state (`NetClientView`, interest bitset, acked baselines, NetId table) — **of which the local client allocates none.**

The mechanism is not new machinery; it is a seam this tree already ships. `ISceneEditor` mutates the same `Scene` the renderer draws, in the same frame, on the same thread, with no copy and no serialization (`Engine.cs:283–296`), and the engine reaches it only through a seam the host installs (`SceneManager.EditorFactory`). The local client is that same arrangement with the roles swapped: the authority writes, the view reads, one object, one thread. Nothing about `Scene` has to change to make singleplayer a server.

**Accepted costs, stated plainly:**

1. **Every game pays the fixed tick even solo** (`N2`). A strictly single-player game gets a fixed-rate simulation it did not ask for, plus the 0..`MaxTicksPerFrame` catch-up semantics and the input-edge audit `N2` already carries. This is the price of the boundary existing on day one, and it is also what makes prediction, replay and deterministic tests possible at all.
2. **Every game pays a little authority indirection even solo** — a write goes through the owning side rather than straight into the node. The indirection is *not* a serialization cost; it is a call shape and an ambient `NetContext` check.
3. **Script authors think in server/client from the start.** For the Roblox-developer audience this arc targets, that is the familiar model rather than a tax — it is what `ScriptKind.Server`/`ScriptKind.Client` and `ServerScripts`/`ClientScripts` already mean to them.
4. **A stray direct write from client-side code is now a bug in singleplayer too** — which is the entire point, and the reason the next subsection exists.

**Replication scope is defined by WHERE a node lives, not by a flag.** Well-known top-level nodes under `Scene.Root`: `Workspace` (spatial, replicated, interest-filtered — and, per the correction below, **the only container that contributes to the static world and the BVH**), `ReplicatedStorage` (non-spatial, replicated to all, client writes stay local and never travel back — Roblox's rule, verified and kept), `ServerStorage` / `ServerScripts` (never leave the server, not sent, and — once the enforcement in the next subsection lands — not enumerable from a client **script** in any topology, local client included), `ClientScripts` (replicated once at join, then run on the client). `ReplicatedFirst` is named and reserved.

Effective scope is a cached byte per node, resolved from the nearest well-known ancestor and recomputed on reparent through the existing `Scene.OnNodeSubtreeMoved` hook — the same idiom that already maintains `_subtreeBrushCount`. There is deliberately no setter: you change replication by moving the node, which makes "what does a client know" visible in the Explorer tree rather than a per-node audit.

**This overturns a row in `docs/roblox-to-spectra.md` line 34**, which maps `ReplicatedStorage`/`ServerStorage` to "a single `Storage` node (deliberate difference)" on the grounds that "Spectra is single-process". That premise dies the moment multiplayer is real. **The mapping document must be corrected in the same change that lands `N11`**, not later.

#### Correction: the container model is NOT structural for the local client

This is the sharpest edge in ruling (5) and it was checked against source rather than accepted. The container list above claims `ServerStorage`/`ServerScripts` are "never sent, **not enumerable from a client**" — and as originally written, without the qualifier now carried there, that claim was false for the local client. For a *remote* client that is structurally true and needs no enforcement: the data never crosses the wire, so there is nothing to enumerate. For the **local** client — which shares the `Scene` — the node is physically present in the same graph, and every one of the following reaches it today with no scope concept anywhere in the path:

1. **The tree itself.** `Scene.Root` is public (`Scene.cs:60`), `SceneNode.Children` is a public `IReadOnlyList<SceneNode>` (`SceneNode.cs:75`), `SceneNode.Traverse()` is public (`:313`) and `Scene.Nodes => Root.Traverse()` is public (`Scene.cs:1392`). A client-side walk from the root reaches `ServerStorage` in three hops.
2. **The id index.** `Scene.TryFindById` (`:186`) is a flat `Dictionary<Guid, SceneNode>` over **every** node in the graph, with no scope awareness. It is also the resolver every `IEditorCommand` and every inbound wire op goes through, so it is a leak channel in both directions: a client script that guesses or is handed a Guid resolves a server-only node, and an inbound op naming a server-only node resolves it too.
3. **The spatial index.** `SceneBvh.IsSpatial(node) => node.MeshRenderer is not null || node.Brush is not null` (`SceneBvh.cs:146`) — ancestry-blind. So `Scene.Raycast` and `Scene.QueryFrustum` hit server-only nodes, and `BuildRenderView` (`Scene.cs:269–285`) *draws* any `MeshRenderer` under `ServerStorage` on the local client's screen.
4. **The compiled world — and this one leaks to remote clients too.** `SnapshotFullWalk` iterates `Nodes`, i.e. the whole tree, and admits every node carrying a `Brush` (`Scene.cs:1345–1351`). A brush parented under `ServerStorage` therefore *carves the static world*, and that compiled world is what every client renders and what `.scmap` ships. "Server-only geometry" is not server-only by any route.

Ruling (5) offers two mitigations for the "local client sees what a remote one never would" trap, neither of them a duplicate implementation: **(a) the container/scope model, sold as *structural* — a tree location rather than a transport rule**, and **(b) a strict-local-client development cvar**. **So the honest statement is that (a) is structural for a remote client and, as specified, is a replication-time filter for a local one — which leaves (b) carrying the entire load in solo development, which is exactly the configuration a developer spends 95% of their time in.** That is the "works solo, breaks in multiplayer" trap wearing the costume of its own fix. The claim is repairable and cheap to repair, but it must be repaired inside `N11` rather than assumed.

**The fix: the scope check belongs in the accessor that issues node access to client-context code, not only in the replication filter.** Concretely, four changes, all inside `N11`'s existing scope byte:

- **One gate, at the handle boundary.** `O5` brings nodes into Luau as tagged userdata holding a `(slot, generation)` handle into a host-side table. That table is the single place a node becomes reachable from a script. **Refuse to issue or resolve a handle for a node whose effective scope is server-only when the ambient `NetContext` is client** — one check in one function, and `O2`'s entire query surface (`FindFirstChild`, `GetChildren`, `GetDescendants`, `Parent`, `GetFullName`, `IsDescendantOf`) inherits it without being patched individually. The behaviour a Roblox developer expects falls straight out: `FindFirstChild` returns nil, `GetChildren` omits it, `script.Parent` walks up to a wall — the same answers a remote client gives because for a remote client the node genuinely is not there.
- **A scope-aware resolver beside the scope-blind one.** `Scene.TryFindById` stays as it is — the editor legitimately addresses every node in the graph, and that is the whole basis of `IEditorCommand` id addressing. Client-context script lookups and inbound wire ops go through a wrapper that additionally checks the scope byte. Pin it with a test that resolves a `ServerStorage` node's Guid from both, and asserts the client-context resolver fails.
- **Server-only containers are non-spatial, and the engine enforces it rather than asserting it.** The cheapest correct rule, and the one that matches Roblox (where only `Workspace` instances exist physically): **only `Workspace` contributes to the static world and to the BVH.** That means `SnapshotFullWalk`'s admission test gains a scope condition, and `SceneBvh.IsSpatial` gains one. This is a change in **Core**, on the compile's hot path, and it is the only place the container model touches CSG — cost it into `N11`, and treat the alternative (letting a server-only brush carve) as rejected on a hard ground: a server-only brush would make the authority's `CsgWorld` differ from every client's, and `IServerAuthority.Validate` (§4.4) tests movement against exactly that world. A brush under a server-only container is a loud refusal or an excluded-with-a-warning, never a silent carve.
- **Then, and only then, the diagnostic.** `net_strictlocalclient` (§6.3) routes the local client through the real serialize-and-interest-filter path, so a developer can see precisely what a remote peer sees. Off by default so solo play stays free. `N14`'s self-test runs strict **always**, because its three contexts are already separate scenes over a really-serializing `LoopbackTransport` and it is the leg where the filter is under test.

**One implementation consequence of strict mode, because it is not free and should not be sold as a flag flip.** Routing the local client through the filter means it must apply the received state into *something*, and it cannot be the authoritative scene it would otherwise share. Strict mode therefore instantiates a second `Scene` in the process — the same thing `N14`'s rig already does with three — and points the renderer at it while the server's scene runs on a `HeadlessRenderer`. That is two background compiles and two sets of chunk GPU meshes, which is precisely why it is a development diagnostic and not a default, and why the cheaper everyday form of the same check is `N20`'s `Server + 1 Client`, where the client is a separate process on a real socket. **Standing constraint that keeps both honest: at most one `Scene` per process may own a real `Renderer`.** §6.1(a)'s reason for separate PIE processes is unaffected — it is about two renderers and two Luau states, not about two scenes.

**Interest filters dynamic instances' existence and all instances' mutable state — never the existence of authored nodes.** An authored node is present on every client from map load, forever; only its property updates are filtered. The client already has it on disk with a matching NetId, and despawning one would break every `NodeRef` attribute, `targetname` lookup and Luau `workspace.Wall` reference pointing at it — which `formats-and-pipeline.md` §2.7 already says is why a node record is kept for every authored node. Roblox has no analogue because it replicates the whole DataModel; for an unbounded world this is strictly better. It is also not world streaming, which §4.5 of that document explicitly does not deliver.

### 4.2 The replication model

**Property/instance-level delta replication.** Not snapshot-delta, not deterministic lockstep.

Snapshot-delta (Quake3/Source) presumes a bounded, densely-indexed, value-shaped entity array with a compile-time-fixed field table. Spectra is a *tree* of Guid-identified nodes with parent/child structure, names, an attribute bag and payload objects; reparenting and renaming have no natural snapshot-delta encoding, and the entity count is unbounded by the open-world pillar. Lockstep would require bit-determinism across a native Luau VM, three graphics backends and eventual physics — and §1.2 shows the CSG layer alone does not have it across ISAs. Property delta matches the data model 1:1, matches the mental model a migrating Roblox developer arrives with, and the three change signals it needs already exist and are **already equality-filtered at the setter** (`Scene.NodeTransformChanged`'s own doc says a no-op write raises nothing).

**Snapshot-delta's actual insight is stolen, applied per field: acked baselines on an unreliable channel.** The server keeps a per-client, per-field baseline and resends until acked. Loss costs bandwidth, never correctness. This is also the arc's memory model, and it is only affordable *because* of interest management — baselines are allocated only for a client's relevant set. If property delta ships before interest management, the baseline store is O(clients × all instances) and a 50k-part world with 8 players is a real problem. **Sequence `N13` immediately after `N12`, and make baseline release on relevancy exit a tested assertion, not a code-review claim.**

### 4.3 Interest management

A **separate replication grid**: `Dictionary<InterestCell, List<NetId>>`, maintained from `Scene.NodeTransformChanged` with a cached cell per instance and boundary hysteresis.

Two things it must **not** do:

- **It must not use `CsgWorld.Chunks`.** That is derived data rebuilt by the background compile; coupling gameplay residency to a compile artifact would make relevancy flicker on every recompile.
- **It must not inherit `ChunkCoord.CellSize`.** That constant is pinned to `ChunkGrid.WeldBand` as a geometry invariant (§1.3) and may never move for relevancy's sake. Worse, a 3D cell radius over 32-unit cells scales as the cube of the radius: 3 cells is 7×7×7 = 343 dictionary probes per client per interest tick, and a plausible 512-unit sight radius is 33×33×33 ≈ 36,000 probes per client per tick — a cvar that turns a walk into a scan.

**So: the interest grid gets its own cell-size constant (128–256 world units, one value, tunable), indexes on XZ with a coarse Y band rather than a full 3D lattice, expresses its radius in world units, and enforces a hard cell-count cap** so a mistyped cvar cannot produce a 36,000-probe query. It reuses `ChunkCoord`'s *idea* — a dictionary-keyed sparse lattice where negative and distant cells cost the same as the origin — and nothing else.

Why a grid rather than `SceneBvh.QueryFrustum` per client: **temporal coherence.** A client's relevant set changes only when its anchor crosses a cell boundary, so a walking player recomputes a few times per second instead of every tick. A frustum query has no such property, is O(clients × log n) every tick, and would leak camera orientation into relevancy — spinning the camera would spawn and despawn replicas. (`SceneBvh` also has no sphere query today.)

Hysteresis is mandatory: a node resting on a boundary would otherwise thrash the buckets, the same hazard `P8` documents for triggers with the same fix.

Per client: an interest anchor (normally `player.Character`, script-overridable), a dense `NetIdBitset` of relevant ids so relevancy is a bit test rather than a hash probe, and the baseline store. A per-client byte budget derived from `INetTransport.MaxPayloadBytes` drains a max-heap scored by base priority × staleness × distance falloff, **rebuilt only from the tick's dirty list — O(dirty), not O(relevant)**.

### 4.4 Ownership and the prediction seam

**Ownership is explicit, not auto-assigned by proximity in v1.** Roblox auto-assigns *physics* ownership by proximity because it has a distributed physics solver to hand work to. Spectra has no physics engine, so auto-assignment would be a policy invented for a system that does not exist. Keeping the API *name* while deferring the policy costs nothing and means the eventual physics arc slots in without renaming anything a developer wrote.

```csharp
public partial class SceneNode
{
    /// <exception cref="InvalidOperationException">
    /// The node carries a Brush. A brush IS the compiled static world and is
    /// permanently server-authoritative — the analogue of Roblox's "the server
    /// always owns anchored BaseParts and you cannot manually change their
    /// ownership", and true for a stronger reason: client-authored world
    /// geometry would diverge every peer's CsgWorld.
    /// </exception>
    public void SetNetworkOwner(NetPlayer? player);
    public NetPlayer? GetNetworkOwner();
    public void SetNetworkOwnershipAuto();
    public bool CanSetNetworkOwnership(out string reason);
}
```

The error text is the documentation, so write it once and write it well:

> `'PillarA' carries a Brush, so it is part of the compiled static world and is always server-owned (like an Anchored part in Roblox). For a client-simulated object, use a dynamic part (a MeshRenderer node outside CSG).`

Ownership means three things that are all meaningful before physics exists: input authority, a server-side validation hook, and `OwnerOnly` field filtering.

**Ship the prediction seam and driver now; defer the simulation behind it.**

```csharp
public readonly record struct NetInputCommand(uint Sequence, Vector2 Move, Vector2 Look,
                                             InputButtons Buttons, float DeltaTime);

public interface IPredictedMover
{
    /// <summary>MUST be a pure function of (captured state, cmd, dt). The reconciler
    /// replays this N times in one frame from a restored state — which is why the
    /// fixed-tick loop is a hard prerequisite, not a later optimisation.</summary>
    void ApplyInput(in NetInputCommand cmd, float dt);
    void CaptureState(ref NetMoverState state);
    void RestoreState(in NetMoverState state);
}

public interface IServerAuthority
{
    /// <summary>Default: a speed/teleport clamp plus a CsgWorld.ContainsPoint
    /// solidity test WITH A SKIN WIDTH. Never a bit-exact containment test —
    /// see §1.2: the client's locally compiled world is not bit-identical to
    /// the server's across ISAs.</summary>
    bool Validate(NetId id, in NetMoverState proposed, float dt, out NetMoverState corrected);
}
```

The seam is expensive to retrofit; the simulation behind it is replaceable. Input sequencing, the client input ring, the `lastProcessedSequence` ack field on owned-instance updates and the compare-restore-replay driver all have to exist before any predicted subsystem does, and all three constrain the tick model. One concrete `IPredictedMover` ships: a kinematic character controller moving against `CsgWorld.Raycast`/`ContainsPoint`. Two people walking around a level together is a genuine deliverable and exercises every seam under load.

Rigid-body physics, physics rollback, proximity auto-ownership and lag compensation are **deferred and documented as deferred**. But the per-instance state history ring is **reserved and built**, because entity interpolation needs the same ring and adding historical storage to every instance after gameplay exists changes every instance's memory layout.

`ApplyInput` purity is pinned by a test that replays the same input ring twice and asserts bit-identical end state. Any read of global state, `Random`, or the scene outside the mover's own node makes replay diverge from the original prediction, and the symptom is intermittent correction jitter that looks like a network problem.

### 4.5 The static world at runtime

**A joining client receives nothing about the static world.** It loads the `.scmap` from its own pack. The join handshake carries `(mapAssetPath, SourceMapDigest, GeometryFormatVersion, VertexLayoutId, CompiledMapFormatVersion, NetSchemaDigest, sentdefDigest)` — every one of which already exists in headers the cook writes — so the entire world-sync protocol is roughly 40 bytes and zero new format work. A mismatch is a **named, refused join stating both values**, the same asymmetric-versioning stance the cooked formats already take. The failure it prevents is the worst kind: two players in geometrically different worlds, where collision disagrees and nothing reports it.

The dedicated server loads `CBSP` and skips `CMSH` and all texture uploads, gated by `sv_loadrenderdata 0`. The section-table design makes skipping free and geometry is the largest part of the file. **BSP stays resident**: `formats-and-pipeline.md` §4.5 already names the mirror hazard — "a collision query into a non-resident region silently answers empty, which is a player falling through the floor of a room they can see."

**Most "moving world" is not a world mutation at all.** `P7`'s brush entities compile to entity-local `BrushModel`s attached as plain `MeshRenderer`s, so a door, lift, platform or rotating fan replicates as an ordinary transform update with zero geometry traffic and zero recompile on either side. Stating this first dissolves roughly 95% of the question at zero cost.

**Genuine structural edits at runtime** (destruction, in-game building, scripted resize) replicate as brush edits on the reliable-ordered channel, scoped by `ChunkGrid.ComputeFootprint` to clients whose interest set intersects, and every client recompiles the affected cells locally via the incremental path in ~0.05–0.1 ms. Carve order determines geometry, so these must be strictly ordered — unreliable world edits are not an option.

**One trap, and it is severe over a network.** `SceneNode`'s `Brush` setter takes `if (had != has) Owner?.MarkStaticWorldDirty()`, which sets `_snapshotForceFull = true` and forces the next compile onto the O(world) full-walk path, because the placement *count* changed and every later slot shifts. `O7` already documents this for `Instance.new("Part")`. Over the network the cost is paid by **every client simultaneously**, so a destruction system built the obvious way ("delete the wall") stutters every player in the game at once and destroys the world-size-independence pillar for the whole session. **Model destruction as brush replacement** — "replace the wall with a smaller wall", count-stable, `SetBrushCommand`-shaped — and emit a lint-style warning on `AddBrush`/`RemoveBrush` at gameplay rates.

**Divergence must be loud, and it must hash the right thing.** A periodic `WorldDigest` over the **replicated inputs** — the ordered placement list plus plane and face records for the affected cells — is compared client-side; a mismatch logs an error naming the cells and requests an authored-state resync. **Never hash the compiled vertex and index arrays** (§1.2). Late joiners replay an ordered `WorldEditLog`, compacted by snapshotting **authored brush state**, never compiled geometry, when it exceeds `sv_worldeditlog_max`.

### 4.6 Where the network tick sits in the frame

Verified current frame body: `Engine.cs:275` input, `:284` editor, `:290` `SceneManager.Update`, `:296` `ProcessStaticWorldCompilation`, `:301` `PumpPendingUploads`, `:352` `BuildRenderView`, `:359` `Render`, `:367` `Present`.

```
:275   _inputManager.Update(dt);                    // (absent headless)
:276a  SpectraConsole.Drain(in frame);              // C0's pinned drain
:276b  SpectraNet.ReceivePump(scene);               // <<< RECEIVE
:284   editor?.Update(dt);                          // (absent headless)
       ─── FIXED TICK LOOP, 0..MaxTicksPerFrame iterations at sv_tickrate ───
         [O8 PreSimulation]
:290     _sceneManager.Tick(fixedDt, _renderView);
         [P4 EntityWorld.Tick(fixedDt)]
         [O8 PostSimulation]
       ─── end tick loop ───
:296   scene.ProcessStaticWorldCompilation(renderer, logger);   // ONCE PER FRAME — see below
:296b  SpectraNet.SendPump(scene);                  // <<< SEND
:301   _assetManager.PumpPendingUploads();          // ONCE PER FRAME (no-op headless)
       [O8 PreRender(alpha)]                        // (absent headless)
:352   viewScene.BuildRenderView(...);              // (skipped headless)
:359   _renderer.Render(...);   :367 Present(...);  // (skipped headless)
```

Four constraints fix `ReceivePump`'s slot, exactly parallel to the console's:

1. Received state must be visible to scripts this tick.
2. A received brush edit must dirty the world **before** `:296`, or the background compile launches a frame late — the identical argument `P4` uses for `EntityWorld.Tick`.
3. Received transforms must reach `BuildRenderView` at `:352` the same frame, or every remote player is a frame stale.
4. Everything a received message touches — scene mutation, asset requests, GPU resource creation — is render-thread-only by standing invariant.

Socket I/O runs on its own threads and crosses to the render thread through a `ConcurrentQueue` of pooled buffers. This is the **fifth** instance of a harvest idiom this codebase already uses four times (`ShaderHotReloader`, `AssetManager.PumpPendingUploads`, `Scene.ProcessStaticWorldCompilation`, `SpectraConsole.Drain`), not a new pattern. Pooled-buffer lifetime across the boundary is an access violation rather than an exception if it is wrong — refcount or copy at the boundary, the same rule `formats-and-pipeline.md` §8 already pins for mapped pack spans.

**`ProcessStaticWorldCompilation` stays once per frame, OUTSIDE the tick loop.** One reviewed design placed it inside. Verified in `Scene.cs:839–905`, that method is the render-thread *harvest*: it early-returns on `if (!inFlight.IsCompleted) return;`, then calls `ReplaceStaticWorld(renderer, result.World)`, which creates and destroys GPU meshes per (chunk, material) and is documented as able to throw mid-swap. Exactly one compile is ever in flight (`_inFlightCompile`, `Scene.cs:450`, launched by `Task.Run` at `:998`). Running it up to `MaxTicksPerFrame` times per frame means up to five calls of which at most one can do work, up to five GPU mesh swap batches on a single catch-up frame, and a `Renderer` — a GPU object — being touched from inside a simulation phase the headless server is specifically supposed to run without one. Same for `PumpPendingUploads`, and same for any `O8 PostSimulation` phase that expects to observe a landed world.

---

## 5. Collaborative editing

### 5.1 The command stream as the protocol

One `CollabAuthority` object; editor-hosted and dedicated differ only in process topology, and **the host's own edits go through it by loopback**, so there is exactly one sequencing path and no "am I the host" branch anywhere in edit code.

```csharp
public enum EditDomain : byte
{
    Transform = 0,   // SetTransformCommand, SetLocalTransformCommand
    Shape     = 1,   // SetBrushCommand: planes, face surfaces, materials
    Identity  = 2,   // name, tags        (needs Scene.NodeRenamed — see §8)
    Payload   = 3,   // entity keyvalues, script node metadata (P4/O8)
    Hierarchy = 4,   // AddNodes / RemoveNodes / ReparentNodes  (E6 — do not exist yet)
}

public readonly record struct EditOpHeader(
    ulong      Seq,            // assigned by the AUTHORITY only; clients submit 0
    PeerId     Author,
    uint       ClientOpId,     // echoed on ack/reject so the client finds its own op
    EditOpCode Code,
    EditDomain Domain,
    EditIntent Intent,         // Apply | Undo | Redo
    Guid       PrimaryNodeId,
    EditPrecondition Precondition);

/// <summary>"The live value for (PrimaryNodeId, Domain) must still be this."
/// Free to compute: for an undo it is literally the command's own After state,
/// which absolute-value commands already store. Deltas could not express it.</summary>
public readonly record struct EditPrecondition(ulong ExpectedStateDigest)
{
    public bool IsUnconditional => ExpectedStateDigest == 0;
}
```

**What must change to make the existing commands wire-ready:**

- **`IEditorCommand` has no identity.** It is `{Name, Do, Undo, RollBack}` — no op code, no author, no sequence, no way to name a command's type without a type test. It needs the envelope above plus a generated codec.
- **`SetBrushCommand` sends far too much, and one field of it is actively wrong.** Wire form is **`After` only** (the receiver already has `Before`), as `planes[] + rigid transform + plane-indexed faces[]`, with the material as a session-table index. `LocalFaces` is derived; `MaterialRef.Id` is process-local; `Before` is waste (§3.3). Real arithmetic: a 6-plane box with world-aligned faces is ~184 bytes, ~424 with explicit Hammer texture axes on every face; a transform-only op is ~50 bytes. The orchestrator's "a few dozen bytes" is right for transforms and low by ~5× for brushes — small enough that nothing about the plan changes, and worth stating correctly anyway.
- **Coalescing mutability is a live hazard, and it is what forces ruling (2).** `SetTransformCommand.SetAfter` (`:114–118`) and `TryAbsorb` (`:145–154`) mutate the **recorded instance in place** while its transaction is open. Serializing at `Record` time would ship a value the sender then mutates underneath the receiver. **Rule: only committed transactions are serialized.** The 60 per-frame drag values ride the presence channel and never touch the op stream. Ruling (2) is not imposed on this code; it falls out of it.
- **The hierarchy commands must be designed wire-first, inside `E6`.** `E6` already pins why the sibling index is load-bearing (traversal order → `BrushPlacement` order → carve order → geometry). Across machines that becomes a *convergence* requirement: two concurrent inserts at index 3 cannot both be index 3, so **the authority rewrites the sibling index at sequencing time and echoes the final value**, which means `AddNodesCommand` must expose an authority-settable index rather than a readonly one. That is an API change to a milestone not yet built, and it is far cheaper now.
- **Three near-misses worth pinning as standing invariants.** (i) `GizmoTool.CaptureTargets` reads `Scene.Selection` — selection is *input to* command construction, never part of a command: **selection is presence, never an edit.** (ii) `RollBack`'s `WeakReference<SceneNode>` is a local affordance for a cancelled gesture, and nothing is ever sent for a cancelled gesture. (iii) `Scene.Camera` is a `Camera`, not a `SceneNode` (`Scene.cs:62`), so camera motion structurally cannot replicate as an edit — **and it must stay that way**, because `O2`'s Roblox-familiarity API is the milestone most likely to expose a camera node.

**One unification is explicitly rejected:** running collab edits over the gameplay reliable channel during a Team-Test session. That interleaves a gameplay tick into an edit sequence whose *order determines carve order*.

### 5.2 Conflict resolution

**Authority-sequenced last-writer-wins per `(node, domain)`, with optimistic local apply and optimistic soft leases.**

The authority assigns a monotonic `Seq`; that sequence is the **only** clock — no wall clocks, no vector clocks. Clients apply locally and optimistically, submit, and on rejection rewind their unacknowledged ops, apply the authoritative op, and replay the survivors. Absolute values make replay exact and free: replaying a value already present dirties zero cells.

**Domains rather than whole-node locking** answers "simultaneous edits to different properties of one node" without per-field metadata. One non-obvious and load-bearing consequence: **the scale gizmo is a `Shape` edit, not a `Transform` edit**, because `ScaleGizmo` never writes node scale on a brush node — it rebuilds via `Brush.WithScaledExtents` and swaps through `SetBrushCommand`. So resize and retexture *do* conflict; resize and move do *not*.

**Soft leases, acquired optimistically.** `GizmoTool.BeginDrag` requests a lease on each captured target's `(nodeId, domain)` immediately before `Undo.BeginTransaction`, and **the drag starts that frame** — never block a grab on an RTT; a 60 ms stall on grab is exactly what makes collaborative editors feel broken. If the authority denies, the local gesture cancels through the path that already exists and is already correct: `GizmoTool.CancelDrag()` → `UndoStack.CancelTransaction()` → `RollBack` on every recorded command, whose contract is that the scene ends up exactly as the gesture found it, reaching even nodes that left the scene mid-gesture. **This is the single best reuse in the design: the Escape-key path built for abandoned gestures is the conflict-rollback path, with no new machinery.** Leases heartbeat (5 s) and hard-expire (15 s), so a crashed peer never wedges a brush.

One implementation constraint: **a denial arriving mid-drag must be dispatched from the inbound pump's deferred position, never re-entrantly from inside a scene event handler.** `Scene`'s contract forbids mutating the graph from an event handler, and a denial handled from the wrong place corrupts a traversal instead of cancelling a gesture.

**Rejected, with reasons specific to this engine:**

- **CRDT** — rejected on a concrete failure, not a preference. A brush's plane set is one semantic unit; merging two concurrent resizes yields a plane set neither user authored, and **`Brush`'s constructor validates** (it rejects duplicate and unbounded plane sets, which is why `P2` must catch `ArgumentException` from deep inside `Brush`). A CRDT merge can therefore produce a `Brush` that throws on construction. Separately, per-property CRDT metadata lands on `SceneNode`, the hottest type in the engine — `O3` already fights over 48 bytes/node for six signal fields at 50k parts; a CRDT is an order of magnitude worse.
- **Operational transform** — needs a transform function per op-pair to buy convergence that absolute idempotent values give for free. OT earns its keep on *text*, which is why script bodies get a different mechanism (§5.5).
- **Pure LWW with no leases** — correct but bad UX: two people dragging one brush produces a visible 20 Hz tug-of-war that reads as a bug.
- **Hard check-out locks** — kills the "just build together" feel. Leases expire; locks do not.

**What the user actually sees** — most designs hand-wave this, so it is specified:

- *Denied at grab* — the gizmo refuses the gesture exactly the way it already refuses an edge-on grab (returns `GizmoUpdateResult.Hovering`, nothing applied, no transaction opened). The node draws in the holder's presence colour and a status line says "Wall_03 — Dana is moving it." **No half-drag, no snap-back**, because nothing was ever written.
- *Superseded mid-gesture* — the node snaps to the authoritative value, and **a 1.5 s fading ghost wireframe of your value** draws in *your* colour via `DebugDraw.Box` over the brush's `WorldBounds`, with "your change to Wall_03 was superseded by Dana — Ctrl+Shift+Z to restore". Never silently discard someone's work without showing them what was lost.
- *Delete while you are editing* — the delete wins (it is sequenced), your gesture cancels, and your history entry is marked dead with a reason rather than removed.

**Convergence is audited by a canonical `.smap` document digest** over the authority's stable node ordering, exchanged on quiescence — **never a hash of the compiled world** (§1.2). A test asserts that a document digest is identical regardless of which SIMD path the local compile took, pinning that the check is compile-independent by construction.

### 5.3 Multi-user undo

The structure that makes this work is a one-line seam: **remote ops call `command.Do(scene)` directly and never touch the local `UndoStack`.** Local tools go through `Execute`/`Record` as they do today. `UndoStack` needs no change for this at all, and `UndoDepth` immediately means "my edits" — which is already what `ISceneEditor.UndoDepth` reports.

The hard case is undoing something the world has moved past. `SetTransformCommand.Undo` writes `BeforePosition`/`BeforeRotation` **absolutely**, so if Dana has since moved the node, a naive undo silently clobbers her. The fix uses a value the command already stores:

> **An undo is submitted as an ordinary forward op carrying a precondition: "the live value for `(node, domain)` must still equal my `After`."** If it does, the undo applies. If it does not, the authority refuses with `Superseded`.

The precondition costs nothing extra to compute — it is literally the op's own `After` state. Deltas could not express this at all.

**Two fixes the reviewed design needed:**

1. **For `SetBrushCommand` the precondition must compare by VALUE** (planes plus face records), never by reference. `Brush` reference identity is deliberately fresh on every edit because it is the carve cache's validity key, so a reference comparison would refuse every undo.
2. **`UndoName`/`RedoName` need the same dead-run skip as `TryUndo`.** Verified: `CanUndo` is `!IsTransactionOpen && _cursor > 0` and `UndoName` reads `EntryAt(_cursor - 1).Name` (`UndoStack.cs:99/121`), so a run of dead entries at the cursor makes the menu advertise a label for an entry that can never act — the UI would lie.

Behaviour by case:

- *Same domain touched since* → refused. The entry is marked **dead** and the UI says "Can't undo Move Wall_03 — Dana changed it since." Strictly better than either clobbering or silently no-op'ing.
- *Different domain touched since* → the undo applies and the other user's edit survives. This is where the domain split pays for itself.
- *Someone built on your structure* (you created a node, Dana parented work under it, you undo the create) → **refused, not cascaded.** The authority walks the subtree for nodes authored by others since and names them. Loud refusal beats silent destruction, consistent with this tree's culture. **Note: this rule has nothing to refuse yet** — there are no hierarchy commands. Fold it into `E6`'s design as a constraint on the commands being built, not into `T3` as a feature over commands that do not exist.

**`UndoStack` changes are small and contained**, which is the point of designing against it rather than replacing it: a `SupersededCommand` wrapper that no-ops both directions and carries a reason string — **keeping its ring slot**, so `RingIndex` arithmetic and the head-bump eviction never learn about removal — plus `TryUndo`/`TryRedo` returning an outcome and a reason, skipping a run of dead entries in one Ctrl+Z. `Capacity` (256), eviction and redo invalidation are unchanged. Measure the dead-entry rate before assuming 256 is still right under collaboration.

**Rejected: a single global shared undo stack.** Beyond making Ctrl+Z nondeterministic from a user's seat, there is a mechanism here that kills it outright: `UndoStack.PushEntry` drops the redoable tail on any new command, so **every remote op would destroy every local redo**. Users would lose redo constantly and never understand why.

The authority keeps **no global undo**. It keeps a bounded op journal for late-join catch-up and reconnect replay — a different feature, which separately enables an operator-level "revert to checkpoint". Conflating the two is how "undo" becomes "undo whatever anyone did".

### 5.4 Presence

Separate `Presence` channel, 15 Hz idle / 20 Hz while dragging, plus reliable-on-change selection updates driven from the existing `SelectionSet.SelectionChanged` (which already fires once per batched change — `Apply` has a `MatchesCurrentSelection` early-out, so it does not spam). Per-peer colour is derived deterministically from the peer id, so every peer paints every other peer the same colour with nobody assigning it.

**Live-drag feedback: broadcast the in-progress absolute transform and let observers apply it to the real node.** This does not violate ruling (2), because the *lease* guarantees exactly one author for that `(node, domain)`: a lossy stream of absolute values converges to the last one received, observers never record it in history and never re-broadcast it, and the committed op on the reliable channel is the authoritative closure.

The cost is quantified from this engine's own numbers, not assumed. A preview move dirties that brush's cells and rides the incremental path at ~0.05–0.1 ms per edit at 1k/10k/50k parts (the `CsgBench openworld` verdict), so 20 Hz of remote preview costs ~1–2 ms of background compile per second per dragger. But **exactly one compile runs at a time** (`_inFlightCompile`), so N simultaneous remote draggers serialize behind a single slot and each swap creates and destroys GPU meshes for the changed chunks. Therefore: **cap adaptively — above K concurrent remote draggers, drop the excess to ghost-only wireframes. K ≈ 4 is a starting point to measure, not a derived number**; if it is really 2, the feature degrades to ghosts far sooner than this reads.

Drawing reuses what exists. Everything goes through the depth-off `DebugDraw` line pass. Remote selections draw via the public `Scene.TryGetWorldBounds` in the peer's colour — this needs a *new* renderer in `SpectraEngine.Editing`, not a change to `DebugVisualizations.DrawSelectionHighlight(DebugDraw, Scene)`, because that one reads `scene.Selection` and there is exactly one `SelectionSet` per scene. A remote peer's manipulator reuses `GizmoGeometry.Build(localScene.Camera, remotePivot, remoteFrame, myViewportSize, pixelSize)` — built against *my* camera at *their* pivot, so it stays constant-screen-size in my viewport. That works only because `GizmoGeometry.Build` is a pure static function of camera + pivot + frame + viewport, which it is.

**Honest gap: no nameplates in v1.** The engine has no text rendering — `DebugDraw` emits lines only. In-viewport presence is colour-coded wireframe with no names; the named peer list lives in the Uno shell and is blocked on `H1`. Do not promise Studio-style floating name tags before then.

### 5.5 Session, persistence and script drafts

**Source of truth is the authority's live `Scene` plus its op journal — not a file.** The `.smap` is a checkpoint the authority writes using `P2`/`D11`'s canonical writer on a quiescence timer (2 s idle) or every 60 s of continuous editing, whichever first, plus on last-peer-disconnect and explicit save. Write-temp-then-atomic-rename, N rolling checkpoints, **each stamped with the `Seq` it includes** — that stamp is what makes reconnect replay possible. (Calibration: Roblox documents auto-saving collaborative projects every four minutes; 60 s is better and costs little because the map is text and the writer is deterministic by specification.)

**Join, in four phases, and the third is the one people forget:**

1. `Hello` → authority assigns `PeerId`, colour, and the session intern tables (materials first).
2. **Snapshot** — the authority serializes the live scene with the **`.smap` writer**, not a bespoke sync format, honouring the one-definition constraint. Compressed with in-box `DeflateStream` (the formats arc pins in-box-only compression).
3. **Catch-up** — ops sequenced *while the snapshot was being built and shipped* are buffered per joining peer and replayed in `Seq` order before the peer flips live. Bounded buffer; **on overflow, restart the snapshot loudly.** This is the most likely source of silent divergence in the whole design: a dropped op here produces a peer that is subtly wrong forever with no error anywhere.
4. The joiner calls `Scene.RebuildStaticWorld` **once, synchronously** — the existing cache-free load-time path, which is exactly what it is for — and every subsequent op rides the async incremental compile. Zero new code for the CSG side of joining.

**Disconnect/reconnect.** The peer keeps its last applied `Seq`. Inside the authority's bounded journal (≈10,000 ops or 5 minutes) → **replay the gap**, no rebuild, so a 20-second wifi blip is cheap. Outside it → full resync. **On disconnect the editor goes read-only and refuses gestures**; unacknowledged offline edits are discarded, loudly and immediately. Offline editing is a deliberate v1 scope cut, because merging a divergent brush history is the CRDT problem again, complete with the constructor-throws hazard. The read-only banner must be *immediate* — a user who keeps dragging for ten seconds against a frozen scene has already formed the wrong mental model.

**Script bodies are carved out, and the carve-out is Roblox's own answer.** Luau source is text; LWW on text loses work and OT on text is a whole subsystem. Roblox documents a Drafts mode: scripts are edited independently, saved to the local filesystem, persist across sessions, and on commit a merge UI offers Draft / Server / Other per hunk. Adopt that shape for `O8`'s `Script` payload: **script bodies never ride the op stream; script nodes (create/delete/rename/reparent) do.** Useful asymmetry: a disconnected editor keeps writing script drafts safely while brush editing is read-only.

### 5.6 The new-asset problem

Peer A drops `Textures/brick_new.png` in and textures a face; peer B gets a `MaterialRef` naming a path it cannot resolve. What saves this from being a crash already exists: a missing texture degrades to the magenta placeholder and a missing material to `AssetManager.DefaultMaterial`, each with a warning. "Everyone else sees magenta" is not collaboration, but it is a safe floor.

- **Identity on the wire is the normalized content-relative source path** — `ContentRoot.NormalizeRelativePath`'s exact output, the same key `.spack`, `AssetManager`'s caches, `MaterialParser`, `MaterialRegistry.Intern` and `.smap` all use. `formats-and-pipeline.md` calls that the pack arc's most important structural decision; collaboration is its third consumer and invents nothing.
- **The authority owns a session content store.** Unknown path referenced → request the bytes from the authoring peer; peer cannot resolve a path → request from the authority. Transfers ride the **`Bulk` channel**, rate-capped and yielding to `EditOps`, so a 4 MB texture never head-of-line-blocks an op.
- **Dedup and integrity key: `XxHash128` of the file bytes** — the same primitive `.spack` uses for `AssetId`/`ContentDigest`. Verbatim from the formats arc's honesty rule: this is **corruption detection and a dedup key, not tamper resistance.** Accepting files from peers adds a trust consequence the pack arc did not have: only Edit-permission peers may upload, uploads land in a per-session cache directory rather than overwriting the project tree, and **`.luau` arriving over the wire is content, not code to execute on arrival.**
- **Never block the op stream on bytes.** Apply the op immediately with the placeholder, transfer in the background, and when the bytes land call the existing public `Scene.RefreshStaticWorldMaterials()` on the render thread. That method exists for exactly this and makes late-arriving assets self-healing rather than requiring a barrier.
- **Cooked packs are never transferred.** Collaboration is a source-format activity over `D2`'s loose-file `IContentSource`. A peer running against a cooked pack is doing "Play cooked", not editing. Pin it, or someone will try to sync `.spack` deltas.
- **Multi-file assets** (`.obj`+`.mtl`+textures, `.gltf`) must transfer their whole closure, which requires the importer to report its referenced-file set. Whether `ModelImporter` does is an open question (§9).

---

## 6. The developer experience

### 6.1 What the developer types or clicks

**Four zero-config paths — and a zeroth that is not a path at all.**

**(0) Singleplayer.** There is no verb, no flag and no mode. `spectra.exe` runs a server with one local client and nothing bound (ruling (5), §4.1). A developer shipping a strictly single-player game never types anything from this section, never sees a join code, and never links a socket into their session — and their game is nonetheless already structured the way a multiplayer one is. The only affordance they need to know about is `net_strictlocalclient` (§6.3) when they want to find out what a remote peer would have seen.

**(a) Play-in-editor.** The Play button's dropdown offers `Play`, `Play Here`, `Run`, and `Server + N Clients` (N up to 7). Each simulated client is a **separate OS process** running the same binary: `spectra.exe --client 127.0.0.1:<port> --pie-token <guid> --map <handoff>`. Three independent reasons: the render thread is a single-threaded ownership spine (two `Scene`s, two renderers and two Luau states on one thread breaks the async CSG pipeline, the BVH, the selection set and the command queue simultaneously); separate processes exercise the real socket path, serializer and tick loop; and a crashing simulated client does not take the editor with it. Roblox does exactly this — Studio's `Server & Clients` mode spins separate Studio instances, up to eight clients. Map handoff writes the live authored scene through `P2`'s `.smap` writer into a temp path, because the editor's scene is authoritative and may be unsaved — and the handoff is **write-only from the editor's perspective**, so a serializer bug corrupts a simulated client's view rather than eating the authored scene.

**(b) LAN / direct connect.** `spectra.exe`, then `` ` `` → `host`. The other machine: `spectra.exe --connect 192.168.1.20`, or `find_servers` and pick. UDP broadcast beacon advertising `{name, map, players/max, engine version, map digest}`. Zero infrastructure by construction.

**(c) Dedicated server.** `spectra.exe --server --map Maps/arena.scmap --port 7777 +sv_tickrate 30 +exec server.cfg`. Same binary, one flag.

**(d) Internet P2P.** `` ` `` → `host --public` prints `Join code: K7M-2QX`. Friend: `` ` `` → `connect K7M-2QX`. **This one is the only path that needs a service behind it — and for this project the service is operated (§7). For anyone else shipping on this engine it is a self-hostable binary and a decision they own.**

**Collaborative editing:** a `Collaborate` button in the editor, or `--collab-host` / `--collab-join <code>` from the CLI.

### 6.2 The code, on both sides

```lua
-- ClientScripts/Shop.client.luau        (ScriptKind.Client)
local buy     = ReplicatedStorage.Remotes.BuyItem      -- RemoteEvent
local balance = ReplicatedStorage.Remotes.GetBalance   -- RemoteFunction

buy:FireServer("sword", 1)                             -- reliable, ordered
print(balance:InvokeServer())                          -- yields the coroutine

-- ServerScripts/Shop.server.luau        (ScriptKind.Server)
ReplicatedStorage.Remotes.BuyItem.OnServerEvent:Connect(function(player, itemId, count)
    print(player.Name .. " bought " .. count .. "x " .. itemId)   -- player is ALWAYS arg 1
end)

ReplicatedStorage.Remotes.GetBalance.OnServerInvoke = function(player)
    return Wallets[player.UserId] or 0
end

-- High-frequency, loss-tolerant: an UnreliableRemoteEvent payload instead.
ReplicatedStorage.Remotes.AimDirection:FireServer(camera.CFrame.LookVector)
```

Character-for-character Roblox, including `RunService:IsServer()` and `Players.LocalPlayer`. In C#, remotes are partial structs and the shared generator emits the codec and the `FireServer`/`OnServer` surface:

```csharp
[Remote(RemoteDirection.ClientToServer, Delivery.Reliable)]
public partial struct BuyItem { public string ItemId; public int Count; }

// client
new BuyItem { ItemId = "sword", Count = 1 }.FireServer();

// server
BuyItem.OnServer += static (player, msg) =>
{
    if (msg.Count is < 1 or > 99) { player.Kick("invalid BuyItem.Count"); return; }
    Shop.Grant(player, msg.ItemId, msg.Count);
};
```

**The one divergence from Roblox, and it errors loudly rather than truncating silently.** AOT forbids reflective serialization, so fully dynamic variadic remotes are unreachable. Arguments are values of `O3`'s closed `AttributeValue` union (bool, int, float, string, Vector2/3/4, Color3, CFrame, NodeRef, NumberRange) plus flat arrays of them. A nested table raises:

> `BuyItem:FireServer argument #2 is a table; remote arguments must be one of {bool,int,float,string,vec2,vec3,vec4,color,cframe,noderef,range} or a flat array of one. Pack it into a NodeRef or send fields separately.`

Naming the argument index is the difference between "restrictive" and "broken". Silently sending `nil` — Roblox's behaviour in some cases — is a notorious debugging trap worth diverging from. Document it in `docs/roblox-to-spectra.md`. An unbounded recursive decoder over untrusted client input is also a denial-of-service surface, so the closed set is a security property as well as an AOT one.

`RemoteEvent`/`UnreliableRemoteEvent`/`RemoteFunction` are node payloads whose channel ids are assigned from the deterministic node pre-order, so both sides agree with no negotiation. `RemoteFunction` re-entrancy is depth-capped and named in the error, the same way `P4`'s I/O cascade budget is.

### 6.3 Testing and debugging

**Console surface** — all `[ConVar]` partial properties per `C0`, all reachable from the same console the editor and game already have:

| cvar | default | purpose |
|---|---|---|
| `sv_tickrate` | 30 | fixed simulation rate |
| `sv_interestradius` | *(units — see §9)* | replication radius in world units |
| `sv_client_budget_bytes` | derived | per-client per-tick byte budget |
| `sv_validate_movement` | *(§9)* | authoritative movement validation |
| `net_fakelag` / `net_fakeloss` / `net_fakejitter` / `net_fakereorder` | 0 | link simulation, `Cheat`-flagged |
| `net_graph` | 0 | 0 off, 1 rates, 2 per-channel bytes, 3 + replication graph |
| `net_showreplication` | false | AABBs coloured by owner, interest cells as boxes |
| `net_strictlocalclient` | 0 | routes the local client through the real serialize-and-interest-filter path into its own `Scene`, so solo development sees exactly what a remote peer sees (§4.1). Costs a second background compile and a second set of chunk meshes; `N14` runs it always |
| `net_connectivity` | — | reports which path a connection took (direct / punched / relayed) |

Commands: `host [map] [--public]`, `connect <addr|code>`, `disconnect`, `status`, `find_servers`, `net_record <file>` / `net_playback <file>`, `sv_kick <player> <reason>`.

**The single highest-value debugging affordance: the link simulator wraps the loopback transport too.** Developers will do 95% of their multiplayer testing in play-in-editor — over localhost sockets for `Server + N Clients` (separate processes, §6.1(a)) and over `LoopbackTransport` for the in-process rigs. Neither is the *solo* path, which moves no bytes at all (§3.1, §4.1). If the simulator only wraps sockets, the in-process rigs test a 0 ms / 0% link — the one configuration that never exists in the wild — and every latency bug ships. Wrapping loopback makes "Server + 2 Clients with `net_fakelag 120`" a one-line reproduction of the actual player experience. The simulator must be **explicitly seeded and log its seed**, or the test suite is flaky, which is worse than no suite.

**Do NOT simulate latency on a listen host's own local client.** It genuinely has zero latency in production too, so simulating it would be lying. But it does mean the developer's own view is the one view that never shows the problem — Roblox Studio has the same property and it is a known source of "works for me". Make "toggle to a simulated client" the documented way to see the real experience. Under ruling (5) this applies to a **singleplayer** developer verbatim — their local client is the zero-latency, full-visibility one by construction — which is why `net_strictlocalclient` and `Server + 1 Client` are named in the same breath as the simulator rather than filed under multiplayer.

**`NetworkSelfTest`** — `EditingSelfTest`'s discipline applied to the wire. Opt-in (`--netselftest` / `SPECTRA_NETSELFTEST`), **off by default** for the same reason the editing one is: it really moves a real brush node and leaves it displaced for the frames the async recompile needs. One PASS/FAIL line per cycle carrying measured numbers; failures at `Error` naming the stage. **One process, three contexts** — a headless server and two headless clients over a `LoopbackTransport` that *really serializes*, delays by `net_fakelag` and drops by `net_fakeloss`. A direct method call would prove nothing about the wire, which is the entire point. No sockets, no ports, no child processes, so it runs in CI unchanged.

**It runs with `net_strictlocalclient` forced on**, always: its three contexts are separate scenes over a really-serializing loopback already, and the filter is precisely what is under test. Eight stages:

1. **Join convergence** — node counts equal; every replicated node's transform bit-for-bit equal to the server's.
2. **Edit convergence** — server moves a known brush node by exactly 1 unit; after the acknowledged tick both clients moved by exactly that delta. **Exact equality, not a tolerance** — absolute-value commands make that the right assertion, `EditingSelfTest`'s own argument reused.
3. **Input convergence** — both clients' *replicated placement lists* for the dirtied cells are element-identical to the server's. (Note: **inputs**, not compiled meshes — §1.2.)
4. **Remote round trip** — a client fires `BuyItem`; the server handler sees the right `NetPlayer` and payload; a `RemoteFunction` invoke returns within a bounded tick count.
5. **Server authority** — a client writes a transform on a node it does not own; assert the server did not adopt it and the client was corrected back.
6. **Loss tolerance** — repeat (2) with `net_fakeloss 20` and `net_fakereorder 10`; convergence must still hold inside a bounded tick count.
7. **Scope containment** — the server parks a marker node under `ServerStorage`; assert neither client's scene contains it, that a **client-context** resolve of its Guid fails on all three contexts (including the server's own, where the node *is* present — §4.1), and that a brush parented under a server-only container contributed nothing to any peer's compiled placement list. This is the stage that turns §4.1's structural claim from prose into a gate.
8. **Restore** — undo, assert both clients returned bit-for-bit, disconnect both, assert the server's scene is exactly as found so the cycle repeats forever without drifting.

```
Network self-test: PASS - 2 clients joined in 4 ticks, moved 'PillarB' by
(1.000, 0.000, 0.000), 3 cells' placement lists identical on all 3 peers,
BuyItem round trip 2 ticks, authority rejection honoured, converged under
20% loss in 7 ticks, ServerStorage marker invisible to both clients,
restored bit-for-bit.
```

**`CollabSelfTest`** is the twin: two in-process editor sessions over the same loopback rig; A drags node X while B drags node Y; assert both scenes converge and **each `UndoStack` holds exactly one entry — its own**. Then A grabs a node B holds a lease on: assert the grab is refused and A's stack is unchanged. Then B commits, A retries, succeeds.

**Warning:** `EditingSelfTest` uses `SceneManager.SelfTestNode` (`PillarB`, chosen because nothing else in the demo dirties its cells). Two self-tests on the same cadence fighting over the same node produce spurious failures. Give the network test its own dedicated node, or make the two mutually exclusive with a loud message.

**Deterministic replay.** `net_record` writes a framed `.snetlog` (magic + u16 version + `(tick, direction, peerId, bytes)` records); `net_playback` feeds it to a fresh client with recorded tick stamps replacing the clock, asserting identical scene state at checkpoints. This is cheap **only because the system is command-shaped and deterministic within a build**. Adopt `formats-and-pipeline.md` §8's determinism list verbatim for the replication path — no timestamps, no absolute paths, no `Guid.NewGuid`, no `string.GetHashCode`, no dictionary iteration order — or replay rots within two months.

---

## 7. The honest infrastructure section

Per ruling (4), stated plainly and up front rather than in a footnote.

**Settled 2026-08-21, and this section is written from the far side of the decision.** The user operates infrastructure and will run the Spectra rendezvous and relay. So for *this* project, NAT traversal is an engineering task, not an open risk: the hosted default of option **(c)** below is available and is what ships, with option **(b)**'s self-hostable binaries shipped alongside it — not as a hedge, but because anyone else shipping a game on this engine inherits the operational question, and handing them a binary is the difference between an engine and a service. Everything the engine owes is unchanged and still has to be built: the STUN client, the punch driver, the relay client and the `ISessionRendezvous` seam. **What changed is the sequencing** — `N6` moves onto the critical path (§8.3), because there is no longer a scenario where it is written, documented as "run this yourself", and then left unexercised.

§7.4 is kept in full, restated as guidance for third parties rather than as a question this project still owes an answer to. The cost arithmetic below is real and does not become less true because someone else is paying it.

### 7.1 What works with zero infrastructure

Everything except one path.

- **Singleplayer**, which under ruling (5) is a real server with a real authority boundary and no socket. Worth listing precisely because it is easy to forget that the *default* configuration of every game built on this engine is in this list.
- **Play-in-editor** with up to 7 simulated clients: nothing but the binary.
- **LAN**: UDP broadcast discovery plus direct connect. Two laptops on a desk, nothing typed but an IP.
- **Direct connect over the internet** where one side can accept inbound: a port forward, or a cloud VM with a public address.
- **Dedicated server**: a machine, which is the developer's problem in every engine.
- **Collaborative editing** in any of the above. On a LAN it needs nothing at all.

That is genuinely equal-to-Studio in *mechanism* for a team on one network or with one reachable host, and in two places better: the domain split means retexture and move do not fight, and Roblox does not document per-user undo semantics at all.

### 7.2 What needs a service, and why the free tier is smaller than it looks

**LiteNetLib's `NatPunchModule` is not NAT traversal.** Verified by reading `LiteNetLib/NatPunchModule.cs`: it implements a three-party *introducer* — `NatIntroduce()` on a mediator, `SendNatIntroduceRequest()` from a peer, `OnNatIntroductionResponse()` spraying packets at internal and external endpoints — and performs **no STUN binding requests, no ICE candidate gathering, no symmetric-NAT port prediction and no relay**. It handles full-cone and restricted-cone NAT, i.e. the pairs that mostly connect anyway, and contributes nothing to the residual failure mode. **It also does not supply the mediator; somebody must run that.** Three of four reviewed designs cited it as if it were a traversal answer; it is the punch handshake and nothing else.

So the first-party work is: a STUN client (RFC 8489 Binding Request, ~200 lines, no dependency), a rendezvous/introducer service, a TURN client, and the `ISessionRendezvous` seam that makes all of it pluggable.

**Measured baseline to plan against.** Tailscale reported internal direct-connection success "well north of 90%" (Oct 2025) — for a mature, heavily-engineered ICE implementation. The largest independent measurement of decentralized hole punching (arXiv 2510.27500) measured **70% ± 7.1% conditional** success, conditional on relay reservation and address discovery that themselves fail ~29% of the time. CGNAT behaves as symmetric NAT and is common in 2026. **Plan for 10–30% of consumer pairs needing a relay.** No amount of engine code fixes that residue.

### 7.3 What a relay costs

A relay forwards real bytes and is billed per GB.

- **Rendezvous** is nearly free: a tiny AOT-published binary that only introduces peers. A €4.50/month VPS runs it.
- **Relay**: a relayed 5-player session costs roughly 0.3–0.6 GB per session-hour depending on whether the provider bills ingress. A Hetzner CX22 is €4.50/month with **20 TB included in EU regions** — but **1 TB in US regions and 0.5 TB in Singapore**, which is the real cliff. So an EU box covers ~30k–60k session-hours a month and a US box ~1.5k–3k. A small VPS will hit packets-per-second limits before bandwidth limits.
- **Cloudflare Realtime TURN**: $0.05/GB after a 1,000 GB free tier shared with their SFU. `stun.cloudflare.com` is free and unlimited for the STUN half.
- **Self-hosted coturn**: free software, a machine you operate, an abuse-report address that never goes away.
- **Steam Datagram Relay / Epic Online Services P2P**: both provide hole punching *and* relay fallback at no charge to the developer — and both bind the game to a platform SDK and its account system. Valve's own README confirms SDR is a Steam partner service not offered on other platforms.

### 7.4 The decision — made for this project, still owed by anyone else

This is the honest form of ruling (4). The four options are unchanged; what is settled is which of them this project takes.

| Option | Cost to the operator | What the docs can claim |
|---|---|---|
| **(a) Ship nothing** | zero | "LAN, direct connect and dedicated servers." Materially weaker than Roblox; zero liability. |
| **(b) Ship self-hostable `spectra-rendezvous` + `spectra-relay`** | a €4.50/month VPS *per game operator*, documented one-command deploy | "Out-of-the-box P2P; run this one binary." Honest, and a genuine answer. |
| **(c) Operate a hosted default** | a permanent bill, an abuse surface, a privacy policy | Actually matches Roblox's ease. The only option that does. |
| **(d) Steam/EOS adapters** | zero, but platform-bound | "Zero-config P2P on Steam." Forecloses non-Steam distribution for P2P. |

**This project ships (b) + (c) + (d): self-hostable binaries, a hosted default we operate, and platform adapters.** (c) is the row that was previously out of reach and is now simply a fact about who is running the box; it is what makes the Roblox-parity claim in §0's first goal true rather than nearly true. (b) is not thereby redundant — it is the deliverable for every other developer shipping on this engine, and it is also the thing that keeps the hosted default honest, because a hosted service nobody can replace is a lock-in the rest of this design would not survive.

The consequences that follow are all sequencing and operations, not architecture:

- **`N6` is on the critical path** (§8.3), not "off to the side".
- **`ISessionRendezvous` stays a real seam with a real null implementation.** A hosted default that is not swappable is worse than no default. The seam's three implementations — hosted, self-hosted, none/LAN — must all be exercised, and `net_connectivity` must report which path a connection took in all of them.
- **The hosted default carries the obligations option (c) always carried**: a bill that scales with relayed GB (§7.3's arithmetic is the planning input — an EU box covers ~30k–60k session-hours a month, a US one ~1.5k–3k), an abuse-report surface, a privacy statement about what the rendezvous logs, and a documented answer for what happens when it is down. **The engine must be fully functional with the hosted service unreachable** — LAN, direct connect and dedicated are unaffected by construction, and `host --public` must fail with one clear line naming the service, never hang.
- **The 10–30% relay residue does not go away**, it changes payer. §7.2's measurement stands: plan for it, do not engineer against it.

The first-screen sentence in the multiplayer documentation changes accordingly, and it should still be on the first screen:

> `host --public` prints a six-character join code. Producing that code requires a rendezvous service, and roughly 10–30% of peer pairs additionally need a relay. **We run both, and using them is the default.** We also ship both as self-hostable binaries, plus adapters for Steam Datagram Relay and Epic Online Services, so a shipped game never has to depend on infrastructure it does not control.

Shipping a "P2P" button that fails for a third of users, and explaining why afterwards, is a credibility loss the rest of the engine's honesty does not survive — which is exactly as true with a hosted default as it was without one, because the failure mode being fixed is the relay-less residue, not the missing rendezvous.

---

## 8. Prerequisites and milestones

### 8.1 Prerequisites

**Hard blockers:**

0. **A verified NativeAOT publish of the transport (`N0`) — promoted from a spike to a gate.** Settled 2026-08-21: shipped games need multiplayer, so `SpectraEngine.Net` **does** enter shipped AOT game binaries, and under ruling (5) the server half is in every binary whether or not a socket is ever bound. It can therefore no longer be sized like `SpectraEngine.Editing`, which is kept out of a game build by a reference direction. If the chosen transport cannot `dotnet publish -p:PublishAot=true`, the arc's dependency choice changes and every milestone after it changes with it — so this is answered **first**, with a warning inventory, before `N1` is scoped. §3.1 expects LiteNetLib to pass on a reachability argument; expecting is not verifying.
1. **Headless mode (`N1`).** `Engine.Run` unconditionally builds a window, an input context and a framebuffer latch, and pumps `DoEvents` with four main-thread latches; `RenderLoop` hard-wires `LoadDemoScene`. Nothing about a dedicated server is verifiable until this exists. **Not blocked on `H1`** (§1.4) — take `IRenderSurface` when it lands, never build a parallel seam. Budget for the latch protocol's no-op owner and a tick-rate cap, which nobody costed.
2. **A fixed-step simulation loop (`N2`).** The engine has none: `Engine.cs` integrates one variable `deltaTime` clamped at `MaxDeltaTime = 0.1`. Prediction requires re-running the simulation N times in a frame from a restored state, which a variable-dt loop cannot do, and retrofitting after gameplay exists is a rewrite. It must land **before `O8` hardens its pump points** and before `P4`'s `EntityWorld.Tick` placement is treated as final. The concrete audit hazard: anything reading a per-frame latched input edge (`WasKeyPressed`) will double-consume or miss edges when the tick loop runs 0 or 2 times — audit every consumer *during* this milestone, not after. **Ruling (5) widens this from a multiplayer prerequisite to an engine-wide one:** if there is no singleplayer code path, the fixed tick is what *every* shipped game runs on, so `N2` is no longer a cost paid only by games that ship multiplayer, and its accepted-cost line in §4.1 is the honest form of that.
3. **`SpectraEngine.Generators`.** Whichever of `P5`/`O5`/`C0` lands first creates it and pays the netstandard2.0-inside-net10.0, Roslyn-pinning and `PrivateAssets` tax once. Replication is a fourth **emitter**, never a fourth project.
4. **`E6` + `O2` + `Scene.NodeRenamed`.** These are not soft prerequisites for collaborative editing — they *are* the majority of it (§1.1). `E6`'s sibling index must become authority-settable while `E6` is still unbuilt. `NodeRenamed` must **not** bump `_graphStructureVersion` (ruling `R‑7`), or every rename forces an O(world) full-walk compile on every peer. `O2`'s settable `Parent` needs a cycle guard before it is reachable from a packet: `AddChild` has no cycle check today and `MarkWorldDirty` recurses the whole child list, so a remote reparent is one malicious packet from unbounded recursion.

**Softer, but on the path:**

- `P2`/`D11` (`.smap` reader/writer, canonical writer spec) — the join snapshot, the autosave checkpoint and PIE's map handoff all use the `.smap` *writer*. Before `P2`, a dedicated server can serve only the demo scene.
- `D2` (`IContentSource` + `LooseFileSource`) — the session content store layers on it.
- `O3` (closed `AttributeValue` union) — remote arguments, entity keyvalue storage and the wire type vocabulary all bottom out here.
- `O5` + `O8` (Luau bindings, `Script` payload, `ScriptKind.Client` activated) — without them there is no `LocalScript` and no client/server split consumer.
- `D9` + `D12` + `D10` (data-driven boot, `.scmap`, `FlatBspTree`) — the join handshake's version gate reads `.scmap`'s existing header fields, and the server queries collision through the flat tree.
- `C0` + `C1` + `C4` + `C6` — the dedicated server's entire operator interface *is* the console arc. `C1`'s stdin front end is the server terminal; a remote console routes through `C6`'s single `ConVar.SetFromText` gate, never a second mutation path.
- `P4` + `P5` (entity runtime and schema generator) — replicated instance state is entity state.
- `P7` (brush entities) — what makes doors, lifts and platforms replicate as plain transform updates.
- `P11a` (play/stop) — PIE's Stop path.
- `ChunkGrid.ComputeFootprint` non-allocating overload (§1.3).
- *(`N0`'s AOT verification was here; it is hard blocker 0 above as of 2026-08-21.)*
- `O5`'s node-handle bridge, as the single place a scope check can gate script access to a node (§4.1). Without it, `N11`'s container model is a replication filter wearing a structural claim's clothes.

### 8.2 Milestones

Prefix allocation, checked against `F/E/P/S/R/H` (ROADMAP), `O0–O9` (roblox-onboarding), `D0–D22` (formats-and-pipeline) and `C0–C12` (console) — all free. **The range is split up front** so the two systems' milestones can be written and reordered without a renumber:

- **`N0`–`N6`** — shared substrate.
- **`N10`+** — gameplay replication.
- **`T0`+** — Team Edit (collaborative editing).

#### Shared substrate

| id | milestone | scope | depends on | risk | size |
|---|---|---|---|---|---|
| **N0** | Transport seam, loopback, link simulator, AOT spike | `INetTransport`, `NetChannel`, `NetPeer`, stats; `LoopbackTransport` that really serializes; `SimulatedLink` decorator wrapping loopback *and* sockets; `.snetlog` recorder. **Gated on an actual `dotnet publish -p:PublishAot=true` of a throwaway console** for LiteNetLib, Riptide and a raw-Sockets baseline, with a warning inventory as `O0`/`D0` do. | nothing hard | **HARD GATE (raised 2026-08-21).** Shipped games need multiplayer and ruling (5) puts the server in every binary, so the transport publishes into shipped AOT output. A failure here changes the dependency and every milestone after it — so it is answered before `N1` is scoped, not in parallel with it. Technically still LOW-risk to *run* | S–M |
| **N1** | Headless: `HeadlessRenderer`, latch no-op owner, `--server` — **the "no local client" leg of the §4.1 topology matrix** | `HeadlessRenderer` overriding the four virtual `IWindow` methods (promoted from `FakeRenderer`'s shape; `CreateShader` throws); `Engine.RunHeadless` as a **sibling** of `Run` that never reaches GLFW; no-op owner for the four main-thread latches; `NullInputSource`; tick-rate cap; `sv_loadrenderdata 0` skipping `CMSH`. | none hard; adopt `H1`'s `IRenderSurface` when it lands, do **not** wait | MEDIUM. Named hazards: `AssetManager.AttachRenderer` creates the magenta placeholder as a GPU resource, so `ReleaseGraphicsResources`/`PumpPendingUploads` must stay callable against an inert renderer; and splitting the frame body creates two copies that must stay in step — extract the shared sequence, do not copy-paste. **Value raised by ruling (5)**: this is not a server-only feature but one cell of a matrix every game exercises, and `net_strictlocalclient` needs a `HeadlessRenderer` for the server's scene inside a *windowed* process — so the headless renderer must be usable alongside a real one, not only instead of it | M |
| **N2** | Fixed-tick simulation loop | Accumulator at `sv_tickrate`, 0..`MaxTicksPerFrame` iterations dropping time rather than spiralling; `SceneManager.Update` → `Tick`; `O8`'s Pre/PostSimulation become tick phases, `PreRender` stays per-frame; **`ProcessStaticWorldCompilation` and `PumpPendingUploads` stay once per frame, outside the loop**; audit every `WasKeyPressed` consumer | N1; must precede `O8` hardening | MEDIUM-HIGH — the uncomfortable prerequisite, and under ruling (5) an unavoidable one: with no singleplayer code path, this loop is what every shipped game runs on. Missed/doubled input edges are intermittent and look like input bugs, not loop bugs | M |
| **N3** | Identity and the wire codec | `NetId`, `NetIdTable`; `NetWriter`/`NetReader` as `ref struct`s, explicit little-endian, RFC 4122 Guid; varint-delta NetId runs; first-reference-order string table; lift `NODE`/`BRSH`/`FaceRecord`/`KeyvalueType` into shared Core records with three codecs; structural enforcement that lossy floats are illegal on `EditOps` | N0; coordinate with `D11`/`D12` before either hardens | MEDIUM. Three hazards, two borrowed from `D12`: `MaterialRef.Id` on the wire; a quantized float leaking onto `EditOps`; `Vector3`/`Quaternion`/`Plane` field-order assumptions, which want `.scmap`'s `Unsafe.SizeOf` pin | M |
| **N4** | Session: handshake, join codes, crypto, roles, rate limiting | Exact-equality version gate naming the diverging field; `NetJoinCode` + HKDF + HMAC challenge answered **before** per-peer allocation; `NetCrypto.Detect()` with the nonce discipline and rollover kill; `SessionRole`; per-peer per-channel token buckets logging once per window; `host`/`connect`/`disconnect`/`status`/`sv_kick` | N0, N1, N3; `C0`/`C1` for the verbs | MEDIUM-HIGH — the only milestone where a bug is a *security* bug. Nonce reuse under GCM is catastrophic and silent. The version gate is the highest value-per-line item in the arc | M–L |
| **N5** | Replication source generator | The fourth emitter: attributes, name-sorted ordinal slots, `partial` dirtying setters with clean→dirty enqueue, `WriteDelta`/`ApplyDelta` switches, `NetTypeDescriptor` + `[ModuleInitializer]`, canonical schema text → `NetSchemaDigest`. Composition: share `P5`'s collector; `.sentdef` `Flags` bits 3/4/5 with the slot **derived** as ordinal (no new field, fixed 32 bytes preserved); `O5` emits `spectra.d.luau` annotations; `ConVarFlags` bit 8 | N3; `SpectraEngine.Generators` must exist | MEDIUM-HIGH, identically to `P5`'s documented risk. Replication-specific hazard: **slot stability** — a slot must be a function of the declaration set, not source order. Mitigate with Verify snapshot tests | L |
| **N6** | Rendezvous, STUN, hole punching, relay client, honesty pass | `ISessionRendezvous` seam with a null impl for direct/LAN; RFC 8489 STUN client; drive LiteNetLib's `NatPunchModule` with retry and per-attempt timeout; self-hostable `spectra-rendezvous` and `spectra-relay`; **the hosted default and its outage behaviour** (`host --public` fails in one named line, never hangs); SDR/EOS adapters; `net_connectivity` reporting direct/punched/relayed on all three seam implementations; **the §7.4 docs section** | N4 | MEDIUM technically. **On the critical path as of 2026-08-21** (§7): the infrastructure decision is settled and the operator is us, so this is no longer optional or deferrable. The risk did not vanish, it changed owner — this is still an **operational commitment, not a feature**, and the tail (abuse surface, relay bill, privacy statement, uptime) is now ours. The null implementation must stay first-class or the hosted default becomes lock-in | M + an operational tail we now own |

#### Gameplay replication

| id | milestone | scope | depends on | risk | size |
|---|---|---|---|---|---|
| **N10** | LAN discovery and direct connect | UDP broadcast beacon; `find_servers`; `--connect`; loud named refusal on version/digest mismatch; multi-homed/VPN interface enumeration with a `net_bindaddress` override | N4 + a real `UdpTransport` | LOW | S — **land it early**, right after `N4`. It is the whole zero-infrastructure story for the smallest cost in the arc |
| **N11** | Containers and the scene-to-wire seam | Well-known nodes (`Workspace`, `ReplicatedStorage`, `ServerStorage`, `ServerScripts`, `ClientScripts`; `ReplicatedFirst` reserved); cached `ReplicationScope` recomputed on reparent via `OnNodeSubtreeMoved`; authored-node zero-spawn NetId mapping asserted by a two-load test; create/destroy/reparent on `GameReliable` in pre-order; `.smap` serializes containers **by role, not by name**; **corrects `roblox-to-spectra.md` line 34 in the same change**. **Plus the four §4.1 enforcement changes, which are the milestone's real content, not a footnote**: the scope gate at `O5`'s handle boundary; a scope-aware id resolver beside the scope-blind `Scene.TryFindById`; `SnapshotFullWalk` and `SceneBvh.IsSpatial` admitting `Workspace` only, so a server-only brush cannot carve; and a loud refusal for a `Brush` parented under a server-only container | N3, N4, `F2`, `O2`, **`O5`** (the handle boundary is where the gate lives) | MEDIUM-**HIGH**, raised: it now edits `Scene`'s compile-snapshot walk and the BVH's admission test, both Core hot paths, in addition to adding a field to the hottest type in the engine — consider packing `NetId` + dirty mask + scope byte into one word. Ruling `R‑9` applies: do not land concurrently with `E4`/`E6`/`P7`/`O2`. Failure mode is **silent**: a stale scope leaks `ServerStorage` content, and under ruling (5) it leaks to the **local** client by direct tree walk rather than by a wire bug, so pin it with a test walking every container pair *and* with `N14` stage 7, run in the **published-binary** CI leg | M–L |
| **N12** | Property delta replication | Hand-written 16-entry `SceneNode` table with `ushort` mask; generated tables for entities; transform codec (cell-relative 13-bit position, smallest-three rotation, **no scale on brush nodes**); per-client per-field acked baselines with resend-until-acked; Luau parity | N5, N11, `P4`/`P5`, `O3` | HIGH — widest blast radius in the arc (generator + Core + entity runtime + Luau bridge) | L |
| **N13** | Interest management | Own cell-size constant (128–256 units), XZ index + coarse Y band, world-unit radius with a **hard cell-count cap**, 1-unit hysteresis; per-client `NetClientView` with a dense bitset; **baselines allocated only for the relevant set and freed on exit — a tested assertion**; spawn/despawn for dynamic instances only; priority budget rebuilt O(dirty) | N12 | MEDIUM-HIGH, and the risk is tuning. Three named hazards: coupling to `CsgWorld.Chunks`; despawning an authored node (breaks every `NodeRef`); unbounded baseline memory | M–L |
| **N14** | Network self-test | The eight-stage in-process rig over a really-serializing loopback with injected loss/latency/reorder, explicitly seeded, **with `net_strictlocalclient` forced on** | N12 (six stages), N15 (the remote round trip), N11 (stage 7, scope containment) | LOW to build, highest leverage in the arc. Loopback hides every real-network failure mode, so injection is not optional — and stage 7 is what keeps §4.1's structural claim from decaying back into a comment | S–M |
| **N15** | Remotes, `Players`, the client script split | Three node payloads; the `[Remote]` generator surface; Luau bindings with `player`-first server signatures; `Players` service and `Player` nodes with `player.Character` as a `NodeRef`; `ScriptKind.Client` activated; ambient `NetContext` so `IsServer` is context-scoped; closed argument set with a loud call-site error naming the index | N11, N12, `O3`, `O5`, `O8` | MEDIUM. Two listen-host Luau contexts on one thread doubles `O8`'s registry-entry concern and turns `O9`'s single `lua_close` into two — cost it, do not assume. **Server script source ships in the client's pack in v1** (`SCPT`/`LUAS` are one section): nothing in a server script is secret, and that must be documented before anyone puts a secret in one | M–L |
| **N16** | Ownership, input protocol, prediction, kinematic mover | `SetNetworkOwner`/`Auto`/`CanSet`; the permanent brush refusal with its error text; `IServerAuthority` with a **skin-width** validator; input sequencing, client ring, `lastProcessedSequence` ack, compare-restore-replay; one `IPredictedMover` | N2 (hard), N12, N13, `O7` | HIGH. Reconciliation looks fine on a LAN and rubber-bands on a real connection, so `N14`'s injection is mandatory here. Pin `ApplyInput` purity with a double-replay test. **Client-authoritative transforms are an exploit surface the moment they exist** — the validation envelope is mandatory at the descriptor level, pinned by a `(ownership × magnitude × scope)` matrix test as `C6` pins `(flag × ConVarSource)` | L |
| **N17** | Entity interpolation and the state history ring | Render non-owned instances at `now - net_interp` (100 ms) between bracketing states; one per-instance ring sized by `max(net_interp, net_lagcomp_history)`, built once and serving both; extrapolation clamp; `DebugDraw` visualisation | N16 | MEDIUM. Most *visible* milestone in the arc. Reserve the ring's shape now even though lag compensation is deferred | M |
| **N18** | Replicated entity keyvalues | `.sentdef` `Flags` bits 3/4/5 wired end to end through both producers; keyvalue changes on the right channel; `D15`'s parity pin extended to include replication bits; mandatory validation envelope on any `clientWritable` keyvalue | `P4`, `P5`, `D14`, `D15`, N12 | LOW-MEDIUM. Additive by construction, which is the point of routing it through the existing schema | S–M |
| **N19** | Runtime world mutation | `WorldEdit` opcodes on `GameReliable`, strictly ordered; footprint-scoped broadcast; `WorldEditLog` with **authored-state** compaction; `WorldDigest` over **replicated inputs**; documentation and a lint warning that `AddBrush`/`RemoveBrush` force a full-walk recompile on **every** client | N12, N13, `P7`, `D12` | MEDIUM-HIGH. Three silent hazards: divergent apply order; an unbounded log growing join cost; the count-changing trap. The `CsgBench openworld` verdict must still say "world-size independent" after this — **show it** | M–L |
| **N20** | Play-in-editor topology | `Play` / `Play Here` / `Run` / `Server + N Clients` (0..7); editor process is the server and in `Play` hosts local client 0 — i.e. `Play` is not a distinct mode but the §4.1 singleplayer topology with the editor attached, and `Server + 1 Client` is the cheap everyday form of `net_strictlocalclient`; child processes via `--client --pie-token`; map handoff through `P2`'s writer (write-only from the editor); window tiling; per-client Output tab over `Bulk`; `Stop` tears down every child and diff-restores through `P11a`; loopback-only bind plus a per-session token | N1, N4, N12, `P2`, `P11a`, `H1` | MEDIUM-HIGH, mostly orchestration: child-process lifetime (a Windows job object; a parent-death watchdog elsewhere — stranded clients holding sockets and GPU contexts make the best feature feel flaky), port allocation, and the handoff race | M–L |
| **N21** | Dedicated-server operations | `sv_*` cvars through `[ConVar]`; `C1`'s stdin front end as the server terminal; remote console routed through `C6`'s single `SetFromText` gate; per-peer stats in the periodic stats line; argv wiring | N4, `C0`, `C1`, `C4`, `C6`, `D9` | LOW-MEDIUM, but `C6` is a **security boundary**: a remote console bypassing the gate exposes `Cheat` cvars to players, and that is found by players. Extend the flag matrix with `ConVarSource.Remote` | S–M |
| **N22** | Network debug surface and deterministic replay | Full `net_*` set; `net_graph` 0..3 through `DebugDraw` (degrading to a logged stats line before `C10`'s text sink); `net_showreplication`; per-channel byte stats; `net_record`/`net_playback` with checkpoint assertions | N0, N12, `C0`, `C10` | LOW-MEDIUM. Replay is cheap only while nothing in the replication path acquires a wall clock or a non-deterministic iteration order — **add that to the standing invariants now** | M |

#### Team Edit (collaborative editing)

| id | milestone | scope | depends on | risk | size |
|---|---|---|---|---|---|
| **T0** | Edit-op vocabulary and generated codec | `EditOpHeader`/`EditOpCode`/`EditDomain`/`EditIntent`/`EditPrecondition`; `BrushPayload`/`FacePayload` as the after-only brush wire form; `SessionMaterialTable`; codec emitter in the shared generator with op fields constrained to the closed `KeyvalueType` subset; round-trip + reconstruct-bit-identical tests; a **`CompositeCommand` nesting depth cap** (untrusted input) | N0, N3; `E1` (landed) | LOW-MEDIUM. Sharp edge is the material table — test it by interning an unrelated material first on one side | S–M |
| **T1** | Session seam and in-memory loopback | `ISessionChannel`, `IEditApplier`, `IEditLeaseBroker`, `EditRejection`; `InMemorySessionChannel`; `PumpInbound` wired into the render-thread frame beside `PumpPendingUploads`; **extend `EditingAssemblyBoundaryTests` to ban `System.Net.*`** | T0 | LOW | S |
| **T2** | `CollabAuthority`: sequencing, leases, optimistic apply, rewind/replay | Monotonic `Seq` as the only clock; per-`(node, domain)` leases with heartbeat/expiry; client unacknowledged list with rewind-apply-replay; **decode-time validation** so a malformed op never reaches a render-thread apply (`Brush`'s constructor throws); gizmo wiring with denial through the existing `CancelDrag → CancelTransaction → RollBack` path | T1 | MEDIUM. Denial mid-drag must dispatch from the pump's deferred position, never re-entrantly from a scene event. Verify empirically that a rewind-replay of N absolute ops dirties zero cells — that is the whole cost argument | M |
| **T3** | Multi-user undo | Remote ops via `Do(scene)`, never the local stack; `IUndoArbiter`; `TryUndo`/`TryRedo` returning outcome + reason; `SupersededCommand` keeping its ring slot; dead-run skip **including `UndoName`/`RedoName`**; by-value precondition for `SetBrushCommand` | T2 | MEDIUM — least prior art of anything here (Roblox does not document Team Create undo semantics at all). **The refusal message quality IS the feature.** Measure the dead-entry rate against `Capacity = 256` | M |
| **T4** | Session lifecycle | Three-phase join (hello + intern tables → `.smap`-writer snapshot → buffered catch-up), one synchronous `RebuildStaticWorld` on the joiner; bounded journal; `TryReplayFrom` + full-resync fallback; quiescence/interval checkpoints with temp+rename and `Seq` stamping; read-only-on-disconnect | T2; `P2`/`D11` **hard** | MEDIUM-HIGH. The catch-up buffer is where silent divergence hides — bounded, with a **loud snapshot restart** on overflow. Measure snapshot size before deciding whether a binary join payload is needed, and **do not invent a third schema before measuring** | M |
| **T5** | Presence and `DebugDraw` rendering | `PeerPresence`; 15/20 Hz cadence; selection-on-change off `SelectionChanged`; deterministic colour from peer id; `PresenceRenderer` using public `Scene.TryGetWorldBounds` and `GizmoGeometry.Build(localCamera, remotePivot, …)`; superseded-edit ghost with 1.5 s fade | T2 | LOW-MEDIUM. **Highest felt-quality-per-line in the arc** — the `E2` slot. No nameplates until `C10`/`H1` | S–M |
| **T6** | Live-drag preview with the adaptive cap | `DragPreview` on `Presence` at 20 Hz while a lease is held; observers apply and never record or re-broadcast; degrade to ghost-only above K concurrent draggers; concurrent-dragger and compile-queue-depth in the stats line | T5, T2 | MEDIUM, measured not logical. One compile slot. **K ≈ 4 is a number to measure** | S–M |
| **T7** | Session content store | Keyed on `NormalizeRelativePath`; `XxHash128` dedup; chunked transfer on `Bulk`; placeholder-then-`RefreshStaticWorldMaterials` self-heal; multi-file closure; uploads restricted to Edit peers into a per-session cache directory | T2, `D2` | MEDIUM. Two hazards: whether `ModelImporter` reports its referenced-file set is **unverified**; and this is the design's trust boundary — `XxHash128` is corruption detection, not tamper resistance | M |
| **T8** | Headless collaboration host | No-window, no-renderer host: `.smap` load, authority loop, checkpoints, journal, content store, structured logging | T4, T7 | LOW — **and that is the finding.** Must not be sequenced behind `N1` | S |
| **T9** | Document-digest convergence check | Canonical `.smap` digest over the authority's node ordering, exchanged on quiescence, loud named alarm and resync offer; a test pinning that the digest is identical regardless of which SIMD path the local compile took | T4 | LOW technically, HIGH value. Enforces §1.2's one rule | S |
| **T10** | Permissions, identity, trust boundary | Owner/Edit/View enforced authority-side per op and per upload; session credential; kick and lease-force-release; audit line per denial | T2, T7 | MEDIUM. No account system exists, so this is where the design must say a session code is not authentication. **Settle before any session is exposed beyond a LAN** | M |
| **T11** | Script drafts and merge | Bodies leave the op stream: local drafts on disk persisting across sessions, explicit commit, per-hunk Draft/Server/Other merge UI; script *nodes* keep riding the op stream; drafts stay writable while the session is read-only | `O8`, T4; merge UI needs `H1` | MEDIUM-HIGH, mostly UI, mostly blocked on the Uno shell. **Sequence it last** — do not let the geometry story wait on a diff/merge surface the engine has no substrate for | M–L |

### 8.3 Where this slots into ROADMAP.md

Nothing here is on ROADMAP §4's critical path, and it should not be added to it: a person can build, texture, save and walk a level with `F1, F2, E1–E4, E6, E7, P2, P11a` and nothing else. Networking is a **new parallel track**, added to §4's parallel-track list as:

> **Networking** — **`N0` first and alone** (it is a hard gate on the dependency choice, §8.1), then `N1 → N2 → N3 → N4 → {N5, N10, N6}` then `N11 → N12 → {N13, N14, N15} → N16 → N17`, with `N19`/`N20`/`N21`/`N22` off to the side. `N6` is **on this chain, not beside it**, as of the 2026-08-21 infrastructure settlement (§7). Needs `H1`-adjacent work only as an opportunity, not a gate.
> **Team Edit** — `T0 → T1 → T2 → {T3, T4, T5} → …`. Needs `E1` (landed), `E6`, `O2`, `P2`. Depends on the networking track only through `N0`/`N3`/`N4`.

**Two sequencing consequences of ruling (5) that reach outside this arc**, and are the reason the track is no longer purely optional for a game that ships single-player:

- **`N2` is engine-wide.** With no singleplayer code path, the fixed tick is what every shipped game runs on, so `N2` must land before any gameplay-shaped milestone treats a variable-dt frame as its contract — the same argument it already makes against `O8` and `P4`, generalised.
- **`N11` is a correctness prerequisite for the local client, not only a wire feature** (§4.1), and it now edits `Scene`'s compile-snapshot walk and `SceneBvh`'s admission test. That is Core surgery on the CSG hot path, so it obeys ruling `R‑9` and must be scheduled against `E4`/`E6`/`P7`/`O2` deliberately.

Three edits to existing documents that must land with their milestones, not later:

1. **`ROADMAP.md` §12 (standing invariants)** gains five: *selection is presence, never an edit*; *`Scene.Camera` stays a `Camera`, never a `SceneNode`*; *no wall clock and no non-deterministic iteration order in the replication path* (adopting `formats-and-pipeline.md` §8's list verbatim); *there is no singleplayer code path — a solo game is a server with one local client sharing the `Scene` and no socket bound* (ruling (5)); and *at most one `Scene` per process may own a real `Renderer`* (§4.1, the constraint that keeps `net_strictlocalclient` and `N14`'s multi-scene rigs honest). Once `N11` lands, a sixth: *only `Workspace` contributes to the static world and to the BVH.*
2. **`ROADMAP.md` §6 arc E**: `E6`'s sibling index becomes authority-settable, and `E6` gains the `WouldOrphanOthersWork` constraint on its hierarchy commands.
3. **`docs/roblox-to-spectra.md` line 34**: the `Storage`-collapse row is overturned by `N11`.

---

## 9. Decisions that need the user

Each is stated with its tradeoff in one sentence. None can be settled from the code. **Numbering is preserved across settlements** — three items are closed and stay in place rather than being removed, so cross-references from other documents keep resolving.

1. **SETTLED 2026-08-21 — the user runs the infrastructure.** *(Was: does anyone run a Spectra rendezvous and relay, and who pays?)* The user operates infrastructure and will run the Spectra rendezvous and relay, so a **hosted default** ships (§7.4 option (c)) **alongside the self-hostable binaries** (option (b)) and the Steam/EOS adapters (option (d)). NAT traversal is therefore an engineering task for this project rather than an open risk — but the engine still owes the whole of it: STUN client, punch driver, relay client and a genuinely swappable `ISessionRendezvous` with a first-class null implementation. `N6` moves onto the critical path; §7 is rewritten from the far side of this decision. The residual obligations are operational, not architectural, and §7.4 lists them.
2. **SETTLED 2026-08-21 — both ship; P2P is viable rather than aspirational.** *(Was: is P2P the headline shipping mode or the convenience mode?)* Dedicated and P2P are both first-class, which is what ruling (1) always claimed and what ruling (5) now makes structurally true — dedicated is the "no local client" cell of the §4.1 matrix, not a separate product. Because the relay infrastructure exists (item 1), P2P is a mode we can actually stand behind rather than a documented aspiration, so `N6` is load-bearing and hosting is owned. Item 3 below is *not* settled by this and still bites: a headline P2P mode makes the target-concurrency answer more urgent, not less.
3. **What is the target concurrency — 4–8 friends, 16–32, or 100+?** It changes per-client baseline memory, whether the priority heap is per-client or shared, whether relevancy needs a coarse far tier, and whether `sv_tickrate` defaults to 30 or 60; nothing in this design is right for all three.
4. **SETTLED 2026-08-21 — yes, shipped games need multiplayer.** *(Was: do shipped games need multiplayer, or only the editor needs Team Edit?)* `SpectraEngine.Net` **does** enter shipped AOT game binaries, and it cannot be sized like `SpectraEngine.Editing`, which stays out of a game build by reference direction. Two consequences, both binding: **`N0`'s AOT spike is a hard gate** (§8.1 blocker 0, `N0`'s risk column) rather than an informational spike — a failure changes the dependency choice and every milestone after it; and under ruling (5) the transport's *presence* in the binary is unconditional even when the game never binds a socket, so the trim/AOT warning inventory is a shipped-size question as well as a correctness one.
5. **Is 1 Spectra unit = 1 Roblox stud?** Already open in three documents (ROADMAP §11 q13, roblox-onboarding §5 q10, formats §7 q7); multiplayer adds a fourth consumer, because `sv_interestradius`'s default is content-scale-dependent and cvar defaults persist into user config files — **answer all four together.**
6. **Is an account/identity system ever real?** Without one, a ban list is keyed on a per-install Guid resettable in ten seconds, "Owner" is whoever generated the code, and a session code is not authentication — fine for friends, inadequate for anything public, and it blocks `T10`.
7. **Is authoritative movement validation on by default?** Default-on is the correct security posture and the wrong default for a hobbyist with teleporters and launchers; `sv_validate_movement` defers the choice but somebody picks the default.
8. **Should the cook strip server-script source from the client pack?** A `--split-scripts` flag is straightforward but adds a second artifact, a second digest and a second version gate to the join handshake — **answer before anyone treats a server script as confidential.**
9. **Does a collaborative session survive the host leaving?** Roblox's does because it persists the place in the cloud; here the host owns the file, so "the host closed their laptop" ends the session unless host handoff (T-tier) or an always-on session server is built.
10. **Should Team Edit and gameplay ever be live in the same process — a Team Test the other collaborators can watch?** Roblox does this; it is genuinely appealing and it is the one configuration where the two systems ruling (2) separates would run concurrently over one transport, so it constrains the channel budget and tick scheduling even if deferred.
11. **Does the editor's Play mode run loose files or a cooked pack?** (`formats-and-pipeline.md` §7 q4.) This design assumes loose, with "Play cooked" as a later separate verb; `N20`'s map handoff cannot be designed without the answer.

---

## 10. Open questions (uncertainty, not decisions)

These are things nobody currently knows, as distinct from things the user must choose.

- **Which transport survives the AOT spike.** LiteNetLib is the presumptive default on licence, dependency and API grounds; its NativeAOT posture is unverified and the standing invariant forbids assuming it. **No longer merely "blocks `N4`": since §9 q4 settled that shipped games carry `SpectraEngine.Net`, this gates the whole arc's dependency choice** (§8.1 blocker 0).
- **Whether a scope gate at `O5`'s handle boundary is sufficient, given that C# game code shares the process with the authority.** The gate makes the container model structural for *scripts* in every topology (§4.1). It cannot make it structural for **C#** on the local client, which can call `Scene.Root`, `Traverse()` or `TryFindById` directly — that is not a hole a runtime check can close in-process, and the honest answer is three-layered: a remote client genuinely does not have the data, `net_strictlocalclient` shows a solo developer the difference, and the C# surface gets a lint/analyzer rule rather than a runtime refusal. **Whether an analyzer rule is worth building, and what it keys on, is unresolved** — say "scripts", not "clients", in any documentation written before it is.
- **What `net_strictlocalclient` actually costs on a real map.** It is a second `Scene`, a second background compile and a second set of chunk GPU meshes in one process (§4.1). If that is affordable on a 50k-part world it can be the default in the editor's Play mode; if it is not, `Server + 1 Client` is the only practical form and the cvar is a narrow diagnostic. Measure before promising either.
- **What K actually is** for concurrent live-drag previews (§5.4). If it is 2 rather than 4, the feature degrades to ghosts far sooner than this design reads.
- **Whether a 50k-part `.smap` join snapshot is acceptable as Deflated text.** Measure before deciding; if a binary join payload is genuinely needed, derive it from `.scmap`'s existing shapes rather than designing a third schema.
- **Whether `ModelImporter` exposes the closure of files a model references** (`.mtl`, textures, glTF external buffers). `T7` needs it; adding it is small but must be scheduled.
- **What the interest radius default should be, and whether a coarse far tier is needed.** A game with long sightlines wants low-rate position-only updates for distant players; adding it later is additive, but knowing changes whether `NetRate` is per-instance or per-(instance, client).
- **Whether Luau-defined entity types should be allowed to declare replicated state in v1.** Parity says yes and `.sentdef`'s layout makes it cheap, but a game-authored Luau table can then declare arbitrary per-instance replicated state with no compile-time budget check — a cook-time warning above a declared byte budget is the obvious guard; whether it is an error under `--strict` is policy.
- **How collaborative editing interacts with hot reload.** A peer editing a `.spectrashade` triggers their own watcher; others see nothing until the file transfers. Clearly shared for a texture; arguably local-only for a shader under active development.
- **Where saved/persistent player state lives.** `formats-and-pipeline.md` §7 q10 already flags that everything in the pipeline is read-only mounted content. A Roblox developer expects `DataStoreService`; "there is no service" is a real gap. Not owned by this arc, but blocked *on* by any persistent-progression game.
- **Whether `.snetlog` is subject to the same tamper question `formats-and-pipeline.md` §7 q9 asks about packs.** It is a debugging artifact today and an anti-cheat artifact the moment anyone thinks about it, and a header digest field cannot be widened later without a format break.
- **Whether the `SC####` diagnostic prefix becomes a three-way collision.** `console.md` §9 already flags it between the ConVar generator and the cooker; the replication emitter needs a range too. Resolve the two-way collision before adding a third claimant.
- **Unverified by construction.** Nothing here was built or run — including §4.1's shared-`Scene` model, whose claims about `Scene`, `SceneBvh`, `SceneManager` and `Engine` are **source reads on 2026-08-21, not measurements**; in particular "one compile serves both roles" is a structural consequence of there being one `Scene`, not a benchmarked saving, and the second-scene cost of `net_strictlocalclient` is an estimate from the compile's known shape — another workflow held the tree and `dotnet build`/`run`/`test` were forbidden. The performance arithmetic (184/424-byte brush ops, ~1–2 ms/s of compile per remote dragger, the identity-bandwidth figures) is arithmetic over the repo's published benchmark numbers and packet sizes, not a measurement of this design. The claim that the `.scmap` `NODE` pre-order index yields identical NetId assignment on two independent loads is a consequence of a format decision that has not been implemented yet.
