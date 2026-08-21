# Native dependencies

## Box3D

The physics solver, vendored as a **git submodule** at `external/box3d` and
pinned to an exact commit. Upstream is [erincatto/box3d](https://github.com/erincatto/box3d),
MIT licensed.

```bash
git submodule update --init --recursive
```

```powershell
native/build-box3d.ps1                  # win-x64, Release
native/build-box3d.ps1 -Rid win-arm64
native/build-box3d.ps1 -Clean
```

Output lands in `native/runtimes/<rid>/native/box3d.dll`. That directory is
build output and is **not** committed — the submodule commit is the thing under
version control, and a binary that can drift from it is worse than no binary.

### No Developer Command Prompt is needed, and none should be used

CMake's Visual Studio generator locates MSVC through the registry and drives
MSBuild itself, so `cl` on `PATH` is irrelevant to this build. *(This is a
different path from the NativeAOT publish, which shells out to the ilcompiler's
`findvcvarsall.bat` and does care — see the note in `CLAUDE.md`.)*

Verified on this machine: CMake 4.2.3 → Visual Studio 18 2026 generator → MSVC
14.50.35717, from an ordinary shell.

### Why a pinned commit and not the tag

`v0.1.0` is the **only** tag upstream has ever cut, and `main` has moved well
past it — the pinned commit reports version **0.2.0**. Tracking a tag would mean
tracking a year-old alpha; tracking `main` would mean the build changing under
us without a commit in this repo saying so. A submodule pin is neither: the SHA
is recorded here, and moving it is a reviewable change.

### Build options are a contract, not preferences

`build-box3d.ps1` is the only place they live, so a developer build, a CI build
and a release build cannot drift into producing different physics. Two of them
are decisions rather than tuning:

| Option | Value | Why |
| --- | --- | --- |
| `BOX3D_DOUBLE_PRECISION` | `OFF` | The float build, decided 2026-08-21 (`docs/physics.md` §7 item 2). **ABI-affecting** — the two modes ship as mutually exclusive builds, so flipping this invalidates every bound struct layout, not just performance. |
| `BUILD_SHARED_LIBS` | `ON` | A DLL beside the managed assembly, which is what `[LibraryImport]` resolves and what a NativeAOT publish carries. |

The rest (`BOX3D_SAMPLES`, `BOX3D_UNIT_TESTS`, `BOX3D_BENCHMARKS`, `BOX3D_DOCS`)
are off because we do not consume them: the samples pull a windowing stack this
engine already has, and the unit tests are upstream's and run upstream.

### Units

`b3SetLengthUnitsPerMeter(1.0f)` at startup, because **one spectraunit is one
metre**. Box3D scales its own collision and constraint tolerance macros by that
value, so the solver runs at exactly the scale it was tuned for and nothing
converts at the binding boundary. What does *not* rescale is anything whose unit
is not a pure length — gravity, sleep thresholds, density — which is why those
live in `SpectraEngine.Core/Physics/PhysicsDefaults.cs` rather than being
inherited.

### The ABI guard

```powershell
native/build-box3d.ps1 -Abi        # rebuild box3d.dll AND regenerate the manifest
```

`native/abi-probe` is a small C program compiled against the **same headers and
the same precision flag** as `box3d.dll`. It prints the real `sizeof`,
`_Alignof` and `offsetof` of every struct the managed binding mirrors;
`native/box3d-abi.manifest` is that output, **committed**.

`Box3DAbiTests` then checks every managed struct against it, field by field.
That test needs no C toolchain and no native library — it reads the committed
text — so every developer and every CI job gets the check for free, and
regenerating the manifest is a deliberate act when the pin moves, with the
layout diff landing in review.

**Why it matters more here than usual:** a P/Invoke struct whose layout
disagrees with the library's does not fail to compile and does not throw. It
silently reads and writes the wrong bytes. Box3D is alpha and its API is already
breaking — recent upstream commits bump `b3HullData` and `b3CompoundData`
versions — so "the pin moved and a struct grew a field" *will* happen; the only
question is whether it surfaces as a red build or as physics that is subtly
wrong and irreproducible.

The guard has been watched to fail, which is the only way to know a guard works:
swapping `b3Quat`'s scalar and vector fields — a mistake that leaves `sizeof`
**identical at 16 bytes** and produces rotations that look almost right — is
caught on field offsets. A size-only check would have missed it entirely.

**Still owed:** a CI job that regenerates the manifest and fails on a diff, so
upstream drift is caught even when nobody re-runs the probe locally.

### Do not take a third-party C# binding as a dependency

Both existing ones are prior art to read, not packages to install: one is
ClangSharp autogen on a *daily* workflow tracking alpha `main`, the other a
recent single-author package. This project has already been burned twice by
that class of dependency — a recommended-then-archived Luau binding, and a
networking library that published clean under NativeAOT and then crashed the
native binary on its default API through a reflective assembly scan.
