# Cook dependency spikes, September 2026

Two measurement spikes run against the plan's rule that no dependency may be
adopted on an inferred AOT posture. Both answers come from an actual publish and
an actual run of the published binary. Neither comes from a README.

These produce answers, not features. Nothing in the repo changed except this
file; the throwaway console used for Spike 1 was built and run entirely outside
the repository and has been deleted.

**Rig.** Windows 11 Pro 10.0.26200, 13th Gen Intel Core i9-13900K (32 logical
CPUs, AVX2 and AVX-512 capable), .NET SDK 10.0.201, runtime 10.0.5, Visual
Studio 18 2026 installer present at `C:\Program Files (x86)\Microsoft Visual
Studio\Installer`.

---

## Spike 1: BCnEncoder.Net determinism and AOT posture

### Question

The cook pipeline's incremental cache is content-addressed, so it rests entirely
on the encoder being deterministic: the same source bytes plus the same settings
must produce the same output bytes, or every cache entry is a lie and every
rebuild churns artifacts that are identical in every way that matters.
BCnEncoder.Net is the intended managed baseline, chosen because the editor must
run the cooking library in process and the editor is AOT.

Three sub-questions, plus one posture check:

- (a) Two encodes in one process, same settings. Equal?
- (b) Parallel option enabled versus disabled. Equal?
- (c) Two separate process runs. Equal? Hash seeds are per process, so this is
  the one that matters.
- Does the encode actually execute from a NativeAOT-published binary, or does it
  merely link?

### Method

`BCnEncoder.Net 2.3.0` is the current version on nuget.org (`dotnet package
search BCnEncoder.Net`), MIT OR Unlicense, one managed dependency
(`CommunityToolkit.HighPerformance 8.4.0`), no native dependency. A throwaway
`net10.0` console referencing it plus `StbImageSharp` (the decoder the engine
already uses, so the decode side is not a confound) was built in the scratchpad.

Input is `D:\Projekte\Spectra\Assets\Textures\dev_grid.png`, read only: 128x128
RGBA8, which is 32x32 = 1024 BC7 blocks, so the parallel path has real work to
split across 32 task slots.

```
dotnet build BcSpike.csproj -c Release -p:PublishAot=false -o out-jit
dotnet publish BcSpike.csproj -c Release -r win-x64 -p:PublishAot=true -o out-aot
dotnet publish BcSpike.csproj -c Release -r win-x64 -p:PublishAot=true -p:IlcInstructionSet=avx2 -o out-aot-avx2
```

The publish ran from an ordinary shell with the VS Installer directory prepended
to `PATH`, per CLAUDE.md.

Each encode is SHA-256'd. The sweep covers `Fast`/`Balanced`/`BestQuality`,
formats BC1/BC1WithAlpha/BC3/BC4/BC5/BC7, a generated mip chain, and all seven
PNGs under `Assets/Textures`. `Options.IsParallel` with `TaskCount = 32` is the
parallel arm; `IsParallel = false` with `TaskCount = 1` is the serial arm.

### Measured result

**(a) Same process, same settings, twice: byte-equal.**

```
A.parallel.1 : len=16384 sha256=63963c31e36b796ba023527d506403acbb0b74a367e96122a63665ff9e3dc08f
A.parallel.2 : len=16384 sha256=63963c31e36b796ba023527d506403acbb0b74a367e96122a63665ff9e3dc08f
A.equal      : True
```

**(b) Parallel versus serial: byte-equal, at every quality and every format.**

```
B.par_vs_ser : True
Q.Fast       : par=c7bd741a... ser=c7bd741a... equal=True
Q.Balanced   : par=c7bd741a... ser=c7bd741a... equal=True
Q.BestQuality: par=63963c31... ser=63963c31... equal=True
F.Bc1         : par=91b75db5... ser=91b75db5... equal=True
F.Bc1WithAlpha: par=91b75db5... ser=91b75db5... equal=True
F.Bc3         : par=c987bba8... ser=c987bba8... equal=True
F.Bc4         : par=3bbd81c3... ser=3bbd81c3... equal=True
F.Bc5         : par=da2a144f... ser=da2a144f... equal=True
F.Bc7         : par=63963c31... ser=63963c31... equal=True
M.mipchain    : par=b34b56db... ser=b34b56db... equal=True
```

**(c) Five separate process runs: byte-equal.** Five distinct pids, one hash.

```
pid          : 22440
C.canonical.parallel : 63963c31e36b796ba023527d506403acbb0b74a367e96122a63665ff9e3dc08f
pid          : 41976
C.canonical.parallel : 63963c31e36b796ba023527d506403acbb0b74a367e96122a63665ff9e3dc08f
pid          : 20776
C.canonical.parallel : 63963c31e36b796ba023527d506403acbb0b74a367e96122a63665ff9e3dc08f
pid          : 41264
C.canonical.parallel : 63963c31e36b796ba023527d506403acbb0b74a367e96122a63665ff9e3dc08f
pid          : 33512
C.canonical.parallel : 63963c31e36b796ba023527d506403acbb0b74a367e96122a63665ff9e3dc08f
```

The same five-process run against the AOT binary is likewise internally
consistent (`526eafe4...` five times, see below).

**AOT posture: publishes clean and actually executes.** The publish emitted zero
trim, AOT or single-file warnings, and produced a single 1,737,728-byte native
executable plus its PDB, with no runtime assemblies beside it.

```
Name         Length
BcSpike.exe 1737728
BcSpike.pdb 8417280
```

Running it proves execution rather than linkage: dynamic code is off, and a real
16,384-byte BC7 payload comes out in 20.48 ms.

```
dynamic code : IsDynamicCodeCompiled=False IsDynamicCodeSupported=False
image        : 128x128 rgba8, blocks=32x32
A.parallel.1 : len=16384 sha256=526eafe48aa7793372e1deae0392c18de5b014b0986dc9fcca236bc8bd3d63ef
A.equal      : True
T.parallel   : 20.48 ms
DONE
```

**The finding: the JIT build and the default AOT build disagree on BC7.**

```
JIT  BC7 BestQuality : 63963c31e36b796ba023527d506403acbb0b74a367e96122a63665ff9e3dc08f
AOT  BC7 BestQuality : 526eafe48aa7793372e1deae0392c18de5b014b0986dc9fcca236bc8bd3d63ef
```

This is not a JIT-versus-AOT effect. It is the instruction-set baseline, and it
was isolated to AVX2 specifically:

| Configuration | BC7 hash |
| --- | --- |
| JIT, stock | `63963c31` |
| JIT, `DOTNET_EnableAVX2=0` | `526eafe4` |
| JIT, `DOTNET_EnableHWIntrinsic=0` | `526eafe4` |
| JIT, `DOTNET_EnableAVX512=0` (AVX2 on) | `63963c31` |
| JIT, `DOTNET_EnableFMA=0` (AVX2 on) | `63963c31` |
| JIT, `DOTNET_MaxVectorTBitWidth=128` | `63963c31` |
| JIT, `DOTNET_TieredCompilation=0` | `63963c31` |
| AOT, default baseline | `526eafe4` |
| AOT, `-p:IlcInstructionSet=avx2` | `63963c31` |

Turning AVX2 off in the JIT reproduces the default AOT bytes exactly; raising
the AOT baseline to AVX2 reproduces the JIT bytes exactly. Neither AVX-512, nor
FMA, nor `Vector<T>` width, nor tiering moves the answer. The exact instruction
inside the BC7 search that changes was not isolated, and the divergence is
almost certainly a floating-point tie-break in the mode and partition search
rather than a defect in either path.

Two consequences of the table are worth stating separately. The default AOT
binary produced the non-AVX2 result *while running on an AVX2 machine*, so a
default NativeAOT publish bakes its baseline at compile time and does not adapt
to the host CPU for this code path. And BC7 is the only affected format: BC1,
BC1WithAlpha, BC3, BC4 and BC5 are byte-identical across both baselines.

The divergence is also content dependent. Of the seven PNGs in
`Assets/Textures`, only `dev_grid.png` differed between the two baselines;
`checker_gray`, `checker_orange`, `floor_tile`, `gradient_mask`, `wall_brick`
and `white` were byte-identical under both.

Magnitude, measured by decoding both blobs and comparing against the source:

```
blocks       : 1024
differing    : 310 (30.27%)
PSNR A vs src: 55.9791 dB      (AVX2)
PSNR B vs src: 56.1173 dB      (non-AVX2)
max |A-B| per channel: 4
```

Thirty percent of blocks differ, at a 0.14 dB quality difference and a maximum
per-channel decode delta of 4/255. The two outputs are visually equivalent and
byte-different, which is the worst shape this could have taken: nothing in the
artifact signals that anything changed.

### Consequence for the plan

BCnEncoder.Net clears the bar the cache actually needs, with one condition
attached.

1. **Adopt it.** Determinism holds where it was questioned: repeated encodes,
   parallel versus serial, and separate processes are all byte-equal. The
   parallel option is free to use; it is not a determinism risk. The AOT posture
   is verified by publish and by execution, not inferred: zero AOT warnings, and
   a 1.7 MB native binary that genuinely encodes.
2. **The cache key must include the instruction-set baseline, or the cook must
   pin one.** Encoder output is deterministic for a fixed baseline and not
   across baselines. Two hosts that disagree about AVX2 will produce different
   BC7 bytes for the same source and the same settings, and a content-addressed
   cache keyed on source plus settings alone will hand one host the other's
   artifact, or thrash rebuilding it. The concrete exposures are an editor run
   under `dotnet run` (JIT, adapts to the host CPU) against a published AOT cook
   tool, and any future decision to raise `IlcInstructionSet`.
3. **Preferred fix: cook only from a NativeAOT binary with a pinned
   `IlcInstructionSet`.** That makes the baseline a build-time constant rather
   than a property of whoever happens to run the cook, which is the same
   discipline `native/build-box3d.ps1` already applies to ABI-affecting options.
   Recording the baseline in the cache key is the cheaper alternative and is
   strictly weaker: it makes the mismatch visible instead of impossible.
4. **This does not block a shared or checked-in cache, but it decides its
   key.** Settle this before the cache format is written down, not after.

### Not measured

- Whether two different AVX2-capable CPUs agree with each other. Only one
  machine was available. The measured fact that a default AOT binary ignores the
  host's AVX2 support makes a fixed-baseline AOT cook safe by construction, but
  the cross-CPU JIT case is untested.
- BC6H (HDR) was not exercised.
- The exact instruction responsible for the AVX2 divergence.
- Encoder throughput at production texture sizes. The 20 ms figure is a 128x128
  image and is not a planning number.

---

## Spike 2: Silk.NET.Assimp AOT posture

### Question

`SpectraEngine.Core` references `Silk.NET.Assimp` and `ModelImporter` uses it at
runtime. Assimp is a native, per-RID library reached through P/Invoke, and
Silk.NET locates it through a path resolver that leans on `DependencyContext`
and `Assembly.Location`, both of which degrade under single-file and AOT
publishing. So: does model import actually execute from a NativeAOT-published
binary, or does it merely link?

### Method

Publish the demo exactly as CLAUDE.md documents, from an ordinary shell with the
VS Installer directory prepended to `PATH` so the ilcompiler's
`findvcvarsall.bat` can reach `vswhere.exe`:

```
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;" + $env:PATH
dotnet publish SpectraEngine.Executable -c Release -r win-x64 -p:PublishAot=true
```

Then run the published binary for 22 seconds with the editing self-test on, and
read its own log. The demo covers both halves of the model contract in one run:
`crate.obj` loads synchronously on the render thread at scene build, and
`signpost.gltf` is requested asynchronously and imported on the thread pool.

```
SpectraEngine.Executable.exe d3d11 --selftest
```

Two negative controls followed, both against the same published binary: rename
`Assimp64.dll` out of the way, and move it into `runtimes/win-x64/native/`.

### Measured result

**The publish succeeded.** Warnings were confined to Silk.NET, and they land
precisely on the native-library resolver this spike is about:

```
Silk.NET.Input.Common.dll : warning IL2104: Assembly 'Silk.NET.Input.Common' produced trim warnings.
Silk.NET.Windowing.Common.dll : warning IL2104: Assembly 'Silk.NET.Windowing.Common' produced trim warnings.
ILC : warning IL3000: Silk.NET.Core.Loader.DefaultPathResolver...: 'System.Reflection.Assembly.Location.get' always returns an empty string for assemblies embedded in a single-file app.
ILC : warning IL3002: Silk.NET.Core.Loader.DefaultPathResolver.TryLocateNativeAssetFromDeps(...): Using member 'Microsoft.Extensions.DependencyModel.DependencyContext.Default.get' ... DependencyContext for an assembly from a application published as single-file is not supported. The method will return null.
ILC : warning IL3002: Silk.NET.Core.Loader.DefaultPathResolver.TryLocateNativeAssetInRuntimesFolder(...): ... The method will return null.
```

The output is one native executable plus the three native libraries flat beside
it, and the content tree:

```
Assimp64.dll                     5767680
box3d.dll                        1073664
glfw3.dll                         232960
SpectraEngine.Executable.exe     7644672
Assets/
```

**Model import executes.** From the published binary's own log:

```
[INF] Loaded model Models/crate.obj (2 submesh(es), 24 vertices, 12 triangles, 3 material(s))
[INF] Loaded model Models/signpost.gltf (2 submesh(es), 48 vertices, 24 triangles, 3 material(s))
[INF] Async prop Models/signpost.gltf landed 0.10 s after load and was placed in the scene
[INF] Assets: 8 texture(s), 10 material(s), 2 model(s) requested / 2 placed; ... scene: 11 of 11 mesh nodes ...
```

Real vertex, triangle, submesh and material counts came back, on both the OBJ
synchronous path and the glTF thread-pool path. Both props reached the scene.
The run logged nothing at `WRN`, `ERR` or `FTL`, and the self-test passed on
every one of its four cycles:

```
[INF] Editing self-test: PASS - screen-ray picked 'PillarB', grabbed its x handle (108 gizmo line vertices),
      dragged over 3 frames to (1.0000, 0.0000, 0.0000) against an expected (1.0000, 0.0000, 0.0000),
      error 0.000000 units; ... undo restored and redo re-applied the transform exactly; node left at rest
```

**Negative control 1: the dependency is real.** With `Assimp64.dll` renamed
away, the same binary fails at exactly the expected frame, naming Silk's own
entry point:

```
[ERR] Demo prop Models/crate.obj failed to load; the demo runs without it
   at Silk.NET.Assimp.Assimp.CreateDefaultContext(String[]) + 0x65
   at Silk.NET.Assimp.Assimp.GetApi() + 0x34
   at SpectraEngine.Core.Assets.ModelImporter.Import(String, String, ModelImportOptions) + 0x91
[ERR] Model import failed (Models/signpost.gltf): Could not load from any of the possible library names!
[WRN] Async prop Models/signpost.gltf failed to import (...); the demo runs without it
[INF] Assets: ... 2 model(s) requested / 0 placed; ... scene: 7 of 7 mesh nodes ...
```

`11 of 11 mesh nodes` became `7 of 7`, and `2 placed` became `0 placed`. Import
genuinely goes through the native library, and the failure degrades to a logged
error rather than taking the process down.

**Negative control 2: the native library must sit flat beside the executable.**
With `Assimp64.dll` present but only under `runtimes/win-x64/native/`, the
published binary cannot find it at all:

```
[ERR] Demo prop Models/crate.obj failed to load; the demo runs without it
[ERR] Model import failed (Models/signpost.gltf): Could not load from any of the possible library names!
[INF] Demo scene 'Demo' loaded ... 2 model(s) requested (0 placed so far)
```

This is the `IL3002` warning on `TryLocateNativeAssetInRuntimesFolder` made
real: under NativeAOT that probe reads `DependencyContext`, which returns null,
so the `runtimes/` layout is not searched.

Restoring `Assimp64.dll` beside the executable returned the run to `2 placed`,
confirming the controls were reversible and nothing else had drifted.

### Consequence for the plan

1. **Silk.NET.Assimp executes under NativeAOT. It does not merely link.** Both
   the synchronous render-thread path and the async thread-pool path import real
   geometry from a published binary with no runtime assemblies present. The
   claim in `ModelImporter`'s remarks is now backed by a published-binary
   measurement rather than by a JIT run. Model import is safe to depend on for
   an AOT cook tool and for the AOT editor.
2. **The deployment constraint is load-bearing and fails silently.** The native
   library must be flat beside the executable. The SDK's AOT publish does this
   automatically, which is why the default path works, but any repackaging that
   preserves the `runtimes/<rid>/native/` layout, or that places the cook's
   native dependencies in a subdirectory, breaks model import at the first
   import with no build-time signal. If the cook ships as its own binary, or
   the editor's payload is ever restructured, this needs a test that imports one
   model from the published layout.
3. **Assimp costs 5.8 MB in every AOT payload that can import a model.** That is
   a real number for a shipped editor and for a cook tool, and it is an argument
   for the cook and the runtime consuming a cooked mesh format rather than the
   game binary linking an importer at all. It is not an argument against the
   dependency for tooling.
4. **The Silk.NET trim warnings (`IL2104` on `Input.Common` and
   `Windowing.Common`) are windowing, not import.** A headless cook tool that
   references only the import path would not carry them. Worth confirming when
   the cook's project file is written, rather than assuming it inherits the
   demo's warning set.

### Not measured

- Whether import works from a published binary on a machine with no Visual C++
  runtime and no developer tooling. The publish and the run were on the build
  machine.
- Any format beyond OBJ and glTF. FBX and COLLADA go through the same
  `aiImportFile` entry point and the same resolver, so the posture answer
  carries, but no other format was exercised.
- Linux or macOS RIDs. The question was asked and answered for `win-x64` only.
- The `--offscreen-probe` gate was not run; the evidence here is the ordinary
  demo run plus the self-test.

---

## Scratch artifacts

The Spike 1 console (`BcSpike`) lived entirely in the session scratchpad at
`%TEMP%\claude\D--Projekte-Spectra\...\scratchpad\BcSpike` and has been deleted.
No probe file was created inside the repository. The only repository file added
by this work is this document. The AOT publish outputs under
`SpectraEngine.Executable/bin/` are ordinary gitignored build output.
