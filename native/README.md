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

### Still owed before the binding is trusted

An **ABI guard**: regenerate `sizeof`/`_Alignof`/`offsetof` for every bound
struct from the C compiler in CI and diff them against the managed layouts, so
an upstream struct change is a red build rather than a corrupted stack. Box3D is
alpha and its API is already breaking — recent upstream commits bump
`b3HullData` and `b3CompoundData` versions — so this is not optional hygiene,
it is the thing that makes a moving pin safe to move.

### Do not take a third-party C# binding as a dependency

Both existing ones are prior art to read, not packages to install: one is
ClangSharp autogen on a *daily* workflow tracking alpha `main`, the other a
recent single-author package. This project has already been burned twice by
that class of dependency — a recommended-then-archived Luau binding, and a
networking library that published clean under NativeAOT and then crashed the
native binary on its default API through a reflective assembly scan.
