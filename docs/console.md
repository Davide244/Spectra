# Spectra — The Console

> A typed ConVar/ConCommand control surface with binds, config files and discovery, stolen from Source; plus a live Luau line, stolen from Roblox. One registry, one command buffer, one bind table.
> Companion to [`ROADMAP.md`](../ROADMAP.md) (arcs `F`/`E`/`P`/`S`/`R`/`H`), [`docs/roblox-onboarding.md`](roblox-onboarding.md) (`O0`–`O9`) and [`docs/formats-and-pipeline.md`](formats-and-pipeline.md) (`D0`–`D22`). This document owns one arc — **`C*`**, for console — and references milestones in the others by id rather than restating them.
> Sizes are relative (**S / M / L**), never calendar. Nothing here was built or run — another workflow holds the tree — so every claim about the tree was read out of source on **2026-08-21**, every claim about a .NET library was checked against its source or documentation on the same date and is marked, and everything speculative is labelled as such in §8.

---

## 1. What is being stolen, and why it beats both alternatives

Source's console is not a log window; it is the **control surface for the whole engine**. Every tunable is a typed ConVar with a default, a clamp range, a help string and flags that decide whether it persists, whether it is a cheat, whether it survives into a shipped build. Every verb is a ConCommand with its own tab completion. `find shadow` lists everything with "shadow" in its name or help; `help r_shadowmapsize` prints the type, the range, the default and the sentence explaining it; `bind f3 "toggle r_drawaabbs"` turns a key into that verb; `exec repro.cfg` replays a whole engine configuration; and the archived cvars write themselves back so the settings a player changed are still there next launch. Roblox's Studio has an Output window plus an edit-mode-only Luau command bar and **none** of the rest — no typed settings, no discovery, no binds, no config files, no way to write down "here is the exact engine state that reproduces my bug" and hand it to someone. But the command bar has the one thing Source does not: you can type a line of the game's own scripting language and it runs *right now* against the live scene. This design takes both, and the seam between them is one character. A bare line is a console command; a line beginning with `>` is Luau. That is the whole disambiguation rule, it never guesses, and it means the engine gets Source's control surface and Roblox's tightest iteration loop out of one input box, one history, one output stream, and one registry that the editor's settings panel is merely a *consumer* of.

**And it retires a wart.** `Engine.cs:234–243` today hardcodes `WasKeyPressed(Key.F1)` through `F5` XOR-ing a private `DebugVisualization _debugFlags` field, and `F6` calling `_renderer.NextPipeline()`. There is no action map and no binding table anywhere in the engine. After `C3` that block is **deleted**, replaced by five bool cvars, an `r_pipeline` cvar, and six default binds shipped as content. That deletion is the honest proof the design works.

---

## 2. The core

### 2.1 Where it lives, and the one naming trap

`SpectraEngine.Core/ConsoleSystem/`, namespace `SpectraEngine.Core.ConsoleSystem`, static facade type `SpectraConsole`.

**In Core, not in `SpectraEngine.Editing`,** because the shipped game is the console's second and equally important consumer: it is the settings surface for a data-driven runtime (§5.3), the support surface ("paste the output of `differences`"), and the modder surface. `SpectraEngine.Editing` is by design absent from a shipped binary.

**Not namespace `...Core.Console`.** A namespace named `Console` shadows `System.Console` for every file inside it, which would make the one place that genuinely needs `System.Console.ReadLine` (the stdin front end, `C1`) fight its own namespace. Cheap to avoid now, annoying forever otherwise. Likewise the facade type is `SpectraConsole`, not `ConsoleSystem` — a type with the same name as its enclosing namespace is a lifetime of ambiguity errors.

### 2.2 Registration: attributes, a source generator, and the cross-assembly problem

The AOT rule bans reflection, so nothing may scan assemblies for `[ConVar]` at runtime. The house pattern is attributes plus a compile-time source generator, exactly as `P5` establishes for entities and `O5` for Luau bindings.

**The problem a first design misses: a source generator only ever sees its own compilation.** A generator living in Core cannot collect `[ConVar]` declarations from `SpectraEngine.Executable`, from `SpectraEngine.Editing`, or from a game assembly in engine-SDK mode (`D21`). Those are separate compilations that Core does not reference and cannot reference.

**The answer: the generator runs in every assembly that references Core, and each emits its own registrar at a fixed name.**

```csharp
// Generated once per assembly, at <RootNamespace>.Generated.SpectraConsoleModule
namespace SpectraEngine.Core.Generated;

public static class SpectraConsoleModule
{
    private static int _registered;

    /// <summary>Registers this assembly's ConVars and ConCommands. Idempotent.</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0) return;
        ConVarRegistry.Add(Graphics.RenderCVars.__cv_r_drawworld);
        ConVarRegistry.Add(Graphics.RenderCVars.__cv_r_shadowmapsize);
        ConVarRegistry.Add(new ConCommand("map", "Load a map by content-relative path.",
                                          ConVarFlags.None, WorldCommands.Map, WorldCommands.CompleteMap));
    }

    // Belt-and-braces ONLY — see the rationale below. Register() is the contract.
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void AutoRegister() => Register();
}
```

and the composition root calls them explicitly:

```csharp
// SpectraEngine.Executable/Program.cs — greppable, ordered, and it ROOTS each assembly for ILC
SpectraEngine.Core.Generated.SpectraConsoleModule.Register();
SpectraEngine.Editing.Generated.SpectraConsoleModule.Register();   // when arc E lands
MyGame.Generated.SpectraConsoleModule.Register();                  // engine-SDK mode (D21)

if (ConVarRegistry.ConVars.Count == 0)
    throw new InvalidOperationException(
        "No ConVars registered — a generated SpectraConsoleModule.Register() call is missing, "
      + "or ILC trimmed the assembly that owned it.");
```

**Why both mechanisms.** `[ModuleInitializer]` alone is the tempting answer and it is the one with a silent failure mode: under `TrimMode=full`, ILC roots from the entry assembly, and an assembly none of whose types are ever reached can in principle be dropped whole — taking its module initializer and therefore its cvars with it, **with no error, in the published build only**. The explicit call is deterministic, orderable, greppable, and it roots the assembly as a side effect; the module initializer is the convenience path for a game author who forgot. The boot-count assertion above turns the remaining failure into a loud one, and **it must run in `D0`/`D5`'s published-binary CI leg, not only in a debug run** — a debug-build test proves nothing about ILC.

*Honest flag: whether ILC actually drops an otherwise-unreferenced assembly's module initializer under `TrimMode=full` was not verified here. It is a ten-line spike and it belongs in `D0`.*

**Rejected:** reflection over loaded assemblies (banned outright); a single Core-side generator with `[assembly: ConVarModule(...)]` forwarding (still needs someone to enumerate assemblies at runtime); a hand-written `RegisterAll()` in Core (Core cannot reference its consumers).

### 2.3 Declaration: one line, and a read that compiles to a static field load

The hard performance requirement is that a cvar read in a hot loop is a field read, not a dictionary probe and not a virtual call through an object. That is only satisfiable if the storage is a real static field in the *reader's* assembly. So the declaration is a **`static partial` property carrying the attribute**, and the generator owns the backing field, the getter, the writer and the descriptor:

```csharp
// What a developer writes — SpectraEngine.Core/Graphics/RenderCVars.cs
internal static partial class RenderCVars
{
    /// <summary>Draw the compiled static world. Off is a fast way to see scene-graph meshes alone.</summary>
    [ConVar("r_drawworld", true, Flags = ConVarFlags.Cheat, Group = "Rendering/Debug")]
    public static partial bool DrawWorld { get; }

    /// <summary>Shadow map resolution per cascade.</summary>
    [ConVar("r_shadowmapsize", 2048, Min = 256, Max = 8192,
            Flags = ConVarFlags.Archive | ConVarFlags.RequiresRestart,
            Widget = Widget.Slider, Group = "Rendering/Quality")]
    public static partial int ShadowMapSize { get; }

    /// <summary>Vertical sync. Applied through the window on the next frame.</summary>
    [ConVar("r_vsync", false, Flags = ConVarFlags.Archive)]
    public static partial bool VSync { get; }

    // Fires AFTER the store is written, inline, on the render thread.
    [ConVarChanged(nameof(VSync))]
    private static void OnVSyncChanged(bool oldValue, bool newValue)
        => Engine.Current.RequestVSync(newValue);   // latched like _pendingTitle — the WINDOW is main-thread-only
}
```

```csharp
// What the generator emits
internal static partial class RenderCVars
{
    private static bool __v_r_drawworld = true;
    public static partial bool DrawWorld => __v_r_drawworld;          // inlines to a static field load
    internal static void __set_r_drawworld(bool v) => __v_r_drawworld = v;

    internal static readonly BoolConVar __cv_r_drawworld = new(
        name: "r_drawworld",
        help: "Draw the compiled static world. Off is a fast way to see scene-graph meshes alone.", // from the XML <summary>
        defaultValue: true, flags: ConVarFlags.Cheat, group: "Rendering/Debug",
        writer: __set_r_drawworld, onChanged: null);
}
```

Two representations, two jobs, one declaration: **the partial property is the read path** (a static field, no indirection, hoistable out of a loop) and **the `ConVar` descriptor is the metadata and write path** (name, help, range, flags, widget, the registry entry, the settings-panel row). The XML `<summary>` doubles as the help string when `Help =` is absent, because the generator can read the syntax trivia — so help text stops being a duplicated string literal.

Partial properties are a C# 13 feature and are available here: `Directory.Build.props` sets `<LangVersion>latest</LangVersion>` on `net10.0` (verified).

**Rejected:** Source's own `ConVar` object with `.GetBool()` (an object indirection plus, in Source, a virtual call — and it invites capturing the object in a hot loop); a `ConVarBool` handle struct (one pointer chase per read, and it cannot constant-fold for a stripped cvar); a plain `static bool` the developer writes themselves (no single place to express the default, and the generator cannot add a writer to a field it does not own).

### 2.4 Types, clamping and flags

**The type vocabulary is `D14`'s closed `KeyvalueType`, not a fourth enum.** `.sentdef` already defines `bool/int/float/string/vec2/vec3/vec4/color/…` with `Min`/`Max`/`Widget`/`readOnly`/`hideInEditor` — which is precisely the shape a settings panel needs, and `R‑2`/`D16` already rule that the editor is a metadata *consumer* and never its author. Ship `bool/int/float/string/vec3/color` in v1; the rest are reserved and a compile error until a consumer exists. Whichever of `C0`/`D14` lands first owns the enum; if they must temporarily stay separate, pin them with a test asserting the shared members agree by name and ordinal.

**Range policy: clamp out-of-range and warn; reject unparseable and change nothing.** They are different user intents. `mat_picmip 99` is a stated intent the engine can honour approximately, and clamping is also what lets a `.cfg` written by an older build with a wider range still load every line instead of half-failing. `r_shadowcascades abc` is a typo, and silently clamping it to the minimum would be actively harmful. `reset <name>` and `resetall [flag]` restore the attribute default.

**Flags, and the enforcement point for each — a flag with no enforcement point does not ship.**

```csharp
[Flags]
public enum ConVarFlags : uint
{
    None            = 0,
    Archive         = 1 << 0, // -> <user>/config.cfg on change (debounced) and at clean shutdown
    Cheat           = 1 << 1, // -> set refused unless sv_cheats is 1
    DevOnly         = 1 << 2, // -> registered and readable; hidden from find/help; set refused when shipping
    ReadOnly        = 1 << 3, // -> settable from code and the BOOT pass only (r_backend, engine_version)
    Hidden          = 1 << 4, // -> excluded from discovery, still settable by exact name (deprecated aliases)
    Notify          = 1 << 5, // -> logs one line on every change: the support boundary
    RequiresRestart = 1 << 6, // -> reuses .sentdef's requiresRestart bit name, same UI affordance
    Strip           = 1 << 7, // -> COMPILE-OUT under the SpectraShipping build property (§2.8)
    // bit 8 reserved. Do not reuse; do not renumber.
}
```

**Dropped from Source's set, deliberately:** `FCVAR_REPLICATED`, `FCVAR_PROTECTED`, `FCVAR_UNREGISTERED`, and the module-provenance flags (`GameDLL`/`ClientDLL`/`MaterialSystem`). There is no networking and none is planned, nothing in the engine holds a secret today, and registration is generated so "unregistered" is meaningless. Reserve bit 8 so adding one later is not a renumber; shipping an unenforced flag teaches users it means something.

**Rejected: a per-cvar `RenderThread` flag.** Every cvar's change callback can reach the renderer or the scene, so making it opt-in is a foot-gun that will be forgotten exactly once. Instead the rule is universal (§2.6): **all writes are render-thread-only**, always.

### 2.5 Commands: a `ref struct` that cannot outlive its call

```csharp
public readonly ref struct ConArgs
{
    public ReadOnlySpan<char> Name { get; }
    public ReadOnlySpan<char> RawArgs { get; }          // everything after the name, verbatim
    public int Count { get; }
    public ReadOnlySpan<char> this[int index] { get; }  // a slice into the line buffer — zero allocation

    public bool TryGetInt(int index, out int value);        // InvariantCulture
    public bool TryGetFloat(int index, out float value);
    public bool TryGetBool(int index, out bool value);      // 0/1 true/false on/off yes/no

    public IConsoleOutput Out { get; }
    public ConVarSource Source { get; }                 // Console | Bind | Alias | BootConfig | Script | CommandLine
    public Scene? Scene { get; }
    public Renderer Renderer { get; }
    public EntityWorld? Entities { get; }               // null until P4
    public ILuauConsoleHost? Luau { get; }              // null until O4/O5
    public IEditCommandSink? Edits { get; }             // E1's queue; null outside the editor
}

public delegate void ConCommandHandler(in ConArgs args);
public delegate int  ConCompletionProvider(in CompletionRequest request, Span<CompletionItem> results);
```

`ref struct` makes it **structurally impossible** for a command to capture its arguments, stash them in a closure or hand them to a `Task` — which is exactly the invariant that keeps commands synchronous and render-thread-only. A named delegate can take a `ref struct` parameter (only generic type arguments like `Action<T>` cannot); the BCL's own `SpanAction<T,TArg>` is the same shape.

**Rejected:** `delegate*<in ConArgs, void>` (marginally faster, and it forces `AllowUnsafeBlocks=true` on every game assembly that declares a command, for a benefit that is irrelevant because a human types a few commands a second); `string[] args` (allocates per token and per invocation, and lets a command outlive its call); one generated `switch` in Core (impossible across assemblies, which is the entire crux); handing each command the raw line to parse itself (eight incompatible quoting dialects).

### 2.6 The command buffer, and where it drains in the frame

**Two stages. Any thread may submit; only the render thread executes.**

```csharp
public static class SpectraConsole
{
    private static readonly ConcurrentQueue<PendingLine> _inbox = new();   // any thread
    private static readonly Deque<string> _work = new();                   // render thread only

    /// <summary>Submit from ANY thread. Never executes inline — the parse touches the registry.</summary>
    public static void Submit(string line, ConVarSource source = ConVarSource.Code)
        => _inbox.Enqueue(new PendingLine(line, source));

    /// <summary>RENDER THREAD, once per frame.</summary>
    internal static void Drain(in ConsoleFrame frame) { /* … */ }
}
```

`ConcurrentQueue` + a once-per-frame pump is the idiom this codebase already uses three times (`ShaderHotReloader`, `AssetManager.PumpPendingUploads`, `Scene.ProcessStaticWorldCompilation`); matching it is free.

**The drain point is pinned: immediately after `_inputManager.Update(deltaTime)` (`Engine.cs:216`) and before `_cameraController?.Update(deltaTime)` (`:217`).**

```
:216   _inputManager.Update(deltaTime);
:216a  _inputRouter.Pump(_inputManager, _commandBuffer);   // toggle key, +/- binds — ENQUEUES only
:216b  SpectraConsole.Drain(in frame);                     // <<< THE DRAIN
:217   _cameraController?.Update(deltaTime);               // sees console camera writes this frame
:221   _sceneManager.Update(deltaTime, _inputManager, _renderView);
       [O8: PreSimulation / PostSimulation Luau pumps land here, AFTER the console drain]
:227   ProcessStaticWorldCompilation(...)   // a brush-touching command dirtied in time
:232   _assetManager.PumpPendingUploads();
:234-243  DELETED by C3
:267   BuildRenderView(...)     :274 Render(...)     :282 Present(...)
```

Four constraints converge on that one position, all checkable against the current file:

1. Key binds turn press edges into command strings, and the edges only exist after `InputManager.Update` latches `_pressedThisFrame` (verified: `InputManager.cs:98–100`).
2. A command that moves, creates or retextures a brush must dirty the world **before** `ProcessStaticWorldCompilation` at `:227`, or the background compile launches a frame late — the identical argument `P4` uses to place `EntityWorld.Tick` and `O8` uses to place `PostSimulation`.
3. A command that moves the camera or flips a debug flag must be visible to `_cameraController.Update` at `:217` and `BuildRenderView` at `:267` in the **same** frame, or every console action feels one frame stale.
4. Everything a command can touch — scene mutation, GPU resource creation, `AssetManager.Load*` — is render-thread-only by standing invariant.

**Conflict resolved.** One design placed the drain between `SceneManager.Update` and `ProcessStaticWorldCompilation` (`O8`'s `PreSimulation` slot). That satisfies constraint 2 but not constraint 3: a `cam_pos` or `r_pipeline` command would land after the camera controller had already run. The `:216b` position satisfies all four, and it leaves `O8`'s script pump points sitting *after* the console drain — which is the right order anyway, because a console command should be able to configure state a script then reads the same frame.

**Rejected:** executing inline on submit (text arrives on the OS-event or stdin thread; this would mutate the scene and create GPU resources off the render thread — non-negotiable); draining after `PumpPendingUploads` (misses this frame's compile launch); two drain points (a command's observable effect becomes position-dependent).

### 2.7 Parsing, recursion, and never taking down the render thread

**Tokenizer rules, pinned by a table test:**

- `;` separates commands; `//` starts a comment. **Both are inert inside double quotes.**
- `"…"` groups one token. Exactly two escapes inside quotes: `\"` and `\\`. Nothing else. (Asset paths use forward slashes — `ContentRoot.NormalizeRelativePath` accepts both — so `\` as an escape never collides with an authored path.)
- An unterminated quote at end of line is implicitly closed, warns, and the line still runs.
- Names compare `OrdinalIgnoreCase`; numbers parse `InvariantCulture`.
- **The payload of `bind`, `alias` and everything after the `>` Luau sigil is NOT tokenized.** It is captured as one string and re-tokenized (or not, for Luau) at execution time. This is what makes `bind f1 "r_drawaabbs 1; echo on"` work, and it is mandatory for Luau, whose source legitimately contains `;`, `"` and `//`.

**`exec` and `alias` expansion push to the FRONT of the working deque** (Source's `Cbuf_InsertText` semantics), so expansion is depth-first and `exec a.cfg; r_x 1` really does end with `r_x` last. Tail-appending would make nested execs run in an order nobody can predict from reading the files.

**Limits, each with a named constant, each tripping loudly and clearing the buffer:**

| Limit | Value | Why |
| --- | --- | --- |
| `MaxCommandsPerDrain` | 512 | an `exec` of a huge cfg must not stall a frame arbitrarily |
| `MaxAliasDepth` | 16 | `alias a "a"` is one keystroke away |
| `MaxExecDepth` | 8 | plus a cycle set that names the full chain |
| `MaxCallbackDepth` | 8 | a two-cvar ping-pong |
| `MaxBufferedLines` | 4096 | a runaway producer |

**Every command invocation is individually wrapped in `try`/`catch` inside the drain.** A throw is logged with the command name and raw argument text, echoed as an error line, and the drain **continues**. `Engine.RenderLoop` already has a render-thread exception boundary whose catch sets `_renderThreadFaulted` and shuts the engine down; that boundary exists for genuine engine faults, and a mistyped console command must never reach it. Per-command catching is what keeps that invariant real rather than aspirational — and one `try` around the whole drain would silently discard every queued command after the bad one, including an `exec autoexec.cfg` tail.

The one failure a `catch` cannot help with is a stack overflow from unbounded alias recursion, which is uncatchable in .NET and kills the process with no log flush. **The depth counters are what prevent it, not the try/catch** — say so in a comment, or someone will remove one as redundant.

**Change callbacks run inline, immediately after the store write**, under the depth guard, with **self-suppression**: a callback that writes its own cvar applies the value but does not re-fire itself. Inline-after-write is the only ordering where a callback that reads the cvar it is reacting to sees the new value, and where a derived effect lands before this frame's `Render`. Self-suppression is what a normalising callback wants (`snap_grid 0.3` → callback writes `0.25`) and is the most common legitimate self-write. A cycle across two or more cvars trips `MaxCallbackDepth`, is reported once naming the full chain, and clears the buffer.

**Threading, precisely.** Writes are render-thread-only, enforced by an id check against the thread captured at `RenderLoop` entry: a `Set*` from another thread throws in debug and converts to a queued command line in release. Reads are legal from any thread — `bool`/`int`/`float`/`string` are atomically read on every supported platform, so an off-thread read is at worst one frame stale, which is harmless for a tuning knob. **`vec3` and `color` are 12 and 16 bytes and would tear**, so the generator stores them behind an immutable reference cell and a write is one atomic pointer swap. Enforce that in the generator, not by convention, or the first `vec4` added will forget.

**`wait [n]` earns its place, defined as a BUFFER SPLIT.** It ends the current drain and re-queues the remainder to run at the start of the next frame's drain (`n` frames later for `wait n`). Not a sleep, not a thread block — a blocking `wait` would freeze the window, stop `ProcessStaticWorldCompilation` from harvesting, and stop `Present`, i.e. exactly the hazard `O4` defends against for `while true do end`. Without `wait`, a `.cfg` cannot sequence anything across frames, and "set the mode, then screenshot" is a real developer workflow. Capped at 300 pending frames, cleared on map/mode change, reported by `wait_pending`. Source's `sv_allow_wait_command` gate is **not** copied: it exists because `wait` enabled scripted-input exploits in competitive multiplayer, and there is no multiplayer here.

*Named hazard: frames are not a proxy for "the world finished compiling."* `map foo; wait 1; screenshot` will screenshot a partially-compiled world, because `ProcessStaticWorldCompilation` lands progressive results across many frames. Either provide `waitforworld` (polls the compile state) or document it loudly — otherwise it is discovered as flaky screenshots in CI.

### 2.8 `Strip`: what "compiled out under AOT" actually means

"Compiled out" has to mean something checkable, and the only honest mechanism is the C# compiler, not the trimmer. The build declares:

```xml
<PropertyGroup><SpectraShipping Condition="'$(SpectraShipping)'==''">false</SpectraShipping></PropertyGroup>
<ItemGroup><CompilerVisibleProperty Include="SpectraShipping" /></ItemGroup>
```

The generator reads `build_property.SpectraShipping` from `AnalyzerConfigOptionsProvider.GlobalOptions` and, when true, emits for every `Strip`-flagged cvar:

```csharp
public static partial bool DumpEveryCarve => false;   // compile-time constant.
                                                      // No field, no writer, no descriptor, no strings.
```

`if (DebugCVars.DumpEveryCarve) { … }` then folds at compile time and ILC dead-code-eliminates the block *and everything only it called*; the name and help string never enter the binary at all. **This is verifiable**: a CI step greps the published binary for the stripped name and fails on a hit — the same shape as `D5`'s cooked-only validation gate.

**The honest limit, documented rather than discovered:** stripping is per-compilation, so a `Strip` cvar declared in Core is stripped for every consumer of that Core build. That is acceptable precisely because a shipped Spectra game is rebuilt from source with `PublishAot` anyway (`D0`'s two-host matrix rebuilds everything per RID), so "Core is rebuilt with the shipping property" is the normal path. It does mean the editor must always be built with `SpectraShipping=false` — confirm that (§7).

**`Strip` and `DevOnly` are distinct and both ship.** `DevOnly` is a runtime gate — registered, readable (so a support log can report it), hidden from discovery, set-refused in a shipped build. `Strip` is genuine absence. A released game genuinely wants both, on different cvars.

**Rejected:** `[Conditional("DEBUG")]` (only removes calls to void methods, does nothing for a property read or a registration, and keys on the wrong axis — the editor wants a Release build *with* dev cvars); `#if` in hand-written code (puts the condition at every declaration site); trimmer feature switches with `ILLink.Substitutions.xml` (works, and is the BCL's own mechanism, but it substitutes a method body at *trim* time, so the strings and descriptors survive in the pre-trim IL and the behaviour is invisible until publish).

### 2.9 Discovery, and the single metadata path

```
] find shadow
r_shadowmapsize      = 2048   (default 2048)  [archive restart]  Shadow map resolution per cascade.
r_shadowcascades     = 3      (default 4)     [archive]          Cascade count for the directional light.
r_drawshadowfrustum  = 0      (default 0)     [cheat]            Draw each cascade's fitted frustum.

] help r_shadowmapsize
r_shadowmapsize : int  = 2048  (default 2048, range 256..8192)
flags   : archive, restart-required
group   : Rendering/Quality
Shadow map resolution per cascade.
```

`find` matches the substring against **name AND help text** — that is what makes it a discovery tool rather than a prefix filter. Typing a bare cvar name prints the same record as `help` (Source's behaviour, and the one people rely on). Also: `cvarlist [prefix]`, `cmdlist [prefix]`, `flags <flag>`, and **`differences`** — every cvar not at its default, which is *the* support command: one paste tells you everything the user changed.

**One registry, one public record shape, three consumers**: `find`/`help`/`cvarlist`; the editor settings panel (`C12`); and `--export-console-schema`, which writes the same records without referencing the declaring assembly — mirroring `P5`'s `--export-entity-schema`. That last one means ROADMAP §11 sign-off 1 (in-process vs separate-process editor host) does not change this design: the in-process editor walks the registry, the out-of-process editor reads the export, and both see identical records.

**A hand-written settings page is rejected outright.** It desynchronises within weeks and it desynchronises *silently* — the panel keeps showing a knob the engine no longer reads. `R‑2` already forbids the editor arc from inventing a parallel descriptor for shader parameters; the same reasoning binds here.

**Duplicate registration is a loud, named error** listing both declaring types, never last-wins — the same rule `F2` applies to duplicate `SceneNode` GUIDs. Under AOT everything is one binary and module initializers run in link order, so **nothing may depend on registration order**; the sorted name index is rebuilt after registration completes.

---

## 3. The surface

### 3.1 Binds, and the retirement of F1–F6

**Binds key on an engine-owned `InputKey` enum plus a `ModifierMask`, never on `Silk.NET.Input.Key`.**

```csharp
[Flags] public enum ModifierMask : byte { None = 0, Shift = 1, Ctrl = 2, Alt = 4, Super = 8 }
public readonly record struct BindKey(InputKey Key, ModifierMask Mods);

public sealed class BindTable
{
    private readonly Dictionary<BindKey, string> _binds = new();
    // Latched at PRESS time, indexed by physical key. This is what makes the release fire even if
    // the bind changed, the console opened, or focus was lost while the key was held.
    private readonly string?[] _pendingRelease = new string?[(int)InputKey.Count];

    public void Bind(BindKey k, string commandString);
    public bool Unbind(BindKey k);
    public void UnbindAll();
    public int  WriteTo(TextWriter cfg);                 // round-tripped into config.cfg, sorted by key

    /// <summary>EXACT modifier match: with both `bind s X` and `bind ctrl+s save`, Ctrl+S fires ONLY save.</summary>
    public void OnKeyDown(InputKey key, ModifierMask mods);
    public void OnKeyUp(InputKey key);

    /// <summary>Console open, focus loss, editor mode change, gesture capture start.</summary>
    public void ReleaseAll();
}
```

**Three decisions, each with a specific reason.**

*Engine-owned enum, not Silk's.* Bind files are persisted user data. Keying them to a third-party enum's spelling means a Silk.NET upgrade silently invalidates every user's binds; and `E1` pins `EditorInputFrame` as host-agnostic and **forbidden from referencing Silk.NET**, so the bind table must be too, or the Uno host (`H1`/`H2`) cannot use it. One `SilkKeyMap` table translates at the event boundary. On top of that sits a **generated lowercase name table** (`f1`, `mouse1`, `kp_enter`, `lshift`) — never `Enum.TryParse`, which is culture-sensitive, allocating, and exposes a third-party spelling as the user-facing contract. Pin the table with a round-trip test over every mapped key, and treat an unknown key name in a cfg as **warn-and-keep-the-line**, never a drop.

*Modifier-qualified, exact match.* This diverges from Source, which has no modifier concept and makes SHIFT itself a bindable key. A Roblox/Hammer-style editor is modifier-heavy (Ctrl+Z, Ctrl+D, Shift+drag, Alt+click), and flat binds would force `SpectraEngine.Editing` to keep a private shortcut table — recreating the exact duplication this arc exists to delete. Exact matching is the rule that makes it safe; a fallback-to-unmodified rule would fire both binds, which is how every hand-rolled shortcut system produces its first bug report.

*`+cmd`/`-cmd` press/release pairs, with the release string latched at press time.* A `ConCommand` declared `Pressable` under base name `zoom` publishes `+zoom` and `-zoom`. The non-obvious half — which Source got right and every naive reimplementation gets wrong — is that the `-` string must be recorded **at press time** into a per-physical-key slot and fired on release even if the bind changed or focus moved, plus a `ReleaseAll()` on console open, focus loss, mode change and gesture capture. Without it you get the classic stuck-`+forward` bug where the camera slides forever after alt-tab, and it is nearly unreproducible after the fact because it depends on the exact frame focus moved.

**Two gaps in `InputManager` this exposes, named now rather than discovered mid-implementation** (both verified against the current file): there is **no released-edge accessor** — `OnKeyUp` just does `_keysDown.Remove(key)`, with no `_releasedThisFrame` set — and **no `KeyChar` subscription at all**; `Initialize` hooks only `KeyDown`/`KeyUp`/`MouseDown`/`MouseUp`/`MouseMove`. Both are small additive changes. Auto-repeat is currently *swallowed* because `OnKeyDown` guards on `_keysDown.Add(key)` returning true; keep that for `_pressedThisFrame` (every existing poller depends on it) but surface repeat on the new event stream, because a console text field without repeating Backspace is unusable.

**Input arbitration is one arbiter with a written order, not per-consumer opinions.**

```csharp
public enum InputConsumer : byte { Console, EditorCapture, Binds, Game }

// 1. Console overlay when open — swallows everything except its own toggle key, and issues
//    ReleaseAll() on open so no '+' command sticks.
// 2. An in-progress editor gesture — E1 already specifies Esc/RMB/capture-loss cancel, so Escape
//    must reach the gizmo, NOT the bind table.
// 3. Binds — deliberately the LOWEST-priority consumer. That is the invariant that stops the bind
//    table ever stealing an editor gesture.
// 4. EditorInputFrame / game polling, built from the same stream after the console pass.
```

**The corollary is the whole arbitration answer: gizmo shortcuts are not a second key map.** `SpectraEngine.Editing` registers `ConCommand`s (`edit_undo`, `edit_redo`, `edit_duplicate`, `gizmo_mode translate`, `gizmo_space world`) and ships their default binds in `default.cfg`. There is exactly one table, and the editing layer contributes commands to it.

**And then F1–F6 is deleted.** Five `Archive` bool cvars (`r_draw_wireframe`, `r_draw_vertices`, `r_draw_aabbs`, `r_draw_normals`, `r_draw_scenegraph`) composed into `DebugVisualization` once per frame — **rebuilt from the cvars, never XOR-toggled in place**, so the cvar is the single source of truth. `toggle <cvar>` (Source has it) is what makes `bind f1 "toggle r_draw_wireframe"` work with no per-toggle command. Five separate bools rather than one bitmask cvar, because `find r_draw` must list them individually — discovery is the feature being stolen.

F6 needs a small renderer addition. Verified: `Renderer` exposes only `abstract string CurrentPipelineName { get; }` and `abstract string NextPipeline();` (`Graphics/Renderer.cs:150,153`) — there is no way to select a pipeline by name, so `r_pipeline wireframe` and its completion provider are impossible today. Add `IReadOnlyList<string> PipelineNames` and `bool TrySetPipeline(string)`; ~8 lines per backend, source-compatible, and `NextPipeline()` stays as a thin wrapper. This matters beyond convenience: a bug-repro cfg (`+r_pipeline wireframe`) cannot be written against a cycle command, and repro cfgs are half of why this console is worth building. It survives ROADMAP §11 sign-off 9 either way — if wireframe becomes an overlay mode, `r_pipeline`'s value set shrinks and nothing else changes.

*Behaviour change to accept deliberately:* F1–F6 work today with zero configuration. Ship **compiled-in default binds that a cfg overrides**, never the reverse, or a fresh clone with no `Assets/Cfg/` loses every debug key. And F1 means "help" everywhere else in software — now that the layout is data, it is worth one deliberate look.

### 3.2 Config files: what is content, what is user state

**They are different things and they live in different places.**

`default.cfg` is **content**: it ships in `Assets/Cfg/`, resolves through `D2`'s `IContentSource` (a loose file in a dev build, a pack entry when shipped), and is never written. That satisfies the standing rule that assets are real files, and it means a total conversion can rebind keys — with the footgun that the mount stack has no notion of "trusted", so a mod pack can shadow it (§7).

User state is **not** content and must never enter the mount stack: packs are memory-mapped read-only and digest-verified, and an installed exe directory is typically not writable.

```csharp
public static class UserPaths
{
    /// Windows: %APPDATA%\Spectra\<project>\     Linux: $XDG_CONFIG_HOME (or ~/.config)/Spectra/<project>/
    public static string Root(string projectName)
    {
        string? over = Environment.GetEnvironmentVariable("SPECTRA_USERDIR");
        if (!string.IsNullOrEmpty(over)) return over;

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        // Guard the empty return unconditionally: the docs promise a non-empty result for
        // ApplicationData on Windows but say nothing about Unix without HOME. Fall back, log once,
        // and never throw — a container or service account must not fail to boot over a cfg path.
        if (string.IsNullOrEmpty(appData))
            appData = Path.Combine(AppContext.BaseDirectory, "userdata");

        return Path.Combine(appData, "Spectra", projectName);
    }

    public static string CfgDir(string p)   => Path.Combine(Root(p), "cfg");    // config.cfg, autoexec.cfg, history
    public static string SavesDir(string p) => Path.Combine(Root(p), "saves");  // RESERVED — formats §7 q10
    public static string LogsDir(string p)  => Path.Combine(Root(p), "logs");
}
```

`ApplicationData` (roaming / `XDG_CONFIG_HOME`) rather than `LocalApplicationData`, because config is config. `-userdir <path>` and `SPECTRA_USERDIR` exist for portable installs and CI. Keyed on `game.spectraproj`'s name/id so two projects never share settings. Deliberate: in a developer build `ContentRoot` resolves into the repo but the user dir does **not** — console history must not land in git.

*Verified only partly:* the Windows mapping for `ApplicationData` is documented (`%APPDATA%`, the roaming profile); **the Unix and macOS mappings are not documented on that page** and were not confirmed here. Confirm both before `C4` lands, because this path bakes into user data. `C4` is also the first writer of user-writable state anywhere in the plan, so it should define this location for `formats-and-pipeline.md` §7 q10 (save games) rather than letting that question invent a second one.

**Load order, one direction, later wins — and this is a merge of two designs that were describing different files:**

| # | Layer | Written by | Notes |
| --- | --- | --- | --- |
| 1 | engine C# defaults | generator | `Origin = Engine` |
| 2 | `game.spectraproj` `settings` | project author | `Origin = Project` (§5.3) |
| 3 | `<pack>/default.cfg` | game author | shipped content, read-only, includes default binds |
| 4 | `<user>/config.cfg` | **the engine** | archived cvars + binds + aliases; overwritten on write-back |
| 5 | `<user>/autoexec.cfg` | **the human** | never touched by the engine — this is the point |
| 6 | argv `+<command>` / `-set <cvar> <value>` | the launch | `spectra.exe d3d11 +r_pipeline wireframe +exec repro.cfg` |

Layer 5 after layer 4 is the property users actually rely on: **a hand-set cvar is never clobbered by last session's archive write-back.** Getting that backwards is the single most-complained-about behaviour in Source-adjacent engines. Layer 6 last is what makes a bug repro reproducible.

**A cfg line naming an unknown CVAR retains its value in a pending table and is re-emitted verbatim on write-back; a line naming an unknown COMMAND warns once and is skipped.** Cvars register late and link-order-dependently, so "unknown at exec time" is the normal case, not an error — retention means a value set before its owner registered still applies. Re-emitting unknowns is the same forward-compatibility stance `.smap` takes for unknown JSON members and `.spectramat` takes for unknown keys, for the same reason: an engine build that does not know about a setting must not eat it. Log at Information, not Warning, or a config shared across engine versions spams the log every boot. A bad line never aborts the cfg. **Pin it with a real test** — author a config with an unknown cvar, load it, write it back, assert the unknown survived — or the preservation behaviour rots within two months, exactly as `P9` argues for unknown classnames.

**Write-back is on clean shutdown, on `host_writeconfig`, on editor mode transitions, and debounced ~2 s after any `Archive` cvar changes.** The debounce is deliberate insurance: write-only-on-shutdown means a crash after a settings change loses it, which is Source's behaviour and a recurring complaint. The writer emits a `# generated — edit autoexec.cfg instead` header, cvars sorted by ordinal name, then `unbindall` + binds sorted by key, then the retained-unknown block — sorted for deterministic diffs, mirroring the determinism discipline the cook pipeline already enforces. Write to a temp file and atomically replace, or a crash mid-write corrupts it.

### 3.3 Completion and history

Per-keystroke allocation in a console that runs on the render thread is a real cost and an easy one to avoid.

```csharp
public readonly ref struct CompletionRequest
{
    public readonly ReadOnlySpan<char> FullLine;
    public readonly ReadOnlySpan<char> Prefix;     // the token being completed
    public readonly int ArgumentIndex;             // 0 = command/cvar name
    public readonly ConsoleContext Context;        // Game | Editor
}

public readonly struct CompletionItem { public readonly string Text; public readonly string? Hint; }

/// <summary>Writes at most results.Length items; returns the count.</summary>
public delegate int ConCompletionProvider(in CompletionRequest request, Span<CompletionItem> results);

// Call site: Span<CompletionItem> buf = stackalloc CompletionItem[64];   // Source's own cap
```

Names are interned at registration into one **ordinal-sorted `string[]`**, rebuilt only on registration (boot and assembly load, i.e. rare). Prefix completion is two binary searches for the `[lo, hi)` matching range: O(log n), zero allocation, no dictionary walk, no LINQ — and `find`/`help`/`cvarlist` reuse the same array. Providers are static, so the whole path is AOT-clean by construction. Steal Source's touch of showing a cvar's current value in `CompletionItem.Hint`.

**Rejected:** `IEnumerable<string> Complete(string prefix)` — the obvious signature, and it allocates an iterator plus a list per keystroke and invites LINQ inside it. A trie — a sorted array beats it at this cardinality (hundreds to low thousands), has no build cost, and is one line to rebuild. Pin the allocation discipline with a test asserting zero allocations per keystroke, because the moment someone adds a LINQ line inside a provider it is gone.

Providers ship for `bind` (key names, then all command names), `exec` (`*.cfg` in the user dir and mounted content), `toggle` (bool-typed cvars only), `r_pipeline` (`Renderer.PipelineNames`), and cvar/command names generally.

**`map <TAB>` is blocked on a cross-arc addition, and it is not this arc's to make.** `IContentSource` as specified in `formats-and-pipeline.md` §4.6 is `TryOpen`/`Exists`/`TryGetWatchPath` — it can answer "is this asset there" but cannot answer "what assets are there", so completing over available maps in a mounted pack is impossible today. The addition (`TryEnumerate(prefix, extension, List<string>)`) is cheap on both implementations — `PackSource` has the name table right there, `LooseFileSource` is `Directory.EnumerateFiles` — and every asset-argument completion needs it. Flag it as a `D2`/`D3` dependency and build the rest of `C5` without it. **Do not have the console scan the filesystem directly**: it would work in a dev build and silently return nothing in a pack-only shipped build, which is exactly the loose/packed drift `D5` exists to catch.

**Inline display:** ghost text of the best match rendered dim to the right of the caret, a dropdown of up to 8 below. Tab accepts, Tab again cycles, Shift+Tab cycles back, Esc dismisses without clearing the line, Ctrl+Space forces the list open. The cycle holds the **original typed prefix**, so cycling past the end restores exactly what the user typed — the thing every naive implementation gets wrong and users notice within a minute.

**History** is a bounded ring (`con_history_lines`, `Archive`, default 256) with the in-progress line stashed at index −1 (so Down returns you to what you were typing) and consecutive duplicates collapsed. Persisted to `<userdir>/cfg/console_history.txt`, plain UTF-8, newest last. Deliberately **not** in `config.cfg`: history is a log, not a setting, it churns every session, and `exec` must never be able to run it.

### 3.4 Drawing it — the honest section

**The engine has no text rendering. At all.** Verified by reading `SpectraEngine.Core/Graphics/`: no font, no glyph, no atlas, no 2D pipeline, and **no blend state anywhere** — `R10` independently records that D3D11 never calls `OMSetBlendState`, GL never enables `GL_BLEND`, and D3D12 hardcodes `BlendEnable = 0`. There is no text-rendering milestone in `ROADMAP.md`, `roblox-onboarding.md` or `formats-and-pipeline.md`. So an in-engine console overlay is a genuinely new subsystem, and **everything else about the console is not**.

Gating the console on an overlay would gate the whole arc on work nobody has scoped. So the staging inverts it:

**Stage 1 — headless, and it is most of the value.** `C0`+`C1` deliver the entire command surface with zero pixels:

- a background thread reading stdin into `Submit` (Source's own dedicated-server console — `IsBackground = true`, and do not wait on it at shutdown);
- `+cmd args` and `-set <cvar> <value>` on the command line, alongside the existing positional backend parse in `Program.cs`;
- `exec autoexec.cfg` at boot;
- key binds, once `C2` lands;
- programmatic `SpectraConsole.Submit(...)` from anywhere;
- an in-memory log ring that a panel or an overlay later binds to.

Every one of `find`, `help`, `differences`, every cvar, every command, every cfg works on day one from a terminal, and the whole core is **headlessly unit-testable** — no window, no renderer, no VM — which is the discipline the CSG oracles already set.

**Stage 2 (`C10`) — an overlay drawn with `DebugDraw` line-segment glyphs.** One `ITextSink` seam; the first implementation emits `DebugDraw.Line` segments through an ortho screen-pixel unprojection.

```csharp
public interface ITextSink
{
    float LineHeight { get; }
    float MeasureAdvance(char c);
    void  Text(Vector2 screenPx, ReadOnlySpan<char> s, Vector4 rgba);
    void  Rect(Vector2 minPx, Vector2 maxPx, Vector4 rgba);   // background, caret, selection
}
```

A ~150-line vector glyph table plus layout. **No new pipeline, no texture, no shader, no blend state** — `DebugDraw` already works identically on OpenGL, D3D11 and D3D12 and is already depth-off. It is ugly, and it works everywhere the day it is written. Crucially it proves the parts that are actually hard to get right — input routing while the console has focus, caret and selection, scrollback and wrapping, suggestion popup layout, resize — against a renderer that exists today, before any three-backend pipeline work.

**Stage 3 (`C11`) — swap the drawing backend for a bitmap font atlas** behind the unchanged `ITextSink`: `UiOverlay.spectrashade` (pos2 + uv + colour, ortho cbuffer from the existing framebuffer latch), `{OpenGL,D3D11,D3D12}QuadBatch` mirroring the existing `*LineBatch` files, and a baked atlas PNG + metrics through the existing `AssetManager` texture path (zero new dependency). Two named hazards live here and nowhere else: **this is the engine's first consumer of alpha blending**, so either `R10` lands first or `C11` hardcodes three local blend states that `R10` then unifies (the way `D3D11Renderer._overlayDepth` already hardcodes an overlay depth state) — coordinate, do not land concurrently. And the unresolved GL-vs-D3D texture-origin question (`formats-and-pipeline.md` §7 q6) hits a font atlas **immediately and visibly**: upside-down text on exactly one backend. That is a feature here — it is the cheapest ground truth anyone will ever get for a question the tree genuinely cannot answer today, because every dev texture is a symmetric checker.

**Rejected:** runtime rasterization (StbTrueTypeSharp / SixLabors.Fonts / FreeType) — the standing rule is that no dependency is adopted on an *inferred* AOT posture, and none of these was verified with an actual `dotnet publish -p:PublishAot=true`. A pre-baked atlas is one PNG plus a metrics file through a path that already exists; accepted costs, stated: fixed sizes, no arbitrary scaling, no CJK, no user font choice. MSDF is the upgrade and changes only the bake and the fragment shader.

**The overlay is a POST-PASS, not a `RenderView` item and not a seventh pipeline.** It draws after `_renderer.Render(scene, view, dt)` and before `Present`, in exactly the slot `DebugDraw` is flushed today. `RenderView` is a frustum-culled world draw list with a determinism contract the CSG oracles lean on; putting UI quads in it would corrupt that for no benefit. Being a pass rather than a pipeline is what keeps it from multiplying when `F3` collapses the six pipeline files.

**Stage 4 (`C12`) — the Uno editor panel needs no engine text rendering at all**, because Uno draws text. So `C10` and `C11` are not on the path to a console in the editor; whichever host gets a console first is the right one to build, and neither blocks the other. Note the constraint from `formats-and-pipeline.md` §5.3: Uno native elements do not alpha-blend and X11 opacity is unsupported, so the *in-viewport* console must be engine-drawn and never XAML floated over the surface — the same ruling that already binds gizmos. A docked panel beside the viewport is fine.

---

## 4. Integration

### 4.1 The log sink

**The console is a `Microsoft.Extensions.Logging.ILoggerProvider` implemented in Core — not a Serilog `ILogEventSink`.** Verified: `SpectraEngine.Core.csproj` references only `Microsoft.Extensions.Logging.Abstractions`, and `ILoggerProvider` lives there, so this adds no dependency. A Serilog sink would drag Serilog into Core and bind the console to one host's logging choice, which the Uno editor may not share.

```csharp
public sealed class ConsoleLogProvider : ILoggerProvider
{
    private readonly ConsoleLogRing _ring;
    private readonly ConcurrentDictionary<string, ConsoleLogger> _byCategory = new();

    public ILogger CreateLogger(string categoryName) =>
        _byCategory.GetOrAdd(categoryName,
            static (name, ring) => new ConsoleLogger(ring, ring.InternCategory(name)), _ring);
}
```

**Ring shape:** a fixed `Entry[]` (power of two, ~4096) plus a fixed `char[]` text ring (~256 KiB) under one write lock, with monotonic sequence numbers. Producers are many threads (background CSG compiles, asset decodes, the OS-event thread, the render thread) and the consumer is one; a lock is correct and its contention is negligible because log lines are rare relative to frames. Category names intern to an `int` **once per `ILogger` instance in `CreateLogger`**, never per call. `CopyView` writes into a caller-supplied `Span` — no `IEnumerable`, no LINQ, no `ToArray`.

**Filtering is applied on READ, not on ingest**, which is what makes raising verbosity reveal lines *already captured* instead of only future ones — a real Source annoyance worth fixing. `con_ingest_level` (default Debug) bounds what enters the ring at all, with an `IsEnabled` gate that skips the formatter below it, because read-time filtering requires ingesting more than you show and an unbounded Trace ingest from a hot loop would make the console the performance problem it exists to diagnose.

**`developer <n>` splits into two mechanisms** rather than Source's one, because mapping it onto MEL forces the split anyway: a **view level floor** (0 = Warning, 1 = Information, 2 = Debug) which is per-entry, and a **`DevOnly` visibility gate** in `find`/`help` which is per-cvar. Keeping the level floor console-side rather than reconfiguring `LoggerFilterOptions` at runtime means turning up console verbosity does **not** also spam the rolling log file and the Debug sink.

**One transient string per emitted line is unavoidable** through the `ILogger<TState>` contract's `formatter`. It is copied into the char ring and dropped, so retained memory is hard-bounded and the string dies in gen0. It is per *log call*, not per frame — a frame that logs nothing allocates nothing. That stops being true the day someone adds a Debug log inside the per-frame draw path; the mitigation is discipline, and it should be said out loud rather than assumed away.

**A correction to a claim that must not ship as fact.** One design asserted that `LoggerFilterOptions.MinLevel` defaults to `Information`, so a newly added provider silently receives nothing below Information unless `Program.cs` adds `b.AddFilter<ConsoleLogProvider>(null, LogLevel.Trace)`. **That is wrong**, and it was checked: `LoggerFilterOptions.MinLevel` is declared `public LogLevel MinLevel { get; set; }` with **no initializer**, so it defaults to `LogLevel.Trace` (the zero value), and `LoggerFactory.Create` does not call `SetMinimumLevel` — it just does `AddLogging(configure)` and builds. So with `Program.cs` as it stands today, a newly added provider **does** receive Trace and up, and the actual gate today is Serilog's own `MinimumLevel.Debug()` in the Serilog configuration. What *was* confirmed is that `AddSerilog` calls `builder.AddFilter<SerilogLoggerProvider>(null, LogLevel.Trace)`. **Add the `AddFilter<ConsoleLogProvider>` line anyway** — it costs one line and it is insurance against the day someone adds `SetMinimumLevel` or config-driven filtering — but write it down as insurance, not as a fix for a bug that does not exist, and pin the behaviour with a test that builds the factory exactly as `Program.cs` does and asserts a Debug line reaches the ring.

**One ring, one record, one Output panel.** `O4` already specifies a structured `{Severity, Message, ScriptNodeId, Line, Timestamp, StackFrames}` sink for Luau output with two implementations (ILogger now, ring buffer for a future panel) precisely so error surfacing is decoupled from the Uno arc. **That type and this one are the same type.** Building a second produces two output panels in the editor and two places for a user to look, with script errors and command errors in different ones for no reason. Whichever of `C1`/`O4` starts second adopts the first one's shape; reserve it when either begins.

```csharp
public enum ConsoleChannel : byte { Log = 1, Echo = 2, ScriptOut = 4, ScriptError = 8 }

public readonly record struct ConsoleLine(
    LogLevel Severity, DateTime Utc, int CategoryId, ConsoleChannel Channel,
    string Message, string? Exception, Guid? ScriptNodeId, int? Line);
```

Filterable by level, by `ILogger` source category (e.g. `SpectraEngine.Core.Scene.Scene`), by channel, and by `con_filter_text` (Source's own).

### 4.2 Luau in the same console

**One console, one explicit sigil, never a heuristic.**

- A bare line is a **console command**.
- A leading `>` makes the **rest of the line** verbatim Luau source, **not tokenized at all**.
- `>` alone on a line latches sticky Luau mode; the prompt becomes `luau>`.
- Inside that mode a leading `\` escapes one line back to command syntax.

**Rejected, and the reason is the point:** the tempting heuristic — "if token 0 is a registered cvar it is a command, otherwise Luau" — has a failure mode that gets *worse* as the system grows. The day a game data-declares a cvar named `x` (§5.3 makes that trivially possible), every previously-working `x = 5` Luau line silently changes meaning. A dispatch rule whose meaning depends on the current contents of a mutable registry is exactly the silent-wrong-guess failure this design exists to avoid. **Pin it with a test**: assert `x = 5` is a cvar set and `>x = 5` is Luau *even after* a cvar named `x` is declared. Also rejected: a `lua <source>` ConCommand (friction on the tightest iteration loop in the product, and the rest-of-line would have to bypass the tokenizer anyway — which is what the sigil does more honestly); mode-toggle-only with no sigil (a mistyped mode executes in the wrong language with no warning); `/` (reads as "chat command" to the Roblox audience, i.e. the opposite meaning); `=` (Lua 5.1 REPL convention, invisible to anyone who has not used it, and it collides visually with assignment).

**It runs against the ACTIVE Luau state** — edit-mode in the editor, the live game state when playing — never a private console state. A private state would have its own `_G` proxy and its own module cache, so a global set by a script would be invisible from the console and vice versa: "works in the console, not in the game", the worst class of debugging-tool bug. Each submission runs as a fresh `luaL_sandboxthread` of that state (locals do not leak, globals resolve through the shared proxy chain — `O4`'s per-script model reused), chunk-named `@console:<n>` so tracebacks point at the console and a future Output panel can double-click to the line.

**Expression sugar, with a precise retry rule:** try `return <src>` first; fall back to `<src>`-as-statement **only on a COMPILE error**. A *runtime* error is never retried, or a half-applied side effect runs twice.

It executes inside the drain, on the render thread — which `O4` already pins as the VM's only legal thread and which is where `Scene` is lock-free by design.

**Shipped-game gating is a manifest capability, not a flag:** `game.spectraproj` carries `console: { script: "off" | "dev" | "on" }`, default `off`; `dev` additionally requires `sv_cheats 1`. Arbitrary script execution in a released game is a capability decision belonging to the game author, in the game's own data, beside the pack list — and it is the *same* question as `formats-and-pipeline.md` §7 q2 and `roblox-onboarding.md` §5 q1. A launch flag cannot express "this game does not allow scripting"; a manifest key can.

**The console itself is never `#if`-compiled out of a shipped build.** A compiled-out shipped path is a path nobody tests, and it would delete the settings surface the data-driven runtime needs. Only script execution and Cheat-flagged commands are gated.

**`C7` REPLACES `O6`.** `roblox-onboarding.md`'s Command Bar and this console are the same tool — "type a thing, run it against the live scene", one history, one output stream, one focus target — and building both produces the duplication users notice first. `O6`'s own requirements are inherited unchanged: the selection exposed as a global so `for _, p in selection do p.CFrame = … end` works, and its named hazard that **command-bar mutations must be undoable like any other edit**. That hazard applies identically to `map`, `ent_create` and any change callback that edits the graph. Route structural changes through `E1`'s command queue once it exists; **before then, log one explicit line saying console edits are outside history.** Silently unrecorded edits in an editor are a trust problem, not a feature gap — and this must be written into `C0`, not discovered when a user loses work.

### 4.3 Entities

`ent_fire` is the cheapest high-value command in the design, because it is **`P4`'s connection tuple with `TimesToFire = 1` and zero new machinery**. `P5` already pinned keyvalues as string-typed on the wire, so a raw console token is already the correct parameter type — no conversion layer, no per-input parsing code, nothing new to test.

```csharp
[ConCommand("ent_fire", Flags = ConVarFlags.Cheat,
            Help = "ent_fire <target|!self|!picker> <input> [parameter] [delay]",
            Completion = nameof(CompleteTargetNames))]
private static void EntFire(in ConArgs a)
{
    if (a.Entities is null) { a.Out.Error("No entity world is running."); return; }
    // QUEUED, never dispatched inline: P4's hazard note requires event-driven spawn/despawn to
    // defer to the tick, and dispatching from inside the drain can re-enter the scene mid-walk.
    a.Entities.QueueInput(new PendingInput(
        Target: a[0].ToString(), Input: a[1].ToString(),
        Parameter: a.Count > 2 ? a[2].ToString() : null,
        Delay: a.Count > 3 && a.TryGetFloat(3, out float d) ? d : 0f,
        Activator: null, Caller: null, TimesToFire: 1));
}
```

`!self` / `!picker` / trailing-`*` reuse `TargetNameIndex` verbatim. Also `ent_create` (through `O2`'s `NodeClassRegistry` factory), `ent_remove`, `ent_pivot` — all `Cheat`-flagged — and `ent_dump`, which is not.

**`ent_dump` is strategically valuable beyond debugging.** It reads `EntitySchema`/`.sentdef` — the *same and only* consumer `D16`'s property panel is — which makes it a headless smoke test of the `D14`/`D15`/`D16` schema pipeline that exists **before any editor UI does**. It is where `D15`'s oracle ("define the same entity in C# and in Luau, assert byte-identical `.sentdef` records apart from the `Origin` badge") gets its human-readable rendering.

### 4.4 The shared generator question

**One generator project: `SpectraEngine.Generators` (netstandard2.0), hosting three incremental generators** — `EntityGenerator` (`P5`), `LuauBindingGenerator` (`O5`) and `ConVarGenerator` (this arc). `roblox-onboarding.md` `O5` already rules "do not create a second analyzer project"; the console would be the *third* payer of that tax, so the rule is restated here as binding on all three.

The tax is real and documented: `netstandard2.0` inside a solution whose `Directory.Build.props` sets `TargetFramework` globally (verified — it does); a `Microsoft.CodeAnalysis.CSharp` pin at or below the installed SDK's Roslyn; `PrivateAssets=all` / `OutputItemType=Analyzer`; central package management; and the incremental-caching rule that capturing an `ISymbol` in the pipeline destroys caching. Paying it three times is three chances to get it wrong differently.

**Shared infrastructure, factored out on the second arrival:** attribute collection, the `[ModuleInitializer]` registrar emitter, the interned-name sorted-array probe emitter, and the closed type vocabulary. That last is the substantive one — the cvar type set is a strict subset of `D14`'s `KeyvalueType`, and all three generators need the same "interned name → dense slot" emission. Building that twice is how two subtly different name-normalization rules ship.

**Whichever of `P5`/`O5`/`C0` lands first creates the project and pays the setup; the other two add a generator to it.** If `C0` is first, it is mis-sized: M becomes M–L for the first mover.

**Diagnostics, reserved codes `SC####`** mirroring `F4`'s `SS####` discipline (and note `formats-and-pipeline.md` §4.1 already claims `SC####` for the cooker — **coordinate the range split or pick a different prefix before either ships**; this is a real collision, flagged here rather than averaged away). Diagnostics: duplicate cvar name anywhere in the compilation; declaring type not partial; `Min > Max`; default outside `[Min, Max]`; name not matching `^[a-z][a-z0-9_]*$`; `Min`/`Max` on a non-numeric type; wrong handler signature; and **`Archive | Cheat` together as an error** — persisting a cheat cvar is a bug, not a configuration.

---

## 5. Cvars as the data-driven runtime's settings surface

### 5.1 The asymmetry with entities that makes this easy

`formats-and-pipeline.md` §3.2 needs `ScriptedEntity` — one compiled C# class doing sorted-array probe plus `lua_pcall` — because **an entity type needs a type with dispatchable behaviour**, and AOT forbids creating one at runtime.

**A ConVar has no behaviour to dispatch.** It is a name, some metadata, and a value slot. So `ConVarRegistry.Declare(descriptor)` at runtime involves no reflection, no codegen and no polymorphism: it is **AOT-legal by construction** and needs no `ScriptedX` trick at all. The source generator exists purely so that engine C# gets a strongly-typed static field with a zero-lookup hot-path read (§2.3) — it is a convenience for the compiled side, not the mechanism.

That is what keeps "one fixed runtime binary serves any game" true for settings.

```csharp
public readonly record struct ConVarDescriptor(
    string Name, KeyvalueType Type, string DefaultText, ConVarFlags Flags,
    string Help, string? Display, string? Group, Widget Widget,
    float Min, float Max, ConVarOrigin Origin);

public enum ConVarOrigin : byte { Engine = 0, Project = 1, Luau = 2, Sdk = 3 }  // mirrors .sentdef's Origin badge
```

### 5.2 Two producers, one registry

- **Engine C#** → the generator → `Origin = Engine`.
- **`game.spectraproj`** gains a typed `settings` declaration block (§3.3 already reserves `settings`) → `Declare` → `Origin = Project`.
- **Luau** gets `ConVar.define{ name = "game_gravity", type = "float", default = "196.2", min = 0, max = 2000, help = "Studs/s^2.", archive = true }` — a host function mirroring `Entity.define`'s validation shape exactly → `Origin = Luau`.
- **Engine-SDK mode** (`D21`) → the same generator in the game assembly → `Origin = Sdk`.

All four land in one registry carrying the `Origin` badge, so `find`/`help`/the settings panel/`--export-console-schema` have exactly one input no matter how many games exist.

**A name collision between an engine cvar and a project- or Luau-declared one is a loud, named startup error**, never last-writer-wins — the same rule `F2` applies to duplicate `SceneNode` GUIDs. A game setting silently shadowing an engine setting is undiagnosable.

### 5.3 Where it plugs into the boot path

`formats-and-pipeline.md` §3.4 creates the renderer at step 4 and the Luau VM at step 6. That ordering forces one thing: **display mode, backend and vsync must be readable before the VM exists.** So the boot chain in §3.2 is layered so that layers 1–4 and 6 are all readable at step 1, and `ConVar.define` (`Origin = Luau`) can only ever add *new* cvars at step 6, never re-order the ones the renderer already consumed.

`con_dumpcvars` emits the whole registry with origins for diffing, and the mount-stack log line is extended to name the config chain in order — the same way the content source stack is already logged.

**A graphics cvar must state whether it applies live or requires a restart.** Reuse `.sentdef`'s `requiresRestart` flag bit and its name rather than inventing a second convention, or players change a setting, see nothing, and change it again.

---

## 6. Milestones

New prefix **`C`**, checked against `F`/`E`/`P`/`S`/`R`/`H` (`ROADMAP.md`), `O0`–`O9` (`roblox-onboarding.md`) and `D0`–`D22` (`formats-and-pipeline.md`). Dependency-ordered.

| id | Milestone | Size | Depends on | Slots into `ROADMAP.md` |
| --- | --- | --- | --- | --- |
| **C0** | ConVar/ConCommand core: attributes, generator, registry, tokenizer, command buffer, drain, discovery | M (M–L if first to create `SpectraEngine.Generators`) | nothing hard; shares the generator project with `P5`/`O5` | Phase 0, parallel to `F2`/`F4` |
| **C1** | Headless front ends: `ILoggerProvider` + ring, stdin thread, `+cmd`/`-set` argv | S | C0 | Immediately after C0 |
| **C2** | `InputKey` + event stream, `InputRouter`, `BindTable`, `+`/`-` commands, aliases | M | C0; **sequence with or after `E1`, never concurrently** | Arc E, alongside E1 |
| **C3** | Retire F1–F6; `Renderer.PipelineNames`/`TrySetPipeline`; `Assets/Cfg/default.cfg` | S | C2 | Arc E |
| **C4** | Config: `UserPaths`, `exec`, archive write-back, unknown-cvar retention, the 6-layer chain | M | C0, C2; wants `D2` for exec-from-pack | After D2 |
| **C5** | Completion engine + history, headless and unit-tested | M | C0; `map <TAB>` blocked on `IContentSource.TryEnumerate` (`D2`/`D3`) | Parallel |
| **C6** | Shipping flags: `sv_cheats`, `DevOnly`, `ReadOnly` boot window, `Strip` compile-out, `--export-console-schema` | S–M | C0; policy half wants `D9` | With/after D9 |
| **C7** | Luau in the same console — **REPLACES `O6`** | M | C0, C1, `O4`, `O5` | Arc O, in place of O6 |
| **C8** | cvars as the data-driven settings surface: project `settings`, `ConVar.define`, boot chain | M | C4, C6, `D9` | Arc D, after D9 |
| **C9** | Entity commands: `ent_fire`, `ent_dump`, `ent_create`, `ent_remove`, `ent_pivot` | S–M | `P4`, `P5`; C0 for completion; wants `D14` | Arc P, after P5 |
| **C10** | Overlay v1 via `DebugDraw` line glyphs + `ITextSink` | S–M | C2, C5 | Off the critical path |
| **C11** | 2D overlay pipeline, font atlas, the engine's first alpha blend | M | C10; **collides with `R10`** | Arc R, sequenced against R10 |
| **C12** | Editor console panel + settings UI generated from cvar metadata | M | `H1`, C5, C7; `D14`'s widget vocabulary | Arc H, after H1 |

**What works without an overlay — say this out loud, because it is the whole staging argument.** After `C0`–`C6` and with **zero** pixels of console: every cvar is typed, clamped, documented and discoverable from a terminal; every key is bindable; `Engine.cs` names no key code; settings persist per user; `spectra.exe d3d11 +r_pipeline wireframe +exec repro.cfg` reproduces a bug exactly; and the editor's settings panel has a complete metadata source waiting for it. `C10`/`C11` add a place to type it inside the window; they add no capability.

**Notes on the sharpest ones.**

- **`C0` is two-thirds generator plumbing risk**, identical to `P5`'s documented MEDIUM–HIGH. Mitigate with Verify snapshot tests over generated output — already the house pattern in the compiler tests. Validate the partial-property output shape against the exact SDK in use *before* the attribute surface has fifty users.
- **`C1`'s two edges:** `Console.ReadLine` on a background thread blocks through process exit (mark it `IsBackground`, never wait on it), and a windowed shipped build has no stdin unless a console is allocated — which is fine, because this is the developer surface.
- **`C2` is the collision milestone.** `E1` is landing `EditorInputFrame` and gizmo drag capture in the same frame body where input arbitration must live. Apply an `R‑9`-style ruling: `C2` lands with or after `E1`, never concurrently, and does **not** convert the existing pollers (`FlyCameraController`, `SceneManager.Update`) — the event stream is purely additive beside the polled API.
- **`C3` is the milestone that proves the arc**, and it is verifiable by a sentence: the keys still do exactly what they did, and `Engine.cs` names no key code.
- **`C6` is a security/support boundary, not a feature.** Route *every* mutation path — console line, cfg exec, bind, Luau, editor panel, programmatic `Set` — through the single `ConVar.SetFromText` gate (enforced structurally by making the generated writers `internal`), and pin it with a matrix test over `(flag × ConVarSource)`. A released game exposing a Cheat-flagged cvar because the gate was checked in one of two paths is found by players, not by tests.
- **Tests ship with each milestone, not as a trailing one.** A console is a text-processing system, and text-processing systems without a quoting/escaping oracle suite acquire undocumented behaviour users then depend on. Minimum set: tokenizer table (quotes, escapes, `;` inside quotes, `//`, unterminated quote, sigil rest-of-line integrity); clamp-vs-reject matrix including NaN-unbounded; alias/exec recursion depth trip; a throwing command not faulting the render loop; config round-trip with an unknown cvar surviving byte-for-byte; bind arbitration including forced `-cmd` release on focus loss; the language-disambiguation test from §4.2; cross-assembly registration from a second test assembly; and the registration-count assertion in the **published-binary** CI leg. Plus `con_selftest`, logged from the demo's existing 5-second self-test cadence.

---

## 7. Decisions that need you

1. **Is `sv_cheats` the right name with no multiplayer?** `sv_cheats` is familiar muscle memory; `cheats` or `spectra_cheats` are honest about an engine with no server — and this is a naming lock the moment cfg files exist.
2. **Does a shipped game expose the console at all by default, and does script execution default to off?** On-by-default with `DevOnly`/`Cheat`/manifest gating makes it a support and modder surface; frozen-shipped-games makes half the flag set dead weight — and this is the same question as `formats-and-pipeline.md` §7 q2 and `roblox-onboarding.md` §5 q1, so answer all three once.
3. **Which key opens the console, and is it identified by scancode or by character?** Scancode is layout-independent and physically stable but unreadable in a cfg; character is readable but Source's `~` is a dead key or absent on German, French and Nordic layouts.
4. **One cvar namespace for editor and game, or an `ed_` prefix — and one `config.cfg` or two?** One namespace means a support cfg from a player and one from the editor are interchangeable but the editor's cvars pollute a player's `find`; splitting is cleaner but needs a rule for which file a cvar belongs to.
5. **Lock the prefix convention now** (`r_`, `mat_`, `sv_`, `con_`, `snd_`, `ed_`, Source's own set): renaming a cvar after cfg files exist is a migration, and a deprecation-alias table has to exist from day one either way.
6. **Is the per-user config root `ApplicationData` (roaming / `XDG_CONFIG_HOME`), and does the save-game write path share it?** Sharing one root answers `formats-and-pipeline.md` §7 q10 for free; keeping them separate risks two locations invented independently.
7. **Are console-driven scene mutations undoable, globally or per command?** Routing everything through `E1`'s queue makes the console safe in the editor but makes a runtime event like `ent_fire` an undo entry, which is wrong — so this may need to be a per-command flag rather than one rule.

---

## 8. What is speculative here

- **Nothing in this document was built or run.** Every line number was read out of source on 2026-08-21 and every design claim is a specification, not an observation.
- **The central performance claim is unmeasured:** that a generated partial-property getter over a static field actually inlines to a static field load in an ILC-compiled Release build, rather than staying a call. Measure it in `C0`; do not assume it.
- **ILC's behaviour for a module initializer in an otherwise-unreferenced assembly under `TrimMode=full` was not verified.** It is the justification for the explicit `Register()` contract and it is a ten-line spike belonging in `D0`.
- **That a named delegate with an `in ref struct` parameter compiles cleanly under this solution's settings** is strongly implied by `SpanAction<T,TArg>` but was not compiled here.
- **`Environment.GetFolderPath(SpecialFolder.ApplicationData)`'s Unix and macOS mappings are not documented on the `SpecialFolder` reference page** and were not confirmed. The Windows mapping (`%APPDATA%`, roaming) is documented. Confirm the rest before `C4` writes a file.
- **Silk.NET's `KeyChar` behaviour** — that it fires for printable characters only, so Enter/Backspace/Tab/arrows must come from `KeyDown` — is community knowledge rather than an API contract, and IME/dead-key behaviour on a German layout (where `^` and `´` are dead keys) is untested. Ten minutes of empirical checking before `C10`'s input line is written.
- **`C11`'s size is extrapolated** from the existing `*LineBatch.cs` files (74 / 130 / 66 lines), not measured. A quad batch needs an index buffer and a texture binding a line batch does not, so treat **M** as a floor.
- **`D14`'s `KeyvalueType` has not landed**, and it carries members (`targetname`, `noderef`, `asset:*`) that mean nothing for a cvar. Sharing one enum with unusable members versus two enums pinned equal by test is a genuine tradeoff, and the first is asserted here without having seen `D14`'s final shape.
- **The `SC####` diagnostic prefix is claimed twice** — by this arc's generator and by `formats-and-pipeline.md` §4.1's cooker. That collision is real and is flagged rather than resolved.
