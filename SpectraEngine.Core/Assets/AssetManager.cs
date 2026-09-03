using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Assets.Images;
using SpectraEngine.Core.Assets.Sources;
using SpectraEngine.Core.Graphics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;

namespace SpectraEngine.Core.Assets;

/// <summary>
/// Loads on-disk content and owns the GPU resources it creates from it.
/// </summary>
/// <remarks>
/// <para><b>Ownership.</b> Every <see cref="Texture"/> handed out by this class
/// was created by the manager and is destroyed by it — through
/// <see cref="Renderer.DestroyTexture"/>, so the creating renderer also drops it
/// from its tracking list. Callers must never dispose a texture they got from
/// here; they call <see cref="UnloadTexture"/>, or let
/// <see cref="ReleaseGraphicsResources"/> clean up at shutdown. Textures a
/// caller creates itself (e.g. a procedural one) stay the caller's problem.
/// </para>
/// <para><b>Threading.</b> Image decoding is pure CPU and runs on the thread
/// pool; GPU texture creation and destruction happen on the render thread only,
/// matching the engine's contract. Decoded pixel buffers cross the boundary
/// through a <see cref="ConcurrentQueue{T}"/> that
/// <see cref="PumpPendingUploads"/> drains once per frame — the same shape as
/// <see cref="ShaderHotReloader.PumpPendingReloads"/>. Every public member below
/// documents which thread may call it.
/// </para>
/// <para><b>Materials.</b> <see cref="LoadMaterial"/> parses a
/// <c>.spectramat</c> file (see <see cref="MaterialParser"/>) and resolves its
/// texture references through the same texture cache, so materials naming an
/// image with the same sampler state share one GPU texture (differing
/// filter/wrap/colour-space gets its own — see <see cref="LoadTexture"/>). Content problems
/// never propagate as exceptions:
/// a missing material falls back to <see cref="DefaultMaterial"/> and a missing
/// texture to the placeholder checker, both with a warning.
/// </para>
/// <para><b>Models.</b> <see cref="LoadModel"/> and <see cref="RequestModel"/>
/// live in <c>AssetManager.Models.cs</c> and follow the same division of labour:
/// the import (see <see cref="ModelImporter"/>) is pure CPU and may run on the
/// thread pool, while mesh creation and material resolution happen on the render
/// thread inside <see cref="PumpPendingUploads"/>.
/// </para>
/// <para><b>Where the bytes come from.</b> Every texture and material read goes
/// through <see cref="Content"/>, the mounted <see cref="ContentSourceStack"/>,
/// and never through <see cref="System.IO.File"/> directly — so a packed build
/// changes what is mounted and nothing else. That matters most because there are
/// <i>three</i> such reads and they must agree: the decode behind
/// <see cref="LoadTexture"/>, the existence probe that decides whether a
/// material's texture slot gets real content or the placeholder, and the parse
/// behind <see cref="LoadMaterial"/>. Convert two of the three and a packed
/// build resolves no material texture at all while every log line still reads
/// healthy.</para>
/// <para><b>An image read FORKS, and the fork is a fourth thing those reads must
/// agree about.</b> A texture's bytes are the cooked <c>.simage</c> beside the
/// authored file when a mounted source has one, and the authored file otherwise;
/// <see cref="ImageContentPath.Resolve"/> is the single expression of that rule,
/// shared with <c>scook verify</c>, and the material slot's existence probe asks
/// it too. The cooked branch runs no decoder and performs no row flip - a
/// block-compressed payload cannot be flipped at all - and the flip that
/// establishes the engine's v = 0 convention has therefore already happened, at
/// cook time, through this same <see cref="ImageDecoder"/>.</para>
/// <para><b>A cooked upload carries its <see cref="ContentBlob"/> across the
/// queue.</b> The payload is a span straight into a mounted pack's memory-mapped
/// view, so whatever hands it to the render thread has to hand the reference over
/// with it: unmapping a view under a live span is an access violation with no
/// managed stack rather than an exception. Every path that drops a queued upload
/// disposes it, which is why the disposal sits in the pump's loop rather than
/// inside <c>ApplyUpload</c>'s several early returns.</para>
/// <para><b>Degradation is not conditional on the stack.</b> A missing material
/// is <see cref="DefaultMaterial"/> and a missing texture is the magenta
/// checker, whatever is mounted and whether or not the stack is strict: the
/// probes use <see cref="IContentSource.Exists"/>, which never throws, and every
/// open sits inside the same try that already caught an unreadable file.</para>
/// </remarks>
public sealed partial class AssetManager : IDisposable
{
    /// <summary>Name carried by <see cref="DefaultMaterial"/>.</summary>
    public const string DefaultMaterialName = "default";

    // The built-in lit shader's diffuse sampler and tint uniform. The fallback
    // material fills them by name because it is built without ever reading a
    // material file.
    private const string DiffuseSlotName = "uDiffuse";
    private const string BaseColorParameter = "uBaseColor";

    // The surface set the deferred geometry pass writes into the G-buffer. Not
    // read by the forward lit shader, which ignores them by name like any other
    // unknown uniform; seeded on every material anyway so an existing
    // .spectramat that predates PBR renders as a plausible surface in both
    // paths instead of a fully metallic mirror, which is what a zeroed
    // roughness and a metallic left over from the previous draw would give.
    private const string RoughnessParameter = "uRoughness";
    private const string MetallicParameter = "uMetallic";
    private const string AmbientOcclusionParameter = "uAmbientOcclusion";
    private const string EmissiveParameter = "uEmissive";
    private const string ShadingModelParameter = "uShadingModel";

    private readonly ILogger _logger;

    // Guards _textures (and the TextureAsset.LoadFailed / PendingDecodes flags)
    // only. Held for dictionary operations, never across a decode or a GPU call.
    private readonly object _sync = new();

    // Path -> the variants loaded for it. One image can legitimately be loaded
    // more than once, because sampler state is baked into the GPU texture on
    // every backend: a material asking for nearest/clamp and one asking for
    // linearmipmap/repeat need DIFFERENT textures, and collapsing them onto
    // whichever loaded first silently rendered one of the two wrong (a clamped
    // tiling floor smears its edge texels instead of repeating). Variants are
    // in load order, so a path-only lookup resolves deterministically to the
    // first one loaded; buckets hold exactly one entry for all normal content.
    private readonly Dictionary<string, List<TextureAsset>> _textures =
        new(StringComparer.OrdinalIgnoreCase);

    // Guards _materials only. A separate lock from _sync because building a
    // material calls LoadTexture, which takes _sync — nesting the two would be
    // a lock-ordering hazard waiting to happen.
    private readonly object _materialSync = new();
    private readonly Dictionary<string, Material> _materials = new(StringComparer.OrdinalIgnoreCase);

    // Interned material paths that path normalisation rejects outright, so
    // ResolveMaterial warns about each exactly once instead of once per compile.
    // They never reach _materials — there is no key to file them under. Guarded
    // by _materialSync.
    private readonly HashSet<string> _unusableMaterialPaths = new(StringComparer.OrdinalIgnoreCase);

    // Built in the constructor, never replaced: DefaultMaterial has to be
    // non-null from the moment the manager exists, including before a renderer
    // is attached and after teardown. AttachRenderer fills in its shader and
    // placeholder binding; ReleaseGraphicsResources strips them again.
    private readonly Material _defaultMaterial;

    // Background decode -> render thread. Record struct, so draining an empty
    // queue allocates nothing (per-frame work must stay allocation-free).
    private readonly ConcurrentQueue<UploadRequest> _uploads = new();

    // Watcher thread -> render thread: absolute paths of files that changed.
    private readonly ConcurrentQueue<string> _changedFiles = new();

    // One watcher per directory, not per file: a texture folder holds many
    // assets, and a watcher costs a native buffer plus a thread-pool
    // registration. Render thread only. (ShaderHotReloader's leak bug was
    // overwriting a registration without disposing the old watcher — here a
    // directory is registered at most once, and every watcher is disposed in
    // StopWatchingIfUnused / ReleaseGraphicsResources.)
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);

    // Reused across pumps so a frame with pending reloads does not allocate a
    // fresh set; null until the first reload ever arrives.
    private HashSet<string>? _reloadScratch;

    // Same reuse for the handles a change resolves to. Collected under the lock
    // and re-decoded outside it, so no decode is ever queued while _sync is held.
    private List<TextureAsset>? _reloadTargets;

    // Written on the render thread in AttachRenderer, read from any thread in
    // RequestTexture — volatile so background callers see the publication.
    private volatile Texture? _placeholder;

    private Renderer? _renderer;
    private bool _graphicsReleased;
    private bool _disposed;

    /// <summary>
    /// Creates a manager over the process's default content root
    /// (<see cref="ContentRoot.Path"/>).
    /// </summary>
    public AssetManager(ILogger logger)
        : this(logger, ContentRoot.Path, ContentRoot.IsDeveloperBuild)
    {
    }

    /// <summary>
    /// Creates a manager over an explicit content root — used by tests, and by
    /// tools that ship content somewhere other than beside the executable. The
    /// content stack is a single <see cref="LooseFileSource"/> over that folder.
    /// </summary>
    /// <param name="hotReloadEnabled">
    /// Whether to watch loaded files for changes. Defaults to on for the
    /// convenience constructor when the content root came from the source tree.
    /// </param>
    public AssetManager(ILogger logger, string contentRoot, bool hotReloadEnabled = true)
        : this(logger, contentRoot, CreateLooseStack(logger, contentRoot), hotReloadEnabled)
    {
    }

    /// <summary>
    /// Creates a manager over an explicit content stack — the entry point a
    /// packed build, a mod overlay or a cook uses.
    /// </summary>
    /// <remarks>
    /// <paramref name="contentRoot"/> is still required and is still the
    /// filesystem anchor: model import and the tools that copy content around
    /// work in real paths, and an asset's <see cref="TextureAsset.SourcePath"/>
    /// is stated against it. What the stack decides is where the <i>bytes</i> of
    /// a texture or a material come from.
    /// </remarks>
    public AssetManager(
        ILogger logger, string contentRoot, ContentSourceStack content, bool hotReloadEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(contentRoot);
        ArgumentNullException.ThrowIfNull(content);

        _logger = logger;
        // Resolved once here, not recomputed per lookup: resolution walks the
        // filesystem, and every Load/Request would otherwise pay for it.
        ContentRootPath = Path.GetFullPath(contentRoot);
        HotReloadEnabled = hotReloadEnabled;
        Content = content;

        // Seeded like every other material the manager builds, which leaves it
        // with a white base colour: the magenta placeholder checker
        // AttachRenderer binds shows through unmodulated, so a surface that fell
        // back to the default material is unmistakable on screen.
        _defaultMaterial = new Material(null) { Name = DefaultMaterialName };
        SeedBuiltInParameters(_defaultMaterial);
    }

    // The stack a content-root-only caller gets: one loose folder, not strict,
    // which is what the engine has always done.
    private static ContentSourceStack CreateLooseStack(ILogger logger, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(contentRoot);

        var stack = new ContentSourceStack();
        stack.Mount(new LooseFileSource(logger, contentRoot));
        return stack;
    }

    /// <summary>
    /// Absolute content root every relative asset path resolves against.
    /// Immutable; any thread.
    /// </summary>
    public string ContentRootPath { get; }

    /// <summary>
    /// Where content bytes come from. Immutable reference; the stack itself is
    /// thread-safe to read and is only mounted at start-up. Any thread.
    /// </summary>
    public ContentSourceStack Content { get; }

    /// <summary>
    /// Whether changed files are re-decoded and swapped in. Set before loading
    /// anything; flipping it later only affects assets loaded afterwards.
    /// Render thread.
    /// </summary>
    public bool HotReloadEnabled { get; set; }

    /// <summary>
    /// The 8x8 magenta/black checker bound while an async load is still in
    /// flight (and after a failed load), so nothing ever renders untextured.
    /// Null until <see cref="AttachRenderer"/> runs. Render thread.
    /// </summary>
    public Texture? PlaceholderTexture => _placeholder;

    /// <summary>
    /// Number of texture assets currently cached. Counts sampler-state variants
    /// separately, so one image loaded with two different filter/wrap
    /// combinations counts twice. Any thread.
    /// </summary>
    public int TextureCount
    {
        get
        {
            lock (_sync)
            {
                int count = 0;
                foreach (List<TextureAsset> variants in _textures.Values)
                    count += variants.Count;
                return count;
            }
        }
    }

    /// <summary>Number of materials currently cached. Any thread.</summary>
    public int MaterialCount
    {
        get { lock (_materialSync) return _materials.Count; }
    }

    /// <summary>
    /// The built-in fallback material, used whenever a surface names a material
    /// that is missing or unreadable — and by anything that has no material of
    /// its own yet.
    /// </summary>
    /// <remarks>
    /// Never null, at any point in this manager's life: that is the whole point
    /// of it. It is what keeps a bad content reference a magenta surface and a
    /// warning line instead of a null-reference crash in the draw loop. Before
    /// <see cref="AttachRenderer"/> it carries no shader and no texture (nothing
    /// can draw yet anyway); afterwards it is the renderer's default lit shader,
    /// a white tint, and the placeholder checker.
    /// </remarks>
    public Material DefaultMaterial => _defaultMaterial;

    /// <summary>
    /// Optional hook that turns a material file's <c>shader</c> name into a
    /// program. Set it on the render thread before loading materials; returning
    /// null (or leaving it unset) falls back to
    /// <see cref="Renderer.DefaultShader"/>.
    /// </summary>
    /// <remarks>
    /// The asset manager deliberately does not know how to compile shaders — it
    /// would have to own SpectraShade compilation, backend selection and
    /// hot-reload registration to do it. A delegate keeps that knowledge in the
    /// host (which already has all three) without any reflection or runtime
    /// codegen, so the AOT constraint is untouched.
    /// </remarks>
    public Func<string, ShaderProgram?>? ShaderResolver { get; set; }

    /// <summary>
    /// CPU-side start-up: reports the resolved content root. No GPU work, so the
    /// engine calls this on the OS-event thread before the render thread exists.
    /// </summary>
    public void Initialize()
    {
        // First, and unconditionally: when content resolves wrongly the first
        // question is always which source answered, and every line below this
        // one is about the loose folder alone.
        _logger.LogInformation("Content sources: {Sources}", Content.Describe());

        if (!Directory.Exists(ContentRootPath))
        {
            _logger.LogWarning(
                "Asset manager initialized, but content root does not exist: {Root}", ContentRootPath);
            return;
        }

        if (HotReloadEnabled)
        {
            _logger.LogInformation(
                "Asset manager initialized; content root {Root} (hot-reload on)", ContentRootPath);
            return;
        }

        // Loud and once, naming the reason: the engine keeps working off the
        // copy beside the executable, so the only symptom of a lost content
        // root is that saving an asset stops doing anything. A NativeAOT
        // developer build hits this every run. A host that asked for it gets
        // the plain line instead, because that is not a loss.
        string? reason = ContentRoot.NotFromSourceTreeReason;
        if (reason is null)
        {
            _logger.LogInformation(
                "Asset manager initialized; content root {Root} (hot-reload off, disabled by the host)",
                ContentRootPath);
            return;
        }

        _logger.LogWarning(
            "Asset manager initialized; content root {Root} (hot-reload OFF: {Reason}). " +
            "Assets load from the copy beside the executable and edits to them will not be picked up.",
            ContentRootPath, reason);
    }

    /// <summary>
    /// Binds the renderer that will own every texture created from here and
    /// builds the placeholder. Render thread only, after
    /// <see cref="Renderer.Initialize"/> — it creates a GPU resource.
    /// </summary>
    public void AttachRenderer(Renderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (ReferenceEquals(_renderer, renderer)) return;
        if (_renderer is not null)
            throw new InvalidOperationException(
                "AssetManager is already attached to a renderer; release its resources first.");

        _renderer = renderer;
        _graphicsReleased = false;
        Texture placeholder = CreatePlaceholder(renderer);
        _placeholder = placeholder;

        // Complete the fallback material now that a shader and a GPU texture
        // exist. The instance is reused, not replaced, so anything already
        // holding it (a mesh, a brush face) starts drawing correctly.
        _defaultMaterial.Shader = renderer.DefaultShader;
        _defaultMaterial.SetTexture(DiffuseSlotName, 0, placeholder);
        if (renderer.DefaultShader is null)
        {
            _logger.LogWarning(
                "Renderer has no default shader; the default material will not draw until one is assigned");
        }

        _logger.LogDebug("Asset manager attached to {Backend} renderer", renderer.Backend);
    }

    /// <summary>
    /// Loads a texture synchronously: decode and GPU upload both happen on the
    /// calling thread, which must be the render thread. This is the load-time
    /// path — use <see cref="RequestTexture"/> for anything loaded while frames
    /// are running. Returns the cached handle if the path is already loaded
    /// <i>with the same sampler state</i>.
    /// </summary>
    /// <remarks>
    /// <para><b>Sampler state and colour space are part of the identity.</b>
    /// Every backend bakes filter, wrap and sRGB-ness into the GPU texture, so
    /// asking for the same image with different ones loads a second variant
    /// rather than handing back the first one's — which would silently give one
    /// of the two callers the wrong mode. The colour space matters most: one
    /// image legitimately serves as albedo in one material and as a mask in
    /// another, and those need two GPU textures.</para>
    /// <para><b>A previously failed load is retried</b>, into the same handle, so
    /// a material already bound to it picks the result up.</para>
    /// </remarks>
    /// <param name="relativePath">Path under the content root, e.g. <c>Textures/dev_grid.png</c>.</param>
    /// <exception cref="InvalidOperationException">No renderer is attached.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="InvalidDataException">The file is not a supported image.</exception>
    public TextureAsset LoadTexture(
        string relativePath,
        TextureFilter filter = TextureFilter.LinearMipmap,
        TextureWrap wrap = TextureWrap.Repeat,
        TextureColorSpace colorSpace = TextureColorSpace.Srgb)
    {
        Renderer renderer = RequireRenderer();
        string key = ContentRoot.NormalizeRelativePath(relativePath);

        TextureAsset? failed;
        lock (_sync)
        {
            TextureAsset? cached = FindVariant(key, filter, wrap, colorSpace);
            // A handle whose decode failed is NOT a cache hit: this method is
            // documented to read the disk, and the file may well be readable now
            // (authored late, or an art tool that was holding the write lock).
            if (cached is not null && !cached.LoadFailed) return cached;
            failed = cached;
        }

        string absolute = ContentRoot.ResolveAbsolute(ContentRootPath, key);
        // The ticket is taken before the decode so a background decode that
        // finishes while this one reads loses the race instead of overwriting
        // the newer result.
        long sequence = failed?.NextRequestSequence() ?? 0;
        ImageSource image = ReadImageThroughContent(key);
        Texture texture;
        try
        {
            texture = CreateTextureFrom(renderer, key, in image, colorSpace, filter, wrap);
        }
        finally
        {
            // The upload is over the moment CreateTexture returns, so a cooked
            // image's pack reference is released here rather than being held for
            // the texture's life. Held longer it would keep a mount alive
            // forever, which is a leak that presents as an unmountable patch
            // pack much later.
            image.Dispose();
        }

        if (failed is not null)
        {
            // Retry: rebind the existing handle rather than making a new one, so
            // every material that resolved through it recovers.
            Texture previous = failed.Texture;
            failed.Texture = texture;
            failed.IsPlaceholder = false;
            failed.AppliedSequence = sequence;
            failed.Version++;
            lock (_sync) failed.LoadFailed = false;

            DestroyOwned(previous);
            EnsureWatching(key);
            _logger.LogInformation(
                "Loaded texture {Path} after an earlier failure ({Description})", key, image.Describe());
            return failed;
        }

        var asset = new TextureAsset(key, absolute, filter, wrap, colorSpace, texture, isPlaceholder: false)
        {
            // Version 1 means "one texture has been bound", the same state an
            // async load reaches after its first pump; a hot-reload takes it to 2.
            Version = 1,
        };
        asset.AppliedSequence = asset.NextRequestSequence();

        lock (_sync)
        {
            // RequestTexture may run on any thread and could have inserted the
            // same variant while we were decoding; the cache stays
            // single-instance per variant, so the loser's GPU texture is
            // destroyed rather than leaked.
            if (FindVariant(key, filter, wrap, colorSpace) is { } raced)
            {
                renderer.DestroyTexture(texture);
                return raced;
            }
            AddVariant(key, asset);
        }

        EnsureWatching(key);
        _logger.LogInformation("Loaded texture {Path} ({Description})", key, image.Describe());
        return asset;
    }

    /// <summary>
    /// Requests a texture asynchronously. Returns immediately with a handle
    /// bound to the placeholder; the file is decoded on the thread pool and the
    /// real texture is created and swapped in by the next
    /// <see cref="PumpPendingUploads"/>. Callable from any thread — but
    /// <see cref="AttachRenderer"/> must already have run, because the
    /// placeholder is a GPU resource.
    /// </summary>
    /// <remarks>
    /// <para>Sampler state and colour space are part of the cache identity,
    /// exactly as in <see cref="LoadTexture"/>.</para>
    /// <para>Asking again while a decode is in flight is free (at most one
    /// decode per handle is queued at a time), and asking again <i>after a
    /// failure retries</i> — the same contract <see cref="RequestModel"/>
    /// documents, so the two halves of the manager behave alike.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">No renderer is attached yet.</exception>
    public TextureAsset RequestTexture(
        string relativePath,
        TextureFilter filter = TextureFilter.LinearMipmap,
        TextureWrap wrap = TextureWrap.Repeat,
        TextureColorSpace colorSpace = TextureColorSpace.Srgb)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Texture placeholder = _placeholder
            ?? throw new InvalidOperationException(
                "AssetManager.RequestTexture needs a renderer; call AttachRenderer on the render thread first.");

        string key = ContentRoot.NormalizeRelativePath(relativePath);
        TextureAsset asset;
        bool retry = false;
        lock (_sync)
        {
            if (FindVariant(key, filter, wrap, colorSpace) is { } cached)
            {
                // Retry a failed handle, but only when nothing is already on its
                // way back: polling this from a frame loop must not pile up one
                // decode per frame.
                if (!cached.LoadFailed || cached.PendingDecodes > 0)
                    return cached;

                cached.PendingDecodes++;
                asset = cached;
                retry = true;
            }
            else
            {
                asset = new TextureAsset(
                    key,
                    ContentRoot.ResolveAbsolute(ContentRootPath, key),
                    filter,
                    wrap,
                    colorSpace,
                    placeholder,
                    isPlaceholder: true);
                asset.PendingDecodes++;
                AddVariant(key, asset);
            }
        }

        if (retry)
            _logger.LogDebug("Retrying the failed decode of {Path}", key);
        QueueDecode(asset);
        return asset;
    }

    /// <summary>
    /// Looks up an already-loaded texture without touching the disk — the first
    /// sampler-state variant loaded for the path (see <see cref="LoadTexture"/>);
    /// use the overload to ask for a specific one. Any thread, though the
    /// returned handle's <see cref="TextureAsset.Texture"/> is only safe to read
    /// on the render thread.
    /// </summary>
    public bool TryGetTexture(string relativePath, [MaybeNullWhen(false)] out TextureAsset asset)
    {
        string key = ContentRoot.NormalizeRelativePath(relativePath);
        lock (_sync)
        {
            if (_textures.TryGetValue(key, out List<TextureAsset>? variants) && variants.Count > 0)
            {
                asset = variants[0];
                return true;
            }
        }

        asset = null;
        return false;
    }

    /// <summary>
    /// Looks up the already-loaded texture for a path <i>and</i> a specific
    /// sampler state — the exact handle <see cref="LoadTexture"/> would return
    /// for the same arguments. Any thread.
    /// </summary>
    public bool TryGetTexture(
        string relativePath,
        TextureFilter filter,
        TextureWrap wrap,
        [MaybeNullWhen(false)] out TextureAsset asset,
        TextureColorSpace colorSpace = TextureColorSpace.Srgb)
    {
        string key = ContentRoot.NormalizeRelativePath(relativePath);
        lock (_sync)
            asset = FindVariant(key, filter, wrap, colorSpace);
        return asset is not null;
    }

    /// <summary>
    /// Loads a <c>.spectramat</c> material file and returns the cached instance
    /// for that path — see <see cref="MaterialParser"/> for the format. Textures
    /// it references are resolved through the texture cache, so two materials
    /// naming the same image share one GPU texture.
    /// </summary>
    /// <remarks>
    /// <para>Render thread only, and synchronous: a material is load-time content
    /// that should be correct on the frame it first draws, and resolving its
    /// textures creates GPU resources.</para>
    /// <para><b>This never throws for content reasons.</b> A missing or
    /// unreadable file yields <see cref="DefaultMaterial"/>; a missing or
    /// undecodable texture yields the placeholder checker in that slot; a
    /// malformed line is skipped. Every one of those is logged as a warning and
    /// the frame keeps rendering. Only a caller error (no renderer attached, a
    /// path escaping the content root) is raised.</para>
    /// </remarks>
    /// <param name="relativePath">Path under the content root, e.g. <c>Materials/wall.spectramat</c>.</param>
    /// <exception cref="InvalidOperationException">No renderer is attached.</exception>
    public Material LoadMaterial(string relativePath)
    {
        RequireRenderer();
        string key = ContentRoot.NormalizeRelativePath(relativePath);

        lock (_materialSync)
        {
            if (_materials.TryGetValue(key, out Material? cached))
                return cached;
        }

        Material material;
        if (!Content.Exists(key))
        {
            // The single most common content bug (a renamed or never-authored
            // material) must degrade, not crash — that is what DefaultMaterial
            // is for.
            _logger.LogWarning("Material {Path} not found; using the default material", key);
            material = _defaultMaterial;
        }
        else
        {
            try
            {
                material = BuildMaterial(key);
            }
            catch (Exception ex)
            {
                // Only I/O can land here: the parser reports its problems as
                // warnings rather than exceptions.
                _logger.LogError(ex, "Reading material {Path} failed; using the default material", key);
                material = _defaultMaterial;
            }
        }

        lock (_materialSync)
        {
            // Cache the fallback under the requested key too: a repeat request
            // then costs a dictionary probe instead of another stat() and
            // another identical warning every time the caller asks.
            if (_materials.TryGetValue(key, out Material? raced))
                return raced;
            _materials[key] = material;
        }

        return material;
    }

    /// <summary>
    /// Turns the interned <see cref="MaterialRef"/> a compiled surface carries
    /// into a real material — the one point where the CSG pipeline's pure-value
    /// material references become asset objects.
    /// </summary>
    /// <remarks>
    /// <para>Render thread only, and intended for mesh-upload time: the compile
    /// itself must never call this (it runs on a background thread and would be
    /// touching GPU-owned state). Resolution is a dictionary probe once the
    /// material is loaded, so resolving a mesh's
    /// <see cref="Bsp.ChunkMesh.Submeshes"/> per upload is cheap.</para>
    /// <para>Degrades exactly like <see cref="LoadMaterial"/>: the default
    /// reference, an id this process never interned, and a path whose file is
    /// missing all yield <see cref="DefaultMaterial"/> rather than throwing or
    /// returning null.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">No renderer is attached.</exception>
    public Material ResolveMaterial(MaterialRef reference)
    {
        if (reference.IsDefault || !MaterialRegistry.TryGetPath(reference, out string path))
            return _defaultMaterial;

        try
        {
            return LoadMaterial(path);
        }
        catch (ArgumentException ex)
        {
            // An interned path is content — MaterialRegistry only trims it and
            // folds separators, so "../evil.spectramat", "C:/x.spectramat" and
            // "/" all survive interning and are rejected by path normalisation
            // here. This runs inside the static-world GPU swap on the render
            // thread, where a throw is an unrecoverable render-thread crash that
            // repeats on every compile — the whole point of this method is that
            // it degrades. Warn once per bad path, then behave like any other
            // unusable material reference.
            bool first;
            lock (_materialSync) first = _unusableMaterialPaths.Add(path);
            if (first)
            {
                _logger.LogWarning(
                    "Material path '{Path}' is not usable ({Message}); using the default material",
                    path, ex.Message);
            }
            return _defaultMaterial;
        }
    }

    /// <summary>
    /// Looks up an already-loaded material without touching the disk. Note that
    /// a path whose file was missing is cached as <see cref="DefaultMaterial"/>,
    /// so a hit does not prove the file existed. Any thread.
    /// </summary>
    public bool TryGetMaterial(string relativePath, [MaybeNullWhen(false)] out Material material)
    {
        string key = ContentRoot.NormalizeRelativePath(relativePath);
        lock (_materialSync)
            return _materials.TryGetValue(key, out material);
    }

    /// <summary>
    /// Applies everything the background workers finished since the last call:
    /// creates the GPU textures and swaps them into their handles, creates the
    /// GPU meshes for any model whose import landed, and turns file-change
    /// notifications into new decode requests. Render thread only; the engine
    /// calls it once per frame. Returns the number of assets applied (textures
    /// and models together). Allocation-free when nothing is pending.
    /// </summary>
    public int PumpPendingUploads()
    {
        if (_renderer is null || _graphicsReleased) return 0;

        DispatchFileChanges();

        int applied = 0;
        while (_uploads.TryDequeue(out UploadRequest request))
        {
            // The queued decode is over the moment its result is in hand,
            // whether or not applying it succeeds — clear the in-flight count
            // first so a retry is possible even when the apply below drops the
            // result. Mirrors EndImport on the model side.
            EndDecode(request.Asset);
            try
            {
                if (ApplyUpload(in request))
                    applied++;
            }
            finally
            {
                // ONE disposal site, covering every early return inside
                // ApplyUpload. A cooked upload holds a pack reference, and a
                // dropped request (an unloaded handle, a stale ticket, a GPU
                // failure) that leaked one would defer that pack's unmount for
                // the life of the process with nothing reporting it.
                request.Image.Dispose();
            }
        }

        return applied + PumpPendingModelImports();
    }

    /// <summary>
    /// Drops a texture from the cache — every sampler-state variant loaded for
    /// the path — and destroys its GPU resources through the creating renderer
    /// (which also deregisters them). Any handle still held by a caller degrades
    /// to the placeholder rather than to a disposed texture. Returns false if
    /// the path was not loaded. Render thread only.
    /// </summary>
    public bool UnloadTexture(string relativePath)
    {
        string key = ContentRoot.NormalizeRelativePath(relativePath);
        List<TextureAsset>? variants;
        lock (_sync)
        {
            if (!_textures.Remove(key, out variants))
                return false;
        }

        for (int i = 0; i < variants.Count; i++)
        {
            TextureAsset asset = variants[i];
            DestroyOwned(asset.Texture);
            asset.Texture = _placeholder!;
            asset.IsPlaceholder = true;
        }

        // Every variant shares the file, so one call covers them all.
        StopWatchingIfUnused(key);

        _logger.LogInformation("Unloaded texture {Path}", key);
        return true;
    }

    /// <summary>
    /// Destroys every GPU resource this manager owns and stops all file
    /// watching. Render thread only — it calls
    /// <see cref="Renderer.DestroyTexture"/> — and must run BEFORE
    /// <see cref="Renderer.Shutdown"/>, i.e. inside the render loop, not in the
    /// engine's main-thread teardown. Idempotent.
    /// </summary>
    public void ReleaseGraphicsResources()
    {
        if (_graphicsReleased) return;
        _graphicsReleased = true;

        foreach (FileSystemWatcher watcher in _watchers.Values)
            watcher.Dispose();
        _watchers.Clear();

        // Late arrivals from decodes still in flight have nowhere to go - but a
        // cooked one holds a pack reference, so they are drained THROUGH their
        // disposal rather than discarded. Dropped silently, a session teardown
        // would leave every in-flight texture pinning its mount.
        while (_uploads.TryDequeue(out UploadRequest stranded)) stranded.Image.Dispose();
        while (_changedFiles.TryDequeue(out _)) { }

        // Models first: their meshes are destroyed through the renderer, which
        // is still attached here, and their materials reference textures the
        // texture pass below is about to destroy.
        ReleaseModelResources();

        var assets = new List<TextureAsset>();
        lock (_sync)
        {
            foreach (List<TextureAsset> variants in _textures.Values)
                assets.AddRange(variants);
            _textures.Clear();
        }

        int destroyed = 0;
        foreach (TextureAsset asset in assets)
        {
            if (DestroyOwned(asset.Texture)) destroyed++;
            asset.IsPlaceholder = true;
        }

        // Materials own no GPU state of their own, but their bindings point at
        // textures that are being destroyed right now — drop the cache and strip
        // the fallback back to its pre-attach state so nothing resolves to a
        // disposed object afterwards. DefaultMaterial itself survives: callers
        // may still be holding it, and it must never become null.
        lock (_materialSync) _materials.Clear();
        _defaultMaterial.ClearTextures();
        _defaultMaterial.Shader = null;

        if (_placeholder is { } placeholder)
        {
            _renderer?.DestroyTexture(placeholder);
            _placeholder = null;
        }

        _renderer = null;
        if (destroyed > 0)
            _logger.LogInformation("Asset manager released {Count} GPU textures", destroyed);
    }

    /// <summary>
    /// CPU-side teardown, mirroring <see cref="Initialize"/>. Safe on the main
    /// thread: GPU resources must already have gone through
    /// <see cref="ReleaseGraphicsResources"/> on the render thread — if they
    /// have not, this says so rather than touching the GPU from the wrong thread.
    /// </summary>
    public void Shutdown()
    {
        // Again, and deliberately: a background decode can land in the window
        // between ReleaseGraphicsResources draining the queue and the manager
        // being disposed, and the pump is no longer running to take it. A cooked
        // one holds a pack reference, so leaving it there would defer that
        // pack's unmount for the life of the process - which in a shell that
        // opens and closes sessions is a mount leaked per session.
        while (_uploads.TryDequeue(out UploadRequest stranded)) stranded.Image.Dispose();

        // Sounds hold content references rather than GPU objects, so they are
        // released here rather than in ReleaseGraphicsResources - and they must
        // be released somewhere, because an open one pins its pack's mapping for
        // the life of the process.
        ReleaseAudioResources();

        if (!_graphicsReleased && _renderer is not null)
        {
            _logger.LogWarning(
                "Asset manager shut down with GPU textures still live; " +
                "ReleaseGraphicsResources must run on the render thread before Shutdown");

            // Watchers are not GPU state, so they can still be cleaned up here.
            foreach (FileSystemWatcher watcher in _watchers.Values)
                watcher.Dispose();
            _watchers.Clear();
            lock (_sync) _textures.Clear();
            lock (_materialSync) _materials.Clear();
            lock (_modelSync) _models.Clear();
            _renderer = null;
        }

        _disposed = true;
        _logger.LogInformation("Asset manager shut down");
    }

    /// <inheritdoc cref="Shutdown"/>
    public void Dispose() => Shutdown();

    // ---- internals -------------------------------------------------------

    /// <summary>
    /// Records that a file changed on disk, exactly as the watcher callback
    /// does. Any thread (the watcher raises on a thread-pool thread); the
    /// re-decode is started by the next <see cref="PumpPendingUploads"/>.
    /// Tests drive this directly so the reload path is covered without
    /// depending on filesystem-notification timing.
    /// </summary>
    internal void NotifyFileChanged(string absolutePath)
        => _changedFiles.Enqueue(Path.GetFullPath(absolutePath));

    /// <summary>Directories currently watched for texture changes. Render thread.</summary>
    internal int WatchedDirectoryCount => _watchers.Count;

    /// <summary>
    /// Writes the built-in lit shader's material-facing parameters at their
    /// neutral values. Every material this manager builds starts from these, so
    /// a surface never inherits another material's value for a parameter its own
    /// file did not mention.
    /// </summary>
    /// <remarks>
    /// Only the built-in shader's own parameters are seeded, because they are
    /// the only ones whose neutral value the engine knows. A material naming a
    /// custom shader still has to set that shader's parameters itself —
    /// SpectraShade has no notion of a uniform default to read one from.
    /// </remarks>
    private static void SeedBuiltInParameters(Material material)
    {
        // White: the diffuse texture (or the magenta placeholder) shows through
        // unmodulated, which is what makes a fallback surface unmistakable.
        material.SetVector3(BaseColorParameter, Vector3.One);

        // A plain dielectric. These are what a material file overrides to be
        // anything else, and leaving them unset is not an option: the deferred
        // geometry pass draws every surface with one program, so an omitted
        // parameter inherits the previous draw's value rather than a default,
        // and a wall would wear whatever the last metal it followed was wearing.
        material.SetFloat(RoughnessParameter, 0.65f);
        material.SetFloat(MetallicParameter, 0f);
        material.SetFloat(AmbientOcclusionParameter, 1f);
        material.SetVector3(EmissiveParameter, Vector3.Zero);
        material.SetFloat(ShadingModelParameter, 0f);
    }

    // Parses the material and turns the definition into a live material. Content
    // problems become warnings and a degraded binding; nothing here throws
    // except the read itself, which LoadMaterial catches.
    private Material BuildMaterial(string key)
    {
        MaterialDefinition definition = ParseMaterialThroughContent(key);
        foreach (string warning in definition.Warnings)
            _logger.LogWarning("Material {Path}: {Warning}", key, warning);

        var material = new Material(ResolveShader(definition.ShaderName, key))
        {
            Name = Path.GetFileNameWithoutExtension(key),
            SourcePath = key,
        };

        // Seed the built-in shader's parameters BEFORE the file's own, so a
        // material that omits one still pushes a defined value for it. Without
        // this, an omitted uniform is simply never written: the shader keeps
        // whatever the previous draw's material left in it (a red tint bleeding
        // onto an untinted surface, flipping with draw order as culling
        // reorders the batches), and on the very first draw it is whatever the
        // backend zero-initialised — uBaseColor = 0 renders a fully textured
        // surface solid black. The engine's fallback material seeds the same
        // value in the constructor; file-built materials get it here.
        SeedBuiltInParameters(material);

        IReadOnlyList<MaterialParameter> parameters = definition.Parameters;
        for (int i = 0; i < parameters.Count; i++)
        {
            MaterialParameter parameter = parameters[i];
            switch (parameter.Kind)
            {
                case MaterialParameterKind.Float: material.SetFloat(parameter.Name, parameter.AsFloat); break;
                case MaterialParameterKind.Vector2: material.SetVector2(parameter.Name, parameter.AsVector2); break;
                case MaterialParameterKind.Vector3: material.SetVector3(parameter.Name, parameter.AsVector3); break;
                default: material.SetVector4(parameter.Name, parameter.AsVector4); break;
            }
        }

        IReadOnlyList<MaterialTextureSlot> slots = definition.Textures;
        for (int i = 0; i < slots.Count; i++)
            BindTextureSlot(material, key, slots[i]);

        _logger.LogInformation(
            "Loaded material {Path} ({Parameters} parameter(s), {Textures} texture(s))",
            key, material.ParameterCount, material.TextureCount);
        return material;
    }

    // Resolves one texture slot, degrading to the placeholder (never to an
    // unbound sampler, which would read whatever the last draw left on the unit)
    // whenever the image cannot be loaded.
    private void BindTextureSlot(Material material, string materialKey, in MaterialTextureSlot slot)
    {
        string textureKey;
        try
        {
            textureKey = ContentRoot.NormalizeRelativePath(slot.TexturePath);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(
                "Material {Path}: texture path '{Texture}' for '{Slot}' is not usable ({Message}); using the placeholder",
                materialKey, slot.TexturePath, slot.Name, ex.Message);
            BindPlaceholder(material, slot);
            return;
        }

        // The same stack AND the same fork the read below uses, deliberately: an
        // existence probe that asked the filesystem while the open asked an
        // archive - or that looked for the PNG while the open took the cooked
        // .simage - would bind the placeholder into every packed material and log
        // nothing wrong.
        if (!ImageExists(textureKey))
        {
            _logger.LogWarning(
                "Material {Path}: texture {Texture} for '{Slot}' not found; using the placeholder",
                materialKey, textureKey, slot.Name);
            BindPlaceholder(material, slot);
            return;
        }

        try
        {
            TextureAsset asset = LoadTexture(textureKey, slot.Filter, slot.Wrap, slot.ColorSpace);
            // Bound as a handle, not a Texture: the material then follows the
            // asset through hot-reloads instead of pinning today's GPU object.
            material.SetTexture(slot.Name, slot.Unit, asset);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Material {Path}: texture {Texture} for '{Slot}' failed to load ({Message}); using the placeholder",
                materialKey, textureKey, slot.Name, ex.Message);
            BindPlaceholder(material, slot);
        }
    }

    private void BindPlaceholder(Material material, in MaterialTextureSlot slot)
    {
        if (_placeholder is { } placeholder)
            material.SetTexture(slot.Name, slot.Unit, placeholder);
    }

    private ShaderProgram? ResolveShader(string? shaderName, string materialKey)
    {
        ShaderProgram? fallback = _renderer?.DefaultShader;
        if (string.IsNullOrEmpty(shaderName)) return fallback;

        if (ShaderResolver?.Invoke(shaderName) is { } resolved)
            return resolved;

        if (string.Equals(shaderName, MaterialParser.BuiltInShaderName, StringComparison.OrdinalIgnoreCase))
            return fallback;

        _logger.LogWarning(
            "Material {Path}: no shader named '{Shader}'; using the built-in lit shader",
            materialKey, shaderName);
        return fallback;
    }

    private Renderer RequireRenderer()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer
            ?? throw new InvalidOperationException(
                "AssetManager has no renderer; call AttachRenderer on the render thread first.");
    }

    // Where an image's bytes come from, asked exactly once per read and shared
    // with the material slot's existence probe below. The whole redirection is
    // ImageContentPath's, so the cooker, the verifier and this class cannot
    // disagree about which of two files an image is.
    private string ResolveImagePath(string key) => ImageContentPath.Resolve(Content, key);

    // The probe half of the same rule. It asks the SAME stack the open asks, on
    // the SAME resolved path, which is the whole reason it is expressed here
    // rather than inline: a probe that looked only for the authored file would
    // bind the magenta placeholder into every material of a build whose textures
    // are all cooked, and every log line would read healthy.
    private bool ImageExists(string key) => Content.Exists(ResolveImagePath(key));

    // One of the content reads, and the only one that forks. Any thread: opening,
    // decoding and parsing a .simage header are all pure CPU, which is what lets
    // the async path run this on the thread pool.
    private ImageSource ReadImageThroughContent(string key)
    {
        // A miss is reported as the I/O failure LoadTexture documents and
        // QueueDecode catches, so the caller sees no difference between content
        // that is absent and content that could not be read — neither is
        // recoverable at this level.
        string resolved = ResolveImagePath(key);
        ContentBlob blob = OpenOrThrow(resolved);

        if (!ImageContentPath.IsCooked(resolved))
        {
            // The loose path, unchanged: decode, and flip the rows on the way in.
            // Nothing needs the blob's bytes past this call.
            using (blob) return new ImageSource(ImageDecoder.Decode(blob.Span, key), null, null);
        }

        try
        {
            // No decode, no row flip, no copy. The blob TRAVELS with the result,
            // because the mips it describes are offsets into these very bytes.
            SimageInfo cooked = SimageReader.Read(blob.Span, resolved);
            return new ImageSource(null, blob, cooked);
        }
        catch
        {
            // A refusal here leaves nobody holding the reference, so it is
            // released before the message goes up. Without this a project whose
            // .simage files are one version stale would leak a pack reference per
            // texture.
            blob.Dispose();
            throw;
        }
    }

    // The two ways a texture reaches the GPU, in one place so the colour-space
    // warning and the sampler state cannot be applied to one and not the other.
    private Texture CreateTextureFrom(
        Renderer renderer,
        string key,
        in ImageSource image,
        TextureColorSpace colorSpace,
        TextureFilter filter,
        TextureWrap wrap)
    {
        WarnIfSrgbUnavailable(key, image.Format, colorSpace);

        if (image.Decoded is { } decoded)
        {
            return renderer.CreateTexture(
                decoded.Pixels, decoded.Width, decoded.Height, decoded.Format, colorSpace, filter, wrap);
        }

        SimageInfo cooked = image.Cooked!;

        // The CALLER's colour space, never the file's. Whether a block of bytes
        // is colour or data is a property of the material slot rather than of the
        // image - which is exactly why this cache keys on it - so the cooker
        // writes the UNORM form and one cooked artifact serves both an albedo and
        // a mask. See SimageFormat.TryResolveVkFormat.
        return renderer.CreateTexture(new TextureUploadDesc(
            cooked.Format, colorSpace, image.Blob!.Span, cooked.Mips, filter, wrap));
    }

    // The second of the three. The bytes never touch the filesystem, so a packed
    // material parses with no temporary file in between.
    private MaterialDefinition ParseMaterialThroughContent(string key)
    {
        using ContentBlob blob = OpenOrThrow(key);
        return MaterialParser.ParseUtf8(blob.Span, key);
    }

    private ContentBlob OpenOrThrow(string key)
    {
        if (!Content.TryOpen(key, out ContentBlob? blob))
            throw new FileNotFoundException($"Content '{key}' is not available from any mounted source.", key);

        return blob;
    }

    // Decode off the render thread and hand the pixels back through the queue.
    // Failures are queued too, so the pump can log them on the render thread
    // instead of them vanishing into an unobserved task.
    // Claims one in-flight decode slot on a handle. RequestTexture claims its
    // own inside the lock it already holds (it has to decide whether to queue at
    // all in the same critical section); the hot-reload path uses this.
    private void BeginDecode(TextureAsset asset)
    {
        lock (_sync) asset.PendingDecodes++;
    }

    // Releases the slot when a result comes back, whatever the result was.
    private void EndDecode(TextureAsset asset)
    {
        lock (_sync)
        {
            if (asset.PendingDecodes > 0) asset.PendingDecodes--;
        }
    }

    private void QueueDecode(TextureAsset asset)
    {
        long sequence = asset.NextRequestSequence();
        _ = Task.Run(() =>
        {
            try
            {
                // Ownership of a cooked image's pack reference transfers to the
                // queue here and is released by the pump, which is the one place
                // that sees every outcome.
                ImageSource image = ReadImageThroughContent(asset.RelativePath);
                _uploads.Enqueue(new UploadRequest(asset, sequence, image, null));
            }
            catch (Exception ex)
            {
                _uploads.Enqueue(new UploadRequest(asset, sequence, default, ex.Message));
            }
        });
    }

    private bool ApplyUpload(in UploadRequest request)
    {
        TextureAsset asset = request.Asset;

        // The handle may have left the cache while the decode ran — UnloadTexture,
        // or an unload-then-reload that put a fresh handle under the same key.
        // Creating a GPU texture for it now would produce one that nothing ever
        // destroys (ReleaseGraphicsResources only walks _textures) and would
        // resurrect the directory watcher the unload just disposed.
        if (!IsCachedVariant(asset))
        {
            _logger.LogDebug(
                "Dropping the decode of {Path}: its handle was unloaded before the upload landed",
                asset.RelativePath);
            return false;
        }

        // A newer decode already landed (rapid saves, or a reload racing the
        // initial load) — this one is stale and must not overwrite it.
        if (request.Sequence <= asset.AppliedSequence)
            return false;

        if (!request.Image.HasContent)
        {
            _logger.LogError("Texture load failed ({Path}): {Error}", asset.RelativePath, request.Error);
            // Keep whatever is bound (placeholder, or the previous version on a
            // failed hot-reload) so the frame still draws something sane, and
            // mark the handle retryable: the entry is already in the cache, so
            // without this every later request would hand back this placeholder
            // for the rest of the process's life.
            asset.AppliedSequence = request.Sequence;
            lock (_sync) asset.LoadFailed = true;
            return false;
        }

        ImageSource image = request.Image;
        Texture created;
        try
        {
            created = CreateTextureFrom(
                _renderer!, asset.RelativePath, in image, asset.ColorSpace, asset.Filter, asset.Wrap);
        }
        catch (Exception ex)
        {
            // A GPU failure must not take the render loop down mid-drain.
            _logger.LogError(ex, "Creating GPU texture for {Path} failed", asset.RelativePath);
            return false;
        }

        Texture previous = asset.Texture;
        asset.Texture = created;
        asset.IsPlaceholder = false;
        asset.AppliedSequence = request.Sequence;
        asset.Version++;
        lock (_sync) asset.LoadFailed = false;

        DestroyOwned(previous);
        EnsureWatching(asset.RelativePath);

        _logger.LogInformation(
            "Texture {Verb} {Path} ({Description})",
            asset.Version > 1 ? "reloaded" : "ready",
            asset.RelativePath, image.Describe());
        return true;
    }

    // Destroys a texture this manager created; the shared placeholder is not
    // per-asset state and outlives every swap, so it is skipped here.
    private bool DestroyOwned(Texture? texture)
    {
        if (texture is null || ReferenceEquals(texture, _placeholder)) return false;
        _renderer?.DestroyTexture(texture);
        return true;
    }

    // Coalesce the watcher's notifications (one save often fires several) and
    // kick off a background re-decode per affected asset.
    private void DispatchFileChanges()
    {
        if (!_changedFiles.TryDequeue(out string? first)) return;

        HashSet<string> seen = _reloadScratch ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        seen.Clear();
        seen.Add(first);
        while (_changedFiles.TryDequeue(out string? path))
            seen.Add(path);

        foreach (string path in seen)
        {
            // Every sampler-state variant of the changed file needs its own
            // re-decode: they are separate GPU textures, so reloading only one
            // would leave the others showing the pre-edit image.
            _reloadTargets ??= [];
            _reloadTargets.Clear();
            lock (_sync)
            {
                foreach (List<TextureAsset> variants in _textures.Values)
                {
                    for (int i = 0; i < variants.Count; i++)
                    {
                        if (IsSourceOf(variants[i], path)) _reloadTargets.Add(variants[i]);
                    }
                }
            }

            for (int i = 0; i < _reloadTargets.Count; i++)
            {
                TextureAsset match = _reloadTargets[i];
                _logger.LogDebug("Texture changed on disk, re-decoding: {Path}", match.RelativePath);
                BeginDecode(match);
                QueueDecode(match);
            }
        }
    }

    // Whether a changed file is one this handle would read. BOTH names count,
    // because the read resolves between them every time: a cook landing a
    // .simage beside a PNG must reach a texture already loaded from the PNG, and
    // an edit to the PNG must reach one already loaded from a .simage that is now
    // stale. Compared as strings rather than probed, because this runs once per
    // variant per change notification and a probe here would be a stack lookup
    // per texture per save.
    private static bool IsSourceOf(TextureAsset asset, string fullPath) =>
        string.Equals(asset.SourcePath, fullPath, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            Path.ChangeExtension(asset.SourcePath, SimageFormat.FileExtension),
            fullPath,
            StringComparison.OrdinalIgnoreCase);

    // Watching is a property of LOOSE content: a source that cannot name a file
    // on disk (a packed archive) supplies no watch path and is simply not
    // watched, which is why this takes the content-relative path and asks the
    // stack rather than resolving one against the content root itself.
    private void EnsureWatching(string relativePath)
    {
        if (!HotReloadEnabled || _graphicsReleased) return;
        // The RESOLVED path, so a tree whose textures are all cooked still gets a
        // watcher: asking for the authored PNG in a folder that holds only
        // .simage files finds no watch path and silently watches nothing.
        if (!Content.TryGetWatchPath(ResolveImagePath(relativePath), out string? watchPath)) return;

        string? directory = Path.GetDirectoryName(watchPath);
        if (directory is null || !Directory.Exists(directory)) return;
        // Exactly one watcher per directory, ever: creating a second would leak
        // the first's native buffer and double every change notification.
        if (_watchers.ContainsKey(directory)) return;

        FileSystemWatcher watcher;
        try
        {
            watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
        }
        catch (Exception ex)
        {
            // Watching is a developer convenience; a platform that refuses it
            // must not break loading.
            _logger.LogWarning("Not watching {Directory} for texture changes: {Message}", directory, ex.Message);
            return;
        }

        // Events arrive on a thread-pool thread — enqueue only, never decode or
        // touch the cache from here.
        watcher.Changed += (_, e) => NotifyFileChanged(e.FullPath);
        watcher.Created += (_, e) => NotifyFileChanged(e.FullPath);
        watcher.Renamed += (_, e) => NotifyFileChanged(e.FullPath);

        _watchers[directory] = watcher;
        _logger.LogDebug("Watching texture directory: {Directory}", directory);
    }

    private void StopWatchingIfUnused(string relativePath)
    {
        if (!Content.TryGetWatchPath(ResolveImagePath(relativePath), out string? watchPath)) return;

        string? directory = Path.GetDirectoryName(watchPath);
        if (directory is null || !_watchers.TryGetValue(directory, out FileSystemWatcher? watcher)) return;

        lock (_sync)
        {
            foreach (List<TextureAsset> variants in _textures.Values)
            {
                for (int i = 0; i < variants.Count; i++)
                {
                    if (string.Equals(
                            Path.GetDirectoryName(variants[i].SourcePath),
                            directory,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
            }
        }

        watcher.Dispose();
        _watchers.Remove(directory);
    }

    // ---- texture cache helpers -------------------------------------------
    // All three assume _sync is already held, except IsCachedVariant which takes
    // it itself (it is called from the pump, outside any critical section).

    private TextureAsset? FindVariant(
        string key, TextureFilter filter, TextureWrap wrap, TextureColorSpace colorSpace)
    {
        if (!_textures.TryGetValue(key, out List<TextureAsset>? variants)) return null;

        for (int i = 0; i < variants.Count; i++)
        {
            TextureAsset candidate = variants[i];
            if (candidate.Filter == filter && candidate.Wrap == wrap && candidate.ColorSpace == colorSpace)
                return candidate;
        }
        return null;
    }

    private void AddVariant(string key, TextureAsset asset)
    {
        if (!_textures.TryGetValue(key, out List<TextureAsset>? variants))
        {
            // Capacity 1: a second sampler-state variant of one image is the
            // exception, not the rule.
            variants = new List<TextureAsset>(1);
            _textures[key] = variants;
        }
        variants.Add(asset);
    }

    // Whether this exact handle is still the cache's entry for its path and
    // sampler state — false once it was unloaded, or replaced by a reload.
    private bool IsCachedVariant(TextureAsset asset)
    {
        lock (_sync)
            return ReferenceEquals(
                FindVariant(asset.RelativePath, asset.Filter, asset.Wrap, asset.ColorSpace), asset);
    }

    // 8x8 magenta/black checker: unmistakable on screen, and nearest-filtered
    // so it stays a hard checker instead of blurring into flat pink.
    private static Texture CreatePlaceholder(Renderer renderer)
    {
        const int size = 8;
        var pixels = new byte[size * size * 3];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool magenta = ((x / 2) + (y / 2)) % 2 == 0;
                int i = (y * size + x) * 3;
                pixels[i + 0] = magenta ? (byte)255 : (byte)0;
                pixels[i + 1] = 0;
                pixels[i + 2] = magenta ? (byte)255 : (byte)0;
            }
        }

        // sRGB: it stands in for colour textures, and #FF00FF has to be the same
        // magenta on screen as the same value typed into a material would be.
        return renderer.CreateTexture(
            pixels, size, size, TextureFormat.Rgb8, TextureColorSpace.Srgb,
            TextureFilter.Nearest, TextureWrap.Repeat);
    }

    // The one place an unhonourable sRGB request is reported, because it is the
    // only layer that knows which file it was. The backends silently resolve to
    // linear (TextureFormatInfo.Resolve) so that all three behave alike; without
    // this line the downgrade would be invisible.
    private void WarnIfSrgbUnavailable(string key, TextureFormat format, TextureColorSpace requested)
    {
        if (requested != TextureColorSpace.Srgb || TextureFormatInfo.SupportsSrgb(format))
            return;

        _logger.LogWarning(
            "Texture {Path} decoded as {Format}, which has no sRGB form; loading it as data. " +
            "Mark the slot 'data' in the material to silence this.",
            key, format);
    }

    // Struct, so an empty drain does not allocate.
    private readonly record struct UploadRequest(
        TextureAsset Asset, long Sequence, ImageSource Image, string? Error);

    /// <summary>
    /// One image read, in whichever of the two forms the content actually holds
    /// it, plus the pack reference that keeps a cooked one alive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The blob is not a convenience.</b> A cooked payload is a span straight
    /// into a mounted pack's memory-mapped view, and this value crosses from a
    /// thread-pool decode to the render thread through a queue, so the thing
    /// keeping those bytes valid has to travel with it rather than with the call
    /// that opened them. Unmapping under a live span is an access violation with
    /// no managed stack, not an exception anybody can catch.
    /// </para>
    /// <para>
    /// A struct, so an empty drain allocates nothing, and
    /// <see cref="Dispose"/> is idempotent because <see cref="ContentBlob"/>'s
    /// is.
    /// </para>
    /// </remarks>
    private readonly record struct ImageSource(DecodedImage? Decoded, ContentBlob? Blob, SimageInfo? Cooked)
    {
        /// <summary>Whether this carries a real image rather than standing for a failure.</summary>
        public bool HasContent => Decoded is not null || Cooked is not null;

        /// <summary>The format the texture will be created in.</summary>
        public TextureFormat Format => Decoded?.Format ?? Cooked!.Format;

        /// <summary>One phrase for a log line, naming which of the two forms this was.</summary>
        public string Describe() => Decoded is { } decoded
            ? $"{decoded.Width}x{decoded.Height}, {decoded.Channels}ch, {decoded.Format}"
            : $"{Cooked!.Width}x{Cooked.Height}, {Cooked.MipCount} mips, {Cooked.Format}, cooked";

        /// <summary>Releases the pack reference a cooked read holds. Safe on a failure value.</summary>
        public void Dispose() => Blob?.Dispose();
    }
}
