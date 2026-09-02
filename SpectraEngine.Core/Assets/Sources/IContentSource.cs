using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace SpectraEngine.Core.Assets.Sources;

/// <summary>
/// One place content bytes can come from: the loose <c>Assets</c> folder today,
/// a packed archive later, a live editor overlay after that.
/// </summary>
/// <remarks>
/// <para><b>Read-only, thread-safe, and ignorant of the GPU.</b> Those three are
/// the whole contract. A source is asked for bytes from the render thread, from
/// a background decode and from a tool thread at the same time, so it may hold
/// no per-call state; and it must never create, touch or even name a GPU
/// resource, because the layer above it (<see cref="AssetManager"/>) is the only
/// thing allowed to decide when an upload happens and on which thread.</para>
/// <para><b>Nothing here throws for a miss.</b> <see cref="TryOpen"/> answers
/// false for content it does not have and false for content it has but cannot
/// read, logging the second case; an unreadable archive entry must degrade
/// exactly like a missing file, or the engine's degrade-don't-crash policy would
/// depend on which source answered. The one deliberate exception is
/// <see cref="ContentSourceStack"/> in strict mode, which is a property of that
/// stack rather than of any source in it.</para>
/// <para><b>Paths are content-relative and normalised by the caller</b>
/// (<see cref="ContentRoot.NormalizeRelativePath"/>): forward slashes, no
/// leading separator, no <c>..</c>. A source that is handed something else must
/// answer false rather than reaching outside itself.</para>
/// </remarks>
public interface IContentSource
{
    /// <summary>
    /// Where this source sits in an overlay: higher wins. Read once when the
    /// source is mounted, so it must not change afterwards.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Opens <paramref name="path"/> and hands the caller its bytes, which the
    /// caller disposes. False when this source has no such content, or has it
    /// and could not read it.
    /// </summary>
    bool TryOpen(string path, [NotNullWhen(true)] out ContentBlob? blob);

    /// <summary>
    /// Whether this source can serve <paramref name="path"/>. Never throws:
    /// callers use it to decide between real content and a documented fallback,
    /// and a probe that could throw would turn that fallback into a crash.
    /// </summary>
    bool Exists(string path);

    /// <summary>
    /// The absolute filesystem path a hot-reload watcher should watch for
    /// <paramref name="path"/>, when this source is backed by real files. False
    /// for anything that is not: a source with no watch path is simply not
    /// watched, which is the correct behaviour for a packed archive.
    /// </summary>
    bool TryGetWatchPath(string path, [NotNullWhen(true)] out string? fullPath);

    /// <summary>
    /// Appends every content-relative path this source serves under
    /// <paramref name="prefix"/> with extension <paramref name="extension"/>
    /// (including the dot; empty matches any) to <paramref name="results"/>.
    /// </summary>
    /// <remarks>
    /// Appends rather than replaces, so an overlay can collect from several
    /// sources into one list. This is a tooling operation (a content browser, a
    /// cook's dependency walk), never a per-frame one, so it may allocate.
    /// </remarks>
    void TryEnumerate(string prefix, string extension, List<string> results);
}
