using SpectraEngine.Core.Assets.Audio;
using SpectraEngine.Core.Assets.Sources;
using SpectraEngine.Core.Audio;
using System;

namespace SpectraEngine.Core.Assets;

/// <summary>
/// A cooked sound the asset manager has open: the parsed <c>.saudio</c> header
/// and a live view of its PCM.
/// </summary>
/// <remarks>
/// <para><b>It HOLDS the <see cref="ContentBlob"/> for its whole life, and that
/// is the entire reason this type exists rather than a tuple.</b>
/// <see cref="Samples"/> is a span into whatever the content source handed over,
/// which for a mounted pack is a memory-mapped view: unmapping under a live span
/// is an access violation with no managed stack, no catch block and nothing in
/// the log naming the file. The reference travels WITH the span, exactly as it
/// does for a cooked texture crossing the upload queue, so a caller cannot hold
/// the samples and drop the mapping.</para>
/// <para><b>The manager owns it.</b> A caller never disposes one; it calls
/// <c>AssetManager.UnloadAudio</c>, or lets the manager's own teardown release
/// every open sound. That is the same ownership contract textures, materials and
/// models already have, and the reason it is worth restating is that this one
/// holds a MOUNT alive rather than a GPU object: an audio asset leaked out of a
/// closed session defers that pack's unmount for the life of the process.</para>
/// <para><b>No AL buffer, deliberately.</b> Uploading is
/// <c>AudioManager.CreateClip</c>'s, on the render thread, and this class has no
/// device and creates nothing. Reading a cooked sound is pure CPU and may
/// therefore happen anywhere - which is what would let a background load exist
/// later without moving the ownership rule.</para>
/// </remarks>
public sealed class AudioAsset : IDisposable
{
    private ContentBlob? _blob;

    internal AudioAsset(string sourcePath, string resolvedPath, SaudioInfo info, ContentBlob blob)
    {
        SourcePath = sourcePath;
        ResolvedPath = resolvedPath;
        Info = info;
        _blob = blob;
    }

    /// <summary>The content path the caller asked for, e.g. <c>Sounds/door.wav</c>.</summary>
    public string SourcePath { get; }

    /// <summary>The path the bytes actually came from, e.g. <c>Sounds/door.saudio</c>.</summary>
    public string ResolvedPath { get; }

    /// <summary>What the file's header declared.</summary>
    public SaudioInfo Info { get; }

    /// <summary>Rate and channel count.</summary>
    public AudioFormat Format => Info.Format;

    /// <summary>The region the sound repeats, or <see cref="LoopRegion.None"/>.</summary>
    public LoopRegion Loop => Info.Loop;

    /// <summary>Length in sample frames.</summary>
    public long FrameCount => Info.FrameCount;

    /// <summary>True once the manager has released it; the samples are empty afterwards.</summary>
    public bool IsReleased => _blob is null;

    /// <summary>
    /// The interleaved PCM16, straight out of the content source's own bytes.
    /// </summary>
    /// <remarks>
    /// Empty rather than throwing once released, because the one thing a caller
    /// must never get here is a span into a mapping that has been let go.
    /// </remarks>
    public ReadOnlySpan<short> Samples =>
        _blob is { } blob ? Info.Pcm(blob.Span) : default;

    /// <summary>
    /// Releases the content reference. The manager's, not a caller's; idempotent
    /// because <see cref="ContentBlob"/>'s own disposal is.
    /// </summary>
    public void Dispose()
    {
        _blob?.Dispose();
        _blob = null;
    }
}
