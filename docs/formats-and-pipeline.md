# Spectra — Formats and the Data Pipeline

> How authored content becomes shipped content: the pack container, the asset formats, the two map formats, the data-driven runtime split, and the cook pipeline that connects them.
> Companion to `ROADMAP.md` (arcs `F`/`E`/`P`/`S`/`R`/`H`) and `docs/roblox-onboarding.md` (scripting, `O0`–`O9`). This document owns one arc — **`D*`**, for data — and references milestones in the other two by id rather than restating them.
> Sizes are relative (**S / M / L**), never calendar. Nothing here was built or run; every claim about the tree was read out of source and is cited, every claim about an external library or format was verified against its documentation on **2026-08-21** and is dated, and everything speculative is labelled.

---

## 1. What is settled, and what the whole pipeline looks like

These are decisions, not proposals. Everything below is designed inside them.

1. **Both the editor and shipped games are NativeAOT-published.** Therefore no .NET runtime codegen anywhere: no Roslyn hosting, no collectible `AssemblyLoadContext`, no `MetadataUpdater`, no reflection-based serialization, no runtime XAML, no `Assembly.LoadFile`. Compile-time source generators and P/Invoke are the sanctioned escape hatches. AOT is a *publish* mode, not a *development* mode — a JIT debug configuration of the editor stays legal for daily work, gated by a CI AOT publish (see `D0`).
2. **Luau is therefore the mandatory scripting language**, vendored and bound via `[LibraryImport]`, per `docs/roblox-onboarding.md`. The C#/Luau boundary is fixed: **C# is what ships inside the runtime binary; Luau is everything that can change without rebuilding it.**
3. **The shipped game executable is data-driven.** It boots from a project manifest, mounts packs, loads a compiled map, and runs. It contains no game-specific C# and, after `D7`, no shader compiler.
4. **Source formats and cooked formats are different artifacts and both are first-class.** The editor authors and reads source formats — `.png`, `.spectramat`, `.spectrashade`, `.luau`, `.smap`. The cooker produces packed, GPU-ready equivalents — `.simage`, `.smaterial`, `.specshadecomp`, bytecode, `.scmap`. **The already-landed source-format asset arc (`AssetManager`, `ContentRoot`, `ImageDecoder`, `TextureAsset`, `MaterialDefinition`/`MaterialParser`) is not thrown away; cooked packs are strictly additive.**
5. **`.smap` is the authored map (text, editor); `.scmap` is the compiled map (binary, shipped).** `.smap` is `ROADMAP.md` `P2`'s `.spectramap` under the user's name, unchanged in design. `.scmap` is *not* `P11b` — see §2.7.

**`F1` has landed** — a fact `ROADMAP.md` §2's status table was stale about when this document was written, and which that table now records correctly. `Bsp/FaceSurface.cs` exists with a per-plane `MaterialRef` and full Hammer texture axes (`UAxis`, `VAxis`, `UOffset`, `VOffset`, `UScale`, `VScale`); `Brush` keys faces by **plane index** exactly as ruling `R‑3` requires; `ChunkMesh` carries per-material `ChunkSubmesh` arrays and the world emits one render item per `(chunk, material)` resolved through `Scene.Assets`, with `Scene.StaticWorldMaterial` demoted to the fallback for faces that name none. **The map format's `faces` records therefore have a real seven-field schema to bind to — nobody needs to guess it any more**, and the `.scmap` mesh layout in §2.7 mirrors the artifact that actually exists rather than the one the roadmap anticipated.

**End to end, from a PNG to a lit wall.** An artist drops `wall_brick.png` into `Assets/Textures/` and a `.spectramat` next to it naming that texture and a `.spectrashade`. In the editor, nothing is cooked: `AssetManager` resolves `Textures/wall_brick.png` through a loose-file content source exactly as it does today, decodes it off-thread, uploads it on the render thread, and the level designer textures a brush face with it. On save, the editor writes `.smap` — authored nodes, authored brush planes, per-face material *paths*, entity keyvalues, scripts — and nothing derived. To ship, `scook` walks the project: it re-decodes the PNG through the **same** `ImageDecoder` the runtime uses, generates mips in linear space, BC7-encodes them, and writes a restricted-profile KTX2 payload under the key `Textures/wall_brick.png` — the *source* path, unchanged, so an asset means the same thing in both modes; it validates the `.spectramat` (a missing texture is a cook error, not a magenta placeholder), compiles the shader once per target backend into a `.specshadecomp` blob, reads the `.smap`, runs the *same* `CsgWorld.Build` the editor runs to bake per-cell welded meshes and per-cell BSP trees into a `.scmap`, and emits one `.spack`. The shipped `spectra.exe` starts, reads `game.spectraproj`, memory-maps the pack, mounts it as the only content source, creates the window and the render thread, loads cooked shader blobs with no compiler present, mmaps the `.scmap`, hands each chunk's vertex and index spans straight to `Renderer.CreateMesh` with no CSG and no parse, resolves each material run's texture from the mapped KTX2 with no decode and no format conversion, boots Luau, and draws frame 1.

---

## 2. The formats

Six new binary formats and one new text format, plus one format that is named and deliberately unspecified. Three are genuinely earned as custom (`.spack`, `.scmap`, `.smodel`); two are subsets or reuses of existing specifications and say so (`.simage`, `.saudio`); one is deferred behind a dependency (`.smaterial`); one is text on purpose (`.smap`); and one — `.svideo` (§2.8) — is reserved by name only, deferred until there is a real need to design against. Every binary format follows the idiom already proven in this tree by `ShaderFileWriter`/`ShaderFileReader` — magic, `uint16` version, a fixed-stride entry table of explicit offsets and sizes, then a data section — widened where it needs to be.

**Rules binding every format below.**

- **Little-endian only.** Assert `BitConverter.IsLittleEndian` at parse and refuse loudly rather than pretending to support big-endian. The entire zero-copy premise is `MemoryMarshal.Cast` over raw bytes, which is endianness-native by construction; a byte-swapping reader would have to copy every vertex, which is the one thing these formats exist to avoid.
- **Struct-over-span reads only.** Every header and table is a `[StructLayout(LayoutKind.Sequential, Pack=1)] readonly struct` of primitives read via `MemoryMarshal.Read<T>` / `MemoryMarshal.Cast<byte,T>`. No reflection, no runtime codegen, no `BinaryReader` on any hot path. (`BinaryReader` is fine for a handful of shader blobs, as today; it is wrong for a 100k-entry table.)
- **Every format constant gets an owner and a reader that enforces it.** `EngineInfo.ModelFormatVersion`, `TextureFormatVersion` and `MapFormatVersion` are referenced by nothing in the solution today — only `ShaderFormatVersion` is wired. `ROADMAP.md` §13 asks whether to delete them. This arc answers: keep them, because `.smodel`/`.simage`/`.smap` are exactly what they version — **but no constant ships without a reader that enforces it**, so each lands with its format, never before.
- **Asymmetric versioning, following the precedent `.specshadecomp` already sets.** *Source* formats carry a version plus a `MinimumReadable*` companion plus unknown-member/unknown-section preservation, because they are the user's data and must survive an engine older or newer than they are. *Cooked* formats require an exact match and refuse loudly with "recook", because they are build outputs that can always be regenerated and any ambiguity is a bug.

### 2.1 `.spack` — the pack container

**Why custom, in one line:** nothing off-the-shelf offers an id-keyed table that is binary-searchable *in place* over a memory-mapped view with payloads aligned for zero-copy GPU upload, and ZIP fails on both counts.

The honest comparison, because "custom" must be earned. ZIP's central directory is a linear sequence of variable-length, name-keyed records at end-of-file: it cannot be binary-searched, so mounting means a full parse and a `Dictionary<string, ZipArchiveEntry>` with thousands of allocated strings at boot — precisely the cost AOT was chosen to avoid. ZIP also guarantees no alignment for stored entries (Android ships `zipalign` as a separate tool for exactly this reason), so `MemoryMarshal.Cast` over mapped vertex data is impossible and every cooked mesh must be copied. And `ZipArchive` has no memory-mapped read path. **Where ZIP wins and should be used: authored-source interchange bundles** — a prefab or asset drop a user emails, a template project — where universal tooling matters and zero-copy does not. Two containers, two clearly different jobs.

**Header — 64 bytes, offset 0.**

```
0x00  u8[4]   Magic          = "SPAK"   four-byte abbreviation of .spack; the extension
                                       is always spelled .spack
0x04  u16     FormatVersion  = EngineInfo.PackFormatVersion
0x06  u16     MinReaderVersion   refuse the pack if this exceeds what we implement
0x08  u32     Flags          bit0 EntriesSortedByAssetId (REQUIRED = 1 in v1)
                             bit1 IsPatchPack   bit2 IsModPack
                             bit3 NameTablePresent (default 1)
0x0C  u32     EntryCount
0x10  u64     EntryTableOffset      absolute, 16-byte aligned (= 64 in v1; explicit so
                                    the header can grow without a version bump)
0x18  u64     NameTableOffset       absolute; 0 = absent
0x20  u64     NameTableLength
0x28  u32     PackSequence          monotonic ordering key for patch packs
0x2C  u32     EngineVersion         (Major<<20)|(Minor<<10)|Revision — informational,
                                    never a load gate
0x30  u64     DataSectionOffset     absolute, 4096-byte aligned
0x38  u64     TotalFileSize         truncation detection without a stat()
0x40          END (ContentDigest lives at the tail — see below)
```

**Entry — 48 bytes, fixed stride, sorted ascending by `AssetId` compared as an unsigned 128-bit big-integer.** The table is reinterpreted as `ReadOnlySpan<PackEntry>` straight off the mapped view; lookup is a branch-free binary search with zero allocation.

```
+0x00  u128   AssetId            XxHash128 of the normalized content-relative SOURCE path
+0x10  u64    PayloadOffset      absolute file offset, 16-byte aligned
+0x18  u64    StoredSize         bytes on disk
+0x20  u64    UncompressedSize   == StoredSize when Codec == None
+0x28  u32    NameOffset         byte offset into the name table; 0xFFFFFFFF = absent
+0x2C  u16    NameLength
+0x2E  u8     Kind               0 Raw, 1 Image, 2 Model, 3 Audio, 4 Material,
                                 5 Shader, 6 Script, 7 Map, 8 EntityDefs,
                                 9 Bundle(reserved), 10 Video(reserved, §2.8),
                                 0xFF Tombstone
+0x2F  u8     Codec              0 None, 1 Deflate, 2 Zstandard (reserved until .NET 11)
+0x30         END
```

Name table: a sequence of `u16 length + UTF-8 bytes`, no terminators, addressed by absolute byte offset. It costs roughly 40 bytes per asset and it is what makes every log line, every `scook inspect` row and every bug report readable. Emit it by default.

Data section: payloads in entry-table order, each zero-padded to the next 16-byte boundary. The 4096-byte section start is for prefetch friendliness and so a block-level patcher diffs on 4K boundaries; the 16-byte per-payload alignment is what makes `MemoryMarshal.Cast` over the mapped span legal for `Vector4`/`Matrix4x4`/`FlatBspNode` reads.

Tail: `u128 ContentDigest` = XxHash128 over `[EntryTableOffset .. EOF)` with the digest bytes themselves excluded. Verified on every mount. **Say plainly what this is: corruption detection and a dedup/patch-diff key, not tamper resistance.** If the threat model ever includes a hostile mod pack, hashing does nothing and the answer is signing, which is a different design with key management attached (§7).

**Four properties worth naming.**

- **Identity is the normalized content-relative SOURCE path**, produced by the existing `ContentRoot.NormalizeRelativePath`. `Textures/wall_brick.png` is the key in the editor (where it resolves to a loose PNG) *and* in the shipped game (where it resolves to a pack entry whose payload is BC7 KTX2). This is the single decision that makes "a bug never reproduces in only one mount mode" structural rather than aspirational, and it means `AssetManager`'s caches, `MaterialParser`'s texture paths, `MaterialRegistry.Intern`'s keys and every existing `.spectramat` are untouched by the pack arc. Content-addressing was considered and rejected as the *identity*: it makes patch-by-name structurally impossible and every log line unreadable. Dedup — content-addressing's real benefit — is recovered at cook time instead (the cooker points duplicate payloads at one extent).
- **Compression is per entry, never solid, never whole-pack.** Solid compression destroys random access and mmap-in-place, and this engine streams a sparse chunked open world whose load order is unknowable ahead of time. The default for BC-compressed texture data and for cooked geometry is `Codec=None` — not laziness, the point: BC blocks are already entropy-dense and compressing them forfeits the zero-copy read entirely. Honest cost: per-entry-only compression forfeits cross-entry redundancy (500 similar small material files compress far better as one solid block). `Kind=Bundle` is reserved for that; v1 pays the cost and should measure it.
- **Codec policy is settled by a verified fact, not a preference.** Zstandard ships **in-box in `System.IO.Compression` in .NET 11** (`ZstandardStream`/`ZstandardEncoder`/`ZstandardDecoder`, mirroring the Brotli API surface; verified 2026-08-21). It is **not** in .NET 10, which this solution targets. Therefore: **v1 implements `None` and `Deflate` only, both in-box and AOT-clean with zero new packages, and reserves codec id 2 for in-box Zstandard on the .NET 11 upgrade. No compression library is ever vendored.** This retires the ZstdSharp/K4os/hand-rolled-LZ4 question entirely — all three carried undocumented NativeAOT posture, and two of them a determinism hazard.
- **Mounting is a priority-ordered stack, flattened at mount time.** Bands: 0 base packs, 100 patch packs (ordered by `PackSequence`), 200 mod packs (ordered by the user's list), 1000 loose files (dev/editor; opt-in in shipped builds). Last wins per logical path; deletion is a `Kind=Tombstone` entry with zero length. Flattening into one dictionary at mount, rather than probing sources in reverse per lookup, matters because mod lists get long and `O(sources)` probes per asset is a real cost that shows up exactly when a user has 40 mods. Every shadowing is logged at mount. Two identical mount lists must produce byte-identical resolution — that is a test, in the same determinism discipline the CSG oracles already enforce.

**The lifetime hazard, designed for rather than discovered.** A `ReadOnlySpan<byte>` into a mapped view is valid only while the pack stays mounted, and unmapping while a background decode holds one is an **access violation, not an exception** — a process crash with no managed stack. Mounts are refcounted (`PackHandle.AddRef`/`Release`), background work takes a ref, and `Unmount` is deferred until the count drops. This must land *with* the reader in `D3`, never as a follow-up, and every path that crosses a span to another thread — the existing `AssetManager` upload queue is exactly such a path — must take a ref.

**Also: map the whole file once.** On Windows, `MapViewOfFile`'s offset must be a multiple of the system *allocation granularity*, which is 64 KB, not the 4 KB page size. Per-entry views would therefore need absurd 64 KB entry alignment or a per-entry offset-modulo dance. Mapping the whole file costs address space, not RAM, and both targets are 64-bit. A `FileStream` + `RandomAccess.Read` variant of `PackSource` is worth keeping as a fallback for any platform where mapping misbehaves.

### 2.2 `.simage` — cooked images

**Why it is NOT custom, in one line:** KTX2 already does everything asked, including the sRGB-versus-linear encoding this engine desperately needs, and a custom format cannot win on capability — so `.simage` is the user's extension name over a **restricted profile of spec-conformant KTX2 bytes**.

This is the clearest case in the design where an existing format is genuinely better. KTX2 (verified against the Khronos spec, 2026-08-21) provides: a 12-byte identifier `AB 4B 54 58 20 32 30 BB 0D 0A 1A 0A`; a header of `u32` `vkFormat`, `typeSize`, `pixelWidth`, `pixelHeight`, `pixelDepth`, `layerCount`, `faceCount`, `levelCount`, `supercompressionScheme`; an index of `dfdByteOffset`/`dfdByteLength`/`kvdByteOffset`/`kvdByteLength` (`u32`) and `sgdByteOffset`/`sgdByteLength` (`u64`); then a level index of `levelCount` entries of three `u64` — `byteOffset`, `byteLength`, `uncompressedByteLength` — where **index 0 is the base (largest) level while the level *data* is stored smallest-first in the file**, for streaming. Byte order is fixed little-endian by spec. `supercompressionScheme` is `0 None / 1 BasisLZ / 2 Zstandard / 3 ZLIB`, applied **per level so levels stay individually seekable**. sRGB-ness is carried twice and unambiguously: in the `VkFormat` (`..._SRGB_BLOCK` vs `..._UNORM_BLOCK`) and in the DFD transfer function.

DDS is strictly worse on the axis that matters most here: its legacy header cannot express sRGB at all, only the DX10 extension can, and its cubemap/array semantics are a documented trap. **Reject DDS as the runtime format; accept it as a cooker *input*.**

The reason a custom format looked attractive was reader cost: a *conforming* KTX2 reader must parse the variable-length Data Format Descriptor and, for `supercompressionScheme=BasisLZ`, ship an entire transcoder. **The restricted profile answers both.** The engine's reader:

- accepts `supercompressionScheme ∈ {0 None, 2 Zstandard}` and rejects everything else by name — no BasisLZ, no transcoder, ever;
- accepts `faceCount ∈ {1, 6}`, `pixelDepth == 0`, and `layerCount ∈ {0, 1}` in v1 (arrays reserved);
- accepts `vkFormat` from a small allowlist — BC1/BC3/BC4/BC5/BC6H/BC7 in UNORM and SRGB variants, plus R8/RGBA8 uncompressed fallbacks — refusing anything else with the numeric format in the message;
- **writes a spec-conformant DFD but never parses it.** The level index and `vkFormat` carry everything the uploader needs.

That reader is a struct-over-span read of a fixed prefix plus a `levelCount × 24`-byte index — under ~150 lines — and the cook path can use `toktx`, KTX-Software, RenderDoc and any GPU debugger on the same bytes, which no custom format will ever offer. The file extension `.simage` is the user's vocabulary and is cosmetic; **the bytes are KTX2 and must stay valid KTX2** — there is no Spectra magic number here to keep in sync, because the file opens with KTX2's own 12-byte identifier. `EngineInfo.TextureFormatVersion` versions the engine's *profile*, recorded in a KTX2 key/value entry (`SpectraProfile`), not the container.

**Two decisions that `.simage` forces, and they are in-tree findings, not preferences.**

**Row order is top-down, and the CPU flip is deleted from the cooked path.** `Renderer.CreateTexture`'s documented contract today is *"tightly packed rows from bottom-left to top-right (OpenGL convention)"* (`Graphics/Renderer.cs:177`), and `ImageDecoder.FlipRowsInPlace` (`Assets/ImageDecoder.cs:91`) performs that flip on every decode. That flip cannot be carried forward: BC1/BC3 blocks can be vertically flipped with bit manipulation, **BC6H and BC7 cannot without a full decode and re-encode**, and no external cooking tool emits bottom-up BC data. Separately and more alarmingly, grep finds no compensating flip and no `1.0 - uv.y` in any backend texture class or in any `.spectrashade` — so **GL and the two D3D backends currently disagree about vertical texture orientation and nothing has caught it, because every texture in `Assets/Textures/` is a symmetric checker or grid.** `.simage` forces this to be settled once, in UV generation or in the shader, and settling it changes the loose-PNG path too. Which backend is presently "correct" is genuinely unknown and needs someone to look at an asymmetric texture on all three (§7).

**A new upload entry point.** `Renderer.CreateTexture` takes a single `ReadOnlySpan<byte>` and cannot express a mip chain or a block format. `.simage` needs `Renderer.CreateTexture(in TextureUploadDesc)` carrying per-mip spans, the pixel format, mip count and row pitches (`ceil(w / blockWidth) * bytesPerBlock`, computed once by the cooker rather than by a reader that could get it wrong for non-multiple-of-4 BC dimensions). Both the `.png` path and the `.simage` path then converge on the existing `PumpPendingUploads`, so the render-thread-owns-GPU-creation rule is preserved with no new pump.

**One promise not to make.** "One memcpy per mip" is achievable on GL (`glCompressedTexImage2D` per level) and D3D11 (`UpdateSubresource` with a source pitch), but **not on D3D12**, whose upload path calls `GetCopyableFootprints` and copies per row into an upload heap because the staging row pitch is 256-byte aligned. No file layout changes that. What `.simage` actually delivers is **no CPU decode and no format conversion**, on all three backends, with the row pitch supplied rather than computed.

### 2.3 `.smodel` — cooked meshes

**Why custom, in one line:** the engine's mesh contract is `CreateMesh(ReadOnlySpan<float> interleaved, ReadOnlySpan<uint> indices, ReadOnlySpan<VertexAttribute>)`, and glTF — the correct *interchange* format, which the importer should read — is by construction indirected, de-interleavable and JSON-headed, i.e. it is the conversion work the cook is supposed to have already done.

```
Header — 64 bytes
0x00  u8[4]   Magic = "SMDL"
0x04  u16     FormatVersion       (owner of EngineInfo.ModelFormatVersion)
0x06  u16     Flags               bit0 HasSkeleton  bit1 HasCollision
                                  bit2 Index32
0x08  u32     GeometryFormatVersion   must match the runtime's — see §4.4
0x0C  u32     SectionCount
0x10  f32[6]  Bounds              model-local AABB (min.xyz, max.xyz) — feeds
                                  Mesh.LocalBounds and SceneBvh with no vertex walk
0x28  ...     Section table: SectionCount × 24 bytes
                { u32 FourCC, u32 Flags, u64 Offset, u64 Length }
```

Sections: `VTXL` vertex layout · `VBUF` interleaved vertices · `IBUF` indices · `SUBM` submeshes · `LODS` · `SKEL` skeleton · `COLL` collision hulls · `NAME` string blob. `ANIM` reserved.

**An unknown section FourCC is skipped, not an error.** This is the most important structural decision in the format: it is what lets `SKEL`/`COLL`/`ANIM` be designed now and written later with no version bump, and it is the same forward-compatibility stance `P2` takes for unknown JSON members.

- **`VTXL`** — `u32 attributeCount`, `u32 strideFloats`, then `attributeCount × 8` bytes of `{ u8 Semantic, u8 ComponentType, u8 ComponentCount, u8 Flags, u16 ByteOffset, u16 Reserved }`. Semantics: `0 Position, 1 Normal, 2 Tangent4, 3 UV0, 4 UV1, 5 Color0, 6 BlendIndices, 7 BlendWeights`. This is what survives `R9` taking `VertexAttribute.StandardLayout` from 8 to 12 floats: the reader compares the file's declared layout against the layout the renderer wants and either hands `VBUF` straight to `CreateMesh` (exact match — the case the cooker makes normal) or stride-copies. The fallback must be *exercised* before `R9` lands, or it is untested the first time it is needed.
- **`IBUF`** — raw `u16` or `u32` per header flag; the cooker picks 16-bit when `vertexCount ≤ 65535`. Honest integration cost: `Renderer.CreateMesh` takes `ReadOnlySpan<uint>` only, so v1 widens on load. Recording the true width from day one is what keeps a native 16-bit path open.
- **`SUBM`** — per submesh `{ u32 IndexStart, u32 IndexCount, u32 MaterialNameOffset, u32 Flags, f32[6] Bounds }`. Material references are logical pack paths interned through the existing `MaterialRegistry` into a `MaterialRef` — the identical mechanism `ChunkSubmesh` already uses, so a model submesh and a chunk submesh are the same shape and should share one draw path. Note the deliberate difference from `.scmap`'s `CMSH`: a model keeps **one** vertex/index buffer with submeshes as index *ranges*, because a model's LODs must share a buffer for an LOD switch to be a draw-range change; a chunk splits the arrays, because its submeshes are uploaded and destroyed independently per cell. Both mirror their respective runtime artifacts rather than imposing one shape on both.
- **`LODS`** — `{ f32 ScreenHeightThreshold, u32 FirstSubmesh, u32 SubmeshCount }`. LODs are index ranges over one shared vertex/index buffer, so an LOD switch is a draw-range change with zero GPU resource churn.
- **`SKEL`** (designed, unimplemented) — `{ u32 NameOffset, i32 ParentIndex, f32[12] InverseBind }`, with `ParentIndex < ownIndex` enforced so a hierarchy walk is one forward loop. **Animation clips live in a separate `.sanim`**, because one skeleton with many clips is the normal case and welding clips into the mesh forces a mesh re-cook when a clip changes.
- **`COLL`** — `u32 hullCount`, then per hull `{ u32 PlaneStart, u32 PlaneCount }`, then a flat array of `{ f32 nx, ny, nz, d }`. This is the engine-specific one and it is why the format is genuinely earned: **collision as convex hulls expressed as plane sets is exactly `Brush`'s constructor input**, so a cooked model's collision converts directly into `Brush` instances and rides the existing `P7`/`P8` machinery with zero new collision code. A triangle soup would demand a query structure the engine does not have and does not want.

**On Assimp:** `Silk.NET.Assimp` is referenced in `SpectraEngine.Core.csproj` (line 35) and no importer code exists anywhere in the tree; `docs/roblox-onboarding.md` `O0` already names it as an AOT suspect. Because the editor must import models and the editor is AOT, a direct managed glTF/GLB reader is preferable to dragging a native library into an AOT-published surface. Verify any candidate's NativeAOT posture by publishing a throwaway console app, not by reading a README (§7).

### 2.4 `.saudio` — cooked audio

**Why it is barely custom, in one line:** the container is a 48-byte header over payloads whose codecs are existing standards, and its only genuinely new content is loop points, residency classification and a seek table — which have to live *somewhere* and would otherwise become a sidecar file.

Be honest about the state: `Audio/AudioManager.cs` is 23 lines — `Initialize`/`Shutdown` that log and nothing else, no device, no context, no source, no listener — while `Silk.NET.OpenAL` is referenced. **The format is the small half of an audio milestone that has no mixer behind it**, and over-specifying it now means specifying it wrong in exactly the way a format cannot be fixed once content exists.

```
0x00  u8[4]  Magic = "SAUD"
0x04  u16    FormatVersion
0x06  u8     Codec          0 PcmS16, 1 Vorbis(reserved), 2 Opus(reserved),
                            3 ImaAdpcm(reserved)
0x07  u8     Flags          bit0 Streaming   bit1 PositionalIntent
0x08  u32    SampleRate     the one project rate the cooker resampled to;
                            the runtime never resamples, it logs and plays
0x0C  u8     Channels
0x0D  u8     ChannelLayout  0 Mono, 1 Stereo (5.1/7.1 reserved)
0x0E  u16    Reserved = 0
0x10  u64    FrameCount     total DECODED sample frames
0x18  u64    LoopStart      SAMPLE FRAMES
0x20  u64    LoopEnd        sample frames; 0 = no loop
0x28  u32    SeekTableOffset   0 = none; streaming only
0x2C  u32    DataOffset
0x30         END
```

Seek table (streaming only): `u32 entryCount`, `u32 framesPerEntry`, then `entryCount × u64` byte offsets — what makes "start the track at 1:30" not a linear decode.

Loop points are in **sample frames**: bytes break the moment the codec changes, seconds lose sample accuracy, and a one-sample gap in a sustained ambience loop is audible. One project sample rate, because mixed rates mean per-source resampling that OpenAL will happily do at a quality and cost you did not choose. Mono required for positional sources (the cooker warns otherwise) because a stereo buffer in OpenAL plays unpositioned — the classic "why is my 3D sound not 3D" bug, free to catch at cook time.

**v1 ships `PcmS16` only.** Vorbis (NVorbis) and Opus (Concentus) are both plausible but their NativeAOT posture is *inferred*, not verified, and this arc has a standing rule against inferred dependencies. Music can pass through as Opus-in-Ogg the moment a decoder is verified — the codec is a header field, so this is reversible with no format change. One constraint to design around rather than discover: OpenAL's `AL_LOOPING` cannot express a sub-buffer loop region, so a loop region on a resident sound means splitting the buffer or using the streaming path.

### 2.5 `.smaterial` — cooked materials *(deferred behind `S3`)*

**Why custom, in one line:** its value is not parse speed — `MaterialParser` is a hand-rolled line parser and 500 files is microseconds — it is **reference resolution, cook-time validation, and parameter bytes pre-packed at the shader manifest's cbuffer offsets**, so the runtime binder does one memcpy per cbuffer instead of a name→location walk per parameter per material.

That last item is only possible with `S3`'s manifest in hand. **Building `.smaterial` before `S3` would bake today's name-keyed binding into a binary format and then have to change it**, which is exactly the mistake this design exists to avoid. So: `.spectramat` stays text and stays authoritative; **v1 packs the validated source text verbatim** as `Kind=Material`, and `.smaterial` lands after `S3`.

```
0x00  u8[4]  Magic = "SMTL"   four-byte abbreviation of .smaterial; the extension is
                              always spelled .smaterial
0x04  u16    FormatVersion
0x06  u16    Flags
0x08  u32    SectionCount
0x0C  u32    Reserved = 0
0x10  ...    Section table: SectionCount × 24 bytes — the IDENTICAL layout as
             .smodel's, so one section-table reader serves both formats
```

Sections: `SHDR` (resolved pack path to a `.specshadecomp`, plus `S3`'s shader source + import-closure hash so a stale cook is detectable) · `TEXS` (per slot: sampler name offset, string offset of the referenced image's logical asset path — the source path, whose pack payload is `.simage` bytes; see below — unit, filter, wrap, sRGB classification) · `PARM` (pre-packed parameter bytes at the manifest's cbuffer offsets) · `NAME` (string blob).

**`.smaterial` REFERENCES `.simage` files; it never embeds them.** `TEXS` stores a *path*, not pixels, and the path is exactly the identity §2.1 already settled on — the normalized content-root-relative logical path (`Textures/wall_brick.png`), resolved through the mounted pack stack at load like any other asset, never a pack-internal entry index and never a file offset. Three reasons, and they are the ordinary ones that decide this question everywhere it comes up: **one texture shared by twenty materials is stored once**, where embedding would store it twenty times; **a texture can be re-cooked and patched on its own** without rewriting — or even re-reading — every material that names it, which is what makes a patch pack a small download instead of a full one; and **textures stream independently of the material definitions**, which matters because a `.smaterial` is a few hundred bytes of parameters while its textures are megabytes, so binding a material must not mean paging in its whole texture set synchronously. It also keeps the reference mechanism identical to `.smodel`'s `SUBM` and `.scmap`'s `ASTB` — logical paths interned through `MaterialRegistry`/`AssetManager` — so there is exactly one way an asset names another asset in this design. The cost, accepted knowingly: a `.smaterial` whose texture is absent is a *load-time* miss rather than a structural impossibility, which is precisely what `scook verify`'s "every material's textures are present" check in §4.6 exists to catch before ship.

This also settles `ROADMAP.md` §13's open question — *"does `.spectramat` key parameter values by authored name or manifest index?"* — by splitting it correctly: **the text form keys by authored name** (survives reordering, is what a human edits and merges); **the cooked form keys by offset** (resolved at cook time, when the manifest is in hand). Both answers are right for their format. The durable risk after `S3`: a `.smaterial` cooked against one shader version and loaded against another silently misaligns cbuffer offsets and renders wrong rather than failing — hence the hash in `SHDR` and a loader that refuses a mismatch loudly.

### 2.6 `.smap` — the authored map (text)

**Why not custom, in one line:** it is `ROADMAP.md` `P2`'s `.spectramap` under the user's name — UTF-8 JSON read and written by a hand-rolled `Utf8JsonReader`/`Utf8JsonWriter` codec, never `JsonSerializer` — and this arc adds only a *canonical-writer specification* that makes `P2`'s pinned byte-identity test actually reachable.

`P2`'s pin is confirmed, not overturned: a scene-graph editor's authored artifact is reviewed, diffed, merged and grepped, and the map contains **zero derived data**.

**Canonical encoding.** UTF-8, **no BOM**, `\n` line endings, 2-space indent. Writer options: `Indented = true`, `IndentCharacter = ' '`, `IndentSize = 2`, and — load-bearing — `Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping`. The default `JavaScriptEncoder` escapes `+ < > &` and all non-ASCII to `\uXXXX`, which would turn every inline Luau script and every non-ASCII node name into unmergeable noise. All floats via `Utf8JsonWriter.WriteNumberValue(float)` and `Utf8JsonReader.GetSingle()` (shortest round-trip); GUIDs as lowercase `"D"` format. Ship a `.gitattributes` entry (`*.smap text eol=lf`) with the format, or a Windows checkout with `core.autocrlf=true` rewrites the file under you and turns a no-op save into a whole-file diff.

**Structure.** Fixed top-level member order: `spectramap`, `minimumReadableVersion`, `engine`, `scene`, `editor`, `nodes`. Hierarchy is **nesting** (`children` arrays), not a flat list with `parentId` — because sibling order is load-bearing: traversal order → `BrushPlacement` order → carve order → the bit-identical determinism oracles. `E6` already derived this for `InsertChild`; it binds the file format identically. A JSON array expresses that order exactly and cannot lose it, and a moved subtree is one diff hunk instead of N scattered edits.

```json
{
  "spectramap": 1,
  "minimumReadableVersion": 1,
  "engine": "1.0.0",
  "scene": { "name": "Testmap", "spawn": { "p": [0, 64, 0], "r": [0,0,0,1] } },
  "editor": { "viewport": { "p": [-120, 90, -120], "yaw": 0.78, "pitch": -0.35 }, "grid": 1.0 },
  "nodes": [
    {
      "id": "3f2a1c88-4b6d-4a19-9d0e-77c1f0a2b3e4",
      "name": "Wall",
      "transform": { "p": [0,0,0], "r": [0,0,0,1], "s": [1,1,1] },
      "brush": {
        "planes": [ [1,0,0,-32], [-1,0,0,-32], [0,1,0,-8], [0,-1,0,-8], [0,0,1,-2], [0,0,-1,-2] ],
        "faces": [ {"material":"Materials/wall.spectramat"},
                   {"material":"Materials/wall.spectramat","u":[0,0,1],"v":[0,1,0],"uo":0.5} ],
        "keepSource": false
      },
      "entity": { "class": "func_door", "keys": {"speed":"100"},
                  "outputs": [ {"output":"OnFullyOpen","target":"light1","input":"TurnOn",
                                "param":"","delay":0,"times":-1} ] },
      "script": { "module": false, "source": ["local part = script.Parent",
                                               "part.CFrame = CFrame.new(0, 10, 0)"] },
      "children": []
    }
  ]
}
```

**Realm and state are node members, written by name** (design: [`docs/realms.md`](realms.md) §2.5, which is normative for their meaning; this section is normative for their encoding, and the two must not drift). Per node, `"realm"` and `"state"` are **lowercase strings from a closed vocabulary** — `"shared" | "server" | "client"` and `"active" | "dormant"` — written **after `"name"` and before `"transform"`**, and **omitted iff the declared value is `Inherit`**:

```json
{ "id": "…", "name": "EnemyTemplate", "state": "dormant", "transform": { "p": [0,0,0] }, "children": [] }
```

Three rules the reader must uphold, each of which a plausible implementation gets wrong. **Never a numeric enum** — the file is reviewed and merged by humans, and a realm renumbering would silently re-audience every existing map. **Never the *effective* value**, which is derived from the ancestor chain and therefore forbidden by `P2`'s zero-derived-data pin; only the node's own declaration is written, and the loader re-resolves. And **an unrecognised value is a reader error naming the node**, never a fall-through to `"shared"` — silently widening a mistyped `"sever"` to Shared is a data leak on load, not a tolerance.

Rules that make byte identity deterministic rather than hopeful: `transform.p` is **always** written; `transform.r` is omitted iff exactly `[0,0,0,1]`; `transform.s` is omitted iff exactly `[1,1,1]`. A plane is `[nx, ny, nz, d]`, matching `System.Numerics.Plane`'s field order. **`faces` is indexed by PLANE index** (ruling `R‑3`), so `faces.Length == planes.Length` or the reader errors naming the node and the byte offset; a world-aligned face omits `u`/`v` entirely, which is already the engine's encoding (`FaceSurface` treats a zero axis as world-aligned). A fully default face is `{}`. Keyvalues are **string-typed on the wire** per `P5`'s pin.

**A script record has ONE axis, and it is not run location.** `"module"` is a bool, omitted iff `false`; there is no `"kind": "server" | "client" | "module"` string, because `realms.md` §6.2 puts run location on the *node's* `"realm"` — a client script is a script node declared `"realm": "client"`, and a `Shared` runnable script runs on the server, once. An earlier version of this section's example wrote `"kind": "server"`, which duplicated the realm on the payload and would have let the two disagree in the same record. The `.scmap` `SCPT` record's `u8 kind` field is correspondingly `0` script / `1` module, with the realm read from the node record's `PayloadFlags`, never from here.

**Scripts: exactly one of `source` or `path`.** `source` is a **JSON array of one string per line** — a JSON string with embedded `\n` is one unmergeable diff line, while an array of lines diffs exactly like the source file. `path` points at a real `.luau` file for `luau-lsp` and `--!strict`. Both cook to the same thing, so the runtime has one path.

**Editor metadata is confined to one key name, `"editor"`, at top level and per node, and the cook never reads it.** A structural rule beats a list. The subtle part is the split it forces: an *editor viewport* camera is `editor.viewport`; a *gameplay spawn* is `scene.spawn`. `P2` currently says the map holds "camera" without distinguishing, and conflating them is how a shipped game spawns wherever the level designer last parked the viewport.

**The RESERVED-KEY list lives here and nowhere else.** It is exactly `"editor"`, `"realm"` and `"state"`. These three names are **never captured by unknown-member preservation** and never round-tripped as opaque text: `"editor"` because the cook must be free to ignore it wholesale, and `"realm"`/`"state"` because a misspelling that survives as a preserved unknown member would load as no declaration at all — the node falls through to `Shared`/`Active`, which is a data leak rather than a lost setting (`realms.md` §2.5). Any document that needs the list **references this paragraph rather than restating it**; a second copy is how the two fall out of step. Growing the list is a format decision made here, in the same change that teaches the reader the new key.

**Unknown-member preservation, exactly.** At an unrecognised property name that is not on the reserved-key list above: record `long start = reader.TokenStartIndex`, call `reader.Skip()`, capture `utf8[(int)start .. (int)reader.BytesConsumed]` into an ordered per-node list; on write, replay with `WritePropertyName` + `WriteRawValue`. **Verified constraint that makes this work:** `WriteRawValue` documents that the writer's `Indented` and `Encoder` settings are *not* applied to raw content — it is emitted as-is. So a preserved value keeps the original file's indentation, which yields byte identity **only because a preserved member's nesting depth never changes**. That is a real invariant the reader must uphold, not an accident.

### 2.7 `.scmap` — the compiled map (binary, baked, lossy)

**Why custom, in one line:** it stores the artifacts the chunked CSG pipeline already produces — per-cell welded meshes, per-cell solid-leaf BSP trees, per-cell material runs — in exactly the shapes `Renderer.CreateMesh` and the BSP queries want, so a shipped game runs **zero CSG at load**.

**This supersedes `ROADMAP.md` `P11b`, which is now marked SUPERSEDED there** (the id is kept so cross-references resolve, and the entry records why it cannot simply be renamed). `P11b` specifies a binary *mirror* of the text map containing zero derived data, pinned by the test "binary-load → text-save → byte-identical to the original text map". That test is unsatisfiable for the artifact the user actually asked for: **welding, T-junction repair and per-cell carving are not invertible**, so `.scmap → .smap` is not a valid operation and must not be attempted. Two milestones each claiming to be the shipping map format is exactly the collision `ROADMAP.md` §3's cross-arc rulings exist to prevent. The correct guard replacing `P11b`'s pin is a **bake oracle**: cook → load → assert the loaded per-cell arrays are element-identical to a fresh `CsgWorld.Build(placements)` of the same source (§4.6). And a fact for the docs rather than a feature to engineer around: **`.smap` is the only editable artifact; a lost `.smap` is a lost map.**

```
Header — 64 bytes, offset 0
0x00  u8[4]  Magic = "SCMP"
0x04  u16    FormatVersion         MUST equal EngineInfo.CompiledMapFormatVersion
0x06  u16    HeaderSize = 64
0x08  u32    Flags                 bit0 HasBrushSource   bit1 HasScriptSource
                                   bit2 HasDebugInfo     bit3 Streamable
0x0C  u32    SectionCount
0x10  u128   SourceMapDigest       XxHash128 of the .smap bytes cooked from
0x20  u32    GeometryFormatVersion see §4.4 — a mismatch REFUSES the load
0x24  u32    MapFormatVersion      from the source map
0x28  u32    VertexLayoutId        FNV-1a over (semantic, componentCount) pairs
0x2C  u32    EngineVersion
0x30  u64    TotalSize
0x38  u64    Reserved = 0
0x40         Section table begins

Section table entry — 32 bytes
+0x00  u32   Kind (FourCC)
+0x04  u16   Version               per-section
+0x06  u16   Flags                 bit0 Compressed
+0x08  u64   Offset                absolute, 16-byte aligned
+0x10  u64   Size                  stored bytes
+0x18  u64   UncompressedSize      == Size when not compressed
```

**Unknown section kinds are skipped, not fatal** — that is what makes a future lightmap, navmesh or audio-occlusion section additive.

Sections: `STRT` strings · `ASTB` asset table · `META` map metadata and compile constants · `NODE` node graph · `CHDR` chunk directory · `CMSH` chunk meshes · `CBSP` chunk BSPs · `RGNI` region index (reserved, §4.5) · `BMDL` brush models · `BRSH` authored brush source (optional) · `ENTT`/`ECON` entities and connections · `SCPT`/`LUAB`/`LUAS` scripts · `NBND` per-node local bounds (optional).

**`STRT`** — `u32 count`, `u32 offsets[count+1]`, `u32 blobSize`, UTF-8 blob (not NUL-terminated). Index 0 is the empty string. Strings are emitted in **first-reference order during the canonical node walk**, never dictionary iteration order, which would leak the runtime hash seed into the file and break the two-process byte-identity test.

**`ASTB`** — `u32 count`, then 16-byte entries `{ u32 Kind, u32 PathString, u64 ContentHash }`. **`MaterialRef.Id` is NEVER written to disk.** `MaterialRegistry` hands out ids in per-process interning order and they are meaningful only for the life of the process (`Assets/MaterialRef.cs` documents exactly this). A cook that serialises `MaterialRun.Material.Id` produces a file that loads perfectly in the test that wrote it and mis-textures the entire world the moment a second map interns first — and the wrong version is *shorter code*, which is why this is a reviewed rule and not a note. Load walks the table, calls `MaterialRegistry.Intern(path)` in table order, and builds an `int[] fileIndex → MaterialRef` remap applied to every `MaterialRun`. `ContentHash` mismatch against the resident pack **warns**, never fails.

**`META`** — scene name, spawns (`{f32 px,py,pz; f32 rx,ry,rz,rw}`), then the compile constants: `f32 cellSize` (must equal `ChunkCoord.CellSize`), `f32 weldBand`, `f32 snapGrid` (must equal `VertexSnapper.GridSize`), `u32 regionSize`, `u32 bytecodeDebugLevel`, `u32 cookFlags`. All three floats are validated on load and refused loudly on mismatch: a runtime built with a different `CellSize` would mis-route every point and ray query against a directory built for another lattice, and the failure would look like sporadic collision bugs rather than a version problem.

**`NODE`** — `u32 nodeCount`, pad to 16, then fixed 80-byte records in **pre-order** (`SceneNode.Traverse()` order), so `parentIndex < selfIndex` always and one forward pass rebuilds the tree with zero fixups:

```
+0x00  16   Guid            Guid.TryWriteBytes(dst, bigEndian: true, out _) — RFC 4122,
                            so the bytes match the .smap hex order character-for-character
+0x10  u32  NameString
+0x14  i32  ParentIndex     -1 for root; INVARIANT ParentIndex < SelfIndex
+0x18  f32[3]  LocalPosition
+0x24  f32[4]  LocalRotation (x, y, z, w)
+0x34  f32[3]  LocalScale
+0x40  u16  PayloadKind     0 None · 1 StaticWorldBrush (geometry baked into chunks)
                            2 DynamicBrush · 3 BrushModel · 4 MeshInstance · 5 PrefabRoot
+0x42  u16  PayloadFlags    bit0 HasSource · bit1 IsEntityOwned · bit2 CanReCarve
                            bits 3-4 DeclaredRealm (2-bit: 0 inherit, 1 shared,
                                                    2 server, 3 client)
                            bits 5-6 DeclaredState (2-bit: 0 inherit, 1 active,
                                                    2 dormant, 3 invalid)
                            bits 7-15 free
+0x44  u32  PayloadIndex
+0x48  u64  Reserved = 0
+0x50       END
```

**`PayloadFlags` bits 3–6 carry the node's DECLARED realm and state, never the effective ones.** This is the full allocation of that `u16` and it is owned here; `realms.md` §2.5 cites these bit numbers rather than assigning its own. The 2-bit values are the enums' own numeric values (`NodeRealm.Inherit=0, Shared=1, Server=2, Client=3`; `NodeState.Inherit=0, Active=1, Dormant=2`), so the writer masks and shifts and nothing remaps — which is also why `3` in the state field is *invalid* rather than a fourth state, and why the enums may never be renumbered once content exists. **Do not confuse this record with `.sentdef`'s keyvalue `Flags` u32** (§3.2), which is a different record with its own allocation — bits 0–2 `readOnly`/`hideInEditor`/`requiresRestart`, bits 3–5 replication (`networking.md` §3.4), bits 6–7 per-property realm — and whose bit numbers deliberately do **not** line up with these. Two different records, two tables; the only thing they share is the word *realm*.

Storing the *effective* value here instead would be a silent correctness bug rather than a size saving: effective realm and state are an intersection down the ancestor chain, so a runtime reparent must be able to recompute them, and it cannot recompute from a value that has already been folded. The forward pass that rebuilds the tree resolves them for free — records are ordered `ParentIndex < SelfIndex`, so the parent's effective value is always already known when a child is read. A node whose `DeclaredState` reads `3` is a per-node load defect (`realms.md` §9 Q3), not a throw.

**Store the authored 10-float `Transform`, never a precomputed world matrix.** `SceneNode.WorldMatrix` is derived by composition; replaying the same composition reproduces bit-identical matrices, which is what `CsgCompileCache`'s exact-matrix-equality keying and the `--verify` recompile depend on. Storing a baked matrix would break the bake oracle in a way that looks like a floating-point mystery.

**Keep a node record for every authored node**, including static-world brushes whose geometry was dissolved into chunks; only the brush *geometry* payload is dropped. Nodes are ~80 bytes and dropping them saves nothing worth having while breaking identity: `SceneNode.Id` is what entity I/O, `Scene.TryFindById`, undo, prefab overrides and every Luau `workspace.Wall` reference resolve through, and `targetname` **is** `SceneNode.Name` per `P4`, so dropping wall nodes would silently break connection wiring that targets them.

**`CHDR`** — `u32 chunkCount`, pad to 16, then 64-byte records sorted by `ChunkCoord.CompareTo` (X→Y→Z): `{ i32 x,y,z; f32 renderBounds[6]; u32 meshOffset, meshSize; u32 bspOffset, bspSize; u32 regionIndex; u32 flags; u32 reserved }`. `renderBounds` is the cell's **true render AABB** (`ChunkMesh.RenderBounds`), not `ChunkCoord.Bounds` — a border-spanning brush is owned by exactly one cell and its surfaces routinely overhang, so culling by cell bounds makes the overhang vanish while visible. The sorted order is the pinned canonical order the oracles use, and it makes point lookup a binary search.

**`CMSH` chunk mesh blob** (each blob 16-byte aligned). **This mirrors the artifact the compile actually produces, which changed when `F1` landed**: `ChunkMesh` now carries `ChunkSubmesh[] Submeshes` — one entry per distinct face material, each with its **own self-contained, zero-based** vertex and index arrays, in **ascending material id** — precisely so the render thread never slices an index array at upload time and a render item stays "one mesh, one material, one matrix". `MaterialRun` survives only as the attribution of the monolithic `CsgWorld.BuildMesh` oracle and is **not** the render path. The file layout must mirror the artifact, or the loader reintroduces the slicing the engine deliberately removed.

```
+0x00  u32  submeshCount
+0x04  u32  vertexStrideFloats     8 today; 12 after R9
+0x08  u64  Reserved = 0
+0x10  ...  submesh directory: submeshCount × 24 bytes, ASCENDING assetIndex
              { u32 assetIndex;      index into ASTB
                u32 vertexCount;
                u32 indexCount;
                u32 Reserved = 0;
                u32 vertexOffset;    from the start of this blob, 16-byte aligned
                u32 indexOffset;     from the start of this blob, 16-byte aligned }
       pad to 16
       ... per submesh, in directory order:
              vertices  f32[vertexCount * vertexStrideFloats]   (16-byte aligned)
              pad to 16
              indices   u32[indexCount]                          (16-byte aligned)
              pad to 16
```

Every array is 16-byte aligned, so `MemoryMarshal.Cast<byte,float>` and `<byte,uint>` over the mapped view are legal and each submesh hands straight to `Renderer.CreateMesh` with zero copies and zero slicing — one GPU mesh per `(cell, material)`, which is exactly the swap granularity `Scene.ProcessStaticWorldCompilation` already uses. **Ascending material id is a total order over a value key**, so two compiles of the same cell emit the same submeshes in the same order; surfaces keep their compile emission order *within* a submesh. Both properties are load-bearing for the bit-identity contract and neither may be reordered by the writer. Uncompressed on purpose: compression and mmap-zero-copy are mutually exclusive, and geometry is where the bytes are.

A cell with no owned render geometry has no `CMSH` blob at all (`meshSize == 0` in `CHDR`), matching the compile, which produces no artifact for resident-only cells. The common case — every default single-material brush world — is exactly one submesh whose arrays are bit-identical to the pre-material ones, so nothing is duplicated in the file either.

*Honest caveat:* `Mesh` currently retains CPU-side positions/normals/indices, so the zero-copy property is real on the file→GPU path and only partial on the file→managed path until `Mesh` gains an opt-out CPU shadow.

**`CBSP` chunk BSP blob** — `u32 nodeCount`, `i32 rootIndex`, `u64 reserved`, pad to 16, then `nodeCount` nodes:

```csharp
[StructLayout(LayoutKind.Sequential)]
readonly struct FlatBspNode {            // 24 bytes
    public readonly System.Numerics.Plane Plane;   // 16 B: Normal.xyz + D
    public readonly int Front;                     // >=0 index, -1 empty leaf, -2 solid leaf
    public readonly int Back;
}
```

`BspNode` is a `sealed class` with `Front`/`Back` references; a 50k-part world's per-cell trees would be tens of thousands of GC objects to allocate and chase at load. Quake-style negative child encoding removes leaves from the array entirely, and a solid-leaf BSP is roughly half leaves, so the encoding costs nothing per leaf. **Query the flat form directly; never rehydrate.** Holding a real `Plane` field rather than four loose floats means the flat query calls the *identical* `Plane.DotCoordinate` the live tree calls, which makes answer-identity between `BspTree` and `FlatBspTree` a structural property rather than an argument about float evaluation order:

```csharp
int i = rootIndex;
while (i >= 0) {
    ref readonly FlatBspNode n = ref nodes[i];
    i = Plane.DotCoordinate(n.Plane, point) >= 0f ? n.Front : n.Back;
}
return i == -2;
```

Flatten order is pre-order DFS, front child first — a pure function of the tree. **Pin `Unsafe.SizeOf<Plane>() == 16` and `Unsafe.SizeOf<FlatBspNode>() == 24` as assertions**: `System.Numerics.Plane`'s field layout is overwhelmingly likely to be `{Vector3, float}` sequential, but that is not a documented contract, and this design casts raw file bytes into it.

**`BRSH` / `BMDL`.** `BMDL` bakes entity-owned brush geometry (`P7`'s brush models) in entity-local space using the **identical blob layouts as chunks**, so one reader serves both and the `BMDL` bake oracle is a copy of the chunk bake oracle — a door's geometry is derived data with a deterministic compile, and the only thing that changes at runtime is its matrix. `BRSH` retains authored planes and 48-byte `FaceRecord`s (material asset index, brush-local `uAxis`/`vAxis`, offsets, scales) for brushes that need to be re-carved at runtime, per-brush `keepSource` plus a cook-wide `--keep-brush-source`. Loading `BRSH` uses a validation-free `Brush.FromValidated(planes, faces)` that skips the O(n²) duplicate-plane rejection and the second `BuildFaces` boundedness probe, justified because the authoring path already validated them; debug builds re-run full validation and assert equality.

**The one silent-corruption hazard this format creates, named so it is a check rather than a discovery.** When baked chunks and `BRSH` are both present, a loader that helpfully calls `Scene.RebuildStaticWorld` produces a world where every wall is drawn twice — with z-fighting that every graphics programmer's instinct attributes to depth precision or a pipeline state bug, not to a map loader. Guard: the `IsStaticWorldBrush` flag, an explicit named contract (a flagged brush must never enter a live carve without first invalidating the chunks containing it), and a test asserting a `--keep-brush-source` cook draws the **same triangle count** as the same map without it.

**`ENTT`/`ECON`/`SCPT`.** Entities: `{ u32 nodeIndex, classNameString, kvStart, kvCount, outStart, outCount }` plus `{u32 keyString, u32 valueString}` pairs — string-typed on the wire, matching `P5`. Connections: `{ u32 outputName, targetName, inputName, parameter; f32 delay; i32 timesToFire }` with `-1` = infinite. Scripts: `{ u32 nodeIndex; u8 kind; u8 flags; u16 reserved; u32 chunkNameString; u32 bytecodeOffset, bytecodeSize; u32 sourceOffset, sourceSize; u32 reserved }`, with `chunkNameString` stored independently of `LUAS` so tracebacks still name the script when source is stripped.

**Scripts: source is the ground truth, bytecode is a cache.** Luau's own documentation is explicit that bytecode is *not* a durable storage format — the supported version range is bounded and old versions are dropped over time, and users are expected to recompile on upgrade. The safe design is therefore: `LUAS` (source, compressed) always present unless explicitly stripped; `LUAB` (bytecode) stamped with the Luau bytecode version and the vendored Luau commit id, validated on load, **falling back to compiling the source when the stamp mismatches**. `--script-source=strip` remains available for a shipper who accepts that the pack is then only loadable by the engine build that produced it. Whether the shipped runtime *also* links Luau.Compiler is a build property, not a format decision, and the format supports all four combinations deliberately because `docs/roblox-onboarding.md` §5 item 1 is explicitly unanswered.

### 2.8 `.svideo` — cooked video *(named, deferred, deliberately unspecified)*

**Purpose:** video playback — cutscenes, UI and menu video, and video textures sampled by a material like any other texture.

**Status: deferred, and specified nowhere in this document on purpose.** There is no video path in the engine today, no decoder, no dependency chosen, and no content that needs one. Writing a header for it now would repeat exactly the mistake §2.5 refuses to make with `.smaterial` — freezing a layout before the thing it serves exists — and unlike `.saudio`, whose 48-byte header is cheap insurance against a stub that at least has a referenced library behind it, a video container's shape is dominated by decisions this arc cannot make yet. The name is reserved so the format family is complete and so nothing else claims the extension; the design waits for a real need.

**The questions its eventual design must answer**, recorded now so the work starts from them rather than rediscovering them:

- **Container versus raw stream.** Does `.svideo` wrap an existing container (an MP4/Matroska/WebM/Ogg subset, the way `.simage` wraps KTX2) or store a bare elementary stream with a Spectra index in front of it? The `.simage` precedent says: prefer a restricted profile of an existing spec if one does the job, and be explicit about what the reader refuses.
- **Which codec, and is hardware decode assumed?** A software decoder is portable but expensive and drags in a dependency whose NativeAOT posture must be *verified*, never inferred (the standing invariant in §8). Hardware decode is cheap but platform-specific and forks per backend and per OS, which is a much larger commitment than a file format.
- **Audio-track interleaving with `.saudio`.** Either the audio track is a separate `.saudio` entry kept in sync by presentation timestamps, or it is interleaved inside `.svideo` and the audio path grows a second source. Both are defensible; the choice determines whether A/V sync is a mixer problem or a container problem, and it must be made before content exists.
- **How a frame reaches the GPU.** The obvious shape is that decoded frames become a GPU texture uploaded through the existing `PumpPendingUploads` pump on the render thread — the same seam `.simage` and the loose-PNG path already converge on, and the same rule that the render thread owns all GPU resource creation. Whether a per-frame upload at video rates is acceptable through that pump, or whether it needs its own ring of persistently-mapped staging buffers, is a measurement nobody has taken.

**What is already settled about it, because it is inherited rather than designed:** `.svideo` is a pack entry like every other asset (`Kind=Video`, reserved id 10 in §2.1), keyed by the same normalized content-relative source path, versioned by the cooked-format rule in §2's preamble (exact version match, refuse loudly with "recook"), and produced by a `scook` cook rule with the same determinism, cache-key and diagnostic-code obligations as every other rule in §4. None of that is optional and none of it needs the format to exist yet.

---

## 3. The runtime split: what is code, what is data

### 3.1 The tension, stated plainly

Under NativeAOT a C# type must exist at publish time. **So if game-specific entities are C#, the binary is per-game, and "one executable runs any map plus asset packs" is only true if no game-specific C# exists.** Three tiers resolve it.

**Tier 1 — compiled into `spectra.exe`, fixed at publish.** The engine spine (`Scene`/`SceneNode`/`Transform`/`SceneBvh`/`Camera`/`Frustum`), the whole chunked CSG+BSP pipeline, all three renderers and both pipelines, `RenderView`, `DebugDraw`, every format reader in §2, the Luau VM and its generated binding surface, `P4`'s entity machinery (`Entity`, the connection tuple, the min-heap scheduler, `TargetNameIndex`, `EntityWorld`, `EntityCatalog`), `P6`'s logic entities and `P8`'s triggers as engine-provided primitives — and one keystone type, **`ScriptedEntity`**.

**Tier 2 — pure data, shipped in packs.** Maps, assets, materials, **cooked shader blobs**, settings, input bindings, and **all Luau**: behaviour scripts, module scripts, and game-defined entity *types*.

**Tier 3 — "engine SDK" mode, opt-in and second-class.** A game references `SpectraEngine.Core` and publishes its own AOT binary with its own `[SpectraEntity]` classes. Necessary because some things are unreachable from Luau by construction — anything owning GPU resources (the render thread owns all creation), a new renderer backend, a new vertex layout, a new CSG stage, a new format reader, a new spatial index. **Document that list rather than letting it be discovered.**

### 3.2 A Luau-defined entity type gets the same editor experience as a C# one

This is the keystone, and it is where the no-code story either holds or fractures.

`P5`'s source generator emits, per `[SpectraEntity("func_door")]` class, five things: a keyvalue parse switch, an input dispatch switch, output declarations, a static `EntitySchema`, and a `[ModuleInitializer]` registering into `EntityCatalog`. The editor's property panel and I/O wiring UI are consumers of `EntitySchema` — nothing else. **So parity reduces to one sentence: a Luau entity definition must produce an `EntitySchema` of exactly the same shape and register into exactly the same `EntityCatalog`.**

Mechanism:

- A `.sent` file is Luau returning `Entity.define("game_pickup", { base = "point_entity", display = "Pickup", group = "Gameplay", keyvalues = { { name = "respawnTime", type = "float", default = "15.0", display = "Respawn Time", min = 0, max = 600 }, { name = "target", type = "targetname" } }, inputs = { "Enable", "ForceRespawn" }, outputs = { "OnPickedUp" } })`.
- `Entity.define` is a **host function**. It validates the table against a closed descriptor grammar (`KeyvalueType`, a closed enum in `SpectraEngine.Core`), builds an `EntitySchema` — the *same C# type* the generator emits — and registers a `ScriptedEntity` factory into `EntityCatalog`. From the engine's side, `game_pickup` is an entity class exactly like `func_door`.
- **`ScriptedEntity` is one compiled C# class and it is the whole trick.** `ParseKeyValue(name, value)` binary-searches the interned descriptor array and converts by the descriptor's declared type into `O3`'s closed `AttributeValue` union, storing into a slot-indexed array. `AcceptInput(name, …)` resolves an interned name to a slot and issues one `lua_pcall`. `FireOutput` goes through the unmodified connection machinery. **Zero reflection, zero runtime codegen — a sorted-array probe plus a `lua_pcall`** — so it is AOT-legal by construction.
- **Parity is structural, not aspirational.** There is exactly one `EntitySchema` type, one `EntityCatalog`, one on-disk `.sentdef`. The generator writes it from C# attributes; `Entity.define` writes it from a Luau table; **the editor reads only `.sentdef` and has no other input**, so the two producers cannot diverge. Pin it: define the same entity twice, once in C# and once in Luau, and assert the two `.sentdef` records are byte-identical apart from a one-byte `Origin` badge.
- **The map format is untouched by any of this**, because `P5` already pinned keyvalues as string-typed on the wire. An unknown classname still round-trips as `PlaceholderEntity`. That property is the real reason this works rather than merely being possible.

**`.sentdef` layout** — one pack entry per game, `Kind=EntityDefs`:

```
Header
0x00  u8[4]  "SENT"
0x04  u16    Version = 1
0x06  u16    HeaderSize = 16
0x08  u32    TypeCount
0x0C  u32    StringTableOffset
0x10  u32    StringTableSize        (strings: u16 length + UTF-8, addressed by u32 offset)

Per type (variable length)
+0x00  u32   RecordSize   INCLUDING this field — what lets an older editor SKIP a newer
                          record's trailing fields instead of failing
+0x04  u32   ClassNameRef
+0x08  u32   DisplayNameRef
+0x0C  u32   GroupRef
+0x10  u8    Placement    0 point, 1 brush, 2 abstract/logic-only
+0x11  u8    Origin       0 engine-C#, 1 luau, 2 sdk-C# — an editor BADGE only
+0x12  u16   KeyvalueCount
+0x14  u16   InputCount
+0x16  u16   OutputCount
then keyvalue, input and output records in that order

Keyvalue record — 32 bytes + a variable choice list
+0x00  u32   NameRef
+0x04  u32   DisplayRef
+0x08  u32   TooltipRef
+0x0C  u32   DefaultRef    a STRING reference, never a typed value
+0x10  u8    Type          closed KeyvalueType: bool, int, float, string, vec2, vec3,
                           vec4, color, angles, targetname, noderef, asset:model,
                           asset:material, asset:texture, asset:sound, choices, flags
+0x11  u8    Widget        0 auto, 1 slider, 2 assetPicker, 3 entityPicker, 4 color, 5 flags
+0x12  u16   ChoiceCount
+0x14  f32   Min
+0x18  f32   Max           NaN = unbounded
+0x1C  u32   Flags         bit0 readOnly, bit1 hideInEditor, bit2 requiresRestart
```

`DefaultRef` is a **string** reference and not a typed value, because `P5` pinned keyvalues as string-typed on the wire — if the schema's defaults were typed, the editor's "is this still the default?" comparison would become a type-conversion round trip and would drift silently.

**Two constraints the editor property panel must obey, both AOT-forced.** It is an `ItemsControl` over a `KeyvalueDescriptor` list with a `DataTemplateSelector`, one compiled template per `(Type, Widget)` pair. **No `DynamicObject`, no `ExpandoObject`, no dynamic property bag** — Uno documents that Expando and DynamicObject bindings "likely will not work" under Native AOT (verified 2026-08-21), and this is the exact place a conventional implementation reaches for one. The failure would appear only in the published build, after the developer stopped looking, which is the concrete justification for the `D0` CI publish gate.

**Engine-SDK mode must export `.sentdef`, not a bespoke JSON.** `P5`'s `--export-entity-schema` already exists to be pointed at this. One export format means the editor keeps exactly one schema consumer no matter how many games exist — the same invariant that makes the Luau path safe. SDK mode's costs, to be documented: a per-game binary (the "one exe" property is voluntarily surrendered for that game), a build host per RID because .NET AOT cannot cross-compile, an editor that sees those entities only through the export, and a **registration-scope object owning and reversing every engine-event subscription**, which must be designed *before* the C# extension API has users.

### 3.3 `game.spectraproj` — the project as data

Deliberately **not** a binary format: it is ~40 lines read exactly once, and a binary encoding would save microseconds while costing git-diffability on the one file a user most needs to hand-edit and merge. Authored JSON, hand-rolled `Utf8JsonReader` codec in the `P2` house style, unknown members skipped on read and re-emitted verbatim on save. Copied verbatim into the boot pack as a `Kind=Raw` entry.

Fields: `formatVersion` + `minimumReadableVersion` + `cookedWithEngineVersion`; `name`; `id` (Guid — save-folder and pack namespace); **`packs` (an ORDERED array; later entries win — that is the mod and patch story, free)**; `startupMap`; `defaultBackend` + `allowedBackends`; `display { mode, width, height, vsync }`; `input { actionName → bindings }` so Luau reads action names and never scancodes; `settings` (unknown keys warn and are preserved, matching `.spectramat`'s existing forward-compatibility rule); `bootScript`; `entityDefinitions` glob.

### 3.4 Boot path of a shipped game, process start to first frame

1. **Process start, OS thread.** `GlfwWindowing.RegisterPlatform()` and `GlfwInput.RegisterPlatform()` — **the first two engine calls, before `WindowOptions` is built.** Silk.NET discovers platforms by reflection, which trimming removes; neither string appears anywhere in the solution today (verified). Then argv: `--project`, `--map`, `--pack`, `--dev`, backend override.
2. **Locate the project:** `--project` → `game.spectraproj` beside the executable → a compiled-in default pack name → **fall back to today's loose-file `ContentRoot` mode**, which is a first-class runtime mode and what the editor's Play uses.
3. **Mount packs. No GPU yet.** Memory-map each, verify the TOC digest, insert into one priority-ordered resolver keyed on **the exact string `ContentRoot.NormalizeRelativePath` already produces.** *This is the single most important structural decision in the arc:* because the keys are identical, `AssetManager`, `MaterialParser`, `MaterialRegistry` and the shader loader need a **source swap**, not a rewrite — and loose files layering on top at highest priority gives hot-reload-over-a-cooked-build for free.
4. **Window + renderer — the existing path, unchanged.** `Window.Create` → `Initialize` → subsystem init → `CreateInput` → seed the framebuffer latch → `ReleaseContext` → start the render thread.
5. **Render thread, frame 0.** `Renderer.Initialize` → `AssetManager.AttachRenderer` → load **cooked** `.specshadecomp` blobs into `ShaderProgram`s (no compiler in the binary) → mmap the `.scmap` → walk `ASTB` and intern every material path **in table order**, building the `MaterialRef` remap → **create one GPU mesh per `(cell, material)` submesh directly from the mapped `CMSH` spans, running no CSG at all** → attach per-cell `FlatBspTree`s → two-phase entity load per `P9` (construct + keyvalues, *then* resolve connections) → resolve each submesh's remapped `MaterialRef` through `Scene.Assets` and queue texture uploads.
6. **Script boot.** `lua_State` + `luaL_sandbox`; register `.sentdef` schemas; run `.sent` definitions; run `bootScript`; run map scripts; `EntityWorld` spawn/activate.
7. **First frame** enters the existing loop.

Note the deliberate difference from the editor: the editor's load path *authors* nodes from `.smap` and calls `Scene.RebuildStaticWorld` (the synchronous cache-free path the async pipeline preserves precisely for load time); the shipped game's load path never carves.

### 3.5 What the AOT editor loses, and the mitigations

- **No compiled-C# hot reload.** `MetadataUpdater.ApplyUpdate` is feature-gated off under trimming/AOT and cannot add types or change signatures anyway. Editor C# change = rebuild + restart. *Mitigations:* the JIT debug configuration (§1); Luau covers all gameplay iteration; `O6`'s Command Bar covers "try something right now against the live edit-mode scene".
- **No runtime plugin assemblies.** `Assembly.LoadFile` is on Uno's own limitation list. Third-party editor extensions cannot be .NET DLLs. *Mitigations, in preference order:* **(a) editor plugins are Luau**, running in the editor's edit-mode VM with the node/selection/command-queue surface the Command Bar already needs — literally Roblox's plugin model, i.e. the pillar; **(b) plugin UI comes from the same closed `KeyvalueType` widget vocabulary the entity property panel already renders**, because **XAML cannot be loaded at runtime under AOT**, so a plugin can never ship a `.xaml`; **(c) out-of-process plugins over a JSON/stdio protocol** for anything the vocabulary cannot express — `SpectraShade.LSP` already proves that pattern here. **Honest limitation: no arbitrary plugin UI, ever.**
- **No embedded browser.** `CoreWebView2` does not work on Linux *or* Windows under Native AOT (macOS only), per Uno's limitation list. No embedded docs browser, no web-based node-graph editor, no in-app release notes. Open the system browser. Decide this before anyone designs a help panel.
- **No cross-compilation.** Two CI hosts minimum, plus one per shipped RID.

---

## 4. The cook pipeline

### 4.1 Shape: one library, one CLI, one tool

**`SpectraEngine.Cooking`** (library: rules, dependency graph, writers) plus **`SpectraEngine.Cooking.CLI`** producing **`scook`**, mirroring `SpectraShade.Compiler` + `ssc` exactly — same `CliOptions`/`ParseResult`/`AnsiStyle`/`DiagnosticWriter` shape, same exit codes (0 success, 1 cook error, 2 usage, 3 I/O), same MSBuild-parseable stderr form `<file>(<line>,<col>): error SC####: message`. Pack **readers** live in `SpectraEngine.Core/Assets/Packs/`; pack **writers** and all cook rules live in the tool-only library, so no shipped game binary carries pack-writing code.

The library split is not cosmetic: **the editor must run the cooker in-process** for cooked-accurate preview and for the cooked-only validation mode, and a CLI-only cooker cannot do that.

**Two consequences that resolve a real disagreement between designs.** Because the editor hosts the cooking library and the editor is AOT, **the cooking library is bound by AOT-safe dependencies regardless of whether the CLI is AOT-published** — so BCnEncoder.NET (pure managed, no native dependency, permissive licence) rather than a native ISPC/`bc7enc` encoder as the baseline, and in-box compression rather than a vendored codec. A native encoder remains legal as an opt-in `--encoder native` escape hatch for cook throughput, because the Luau decision already commits the project to per-RID native build machinery; it is never the baseline, because the editor must be able to encode without it. And **there is one cook tool, not three**: map compilation is a cook rule inside `scook`, not a separate `smapc`. `ssc` stays separate because it compiles one shader and has a different argument grammar; a whole-project build tool must not be conflated into it.

**Verbs and options.** `scook cook <projectDir>` (default) · `scook verify <pack|scmap>` · `scook inspect <pack>` (entries, sizes, codecs, names — the tool that makes the format debuggable) · `scook clean`. Options: `-o/--output`, `--profile <ship|fast|preview>`, `-t/--target <backend>` (same grammar and same `opengl,d3d11,d3d12` default as `ssc`), `-j/--jobs` (`-j1` is the determinism-oracle mode), `--cache`/`--no-cache`, `--loose` (emit a cooked directory tree instead of a pack — the overlay input for editor preview), `--keep-brush-source`, `--script-source={embed|strip}`, `--encoder <managed|native>`, `--strict`, `--watch` (implies `--loose`), `--manifest <path>` (a JSON cook manifest of every asset, its id, its inputs and its output hash — what CI diffs), `-q`, `--no-color` (honouring `NO_COLOR`), `--version`.

**Diagnostic codes: reserve `SC####`**, mirroring `F4`'s `SS####` scheme: 0xxx project/CLI · 1xxx discovery and dependency graph · 2xxx image/texture · 3xxx model · 4xxx audio · 5xxx material · 6xxx shader (**wrapping `SS####` codes, never renumbering them** — a shader error surfaced through the cooker must be the same code the LSP and `ssc` report) · 7xxx map/geometry · 8xxx script · 9xxx pack writing and integrity. Retired codes are never reused.

### 4.2 The cooker is the loud gate; the runtime stays the soft landing

`CLAUDE.md` pins that content errors never reach the draw loop: a missing material degrades to `AssetManager.DefaultMaterial`, a missing texture to the magenta placeholder, each with a warning. That is exactly right for a running frame and exactly wrong for a build step whose job is to stop broken data shipping. **Write the asymmetry down so nobody "fixes" one to match the other.**

**Cook-fatal:** a `.spectramat` naming a nonexistent texture or shader; a brush node whose world transform is non-rigid (report `Scene.DescribeNonRigidDefect`'s message, naming the node); a plane set `Brush`'s constructor rejects; a Luau syntax error; a shader that fails for any requested backend; a duplicate `SceneNode` Guid; a pack asset-id collision; a `faces.Length != planes.Length` mismatch.

**Warning by default, error under `--strict`:** a connection naming a target that does not exist (`P9` explicitly requires it be *kept* and warned about — a mapper who renames a door must not silently lose wiring); an unknown entity classname; an unknown `.spectramat` key (the parser deliberately warns so files stay forward-compatible).

### 4.3 Incremental and deterministic

**Cache key** — `XxHash128` over a canonical byte stream, no separators beyond those listed, every integer little-endian:

```
"SCOOK\0" || u32 CookerVersion || u32 RuleKindId || u32 RuleVersion
 || u32 settingsLength || canonical settings bytes   (sorted key\0value\0 pairs —
                                                      sorted so dictionary order cannot leak)
 || u32 toolVersionCount || per tool: name\0 + u32 version
      (BCn encoder assembly version, compressor version, EngineInfo.ShaderFormatVersion,
       MapFormatVersion, GeometryFormatVersion, Luau bytecode version)
 || u32 inputCount   || per input IN DECLARED ORDER: u128 hash(path) + u128 hash(contents)
 || u32 missingCount || per missing probe: u128 hash(path)
```

**Content hashes are the truth; an mtime+size stat cache only short-circuits re-hashing.** That makes a `git checkout` (which rewrites timestamps without changing bytes) a no-op instead of a full rebuild, and makes a revert-to-identical-content detectable — both of which timestamp-only invalidation gets wrong. `System.IO.Hashing`'s `XxHash128` is Microsoft-shipped with a span API; a build cache does not need collision resistance against an adversary, but 128 bits rather than 64 matters for the patch case, where a false "unchanged, skip it" ships a corrupt patch.

**Cache layout:** `.spectra-cook/cas/<2 hex>/<30 hex>` holding uncompressed cooked payloads, plus `graph.bin` with one record per rule (key, output hash, resolved dependencies, missing-probe list) and the stat cache.

**Dependencies are discovered by parsing, never guessed, and negative dependencies are recorded.** `MaterialParser` already yields the shader reference and every `texture <sampler> = <path>`; the `.smap` reader yields material/model/script references; `S4`'s import resolver will yield the shader import closure once it exists (today `import` parses and is ignored, so shaders are leaves — say so rather than pretend). Luau `require` is deliberately *not* statically resolved: `O8` keys module require on `SceneNode.Id`, so requires are runtime graph lookups, not file paths; scripts are leaves and the map holds the edges. **Negative dependencies — the paths a rule probed and did not find — are the non-obvious piece:** without recording that `wall.spectramat` looked for `Textures/wall_brick.png` and did not find it, adding that file later never invalidates the material, and `--watch` silently serves a broken cook while reporting success. This is the single most common incremental-build bug and it costs one list per rule to avoid. Make it structural: the rule API takes inputs through an explicit `IRuleContext.Read(path)`/`Probe(path)` that records every access, so **the declared set IS the accessed set by construction**, not by author discipline.

**Parallelism is level-synchronous over the topologically sorted DAG**, `Parallel.ForEach` bounded by `-j`, with **results written into a pre-sized array indexed by rule index, never appended in completion order** — the same reasoning `ChunkMesh` uses when it documents that submeshes are emitted in ascending material id and surfaces keep their emission order within a submesh, neither ever reordered. That is what makes the pack's byte layout independent of scheduling. **Diagnostics are buffered per rule and flushed in rule order**: `Console.Error.WriteLine` from N workers tears lines apart, and the CLI's entire diagnostic contract is that each line is IDE-parseable, so this is a correctness requirement of the output format, not polish. The map rule is itself internally parallel (CSG) and memory-heavy, so it draws from a shared concurrency budget rather than running as one more peer task.

**Determinism rules, exhaustively.** (1) Chunk directory in `ChunkCoord.CompareTo` order. (2) Node records in `SceneNode.Traverse()` pre-order — the same order that drives the placement list and therefore carve order. (3) Strings and asset entries in first-reference order during that walk, never dictionary iteration order. (4) BSP flatten pre-order, front-first. (5) No timestamps, absolute paths, machine names, culture-dependent formatting, or `Guid.NewGuid` anywhere in the cook; prefab GUIDs come from `P10`'s fixed hash into a v8-shaped Guid, **never `string.GetHashCode`, which is process-randomised**. (6) All padding explicitly zero-filled — an unzeroed `reserved` field picks up stack garbage and breaks the two-process test in a way that is very hard to bisect. (7) The map cook uses the **cache-free** `CsgWorld.Build(placements)` overload, never the incremental `Build(placements, dirtyCells, previousWorld)` — a build artifact must not depend on build history. (8) Determinism is contracted over the **uncompressed** payload; whole-file byte identity holds because v1's geometry codec is `None`.

**Three oracles, in the repo's existing style:** clean-cook twice, in two processes → byte-identical pack; cook-from-cache versus cook-from-clean → byte-identical; `-j1` versus `-jN` → byte-identical. Plus, for the BC encoder specifically: encode twice and compare bytes, and encode at `-j1` versus `-jN` and compare bytes. BC blocks are independent so parallel encoding *should* be deterministic — but no source consulted states it, and the whole cache rests on it, so a ten-line test answers it definitively before the library is committed to.

### 4.4 The version gate that stops a stale pack rendering garbage

A `.scmap` or `.smodel` embeds whatever `ChunkMeshBuilder` emitted at cook time, and that output demonstrably changes: **`F1` already reshaped it once** — `ChunkMesh` went from one vertex/index array with `MaterialRun` slices to per-material `ChunkSubmesh` arrays — and **`R9` will reshape it again**, taking `VertexAttribute.StandardLayout` from 8 to 12 floats. Any pack cooked before either change would be unreadable by a runtime after it. Without a gate, the failure is a misinterpreted vertex buffer — garbage or nothing on GL, and possibly a validation failure on D3D11, which bakes input layouts from the lit shader's VS bytecode at mesh creation.

The gate: **`EngineInfo.GeometryFormatVersion`, bumped by hand whenever CSG or vertex-layout output can change, included in the cook key AND stamped in the `.scmap`/`.smodel` header AND enforced at load with a message naming both values.** Plus `VertexLayoutId` (FNV-1a over the layout's `(semantic, componentCount)` pairs) so a mismatch can be reported precisely rather than as a generic version bump. This must be in v1; retrofitting it means every existing pack is unversioned.

### 4.5 Streaming — what is delivered and what is not

**Delivered:** per-asset streaming. Textures already stream (async decode, `PumpPendingUploads`); extend to pack-backed streams, where mmap makes the read nearly free. Audio streams naturally.

**Not delivered, and it must not be promised:** map and world streaming. **The chunk grid is a *compile* partition, not a *residency* partition.** The static world compiles from the full placement list; a partially-loaded placement list produces a different-but-valid world and quietly invalidates the premise of the chunked-versus-monolithic equivalence oracles. The honest v1 is whole-map load plus per-asset streaming.

The `RGNI` section is therefore **reserved, not built**: an 8×8×8-cell region index (`{i32 rx,ry,rz; u32 chunkListStart/Count; u64 blobOffset/blobSize; u32 assetListStart/Count}`) with `CMSH`/`CBSP` laid out region-major and chunk-canonical within region, so one region is one sequential byte range. Reserving the layout is nearly free; building the streamer is a separate hard design. Two hazards to record now so they are not discovered later: **an mmap page fault landing on the render thread is a synchronous disk read mid-frame** — invisible on a warm page cache during development, catastrophic on a cold install — so it must be *structurally* impossible (prefetch off-thread, then marshal), not merely avoided by convention; and **BSP data must stay resident even when meshes evict**, because a collision query into a non-resident region silently answers "empty", which is a player falling through the floor of a room they can see. At 24 bytes per internal node with zero-byte leaves, full BSP residency is cheap enough that this is not a real tradeoff.

### 4.6 Dev-mode loose mounting versus shipped pack mounting

One seam, three implementations:

```csharp
public interface IContentSource {                 // read-only, thread-safe, no GPU knowledge
    int  Priority { get; }
    bool TryOpen(string normalizedPath, out ContentBlob blob);   // false on miss; never throws
    bool Exists(string normalizedPath);
    bool TryGetWatchPath(string normalizedPath, out string absolutePath);  // packs return false
}
public readonly struct ContentBlob : IDisposable {
    public ReadOnlySpan<byte> Span { get; }   // INTO the mapped view when Codec=None,
                                              // else into a pooled array
    public void Dispose();                    // returns the pooled array; no-op for a mapped span
}
```

`LooseFileSource` (today's `File.Exists` / `ImageDecoder.DecodeFile` / `MaterialParser.ParseFile` path, refactored out of `AssetManager` with **zero behaviour change**), `PackSource` (mmap'd `.spack`), `OverlaySource` (priority-ordered, first hit wins). `ContentRoot` keeps normalization and developer-build detection; it stops being the only way to reach bytes. `FileSystemWatcher` hot reload stays attached to the loose source only — you cannot watch a pack — and in pure-pack mode `AssetManager.HotReloadEnabled` is forced false **with an explicit log line** rather than silently no-opping.

**Editor:** loose files at top priority, packs beneath. An artist drops a PNG and it shadows the cooked `.simage` with no rebuild. **Shipped:** packs only, loose mounting opt-in and logged. **The engine logs the mounted source stack, in order, at startup** — nearly free, and it kills an entire class of "works on my machine".

**The one sharp edge in the refactor:** `AssetManager` reaches the filesystem in three separate places today (`LoadTexture`, `BindTextureSlot`'s own `File.Exists` probe, and `LoadMaterial`). All three must move together, or a missing-file check disagrees with the open and material textures silently never resolve from a pack — and the failure is *quiet* (magenta placeholder plus a warning), which is exactly the kind of bug that ships.

**Cooked-only validation, in three layers, and it ships early rather than last.** (1) `scook verify <pack>`: every entry decodes, every reference resolves inside the pack, the digest matches, every material's textures are present, every shader has a blob for every target backend; on a `.scmap` with `--keep-brush-source`, additionally recompile from the retained authored brushes and assert bit-identity against the baked artifacts — the chunked-versus-monolithic oracle applied to the ship artifact. (2) An editor **Validate Cooked** action that mounts `PackSource` alone with `StrictMode = true`, where any resolve miss throws instead of degrading — without it the editor silently falls through to the loose file and validates nothing. **Strict mode is a property of the source stack, not of `AssetManager`**, whose degradation behaviour is a pinned engine invariant that must not become conditional. (3) A CI job running cook → verify → the existing Bsp/asset test suites against a cooked pack rather than the loose tree; the test projects already run headlessly against `FakeRenderer` and would need only a source-stack parameter.

**One residual honesty gap:** a BC7-encoded texture is lossy and the loose PNG path is not, so identical asset *identity* does not guarantee identical *pixels*. That is what block compression means. The mitigation is a cooked-accurate preview mode (`scook --watch --loose` overlaid on the loose tree), not a claim that the two modes are pixel-identical.

---

## 5. Uno Native AOT — settled in practice

**This section is marked separately because it bears on a user decision (both editor and game AOT) and because it changes the premises of `ROADMAP.md` `H2` and `H3`. The decisive evidence is the user's own first-hand result; the supporting documentation below was read on 2026-08-21 and is dated.**

### 5.1 The evidence

**First-hand and decisive: the user has already Native-AOT-published an Uno application and shipped it.** It ran on a PLC — **ARM Linux, in a 1 GB RAM environment**. On every axis it shares with this arc's targets, that is a *harder* environment than any of them: a non-x64 architecture, a memory budget an order of magnitude below any desktop the docs measured, and a Linux head. A working build by a practitioner outranks any inference drawn from documentation, and this section now rests on it rather than on a reading. **Uno + NativeAOT works.**

**The published documentation agrees, and supplies the numbers, the mechanism and the prerequisites:**

- Uno's dedicated Native AOT page: *"Uno Platform 6.6 introduces Native AOT support across Android, iOS, Linux, macOS, and Windows."* Enable with `<PublishAot>true</PublishAot>`; `<IsAotCompatible>true</IsAotCompatible>` also recommended.
- Uno's per-platform measurements blog, **published 2026-08-06**: **Windows Desktop 1,605 ms → 824 ms (49% faster)**; **Linux Desktop 870 ms → 350 ms (60% faster)**. Windows requires *"the Visual Studio C++ desktop workload, because Native AOT uses the platform linker and C++ static runtime libraries"*; Linux requires *"the native toolchain listed in the .NET prerequisites"*. Publishing is per-platform-and-architecture, scoped with `-p:PublishRid` rather than a global `-r`. Those toolchain prerequisites are a CI provisioning item, not a risk — `D0`'s two-host matrix has to stand them up regardless.
- Binding survival is **source-generated, not reflective**: Uno preserves public properties of `[Bindable]` types and the property references used by XAML binding expressions automatically at build time — the same mechanism this engine's AOT rule already sanctions.

**Scope, stated precisely, because the proof and the residual doubt are about different platforms.** The user's shipped result is **Linux on ARM**. Say plainly what it does and does not establish: it is first-hand evidence about the Linux head, the X11/Skia path and the ARM native toolchain, and **it is not by itself evidence about the Windows Skia head**, which uses a different shell, a different windowing stack and a different platform linker. What it retires is the general doubt — *does an Uno app survive NativeAOT at all* — which was the only doubt large enough to reopen a decision. What it leaves is narrower and per-head. The one documentation contradiction the research turned up is about **Windows**: Uno's Skia Desktop page still carries the sentence *".NET Native AOT on Windows is not yet supported as WPF does not support it at this time,"* and the same page describes `net10.0-desktop` as the standard target and Skia Desktop on Windows as using a **WPF shell internally**. Uno Skia has two Windows heads — the legacy Skia+WPF shell, and a **Win32 shell** selected via `.UseWin32()` in `Platforms/Desktop/Program.cs`, which Uno 6.4's release notes describe as receiving desktop-chrome work — and Uno's native-element-hosting page lists both a `Win32NativeWindow` (wrapping an `Hwnd`) and a WPF `System.Windows.UIElement` as hosting targets, corroborating two distinct heads. **The stale sentence is scoped to the Skia+WPF head; the 6.6 blog's measured Windows Desktop numbers are the Win32 head.** That last step is still a reading rather than a quote — which is why §5.2 keeps one cheap confirmation on the Windows side, and nothing more.

### 5.2 The verdict

**Settled. Editor AOT and game AOT are both achievable, no fallback plan is required, and nothing downstream in this document is contingent on the answer.** Linux is proven by the user's own build, under conditions stricter than this engine will ever face. Windows is a **confirmation, not a risk**: `D1` still runs one `dotnet publish -f net10.0-desktop -p:PublishAot=true` against a hello-world Uno head on Windows, because the stale doc sentence is unretracted and a single publish costs an afternoon. If that publish complains, the known remedy is to **force the Win32 head explicitly (`.UseWin32()`) and retest** — which keeps every decision in this document intact. There is no scenario in play that reopens the editor's UI framework or its process model.

### 5.3 The bigger finding: native element hosting deletes most of `H2` and `H3`

Uno Skia supports embedding an **app-owned** native window in the XAML tree: `Uno.UI.NativeElementHosting.Win32NativeWindow` (wrapping an `Hwnd`) and `X11NativeWindow` (wrapping an `XID`), set as `ContentControl.Content`. The docs are explicit that *"the app developer is responsible for creating the native element"* and that on Win32 you *"create a native Windows window first, get its `Hwnd`, and then create a `Win32NativeWindow` instance with that `Hwnd`."*

Consequences, and they are large:

- **`H2`'s premise is wrong for this stack.** `ISwapChainPanelNative` + `CreateSwapChainForComposition` is a WinUI3/WinAppSDK mechanism; Uno Skia's Win32 head has no `SwapChainPanel` to bind. The engine keeps `CreateSwapChainForHwnd` against a child HWND it owns. `H2` shrinks from *"hand-declared COM interop, size L, the single biggest unknown in the arc"* to *"create a child window, wrap it, forward resize into the existing `Renderer.SetFramebufferSize` latch"* — and that latch is already exactly the right shape; only the resize *source* changes.
- **`H3`'s readback fallback becomes unnecessary.** `X11NativeWindow` means the engine keeps its own GLX/EGL context on its own X11 window. No `glReadPixels`, no `GRBackendTexture` share group, no per-frame 8 MB copy. The roadmap's "highest risk item, could stall indefinitely" becomes the same shape as Windows.
- **`R3` (offscreen render targets) comes off the editor's critical path on *both* platforms**, not just Windows. `R3` remains required for shadows, post, preview thumbnails and multi-viewport — but it no longer gates "a person can edit a level on Linux".
- **Embedded OpenGL on Windows becomes trivially supported**, which flips `ROADMAP.md` §11 sign-off 3.
- **Airspace is the price, and it is a real design constraint.** Uno documents that *"native elements don't alpha-blend"* and that *"as of Uno Platform 6.0, setting the opacity of native elements is not supported on X11"*. Therefore **all viewport overlay UI — gizmos, selection outlines, marquee, snap indicators — must be drawn by the engine inside the viewport** and never as XAML floated over the surface. This is not a compromise: `E1`/`E3` already specify `DebugDraw`-based gizmos and unprojected world-space marquee lines precisely because the engine has no render targets, so the constraint and the existing plan agree. Uno also warns that *"focus and pointer/keyboard input work as expected most of the time"* with platform quirks — `E1`'s host-agnostic `EditorInputFrame` seam is exactly the right insulation; keep it.
- **macOS native hosting is documented as "not yet supported" as of Uno 6.0.** If macOS ever becomes a target, the offscreen-render-and-upload path this design deletes comes back. `GLCanvasElement` is the documented alternative but it renders on Uno's UI thread inside Uno's own GL context, which collides head-on with "the render thread owns the GL context" — reject it for the main viewport; it is the right tool for `S7` material-preview thumbnails.

---

## 6. Milestones

New prefix **`D`**, non-colliding with `F`/`E`/`P`/`S`/`R`/`H` (`ROADMAP.md`) and `O0`–`O9` (`docs/roblox-onboarding.md`). Dependency-ordered. Sizes are relative.

| id | Milestone | Size | Depends on | Slots into `ROADMAP.md` |
| --- | --- | --- | --- | --- |
| **D0** | AOT publish gate, reference hygiene, two-host CI | S | — (extends `O0`) | Before everything; parallel to Phase 0 |
| **D1** | Uno Windows-AOT confirmation + native-hosting spike | S | — | Before arc `H` commits; **rewrites `H2`/`H3`** |
| **D2** | `IContentSource` seam + `LooseFileSource` | S–M | — | Phase 0, beside `F2` |
| **D3** | `.spack` container: writer, mmap reader, mount stack | M | D2 | After Phase 0 |
| **D4** | Cook spine: `scook`, CAS cache, dependency DAG, oracles | M–L | D3 | After D3 |
| **D5** | Cooked-only validation + CI gate | S | D3 (grows with D6–D12) | **Immediately after D3/D4, not last** |
| **D6** | `.simage` (KTX2 profile), BC cook, row-order + sRGB decision, `TextureUploadDesc` | M–L | D4; adjacent to `R2` | With/next to `R2` in arc `R` |
| **D7** | Shader cook into packs; shipped game drops the compiler | S–M | D3; sequence around `S3` | Arc `S`, before or after `S3`, never concurrent |
| **D8** | Material cook + cross-asset validation gate | S–M | D4, D6, D7 | `F1` has landed — unblocked |
| **D9** | `game.spectraproj` + data-driven boot path | M | D2, D3; `P2` for the map | After `P2` |
| **D10** | Flat BSP: `FlatBspNode`, flattener, `FlatBspTree`, oracle | S–M | — | **Unblocked today; start early** |
| **D11** | `.smap` canonical-writer spec + `.gitattributes` | S | rides **`P2`** | Inside `P2`; not a separate milestone |
| **D12** | `.scmap` container v1 + `scook` map bake | L | D4, D10, D11 (`F1` landed) | **Supersedes `P11b`**, which `ROADMAP.md` now marks superseded rather than schedules |
| **D13** | Cook determinism suite + bake oracle | S–M | D12 | Immediately after D12 |
| **D14** | `KeyvalueType`/`EntitySchema` in Core + `.sentdef` writer | M | `P4`, `P5`, `O3` | Arc `P`, after `P5` |
| **D15** | `ScriptedEntity` + `Entity.define`: Luau entity types | L | D14, `O4`, `O5` | After `O5` and `P5` |
| **D16** | Editor property panel + I/O wiring from `.sentdef` only | M | D14, `H1` | Arc `H`, after `H1` |
| **D17** | `.smodel` + glTF/GLB importer | L | D4; wants `R9` landed | Parallel track; after `R9` if possible |
| **D18** | `.saudio` v1 (PCM16) + a real `AudioManager` | L | D4 | Fully independent parallel track |
| **D19** | `.smaterial` cooked materials | S–M | **`S3` (hard block)**, D6, D8 | Arc `S`, strictly after `S3` |
| **D20** | Patch packs, mod packs, mount-order policy | M | D3, D4 | Only if §7 question 2 is "yes" |
| **D21** | Engine-SDK mode + Luau editor plugins | M–L | D14, `P5`, `O6`, `E1` | Late; after the editor exists |
| **D22** | `.svideo` — **deferred, unspecified** | — | D4, plus a verified decoder | **Not scheduled.** Name reserved only; see §2.8 |

**Notes on the sharpest ones.**

- **`D0`** extends `O0` rather than replacing it. `O0` owns proving one AOT publish and inventorying warnings. `D0` adds what `O0` does not name: `<IsAotCompatible>true</IsAotCompatible>` on **both** `SpectraEngine.Core` and `SpectraShade.Compiler` (both are referenced by the AOT'd executable; only the exe is analysed today, verified — `PublishAot` appears on `SpectraEngine.Executable` line 5 and `SpectraShade.Compiler.CLI` line 8 and nowhere else); the two Silk platform registration calls; **removing five unconsumed Silk.NET package references from Core** — `Silk.NET.Assimp` (native per-RID library, no importer code exists), `Silk.NET.OpenCL`, `Silk.NET.Vulkan` (no Vulkan renderer exists), `Silk.NET.XInput`, and the umbrella `Silk.NET` metapackage, each publish weight and warning surface for zero value; hardening `BaseShaders.ReadEmbedded` from `GetManifestResourceNames()` + LINQ suffix match to a constant resource name; and standing up the **two-host CI matrix**, which is mandatory because .NET AOT cannot cross-compile. One thing to know before someone debugs it: `ContentRoot.ResolveCore` and `BaseShaders.TryFindSourceRoot` both key on finding a `*.slnx` above `AppContext.BaseDirectory`, so **an AOT-published developer build silently loses asset and shader hot reload**.
- **`D5` ships early, not last.** Every milestone after it is another opportunity for the loose and packed paths to drift, and it is the only thing that catches drift. Low cost to build, high value.
- **`D7` also fixes a latent bug.** `ShaderFileReader.ReadPipeline` recomputes the data-section start as the literal `8 + (pipelineCount * 12L)` (line 100) while `Read` uses `stream.Position` — two expressions of one layout that *will* diverge the first time the header grows, and `S3` explicitly plans to grow it with a `manifestSize` field. Consolidate the layout constants **before** `S3`, or the "v1 still loads" branch `S3` promises is built on a wrong offset. `D7` also adds `ReadPipeline(ReadOnlySpan<byte>, GraphicsBackend)` so a mapped pack entry is read without a `MemoryStream`, and teaches the cooker to strip non-target backends for a single-backend ship build — a pure size win requiring no reader change, which is the sign the format was designed right.
- **`D10` first among the map work.** It is pure `Bsp/` work with no format, no I/O and no renderer, it de-risks the largest unknown in `.scmap`, and it can be built and proven before anything else exists. Its one sharp edge: `TraceSegment`'s entry-normal bookkeeping and the `>= 0f` sidedness convention must transcribe index-for-index; a sign flip is a silent wrong-normal that shows up only as sliding along the wrong surface.
- **`D12` is the largest and highest-risk.** Three named hazards: writing `MaterialRef.Id` instead of an `ASTB` index (works in every single-map test, mis-textures the world when two maps load in a different order — test it by interning an *unrelated* material before loading, or the bug hides behind a coincidentally matching order); a blob landing at a non-16-aligned offset (the layout pass and the write pass must agree on every size *including padding*, and a one-byte disagreement corrupts every later section with an arbitrary symptom); and the double-geometry hazard of §2.7.
- **`D17` has an unpredictable tail** that is not the format: choosing an importer. Assimp is a native library the editor cannot safely carry under AOT; SharpGLTF's NativeAOT/trimming posture is unverified. If neither survives an AOT publish spike, the importer becomes cooker-CLI-only and the editor loses direct glTF import — a real product consequence worth discovering before `D17` starts, not during it.
- **`D19` is hard-blocked on `S3`** and must not be started early, for the reason in §2.5.
- **`D20`'s value depends entirely on §7 question 2.** If shipped games are frozen, the mount table collapses to base + patch and `D20` mostly drops off the plan — though tombstones and the priority bands stay as cheap insurance in the format.

---

## 7. Decisions that need you

Each of these is genuinely open, blocks something concrete, and is not settled elsewhere.

1. **One pack per game, or per-area/per-chunk packs?** One pack is simplest and dedups best; many packs enable partial download and eventual world streaming — but the map format has no notion of asset locality yet, so there is nothing to partition on. Answer before `D4`'s rules file hardens.
2. **Must a shipped game load mod or patch packs at all?** Yes makes `D20` real and makes tombstones and priority bands load-bearing; no collapses the mount table to base + patch. This is the same unanswered question as `docs/roblox-onboarding.md` §5 item 1 (editable scripts in shipped games) and should be answered once for both.
3. **Does a shipped game ever re-carve brushes at runtime** (destructible geometry, in-game editing, scripted resize)? Yes makes `.scmap`'s `BRSH` section mandatory and requires designing the invalidation contract properly; no makes `--keep-brush-source` a debug flag and shrinks ship packs substantially.
4. **Does the editor's Play mode run against loose files or a cooked pack?** Loose is instant and makes iteration bearable; cooked is what the player actually runs, and testing only the loose path means cooking bugs ship. A "Play cooked" command that cooks-then-runs is the obvious middle, at the cost of a cook per playtest.
5. **Is removing runtime shader authoring from shipped games acceptable?** `D7` drops the SpectraShade compiler and the `d3dcompiler_47.dll` dependency from the game binary and cuts per-launch compile time; it also means a shipped game can never compile a shader, which forecloses shader modding.
6. **GL versus D3D texture origin: which backend is currently correct, and where does the fix go?** Options are flipping V in UV generation (`FaceSurface`'s V axis and `ChunkMeshBuilder`), flipping in the shader, or flipping per backend at upload. This changes existing loose-PNG rendering on at least one backend and blocks `D6` — and it genuinely cannot be answered from the code, because nothing in the tree establishes ground truth. Someone needs to look at an asymmetric texture on all three backends.
7. **What is the project audio sample rate, and is 1 Spectra unit = 1 Roblox stud?** Both are content decisions that cannot be changed after a library or a map corpus exists. The unit question overlaps `ROADMAP.md` §11 item 13 and `docs/roblox-onboarding.md` §5 item 10 — answer all three together.
8. **Rename the landed shader extensions into the `s*` family now** (`.spectrashade` → `.sshade`, `.specshadecomp` → `.sshadec`)? The authored material keeps `.spectramat` either way: the short material name in the family is `.smaterial`, §2.5 has already given it to the cooked form, and the authored and cooked forms must never share an extension because both can sit in the same content tree. It will never be cheaper than today (~5 content files), but it fans out into `BaseShaders`, the LSP's file associations, the VSIX TextMate grammar and item templates. Deciding "no" is fine; deciding nothing cements a mixed convention.
9. **Are packs required to be tamper-evident, or only integrity-checked?** The design uses `XxHash128` — fast, non-cryptographic, honest about being corruption detection. If signed DLC, mod distribution or anti-cheat are ever in scope, that becomes a cryptographic hash plus a signature, and **the header field width must be reserved now**, because it cannot be widened later without a format break.
10. **How is save-game / persistent player state expressed?** Everything in this document is read-only mounted data. Nothing in `ROADMAP.md` or the onboarding doc addresses the one write path a shipped game genuinely needs.

---

## 8. Standing invariants this arc adds

Inherited by every milestone above, alongside `ROADMAP.md` §12.

- **`MaterialRef.Id` is never written to disk.** Cooked artifacts store an asset-table index; the loader interns paths in table order and remaps.
- **Node transforms are stored as the authored 10-float `Transform`, never a composed world matrix.**
- **A cooked artifact's `GeometryFormatVersion` and `VertexLayoutId` are checked at load, and a mismatch refuses loudly.** Never upload a buffer whose layout you cannot confirm.
- **Determinism is contracted over uncompressed payload bytes**, and the cook is a pure function of its inputs plus flags — no timestamps, no absolute paths, no `Guid.NewGuid`, no `string.GetHashCode`, no dictionary iteration order in any emitted table, all padding explicitly zeroed.
- **The cooker fails where the runtime degrades.** Both behaviours are correct for their context; neither may be "fixed" to match the other.
- **Asset identity is the normalized content-relative source path, in every mode.**
- **A span into a mapped pack is valid only under a held `PackHandle` ref.** Unmapping under a live span is an access violation, not an exception.
- **No compression library is vendored.** In-box `Deflate` now; in-box `ZstandardStream` on the .NET 11 upgrade.
- **No dependency is adopted on an inferred AOT posture.** Verify with an actual `dotnet publish -p:PublishAot=true` of a throwaway console app, never by reading a README.
- **`.scmap → .smap` is not a valid operation.** `.smap` is the only editable artifact.

---

## 9. What is speculative here

Stated plainly, because the quality bar demands it.

- **Nothing in this document was built or run** — another workflow holds the tree. Every byte offset is arithmetic, not an observation; every alignment claim is a specification the reader must *assert* at load, not an assumption; and the claim that `MemoryMarshal.Cast` over a memory-mapped span behaves identically under NativeAOT on all three targets is a reasonable expectation, not a measurement.
- **Uno + NativeAOT is no longer speculative**: the user has published one and shipped it on ARM Linux in 1 GB of RAM (§5.1). What remains an inference is narrower — *which Windows head* Uno's measured Windows AOT numbers belong to, over documentation that still contradicts itself. `D1`'s single Windows publish closes that, and `.UseWin32()` is the known remedy if it complains.
- **The whole arc rests on an unproven premise**: `docs/roblox-onboarding.md` `O0` has not yet demonstrated a single successful AOT publish of this engine. Everything above is downstream of that.
- **`System.Numerics.Plane`'s 16-byte sequential layout is relied on but not contractually guaranteed** — hence the `Unsafe.SizeOf` assertions rather than a comment.
- **`Utf8JsonWriter`'s shortest-round-trip float output is documented as round-trippable, which is not the same guarantee as byte-identical across runtime versions.** If a future runtime changed the shortest-representation algorithm, every `.smap` in existence would produce a whole-file diff on the next save. Worth a pinned test over a corpus of adversarial floats, and worth knowing whether a runtime upgrade rewriting every map is acceptable.
- **BC encoder determinism under parallelism is assumed, not verified.** Ten-line test, before `D6` commits to a library.
- **`.saudio` is designed against a 23-line stub with no mixer behind it.** The header above is cheap insurance; do not treat it as a finished design.
