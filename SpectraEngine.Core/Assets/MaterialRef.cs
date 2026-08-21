using System;
using System.Collections.Generic;

namespace SpectraEngine.Core.Assets;

/// <summary>
/// A lightweight, pure-value reference to a material: an interned id, never the
/// <see cref="Graphics.Material"/> object itself. This is what brush faces and
/// carved polygons carry through the CSG pipeline.
/// </summary>
/// <remarks>
/// <para><b>Why a value and not a material.</b> The static-world compile runs on
/// a background thread and must stay pure CPU — no GPU handles, no asset-manager
/// state, nothing whose lifetime the render thread owns. A 4-byte id satisfies
/// that trivially: it can be copied, compared, hashed, and cached without
/// touching anything the render thread also touches. The id is resolved to a
/// real material exactly once, later, on the render thread at mesh-upload time
/// (<see cref="AssetManager.ResolveMaterial"/>).</para>
/// <para><b>Identity.</b> <c>default(MaterialRef)</c> — id 0 — means "the engine
/// default material", so a face that was never assigned one still resolves to
/// something drawable. Every other id comes from
/// <see cref="MaterialRegistry.Intern"/> and maps one-to-one to a
/// content-relative material path for the life of the process; two refs are
/// equal exactly when they name the same path.</para>
/// </remarks>
public readonly struct MaterialRef : IEquatable<MaterialRef>
{
    /// <summary>The engine default material — what an unassigned face resolves to.</summary>
    public static readonly MaterialRef Default = default;

    internal MaterialRef(int id) => Id = id;

    /// <summary>
    /// The interned id. 0 is the default material; every other value was handed
    /// out by <see cref="MaterialRegistry.Intern"/>. Exposed because callers
    /// (mesh sorting, run merging, debug output) legitimately want the raw key.
    /// </summary>
    public int Id { get; }

    /// <summary>True when this is the default material (id 0).</summary>
    public bool IsDefault => Id == 0;

    /// <inheritdoc/>
    public bool Equals(MaterialRef other) => Id == other.Id;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is MaterialRef other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Id;

    /// <summary>Equality on the interned id.</summary>
    public static bool operator ==(MaterialRef left, MaterialRef right) => left.Id == right.Id;

    /// <summary>Inequality on the interned id.</summary>
    public static bool operator !=(MaterialRef left, MaterialRef right) => left.Id != right.Id;

    /// <inheritdoc/>
    public override string ToString() =>
        MaterialRegistry.TryGetPath(this, out string? path) ? path : $"material#{Id}";
}

/// <summary>
/// The process-wide intern table behind <see cref="MaterialRef"/>: turns a
/// content-relative material path into a stable id and back again.
/// </summary>
/// <remarks>
/// <para><b>Append-only.</b> Ids are handed out in interning order and are never
/// reused or revoked, so an id captured in a compiled world (or in a carve
/// cache) stays meaningful for as long as the process lives. That is what makes
/// the id safe to hold in immutable CSG artifacts.</para>
/// <para><b>Threading.</b> Interning may happen on any thread — an editor
/// assigning a material, a loader reading a map file, a test — so the table is
/// lock-guarded. Lookups are lock-guarded too; they are not on any per-frame or
/// per-polygon path (the compile only ever copies ids around, and upload-time
/// resolution happens once per material per mesh).</para>
/// <para><b>Path form.</b> Paths are keyed case-insensitively with backslashes
/// folded to forward slashes, so the two ways of spelling the same content path
/// intern to one id. Anything further (content-root resolution, extension
/// defaults) is the asset manager's business at resolve time.</para>
/// </remarks>
public static class MaterialRegistry
{
    private static readonly object Sync = new();

    // Id -> path. Index 0 is the default material's reserved slot; its entry is
    // never read (TryGetPath short-circuits) but keeps id and index aligned.
    private static readonly List<string> Paths = [string.Empty];

    private static readonly Dictionary<string, MaterialRef> ByPath =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Number of distinct material paths interned so far (excluding the
    /// default). Diagnostics and tests only.
    /// </summary>
    public static int Count
    {
        get { lock (Sync) return Paths.Count - 1; }
    }

    /// <summary>
    /// Returns the stable id for <paramref name="materialPath"/>, assigning one
    /// on first use. An empty or whitespace path is the default material.
    /// </summary>
    /// <param name="materialPath">
    /// Content-root-relative path of a <c>.spectramat</c> file, e.g.
    /// <c>Materials/wall.spectramat</c>.
    /// </param>
    public static MaterialRef Intern(string materialPath)
    {
        ArgumentNullException.ThrowIfNull(materialPath);
        if (string.IsNullOrWhiteSpace(materialPath))
            return MaterialRef.Default;

        string key = Normalize(materialPath);
        lock (Sync)
        {
            if (ByPath.TryGetValue(key, out MaterialRef existing))
                return existing;

            var created = new MaterialRef(Paths.Count);
            Paths.Add(key);
            ByPath[key] = created;
            return created;
        }
    }

    /// <summary>
    /// Resolves an id back to the path it was interned from. False for the
    /// default material (which has no path) and for an id this process never
    /// handed out.
    /// </summary>
    public static bool TryGetPath(MaterialRef reference, out string path)
    {
        int id = reference.Id;
        if (id > 0)
        {
            lock (Sync)
            {
                if (id < Paths.Count)
                {
                    path = Paths[id];
                    return true;
                }
            }
        }

        path = string.Empty;
        return false;
    }

    private static string Normalize(string materialPath) =>
        materialPath.Trim().Replace('\\', '/');
}
