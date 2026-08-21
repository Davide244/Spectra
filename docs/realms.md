# Realms: audience and liveness as node properties

> **Status.** Design, not implementation. Nothing in this document exists in the tree today. Every claim about the current codebase carries a `file:line` and was read on 2026-08-21; every claim about Roblox behaviour was verified against `create.roblox.com/docs` rather than recalled.
>
> **Naming is LOCKED: the axis is `Realm`.** Chosen by the user on 2026-08-21, over *scope*. Three reasons, on record: *realm* is Garry's Mod / Source Lua vocabulary for exactly this axis (`sv_`/`cl_`/`sh_`), so it arrives already meaning the right thing to the Source-lineage developer it is aimed at; `docs/networking.md` already uses *scope* for interest management, and shipping both words would collide in the two documents that have to be read together; and the value is written into every `.smap` node record, every `.scmap` `NODE` payload-flag bit and every `.sentdef` `Flags` bit pair, so it cannot change once content exists. **This closes §9 Q8 and `networking.md` §9 item 12.** Standing consequence: nothing in these documents may use *scope* to mean node audience, and nothing may use *realm* to mean interest management.
>
> **The supersession of the container model is COMPLETE — do not reinstate it.** This document replaced `docs/networking.md` §4.1's container paragraph ("Replication scope is defined by WHERE a node lives, not by a flag… There is deliberately no setter") and the container rows of `docs/roblox-to-spectra.md`. **Both corrections have landed**: `networking.md` carries the amendment at the top of the document and states the realm model directly in §4.1, and `roblox-to-spectra.md`'s tree-and-services rows are rewritten and marked **Corrected**. No document in this set still asserts the container model, and nothing may re-derive audience from tree location again. Everything else in `networking.md` §4.1 — the four leak channels, the accessor gate, the realm-aware resolver, the CSG/BVH admission conditions, `net_strictlocalclient` — survived that change unchanged; only the *source* of the byte moved, from "nearest well-known ancestor" to "nearest explicit declaration". (Cross-references between these documents name sections and rule ids, never line numbers, because line numbers go stale the first time either file is edited.)
>
> **Reading the `R*` ids in this document.** `R1`–`R17` below are **rules of this design**, not `ROADMAP.md`'s rendering-arc milestones (`R1`, `R9`, `R10`, …) and not its cross-arc rulings (written `R‑1`, `R‑3`, … with a dash). Other documents therefore cite these as `realms.md R9`, qualified by the document name; do the same when citing them.

---

## At a glance

Roblox asks *"who can see this?"* by making you drag the object into a particular folder. Spectra asks the same question with a property on the object, wherever it already lives.

The same game, both ways:

```
ROBLOX                              SPECTRA
Workspace                           Scene.Root   (no Workspace container,
                                                 and no World folder either)
  └ Enemy                             └ Enemy           (Shared — inherited; you set nothing)
ServerStorage                         └ EnemyTemplate   (Dormant — and Shared, because
  └ EnemyTemplate                     │                  it is built from brushes: R15)
ServerScriptService                   └ EnemySpawner    (Server)
  └ EnemySpawner                      └ HealthBar       (Client)
StarterGui
  └ HealthBar
```

Note the one place the translation is not a straight swap: **a template made of brushes stays `Shared`.** R15 refuses a brush subtree under a non-`Shared` node — a brush *is* the compiled static world every client renders and collides against — so the audience half of `ServerStorage` is unavailable for exactly the commonest template there is, and `Dormant` alone is what parks it. `Server` + `Dormant` is for parked data and non-brush content. §5 has the full argument.

Note what moved: **the spawner now sits next to the enemies it spawns.** In Roblox it cannot — it has to live in `ServerScriptService`, structurally separated from the thing it is about, because that folder *is* how you said "server only".

| Roblox container | Spectra |
| --- | --- |
| `Workspace` | nothing to set — `Shared` + `Active` are the defaults |
| `ServerStorage` | `Realm = Server`, `State = Dormant` — or just `State = Dormant` for a template holding brushes, which R15 keeps `Shared` |
| `ServerScriptService` | `Realm = Server` on a script node |
| `ReplicatedStorage` | `Realm = Shared`, `State = Dormant` |
| `StarterGui` / `StarterPack` | `Realm = Client` |
| `Script` | a script node whose realm resolves to `Server` |
| `LocalScript` | a script node whose realm is `Client` |

Eight containers with unwritten rules become two dropdowns. Inheritance behaves exactly as a folder did — mark one node `Server` and its whole subtree is server-only — except the mark goes on *any* node, so the tree stays organised around the game rather than around the replication system.

The next section answers the obvious objection to all of it — that a property nobody has been told about organises nothing, so a fresh project would be a bare root with everything piled under it. The rest of the document is the precise version of the idea above: the lattice, the resolution rule, the enforcement seam, and the one hard refusal on the static world.

---

## The bare-root objection, and what a new project actually ships

> *"One thing thats genuenly good about the roblox structure, is that it enforces some sort of organization for free, though. This system right now will result in everything being under the World node."* — the user, 2026-08-21

**The objection is correct, and it is the strongest one raised against this model.** Everything above argues that Roblox's containers are a bad *replication mechanism*. None of it disputes that they are a genuinely good **starter kit**, and that is a separate virtue with three parts: an empty project already has a place for everything; the names teach that the concepts exist, before you have any reason to look them up; and every project looks broadly alike, so you can open someone else's and navigate it. Realms alone delete all three and hand the user a bare root. A property nobody has been told about cannot organise anything.

### The answer: folders come back as content, never as engine concepts

A payload-free `SceneNode` already *is* a folder — `SceneNode.CreateChild` (`SceneNode.cs:287`) needs nothing else — so the recovery costs no new type and no new concept. **A new project ships a template tree whose folders already carry declared realms.** Dragging a node into `ServerLogic` then works exactly as dragging it into `ServerScriptService` did, by R1, with no property ever opened; a beginner can ship a game without learning that realms exist. And because they are ordinary nodes, they stay renameable, deletable, nestable and ignorable — which is the escape hatch that motivated the entire redesign. The spawner still goes next to the enemies it spawns; the template just means you have somewhere obvious to put it if you have no opinion yet.

> ### The hard rule
>
> **No engine code may EVER check for a folder by name.** Not `Scene`, not `SceneNode`, not the CSG or BVH admission predicates, not the loader, not the cooker, not the Luau bindings, not the editor's own systems. There is no well-known-name lookup, no `Scene.ServerLogic` accessor, no `GetService("ServerLogic")`, no default that keys on a string, and no configurable list of names either — "configurable" is how name checks get in.
>
> The moment one name is special-cased, the container zoo is rebuilt with extra steps and a worse story: the folder would once again *mean* something the property does not, the two would drift, and every rule in §3 would acquire a silent exception nobody can see in the tree. **The template is content — a file the editor copies — and the engine must be unable to tell that it ever existed.** Pinned by test pin 12 in §10, which is deliberately the same reflection-based boundary assertion R10's counting-oracle pin uses.

### The tree a new project gets

Everything below is an ordinary `SceneNode`. Nothing here is a type, a service, or a name the engine knows.

```
Untitled Place                     root · Shared · Active (permanent, R4)
│   The scene name (.smap `scene.name`). The root IS the world — Luau `workspace`
│   aliases it (§7.1), so `workspace.Baseplate` ports character-for-character.
│   `scene.spawn` is set above the baseplate as a VALUE in the map
│   (`formats-and-pipeline.md` §2.6's `scene.spawn`), deliberately NOT a SpawnPoint node.
│
├── Baseplate                      no declaration · brush
│      A real brush, not a folder. Teaches by existing that the default case
│      needs no marking: its dimmed inherited badge sits beside five solid ones.
│
├── ServerLogic                    Realm = Server
│      The game's authority: round rules, scoring, spawners, saves.
│      Replaces ServerScriptService.
│
├── ClientLogic                    Realm = Client
│      Per-player scripts: camera, input, local effects. One private copy each.
│      Replaces StarterPlayerScripts and LocalScript-by-location.
│
├── Interface                      Realm = Client
│      HUD and menus; the source the `playerSpawnRules` of §7.2 point at.
│      Replaces StarterGui. Deliberately a SECOND Client folder — two folders,
│      one realm, is the lesson: folders are organisation, realm is data.
│
├── Templates                      State = Dormant  (Realm inherited = Shared)
│      Parked content you clone: not carved, not drawn, not queried, not
│      ticking. Replaces ServerStorage's second job. MUST be Shared, never
│      Server — R15 refuses a brush subtree under a non-Shared node, so a
│      ServerStorage-shaped folder would reject the commonest template there is.
│
└── Modules                        no declaration (Inherit → Shared · Active)
       Shared Luau modules, required from both sides, one instance per side
       (§6.1). A second dimmed row, reinforcing that shared needs no marking.
```

**An `Empty` template — root, `scene.spawn`, nothing else — ships beside it, one click away in the same dialog.** That is not a footnote: this design's thesis is that the tree belongs to the user, and shipping only an opinionated tree would contradict it in the first dialog a new user ever sees.

**New Map inside an existing project offers `Empty` and `Level` (root + spawn + baseplate) only** — never the logic folders. A project's logic folders already exist in its startup map, and duplicating them across five maps is how *"which `ServerLogic` is the real one"* starts.

### Four naming and shape decisions, with their reasons

1. **There is no `World` folder — world content sits directly under the root.** `Scene.Root` is already the `Workspace` equivalent by explicit decision here (§7.1) and in `roblox-to-spectra.md`, which promises that `workspace.Wall` ports character-for-character. A `World` child makes it `workspace.World.Wall` in every template-created project, breaking that promise on first use. It is also the one folder whose declaration is a **no-op**: `Shared`/`Active` under a `Shared`/`Active` root is either a redundant key or, per §2.5's omit-iff-`Inherit` rule, nothing at all. A folder carrying no declaration teaches nothing about the model while costing a path segment on the project's hottest content — and worse, a folder named `World` invites the belief that the *folder* is what makes content live, when liveness is `IsLive`. **The discoverability `World` was meant to buy is bought better by shipping a `Baseplate`**: an empty folder teaches by name, a baseplate teaches by existence, and it demonstrates the model's most important fact — the default case needs no marking — in one glance.
2. **The realm word appears as an adjective, never as the bare value: `ServerLogic`, never `Server`.** A folder literally named `Server` collapses two operations into one sentence — "move it to Server" and "set its realm to Server" — when separating location from data is the entire point. And the moment the expert declares `Realm = Server` on a node outside it (the spawner beside its enemies, the motivating case), the name reads as an invariant that is false. A compound name states the job, hints the realm, cannot be confused with the value token, and can be renamed by the user without it feeling like renaming a system concept. Dropping the realm word entirely (`Gameplay`/`Library`) was rejected for the opposite reason: the template's job is to teach that the axis exists, and a name that never says "server" cannot.
3. **`Templates`, not `Prefabs`.** §7.4 states plainly that a parked template is a `Dormant` subtree and **not** a prefab until `P10`, which `ROADMAP.md` `P10` puts off the critical path. A folder named `Prefabs` promises an unbuilt feature and teaches a word with a specific meaning to people who cannot use it. `Templates` is what this document's own worked example already calls it (`script.Templates`, `templates.Grunt:Clone()`), describes exactly what is inside, and absorbs real prefabs later with no rename.
4. **`Interface`, not `UI`** — every other name in a six-row teaching tree is a full word, and a lone initialism reads as inconsistency in the one artifact whose job is to be read. This is the weakest of the four calls and `UI` is defensible; what is *not* defensible is two different starter names across two documents, so §7.2's example paths are corrected in the same change.

### R7 amendment: the dormant `AddChild` refusal is deleted

The `Templates` folder is unusable on day one without this, and the contradiction it removes is already in the document. R15's own refusal message tells the user that **`State = Dormant` is how you park a brush subtree** — while R7 as first written threw `BrushSubtreeUnderDormantParent` when that same subtree was *dragged into* an already-dormant folder. Setting `Dormant` on the subtree was blessed; dropping the subtree into `Dormant` threw. The beginner's first non-trivial gesture — drag the house you just built into `Templates` so the spawner can clone it — hit an exception dialog.

It also closes nothing. `State` is a mutable admission filter whether it is declared on the node or inherited from an ancestor, which is precisely why R17 requires `Scene.MarkAdmissionChanged()` on **every** `State` transition over a brush-bearing subtree — the guarantee comes from that bump, not from the refusal. **R7's realm half stays**, because it has an independent correctness argument (the imprint channel, R15/R16) that dormancy does not share.

### How a template ships, and why the engine never learns one existed

- **A template is a directory that is already a valid project** — `game.spectraproj`, `Maps/Startup.smap`, `Assets/` — plus exactly one file the instantiated project never receives: a small `template.json` manifest (display name, one-line description, sort order, icon). Deliberately not in the `s*` family (it is editor tooling metadata: never content, never cooked, never mounted) and deliberately not a block inside `game.spectraproj`, where unknown keys warn and are *preserved* (`formats-and-pipeline.md` §3.3) and a template field would therefore survive into every shipped project meaning nothing.
- **Resolution is an ordered search path** — built-in (`<editor>/Templates/`) → per-user → studio/shared, later entries winning by directory name. Identical in shape to `game.spectraproj`'s `packs` array, which is already *"an ORDERED array; later entries win — that is the mod and patch story, free"*. A studio ships house structure by dropping its own `Game` template on a later path entry: content, versioned in their repo, requiring nothing from the engine.
- **The shareable form is a ZIP of that directory.** Not a new decision — `formats-and-pipeline.md` §2.1 already reserves ZIP for *"authored-source interchange bundles — a prefab or asset drop a user emails, a template project"*.
- **Instantiation, in full:** copy the directory verbatim → drop `template.json` → set `name` and a fresh `id` Guid in `game.spectraproj` → **re-GUID every node in every `.smap`** → rewrite through the canonical writer. Copying node GUIDs verbatim would give every project ever made from the template identical node ids and collide two maps from one template inside one project — and Guid identity is load-bearing for `IEditorCommand` addressing, collaborative editing (`networking.md` §3.3) and NetId assignment.
- **No provenance is recorded anywhere.** No template id, no inheritance link, no "update from template". A live dependency on a template would make the folder names semantically load-bearing again, which is the failure this section exists to prevent. A template is a starting position, not a parent. Pinned by test pin 13.
- **"Save Project As Template…"** writes the current project into the user templates directory minus `logs/` and every cooked artifact, generates a `template.json`, and **shows the exclusion list before writing** — a template is the one artifact designed to be handed to strangers.

### Enforcement is advisory, and that is not a compromise

Roblox's organisation is enforced by **breakage**: a `LocalScript` in `ServerStorage` does not run, so the folder is a *claim about* the node that can be wrong. Here the folder **is the mechanism that makes the claim true** — a node dragged into `ServerLogic` resolves to `Server` by R1, full stop. The Roblox failure mode (node in the wrong folder misbehaves) is not merely discouraged; it is **unreachable**.

So the obvious lint cannot fire. *"Warn when a node's realm disagrees with its folder's"* has no satisfiable condition: by R1 the effective value always **is** the folder's, narrowed. The only way a declaration can differ from its parent's is a narrowing — and narrowing beside the thing it is about is the feature this whole redesign exists to permit. That warning would fire on the expert path and on nothing else. **Reject it, and reject a project-conventions file for the same reason**, plus a second: it would have to key on names.

The lattice already carries every legitimate tooth, and each one is name-blind:

- **R4** refuses an explicit widening or a disjoint write at the setter.
- **R3's `Inert`** is the one genuine mistake state the model has, already required to be badged unmistakably (§7.5 item 2) — and it now also becomes a **cook diagnostic: warning by default, error under `--strict`**, matching the asymmetry `formats-and-pipeline.md` §4.2 already pins for unresolved connections and unknown keys. Content that runs nowhere, draws nowhere and replicates nowhere is always either a mistake or unfinished, and the cook is where "unfinished" stops being acceptable. It keys on the resolved `RealmSet`, never on a name.
- **R8's** non-suppressible notice when a gesture changes where a script runs.
- **R15's** three refusals on brush subtrees.

**The escape valve that preserves the hard rule:** a studio that genuinely wants *"all server scripts must live under `ServerLogic`"* writes it as a **Luau editor plugin** — `formats-and-pipeline.md` §3.5 already settles that editor plugins are Luau running against the edit-mode VM's node and selection surface. That is their policy, in their tool, on their projects, and the engine still contains zero folder names.

### The payoff the folder model structurally cannot match

Because realm is **data rather than location**, the editor can organise by it **on demand**: filter the Explorer to everything `Server`, colour by realm, or switch to a group-by-realm view — while the tree itself stays organised for the game. **Roblox gets exactly one view of a project, forever, because there location *is* the grouping.**

One implementation note decides whether this is the headline payoff or a useless feature: **group by DECLARATION and list the subtree roots, not by effective value listing every node.** Grouping by effective value puts the entire project under `Shared` and is worthless at any real size. Grouping by declaration answers the question people actually have — *"where are the exceptions in this project"* — in a list whose length is the number of decisions someone made, not the number of nodes they have.

The editor obligations this creates are acceptance criteria, not polish, and they live with the rest of them in **§7.5** — the Realm column (item 1), the badge vocabulary (item 2), the `View as` lens (item 4), one-gesture setting from the tree (item 6), `IEditorCommand` coverage (item 8), and the filter-and-group-by-declaration views (item 13) — rather than being restated here. Two more are specific to dragging into a template folder and are added there: **the drag preview must show the resulting badge before the drop** (item 10), and **reparent-by-drag must preserve the world transform** (item 11), because `AddChild` preserves the *local* transform and `WorldMatrix` is `local * Parent.WorldMatrix` (`SceneNode.cs:225–237`), so without compensation dragging a part into any folder with a non-identity transform teleports it — and "dragging into a folder just works exactly as in Roblox" would be false.

### Two narratives, and the one wall each hits

**The beginner, who never learns realms exists.** New Project → Game. She sees a place name, a baseplate and five folders. She drags parts into the viewport; they land under the root, she touches no property, they carve the world and every player sees them. She builds a HUD inside `Interface` and it appears on each player's screen. She adds a round timer to `ServerLogic`; its badge reads `runs: Server · inherited` (§6.5) and it runs once, authoritatively. She builds an enemy, drags it into `Templates`, watches it ghost out of the world, and her spawner clones it and sets `State = "Active"`. Five folders, zero properties, no dropdown ever opened. **She hits exactly one wall, and it is unavoidable:** dragging a *brush* model into `Interface` is refused by R15, because brushes are permanently `Shared`. The obligation that creates is that the refusal arrives as a forbidden-drop cursor carrying R15's message **before the mouse is released**, never as an exception dialog after it (§7.5 item 10).

**The expert, who puts the spawner next to its enemies.** He has an `Arena` folder he named himself, holding grunts, cover and a spawn logic node. He selects `Arena/Spawner`, right-click → Realm → Server: one gesture from the tree, the badge goes solid, and the node stays next to the thing it is about — which is the sentence this whole design was built to make true. The brush templates it clones sit beside it as `Shared` + `Dormant`, because R15 will not let them be otherwise, and he can see that in the badges without reading a doc. He deletes `ClientLogic` and `Interface` because his game has no UI, and nothing breaks. Then he filters the Explorer to `Server` and sees **every server declaration in the project in one list, wherever it lives.** He never once moved a node to change what it means.

### The three risks this section owns

1. **`playerSpawnRules` reference template nodes by PATH** (§7.2). That is the single place where a folder name becomes load-bearing *data*, and it breaks silently the moment a user exercises the promised freedom to rename or delete the folder. It needs rename-repair in the editor (§7.5 item 12) and a loud unresolved-spawn-rule diagnostic at cook. It is not a violation of the hard rule — the name lives in the user's own project file, not in engine code — but it is the closest thing to one, and it should be the last such reference ever added.
2. **The naming cost is the same shape as the realm/scope decision itself.** These five names go into every tutorial, every forum answer and every third-party template. Renaming `Interface` to `UI` after content exists is cheap in the engine and expensive in the ecosystem. Decided here, once.
3. **A ZIP-imported third-party template is arbitrary content the editor opens with full trust** — it can carry scripts under `ServerLogic` that run on the first Play. Template import needs the same posture as opening any untrusted project, and the import dialog must list the scripts a template contains before it lands on disk.

---

## 1. What this replaces, and the precise diagnosis

The ask was: *"Let's take the opportunity of us reinventing the wheel to actually change this to a good intuitive system that fits us."* The thing being replaced is Roblox's container zoo — `ServerStorage`, `ServerScriptService`, `ReplicatedStorage`, `ReplicatedFirst`, `StarterGui`, `StarterPack`, `StarterPlayerScripts`, `StarterCharacterScripts`, with `Workspace` as the ninth that everything else is defined against.

### The diagnosis

Roblox encodes **four orthogonal axes** in **one dimension — tree location**:

| Axis | The question it answers | How Roblox asks it |
| --- | --- | --- |
| **Audience** | who holds this data at all | `ServerStorage`/`ServerScriptService` vs `ReplicatedStorage` vs `Workspace` |
| **Replication timing** | when does it arrive on a client | `ReplicatedFirst` vs everything else |
| **Liveness** | is this live world content, or a parked template | `Workspace` vs any storage container |
| **Per-player templating** | is this copied per player, and when | the four `Starter*` containers |

One dimension cannot carry four independent values. The consequences are mechanical, not stylistic:

- **The axes cannot be combined.** `ServerStorage` and `ServerScriptService` have *identical* audience and differ only in liveness — that is why there are two folders with one word of difference in their names and no documentation that says so plainly. `Shared + parked` is not expressible at all.
- **The rules are tribal knowledge, because a folder name cannot carry a rule.** Each container silently answers "does it replicate / in which direction / do scripts run / is content copied / when / does it render", and the answers live in forum threads. Roblox's own partial fix proves it: `Script.RunContext` (shipped 2022, `{ Legacy, Server, Client, Plugin }`) decoupled exactly one axis — run location — and left the rest coupled, so a `RunContext = Client` script placed in `ServerScriptService` **silently does not run** (the container does not replicate, so the client never receives the script), and in `StarterPlayerScripts` Roblox's own announcement records the failure verbatim: *"Starter containers are copied to clients, though, so the original script and the copy run, which isn't desirable."* A feature whose documented guidance is "don't use it here" is a partial decoupling failing in public.
- **And the one that actually hurts every day: you cannot put a server-only spawner next to the enemies it spawns.** The spawner must live in `ServerScriptService`; the enemies must live in `Workspace`; the templates must live in `ServerStorage`. Three folders, one feature, and the tree is organised for the replication system instead of for the game.

The fix is not a better folder set. It is to stop overloading location: **the tree is organised for the game, and the exceptions are marked where they naturally sit.**

### What this document decides

Two inherited node properties, one lattice, one resolution rule, one enforcement seam, and one hard refusal on the static world. It is a scene-graph and data-model design first — it changes `SceneNode`, `Scene`, the CSG admission predicate and the BVH — and a networking design only second, which is why it is its own document and not a section of `networking.md`.

---

## 2. The two properties

### 2.1 Why two, and not one

The tempting version of this design is one inherited property answering "who sees it", with parked templates handled by prefabs. **That is false today, and verifiably so.**

- **Prefabs are `ROADMAP.md` `P10`, size L, on the `P4 → P5 → P6 → P7 → P8 → P9 → P10` chain, and `ROADMAP.md` line 92 lists prefabs explicitly among the things that are *off the critical path*.** `.scmap` reserves `PayloadKind 5 PrefabRoot` and nothing else exists. Pointing at prefabs for "a model I clone later" leaves a developer with nothing for a very long time.
- **There is no way to make a brush inert.** `Scene.SnapshotFullWalk` (`SpectraEngine.Core/Scene/Scene.cs:1349–1369`) iterates `Nodes` and admits **every** node carrying a `Brush`, unconditionally — there is no enable flag anywhere on that path. `SceneBvh.IsSpatial` (`SpectraEngine.Core/Scene/SceneBvh.cs:146`) is `node.MeshRenderer is not null || node.Brush is not null` — ancestry-blind.

So a parked brush template **carves the world**. It punches its shape as a hole through every neighbouring brush, in the one compiled artifact every client renders and collides against. A design that deletes `ServerStorage` and offers nothing for its second job does not simplify the model; it removes a capability.

Therefore: **two inherited bytes. Ship both or ship neither.**

### 2.2 The enums

```csharp
// SpectraEngine.Core/Scene/NodeRealm.cs

/// <summary>
/// WHO holds this node. Declared per node, inherited down the subtree.
/// This is Roblox's ServerStorage / ServerScriptService / ReplicatedStorage
/// axis, lifted off tree location so the tree can be organised for the game.
/// </summary>
public enum NodeRealm : byte
{
    /// <summary>Take the parent's answer. The default for every node, and the
    /// value that is omitted from the serialized record.</summary>
    Inherit = 0,
    /// <summary>Server and every client.</summary>
    Shared  = 1,
    /// <summary>Server only.</summary>
    Server  = 2,
    /// <summary>Every client, each holding a private divergent copy. This is
    /// LocalScript and StarterPlayerScripts, collapsed.</summary>
    Client  = 3,
}

/// <summary>
/// Whether this node is LIVE: carving the static world, occupying the BVH,
/// drawn, queried, ticking its script. This is Roblox's Workspace-versus-
/// storage axis. A Dormant subtree is a template you can park next to the
/// thing that clones it.
/// </summary>
public enum NodeState : byte { Inherit = 0, Active = 1, Dormant = 2 }
```

**`NodeRealm.Owner` is deliberately absent**, and this is a correction to an earlier proposal rather than an omission. "Server plus exactly one player" (Roblox's `PlayerGui`/`Backpack`) cannot be a realm, because owner-realm content is by definition replicated — to exactly one client — so it needs a wire identity, while NetIds are assigned only to `Shared` nodes so that a client-target and a server-target cook of one `.smap` produce identical numbering. Those two rules are mutually exclusive. **Per-player content is a per-client replication filter over `Shared` nodes**, which is what `networking.md` §4.3's per-client interest bitset already provides, and what Roblox actually does underneath. It is not a fifth audience.

### 2.3 The effective values, and why they are sets

The declared value is an enum. The **effective** value is a *set*, because resolution is an intersection (§3) and an intersection can be empty:

```csharp
/// <summary>The RESOLVED audience: which sides hold this node. Never a
/// declaration — always the intersection of this node's declaration with its
/// parent's resolved answer. The empty set is a real, reachable value.</summary>
[Flags]
public enum RealmSet : byte
{
    /// <summary>Inert: no side holds this node as live content. Reachable only
    /// by reparenting a Client subtree under a Server ancestor or the reverse.
    /// Its scripts run nowhere; the Explorer badges every affected node.</summary>
    Inert  = 0,
    Server = 1 << 0,
    Client = 1 << 1,
    /// <summary>Both sides. The root's permanent value.</summary>
    Shared = Server | Client,
}
```

`NodeState` resolves the same way over a one-bit set (`Live`), so the two properties share **one** implementation:

```csharp
// The whole resolution rule, both axes, no branches.
private static RealmSet ToSet(NodeRealm d) => d switch
{
    NodeRealm.Server => RealmSet.Server,
    NodeRealm.Client => RealmSet.Client,
    _                => RealmSet.Shared,   // Inherit and Shared are both "the full set"
};

effectiveRealm = ToSet(declaredRealm) & inheritedRealm;
effectiveLive  = (declaredState != NodeState.Dormant) & inheritedLive;
```

`Inherit` and `Shared` map to the same set on purpose. They differ in exactly two places: whether an explicit write is legality-checked (R4), and whether the value is written to disk (§2.5 — omitted iff `Inherit`). That is the whole difference, and stating it plainly heads off the first question every implementer will ask.

### 2.4 Fields, defaults, and packing

**`partial` is a change this design requires, not the state of the tree.** `SceneNode` is declared `public class SceneNode` (`SceneNode.cs:14`) and is not `partial` anywhere in the solution today, so the `partial` keyword below is part of the work: either add it to the existing declaration (which `networking.md` §4.4 also assumes for `SetNetworkOwner`, so the two arcs want the same one-word edit) or drop it and add these members to the single existing file. The keyword is used here only to show the addition in isolation — nothing in the design depends on the members living in a second file.

```csharp
public partial class SceneNode
{
    private NodeRealm _declaredRealm = NodeRealm.Inherit;   // authored; serialized when != Inherit
    private NodeState _declaredState = NodeState.Inherit;
    private RealmSet  _effectiveRealm = RealmSet.Shared;    // resolved; never a declaration
    private bool      _effectiveLive  = true;

    /// <summary>The resolved audience. O(1) — a cached field, maintained on
    /// declaration change and on reparent only, NEVER on the transform path.</summary>
    public RealmSet EffectiveRealm => _effectiveRealm;

    /// <summary>True when this node participates in the live world: carve, BVH,
    /// draw, query, tick. The single predicate every subsystem consults.</summary>
    public bool IsLive => _effectiveLive;

    /// <summary>One AND and one compare. This is the entire enforcement primitive.</summary>
    public bool IsVisibleTo(RealmSet mask) => (_effectiveRealm & mask) != 0;
}
```

Four bytes added to `SceneNode`. **Do not claim they land in existing padding** — the CLR reorders fields and nobody has measured this node's layout; if the 50k-part benchmark is the gate, measure it there rather than asserting it here.

Defaults are chosen so that the 99% of nodes that never think about either axis pay two zero bytes and no decision: **`Inherit` is `0` for both, the root resolves to `Shared` and `Active`, and a detached subtree root resolves its own declaration against `Shared`/`Active`** so effective values are total and there is never a null state.

### 2.5 Where the bytes live on disk

| Carrier | Encoding | Rule |
| --- | --- | --- |
**This document is normative for what the values MEAN; `formats-and-pipeline.md` is normative for how they are ENCODED.** The bit numbers and member order below are quoted from it, not decided here, and the two must be changed together.

| Carrier | Encoding | Rule |
| --- | --- | --- |
| `.smap` node record | `"realm"`, `"state"` — lowercase strings from a closed vocabulary (`shared \| server \| client`, `active \| dormant`) | **Omitted iff the declared value is `Inherit`.** Written after `"name"`, before `"transform"`. Never a numeric enum, never the *effective* value (that is derived data, and `P2` forbids storing derived data). Spelled out in `formats-and-pipeline.md` §2.6. |
| `.scmap` `NODE` record | **declared** realm + state in `PayloadFlags` **bits 3–4** (realm) and **bits 5–6** (state), each a 2-bit value | Allocation owned by `formats-and-pipeline.md` §2.7, which also holds bits 0–2 (`HasSource`/`IsEntityOwned`/`CanReCarve`). Effective is derived in the pre-order forward pass that already rebuilds the tree (records are ordered `ParentIndex < SelfIndex`, so it is free). **Storing effective only is wrong** — a runtime reparent could not recompute. |
| `.sentdef` keyvalue record | per-property realm in `Flags` **bits 6–7**, as a 2-bit value | A **different record from the one above, with a different allocation** — do not carry `.scmap`'s bit numbers across. Bits 0–2 are `readOnly`/`hideInEditor`/`requiresRestart` (`formats-and-pipeline.md` §3.2); bits 3–5 are `replicated`/`unreliable`/`clientWritable` (`networking.md` §3.4, whose table is the single allocation of this u32). **Do not touch 3–5 and do not grow the fixed 32-byte record.** `D15`'s C#↔Luau byte-identity parity pin extends to the new bits in the same change. |

**`"realm"` and `"state"` are reserved keys**, never captured by `formats-and-pipeline.md` §2.6's unknown-member preservation — the reserved-key list itself lives in that section and is not restated here. The reason is this document's: a misspelled realm surviving as a preserved unknown member means the node declares nothing and falls through to `Shared`, which is a data leak on load rather than a lost setting.

---

## 3. The lattice and the rules

Two operations, and they behave differently on purpose. **An explicit write is checked. A reparent is clamped.**

### R1 — Resolution is an intersection, and it is one line per axis

```
effectiveRealm(node) = ToSet(node.declaredRealm) & effectiveRealm(node.Parent)
effectiveLive(node)  = (node.declaredState != Dormant) && effectiveLive(node.Parent)
```

with `effectiveRealm(root) = RealmSet.Shared` and `effectiveLive(root) = true`. `EffectiveRealm` is never a declaration and is never `Inherit`. Every consumer reads the effective value; nothing outside `SceneNode`, the serializer and the editor's Properties panel reads the declared one.

### R2 — Realm may only narrow going down, and the reason is this engine's, not a policy preference

`SceneNode.WorldMatrix` is `local * Parent.WorldMatrix` (`SceneNode.cs:225–237`). Consider a `Shared` child under a `Server` parent — a widened child under a hidden ancestor. The client does not have the parent. So the client must do one of exactly two things:

1. **Reparent the child to its nearest visible ancestor**, which silently changes its world transform. Server and client now disagree about where a replicated object *is*. That is geometry divergence with no error anywhere — the worst failure shape available, because it is invisible until a player is standing inside a wall.
2. **Ghost-replicate the hidden ancestor chain**, sending the names and transforms of exactly the nodes that were marked hidden — leaking the thing the declaration existed to hide.

Both are unacceptable, so the case is refused rather than resolved. The same argument closes the third option (dropping the child): a node whose parent is server-only genuinely has no client-side position, and pretending otherwise is what options 1 and 2 are.

**The `State` axis does not inherit this argument, and must not be given it.** A live child under a dormant parent has a perfectly computable world transform — the parent is still in the graph, just not admitted. The reason `State` narrows anyway is different and weaker, and should be stated as what it is: `Dormant` means *this whole subtree is parked*, and if one descendant could escape, admission stops being a subtree property and becomes a per-node walk on the CSG snapshot's hot path. It narrows for the compile's sake, not for correctness's.

### R3 — Clamping is an INTERSECTION, and the empty intersection has a name

`Server ∩ Client` is **empty**, not "the ancestor's value". A rule that clamps a reparented subtree "to the ancestor's realm" takes a `Client` HUD/VFX subtree dragged under a `Server` parent and turns its scripts into **server** scripts — code that ran on each client with no authority now runs once, on the authority, *with* authority, from a drag, behind one warning line. That is a privilege escalation performed by a mouse gesture, and it is a more severe version of the exact flaw ("moving an object silently changes its semantics") this redesign exists to delete.

So the empty intersection resolves to **`RealmSet.Inert`**: the subtree exists nowhere as live content, its scripts run nowhere, it is not drawn, not carved, not queried, not replicated — and the Explorer badges every affected node with the reason. **Never promote.**

### R4 — An explicit write must name a legal subset; it never clamps

```csharp
public NodeRealm Realm
{
    get => _declaredRealm;
    set
    {
        if (value == _declaredRealm) return;          // house idiom: a no-op write does nothing
        if (Parent is null && Owner is not null)
            throw new InvalidOperationException("The scene root's realm is permanently Shared.");

        RealmSet inherited = Parent?._effectiveRealm ?? RealmSet.Shared;
        RealmSet requested = ToSet(value);
        if (value != NodeRealm.Inherit && (requested & inherited) != requested)
            throw new InvalidOperationException(RealmMessages.CannotWiden(this, value, inherited));

        // R11 — the static-world refusal, checked before anything moves.
        if (value is NodeRealm.Server or NodeRealm.Client && _subtreeBrushCount > 0)
            throw new InvalidOperationException(RealmMessages.BrushSubtree(this, value));

        _declaredRealm = value;
        PropagateRealm(inherited);
    }
}
```

The subset test does all the work: `Shared` under a `Server` parent throws (widening), and `Client` under a `Server` parent throws too (disjoint) — which is right, because an *explicit* write that could only ever produce `Inert` is a typo, not an intent. `Inert` is reachable only by reparenting, where it is a consequence of a gesture the user can undo. Explicit writes name a legal subset or fail, and **the failure message names both realms and the node**.

### R5 — A reparent into a narrower parent clamps, does not rewrite declarations, and is exactly reversible

Reparenting never throws on realm grounds (the one exception is R11's brush refusal). It recomputes the effective set as the intersection and leaves every declaration untouched. Moving the subtree back restores it **exactly**, because there was nothing to restore — the declarations never changed. This is what makes `Inert` survivable: it is a visible, reversible state, not a lossy coercion.

`State` clamps the same way. A subtree dragged into a `Dormant` parent goes dormant and comes back live when dragged out.

### R6 — Propagation uses `SetOwner`'s early-out, and only it

```csharp
// Same shape and the same early-out as SetOwner (SceneNode.cs:343-358): if this
// node's answer did not change, no descendant's did either, so stop.
internal void PropagateRealm(RealmSet inherited)
{
    RealmSet resolved = ToSet(_declaredRealm) & inherited;
    if (resolved == _effectiveRealm) return;

    RealmSet previous = _effectiveRealm;
    _effectiveRealm = resolved;
    Owner?.OnNodeRealmChanged(this, previous, resolved);

    for (int i = 0; i < _children.Count; i++)
        _children[i].PropagateRealm(resolved);
}
```

Maintained at **exactly four sites** — the two declaration setters, `AddChild` and `RemoveChild`. A fifth site is a bug.

**It must never be attached to `MarkWorldDirty` or any transform setter.** Those run every frame of every gizmo drag; realm changes at human rate.

**The trap, which is worth a comment in the source.** `SetOwner` early-outs when the owner is *unchanged* (`SceneNode.cs:345–346`), and an unchanged owner is precisely the **same-scene reparent** — the case where realm and state *do* need recomputing. Do not fold the realm walk into `SetOwner`. Hook it explicitly, and cover it with a 4×4 reparent matrix test asserting the effective byte on every descendant.

### R7 — In `AddChild`, the legality check is the FIRST statement of the method

Read the real method (`SceneNode.cs:247–284`). By the end of its detach block the child has been removed from `oldParent._children` (`:255`), its brush count unwound from the old ancestor chain (`:258`) and the old scene marked dirty (`:259`) — while `child.Parent` still points at the old parent (`Parent` is only repointed at `:263`). **A throw placed after that block leaves a node that is unreachable from the root, still claims a parent that does not list it, is still in `_nodesById`, and whose brushes have been unwound from a chain it has not left. There is no rollback path.**

So:

```csharp
public SceneNode AddChild(SceneNode child)
{
    // BEFORE ANY MUTATION. A half-applied reparent is worse than a refusal, and
    // the detach block below is already destructive by its second statement.
    if (child._subtreeBrushCount > 0 && _effectiveRealm != RealmSet.Shared)
        throw new InvalidOperationException(RealmMessages.BrushSubtreeUnderNonSharedParent(this, child));
    // There is deliberately NO dormant refusal here. See the R7 amendment below:
    // dropping a brush subtree into a Dormant folder is the supported gesture, and
    // the guarantee comes from MarkAdmissionChanged() further down, not from a throw.

    Scene? previousOwner = child.Owner;

    if (child.Parent is { } oldParent) { /* ...unchanged detach + brush-count unwind... */ }

    child.Parent = this;
    _children.Add(child);
    child.MarkWorldDirty();

    // REALM AND STATE BEFORE SetOwner. SetOwner raises NodeAdded, and NodeAdded
    // handlers (the BVH; NetId allocation) branch on both — exactly the same
    // reason MarkWorldDirty already precedes SetOwner on the line above.
    RealmSet beforeRealm = child._effectiveRealm;
    bool beforeLive = child._effectiveLive;
    child.PropagateRealm(_effectiveRealm);
    child.PropagateState(_effectiveLive);

    child.SetOwner(Owner);

    if (child._subtreeBrushCount > 0) { /* ...unchanged adjust + MarkStaticWorldDirty... */ }
    if (beforeLive != child._effectiveLive)
        Owner?.MarkAdmissionChanged();          // R13 — bumps _graphStructureVersion

    if (previousOwner is not null && ReferenceEquals(previousOwner, Owner))
        previousOwner.OnNodeSubtreeMoved(child);

    return child;
}
```

`RemoveChild` mirrors it: after `SetOwner(null)`, re-propagate the detached root against `RealmSet.Shared` / live, so a detached subtree resolves its own declarations and is never left holding a stale ancestor's answer.

**Amendment, 2026-08-21 — the dormant refusal is deleted, and must not be reinstated.** An earlier draft of R7 also threw `BrushSubtreeUnderDormantParent` when a brush-bearing subtree was added under a non-live parent. It contradicted R15's own refusal message, which tells the user in as many words that **`State = Dormant` is how you park a brush subtree**: setting `Dormant` *on* the subtree was blessed while dragging the same subtree *into* a `Dormant` folder threw. It also made the shipped project template's `Templates` folder unable to receive the commonest template there is, which is the beginner's first non-trivial gesture. And it closed nothing — `State` is a mutable admission filter whether it is declared on the node or inherited from an ancestor, which is exactly why R17 requires `Scene.MarkAdmissionChanged()` on **every** `State` transition over a brush-bearing subtree. The guarantee lives in that bump, which the method above already performs. **R7's realm refusal stays**, because it rests on an independent correctness argument — the imprint channel of R15/R16 — that dormancy does not share.

### R8 — A realm change on a node carrying a runnable script is a distinct, non-suppressible editor event

No other inherited property in this engine changes whether **code runs**. Reparenting already changes a subtree's world matrix, its `Owner`, and its static-world membership — adding a fourth inherited property is consistent with that model, not a new sin. But run location is genuinely new, and it gets a rule the others do not: a reparent that changes the effective realm of any node carrying a runnable `Script` raises a distinct, non-suppressible editor notice naming **every script whose run location changed**, and at runtime it is a structural event with explicit semantics (R16).

---

## 4. Enforcement — where the guarantee actually lives

`networking.md` §4.1's correction already established the shape of the problem: for a **remote** client the guarantee is structural (the data never crossed the wire, so there is nothing to enumerate), but for the **local** client on a listen host — which shares one `Scene` — the node is physically in the same graph, and four channels reach it today with no realm concept anywhere in the path. This section is where the local client is closed.

### R9 — One `lua_State`, one handle table and one `require` cache per net context

The visibility mask is a **readonly field on the VM**, not an ambient `NetContext`.

Without this the entire accessor design is decorative. `O8` caches required modules by `SceneNode.Id` and `O5` caches one handle per node so identity comparison works (`part.Parent == workspace` must be true). On a single shared VM, a server script stashes a hidden node's userdata in a module table and a client script reads it out — **the gate never runs, because the userdata already exists**. Separate states make the transfer *impossible* rather than *checked*, which is a different quality of guarantee.

It is also what Roblox does: a `ModuleScript` required from both sides yields separate instances with independent state, so this matches what a migrating developer already expects rather than surprising them.

```csharp
internal sealed class LuauHost
{
    // A readonly per-VM constant, NOT an ambient mutable: one lua_State per net
    // context, so there is no set/clear discipline to get wrong and the mask is a
    // JIT-visible constant at every gate.
    private readonly RealmSet _visibility;   // ServerVm: Server | Shared bits. ClientVm: Client bits.
    private readonly bool _isEditor;         // Editor bypasses — see R12.
}
```

### R10 — The gate covers ISSUE, RESOLVE **and COUNT**

Three surfaces, not one. The third is the one every version of this design missed.

1. **Issue.** `LuauHost.PushNode` is the only function that converts a `SceneNode` into a Luau value. It pushes `nil` when the node is not visible to the VM's mask. `O2`'s entire query surface — `FindFirstChild`, `GetChildren`, `GetDescendants`, `Parent`, `GetFullName`, `IsDescendantOf`, `WaitForChild` — inherits the gate without being patched individually.
2. **Resolve.** A `Guid` or `NodeRef` obtained in one context and handed to another must fail **at the point of use**, not merely at the point of handout. `Scene.TryFindById` stays realm-blind **by permanent contract** (the editor legitimately addresses every node, and that is the entire basis of `IEditorCommand` id-addressing); the realm-aware resolver is a separately-named member beside it.
3. **Count.** **Any scene-level index or count that spans hidden nodes is a bypass**, because an exact count is an existence oracle that survives every handle-boundary check: a client script reads it before and after and detects server-side spawns without ever holding a handle. Named, because they are real and they are public today:
   - **`Scene.NodeCount`** — `public int NodeCount => _nodesById.Count` (`Scene.cs:193`). Exact count of every node in the graph.
   - **`RenderView.TotalCount`** — `view.TotalCount = Bvh.MeshLeafCount` (`Scene.cs:321`, backed by `SceneBvh._meshLeafCount`, `SceneBvh.cs:142`). Exact count of every mesh leaf, hidden ones included.
   - **`O3`'s planned `Scene.GetTagged(tag)` reverse index and `ObserveTag(tag, onAdded, onRemoved)`**, which *replays already-tagged nodes before connecting*. That is a direct enumeration bypass with no handle involved anywhere, for a feature that is already designed rather than hypothetical.

   The rule generalises: **any new public API returning or counting `SceneNode`s is a bypass by default.** The realm-aware forms take a mask (`Scene.VisibleNodeCount(mask)`, `Scene.GetTagged(tag, mask)`, `ObserveTag(tag, mask, …)`), and the Luau binding layer must never name the blind form.

   Enforce it with a **reflection-based test in the test assembly**, or a Roslyn analyzer. Reflection is legal there — test assemblies are not AOT-published, and `Test/SpectraEngine.Editing.Tests/EditingAssemblyBoundaryTests.cs` already uses `System.Reflection` and `Assembly.GetReferencedAssemblies` (`:4`, `:26`, `:44`) as the existing precedent. A "reflection-free test over the public surface" is not implementable; enumerating every public API that returns a `SceneNode` needs reflection or an analyzer.

### R11 — A denial looks like ABSENCE, routed into the surface's existing absent path

Never a new error kind. A bespoke `RealmViolationException` is an existence oracle under `pcall` — the caller learns the node exists by the *shape* of the failure — and it makes hosting behave differently from joining, which is the "works solo, breaks in multiplayer" trap wearing the costume of its own fix.

| Surface | Denial |
| --- | --- |
| Child lookup (`FindFirstChild`, `.Name` index) | `nil` |
| `GetChildren` | omitted from the table |
| `GetDescendants` | subtree **pruned**, not per-node tested |
| Property read | falls to the generated `__index` switch's existing `default` — *"X is not a valid member of Y"* |
| `Guid` / `NodeRef` resolution | `false` / absent |
| Raycast, frustum query | no hit / not in results |
| Signal connection | the connection is **skipped**, not invoked with `nil` |
| Attribute bag holding a `NodeRef` to a hidden node | the **attribute** is omitted entirely — it reads as absent, not as an unresolvable Guid, because a remote client's schema would not contain it |

Debuggability comes from a **separate, rate-limited diagnostic** naming script, line, node and surface — never from the return value. It is dev-build only, and it is what stops "returns nil" from becoming an afternoon of confusion.

### R12 — Filtering happens at the query boundary; the BVH stays realm-blind

`SceneBvh.IsSpatial` does **not** gain a realm condition. One `Scene` instance serves both sides, and a server script must still be able to raycast server-realm content. The mask is threaded **through** BVH traversal and tested **at leaf-test time**, not applied afterwards: a post-hoc filter of a nearest-hit raycast returns the wrong nearest hit, because the walk early-outs on distance.

`IsSpatial` **does** gain the liveness condition, because a `Dormant` node is not in the world at all:

```csharp
private static bool IsSpatial(SceneNode node) =>
    node.IsLive && (node.MeshRenderer is not null || node.Brush is not null);
```

Signature policy, and the asymmetry is deliberate:

```csharp
// Defaulted: every existing editor/engine call site stays correct, unchanged.
public bool Raycast(in Ray3 ray, out SceneRaycastHit hit,
                    float maxDistance = float.PositiveInfinity,
                    RealmSet mask = RealmSet.Shared | RealmSet.Server | RealmSet.Client);
public void QueryFrustum(in Frustum frustum, List<SceneNode> results, RealmSet mask = /* everything */);

// NOT defaulted, deliberately: the render path is where the VISIBLE leak lives
// (a MeshRenderer under a Server node is drawn on the local client's screen
// today), so adding realm must break every render call site exactly once, and
// each one must be decided rather than defaulted.
public void BuildRenderView(Camera camera, RenderView view, RealmSet mask);
```

### R13 — The editor is a bypass, not a mask value, and `Inert` is why

The obvious implementation makes the editor just another mask (`Server | Client`) — and it is wrong, because `Inert` is the empty set and `0 & anything == 0`. An `Inert` subtree would be invisible and unpickable in the editor, which is exactly the subtree the user most needs to drag back out.

So the editor context short-circuits **true** before the AND:

```csharp
internal bool CanSee(SceneNode node) => _isEditor || node.IsVisibleTo(_visibility);
```

Consequences worth stating: edit mode and C# engine code see the whole graph, which is the same trust split the codebase already has. The **Command Bar** therefore carries an explicit, visible context selector (Editor / Server / Client), defaulting to Editor in edit mode and Server in Play — a text box that executes arbitrary Luau against the live scene is a hole otherwise. Console entity commands (`ent_dump`, `ent_fire`, `!picker`, trailing-`*`) refuse to name a node not visible to the **executing** context, on top of their existing `Cheat` flag.

### What a script actually observes

The point of R11 is that neither side needs to know the gate exists. Same map, two contexts:

```lua
-- Arena/Spawner   node Realm = "Server", declared on the spawner itself, which
-- sits RIGHT NEXT TO the enemies it spawns. No ServerScriptService, no
-- ServerStorage, and the tree is organised for the game.
local arena     = script.Parent                    -- Shared
local templates = script.Templates                 -- State = "Dormant": parked, never carved
local chest     = workspace.Vault.Chest            -- Shared node

print(chest.LootTable)                             --> "boss_tier3"   (Server-realm keyvalue)

for i = 1, 8 do
    local grunt = templates.Grunt:Clone()
    grunt.State  = "Active"                        -- joins the live world
    grunt.Parent = arena                           -- Shared: every client sees it
end
```

```lua
-- Hud/FollowCam   node Realm = "Client". This is a LocalScript, with no new type.
local arena = workspace.Arena

print(#arena:GetChildren())                        --> 8    (Spawner simply omitted)
print(arena:FindFirstChild("Spawner"))             --> nil
print(arena.Grunt1.Health)                         --> 42   (Shared keyvalue)
print(arena.Grunt1.LootTable)
--  error: LootTable is not a valid member of Grunt1
--  ...which is the generated __index switch's EXISTING default case, byte for
--  byte the same error a misspelled name produces. Not a new error kind.

-- Every one of these is the answer a REMOTE client gives, because for a remote
-- client the node is genuinely not in its pack. Hosting and joining agree.
```

Declarations are readable and writable from Luau as interned strings, so no enum userdata is needed and the printed value is the value:

```lua
print(spawner.Realm)            --> "Server"     -- declared HERE
print(spawner.EffectiveRealm)   --> "Server"     -- resolved
print(arena.Realm)              --> "Inherit"
print(arena.EffectiveRealm)     --> "Shared"     -- resolved from the root
print(templates.EffectiveState) --> "Dormant"

vfx.Parent = spawner            -- vfx.Realm == "Client", spawner is Server
print(vfx.EffectiveRealm)       --> "Inert"      -- Client ∩ Server = ∅ (R3)
print(vfx.Realm)                --> "Client"     -- the DECLARATION is untouched
vfx.Parent = arena              -- and moving it back restores it exactly (R5)
print(vfx.EffectiveRealm)       --> "Client"
```

Per-property realm carries the same vocabulary from both producers, into one byte-identical `.sentdef` record:

```csharp
[SpectraEntity("game_chest")]
public sealed partial class Chest : Entity
{
    [Keyvalue("isOpen"), Replicated]              public partial bool   IsOpen    { get; set; }
    [Keyvalue("lootTable", Realm = NodeRealm.Server)] public partial string LootTable { get; set; }
    [Keyvalue("openSound", Realm = NodeRealm.Client)] public partial string OpenSound { get; set; }
}
```

```lua
Entity.define("game_chest", {
    keyvalues = {
        IsOpen    = { type = "bool",   default = "0", replicated = true },
        LootTable = { type = "string", default = "",  realm = "server" },
        OpenSound = { type = "string", default = "",  realm = "client" },
    },
})
```

Two boundaries, stated up front because implementers will hit both. **A property's effective realm is the narrower of its own declaration and the node's** — declaring `shared` on a `Server` node does not widen it. And **per-property realm covers entity keyvalues and node attributes ONLY**: `LocalTransform` and its components, `Brush`, `MeshRenderer`, `Name` and `Parent` carry the node's realm and none of their own, because `networking.md` §3.4 pins built-ins to a hand-written 16-entry table deliberately outside the generator. **Say so in the attribute's own XML doc** — `LocalPosition` is the first place people will reach.

Enforcement of per-property realm is **not** at the C# property. C# is trusted engine code; the generator emits the realm into the `.sentdef` bits and into the per-context binding table, and the wire and Luau are where it bites.

### R14 — Do not optimise the gate

A proposed `Scene.RealmDeclaredNodeCount == 0` short-circuit — skip the check when the project has no realm-marked content — should be **deleted from the design**. `(effective & mask) != 0` is one load, one AND, one compare, against an `__index` metamethod plus an interned-name lookup plus a generated switch: it is unmeasurable. The short-circuit costs a field load through a `Scene` reference, and it makes a security check depend on a separately-maintained counter whose drift is a **total silent bypass**. Keep such a counter as a diagnostic and as a fast path for whole-subtree *enumeration* if it earns it; never guard an individual visibility test with it.

---

## 5. The static world rule

This is the sharpest rule in the document, and the one most likely to be softened by someone who has not read `Scene.cs`.

### R15 — A node admitted to the static-world carve must have `EffectiveRealm == Shared`

Refused at **three O(1) sites**:

1. **The `Brush` setter** — throw if the node's effective realm is not `Shared`.
2. **The `Realm` setter** — throw if the requested value is non-`Shared` and `_subtreeBrushCount > 0`. **The SUBTREE counter, never `Brush is not null`**: a group node carrying no brush of its own can still be the root of a subtree full of them (`SceneNode.cs:218–222` says exactly this about `LocalScale`, for exactly this reason).
3. **`AddChild`** — throw, as the method's first statement, if `child._subtreeBrushCount > 0` and the parent is not `Shared` (R7).

**Never "excluded with a warning."** Two independent reasons, and the second is the deciding one:

- *Ergonomics.* A map that compiles to different geometry depending on a property is a feature someone will use, and then a brush that visibly does nothing is indistinguishable from a CSG bug.
- *Correctness.* Excluding-with-a-warning makes **admission depend on a mutable property**, which is what opens R17's hole below. Hard refusal means a brush node is **always** `Shared`, so admission can never change by realm and that entire corruption class cannot exist. This is a stronger argument than the ergonomic one and it is why the rule is a refusal.

Why the rule exists at all: a server-only brush that carves a shared brush **publishes its exact shape as the hole it leaves** in geometry every client renders. That is a blueprint, not a leak. And two compiled worlds is worse — it doubles the background compile that the shared-`Scene` model exists to save, and it makes client prediction and server validation disagree about collision *by design*, because `IServerAuthority.Validate` (`networking.md` §4.4) tests movement against exactly that world. The player rubber-bands into a wall they cannot see.

**The payoffs are large enough to be worth naming.** Brush nodes are always `Shared`, so R12's mask branch is only ever reachable for `MeshRenderer` leaves; and the client and server packs contain **bit-identical** `CMSH`/`CBSP` sections, so compiled geometry needs no stripping and no divergence handling at all.

**When `P7` splits the brush counter, migrate all three checks to `IsStaticWorldBrush` / `SubtreeStaticWorldBrushCount` in the same commit.** Do not add a third subtree invariant — `O7` already names two independently-maintained subtree invariants of this shape as how silent corruption happens.

**And give the capability back in the message.** Refusing non-`Shared` brushes takes away a designer's only route to "park this region", and there is no enable flag on the brush path today. `State = Dormant` is that route, so the refusal text names it **first** and the `MeshRenderer` fallback second — `Dormant` is the answer to *"I want this parked"*, `MeshRenderer` is the answer to *"I want this drawn but not carved"*:

```csharp
internal static string BrushSubtree(SceneNode node, NodeRealm requested) =>
    $"'{node.Name}' has {node.SubtreeBrushCount} brush(es) in its subtree, so it is world " +
    $"geometry: brushes are carved into the one shared static world that every peer renders " +
    $"AND collides against, so they cannot be {requested}. To park this subtree — not carved, " +
    $"not drawn, not queried, ready to clone — set State = Dormant. For content that is drawn " +
    $"but never carved, use a MeshRenderer node, which lives outside CSG. For a server-side " +
    $"volume, use a trigger/query volume (P8), which never enters the carve.";
```

### R16 — Geometry secrecy is not offered, and the docs say so in one sentence

Anything compiled into the shared world is reconstructible by any client that renders it. The supported answers are: a separate map, a trigger/query volume, or a `Server`-realm `MeshRenderer` node outside CSG. **Do not add a flag that claims otherwise.**

### R17 — Any change to which nodes are admitted to the placement list must bump `Scene._graphStructureVersion`

This is the finding that makes the whole feature dangerous if implemented naively, and it is verified end to end.

- `_graphStructureVersion` is bumped **only** by `OnNodeAdded` (`Scene.cs:128`), `OnNodeRemoved` (`Scene.cs:137`) and `OnNodeSubtreeMoved` (`Scene.cs:159`). Its documented meaning, in the comment above them (`Scene.cs:120–125`), is *"the signal that the brush snapshot's TRAVERSAL ORDER may have changed"*.
- `MarkStaticWorldDirty()` sets **only** `_staticWorldVersion++` and `_snapshotForceFull = true` (`Scene.cs:724–728`). It does not touch the structure version.
- The compile launch reads `bool orderStable = carry is not null && _carryStructureVersion == _graphStructureVersion;` (`Scene.cs:996`) and hands the carry over as **trusted** when that holds.
- `CsgIncrementalCompiler.TryBuild` catches a pure count change — `if (n == 0 || n != prevPlacements.Count) return false;` (`CsgIncrementalCompiler.cs:99`) — but **not a count-preserving pair in one frame**: node A leaves the placement list because it went `Dormant` while node B gains a `Brush`. Count identical, slot mapping shifted, carry trusted.
- The order half of the contract is checked by `VerifyTrustedDiff`, which is `[Conditional("DEBUG")]` (`CsgIncrementalCompiler.cs:590–592`) and compiled out in Release **by design**. So a dev build throws and a shipping build silently compiles corrupt geometry.

`State` is an admission filter. A `Dormant` toggle on a brush-bearing subtree changes *which* nodes are admitted, i.e. the slot mapping, while leaving the structure version equal.

**So: every `State` transition on a brush-bearing subtree bumps `_graphStructureVersion`, and so does any future admission predicate.** `MarkStaticWorldDirty()` alone is not sufficient. Add a named entry point (`Scene.MarkAdmissionChanged()`) that bumps both, so the requirement is visible at the call site rather than remembered.

**Pin it with a test that runs in the Release configuration**, precisely because `VerifyTrustedDiff` does not. The oracle is the one the CSG suite already knows how to write: toggle `State` on one brush subtree while attaching a brush elsewhere in the same frame, then assert the incrementally compiled world is element-identical to a from-scratch compile of the same placements.

Realm cannot hit this hole, because R15 makes a brush node permanently `Shared` — which is the deciding technical argument for R15 being a refusal rather than an exclusion.

---

## 6. Scripts

### 6.1 The corrected position on run location

**Retire the slogan "where a script exists is where it runs."** It is false as stated, and the two designs that owned it contradicted each other outright — one ruling that a `Shared` runnable runs on the *server*, the other that it runs in *every* context. Both cannot ship, and the slogan is what let the contradiction survive unnoticed, because it sounds like an answer.

The honest rule, which is what goes in the onboarding doc and on the editor badge:

> **A script runs on the narrowest side its node exists on — and `Shared` means server.**

| `EffectiveRealm` | runnable (`IsModule == false`) | module (`IsModule == true`) |
| --- | --- | --- |
| `Shared` | runs on the **server**, once | requirable from **both** sides; **one instance per side** |
| `Server` | runs on the server | server-context `require` only |
| `Client` | runs on **every** client, one private instance each | client-context `require` only |
| `Inert` | **runs nowhere** | requirable nowhere |

### 6.2 What a `Shared` runnable script does, and why

It runs **on the server, once.** Not on both sides.

The rejected alternative — "runs in every context holding it" — was argued as the one genuinely useful case Roblox cannot express (shared cosmetic behaviour, correct on both sides, zero replication). It is rejected for two reasons:

1. **It is unsafe without R9 and merely surprising with it.** On a single shared VM, "runs in every context" is two runs sharing one global table — two executions of the same chunk mutating the same state, which is a class of bug with no good diagnostic. With one VM per context it becomes safe but still surprising: a script that mutates authoritative state now does so twice, once with authority and once without.
2. **The useful case already has a mechanism that is strictly better.** Shared logic is a **`Shared` module**, required from each side. That produces one instance per side with no shared state, which is what the "runs on both" author actually wanted, and it is what a Roblox `ModuleScript` in `ReplicatedStorage` already does — so the migrating developer already knows it.

There is therefore **no value meaning "runs on both"**, and `ScriptKind` loses its `Server` and `Client` members entirely:

```csharp
public sealed class Script
{
    public string? Source { get; init; }   // exactly one of Source or Path
    public string? Path   { get; init; }
    public bool Disabled  { get; set; }

    /// <summary>A module is required, not run. This is the ONLY surviving axis of
    /// the old ScriptKind. There is no per-script realm field: a script node is a
    /// leaf, and marking the leaf is the natural gesture.</summary>
    public bool IsModule { get; init; }
}
```

**`roblox-onboarding.md` `O8`'s "reserve `ScriptKind.Client`" is spent, not honoured — and that correction has LANDED.** `O8` now specifies the single `bool IsModule` axis and records the supersession in place, `roblox-to-spectra.md` and `data-model.md` agree, and `formats-and-pipeline.md` §2.6 no longer writes a `"kind"` string into `.smap`. No document in this set still specifies a three-member `ScriptKind`.

### 6.3 Roblox already collapsed `Script`/`LocalScript` — corroboration, not coincidence

Roblox shipped `Enum.RunContext { Legacy, Server, Client, Plugin }` in 2022 for exactly this reason, with the stated motivation *"consolidating Script and LocalScript behavior to simplify future script type development."* **Roblox agrees the two-script-types design is a mistake** and is trying to unwind it against fifteen years of content. We have no content, so we do it once, and we do all the axes at the same time instead of one — which is precisely where their attempt failed (§1).

**Qualify the parity claim honestly, or the first migrating developer reports it as a bug.** `Shared`-resolves-to-server matches `RunContext.Legacy` *under `Workspace`*, not `Legacy` generally: Roblox's docs say Legacy *"a) is a server-side script and b) only runs if it is in a server container, such as Workspace or ServerScriptService."* A `Legacy` script under `ReplicatedStorage` does not run **at all**, whereas a `Shared` node in Spectra runs it on the server. The parity claim is worth making — a `Script` under `Workspace` behaves identically — but it must be stated with that qualifier.

### 6.4 A realm change on a running script, at runtime

Because R8 makes this the one inherited property that changes whether code runs, both directions are specified rather than left to the scheduler:

- **Demotion** (the node leaves a VM's visibility) tears down that script's coroutines, its `O3` signal connections and its pending `task.delay` callbacks **in the losing VM**, and bumps its handles' generation so a retained handle errors with *"attempt to index a destroyed node"*. To that context it is indistinguishable from `Destroy`: `ChildRemoved`/`AncestryChanged` fire in that VM and nowhere else.
- **Promotion** (the node enters a VM's visibility) **starts** the script in the gaining VM, as an authored-node create.
- **Runtime realm writes are server-context only** and are flagged as structural replication events, like `AddBrush`/`RemoveBrush`.

`State = Dormant` gets the *same* answer, deliberately (§9, Q2): a dormant script is torn down, not suspended.

### 6.5 The editor must show the answer

Put the resolved run location on every script node as a badge — **`runs: Server · inherited`** — or this becomes the design's most-reported confusion. Node realm is one word; "what does that mean for this script" is the question people actually have, and the editor is the only place to answer it without a doc lookup.

---

## 7. The Roblox replacement table

Status is honest about the Spectra side. **planned** means designed and unbuilt.

### 7.1 Containers whose whole job was audience

| Roblox | Verified semantics | Spectra | Status |
| --- | --- | --- | --- |
| `Workspace` | *"contains all objects that make up a place's 3D world"*; clients render only this container; holds `Terrain` and `Camera` | **Deleted as a container.** `Scene.Root` *is* the world; Luau `workspace` aliases it so `workspace.Wall` ports character-for-character. **Rendering and carving are decided by `IsLive`, not by ancestry.** | root **exists**; alias planned (`O5`) |
| `ServerStorage` | *"objects only meant for server use"*; never replicated; **scripts do not run there**; cloned into `Workspace` at runtime | `Realm = Server` **+** `State = Dormant`. Note this was *two* properties fused into one folder — which is why Roblox's docs must state the script rule separately: the folder cannot express it. **One caveat R15 makes unavoidable:** a template containing *brushes* cannot be `Server` at all — it is `Shared` + `Dormant`, which is exactly what the shipped project template's `Templates` folder is. `Server` + `Dormant` is for parked data and non-brush content | **planned** |
| `ServerScriptService` | *"Scripts…only meant for server use"*; never replicated | `Realm = Server` **+** `State = Active`. Identical audience to `ServerStorage`, different liveness — exactly the distinction the two folder names encode and never explain | **planned** |
| `ReplicatedStorage` | *"available to both server and connected clients"*; client changes persist locally but do not replicate back | **Deleted.** `Shared` is the root default, so shared content needs no marking at all. The client-writes-stay-local rule survives as an **authority** rule (`networking.md` ruling 5), which is stronger than Roblox's "persists locally then gets overwritten" | **planned** |
| `ReplicatedFirst` | *"replicate to a client when it joins…only once"*; `RemoveDefaultLoadingScreen()` | `JoinPriority = First` on the node (usually with `Realm = Client`), sent ahead of the `Bulk` world-sync channel `networking.md` §3.2 already defines. `game:IsLoaded()` → a `WorldReady` signal; `RemoveDefaultLoadingScreen()` → `DismissBootScreen()` | **planned** |
| `ReplicatedScriptService` | **Removed from Roblox 2022-05-12 (v0.526.0)**; never had members; not creatable | **Nothing.** Its intent ("server and client scripts in one container") is the default here. Named only to close the question | n/a |

### 7.2 Containers whose job was per-player templating

Verified: *"the server copies the objects from the client containers in the edit data model to the corresponding location in the runtime data model inside the `Players` object."*

| Roblox | Verified semantics | Spectra | Status |
| --- | --- | --- | --- |
| `StarterGui` | copied to `Player.PlayerGui` on join **and respawn**; per-object `LayerCollector.ResetOnSpawn = false` opts out | Spawn rule `{ Template, Phase = OnCharacterSpawn, Destination = PlayerGui }`. `ResetOnSpawn` disappears — use `Phase = OnJoin` for a persistent HUD | **planned** |
| `StarterPack` | copied to `Player.Backpack` on join/spawn | Spawn rule → `PlayerInventory`. The *rule* ports; the destination does not exist (tools/inventory is its own unbuilt subsystem) | **planned; destination missing** |
| `StarterPlayer` | **not a container** — a property bag of 23 properties (`CharacterWalkSpeed`, `CameraMode`, `LoadCharacterAppearance`…) that also *parents* two script containers | Split, because it is two things: properties → player-defaults settings; containers → spawn rules. **There is no `StarterPlayer` node** | **planned** |
| `StarterPlayerScripts` | copied to `Player.PlayerScripts` **once per join** | Spawn rule, `Phase = OnJoin` | **planned** |
| `StarterCharacterScripts` | copied into `Player.Character` **on every spawn** | Spawn rule, `Phase = OnCharacterSpawn`, `Destination = Character` | **planned** |
| `StarterCharacter` (model) | a `Model` so named replaces the avatar | `PlayerDefaults.CharacterTemplate` — a prefab reference; interim, a `NodeRef` to a `Dormant` subtree | **planned** |

```jsonc
// game.spectraproj — the four Starter* containers, as data.
"playerSpawnRules": [
  { "template": "Interface/Hud",              "phase": "OnJoin",           "destination": "PlayerGui" },
  { "template": "Interface/RespawnPanel",     "phase": "OnCharacterSpawn", "destination": "PlayerGui" },
  { "template": "ClientLogic/Boot",           "phase": "OnJoin",           "destination": "PlayerScripts" },
  { "template": "Templates/CharacterScripts", "phase": "OnCharacterSpawn", "destination": "Character" }
]
```

Those paths name folders from the shipped project template ("The bare-root objection", above). **They are the one place in the whole design where a folder name becomes load-bearing data** — in the user's own project file, never in engine code, so the hard rule is intact — and they are therefore the one place a rename or a delete can break something silently. The editor owes rename-repair (§7.5 item 12) and the cook owes a loud unresolved-spawn-rule diagnostic. Do not add a second reference of this shape.

**The `OnJoin` vs `OnCharacterSpawn` distinction is the single most valuable thing this replacement buys.** In Roblox it is unwritten in the tree: you must simply know that `StarterPlayerScripts` copies once and `StarterCharacterScripts` copies every death. Here it is a word on the rule.

### 7.3 Services that were never containers

| Roblox | Verified semantics | Spectra | Status |
| --- | --- | --- | --- |
| `Lighting` | global lighting properties **and** a container for `Sky`/`Atmosphere`/post-effects | **Three things, three homes.** Global properties → a typed `Scene.Environment` settings struct; sky/atmosphere → asset references on it; post-effects → an ordered chain owned by the render arc. **Lights are spatial `SceneNode`s with a `Light` payload.** This overturned an earlier `roblox-to-spectra.md` row ("a `Scene.Lighting` node with attributes") — a node implies a transform, a parent, a realm and a subtree brush count, none of which mean anything for fog density. **That correction has landed**; the row there is marked *Corrected* and agrees with this one | **planned** |
| `SoundService` | global audio properties **and** a container where a parented `Sound` plays non-spatially | Properties → `Scene.Audio` settings; `Audio.Play2D(clip)` for non-spatial; spatial sounds are `Sound` payloads on world nodes. A sound is never parented to a service | **planned** (audio is a stub today) |
| `Players` | creates a `Player` per client; parents `PlayerGui`/`Backpack`/`PlayerScripts`/`Character` | A **service and index**, not a tree container — a player has no transform, no brush and no realm of its own. Its *contents* stay nodes: `player.Gui`, `player.Scripts`, `player.Character` are real `SceneNode`s in a per-player subtree, `Realm = Client` plus a per-client replication filter. This is the same split Roblox already makes (`Player` in `Players`, `Character` in `Workspace`), made explicit | **planned** |
| `CollectionService` | tags | `O3`'s per-node tags + scene reverse index + `ObserveTag` — **with R10's mask parameter, non-negotiably** | **planned** |
| `RunService` | frame phases | `O8`'s three pump points | **planned** |
| `Teams` | `Team` objects; `TeamColor`; `GetTeams()` | **Nothing in v1 — deliberate difference.** `Teams` exists largely to drive `TeamColor` on the default leaderboard, which does not exist here, and `BrickColor` is already rejected. A team is a string attribute or a `NodeRef` to a team entity. Cheap to add later; expensive to ship an empty one | **deliberate difference** |
| `Debris`, `Terrain`, `CoreGui`, `TestService`, `Chat` | — | Not provided. `Terrain` is a deliberate difference (brushes are the world); `Debris` is `task.delay` + `Destroy`; `CoreGui` has no analogue in an engine with no platform UI | **deliberate difference** |

### 7.4 Where the replacement is genuinely harder

Three places, stated rather than buried:

1. **`StarterPack`'s destination does not exist.** Tools and inventory are an unbuilt subsystem. The spawn rule ports; there is nothing to spawn into.
2. **A parked template is a `Dormant` subtree until `P10`, not a prefab.** That is a real ergonomic gap: a `Dormant` subtree is cloned and re-parented by hand, has no override mechanism, and its contents are written into the map rather than expanded from a shared definition. It is the honest answer for the next several milestones and prefabs are the eventual optimisation of it.
3. **Porting an existing Roblox place is still mostly not about containers.** The container mapping is the easy third. The hard two thirds are that ported gameplay code references physics, `Humanoid`, `TweenService`, `DataStoreService` and per-part colour, none of which exist. A migration guide that leads with a slick container table and buries that is dishonest in exactly the way this repo's docs have so far refused to be.

**Porting position: documented rewrite, plus type-level deprecation in the generated `spectra.d.luau`. Explicitly no runtime compat shim, and no automated codemod.**

- **No runtime shim, and this one is refused on principle rather than on cost.** The moment `game.ServerStorage` resolves to a real node, the container model is back — *without its rules*, since nothing then stops a brush under it from carving. You would have both models simultaneously, which is worse than either, and every future Spectra tutorial would have two right answers.
- **No codemod.** It cannot be made sound for Luau: `game[name]`, `FindFirstChild("ServerStorage")`, string-built paths and `require` chains defeat regex and a real parser alike without full type information. A codemod that fixes 80% and silently misses 20% is worse than none, because the misses fail in production rather than at the keyboard.
- **Deliver the codemod's value as diagnostics.** Declare `game.ServerStorage`, `game.ReplicatedStorage`, `game.StarterGui` and friends as deprecated symbols in `spectra.d.luau` whose type-level message names the replacement. Under `--!strict` with `luau-analyze` in CI, the 20% a codemod would miss fails at the type checker instead.
- **`game:GetService(name)` throws, naming the replacement — it does not return `nil`.** The universal idiom is `local RS = game:GetService("ReplicatedStorage")` on one line and `RS.Modules.Foo` on a later one, so a `nil` produces *"attempt to index nil value"* with a traceback pointing at the **wrong line** while the helpful log scrolls past. A thrown error lands on the right line with the right message.

```lua
-- Roblox
local enemy = game.ServerStorage.Enemy:Clone()
enemy.Parent = workspace

-- Spectra today: a Dormant template, sitting next to the spawner that uses it
local enemy = script.Parent.Templates.Enemy:Clone()   -- Templates.State == "Dormant"
enemy.State  = "Active"
enemy.Parent = workspace.Arena

-- Spectra after P10
local enemy = Assets.Prefab("Enemies/Grunt"):Instantiate(workspace.Arena)
```

### 7.5 What the editor must do — acceptance criteria, not polish

Roblox's containers have three virtues a flag does not: they are **discoverable** (you open Explorer and learn the model by reading folder names), **zero-configuration** (dragging *is* the config), and **glanceable** (audience is visible in the tree without clicking anything). **An invisible flag is worse in practice than a visible folder.** If the first editor build ships without these, this design is worse than Roblox's regardless of being better in theory.

1. **A Realm column in the Explorer, always visible.** Effective value on every row; inherited values dimmed, explicit declarations solid. This recovers glanceability *and* shows something Roblox's tree cannot — **where the exception was declared**.
2. **A per-row gutter badge: glyph AND colour, never colour alone.** The glyphs are `sh` / `sv` / `cl` — deliberately the Garry's Mod / Source prefixes the naming decision was made on (§9 Q8), so the badge teaches the same vocabulary as the console and these documents, and it survives colourblindness and a greyscale screenshot. `Shared` neutral, `Server` one hue, `Client` another; **`Dormant` is encoded by FILL, not by hue** — badge outlined instead of filled, row text desaturated — so one glyph carries both axes without a second column; and **`Inert` is unmistakable** (error-coloured badge plus a struck-through row, and a persistent inspector banner naming the conflicting ancestor with a click-through that selects it), because it is always an accident or a work-in-progress and the recovery must be one click rather than a doc lookup.
3. **`Dormant` subtrees render ghosted in the viewport.** A dormant brush that is not carving is otherwise indistinguishable from a CSG bug — and this repo has already shipped one symptom mistaken for a brush bug (the self-test jitter, commit `d4701d6`). Non-negotiable.
4. **A `View as: Editor | Server | Client` lens** that feeds the **same** `RealmSet` mask to `BuildRenderView`, `Scene.Raycast` and the box-select query that the runtime uses — so it **cannot drift from what the runtime does**. This is strictly better than Roblox, which offers no such view, and it is nearly free because the byte is already cached.
5. **Picking follows render visibility, always.** In any view context, a node that is not drawn is not pickable and not box-selectable, and switching to a narrower view **deselects** what the new view hides — the same auto-deselect `RemoveChild` already performs. Without this a gizmo can drag something the user cannot see, which is worse than having no lens.
6. **Settable in one gesture from the tree** — right-click → Realm → …, with **illegal options disabled and the refusal text as the tooltip** (a brush subtree shows "Server" disabled carrying R15's message). Disabled-with-a-reason beats an exception dialog. If it needs a Properties-panel round trip, people stop marking things and the model rots.
7. **New-node defaults declare explicitly** ("template" is reserved here for the project template above). New Script → `Server`. New UI root → `Client`. New folder → `Inherit`. This is how the zero-configuration property is recovered.
8. **Realm and state edits are `IEditorCommand`s.** `SetRealmCommand` is addressed by `SceneNode.Id`, records the absolute **before** and **after declared** value of every node it changed (never effective values — those are derived), and coalesces into one transaction per gesture, exactly like every other editor command.
9. **The new-project template ships a pre-declared tree, and this is an acceptance criterion.** This is the discoverability property nobody else named, and the answer to the strongest objection this design has faced: **Roblox's containers are discoverable because they exist in an empty project** — the model teaches itself on first open, while a fresh Spectra project would be a bare root that teaches nothing, and no badge on a node nobody created can fix that. The full design, the tree, the ship mechanism and the reasoning are in **"The bare-root objection, and what a new project actually ships"** near the top of this document; the acceptance criterion here is that the tree ships with the first editor build — `Baseplate` (no declaration), `ServerLogic` (Server), `ClientLogic` (Client), `Interface` (Client), `Templates` (Dormant), `Modules` (no declaration) — with the realms already declared and the badges already visible, an `Empty` template beside it in the same dialog, and **no engine code anywhere that knows any of those names**.
10. **A drag shows its consequence BEFORE the drop, and an illegal drop is refused before the mouse is released.** While hovering a target whose effective realm or state differs from the dragged selection's, the drag ghost carries the resulting badge (`Shared → Server`), and an empty intersection reads `INERT` in the error colour; a mixed multi-selection shows per-source counts rather than one badge. An R15-illegal drop (a brush subtree onto a non-`Shared` target) shows a forbidden-drop cursor with R15's refusal text in the drag tooltip — **never an exception dialog after the fact**. Without this, drag-into-folder is precisely the silent semantic change this design exists to eliminate. On drop: affected rows flash their new badge, a one-line status summary states what changed ("6 nodes narrowed to Server; 2 became Inert"), R8's script notice fires separately, and the undo entry is labelled with the semantics rather than the mechanics.
11. **Reparent-by-drag preserves the world transform**, by recomputing the local transform against the new parent. This is not polish: `AddChild` does no transform compensation and `WorldMatrix` is `local * Parent.WorldMatrix` (`SceneNode.cs:225–237`), so dragging a part into any folder with a non-identity transform teleports it. Roblox never does this because its `CFrame` is world-space, so a migrating developer has no defence against it. Template folders are authored at identity **and** the editor compensates; without both, "dragging into a folder just works exactly as in Roblox" is false in the general case.
12. **Renaming or deleting a node that a `playerSpawnRules` entry names by path surfaces the affected rules in the same confirmation**, offering to update or drop them (§7.2). That is the only data anywhere that references a template folder by name, and it is in the user's project file rather than in engine code.
13. **Filter and group by realm — the view Roblox structurally cannot offer.** Explorer filter chips (`Shared` / `Server` / `Client` / `Dormant` / `Inert`) that filter the tree while keeping ancestor rows as dimmed context, and an alternate group-by-realm view. **Group by DECLARATION, listing the subtree roots where each declaration was made** — never by effective value listing every node, which puts the whole project under `Shared` and is worthless at any real size. Grouping by declaration answers the question people actually have: *where are the exceptions in this project*. Drag is disabled inside the grouped view and group headers are not drop targets, because a drop into a virtual group has no defined parent and inventing one would be a silent reparent.

### 7.6 The honest onboarding claim

Do not claim "smaller". The honest count for this model is: realm as an inherited enum, the narrowing rule, what a reparent does, `Shared`-scripts-run-on-server, per-property realm and where it does *not* apply, brushes-must-be-`Shared`-and-why, modules instance per side, `Dormant`, spawn rules, `JoinPriority`. That is about ten rules, **three of which are exceptions** — and exceptions are precisely what made Roblox's model expensive. Meanwhile a Roblox developer does not *experience* containers as thirty facts; they experience them as "server stuff goes in ServerScriptService" and absorb the rest over years.

**The defensible claim is orthogonality, not size: you can finally put the spawner next to the enemies it spawns.** Claim that, and stop there.

---

## 8. What is guaranteed, and what is not

**Guaranteed.**

- **Server-realm state never crosses the gameplay wire.** No NetId is allocated, no baseline is written, no delta is packed.
- **Client-context code cannot reach a server-realm node in any topology** — including the shared-`Scene` local client on a listen host, which is the case `networking.md` §4.1 originally got wrong and corrected. R9 (separate VMs) makes cross-context handle transfer impossible rather than checked; R10 closes issue, resolve and count; R11 makes every denial look like absence, which is what a remote client already sees.
- **`Dormant` and non-`Shared` content contributes nothing to the compiled world.** `CsgWorld.Build` is a pure function of the placement list, and a node that is never admitted contributes nothing to any carve, weld, BSP or mesh. R15 makes brush nodes permanently `Shared`, so the imprint channel is closed by construction rather than by a filter.
- **The client and server packs contain bit-identical `CMSH`/`CBSP` sections**, and NetId numbering is identical across complementary strips.

**Not guaranteed in v1 — say this plainly, and say it before anyone puts a secret in a server script.**

- **Secrecy of server content ON DISK.** Server node records ship in the client pack, exactly as `networking.md` `N15` already accepts for server script source: *"Server script source ships in the client's pack in v1 (`SCPT`/`LUAS` are one section): nothing in a server script is secret, and that must be documented before anyone puts a secret in one."* The same is true of server node records, their names, their transforms and their attributes. **The tree does not hide them; it only stops your own client code from reading them.**
- **Defence against a modified client on the player's own machine.** The accessor gate defends against a developer's own client code and against a remote player reading data that was never sent. It is not a defence against a patched binary. Only the cooker's client-target strip is, and only for content it removed.

**The route to on-disk secrecy, if it is ever wanted**, is a cook-target strip: `--target client` drops every `Server`-realm node **whole** — its `NODE` record, `ENTT`/`ECON` records, `SCPT`/`LUAS`/`LUAB` blobs and attributes — which retires `formats-and-pipeline.md` §7's open question about stripping server script source (the source is not stripped; the node carrying it is absent). **And `STRT` must be REBUILT from the surviving records, never copied**, with a `scook verify --target client` pass asserting every string-table entry is referenced by a surviving record. Otherwise a stripped node's name — `SecretBossSpawner` — ships to every player in the string blob. Copying the string table is the shorter implementation and the leak nobody would check.

A client self-reporting its strip mask (`sv_require_stripped_client`) is hygiene, never security.

---

## 9. Open questions

Eight edges the adversarial pass could not close from source. A position is taken where one is defensible; where it is not, the question and the tradeoff are stated rather than dropped. **Q8 is now closed by the user (2026-08-21) and Q1 by argument; both are kept in place, numbered, so cross-references keep resolving.**

**Q1 — What does a `Shared` runnable script do? → CLOSED. It runs on the server, once.** See §6.2 for the full argument. The alternative ("runs in every context") is unsafe without R9 and merely surprising with it, and the useful case it was reaching for is better served by a `Shared` **module**. This had to be answered before `O8` ships the `Script` payload, because it decides whether `ScriptKind` has two members or three. It has two.

**Q2 — What happens to a running script when its node's realm or state changes at runtime? → POSITION: tear down on loss, start on gain, and `Dormant` gets the same answer as demotion.** §6.4 specifies handles; the script *instance* follows the same rule: coroutines stopped, `O3` connections disconnected, pending `task.delay` callbacks cancelled in the losing VM. **`Dormant` does not suspend — it tears down.** The alternative (suspend coroutines and timers, resume on reactivation) is more intuitive for "inert" and matches Roblox's storage behaviour, but a script that goes dormant mid-`task.wait` and resumes later with a stale `dt` is a subtle failure with no good diagnostic, and it forces `O8`'s scheduler to carry a second, suspended run queue. The cost of the position, stated: reactivating a template restarts its scripts from the top, so template scripts must be written to be re-entrant. That is the same contract `Clone` already implies.

**Q3 — What does the map loader do with an illegal combination on disk? → POSITION: a per-node load defect, never a mid-load throw.** Setter-based refusal is right for authoring and **wrong for deserialization**. A `.smap` or `.scmap` carrying a brush on a non-`Shared` node, or a realm string the reader does not know, is reported as a loud, named, per-node load **defect** that skips or coerces the offending payload and leaves the rest of the map loadable. A mid-load exception leaves a half-built scene in the editor, which is the one failure mode a loader must not have. The loader therefore constructs nodes through an internal path that bypasses the setters and validates afterwards. `"realm"` and `"state"` are reserved keys and an unrecognised value is a reader error, never a silent fall-through to `Shared` (§2.5).

**Q4 — Prefab instantiation into a narrower destination: refuse, or clamp? → POSITION: refuse, with the list of conflicting descendants.** The distinction that resolves the apparent contradiction with R5 is **reversibility**: a reparent is a gesture the user can undo, and clamping leaves every declaration intact so moving the subtree back restores it exactly. An instantiation **authors a new declaration set that bakes into the saved map**; there is no "move it back". So instantiation validates the prefab's internal declarations against the destination's effective realm and refuses, naming every conflicting descendant. It must never coerce them. *Related and still open:* `ROADMAP.md` §11.8's prefab-internal `targetname` scoping is the same shape of question, and answering them separately risks two different answers for one concept.

**Q5 — How many `lua_State`s does the editor process actually hold? → PARTIAL POSITION; the cost is real and uncosted.** Count them honestly against what is already planned: `O6` gives the Command Bar an editor-owned state, `O9` gives Play a fresh state torn down on Stop, `O8` gives every script its own thread with `luaL_sandboxthread`, and R9 now requires one state per net context. **Position: the Command Bar uses the editor state rather than a fourth, and Play creates one server state and one client state** — so edit mode holds one, Play holds three, plus a thread per script. **What is not settled is the memory cost**, because each state carries an independent `require` cache and therefore an independent instance of every `Shared` module's state. Nobody has measured it. It is not a reason to weaken R9 — a shared VM makes the gate decorative — but it must be measured before the listen-host topology is called shippable.

**Q6 — Does `Client` earn its place in v1? → POSITION: keep it.** The cut is genuinely attractive: `Client` costs a third lattice value, a server-side gate, a second cook strip target and a set of nonsense combinations that each need a bespoke refusal message. But the strongest argument for cutting it was that the `Server`-versus-`Client` conflict case is where the clamp bug lived — and **R3 fixes that by naming `Inert`**, so the argument no longer holds. Cutting `Client` would leave a hole in the replacement table exactly where Roblox developers live (`LocalScript`, `StarterPlayerScripts`, client HUD authored *in the map*), and the fallback — "create client-local content in the client VM with no declared realm" — has no answer for authored content. **The fallback if implementation cost bites:** reserve the value in the enum and in the `.smap` vocabulary, ship `Shared`/`Server` only, and add the client strip later. Reserving costs nothing; renaming after content exists costs everything.

**Q7 — Per-property realm versus the signal surface. → POSITION: follows from R11 mechanically.** A server-only property is **absent from the client's binding table**, so in client context `GetPropertyChangedSignal("LootTable")` takes the same path as a misspelled name — the generated switch's existing `default`, *"LootTable is not a valid member of Chest"* — and the `Changed` signal **does not fire for it in that context at all**. Firing `Changed` with a name the client cannot resolve is an existence oracle; firing it with nothing is a silent hole in a documented API; **not firing it is neither**, because in a client context that property genuinely does not exist. `O3` ships both signals, so this must be settled in the same change.

**Q8 — The naming lock. → CLOSED by the user on 2026-08-21. The axis is `Realm`.** The choice was between *realm* and *scope*, and the user took *realm* for three stated reasons. (1) **Precedent:** *realm* is Garry's Mod / Source Lua vocabulary for exactly this axis — the `sv_`/`cl_`/`sh_` split — so it arrives already meaning the right thing to the Source-lineage developer, and the editor badge vocabulary (§7.5 item 2) can teach the same three letters the console and these documents use. (2) **The collision:** `networking.md` already uses *scope* for interest management and "replication scope" in adjacent paragraphs, so shipping both would put two meanings of one word in the two documents that must be read together — and *scope* was already overloaded three ways (interest management, milestone scope, lexical scope in a document about scripting). (3) **Irreversibility:** the word goes into every `.smap` node record, every `.scmap` `NODE` payload-flag bit and every `.sentdef` `Flags` bit pair, so it could not be changed after the first map was saved; it was decided before that happened. The counter-argument — *realm* is a new word to learn, and *scope* was what the original ask said — was heard and overruled.

**Binding consequences of the lock**, so it does not have to be re-argued: the property is `SceneNode.Realm`, the enum is `NodeRealm`, the resolved set is `RealmSet`, the serialized key is `"realm"`, the Luau property is `.Realm`/`.EffectiveRealm`, and the diagnostic vocabulary is `Shared`/`Server`/`Client`/`Inert`. **No engine identifier, message, key or document may use *scope* for node audience** (this document's own `BrushSubtreeUnderScopedParent`, `ScopeViolationException` and `Scene.ScopedNodeCount` were renamed in the same change), **and none may use *realm* for interest management** — `networking.md` §4.3 keeps *scope* and *relevancy* for that, permanently.

### Also carried forward, from the migration pass

Smaller, but not dropped:

- **Can a server-context script create a `Client` node at runtime**, meaning "create this on every client"? A coherent primitive with no Roblox analogue (Roblox forces a `RemoteEvent`), and also a way to accidentally spawn N copies. The v1 rule refuses it — a node's realm is fixed at creation and may only be created in a context that would hold it — but the generalisation needs a decision before the replication vocabulary is frozen.
- **What is the relationship between `State = Dormant` and `P7`'s dynamic-part split?** Both express "this exists but is not world geometry". If a `Dormant` brush subtree goes `Active` during Play, does it join the carve (a structural edit, full recompile) or become a dynamic `MeshRenderer`? A spawner activating templates every few seconds would full-recompile the world on each one. This is the same hazard `P7` already names and must be answered together with it.
- **Where do shared Luau modules live by convention** now that `ReplicatedStorage` is gone? **ANSWERED: the `Modules` row of the shipped project template** — `Shared` by inheritance, declaring nothing (see "The bare-root objection" above, and §7.5 item 9). The mechanism was never in doubt (`Shared` is the default; `require` is by node id); what was missing was a convention, and a convention is a template row rather than an engine feature. **One edge of it is genuinely still open: is a `Dormant` module requirable?** §6.4 and Q2 rule that a dormant script is torn down rather than suspended, while §6.1's module table is indexed by realm and says nothing about state. If dormant modules are *not* requirable, a `Templates` subtree cannot carry the module its own scripts require, and the template's documentation must say "reach out to `Modules`" instead.
- **Do `StarterPlayer`'s 23 character properties become a settings struct or entity keyvalues on the character prefab?** Keyvalues are consistent with the entity arc and give the property panel for free; a settings struct is simpler and replicates through the built-in table. Small, but it blocks the character work.
- **Does per-property realm need a `Client` value at all**, or only `Shared`/`Server`? A client-only keyvalue on a replicated entity is arguably a local cache wearing the realm word. Dropping it would free a bit and remove a combination nobody has asked for.

---

## 10. Test pins

None of this is real until these exist.

| # | Pin |
| --- | --- |
| 1 | A `Server`-realm node's `Guid` resolves through `Scene.TryFindById` and **fails** through the realm-aware resolver with a client mask. |
| 2 | `GetChildren` in a client VM over a mixed parent returns exactly the `Shared` children, **in order**. |
| 3 | All three R15 refusals throw, with the named messages. |
| 4 | A 4×4 reparent matrix (`Shared`/`Server`/`Client`/`Inert` parent × the same declared child) asserting the effective byte on **every descendant**, including the same-scene reparent that R6's `SetOwner` trap covers. |
| 5 | **Release-configuration** admission test: toggle `State` on one brush subtree while attaching a brush elsewhere in the same frame; assert the incrementally compiled world is element-identical to a from-scratch compile. This is R17, and it must not be a `DEBUG`-only assertion, because `VerifyTrustedDiff` already is. |
| 6 | Compile-equivalence oracle: a map with N `Server`-realm non-brush nodes compiles to a `CsgWorld` element-identical to the same map with those nodes deleted. |
| 7 | A `Dormant` brush subtree contributes **zero** placements and **zero** BVH leaves. |
| 8 | A `client`-target and a `server`-target cook of one `.smap` produce **identical NetId numbering**, and bit-identical `CMSH`/`CBSP`. |
| 9 | `scook verify --target client` asserts every `STRT` entry is referenced by a surviving record. |
| 10 | A reflection-based public-surface test (the `EditingAssemblyBoundaryTests` precedent) asserting every public API returning or counting `SceneNode`s is either mask-taking or on an explicit allow-list — and that the generated Luau bindings never name a blind form. |
| 11 | **The master pin:** `N14`'s three-context loopback rig asserts the **local** client's observation of a mixed tree is identical, member for member, to the **remote** client's. Everything else on this list is a shortcut to this one. |
| 12 | **The hard rule's pin.** A reflection/source scan over the engine assemblies (`SpectraEngine.Core`, `SpectraEngine.Editing`, and the cooking library when it exists) asserts that **no template folder name appears as a string literal anywhere** — `ServerLogic`, `ClientLogic`, `Interface`, `Templates`, `Modules`, `Baseplate` — and that no public member resolves a node by a well-known name. Same precedent and same assembly as pin 10 (`EditingAssemblyBoundaryTests`). The rule is only as real as the thing that catches its first violation, and the container zoo was documented too. |
| 13 | **The template is a starting position, not a parent.** Instantiating the `Game` template and hand-authoring the same project produce **byte-identical** files modulo the freshly generated node Guids and the project name and id — which simultaneously pins that instantiation re-GUIDs every node, that `template.json` does not survive, and that no provenance field is written anywhere. Byte identity is already this repo's preferred oracle (`P2`'s save→load→save pin). |
| 14 | **The R7 amendment.** `AddChild` of a brush-bearing subtree into a `Dormant` parent **succeeds**, the subtree contributes zero placements and zero BVH leaves (pin 7's oracle), and `_graphStructureVersion` was bumped by `MarkAdmissionChanged()` — asserted in the **Release** configuration, for pin 5's reason. |
