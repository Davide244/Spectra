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

**Row order is top-down, and the CPU flip is deleted from the cooked path.** `ImageDecoder.FlipRowsInPlace` performs a vertical flip on every decode, and that flip cannot be carried forward: BC1/BC3 blocks can be vertically flipped with bit manipulation, **BC6H and BC7 cannot without a full decode and re-encode**, and no external cooking tool emits bottom-up BC data. So `.simage` forces the convention to be settled once. What it does *not* force is a per-backend answer, and that half is now **measured rather than open**.

**Measured, 2026-09-02: the three backends AGREE, and there is no odd one out.** This paragraph previously said they disagreed and that nothing had caught it because every texture in `Assets/Textures/` is a symmetric checker or grid. The second half was true and the first was a guess. `Assets/Textures/orientation_probe.png` is an 8x8 fixture with four differently coloured quadrants (authored: top-left red, top-right green, bottom-left blue, bottom-right yellow), drawn through a quad whose UVs carry no per-backend adjustment (`OrientationQuad`) and read back corner by corner. Every backend returned **top-left Red, top-right Green, bottom-left Blue, bottom-right Yellow**, which is the authored image, upright:

```
Texture orientation on OpenGL: UPRIGHT - top-left Red, top-right Green, bottom-left Blue, bottom-right Yellow
Texture orientation on D3D11:  UPRIGHT - top-left Red, top-right Green, bottom-left Blue, bottom-right Yellow
Texture orientation on D3D12:  UPRIGHT - top-left Red, top-right Green, bottom-left Blue, bottom-right Yellow
```

The instrument was falsified before the result was believed: inverting one V term made all three report `FLIPPED vertically`, so "they agree" is a reading and not a blind spot. Two further precautions, because a measurement of orientation can so easily measure its own instrument. The readback's coordinates are defined in **picture** space (y from the bottom, the edge a clip y = -1 vertex rasterises to) with each backend converting to its own row order, and that conversion is proved per backend by a texture-free pass first: a quad covering clip y 0..1 must light the top of the picture and nothing else.

**Why they agree, now that it is known.** GL's `glTexImage2D` and D3D's `SubresourceData` both place the *first row supplied* at v = 0. The bottom-left-versus-top-left origin difference this repo documents elsewhere is a fact about **render targets**, surfaces filled by rasterisation, where GL writes the bottom of the picture into row 0 and D3D writes the top. That is why `FullscreenTriangle` flips V on D3D and why the content path needs nothing. Conflating the two is what made this look like a per-backend problem. `FullscreenTriangle`'s own doc comment got the conclusion right and the reason wrong, and has been corrected.

**The convention, pinned: v = 0 is the BOTTOM of the picture.** `ImageDecoder`'s flip is what establishes it (files store rows top-down; the decoder reverses them), it is honoured identically by all three backends, it is now stated on `Renderer.CreateTexture`, and it is guarded in two places: `TextureOrientationGlTests` against a real GL driver, and a verdict line per backend from `--offscreen-probe` for the two with no headless fixture, which **fails** the probe on any reading but upright.

**Built, 2026-09-03: the flip moved to COOK TIME and the file declares it, so no convention changed and no content moved.** The paragraph that stood here proposed the other migration - delete `ImageDecoder.FlipRowsInPlace`, adopt v = 0 as the TOP of the picture, and flip V once in `FaceSurface`, `ChunkMeshBuilder` and the model importer's UV0. That is still the right end state and it is still open; what it is not is part of shipping a cooked image. Three facts decide it. A block-compressed payload cannot be flipped at LOAD (BC6H and BC7 need a full decode and re-encode), which is real. A block-compressed payload can trivially be flipped BEFORE it is compressed, because the cooker is holding decoded texels either way - and it gets there through `ImageDecoder` itself, so the loose path and the cooked path share one flip and cannot drift about it. And KTX2 already has a key for saying which way up a file's rows are, `KTXorientation`, whose `ru` value means exactly "row 0 is the bottom of the picture" - so the cooked file is honest rather than silently inverted, and `toktx`, RenderDoc and any other KTX2 tool read it correctly. `SimageReader` **requires** the key and **refuses `rd` by name**, because a top-down payload uploaded as-is renders the whole world upside down, raises nothing, and looks like an art problem. When the wider migration lands, the cooker writes `rd` and the reader's two arms swap; nothing else in the format changes. The cost of not doing it now is one flip per cooked image at cook time; the cost of doing it now would have been a content-visible change to every brush face and every imported model in the same commit as a new binary format.

**A new upload entry point.** `Renderer.CreateTexture` takes a single `ReadOnlySpan<byte>` and cannot express a mip chain or a block format. `.simage` needs `Renderer.CreateTexture(in TextureUploadDesc)` carrying per-mip spans, the pixel format, mip count and row pitches (`ceil(w / blockWidth) * bytesPerBlock`, computed once by the cooker rather than by a reader that could get it wrong for non-multiple-of-4 BC dimensions). Both the `.png` path and the `.simage` path then converge on the existing `PumpPendingUploads`, so the render-thread-owns-GPU-creation rule is preserved with no new pump.

**One promise not to make.** "One memcpy per mip" is achievable on GL (`glCompressedTexImage2D` per level) and D3D11 (`UpdateSubresource` with a source pitch), but **not on D3D12**, whose upload path calls `GetCopyableFootprints` and copies per row into an upload heap because the staging row pitch is 256-byte aligned. No file layout changes that. What `.simage` actually delivers is **no CPU decode and no format conversion**, on all three backends, with the row pitch supplied rather than computed.

**Two more things settled while building it, both of which look like defects until the reason is stated.**

**The colour space is a property of the material SLOT, so the cooker writes the UNORM `vkFormat` and the loader passes the CALLER's request through.** KTX2 carries sRGB-ness twice and unambiguously, which reads like a reason to bake it in - and the engine cannot, because the same image is legitimately an albedo in one material and a mask in another, which is exactly why `AssetManager`'s texture cache keys on colour space and holds two GPU textures for one file. A cooked file claiming `_SRGB` would either have to be re-cooked per use or be overridden at load, and the second is what happens, so the claim would be decorative at best and contradictory at worst. `SimageFormat.TryResolveVkFormat` still accepts the sRGB variants, so a file another tool wrote is read rather than refused; `SimageInfo.DeclaredColorSpace` reports what it said, and nothing uploads with it.

**A cooked image is bigger on disk than its PNG, often much bigger, and that is the format working.** Measured on the demo's own content: `Logo/LogoSpectra.png` is 17 KB and `Logo/LogoSpectra.simage` is 342 KB. BC7 is a fixed one byte per texel and a PNG is entropy-coded, so a mostly-transparent 512x512 logo compresses twenty times better as a picture than as GPU-ready blocks. What the cooked form buys is **VRAM and load time**, not download size: the same logo costs 1 MB of VRAM as RGBA8 and 342 KB as BC7, with no decode and no driver-side mip build. Download size is the pack's job, through per-entry compression - which is deliberately `none` for images today, because a compressed entry has to be inflated into a pooled buffer and that forfeits the whole point of mapping the payload straight to the GPU. If a shipped build ever needs the bytes back, the answer is a `Bundle` entry or a patch-time codec, never a change to what `.simage` stores.

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
0x28  u32     VertexLayoutId      FNV-1a over the layout — see §4.4
0x2C  u8[20]  Reserved            written zero
0x40  ...     Section table: SectionCount × 24 bytes
                { u32 FourCC, u32 Flags, u64 Offset, u64 Length }
```

**AS BUILT (2026-09-03).** The block above said "Header — 64 bytes" over a field list
that ran to `0x28` and then put the section table there, i.e. 40. Those cannot both be
true, and §4.4 separately requires a `VertexLayoutId` for which the list had no slot.
Both resolve the same way, which is why this reading was chosen: **the header is 64
bytes as the heading says, every field keeps the offset it was given, `VertexLayoutId`
takes the spare tail at `0x28`, and the section table starts at `0x40`.** The other
branch (a 40-byte header) forces `VertexLayoutId` to displace the one remaining stated
offset and lands the 24-byte, `u64`-bearing section records at 4-mod-8.

Three things the spec left open that a reader cannot, settled here because the writer
has to agree with them byte for byte:

- **`COLL`'s plane array is realigned to 16 within the section.** `4 + hullCount × 8` is
  not 16-aligned for any odd hull count, so without padding the first `Plane` straddles
  a boundary and the in-place cast, which is the whole reason the section is shaped this
  way, stops being legal.
- **`SKEL`'s `f32[12]` is four rows of three**, dropping the constant fourth *column*
  `(0,0,0,1)`. Dropping the last *row* instead is the column-vector convention, and under
  `System.Numerics`'s row-vector layout that discards exactly the translation.
- **`NAME` records are `u16` length plus UTF-8**, mirroring `.spack`, with `0xFFFFFFFF` as
  the absent sentinel (the same constant as `PackFormat.NameOffsetAbsent`).

One gap recorded rather than fixed: §4.4 defines `VertexLayoutId` over
`(semantic, componentCount)` pairs only, so two layouts differing solely in *component
type* hash identically, leaving `GeometryFormatVersion` as the only thing covering that
case.

Sections: `VTXL` vertex layout · `VBUF` interleaved vertices · `IBUF` indices · `SUBM` submeshes · `LODS` · `SKEL` skeleton · `COLL` collision hulls · `NAME` string blob. `ANIM` reserved.

**An unknown section FourCC is skipped, not an error.** This is the most important structural decision in the format: it is what lets `SKEL`/`COLL`/`ANIM` be designed now and written later with no version bump, and it is the same forward-compatibility stance `P2` takes for unknown JSON members.

- **`VTXL`** — `u32 attributeCount`, `u32 strideFloats`, then `attributeCount × 8` bytes of `{ u8 Semantic, u8 ComponentType, u8 ComponentCount, u8 Flags, u16 ByteOffset, u16 Reserved }`. Semantics: `0 Position, 1 Normal, 2 Tangent4, 3 UV0, 4 UV1, 5 Color0, 6 BlendIndices, 7 BlendWeights`. This is what survives `R9` taking `VertexAttribute.StandardLayout` from 8 to 12 floats: the reader compares the file's declared layout against the layout the renderer wants and either hands `VBUF` straight to `CreateMesh` (exact match — the case the cooker makes normal) or stride-copies. The fallback must be *exercised* before `R9` lands, or it is untested the first time it is needed.
- **`IBUF`** — raw `u16` or `u32` per header flag; the cooker picks 16-bit when `vertexCount ≤ 65535`. Honest integration cost: `Renderer.CreateMesh` takes `ReadOnlySpan<uint>` only, so v1 widens on load. Recording the true width from day one is what keeps a native 16-bit path open.
- **`SUBM`** — per submesh `{ u32 IndexStart, u32 IndexCount, u32 MaterialNameOffset, u32 Flags, f32[6] Bounds }`. Material references are logical pack paths interned through the existing `MaterialRegistry` into a `MaterialRef` — the identical mechanism `ChunkSubmesh` already uses, so a model submesh and a chunk submesh are the same shape and should share one draw path. Note the deliberate difference from `.scmap`'s `CMSH`: a model keeps **one** vertex/index buffer with submeshes as index *ranges*, because a model's LODs must share a buffer for an LOD switch to be a draw-range change; a chunk splits the arrays, because its submeshes are uploaded and destroyed independently per cell. Both mirror their respective runtime artifacts rather than imposing one shape on both.
- **`LODS`** — `{ f32 ScreenHeightThreshold, u32 FirstSubmesh, u32 SubmeshCount }`. LODs are index ranges over one shared vertex/index buffer, so an LOD switch is a draw-range change with zero GPU resource churn.
- **`SKEL`** (designed, unimplemented) — `{ u32 NameOffset, i32 ParentIndex, f32[12] InverseBind }`, with `ParentIndex < ownIndex` enforced so a hierarchy walk is one forward loop. **Animation clips live in a separate `.sanim`**, because one skeleton with many clips is the normal case and welding clips into the mesh forces a mesh re-cook when a clip changes.
- **`COLL`** — `u32 hullCount`, then per hull `{ u32 PlaneStart, u32 PlaneCount }`, then a flat array of `{ f32 nx, ny, nz, d }`. This is the engine-specific one and it is why the format is genuinely earned: **collision as convex hulls expressed as plane sets is exactly `Brush`'s constructor input**, so a cooked model's collision converts directly into `Brush` instances and rides the existing `P7`/`P8` machinery with zero new collision code. A triangle soup would demand a query structure the engine does not have and does not want.

**On Assimp:** `Silk.NET.Assimp` is referenced in `SpectraEngine.Core.csproj` (line 35) and no importer code exists anywhere in the tree; `docs/roblox-onboarding.md` `O0` already names it as an AOT suspect. Because the editor must import models and the editor is AOT, a direct managed glTF/GLB reader is preferable to dragging a native library into an AOT-published surface. Verify any candidate's NativeAOT posture by publishing a throwaway console app, not by reading a README (§7).

**AS BUILT (2026-09-03): the writer, the reader and the cook rule all exist, and the importer question resolved as BOTH rather than either.** `Spectra.Kitchen/Models/` holds a hand-rolled managed glTF 2.0 and GLB reader (`GltfReader`, `GltfDocument`) and `SmodelWriter`; `Spectra.Kitchen/Rules/ModelRule.cs` is the rule; `SmodelReader` had already landed. Assimp stays where it was, as the runtime's loose-file importer, because the spike measured it running under NativeAOT and the paragraph above was written before that measurement. What the hand-rolled reader buys is not AOT posture, it is COOK DETERMINISM: a native importer's triangulation, welding and cache optimisation are version-dependent, so cooked bytes would depend on which machine cooked them, which is exactly what the three byte-identity oracles exist to catch and exactly what they would be worst at explaining. The model joins those oracles, and it is the first cooked artifact whose payload is FLOATS laid out by arithmetic this repo wrote rather than bytes copied or bytes an encoder produced.

*What the spec left open and is settled here.*

- **The node hierarchy is BAKED into the vertices and is then gone.** A `.smodel` has one vertex buffer and no hierarchy section, so a transform has to be spent somewhere and this is the only place it can be spent; a mesh two nodes reference becomes two submeshes, each already in the model's own space. The consequence is visible and is therefore stated rather than left to be discovered: a cooked prop instantiates as ONE node where the loose import rebuilds the subtree the source file drew. Its bounds are the same box either way, which is what the acceptance test compares.
- **Each submesh's vertices are a contiguous run of `VBUF`, which the FORMAT does not promise.** That is what makes the loader's gather exact rather than merely correct; the loader still takes the MINIMUM index in each range rather than assuming a partition, so a file whose submeshes interleave their vertices loads correctly with a wider slice than it needs. Assuming contiguity would mis-address every vertex of such a file with nothing reporting it.
- **The file ends at its last payload rather than on the alignment.** Every section START obeys the 16-byte rule, which is what the in-place casts need; a padded tail would be bytes no reader looks at and would make the writer's output differ from a byte-for-byte transcription of this section for no visible reason. `SmodelCodecTests` holds `SmodelWriter`'s bytes against exactly such a transcription (`HandBuiltSmodel`, shared with the reader's own tests), which is the only oracle that can catch a writer and a reader agreeing with each other and with nothing else.
- **The index width is derived from the vertex count and the flag is written from the same decision**, 16-bit whenever `vertexCount <= 65536` (one wider than the section's "≤ 65535", because the largest index is `vertexCount - 1`).
- **`SUBM` material references are logical asset PATHS the cook resolved, and `SmodelWriter` has no way to take an id.** The standing invariant is that `MaterialRef.Id` is never written to disk; the cheapest way to keep an invariant is to make the wrong value inexpressible. The lookup is `ModelMaterialOverride.PathFor`, the SAME function `AssetManager` asks at load, so a cooked reference and a loose override cannot be two different files.
- **A model naming a material this project does not author is SC3002, soft.** A `.smodel` submesh can only point at a `.spectramat` that exists, and an exporter writes its surface inline as a base colour texture and a factor, which the format has no field for. The cooked submesh then binds the engine's default material where a loose import would have used what the file describes. That is a real difference between the two paths and deserves saying out loud, but the author's model is valid and the limitation is the format's, so refusing the build over it would blame the wrong party; `--strict` is how a ship gate asks for the stricter reading. The engine's own `Assets/Models/signpost.gltf` now has `Materials/PostWood.spectramat` and `Materials/SignFace.spectramat` beside it for exactly this reason.
- **`ModelContentPath` is the redirection**, the third instance of the rule `ImageContentPath` and `AudioContentPath` already state, shared by `AssetManager` and by `scook verify`.

*What the reader refuses, by name, and why each is a refusal rather than a guess.* A primitive mode that is not 4 (the number and its glTF spelling are both in the message); any `extensionsRequired` entry, since that member is the file's own declaration that something it carries cannot be ignored; a sparse accessor, because reading only its base array drops exactly the values a sparse accessor exists to carry; an attribute component type that is not `FLOAT` and an index component type outside the unsigned allowlist, since a signed index read anyway becomes a very large vertex index rather than an error; an `asset.version` that is not 2.x; a node carrying both a `matrix` and a TRS component, which glTF forbids and which makes the two disagree about where the node is; a node cycle; a GLB of another container version, a GLB with no JSON chunk, and a truncated one; a data uri that is not base64; and every bounds violation an accessor, a bufferView or a buffer can express. The failure of guessing at any of these is not an exception, it is an accessor walked at a stride the file never meant, which produces a model that draws and is wrong.

*What the reader CARRIES and drops rather than refusing, reported once per model as SC3004.* Vertex attributes outside `POSITION`/`NORMAL`/`TEXCOORD_0` (a second UV set, tangents, vertex colours, skinning weights), morph targets, skins and animations. None of them makes a model unusable and none survives into a v1 `.smodel`, so the geometry is carried and the loss is named; silence here would make "my vertex colours do nothing in the engine" a question with no answer anywhere in a build log. A primitive with no `NORMAL` gets FLAT normals per the glTF specification's own rule, which needs one vertex per corner, so it is expanded rather than smoothed: smoothing would need a weld by position, which is the importer's business and would make the cooked model differ from the file for a reason the file did not state.

*Two conversions are applied always, because both are properties of the SOURCE FORMAT rather than options.* glTF puts v = 0 at the top of an image and this engine samples v = 0 at the bottom, so v is flipped; and a node transform with a negative determinant mirrors, so its triangles have their winding reversed, or a mirrored part renders inside out under backface culling with nothing reporting it.

**A defect this found in the loose path, by measurement.** `SceneManager.GltfImportOptions` set `FlipTextureV = true` for the signpost, on the (correct) reading that glTF puts v = 0 at the top. The importer underneath ALREADY converts that: Assimp's own convention is bottom-up, the same one this engine samples with, so the option flipped a coordinate that had already been flipped and handed back exactly the numbers the file was written with. Nobody could see it because the signpost wears a brick and a grid, both near enough symmetric. The cooked path applies the glTF flip itself, from the specification, and the two agree on every UV of that model with the option OFF and disagree on every one of them with it on. The constant is now `ModelImportOptions.Default` and the remark on `FlipTextureV` says what it is actually for.

*Not built, and honest about it.* `LODS`, `SKEL`, `COLL` and `ANIM` are still designed and unwritten, which costs nothing because the reader skips a FourCC it does not know. `.obj` has no rule and still raw-copies, so the crate loads through the importer exactly as before. The loose model path still reads the FILE from disk: `ModelImporter` hands a path to a native importer that opens the file itself and follows the material library beside it, so a loose model cannot be served out of a pack. Only the cooked path goes through the content stack, which is what `--pack` needs and is why the limit is narrower than it was. And `CookedModelData` COPIES: the format's zero-copy property survives as far as `SmodelReader` and stops there, because `ModelMesh` predates the format and demands a self-contained zero-based array per submesh, which is what one `CreateMesh` call takes. Removing that copy is a renderer change, a mesh drawable as a sub-range of a shared buffer, which is exactly what the one-buffer layout was designed to allow later.

### 2.4 `.saudio` — cooked audio

**Why it is barely custom, in one line:** the container is a 48-byte header over payloads whose codecs are existing standards, and its only genuinely new content is loop points, residency classification and a seek table — which have to live *somewhere* and would otherwise become a sidecar file.

Updated state (D-Stage 27): **the format is built and the header below is what `SaudioWriter` writes and `SaudioReader` reads**, field for field. `Audio/` is real too, from D-Stage 26: a device, a context, a listener, a 32-source pool with an oldest-finished reclaim, `StreamingVoice` over a refilled buffer queue, and a disabled mode for a machine with no sound card. There is still **no mixer**: no buses, no submix, no DSP, no attenuation curves beyond OpenAL's own distance model. What the runtime settled first is the field this header cares most about: **loop points are sample frames, and the runtime plays them by buffer-queue arithmetic rather than by `AL_LOOPING`** (`AudioLoopCursor`), so `LoopStart`/`LoopEnd` below are consumed exactly as written with no sub-buffer restriction, and the last paragraph of this section is no longer a constraint to design around.

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

**v1 ships `PcmS16` only.** Vorbis (NVorbis) and Opus (Concentus) are both plausible but their NativeAOT posture is *inferred*, not verified, and this arc has a standing rule against inferred dependencies. Music can pass through as Opus-in-Ogg the moment a decoder is verified — the codec is a header field, so this is reversible with no format change. The constraint that shaped the runtime, recorded here because it is what the loop fields exist for: OpenAL's `AL_LOOPING` cannot express a sub-buffer loop region. The engine's answer is that a resident sound carrying loop points goes through the streaming path, which is one code path rather than two and is why a region shorter than one buffer needs no special case.

**What shipped, and the two rules that are the whole of it.** `AudioRule` reads a WAV (PCM 8/16/24/32 and 32-bit float, mono or stereo, `WAVE_FORMAT_EXTENSIBLE` unwrapped), resamples through a windowed sinc to the one project rate, and emits `.saudio` under `PackEntryKind.Audio` at the source path with the extension swapped; `AudioContentPath` is the single expression of that redirection, shared by `AssetManager` and by `scook verify`, exactly as `ImageContentPath` is for textures. **Frame counts and loop points convert through one integer function** (`AudioResampler.ConvertFrames`), never through seconds and never through byte offsets: the obvious floating-point spelling truncates, so a loop point drifts a frame at every rate that does not divide evenly, and a frame off at a loop boundary is a click once a bar forever in an asset that measures correct everywhere else. **A loop comes out of the WAV's `smpl` chunk, whose `end` is INCLUSIVE** while `LoopRegion` is half-open, so the conversion is a `+1` and getting it wrong drops or repeats one frame per pass. **Intent is declared by the FILE NAME**: a stereo sound warns (SC4003) unless its stem ends `_2d`, because there is no per-asset settings mechanism yet and a name travels with the asset through every content source. The project rate lives on `CookSettings.AudioSampleRate` (48 kHz) rather than in the project manifest, and belongs in the manifest the moment that file grows a place for it; it is deliberately not a command-line switch, since a per-invocation override is how half a library ends up at one rate and half at another. Codes SC4001 to SC4006 are classified in `CookGate` like every other band.

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

**Amendment (2026-08-27, user directive): a map is a FOLDER bundle, not a single file.** A game is made of multiple maps (something Roblox barely has), and the authored map gains room to be one. `MyMap.smap` is a **directory** carrying the `.smap` suffix, and its v1 contents are:

- **`map.json`**: exactly the document this section specifies, grammar unchanged. One scene document in v1; `map.json` is also the manifest, so a later version may list multiple scene documents (split by subtree, under merge pressure) without a format break. All canonical-encoding rules below apply to every JSON document in the bundle.
- **`scripts/*.luau`**: script payloads as **real Luau files**, referenced from `map.json` script records via the already-specified `path` member, bundle-relative (`"path": "scripts/door_logic.luau"`). The inline `source` array stays legal for one-liners, but the editor authors `path` by default. This is the Rojo lesson from `roblox-pitfalls.md` made native: scripts are files first, so git, external editors, `luau-lsp` and `--!strict` work with no sync layer, and the editor watches the bundle so an external edit hot-applies like any other content file.
- **Anything else is an unknown FILE and is preserved**: the save never deletes or rewrites a file it does not reference, the directory-level sibling of unknown-member preservation. The inverse rule bounds it: a file the map *previously referenced* and no longer does is deleted by the save that unreferences it (the referenced set is the owned set; git holds the history).

Rules that keep the bundle deterministic. **Byte identity is per file**: save, load, save yields byte-identical bytes for every document and script the edit did not touch, and a save writes only the files whose content changed (temp file plus rename per file, so a crashed save never leaves a half-written document). **Script file naming is a creation-time choice, stored thereafter**: the editor derives the name from the node name (sanitised to a portable subset), resolves collisions with a short id suffix, and from then on the `path` stored in `map.json` is the identity; renaming the node does not silently rename the file. **The `.gitattributes` entry becomes `*.smap/** text eol=lf`**, covering every text file in every bundle.

Two consequences elsewhere. `.scmap`'s `SourceMapDigest` (§2.7) becomes the hash of the bundle's canonical enumeration: sorted bundle-relative paths, each path's UTF-8 bytes then its file bytes, so the digest is stable across platforms and file-system orderings. And `game.spectraproj` (§3.3) lists maps as bundle paths; **maps are plural and first-class**: one `.smap` bundle cooks to one `.scmap`, a shipped game carries several, and runtime map switching is an engine verb (owned by the entity/runtime arcs, not this document). The compiled side needs no amendment at all: `.scmap` is already the single binary artifact with baked geometry, entity data and compiled scripts that a multi-map game wants to load per map.

**Canonical encoding.** UTF-8, **no BOM**, `\n` line endings, 2-space indent. Writer options: `Indented = true`, `IndentCharacter = ' '`, `IndentSize = 2`, and — load-bearing — `Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping`. The default `JavaScriptEncoder` escapes `+ < > &` and all non-ASCII to `\uXXXX`, which would turn every inline Luau script and every non-ASCII node name into unmergeable noise. All floats via `Utf8JsonWriter.WriteNumberValue(float)` and `Utf8JsonReader.GetSingle()` (shortest round-trip); GUIDs as lowercase `"D"` format. Ship a `.gitattributes` entry (`*.smap text eol=lf`) with the format, or a Windows checkout with `core.autocrlf=true` rewrites the file under you and turns a no-op save into a whole-file diff.

**Structure.** Fixed top-level member order: `spectramap`, `minimumReadableVersion`, `engine`, `scene`, `editor`, `nodes`. Hierarchy is **nesting** (`children` arrays), not a flat list with `parentId` — because sibling order is load-bearing: traversal order → `BrushPlacement` order → carve order → the bit-identical determinism oracles. `E6` already derived this for `InsertChild`; it binds the file format identically. A JSON array expresses that order exactly and cannot lose it, and a moved subtree is one diff hunk instead of N scattered edits.

```json
{
  "spectramap": 1,
  "minimumReadableVersion": 1,
  "engine": "1.0.0",
  "scene": { "name": "Testmap", "spawn": { "p": [0, 64, 0], "r": [0,0,0,1] } },
  "editor": { "grid": 1.0 },
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

**Brush kind is a NODE member; brush operation is a BRUSH member. The split follows the code exactly and is not a style choice.** `SceneNode.BrushKind` ([`physics.md`](physics.md) §2.3a) is a field on the *node*, and it is meaningfully stamped on a node that carries no brush yet — *"a stamp for a brush that may arrive later"* — so writing it inside the `brush` record would silently lose that stamp on a round trip. `Brush.Operation` ([`negative-brushes.md`](negative-brushes.md) §2) is a property of the immutable *brush value*, and putting it on the node would make one `Brush` instance mean different things on different nodes, which breaks two brush-reference-keyed caches at once. Therefore:

- **`"kind"`** — a node member, lowercase closed vocabulary `"world" | "part"`, written **after `"state"` and before `"transform"`**, **omitted iff `World`**.
- **`"operation"`** — a member *inside* the `brush` record, lowercase closed vocabulary `"additive" | "subtractive"`, written **first inside `brush`, before `planes`**, **omitted iff `Additive`**.

```json
{ "id": "…", "name": "Doorway", "kind": "world",
  "brush": { "operation": "subtractive", "planes": [ … ], "faces": [ … ] }, "children": [] }
```

Both obey the three rules the realm/state paragraph states, and both need them more sharply than realm and state do. **Never a numeric enum.** **Never a derived value** — there is no effective kind or effective operation; neither is inherited. And **an unrecognised value is a reader error naming the node**, never a fall-through: a mistyped `"prt"` silently widening to `World` **re-admits a simulated brush to the carve**, and a mistyped `"subtracive"` silently widening to `Additive` **turns a doorway into a wall**. Those are world-topology changes on load, which is strictly worse than the data leak that put `"realm"` on the reserved-key list. `"kind"` therefore **joins the reserved-key list below**; `"operation"` does not need to, because a `brush` record is a **closed vocabulary** — every member of it is load-bearing geometry, so an unrecognised member inside `brush` is a reader error naming the node and the byte offset, and unknown-member preservation does not apply there.

Rules that make byte identity deterministic rather than hopeful: `transform.p` is **always** written; `transform.r` is omitted iff exactly `[0,0,0,1]`; `transform.s` is omitted iff exactly `[1,1,1]`. A plane is `[nx, ny, nz, d]`, matching `System.Numerics.Plane`'s field order. **`faces` is indexed by PLANE index** (ruling `R‑3`), so `faces.Length == planes.Length` or the reader errors naming the node and the byte offset; a world-aligned face omits `u`/`v` entirely, which is already the engine's encoding (`FaceSurface` treats a zero axis as world-aligned). A fully default face is `{}`. Keyvalues are **string-typed on the wire** per `P5`'s pin.

**A script record has ONE axis, and it is not run location.** `"module"` is a bool, omitted iff `false`; there is no `"kind": "server" | "client" | "module"` string, because `realms.md` §6.2 puts run location on the *node's* `"realm"` — a client script is a script node declared `"realm": "client"`, and a `Shared` runnable script runs on the server, once. An earlier version of this section's example wrote `"kind": "server"`, which duplicated the realm on the payload and would have let the two disagree in the same record. **That name is now spoken for anyway:** `"kind"` is a reserved *node* member meaning `BrushKind` (above), so a script payload may never reclaim it even if the axis were wanted back. The `.scmap` `SCPT` record's `u8 kind` field is correspondingly `0` script / `1` module, with the realm read from the node record's `PayloadFlags`, never from here.

**Scripts: exactly one of `source` or `path`.** `source` is a **JSON array of one string per line** — a JSON string with embedded `\n` is one unmergeable diff line, while an array of lines diffs exactly like the source file. `path` points at a real `.luau` file for `luau-lsp` and `--!strict`. Both cook to the same thing, so the runtime has one path.

**Editor metadata is confined to one key name, `"editor"`, at top level and per node, and the cook never reads it.** A structural rule beats a list. The subtle part is the split it forces: an *editor viewport* camera is editor state; a *gameplay spawn* is `scene.spawn`. `P2` currently says the map holds "camera" without distinguishing, and conflating them is how a shipped game spawns wherever the level designer last parked the viewport. **Amended by §3.3's project-layout rules: the `editor` key holds only SHARED, deliberate editor settings (the grid size); per-user state such as the viewport camera lives in the bundle's `editor.user.json` sidecar, gitignored and never load-bearing.** The example above shows `grid` under `editor` for exactly that reason and no longer shows a viewport there.

**The RESERVED-KEY list lives here and nowhere else.** It is exactly `"editor"`, `"realm"`, `"state"` and `"kind"`. These four names are **never captured by unknown-member preservation** and never round-tripped as opaque text: `"editor"` because the cook must be free to ignore it wholesale, `"realm"`/`"state"` because a misspelling that survives as a preserved unknown member would load as no declaration at all — the node falls through to `Shared`/`Active`, which is a data leak rather than a lost setting (`realms.md` §2.5) — and `"kind"` for the same mechanism with a worse consequence: a preserved-and-ignored `"kind"` falls through to `World` and **re-admits a part brush to the carve**, changing world topology on load (`negative-brushes.md` §2.5). Any document that needs the list **references this paragraph rather than restating it**; a second copy is how the two fall out of step. Growing the list is a format decision made here, in the same change that teaches the reader the new key.

**Unknown-member preservation, exactly.** At an unrecognised property name that is not on the reserved-key list above: record `long start = reader.TokenStartIndex`, call `reader.Skip()`, capture `utf8[(int)start .. (int)reader.BytesConsumed]` into an ordered per-node list; on write, replay with `WritePropertyName` + `WriteRawValue`. **Verified constraint that makes this work:** `WriteRawValue` documents that the writer's `Indented` and `Encoder` settings are *not* applied to raw content — it is emitted as-is. So a preserved value keeps the original file's indentation, which yields byte identity **only because a preserved member's nesting depth never changes**. That is a real invariant the reader must uphold, not an accident.

**AS BUILT (2026-08-29).** `SpectraEngine.Core/Maps/` implements this section: `MapFormat` (constants and the canonical encoding), `MapDocument` (the round-trip DOM), `MapReader`, `MapWriter`, `MapSceneBinder` (scene projection) and `MapBundle` (the folder rules). `EngineInfo.MinimumReadableMapVersion` lands with the reader that enforces it, per §2's rule. Oracles: `MapCodecTests` (byte identity, preservation, refusals), `MapSceneRoundTripTests` (graph, payloads, bundle rules) and `MapLevelFidelityTests` (`DemoPlayArea` saved, loaded, and compared as compiled chunk meshes). Everything below is a decision this section left open, or got wrong, resolved against the tree.

*Four defects in this section, found by implementing it.*

1. **The unknown-member recipe does not compose.** It records `start = reader.TokenStartIndex` at the unrecognised *property name*, skips, and captures to `BytesConsumed`, so the span is `"name": value` with the name included; it then says to replay with `WritePropertyName` + `WriteRawValue`, which needs the value alone. Implemented literally, the name is emitted twice. The reader takes the name via `GetString()`, advances to the value, and captures from there.
2. **The `.gitattributes` entry `*.smap/**` matches nothing that matters.** Attribute patterns use gitignore syntax, where a separator in the middle anchors the pattern to the `.gitattributes` directory: it catches `Lobby.smap/map.json` at the repo root and misses `MyGame/Maps/Lobby.smap/map.json`, which is where §3.3's layout actually puts maps. Corrected to `**/*.smap/**` and verified with `git check-attr` both ways, because the failure mode is silence. The superseded `*.smap text eol=lf` earlier in this section is dead either way, since `.smap` is a directory.
3. **Where preserved members are replayed was never stated**, and replaying them at the end of the object satisfies every stated rule while still changing the bytes, for exactly the case preservation exists for: a newer engine writes its own members interleaved among the ones this engine knows. Each preserved member therefore carries an **anchor**, the index into the owning object's canonical member order of the last known member that preceded it (`-1` for "before all"). The writer flushes anchored members after emitting each canonical slot.
4. **Preservation was specified only per node.** It now applies at top level, inside `scene`, inside a `face` record and inside `light`. `brush` stays closed, as this section requires.

*Members this section does not name.*

- **`light`**. The engine has always had lights and this section omits them. Written after `brush` in the node member order: `{"kind":"point","color":[r,g,b],"intensity":n,"range":n,"enabled":false}`, every member omitted at its default (`directional`, `[1,1,1]`, `1`, `10`, `true`). **`range` is never written as `0` and never defaults to `0` on read**: `Light.Range` refuses anything not strictly positive, so the obvious shortcut throws out of the property setter mid-load.
- **`brush.transform`**. Sixteen floats, omitted iff identity. `Brush.Transform` is a public settable member of the brush value, `Brush.CreateBox` puts the centering translation there, and the standalone `Csg.Carve`/`CsgWorld.Build` overloads read it. A node-attached brush ignores it, which is not a reason to drop it.
- **`vo` / `us` / `vs`**. This section names only `material`, `u`, `v` and `uo` of `FaceSurface`'s seven fields. Offsets omit at `0`, scales at `1`. A world-aligned face omits `u` and `v` entirely, as stated. Face member order is `material, u, v, uo, vo, us, vs`.
- **`nodes` is `Scene.Root.Children`**, with no record for the root: `Scene.Root` is get-only and mints its own id, so a root record could not be restored anyway.

*Writer rules this section implies but does not pin.*

- **`JsonWriterOptions.NewLine` is set explicitly to `\n`.** Its default is `Environment.NewLine`, so a writer that leaves it alone emits CRLF on Windows and LF elsewhere, and byte identity would hold only within one operating system.
- **The document ends with a newline**, because it is a file in a git repository before it is anything else.
- **Numeric records are compact, and one per line.** `Utf8JsonWriter` with `Indented` breaks every array across lines, turning a six-plane brush into forty of them; `WriteRawValue` does not indent raw content at all, which is the same documented behaviour preservation relies on, seen from the other side. So `transform`, each plane, each face and `light` are rendered through a *second, un-indented writer* and spliced in, never built by string concatenation, so escaping and float formatting stay the library's problem and the two paths cannot disagree about how a number is spelled. The array's line layout is the only hand-written whitespace. One record per line is a merge decision: a plane and a face are each the unit a person edits.
- **A BOM is accepted on read and never written.** A file someone saved from an editor that insists on one is still a file they want to open; the next save writes it back canonically, which is a one-time diff rather than a refusal.
- **Comments and trailing commas are refused**, because both would be dropped on the next save, and a round trip that silently deletes a reviewer's comment is worse than one that refuses to load.

*What is specified here and not yet bound to engine state.* `realm` and `state` have no enum in Core, so the reader **validates them against the closed vocabulary and carries the validated string**, which upholds the rule that matters (a mistyped `"sever"` is an error, never a silent widening to `shared`) without inventing a concept the engine does not have. `script` and `scene.spawn` are preserved opaquely. **`entity` moved to the bound side on 2026-09-02** and is no longer in this list: `MapEntity` decodes `class`, `keys` and `outputs` into `SceneNode.Entity`, the record stays **open** (like a face, unlike a brush) so an unrecognised member still rides through, and a document carrying one raises its own `minimumReadableVersion` to `EngineInfo.EntityMapVersion`, because `MapSceneBinder` builds a fresh `MapEntity` on save, so an older editor that read the payload as an opaque unknown would open such a map, display it correctly, and delete every keyvalue and wire in it on the next Ctrl+S. `editor` is carried as raw bytes *by name* rather than as an unknown member, so the cook can still drop it wholesale; its v1 content is the grid size, which lives in `SpectraEngine.Editing`, an assembly Core cannot reference.

*Mesh nodes (added 2026-08-29).* A node whose renderer came from a model file carries a `SceneNode.MeshSource` and is written as `"mesh": {"model": "Models/crate.obj", "submesh": 2}` - a reference, never geometry, exactly as a face carries a material path. `submesh` is omitted at 0, which is the single-submesh prop. The record is **open** (like a face, unlike a brush), because this is where a model reference grows - a material override, a LOD choice, a collision hint - and none of that changes which geometry is named. A `mesh` with no `model` is a reader error, since a node that silently loses its geometry looks exactly like one that never had any, and the submesh index is bounds-checked against the loaded model because a re-exported file can name a submesh that is no longer there. **A load that cannot resolve the model degrades** to a node with its identity, placement and children and no renderer, with the reason in `MapLoadReport`: deliberately the opposite of the brush path, which throws, because a brush that cannot be built is a hole in the world while a missing prop is a missing decoration. **What remains permanently unsaveable is a mesh built in CODE** (`Primitives.Cube()` and friends): there is no file to name, and the only ways to close that are to write vertices into a file whose whole rule is that it holds no derived data, or to give procedural geometry a recipe worth naming.

*Byte identity, stated precisely.* `Write(Read(bytes)) == bytes` is exact. Scene projection is **not**, once: `Brush`'s constructor re-normalises every plane, so an authored `[2,0,0,-64]` is canonicalised to `[1,0,0,-32]` on the way through. Save/load/save over a real level is therefore exact **from the second save on**, and both halves are pinned separately so a canonicalisation can never be mistaken for a codec defect.

---

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
0x10  u128   SourceMapDigest       XxHash128 of the .smap bundle's canonical
                                   enumeration (see the §2.6 folder amendment:
                                   sorted relative paths, path bytes then file
                                   bytes), so one digest covers map.json and
                                   every script the map references
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

Sections: `STRT` strings · `ASTB` asset table · `META` map metadata and compile constants · `NODE` node graph · `CHDR` chunk directory · `CMSH` chunk meshes · `CBSP` chunk BSPs · `RGNI` region index (reserved, §4.5) · `BMDL` brush models (**reserved, no longer emitted** — §2.7's `PayloadKind` ruling) · `BRSH` authored brush source (optional) · `ENTT`/`ECON` entities and connections · `SCPT`/`LUAB`/`LUAS` scripts · `NBND` per-node local bounds (optional).

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
                            2 PartBrush · 3 RETIRED, refuse · 4 MeshInstance · 5 PrefabRoot
+0x42  u16  PayloadFlags    bit0 HasSource · bit1 IsEntityOwned · bit2 CanReCarve
                            bits 3-4 DeclaredRealm (2-bit: 0 inherit, 1 shared,
                                                    2 server, 3 client)
                            bits 5-6 DeclaredState (2-bit: 0 inherit, 1 active,
                                                    2 dormant, 3 invalid)
                            bit7  SubtractiveBrush (Brush.Operation)
                            bits 8-15 free
+0x44  u32  PayloadIndex
+0x48  u64  Reserved = 0
+0x50       END
```

**`PayloadFlags` bits 3–6 carry the node's DECLARED realm and state, never the effective ones.** This is the full allocation of that `u16` and it is owned here; `realms.md` §2.5 cites these bit numbers rather than assigning its own. The 2-bit values are the enums' own numeric values (`NodeRealm.Inherit=0, Shared=1, Server=2, Client=3`; `NodeState.Inherit=0, Active=1, Dormant=2`), so the writer masks and shifts and nothing remaps — which is also why `3` in the state field is *invalid* rather than a fourth state, and why the enums may never be renumbered once content exists. **Do not confuse this record with `.sentdef`'s keyvalue `Flags` u32** (§3.2), which is a different record with its own allocation — bits 0–2 `readOnly`/`hideInEditor`/`requiresRestart`, bits 3–5 replication (`networking.md` §3.4), bits 6–7 per-property realm — and whose bit numbers deliberately do **not** line up with these. Two different records, two tables; the only thing they share is the word *realm*.

> **RULING 2026-08-21 — `PayloadKind` 2 IS `BrushKind.Part`, and it is renamed; `PayloadKind` 3 `BrushModel` is RETIRED, not reused.** This entry previously read `2 DynamicBrush · 3 BrushModel`, from a design in which a non-carving brush could be one of two different things. It cannot any more.
>
> **`DynamicBrush` → `PartBrush`.** *"Dynamic"* is wrong on two counts: it implies a physics-dynamic body (`physics.md` `Y6`), which a part brush need not have — an *anchored* part brush is the common case — and it is not the word any of the five documents that now cite this split use. A cooked node whose `SceneNode.BrushKind` was `Part` writes `2`; a node whose kind was `World` writes `1`, which is the same statement as *"its geometry was baked into the chunks"* because `SceneNode.IsStaticWorldBrush` is exactly what the snapshot admitted. **That is the whole `BrushKind` route, and without it a converted part does not survive save/load** — which is data loss, not a gap.
>
> **`BrushModel` collapses into `PartBrush` and value `3` is refused.** `BrushModel` named `P7`'s fused, entity-local, chunk-blob-layout bake of an entity's brushes, and **that mechanism is dead** — `physics.md` §2.3a overturned it (a `MeshRenderer` holds one `Mesh` and one `Material`, so after `F1` it cannot express a multi-material brush at all) and replaced it with `P7a`'s per-brush render arm plus a `Brush`-reference-keyed mesh cache. An entity-owned brush is therefore a **part brush whose owner happens to be an entity**, and the only surviving distinction is already carried by `PayloadFlags` bit1 `IsEntityOwned`. Consequences: the **`BMDL` section loses its only producer** — keep the four-character code reserved and emit nothing; and a loader meeting `PayloadKind == 3` **errors naming the node** rather than guessing. No `.scmap` has ever been written, so reuse would in fact be free today — the value is burned anyway, because an enum value in a shipped format is exactly the thing that must never mean two things. **If `P7` ever revives a genuinely *fused* multi-brush model (a door welded from five brushes into one mesh), it needs a new payload kind and a new section, and it must argue against `physics.md` §2.3a first.**
>
> **`PayloadFlags` bit7 carries `Brush.Operation`** ([`negative-brushes.md`](negative-brushes.md) §2). It is a flag rather than a `PayloadKind` value **because operation is orthogonal to admission**: all four `(kind, operation)` combinations are legal, including `(Part, Subtractive)` — which is inert by design and must round-trip, or a `SetBrushKindCommand` becomes lossy. Folding operation into `PayloadKind` would multiply that enum by two and re-create exactly the three-valued admission predicate `BrushKind` exists to prevent. The bit is meaningful only when `PayloadKind` is 1 or 2; a loader must **ignore** it otherwise rather than error, so a future payload kind is free to leave it zero.
>
> **Cooking a `StaticWorldBrush` with bit7 set is legal and normal** — a subtractive world brush is admitted, is in the placement list, and its *effect* is baked into the chunks exactly as an additive one's is. It carries no geometry of its own into `CMSH` for the reason `negative-brushes.md` §3.5 gives (its carved array is always empty), which costs the format nothing: `PayloadIndex` is unused for a baked brush either way.

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

**`BRSH` / ~~`BMDL`~~.** ~~`BMDL` bakes entity-owned brush geometry (`P7`'s brush models) in entity-local space using the **identical blob layouts as chunks**, so one reader serves both and the `BMDL` bake oracle is a copy of the chunk bake oracle — a door's geometry is derived data with a deterministic compile, and the only thing that changes at runtime is its matrix.~~ **`BMDL` is reserved and no longer emitted**, by §2.7's `PayloadKind` ruling: `P7`'s fused brush-model mechanism is dead, an entity-owned brush is a **part brush** whose mesh is built at load from its own `Brush` by the same `Brush`-reference-keyed cache every other part uses, and there is nothing left for a bake section to hold. Nothing is lost that was ever written — no `.scmap` exists — and the *"derived data is never authored"* posture improves: a part brush's mesh is now derived at runtime from the authored planes rather than baked into the map, so it cannot go stale against the brush it came from. `BRSH` is unaffected and grows in importance, because it is now the only place authored brush planes live in a cooked map. `BRSH` retains authored planes and 48-byte `FaceRecord`s (material asset index, brush-local `uAxis`/`vAxis`, offsets, scales) for brushes that need to be re-carved at runtime, per-brush `keepSource` plus a cook-wide `--keep-brush-source`. Loading `BRSH` uses a validation-free `Brush.FromValidated(planes, faces)` that skips the O(n²) duplicate-plane rejection and the second `BuildFaces` boundedness probe, justified because the authoring path already validated them; debug builds re-run full validation and assert equality.

**The one silent-corruption hazard this format creates, named so it is a check rather than a discovery.** When baked chunks and `BRSH` are both present, a loader that helpfully calls `Scene.RebuildStaticWorld` produces a world where every wall is drawn twice — with z-fighting that every graphics programmer's instinct attributes to depth precision or a pipeline state bug, not to a map loader. Guard: the **`BakedIntoChunks`** flag, an explicit named contract (a flagged brush must never enter a live carve without first invalidating the chunks containing it), and a test asserting a `--keep-brush-source` cook draws the **same triangle count** as the same map without it.

> **RENAMED 2026-08-21 — this guard was called `IsStaticWorldBrush`, and that name is now taken by something else on a different type.** `SceneNode.IsStaticWorldBrush` (`Scene/SceneNode.cs:231`) is the engine's **admission** predicate — *"is this brush admitted to the carve"* — and five documents now cite it by that name (`physics.md` §2.3a, `realms.md` R15/R17, `data-model.md` §2.4, `roblox-onboarding.md` `O7`, `negative-brushes.md` §1.1). The cooked-record flag says something almost opposite and strictly narrower: *"this brush's geometry is **already baked** into the chunks; do not re-carve it."* Two identifiers with one spelling, on two types, in two layers, whose meanings differ by exactly the mistake the paragraph above exists to prevent — a loader that reads *"static world brush"* as *"belongs in the carve"* re-carves it and draws every wall twice. **`BakedIntoChunks` is the cooked-record name; `IsStaticWorldBrush` is the engine name; neither may take the other's spelling.** The flag itself is not a new bit — it is exactly `PayloadKind == 1 (StaticWorldBrush)`, so the rename is to the *contract*, and every reference to the guard uses the new word.

**`ENTT`/`ECON`/`SCPT`.** Entities: `{ u32 nodeIndex, classNameString, kvStart, kvCount, outStart, outCount }` plus `{u32 keyString, u32 valueString}` pairs — string-typed on the wire, matching `P5`. Connections: `{ u32 outputName, targetName, inputName, parameter; f32 delay; i32 timesToFire }` with `-1` = infinite. Scripts: `{ u32 nodeIndex; u8 kind; u8 flags; u16 reserved; u32 chunkNameString; u32 bytecodeOffset, bytecodeSize; u32 sourceOffset, sourceSize; u32 reserved }`, with `chunkNameString` stored independently of `LUAS` so tracebacks still name the script when source is stripped.

**Scripts: source is the ground truth, bytecode is a cache.** Luau's own documentation is explicit that bytecode is *not* a durable storage format — the supported version range is bounded and old versions are dropped over time, and users are expected to recompile on upgrade. The safe design is therefore: `LUAS` (source, compressed) always present unless explicitly stripped; `LUAB` (bytecode) stamped with the Luau bytecode version and the vendored Luau commit id, validated on load, **falling back to compiling the source when the stamp mismatches**. `--script-source=strip` remains available for a shipper who accepts that the pack is then only loadable by the engine build that produced it. Whether the shipped runtime *also* links Luau.Compiler is a build property, not a format decision, and the format supports all four combinations deliberately because `docs/roblox-onboarding.md` §5 item 1 is explicitly unanswered.

**AS BUILT (2026-09-03), the container and its first five sections.** `SpectraEngine.Core/Maps/Compiled/` carries the format constants, the record structs and the reader (`ScmapFormat`, `ScmapHeader`, `ScmapSection`, `ScmapAssetEntry`, `ScmapNodeRecord`, `ScmapChunkRecord`, `ScmapMeta`, `ScmapSpawn`, `ScmapStringTable`, `ScmapDocument`, `ScmapReader`); `Spectra.Kitchen/Maps/` carries the writer (`ScmapLayout`, `ScmapWriter`, `ScmapStringTableBuilder`, `ScmapBuilder`, `MapBundleDigest`). The split is `.spack`'s, for `.spack`'s reason: a shipped game reads and never writes, so no map-baking code may ship in a game binary. `EngineInfo.CompiledMapFormatVersion = 1` is an **exact-match** gate and the refusal names both numbers and says recook. `STRT`, `ASTB`, `META`, `NODE` and `CHDR` are written and read; `CMSH`, `CBSP`, `ENTT`, `ECON`, `SCPT`, `LUAB` and `LUAS` are emitted as **empty sections so the codes are claimed**; `RGNI` and `BMDL` are refused by name at the writer, which is the only place they could be caught, because a reader steps over both in silence. Oracles: `ScmapFormatTests` (Core: size and offset pins, the RFC 4122 byte order, the string table), `ScmapWriterTests`, `ScmapBuilderTests` and `ScmapDeterminismTests` (the two-process byte-identity oracle).

**The named hazard is answered structurally rather than by a test, and then tested anyway.** §6 says a blob landing at a non-16-aligned offset is the risk, because the layout pass and the write pass must agree on every size *including padding* and a one-byte disagreement corrupts every later section with an arbitrary symptom. So **exactly one function computes what a section costs** (`ScmapLayout.PaddedSectionSize`) and both passes call it; there is no second expression of that arithmetic anywhere. That is necessary and not sufficient, because a section DECLARES its length and then writes it, which is the shape the chunk-mesh blob needs (its size is knowable from the compiled artifact long before its bytes are materialised). Two statements can disagree, so the writer asserts at every section boundary that the stream is where the layout put it and that the body wrote what it declared, and **both refusals name the section**. The reader then re-checks alignment from the file's own table, which is the only one of the three that survives a file written by something else.

Ten things the spec left open that a writer and a reader cannot, settled here:

- **`META`'s layout.** The section named its fields and not their offsets. The preamble is a 48-byte struct (32 bytes of declared fields, 16 reserved and zero-filled) so the spawn array starts 16-byte aligned inside a section that is itself 16-byte aligned; a spawn record is 28 bytes of content padded to 32 by a **declared** reserved field, because an undeclared gap is exactly the byte that picks up stack garbage and turns a byte-identity oracle red in a way that is very hard to bisect. **All three compile constants are validated, not two**: cell size, weld band and snap grid, exactly (`==`, which also refuses a NaN that no tolerance would), each naming both numbers and the consequence of loading anyway.
- **The canonical interning order, and WHEN it happens.** "First-reference order during the canonical node walk" does not say where the scene name and the asset paths sit. Settled: the empty string, the scene name, the asset table in its own order, then node names in pre-order. More consequentially, interning happens at **build** time in that fixed order rather than as each `Add` arrives: interning on arrival satisfies the rule only while the cook happens to call in walk order, so a bake that gathered its materials first, or on a worker, would emit a different string blob for the same map with nothing failing.
- **The node id is stored as the integer its RFC 4122 bytes read as, never as a `System.Guid` field.** `Guid`'s in-memory layout byte-swaps its first three components on a little-endian machine, so a raw field would put an id on disk in an order that does not match the hex the authored map spells the same id with. `ScmapNodeRecord.EncodeId` and `DecodeId` are the only two places that byte order is spelled.
- **`ASTB.Kind` reuses `PackEntryKind`** rather than minting a second vocabulary: an `ASTB` row names exactly the thing a pack entry names, and two enums for one concept is how a material becomes a model in a log line. `ContentHash` is the low 64 bits of the cooked payload's hash and is advisory, as specified.
- **`NODE` and `CHDR` open with a 16-byte preamble** (a `u32` count and twelve reserved zero bytes), which is what "pad to 16" has to mean for the 80- and 64-byte records after it to be castable in place.
- **`STRT` keeps its redundant `blobSize`.** `offsets[count]` already carries it, and both are written and cross-checked, because the blob length is the one value that lets a truncated section be refused before any offset is trusted.
- **Section order in the file is fixed** and is part of the byte identity, so `ScmapLayout` preserves the order it is given and never sorts. The **chunk directory** is the one thing sorted at build time, because a cook walks its cells out of a dictionary; nodes are never sorted, since sibling order is authored data.
- **Four version words, and only three gate.** `FormatVersion`, `GeometryFormatVersion` and `VertexLayoutId` are exact-match refusals; `MapFormatVersion` is recorded and **informational**, because the authored map is not present at runtime and a load cannot act on which grammar the bake read. The layout id is `SmodelFormat.ComputeVertexLayoutId` over the same `(semantic, component count)` pairs rather than a second copy of the hash, so two cooked formats naming one geometry shape cannot report different ids.
- **The section record's `UncompressedSize` equals `Size`, and the `Compressed` flag is refused on read.** Zero would make "not compressed" and "empty" the same bytes, and an empty section is legal here precisely because the reserved codes are claimed at length zero.
- **`SourceMapDigest` is computed in the cook, not in the engine** (`MapBundleDigest`), although `MapBundle` next door already does file I/O: only a cook can compute the value, because it needs the authored bundle, and a shipped game has none and only ever compares the number it was handed.

**The two-process oracle re-entered the TEST binary, and that harness is RETIRED.** .NET randomises the string hash seed per process, so an in-process comparison structurally cannot detect a hash-order dependency, which is exactly why §4.3's cook oracles drive the real `scook` through `Process.Start`. While the compiled-map writer had no CLI route the child was that same test binary, re-entered through an environment variable and a `[ModuleInitializer]` that wrote the fixture and exited before a test was discovered. The map bake gives the cook a map to bake, so `scook cook --loose` writes a `.scmap` as a real file and `ScmapDeterminismTests` uses the same mechanism `CookDeterminismTests` already uses for packs: two clean bakes, `-j1` against `-j8`, and a cached bake against a clean one. **What went with the harness is its seed probe**, which asserted that two children really had hashed differently; the map oracles now stand exactly where the three pack oracles already stood, with no such evidence, and the falsification below is the stronger claim in its place.

**AS BUILT (2026-09-03), the bake: `CMSH`, `CBSP`, `BRSH` and `MapRule`.** `Spectra.Kitchen/Rules/MapRule.cs` takes a bundle and emits `Maps/<Name>.scmap` under `PackEntryKind.Map`; `Spectra.Kitchen/Maps/ScmapBake.cs` is the chain, binding the document into a headless `Scene`, capturing its placements and running a **cache-free** `CsgWorld.Build`. Cache-free is the point: the incremental compiler carries state across compiles to make an EDIT cheap, and a bake must be a pure function of its source. Core gains `ScmapChunkMesh`, `ScmapSubmeshEntry`, `ScmapChunkBsp`, `ScmapBrushSource`, `ScmapBrushRecord` and `ScmapFaceRecord`, all `ref struct` readers over the mapped bytes.

**The three named hazards, and what each was falsified against.** *(1) `MaterialRef.Id` never reaches the file.* `ScmapBake.AssetTable` is the only route from a runtime reference to an `ASTB` row, and the submesh directory carries that row. Falsified by writing `(uint)submesh.Material.Id` into the directory instead: three tests go red, and notably the ORDERING test does not, which is the coincidence the doc warns about. *(2) Alignment.* Every padded run in the file goes through `ScmapLayout.PaddedSectionSize` and nothing else, at every scale: the section table, a chunk-mesh blob's directory, each vertex and index array, and `BRSH`'s plane and face tables. Falsified twice: a second, unpadded expression of the submesh directory's size sends twelve tests red on the reader's per-array alignment check, and changing the one function to align to 8 sends twenty-eight red across the writer's own boundary assert and the reader's per-section check. *(3) Double geometry.* Falsified by making `IsReCarvable` read the payload kind as "is a brush" rather than as `BakedIntoChunks` (one test red, the guard) and by drawing every triangle twice under `--keep-brush-source` (one test red, the triangle-count oracle).

Nine more things the spec left open, settled here:

- **A submesh that names no material carries `NoAssetIndex` (`0xFFFFFFFF`), not row 0.** `ASTB` has no reserved first row - unlike `STRT`, whose index 0 is the empty string precisely so "no name" needs no sentinel - so a surface wearing `MaterialRef.Default` has nothing to point at, and writing 0 would paint every unnamed surface in whichever material the bake referenced first. The engine's answer to such a face is already `Scene.StaticWorldMaterial`.
- **Ascending asset index is a SORT, because the compile's order is ascending material id.** A `ChunkMesh` is ordered by a per-process interning number and the file is ordered by a row the map itself decides; the two are different orders on purpose, and the bake sorts. The bake oracle therefore compares submeshes as a set keyed on the material's PATH rather than positionally.
- **A blob's own length is a multiple of 16**, because each array inside it is padded up after itself, so blobs tile with no gap and a cell's declared size is the same number whether you count its content or its footprint.
- **A cell with a tree but no mesh still gets a `CBSP` blob**; a null node array means no tree at all. Solid and empty are different answers and a 16-byte header is what lets the root's leaf code say which.
- **A cell with no mesh writes the CELL CUBE as its render bounds.** Nothing culls a cell whose `MeshSize` is zero, so the field bounds nothing; a finite deterministic box is better than a fabricated one and obviously not a claim about anything drawn.
- **A PART brush is in `BRSH` whatever the cook was asked for.** Its planes live nowhere else - it is never baked into a chunk and its mesh is built at runtime from its own `Brush` - so a map that dropped them ships a level whose parts are invisible. `--keep-brush-source` adds the WORLD brushes on top, and the per-brush `keepSource` the `.smap` format already carries is honoured beside it. `HasBrushSource` therefore means PRESENCE and never permission, and the reader refuses a file whose header flag and section table disagree about it.
- **`BRSH` carries the SCENE's planes, which the `Brush` constructor has normalised**, rather than the document's authored numbers. A runtime re-carve builds a `Brush` from them and would normalise anyway, and taking them from the scene keeps one source of truth for the whole bake.
- **The link between a brush and its node runs ONE way**, from the `BRSH` record's `nodeIndex`. A brush node's `PayloadIndex` stays zero; a mesh instance's is its model's `ASTB` row, which is the table its payload kind names.
- **`editor.user.json` is not read and not hashed.** It is gitignored per-user state that changes every time somebody moves a viewport camera: hashed into `SourceMapDigest` it would put a different number in every developer's compiled map for one level, and read as a dependency it would miss the cook cache on every launch. One predicate (`MapBundleDigest.IsSourceFile`) answers for the rule and for both ways of gathering the digest.

**What is not built, named so it is a gap rather than a discovery.** `ScmapPayloadKind` **has no light value and the format has no light table**, so a compiled map v1 carries a lamp's node and not its lamp; both are append-only additions and neither can be invented here without the other. `MeshSource.SubmeshIndex` has nowhere to go either - a mesh instance names its model through `ASTB` and not which submesh of it - so mesh instancing needs a table of its own before it means anything. Spawns are still absent because `scene.spawn` is a PRESERVED member of `.smap` rather than a bound one, so `META` writes a spawn count of zero. `NBND`, `ENTT`/`ECON` and the three script sections have no producer. `PayloadFlags` bits 3 to 6 are written as `Inherit` because the engine has no realm or state enum yet, and the format owns that numbering, so the enum that lands later must match it. `PackVerifier` still has no 7xxx arm, so nothing cross-checks a compiled map's `ASTB` against the pack it ships in - which for a map is exactly the "mounts cleanly and shows checkerboards" failure §2.1's verifier exists for. And **a file newly ADDED to a bundle does not invalidate a cached bake**: `IRuleContext.ListFiles` is how a rule over a FOLDER names its inputs, every file it returns becomes a dependency when the rule reads it, and a directory listing is not something `CookCache` can restate - it closes the day a directory observation joins `RuleDependencyKind`.

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
0x06  u16    HeaderSize = 20        (this table's fields run to 0x14; 16 would put the
                              first type record four bytes inside the header)
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

Input record / output record - 4 bytes
+0x00  u32   NameRef

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

Choice record - 8 bytes, ChoiceCount of them immediately after the keyvalue record
+0x00  u32   ValueRef      the token written into a map, which IS the wire form
+0x04  u32   DisplayRef    the label an editor shows, or 0 to use the value
```

**String references are offsets INTO the string table**, not absolute file offsets, so the table can move without rewriting every record. **Offset 0 is the empty string**, which the table always opens with (`00 00`), so every unset display name, tooltip and default shares one reference a reader answers without touching the table at all. **Type records are sorted by class name with `string.CompareOrdinal`** and strictly unique - the same order `EntityCatalog` enumerates in, which is what keeps the loader-dependent order module initializers ran in out of the bytes. A reader enforces that order rather than trusting it, which also refuses a duplicate class name for free.

`DefaultRef` is a **string** reference and not a typed value, because `P5` pinned keyvalues as string-typed on the wire — if the schema's defaults were typed, the editor's "is this still the default?" comparison would become a type-conversion round trip and would drift silently.

**Two constraints the editor property panel must obey, both AOT-forced.** It is an `ItemsControl` over a `KeyvalueDescriptor` list with a `DataTemplateSelector`, one compiled template per `(Type, Widget)` pair. **No `DynamicObject`, no `ExpandoObject`, no dynamic property bag** — Uno documents that Expando and DynamicObject bindings "likely will not work" under Native AOT (verified 2026-08-21), and this is the exact place a conventional implementation reaches for one. The failure would appear only in the published build, after the developer stopped looking, which is the concrete justification for the `D0` CI publish gate.

**Engine-SDK mode must export `.sentdef`, not a bespoke JSON.** `P5`'s `--export-entity-schema` already exists to be pointed at this. One export format means the editor keeps exactly one schema consumer no matter how many games exist — the same invariant that makes the Luau path safe. SDK mode's costs, to be documented: a per-game binary (the "one exe" property is voluntarily surrendered for that game), a build host per RID because .NET AOT cannot cross-compile, an editor that sees those entities only through the export, and a **registration-scope object owning and reversing every engine-event subscription**, which must be designed *before* the C# extension API has users.

**AS BUILT (2026-09-03), the C# half: `D14`, `D16`, and the gate that proves them.** `SpectraEngine.Core/Entities/` implements the schema side of this section: `KeyvalueType` (the closed vocabulary), `EntitySchema`/`KeyvalueDescriptor`, `EntitySchemaCatalog`, and `SentDef`, which is **writer, reader and layout constants in one file**, because two halves of a byte layout in two files drift into a file that parses into different numbers than it was written from, and nothing throws. `SpectraEngine.Entities.Generator` emits the five things named above per `[SpectraEntity]` class; `SpectraEngine.Entities` carries `logic_relay`, `logic_timer` and `math_counter`; `--export-entity-schema=<path>` writes the file and exits before a renderer or a window exists. The Luau half (`D15`, `ScriptedEntity`, `Entity.define`) is untouched and still reads exactly as specified, which is the point of the `Origin` badge shipping now.

- **The editor really does read `.sentdef` and nothing else, and it costs one serialize.** `EditorSession` writes `EntityCatalog.Shared` out to `.sentdef` bytes at construction and parses them straight back into the `EntitySchemaCatalog` both the render thread and the UI thread then share. A second population path taking a schema list would have let an in-process editor read `EntityCatalog` directly while an out-of-process one read the file, and the two producers this section exists to make indistinguishable would drift with nothing failing. It also exercises the round trip on every launch rather than only in a test.
- **Two things this section left open are settled in the file.** The choice record's shape (`ValueRef` + `DisplayRef`, 8 bytes, `ChoiceCount` of them immediately after the keyvalue record), and that `HeaderSize` is **20** rather than 16, because the field table above runs to 0x14, so 16 would put the first type record four bytes inside the header. The block above carries both corrections in place.
- **Reserved flag bits are refused at the write and masked on the read, deliberately asymmetric.** A writer that quietly dropped bit 3 would let the first real producer of replication (`networking.md` §3.4) ship a file missing it while every parity test still passed; a file some other tool wrote must instead lose what this build cannot honour. `NaN` for an unbounded `Min`/`Max` is asked with `float.IsNaN` and never `==`, which reports every bound as present and then clamps against a NaN.
- **The gate is the PUBLISHED binary, and no JIT test can give it.** A registration is a `[ModuleInitializer]` in an assembly nothing statically calls into, which is exactly the shape a trimmer removes, and a build that dropped them still starts and still loads every map. So the host prints `Entity catalogue: N classes registered (...)` on every run, at Error when N is zero. Measured 2026-09-03 on `dotnet publish SpectraEngine.Executable -c Release -r win-x64`: 3 classes, and `--export-entity-schema` from that binary and from a JIT run produce **byte-identical** `.sentdef` (1103 bytes, SHA256 equal), which is the stronger form of the same claim because it exercises the whole schema surface rather than a count.

### 3.3 `game.spectraproj` — the project as data

Deliberately **not** a binary format: it is ~40 lines read exactly once, and a binary encoding would save microseconds while costing git-diffability on the one file a user most needs to hand-edit and merge. Authored JSON, hand-rolled `Utf8JsonReader` codec in the `P2` house style, unknown members skipped on read and re-emitted verbatim on save. Copied verbatim into the boot pack as a `Kind=Raw` entry.

Fields: `formatVersion` + `minimumReadableVersion` + `cookedWithEngineVersion`; `name`; `id` (Guid — save-folder and pack namespace); **`packs` (an ORDERED array; later entries win — that is the mod and patch story, free)**; `startupMap`; `defaultBackend` + `allowedBackends`; `display { mode, width, height, vsync }`; `input { actionName → bindings }` so Luau reads action names and never scancodes; `settings` (unknown keys warn and are preserved, matching `.spectramat`'s existing forward-compatibility rule); `bootScript`; `entityDefinitions` glob.

**Amendment (2026-08-27, user directive): the project has a CANONICAL layout, shaped like a Visual Studio solution, and every authored artifact in it is text.** A game is one folder; opening it in the Spectra editor and opening it in VS Code are both first-class, because there is nothing in it an external editor cannot read:

```
MyGame/
  MyGame.spectraproj        ← the project file above; the double-clickable identity
  Assets/                   ← the existing content root, unchanged
    Textures/  Materials/  Models/  Sounds/  Shaders/
  Maps/
    Lobby.smap/             ← map bundles (§2.6 amendment): map.json + scripts/*.luau
    Arena.smap/
  Scripts/                  ← project-level shared Luau modules (maps reference them)
  spectra.d.luau            ← generated API definitions (O5); regenerated, never hand-edited
  .vscode/                  ← shipped by the project template: luau-lsp wired to spectra.d.luau
  .gitignore  .gitattributes ← shipped by the template (cook output, *.user.json, eol rules)
  cooked/                   ← scook output; derived, gitignored, never authored
```

Three rules this layout binds:

1. **No authored artifact is ever binary, and editor state is never load-bearing.** Studio's binary place file is the named counter-example (`roblox-pitfalls.md` §1): it is why that platform needed Rojo. Here the project file, the map bundles, the materials, the scripts and the shader sources are all text; binary exists only in `cooked/` as derived output. This is `roblox-pitfalls.md` law 3 applied to the project as a whole.
2. **Per-user editor state lives in `*.user.json` sidecars, gitignored, exactly Visual Studio's `.csproj.user` convention.** The editor's viewport camera, selection, expanded-tree state and window layout go to `<bundle>/editor.user.json` (per map) and `MyGame.spectraproj.user` (per project); losing one loses nothing but a camera position. Shared, deliberate editor settings (the map's grid size) stay in `map.json`'s reserved `editor` key. The split exists because a per-user camera in a shared file is merge noise on every save, which is a small copy of the Team Create lesson.
3. **VS Code is the blessed external editor, and the loop closes without the Spectra editor in the middle.** The template's `.vscode/` wires `luau-lsp` to the generated `spectra.d.luau`, scripts are real `.luau` files inside bundles (§2.6), and the running editor watches the project tree, so an edit saved from VS Code hot-applies exactly as a texture edit already does through the existing watcher path. A second editor is not a sync product here; it is a text editor pointed at a folder.

**AS BUILT (2026-08-29).** `SpectraEngine.Core/Projects/` implements the manifest and the layout: `ProjectFormat` (constants and the folder names), `SpectraProject` (the document), `ProjectReader`, `ProjectWriter`, and `ProjectLayout` (open, save, scaffold, discover). `EngineInfo.ProjectFormatVersion` and `MinimumReadableProjectVersion` land with the reader that enforces them. Oracle: `ProjectTests`. Verified end to end on the demo, which exports itself as a standalone project (`--save-project`) and then runs entirely out of that folder (`--project`): content root, materials, textures, models and the startup map all resolved from it, offscreen probe PASS, editing self-test PASS, zero errors.

*What v1 binds, and what it carries.* Bound: `spectraproject`, `minimumReadableVersion`, `engine`, `name`, `id`, `startupMap`, `maps`, `packs`, `display` (`width`, `height`, `vsync`, `mode`), `defaultBackend`, `allowedBackends`. Carried as preserved members, because nothing in the tree binds to them yet: `input`, `settings`, `bootScript`, `entityDefinitions`. Same three-tier rule the map uses, and for the same reason: a member decoded into a value that means nothing is worse than one carried untouched.

*Decisions this section left open.*

- **`maps` is a listed array AND `DiscoverMaps()` walks the folder, and both are real.** The manifest is the author's ordered list and is what a cook bakes; the folder is what a person actually dropped in. Neither is allowed to silently win: an editor shows the difference and offers to reconcile, because ignoring a map somebody added is the same class of surprise as shipping one they were not ready to.
- **A project opens from its manifest file or from its folder**, since both are what a person means (double-clicking gives the file; dragging a folder or typing a path gives the directory). A folder holding two manifests is **refused rather than guessed at**, because which project a folder *is* is not a question to answer alphabetically.
- **Backend and window-mode names match the command line exactly** (`opengl`, `d3d11`, `d3d12`, `vulkan`; `windowed`, `fullscreen`). Somebody who has typed `d3d11` at a prompt should not discover the file wants `Direct3D11`; two vocabularies for one concept is how a config file becomes something you have to look up. An unrecognised value is refused rather than defaulted, because a mistyped `d3d1` silently becoming OpenGL would ship a game rendering through a path nobody tested.
- **`display.width`/`height` are refused at zero or below**, at the file, rather than surfacing three layers down inside a windowing backend as a failure to create a window.
- **Scaffolding never overwrites a file that is already there.** `.gitignore` and `.gitattributes` become the user's the moment the folder exists, and a scaffold that clobbers a hand-edited one is a scaffold nobody runs twice. Both are shipped because both are load-bearing: the first keeps `cooked/` and `*.user` sidecars out of history, the second pins bundle text to LF.
- **Saving is temp-file-plus-rename and skips an unchanged file**, exactly as a map bundle saves. A project that will not open is worse than one with a stale field in it.

*What `--save-project` does and does not do.* It creates the layout, copies **the whole content root** into `Assets/`, writes the scene as one map, lists it and sets it as `startupMap`. The whole-tree copy is deliberately blunt: working out which files a map actually needs means parsing every material for its textures and every model for its material library, which is the cook's dependency walk (`D4`) with its own correctness rules. A worse guess at it here would ship projects missing one texture nobody noticed; copying everything is wrong in a way that costs disk rather than correctness.

*Not built here.* Nothing about `.vscode/` or `spectra.d.luau`: both depend on arcs that do not exist yet, and `.vscode/` in particular would ship a config pointing at a generated file nobody generates.

**AS BUILT (2026-09-03): `packs` is bound, and a boot mounts it.** `SpectraProject.Packs` leaves the preserved arm; `ProjectPacks` turns a manifest into an ordered list of pack files; `ProjectContentMount` mounts them and hands out the `ContentSourceStack` an `AssetManager` takes. `--pack` on the demo boots a project out of its cooked packs and `--dev` lays loose files over them. Verified end to end - `--save-project`, `scook cook`, `scook verify`, then a run with `--pack` - on d3d11 and on opengl, self-test PASS and nothing at `ERR`, with the whole frame's content resolving out of one `.spack`.

- **The writer emits `packs` only when the manifest names one, at the same anchor a preserved one sat at.** That is the whole difficulty of binding a member the reader was already carrying: the round trip has to stay byte-identical for a file that omits it, which rules out writing an empty array, and byte-identical for a file that has it, which pins where the bound member is emitted among the canonical ones. `ProjectTests` holds a corpus with `packs` between two members this engine still carries, because a PRESERVED member reproduces its own position for free and only a bound one can be in the wrong place.
- **Compact on one line, like `allowedBackends` rather than one-per-line like `maps`.** A manifest's `packs` is a game's own base and patch packs - short names, few of them. A user's forty-mod list is the mount stack's input and never lives in this file.
- **An empty list and an absent member mean different things, and the convention covers the common one.** A project cooked by `scook` and never hand-edited names nothing here and still has a pack, at `cooked/<manifest name>.spack`; `ProjectPacks.ConventionalPackPath` is the one place that is spelled, and `PackFormat.FileExtension` moved into Core so the cook and the boot cannot write and look for different strings - a disagreement there is not an error anywhere, the boot simply finds no pack and falls back to loose files while every log line reads healthy.
- **A missing pack is fatal at the mount, naming the cook command.** Falling back to loose files would make an uncooked project look like a passing cooked run, which is the one thing `--pack` exists to tell apart; the loose mode is what a host gets by not mounting at all.
- **Pure-pack forces hot reload off WITH the reason on the log line.** `TryGetWatchPath` answers false for a pack, so a manager left nominally watching attaches no watcher and the only symptom is that saving a file stops doing anything. The reason also rides `ProjectContentMount.HotReloadDisabledReason`, so a shell can show it rather than re-deriving it.
- **The flatten is a SNAPSHOT of what was on disk at mount.** `PackMountStack` builds one dictionary from every source's enumeration rather than probing per lookup, which is what makes forty mods free per asset and what lets every shadowing decision be recorded where it is made. The consequence, stated rather than discovered: a loose file CREATED after the mount is not served until something remounts. Editing a file that already existed, which is what hot reload is for, is unaffected.
- **The content ROOT stays the project's `Assets/` even in a pure-pack run.** It is the filesystem anchor a model import and an asset's stated `SourcePath` resolve against; what the stack decides is where the BYTES come from. Model FILES still read from disk, which is the standing limitation this stage does not close.

---

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

**The `SC####` prefix collision with `console.md` §4's ConVar generator is settled here, in the cooker's favour, and the console arc renumbers to `CV####`.** The two claims are not symmetric, which is what decided it rather than a coin toss: this prefix is bound into the MSBuild-parseable stderr contract every consumer of `scook` parses, and into a band plan whose 6xxx entry is a **wrap** of another tool's numbers, so renumbering here would break codes that are deliberately not the cooker's to renumber, while the generator is unbuilt and pays nothing for moving.

**AS BUILT (2026-09-02).** The names are `Spectra.Kitchen` (library) and `Spectra.Kitchen.CLI` (producing `scook`), not `SpectraEngine.Cooking`; everything else in this section stands as written. Landed: the four verbs (`cook` and `clean` real, `verify` and `inspect` refusing loudly with `SC0002` and exiting non-zero rather than doing nothing quietly - **both are real as of the cooked-only validation note in 4.6**), the full option set above, `SC####` with its bands and a never-reuse list, the `IRule`/`IRuleContext` seam with `Read`/`Probe`/`Emit`/`Report`, the recording `RuleContext`, the content walker, `CookSession` writing through `PackWriter`, the `--manifest` document, and one rule: `RawCopyRule`. Not landed and named as such by the tool itself: `--watch`, and every rule with a cooked format of its own. The cache and the scheduler landed after this note was written, each with its own AS BUILT below. Oracles: `CookSessionTests` (a cooked project mounts through the engine's own `PackSource`, two cooks are byte-identical), `RuleContextTests` (every `Read` and `Probe` lands in the dependency set, **the miss included**), `ScookCliTests` (exit codes and the canonical stderr line).

**AS BUILT, the scheduler (2026-09-03).** `CookSession.Run` builds the whole work list on
the calling thread in walk order, runs one level of it through `Parallel.For` bounded by
`-j`, and writes results into a pre-sized array indexed by WORK ITEM, never appended in
completion order. Everything that decides a byte (diagnostics, `PackWriter.Add`, the loose
tree, the payload counters, the manifest rows) is then applied on the calling thread in
that index order, so the only thing parallelism can change is how long the cook takes.
`-j1` goes through the same call rather than a serial twin, because a second
implementation is a second thing to keep in step.

There is still exactly ONE level, and that is honest rather than provisional: no rule
declares a dependency on another rule's output yet, so there is nothing to order. A level
is expressed as a RANGE of the work list, so the day a real DAG exists it sorts the list
and turns this into a loop without touching an ordering rule.

`CookGraph` and `StatCache` each took one lock. `ContentStore` deliberately took none: its
temp-file-plus-atomic-rename already survives concurrent writers AND other processes,
which no in-process lock could, and a lock there would put the payload write inside a
critical section for nothing.

Oracles (`CookDeterminismTests`), and they run the real `scook` through `Process.Start`
rather than in process, because .NET's string hash seed is per PROCESS and an in-process
comparison structurally cannot detect the hash-order dependency they exist to catch: two
clean cooks in two processes are byte-identical; a cached cook matches a clean one; `-j1`
matches `-j8`. The fixture is 36 assets across four folders at six sizes whose cycle
lengths share no factor, so size does not follow from walk position. Oracles 1 and 3
compare the MANIFEST as well as the pack, because the pack sorts entries by asset id and
would absorb a scheduling leak in exactly the place one appears first. They were checked
against a deliberately broken build (results written in completion order) and two of them
went red, which is the only evidence that an oracle bites.

**AS BUILT, the cache (2026-09-02).** `Spectra.Kitchen/Cache/` landed and `--cache` is the default; `--no-cache` neither reads nor writes it, and `clean` removes it alongside `cooked/` so that "clean then cook" is a clean cook rather than a replay. The layout is this section's: `.spectra-cook/cas/<2 hex>/<30 hex>` for uncompressed payloads, `graph.bin` for the records, and the stat cache beside it as `stat.bin`. Four deliberate departures from the sketch above, each because building it found something the sketch could not know.

- **Every string in the key is length-prefixed, not `\0`-separated.** The stream is `"SCOOK\0"`, then `u32` cooker version, rule kind id and rule version, then a `u32`-counted run of sorted setting key/value pairs, a `u32`-counted run of tool name/value pairs, the inputs in declared order as `u128` path id plus `u128` content hash, and the missing probes as `u128` path ids - with every string a `u32` UTF-8 byte count followed by its bytes. A separator needs an escaping rule the moment a value can contain one, and getting that wrong does not throw: it makes two different rule runs hash to one key, which the cache then reports as "unchanged, skip it".
- **A tool version is a string, and the list carries a sixth entry.** Most tool versions are not one number (an assembly version is four; an instruction-set baseline is a set), so forcing them through a `u32` means inventing a lossy encoding per tool inside a cache key. The entries, in a fixed append-only order: `encoder`, `shaderFormat`, `mapFormat`, `geometryFormat`, `packFormat`, `isa`.
- **`isa` is the instruction-set baseline, and it is there because of a measurement.** `docs/spikes/2026-09-cook-dependency-spikes.md` §1 encoded one PNG with one encoder and one set of settings and got two different BC7 payloads on either side of AVX2, visually equivalent and byte-different, with a default NativeAOT binary producing the non-AVX2 result *while running on an AVX2 machine*. `InstructionSetBaseline.Token` is a pin when a cook binary declares one (`Pinned`, which a publish with a fixed `IlcInstructionSet` should set) and otherwise a probe of the process: JIT versus AOT, the process architecture, `Vector<T>`'s width, and an append-only list of ISA flags. The pin makes the mismatch impossible and the probe only makes it visible, exactly as the spike's §3 says; unconditional rather than scoped to encoding rules, because a cache that under-invalidates ships wrong bytes while one that over-invalidates costs a rebuild.
- **Settings are per rule, through `IRule.SettingsRead` over a `CookSettingKeys` flags enum.** Hashing the whole settings block into every key means `--script-source strip` re-cooks every texture in the project. `RawCopyRule` declares none, so a profile switch skips the whole content tree. `Jobs`, `Loose`, `Strict`, `UseCache`, `-o` and `--manifest` are not declarable at all: they decide scheduling, container and destination, never a payload.

Two further decisions the sketch did not reach. **A rule that reported a diagnostic is never cached** - the store holds bytes and the graph holds dependencies, and neither holds what the rule *said*, so a hit would drop a warning on every run after the first. And **a graph record remembers the last four keys rather than one**, which is what makes "revert a file to content it held before" a hit rather than a rebuild; the cap is a disk budget, because each remembered generation is a full set of payloads in a store nothing sweeps yet. Oracles: `CookCacheKeyTests` (the stream's opening fields; rule kind, rule version, input contents and input ORDER each move the key; a probe that missed and the same probe finding the file key differently; a setting moves the key only for a rule that declared it; the `isa` token is in the bytes) and `CookCacheTests` (a checkout that rewrites timestamps is a no-op cook, a revert restores the hit, **adding a file a rule probed and missed invalidates that rule**, a settings change invalidates exactly what read it, cook-from-cache and cook-from-clean are one pack, a missing payload or an unparseable graph degrades to a miss).

### 4.2 The cooker is the loud gate; the runtime stays the soft landing

`CLAUDE.md` pins that content errors never reach the draw loop: a missing material degrades to `AssetManager.DefaultMaterial`, a missing texture to the magenta placeholder, each with a warning. That is exactly right for a running frame and exactly wrong for a build step whose job is to stop broken data shipping. **Write the asymmetry down so nobody "fixes" one to match the other.**

**Cook-fatal:** a `.spectramat` naming a nonexistent texture or shader; a brush node whose world transform is non-rigid (report `Scene.DescribeNonRigidDefect`'s message, naming the node); a plane set `Brush`'s constructor rejects; a Luau syntax error; a shader that fails for any requested backend; a duplicate `SceneNode` Guid; a pack asset-id collision; a `faces.Length != planes.Length` mismatch.

**Warning by default, error under `--strict`:** a connection naming a target that does not exist (`P9` explicitly requires it be *kept* and warned about — a mapper who renames a door must not silently lose wiring); an unknown entity classname; an unknown `.spectramat` key (the parser deliberately warns so files stay forward-compatible).

**Built, as one table: `CookGate`.** Every diagnostic the cook and the verify produce goes through `CookDiagnosticLog`, which applies the gate on the way in, so a severity is decided once rather than at each of the forty-odd reporting sites - and a rule that reported a fatal code as a warning cannot quietly weaken a build. Five verdicts: `Fatal`, `WarningUnlessStrict` (the soft list above), `Warning` (about the RUN rather than the data - a cache that would not save, a switch that is not wired up yet - which `--strict` deliberately does not touch), `Note`, and `AsReported` for a code carrying another tool's judgement about a source line, which is `SC6001` today and a wrapped `SS####` once the shader compiler numbers its own. An unclassified code is `Fatal`, because a code defaulted soft ships the data it was written to stop while one defaulted fatal is a build failure fixed by one line; `CookGateTests` fails the test run instead, and holds both halves of the asymmetry against one piece of content so neither can be "fixed" to match the other. The codes 4.2 names on either side of the line are declared and classified today even where the rule that issues them is unbuilt, so a map rule landing later inherits the decision rather than retaking it.

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

**AS BUILT, cooked-only validation (2026-09-03).** All three layers landed, in the shape above, plus one claim the sketch did not name.

- **`PackVerifier` (`Spectra.Kitchen/Packs/`) and `scook verify` are real**, and they exit 0 / 1 / 3 as everything else here does - 3 specifically for a path the filesystem refused, because a typo in a path and a material missing its texture want different people looking at them. The pack is mounted through `PackSource`, the reader a shipped game uses, so a verify is not a claim that some tool can read the file. Diagnostics land in the band that names the failing SUBSYSTEM rather than in the pack band: a texture nobody cooked is `SC5001`, a parser complaint carried rather than swallowed is `SC5002`, and `SC9003`-`SC9006` cover the container itself.
- **The four claims are deliberately independent, and one of them is only reachable because the fixtures re-stamp the digest.** Any edit to a pack breaks the trailing hash, so without a re-stamp every corruption test is the same test: the digest catches it first and nothing past the mount ever runs. Re-stamping separates *a bit rotted on disk* (`SC9003`) from *these bytes were never valid* (`SC9004`) - a payload rewritten together with the hash over it verifies correctly and is still not a deflate stream, which is the exact case a digest structurally cannot see. The table check is a third claim: the writer sorts, which is a statement about the code that wrote a pack; `SC9005` is a statement about the bytes in front of you, and it is the only one of the two that survives the file being edited afterwards.
- **`CheckReferences` is one arm today and is shaped to grow one arm per format**, each parsing with the engine's own reader for that format and reporting in that format's band: `.smodel` -> 3xxx, `.scmap` -> 7xxx (the largest, and what turns this from a spot check into a whole-game one), a shader blob per requested backend -> 6xxx. The authored `.obj`/`.mtl` pair expresses a reference today and is deliberately *not* checked, because it is raw-copied rather than cooked and the importer resolves it at load time.
- **The editor's Validate Cooked action cooks first, every time.** A stale pack passing is worse than no answer: the question somebody asks by clicking it is "will what I have now ship", and a green tick against last week's artifact answers a different one. It runs off the UI thread and names no `Scene`, which is what lets it need no `EnqueueCommand` at all - the render thread's ownership of the graph is untouched by construction rather than by care. It cooks from what is ON DISK, so an unsaved level is not in the pack; that is correct for a build of a source tree and is why the summary names the artifact rather than the viewport.
- **The CI job is `cooked-windows`, and it is a job of its own because every other suite resolves loose files.** That is the editor's workflow and is exactly why a material naming a texture nobody cooked looks perfectly fine in all of them. The export half (`demo --save-project`) needs a graphics device and is best-effort with a timeout: `--exit-after-save` is what makes it terminate at all, and the demo host installs Box3D unconditionally while no job builds `box3d.dll`, so the run can legitimately export and then exit non-zero. The step that decides looks at the artifact rather than at the exit code, a skip is an annotation plus a summary block naming what stopped being checked, and `Test/Spectra.Kitchen.Tests` runs either way on both hosts - it cooks the engine's own `Assets/` and verifies the pack with only that pack mounted, which is the same claim through the same library.
- **`--exit-after-save` cannot be an exit-before-a-window** the way `--export-entity-schema` is: a schema is a fact about the build, and a saved scene is the scene, which does not exist until the render thread has created its meshes and textures. So the run is real and exactly one frame long, ended through `EngineHost.RequestShutdown` because the handler is raised on the render thread and a window belongs to the main one. It is refused without a `--save-map` or `--save-project` to pair with, since on its own it would end the run one frame in with nothing written, which reads as a crash at startup.

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
- ~~**`D17` has an unpredictable tail** that is not the format: choosing an importer.~~ **Answered, 2026-09-03; see §2.3's AS BUILT.** The tail was real and it resolved as BOTH rather than either: the cooker got a hand-rolled managed reader and Assimp stayed the runtime's loose-file importer. Neither half was chosen on AOT posture, which the spike had already measured as fine; the cooker's reader exists because a native importer's version drift would make cooked bytes depend on the cooking machine, which is what the determinism oracles are for. SharpGLTF was never evaluated, because a dependency taken for determinism has to be one this repo can read.
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
6. ~~**GL versus D3D texture origin: which backend is currently correct, and where does the fix go?**~~ **Answered by measurement, 2026-09-02; see §2.2.** The three backends agree; there is no odd one out; an uploaded texture's row 0 is the v = 0 end everywhere, and the engine's convention is that v = 0 is the bottom of the picture. Nothing renders differently on any backend today and `D6` is unblocked. What remains is not a question but a migration, and its shape follows from the measurement: because no backend disagrees, the flip cooked BC data forces belongs in the V axis of UV generation, applied identically everywhere, and never per backend at upload. **`D6` shipped without performing that migration**, because a third option turned out to exist: flip at COOK time, before compression, and declare it with KTX2's `KTXorientation` key. See §2.2.
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
- ~~**BC encoder determinism under parallelism is assumed, not verified.**~~ **Measured, and it holds** (`docs/spikes/2026-09-cook-dependency-spikes.md`, then re-measured per run by `ImageRuleTests.A_parallel_encode_and_a_serial_one_agree`): parallel and serial encodes of one image are byte-equal, as are two encodes in one process and two separate process runs. What is NOT stable is the instruction-set baseline - 310 of 1,024 BC7 blocks differed between an AVX2 host and a non-AVX2 one - which is why `InstructionSetBaseline` is in every cache key and why the cook runs the encoder single-threaded anyway: not for determinism, but because it already parallelises across assets and a second layer competes for the same cores. **Still open**: whether two different AVX2-capable CPUs agree with each other. Only one machine has ever been available, and a fixed-baseline NativeAOT `scook` makes the question moot by construction.
- **`.saudio` is designed against a runtime with no mixer behind it.** The manager half of D18 has landed (device, listener, source pool, streaming voice, sample-frame loop points), so the loop fields now have a consumer; buses, submixes and DSP still have none. The header above is cheap insurance; do not treat it as a finished design.
