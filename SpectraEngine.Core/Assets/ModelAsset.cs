using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using System;
using System.Collections.Generic;
using System.Threading;

namespace SpectraEngine.Core.Assets;

/// <summary>
/// A stable handle to a model asset: the imported CPU data plus the GPU meshes
/// and materials the render thread built from it. One instance per content path,
/// shared by every instantiation of that model — the meshes and materials are
/// the shared originals, and a scene subtree only ever references them.
/// </summary>
/// <remarks>
/// <para><b>Lifetime.</b> Owned by <see cref="AssetManager"/>, like every other
/// asset it hands out. Callers must never dispose the meshes; they call
/// <see cref="AssetManager.UnloadModel"/> or let
/// <see cref="AssetManager.ReleaseGraphicsResources"/> clean up at shutdown.</para>
/// <para><b>Readiness.</b> <see cref="AssetManager.LoadModel"/> returns a handle
/// that is already ready. <see cref="AssetManager.RequestModel"/> returns one
/// immediately with <see cref="IsReady"/> false, and the import lands on a later
/// <see cref="AssetManager.PumpPendingUploads"/>. Instantiating a handle that is
/// not ready throws rather than silently producing an empty subtree.</para>
/// <para><b>Threading.</b> <see cref="RelativePath"/>, <see cref="SourcePath"/>
/// and <see cref="Options"/> are immutable and readable anywhere. Everything
/// else is written by the asset manager on the render thread and should only be
/// read there.</para>
/// </remarks>
public sealed class ModelAsset
{
    // Same ticket scheme as TextureAsset: a synchronous load issued while a
    // background import for the same path is still running must win regardless
    // of which finishes first.
    private long _requestSequence;

    internal ModelAsset(string relativePath, string sourcePath, ModelImportOptions options)
    {
        RelativePath = relativePath;
        SourcePath = sourcePath;
        Options = options;
    }

    /// <summary>Normalised content-root-relative path this model was loaded from. Any thread.</summary>
    public string RelativePath { get; }

    /// <summary>Absolute path of the backing file on disk. Any thread.</summary>
    public string SourcePath { get; }

    /// <summary>
    /// Import settings this handle was created with. Fixed for the handle's
    /// life: a second request for the same path reuses the cached handle and
    /// therefore the original options.
    /// </summary>
    public ModelImportOptions Options { get; }

    /// <summary>
    /// The imported geometry and hierarchy, or null while an async import is
    /// still in flight (or after one failed). Render thread.
    /// </summary>
    public ModelData? Data { get; internal set; }

    /// <summary>
    /// GPU meshes, index-aligned with <see cref="ModelData.Meshes"/>. Empty
    /// until the import lands. Render thread.
    /// </summary>
    public IReadOnlyList<Mesh> Meshes { get; internal set; } = [];

    /// <summary>
    /// Resolved materials, index-aligned with <see cref="ModelData.Materials"/>.
    /// Empty until the import lands. Render thread.
    /// </summary>
    public IReadOnlyList<Material> Materials { get; internal set; } = [];

    /// <summary>
    /// Why the last import attempt failed, or null if it did not. A failed
    /// handle stays cached with the reason recorded rather than disappearing;
    /// asking for the same path again retries, which is what makes fixing the
    /// file on disk and reloading work. Render thread.
    /// </summary>
    public string? Error { get; internal set; }

    // True between queueing a background import and its result being drained.
    // Guarded by the asset manager's model lock, because it is what stops a
    // caller that polls RequestModel every frame from spawning a task per frame.
    internal bool ImportPending { get; set; }

    /// <summary>True once the import landed and the GPU resources exist. Render thread.</summary>
    public bool IsReady => Data is not null;

    /// <summary>
    /// The model's bounds in its own space, or an empty box before it is ready.
    /// Render thread.
    /// </summary>
    public Aabb LocalBounds => Data?.LocalBounds ?? default;

    /// <summary>
    /// The material a submesh draws with. Falls back to the last material rather
    /// than throwing on an out-of-range index, because a material index is
    /// content and content must not be able to crash a draw. Render thread.
    /// </summary>
    public Material MaterialFor(in ModelMesh mesh)
    {
        IReadOnlyList<Material> materials = Materials;
        if (materials.Count == 0)
        {
            throw new InvalidOperationException(
                $"Model '{RelativePath}' has no materials; it is not loaded yet.");
        }

        int index = mesh.MaterialIndex;
        if ((uint)index >= (uint)materials.Count)
            index = materials.Count - 1;
        return materials[index];
    }

    /// <summary>Reserves the next import ticket for this asset. Any thread.</summary>
    internal long NextRequestSequence() => Interlocked.Increment(ref _requestSequence);

    // Ticket of the newest import actually applied; see _requestSequence.
    internal long AppliedSequence { get; set; }
}
