using SpectraEngine.Core.Graphics;
using System.Threading;

namespace SpectraEngine.Core.Assets;

/// <summary>
/// A stable handle to a texture asset. The handle's identity never changes for
/// a given content path, but the <see cref="Texture"/> behind it does: an async
/// load starts on the placeholder and swaps to the real texture when the upload
/// lands, and a hot-reload swaps it again.
/// </summary>
/// <remarks>
/// <para>
/// Bind the handle (not the <see cref="Texture"/>) into a
/// <see cref="Material"/> — see <see cref="Material.SetTexture(string, int, TextureAsset)"/> —
/// so the material follows those swaps instead of pinning whatever texture
/// happened to be current when it was authored.
/// </para>
/// <para>
/// Threading: <see cref="RelativePath"/>, <see cref="SourcePath"/>,
/// <see cref="Filter"/>, <see cref="Wrap"/> and <see cref="ColorSpace"/> are
/// immutable and readable from any thread. <see cref="Texture"/>, <see cref="IsPlaceholder"/> and
/// <see cref="Version"/> are written only by <see cref="AssetManager"/> on the
/// render thread and should only be read there.
/// </para>
/// </remarks>
public sealed class TextureAsset
{
    // Monotonic ticket handed to each decode request for this asset. A reload
    // issued while an earlier decode is still running must win no matter which
    // finishes first, so the pump drops any upload whose ticket is older than
    // the one already applied.
    private long _requestSequence;

    internal TextureAsset(
        string relativePath,
        string sourcePath,
        TextureFilter filter,
        TextureWrap wrap,
        TextureColorSpace colorSpace,
        Texture initialTexture,
        bool isPlaceholder)
    {
        RelativePath = relativePath;
        SourcePath = sourcePath;
        Filter = filter;
        Wrap = wrap;
        ColorSpace = colorSpace;
        Texture = initialTexture;
        IsPlaceholder = isPlaceholder;
    }

    /// <summary>Normalised content-root-relative path this asset was loaded from. Any thread.</summary>
    public string RelativePath { get; }

    /// <summary>Absolute path of the backing file on disk. Any thread.</summary>
    public string SourcePath { get; }

    /// <summary>Sampling filter requested at load time. Any thread.</summary>
    public TextureFilter Filter { get; }

    /// <summary>Wrap mode requested at load time. Any thread.</summary>
    public TextureWrap Wrap { get; }

    /// <summary>
    /// Colour space requested at load time, and part of this handle's cache
    /// identity. Any thread.
    /// </summary>
    /// <remarks>
    /// The <i>requested</i> space, not the resolved one: what identifies the
    /// variant is what the caller asked for, and
    /// <see cref="Texture.ColorSpace"/> on the bound texture is what it got.
    /// </remarks>
    public TextureColorSpace ColorSpace { get; }

    /// <summary>
    /// The texture to bind right now — the shared placeholder while an async
    /// load is in flight, the real texture afterwards. Render thread only.
    /// </summary>
    public Texture Texture { get; internal set; }

    /// <summary>
    /// True while <see cref="Texture"/> is still the placeholder (load pending,
    /// or failed). Render thread only.
    /// </summary>
    public bool IsPlaceholder { get; internal set; }

    /// <summary>
    /// Increments on every successful swap, so callers that cache derived state
    /// per texture can notice a hot-reload. Render thread only.
    /// </summary>
    public int Version { get; internal set; }

    /// <summary>
    /// True when the last decode for this handle failed, i.e. the texture is
    /// sitting on the placeholder because of a content or I/O problem rather
    /// than because a load is still in flight. Render thread only.
    /// </summary>
    /// <remarks>
    /// The flag is what makes a failure retryable: a later
    /// <see cref="AssetManager.RequestTexture"/> re-queues the decode and a
    /// later <see cref="AssetManager.LoadTexture"/> re-reads the file, both into
    /// this same handle — so a texture whose file appeared late (or whose write
    /// lock cleared) reaches every material already bound to it. Without it, the
    /// cache entry inserted before the failed decode would keep every future
    /// request on the placeholder for the process lifetime.
    /// </remarks>
    public bool LoadFailed { get; internal set; }

    // Ticket of the newest upload actually applied; see _requestSequence.
    internal long AppliedSequence { get; set; }

    // Background decodes queued for this handle that have not been drained by
    // the pump yet. Guarded by AssetManager's texture lock, because it is read
    // from RequestTexture (any thread) to decide whether a failed handle needs
    // a fresh decode or already has one coming.
    internal int PendingDecodes { get; set; }

    /// <summary>Reserves the next decode ticket for this asset. Any thread.</summary>
    internal long NextRequestSequence() => Interlocked.Increment(ref _requestSequence);
}
