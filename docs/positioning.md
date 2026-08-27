# Positioning

This is the product thesis: who Spectra is for, what "easy" means here in operational terms, how general appeal is reached without pretending to start there, and which promises differentiate the engine. `ROADMAP.md` sequencing inherits this document; `docs/roblox-pitfalls.md` is its evidence base (what the primary audience is fleeing) and `docs/roblox-onboarding.md` is its mechanics (how they arrive). Nothing here changes an architecture decision; it changes what gets built first and what gets said out loud.

---

## 1. Who this engine is for

**The primary audience is one species with two homes: the world-builder.**

- The **graduating Roblox developer**: experienced, often years on the platform, wants ownership, real revenue share, Steam, and freedom from platform risk (the whole of `roblox-pitfalls.md`), but has bounced off Unity and Unreal. Skills: building playable places in an editor, Luau, live-ops instincts, multiplayer-native thinking.
- The **Hammer mapper**: fluent in brush-based level design and entity I/O, whose toolchain is frozen in time and whose engine was never theirs to ship games on. Skills: solid-geometry level design, scripted sequences through entity wiring, shooter-shaped gameplay.

Spectra is the intersection of their two homes by construction: Hammer's brushes and entity I/O on one side, Studio's parts, immediacy, and Luau on the other. No shipping engine occupies that intersection; the incumbents treat in-editor level design as a plugin or a deprecated legacy feature.

**Why Unity and Unreal lose these people.** Not intelligence, and not editor complexity as such. The failure is the distance from intent to result:

- They open to an empty project and a scaffolding decision tree; Studio and Hammer open into a world.
- Behavior there is code-first from minute one; in their homes, behavior starts as configuration (place an entity, set properties, wire an output to an input).
- Play there is a build pipeline; in their homes it is one key, in place, instantly.

Everything in section 2 exists to keep that distance short here.

## 2. What "easy" means, precisely

"Easy to make a game" is not a mood; it is three testable properties plus a ramp.

1. **The world is the project.** A new project opens into a playable place, not an empty hierarchy. Starter content ships as content with realms preset (per the standing Realms decision), never as engine concepts. There is nothing to scaffold before the first wall goes up.
2. **Behavior is configuration before it is code.** The entity layer (`P4` to `P6`, and `P6`, the no-code logic entities, is the load-bearing milestone for this audience) means a door, a trigger, a counter, a sound cue are placed and wired, not programmed. Luau is the second step, not the entry fee.
3. **Play is instant.** One key, in place, from the world being edited, with state restored on stop (`P11a`). This already half-exists (F8 play mode); `P11a` completes it.

**The ramp: easy start, no ceiling.** Roblox-easy came with sealed walls (`roblox-pitfalls.md` shape one). Spectra's version of easy must not: configuration ramps to Luau, Luau ramps to the C# seam, materials ramp to authored SpectraShade, and every internal abstraction stays replaceable or constructible from userland (pitfalls law 1). The pitch in one line: floors as low as Studio's, ceilings that do not exist.

**The first-hour test, a standing gate.** A scripted evaluation walkthrough, run and timed per release once the surface exists to walk it: open the editor, build a room from brushes, texture it, place a door and a trigger that opens it, import a character model, hear a sound, put a label on screen, press play, walk it; later rungs add "join a friend." Where the walkthrough stalls is where the next work is. This is the product-side sibling of `--selftest`: the demo proves the engine to itself; the first-hour test proves it to a stranger.

## 3. The generality contract

The goal of general appeal ("any game, from a small horror game to an FPS to an RTS to open world") is kept, but split into its two honest halves:

- **As an engineering constraint, generality is already law**: nothing genre-specific bakes into the engine core, and any game must be makeable without touching engine C# (the standing genre-targets pillar). This document does not weaken that; the RTS rung below exists precisely because the constraint must someday be proven, not asserted.
- **As a pitch, generality is earned, never claimed.** Every general engine got general through a wedge: Unreal was an FPS, Unity won indie mobile, Godot won free 2D, Source was a mod SDK. "Easy for everything" from a new engine is neither credible nor testable. Spectra's public identity is the wedge (section 1); the ladder (section 4) is how the claim widens one proven rung at a time.

## 4. The ladder

Each rung is a small, complete, shippable **template game** built entirely through the editor and Luau. A rung is done when the template plays, and the template then serves as tutorial, demo, and marketing at once. Rungs are ordered by which missing subsystems they force, smallest bill first.

**Rung 0, the loop (in progress).** Build, texture, save, play: the existing critical path `E6` (duplicate/delete/group), `E7` (face texturing tool), `P2` (`.spectramap` round trip), `P11a` (play/stop). No positioning change; this document simply confirms the order.

**Rung 1: a small first-person horror game.** The perfect first rung: brush worlds, dynamic light and shadow, the first-person controller, and scripted sequences through entity I/O are exactly Source's horror toolkit, the scope is solo-sized, there is no netcode dependency, and it is the genre where solo developers actually succeed commercially. What it forces, deliberately: **audio** (currently a stub, and horror is mostly audio), **a minimum game UI** (menus, prompts, a label), **minimum skeletal animation** (import and play a clip; a monster must move), plus entities and triggers (`P4` to `P8`).

**Rung 2: a multiplayer FPS.** The graduating-Roblox-dev proof and the netcode pillar's showcase (`docs/networking.md` arcs): server authority with prediction, lag-compensation history as an engine primitive, documented replication semantics with telemetry, and the viewmodel rendering path (an R-arc addition: own pass, own FOV, depth remap). The FPS case study and `roblox-pitfalls.md` section 3 are the requirements list.

**Rung 3: a co-op open-world survival game.** The reference image is Valheim, not AAA: a five-person-scale game, low-fi art, persistent shared world. The chunked unbounded world and world-size-independent editing mean the world half is already architecturally ahead of the indie competition; this rung forces **streaming at scale, terrain (which does not exist), instancing (`R12`), and runtime save games** (distinct from `P2` map persistence: player-state saves are an entity-arc concern). "AAA-type open world" is not said out loud until long after this rung ships.

**Rung 4: an RTS, explicitly deferred.** The command-log netcode model fits RTS naturally and the determinism posture helps, but three load-bearing subsystems exist in no plan today: **pathfinding/navmesh, crowd-scale instanced animation, and RTS-density game UI**, and RTS maps want terrain, not brushes. The architecture must never forbid this rung (that is the generality constraint working); the pitch must never promise it until pathfinding exists.

## 5. The bill for generality: subsystem arcs the ladder forces

Named here so `ROADMAP.md` can absorb them as arcs when their rung approaches; deliberately not scheduled in this document. In ladder order of first need: **game UI**, **audio**, **skeletal animation** (the README's Animation arc, promoted from a placeholder), **terrain**, **runtime save games**, **pathfinding/navmesh**, plus the already-planned instancing (`R12`). The absence of these from today's roadmap is the measured gap between "excellent foundations" and "an engine indies evaluate favorably in the first hour"; the ladder retires that gap one rung at a time instead of all at once.

## 6. What is not promised early

2D as a first-class mode, mobile, consoles, and real AAA content pipelines. These are absences to state plainly when asked, not directions to argue about. Nothing in the architecture forecloses them; nothing in the next several rungs funds them.

## 7. The promises that differentiate

1. **Ownership, structurally.** A Spectra game is a standalone AOT binary; assets are local files; the map is text; there is no platform between developer and player unless they choose one. Against the primary audience's history, this is the loudest promise available, and it costs nothing because it is already true.
2. **Easy start, no ceiling** (section 2's ramp), the direct inversion of the sealed-layer pattern that defines their old home.
3. **Nothing degrades silently**, elevated to contract in `roblox-pitfalls.md` section 2.
4. **The guardrails are written down.** `roblox-pitfalls.md` is a public constitution: the failure modes of the platform this audience is leaving, each with the structural reason it cannot recur here.

## 8. Open decisions this document surfaces

1. **The license.** Unmade, and existential for the trust question every indie now asks first (post-Unity-runtime-fee, source availability is the number one adoption filter for a new engine). Open source with a permissive or protective license, source-available, or proprietary changes the ceiling on everything in section 1. Needs an explicit sign-off; every month undecided compounds.
2. **Template ownership.** Each rung's template game is real content (art, sound, design). Deciding who builds it, and to what quality bar, is a real cost to plan, not a byproduct.
3. **The wedge's name.** "The world-builder's engine" is the working phrase for section 1's identity; the final wording is marketing, but the identity itself is now pinned and later documents should not drift from it.
