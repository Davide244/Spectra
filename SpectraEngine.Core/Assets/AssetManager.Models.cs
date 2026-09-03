using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Assets.Models;
using SpectraEngine.Core.Assets.Sources;
using SpectraEngine.Core.Graphics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading.Tasks;

namespace SpectraEngine.Core.Assets;

/// <summary>
/// The model half of the asset manager: import, GPU upload, material
/// resolution, and cache lifetime for <see cref="ModelAsset"/>.
/// </summary>
/// <remarks>
/// Split into its own file rather than its own class because it shares the
/// texture half's renderer, its upload pump, its content root and its degrade-
/// don't-throw policy — a second type would have had to duplicate all four, and
/// a second pump would have meant a second per-frame call site in the engine.
/// </remarks>
public sealed partial class AssetManager
{
    // Guards _models only. A third lock rather than reusing _materialSync:
    // building a model's materials calls LoadMaterial, which takes that one.
    private readonly object _modelSync = new();
    private readonly Dictionary<string, ModelAsset> _models = new(StringComparer.OrdinalIgnoreCase);

    // Background import -> render thread. Same shape as the texture queue.
    private readonly ConcurrentQueue<ModelImportResult> _modelImports = new();

    /// <summary>Number of model assets currently cached. Any thread.</summary>
    public int ModelCount
    {
        get { lock (_modelSync) return _models.Count; }
    }

    /// <summary>
    /// Loads a model synchronously: import and GPU upload both happen on the
    /// calling thread, which must be the render thread. This is the load-time
    /// path — use <see cref="RequestModel"/> for anything loaded while frames
    /// are running. Returns the cached handle if the path is already loaded.
    /// </summary>
    /// <remarks>
    /// If an async request for the same path is still in flight, this imports
    /// anyway and wins: it takes a newer ticket, so the background result is
    /// dropped as stale when it lands. The caller asked for a model it can use
    /// on the next line, and that is what it gets.
    /// </remarks>
    /// <param name="relativePath">Path under the content root, e.g. <c>Models/crate.obj</c>.</param>
    /// <param name="options">Import tuning; ignored if the path is already cached.</param>
    /// <exception cref="InvalidOperationException">No renderer is attached.</exception>
    /// <exception cref="FileNotFoundException">The model file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is not a readable model.</exception>
    public ModelAsset LoadModel(string relativePath, ModelImportOptions? options = null)
    {
        RequireRenderer();

        ModelAsset asset = GetOrCreateModel(relativePath, options);
        if (asset.IsReady) return asset;

        // Unlike the async path, a synchronous load reports its failure to the
        // caller: it is load-time code that can still decide what to do about a
        // missing prop, and swallowing it would leave an empty handle with no
        // stack to explain it.
        long sequence = asset.NextRequestSequence();
        ModelData data = ReadModelThroughContent(asset);
        ApplyImport(asset, sequence, data, error: null);
        return asset;
    }

    /// <summary>
    /// Requests a model asynchronously. Returns immediately with a handle that
    /// is not yet ready; the file is imported on the thread pool and the GPU
    /// meshes are created by the next <see cref="PumpPendingUploads"/>. Callable
    /// from any thread, once <see cref="AttachRenderer"/> has run.
    /// </summary>
    /// <remarks>
    /// <para>A failed import is reported through <see cref="ModelAsset.Error"/>
    /// and a log line, never as an exception: by the time it fails, the caller
    /// that asked is several frames gone. Asking again after a failure retries.</para>
    /// <para>Calling this repeatedly for a model that is still importing is free
    /// — at most one import per handle is ever in flight — so polling it from a
    /// frame loop is a supported way to wait for one.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">No renderer is attached yet.</exception>
    public ModelAsset RequestModel(string relativePath, ModelImportOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Same attachment proxy RequestTexture uses: _placeholder is the one
        // piece of attach state published for cross-thread reads.
        if (_placeholder is null)
        {
            throw new InvalidOperationException(
                "AssetManager.RequestModel needs a renderer; call AttachRenderer on the render thread first.");
        }

        ModelAsset asset = GetOrCreateModel(relativePath, options);
        if (asset.IsReady) return asset;

        if (TryBeginImport(asset))
            QueueImport(asset);
        return asset;
    }

    /// <summary>
    /// Looks up an already-requested model without touching the disk. A hit does
    /// not prove the model is usable — check <see cref="ModelAsset.IsReady"/>.
    /// Any thread.
    /// </summary>
    public bool TryGetModel(string relativePath, [MaybeNullWhen(false)] out ModelAsset asset)
    {
        string key = ContentRoot.NormalizeRelativePath(relativePath);
        lock (_modelSync)
            return _models.TryGetValue(key, out asset);
    }

    /// <summary>
    /// Drops a model from the cache and destroys the GPU meshes it owns through
    /// the creating renderer. Scene nodes still referencing those meshes must be
    /// removed first — this does not hunt them down. Returns false if the path
    /// was not loaded. Render thread only.
    /// </summary>
    public bool UnloadModel(string relativePath)
    {
        string key = ContentRoot.NormalizeRelativePath(relativePath);
        ModelAsset? asset;
        lock (_modelSync)
        {
            if (!_models.Remove(key, out asset))
                return false;

            // The handle is off the cache, so no import can ever be applied to
            // it again (ApplyImport drops results for evicted handles); leaving
            // the flag set would advertise an import that will never land.
            asset.ImportPending = false;
        }

        int destroyed = DestroyModelMeshes(asset);
        _logger.LogInformation("Unloaded model {Path} ({Count} GPU mesh(es))", key, destroyed);
        return true;
    }

    // ---- where a model's bytes come from ---------------------------------

    /// <summary>
    /// Whether the model at <paramref name="relativePath"/> would be served from
    /// a cooked <c>.smodel</c> rather than imported from its authored file.
    /// </summary>
    /// <remarks>
    /// For a host that wants to say which path a load took - a log line, an
    /// editor badge, a test. It asks <see cref="ModelContentPath.Resolve"/>, the
    /// same function the read asks, rather than probing for itself: a second
    /// spelling of the redirection is the failure this whole content layer has
    /// already paid for once.
    /// </remarks>
    public bool IsModelCooked(string relativePath)
    {
        string key = ContentRoot.NormalizeRelativePath(relativePath);
        return ModelContentPath.IsCooked(ModelContentPath.Resolve(Content, key));
    }

    // The model read, and the second of the two that FORK. Any thread: opening a
    // blob, validating a .smodel and copying its arrays are all pure CPU, exactly
    // like the image read beside it, which is what lets the async path run this on
    // the thread pool.
    //
    // The asymmetry between the two arms is real and is documented on
    // ModelContentPath: a cooked model is one self-contained payload and comes
    // through the mounted stack, while an authored one is handed to a native
    // importer that opens the FILE itself and follows the material library beside
    // it, so it can only come from a folder.
    private ModelData ReadModelThroughContent(ModelAsset asset)
    {
        string resolved = ModelContentPath.Resolve(Content, asset.RelativePath);
        if (!ModelContentPath.IsCooked(resolved))
            return ModelImporter.Import(asset.SourcePath, ContentRootPath, asset.Options);

        using ContentBlob blob = OpenOrThrow(resolved);

        // The span dies with the blob at the end of this statement, which is safe
        // for exactly one reason: CookedModelData copies. A builder that handed a
        // span onward would have made this blob's lifetime the model's, and
        // unmapping a pack view under a live span is an access violation with no
        // managed stack.
        return CookedModelData.Build(SmodelReader.Read(blob.Span, resolved), asset.RelativePath);
    }

    // ---- pump ------------------------------------------------------------

    // Drains finished background imports. Called by PumpPendingUploads, so
    // models ride the same once-per-frame render-thread call as textures.
    private int PumpPendingModelImports()
    {
        int applied = 0;
        while (_modelImports.TryDequeue(out ModelImportResult result))
        {
            // The queued import is over the moment its result is in hand,
            // whether or not applying it succeeds — clear the in-flight flag
            // first so a retry is possible even if the apply below fails.
            EndImport(result.Asset);
            if (ApplyImport(result.Asset, result.Sequence, result.Data, result.Error))
                applied++;
        }
        return applied;
    }

    // Claims the single in-flight import slot for this handle. False when one is
    // already running, which is what makes RequestModel idempotent per frame.
    private bool TryBeginImport(ModelAsset asset)
    {
        lock (_modelSync)
        {
            if (asset.ImportPending) return false;
            asset.ImportPending = true;
            return true;
        }
    }

    private void EndImport(ModelAsset asset)
    {
        lock (_modelSync)
            asset.ImportPending = false;
    }

    // Import off the render thread and hand the (pure CPU) result back through
    // the queue. Failures are queued too, so the pump can log them on the render
    // thread instead of losing them in an unobserved task.
    private void QueueImport(ModelAsset asset)
    {
        long sequence = asset.NextRequestSequence();
        _ = Task.Run(() =>
        {
            try
            {
                ModelData data = ReadModelThroughContent(asset);
                _modelImports.Enqueue(new ModelImportResult(asset, sequence, data, null));
            }
            catch (Exception ex)
            {
                _modelImports.Enqueue(new ModelImportResult(asset, sequence, null, ex.Message));
            }
        });
    }

    // Turns an imported model into GPU resources and publishes it on the handle.
    // Render thread only.
    private bool ApplyImport(ModelAsset asset, long sequence, ModelData? data, string? error)
    {
        // The handle may have left the cache while the import ran (UnloadModel,
        // or an unload-then-reload that put a fresh handle under the same key).
        // Creating GPU meshes for it now would publish meshes that nothing ever
        // destroys: ReleaseModelResources only walks _models, so an editor
        // load/unload loop would leak one mesh per submesh per cycle.
        if (!IsCachedModel(asset))
        {
            _logger.LogDebug(
                "Dropping the import of {Path}: its handle was unloaded before the meshes were created",
                asset.RelativePath);
            return false;
        }

        // A newer import already landed (a sync load that raced this one) — this
        // result is stale and must not overwrite it.
        if (sequence <= asset.AppliedSequence)
            return false;
        asset.AppliedSequence = sequence;

        if (data is null)
        {
            _logger.LogError("Model import failed ({Path}): {Error}", asset.RelativePath, error);
            asset.Error = error;
            return false;
        }

        IReadOnlyList<string> warnings = data.Warnings;
        for (int i = 0; i < warnings.Count; i++)
            _logger.LogWarning("Model {Path}: {Warning}", asset.RelativePath, warnings[i]);

        IReadOnlyList<ModelMesh> sources = data.Meshes;
        var meshes = new Mesh[sources.Count];
        int created = 0;
        try
        {
            for (; created < sources.Count; created++)
            {
                ModelMesh source = sources[created];
                meshes[created] = _renderer!.CreateMesh(
                    source.Vertices, source.Indices, VertexAttribute.StandardLayout);
            }
        }
        catch (Exception ex)
        {
            // Create-before-publish, like the static-world chunk swap: a GPU
            // failure halfway through must leave no orphaned meshes and no
            // half-built model on the handle.
            for (int i = 0; i < created; i++)
                _renderer!.DestroyMesh(meshes[i]);

            _logger.LogError(ex, "Creating GPU meshes for model {Path} failed", asset.RelativePath);
            asset.Error = ex.Message;
            return false;
        }

        Material[] materials = ResolveModelMaterials(asset, data);

        Mesh[] previous = [.. asset.Meshes];
        asset.Data = data;
        asset.Meshes = meshes;
        asset.Materials = materials;
        asset.Error = null;

        // Only reachable when a sync load overtook an in-flight async one for a
        // handle that had already been populated; destroy after publishing so
        // nothing ever observes the handle pointing at a disposed mesh.
        for (int i = 0; i < previous.Length; i++)
            _renderer!.DestroyMesh(previous[i]);

        _logger.LogInformation(
            "Loaded model {Path} ({Submeshes} submesh(es), {Vertices} vertices, {Triangles} triangles, {Materials} material(s))",
            asset.RelativePath, sources.Count, data.VertexCount, data.IndexCount / 3, materials.Length);
        return true;
    }

    // ---- materials -------------------------------------------------------

    private Material[] ResolveModelMaterials(ModelAsset asset, ModelData data)
    {
        IReadOnlyList<ModelMaterial> descriptions = data.Materials;
        var materials = new Material[descriptions.Count];

        // Which slots any submesh actually draws with. Importers habitually emit
        // a spare default slot (and sometimes a whole unused library), and
        // resolving those would load textures nothing ever samples.
        Span<bool> referenced = descriptions.Count <= 64
            ? stackalloc bool[descriptions.Count]
            : new bool[descriptions.Count];
        referenced.Clear();

        IReadOnlyList<ModelMesh> meshes = data.Meshes;
        for (int i = 0; i < meshes.Count; i++)
        {
            int index = meshes[i].MaterialIndex;
            if ((uint)index < (uint)referenced.Length)
                referenced[index] = true;
        }

        for (int i = 0; i < materials.Length; i++)
        {
            materials[i] = referenced[i]
                ? ResolveModelMaterial(asset.RelativePath, descriptions[i])
                : _defaultMaterial;
        }
        return materials;
    }

    /// <summary>
    /// Resolves one imported material description to a real material, in
    /// descending order of authority: an engine-authored
    /// <c>Materials/&lt;name&gt;.spectramat</c>, then the diffuse texture the
    /// model file itself named, then <see cref="DefaultMaterial"/>.
    /// </summary>
    /// <remarks>
    /// The override step is the point of the whole chain. An imported material
    /// is whatever some other tool's exporter thought — the engine's own
    /// material files know about its shaders, its sampler states and its
    /// parameters. Matching on name lets a designer re-shade imported content
    /// without editing the model, and costs one existence probe per material
    /// per load.
    /// </remarks>
    private Material ResolveModelMaterial(string modelPath, in ModelMaterial description)
    {
        // A COOKED model already answered this question, once, at cook time, and
        // recorded the answer as a path. Re-deriving it from the name here would
        // be the second spelling of a rule the cooker applied - identical today
        // and silently wrong the day a material lives outside Materials/ - so the
        // recorded path is asked for directly and nothing below it runs.
        if (description.AssetPath is { Length: > 0 } assetPath)
        {
            _logger.LogDebug(
                "Model {Path}: material '{Material}' is the cooked reference {Asset}",
                modelPath, description.Name, assetPath);
            return LoadMaterial(assetPath);
        }

        if (TryFindMaterialOverride(description.Name, out string? overridePath))
        {
            _logger.LogDebug(
                "Model {Path}: material '{Material}' resolved to authored {Override}",
                modelPath, description.Name, overridePath);
            return LoadMaterial(overridePath);
        }

        if (description.DiffuseTexturePath is not { } texturePath)
        {
            // No texture and no authored override: the magenta checker is the
            // engine's standing signal that this surface needs content work.
            _logger.LogDebug(
                "Model {Path}: material '{Material}' has no diffuse texture; using the default material",
                modelPath, description.Name);
            return _defaultMaterial;
        }

        try
        {
            TextureAsset texture = LoadTexture(texturePath);
            var material = new Material(_renderer?.DefaultShader)
            {
                Name = description.Name,
                // No SourcePath: this material came out of a model file, not a
                // .spectramat, so there is nothing on disk to hot-reload it from.
            };
            material.SetVector3(BaseColorParameter, description.BaseColor);
            // The handle, not the texture, so the material follows the asset
            // through hot-reloads exactly like a .spectramat-authored one.
            material.SetTexture(DiffuseSlotName, 0, texture);
            return material;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Model {Path}: texture {Texture} for material '{Material}' failed to load ({Message}); " +
                "using the default material",
                modelPath, texturePath, description.Name, ex.Message);
            return _defaultMaterial;
        }
    }

    // The name-to-path half is ModelMaterialOverride's, shared with the cook so
    // the two cannot disagree about which file an imported material's name means;
    // the existence half is this manager's, asked of the mounted stack, because an
    // override that ships inside a pack has to be found there and Exists never
    // throws on a name the filesystem would refuse.
    private bool TryFindMaterialOverride(string materialName, [NotNullWhen(true)] out string? path)
    {
        path = ModelMaterialOverride.PathFor(materialName);
        if (path is not null && Content.Exists(path)) return true;

        path = null;
        return false;
    }

    // ---- lifetime --------------------------------------------------------

    // Whether this exact handle is still the cache's entry for its path — false
    // once it was unloaded, or replaced by a later request for the same path.
    private bool IsCachedModel(ModelAsset asset)
    {
        lock (_modelSync)
            return _models.TryGetValue(asset.RelativePath, out ModelAsset? current)
                && ReferenceEquals(current, asset);
    }

    private ModelAsset GetOrCreateModel(string relativePath, ModelImportOptions? options)
    {
        string key = ContentRoot.NormalizeRelativePath(relativePath);
        lock (_modelSync)
        {
            if (_models.TryGetValue(key, out ModelAsset? cached))
                return cached;

            var created = new ModelAsset(
                key,
                ContentRoot.ResolveAbsolute(ContentRootPath, key),
                options ?? ModelImportOptions.Default);
            _models[key] = created;
            return created;
        }
    }

    // Destroys every GPU mesh a model owns and blanks the handle, so anything
    // still holding it sees an unloaded model rather than disposed meshes.
    private int DestroyModelMeshes(ModelAsset asset)
    {
        IReadOnlyList<Mesh> meshes = asset.Meshes;
        for (int i = 0; i < meshes.Count; i++)
            _renderer?.DestroyMesh(meshes[i]);

        int count = meshes.Count;
        asset.Meshes = [];
        asset.Materials = [];
        asset.Data = null;
        return count;
    }

    // Called from ReleaseGraphicsResources, on the render thread, before the
    // renderer shuts down.
    private void ReleaseModelResources()
    {
        while (_modelImports.TryDequeue(out _)) { }

        ModelAsset[] assets;
        lock (_modelSync)
        {
            assets = new ModelAsset[_models.Count];
            _models.Values.CopyTo(assets, 0);
            _models.Clear();
        }

        int destroyed = 0;
        foreach (ModelAsset asset in assets)
            destroyed += DestroyModelMeshes(asset);

        if (destroyed > 0)
            _logger.LogInformation("Asset manager released {Count} GPU model meshes", destroyed);
    }

    // Struct, so draining an empty queue allocates nothing.
    private readonly record struct ModelImportResult(
        ModelAsset Asset, long Sequence, ModelData? Data, string? Error);
}
