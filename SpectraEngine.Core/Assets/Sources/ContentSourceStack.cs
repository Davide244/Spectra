using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;

namespace SpectraEngine.Core.Assets.Sources;

/// <summary>
/// An overlay of <see cref="IContentSource"/>s: priority ordered, first hit
/// wins.
/// </summary>
/// <remarks>
/// <para><b>Flattened at mount, never probed per lookup.</b> The order is
/// computed once when a source is mounted and published as an array a lookup
/// walks; mounting a stack inside a stack splices its sources in rather than
/// nesting, so a lookup is one linear walk however the overlay was assembled.
/// Equal priorities keep mount order, which makes an overlay assembled the same
/// way resolve the same way every run.</para>
/// <para><b>Strictness belongs here and nowhere else.</b> A cook wants a missing
/// asset to stop the build; the engine wants it to become a magenta checker and
/// a warning. Those are the same lookup with different consequences, so the
/// consequence is a property of the stack that was mounted rather than a flag on
/// the asset manager: <see cref="AssetManager"/>'s degradation is a pinned
/// invariant and must not become conditional on anything.</para>
/// <para><b>Strictness applies to <see cref="TryOpen"/> only.</b>
/// <see cref="Exists"/> is a question whose false is a legitimate answer rather
/// than a miss — it is what a caller uses to choose a documented fallback
/// <i>before</i> asking for bytes — and a probe that threw would convert exactly
/// the degradation this engine relies on into a crash.</para>
/// <para><b>Thread-safe.</b> Mounting publishes a fresh array, so a lookup on
/// another thread either sees the source or does not, never a half-built list.
/// Mounting is a start-up operation; lookups are not.</para>
/// </remarks>
public sealed class ContentSourceStack : IContentSource
{
    // Published whole on every mount and never mutated in place, so a reader
    // needs no lock. Highest priority first.
    private volatile IContentSource[] _sources = [];

    /// <summary>
    /// Creates an empty stack. <paramref name="strict"/> makes a total
    /// <see cref="TryOpen"/> miss throw instead of returning false;
    /// <paramref name="priority"/> only matters when this stack is itself
    /// mounted into another one.
    /// </summary>
    public ContentSourceStack(bool strict = false, int priority = 0)
    {
        Strict = strict;
        Priority = priority;
    }

    /// <summary>
    /// Whether a total miss is an error. False for the engine (content problems
    /// degrade), true for tools that would rather stop than ship a hole.
    /// </summary>
    public bool Strict { get; }

    /// <inheritdoc/>
    public int Priority { get; }

    /// <summary>Number of mounted sources, after flattening.</summary>
    public int Count => _sources.Length;

    /// <summary>The mounted sources, highest priority first.</summary>
    public IReadOnlyList<IContentSource> Sources => _sources;

    /// <summary>
    /// Mounts <paramref name="source"/> into the overlay. Another stack is
    /// flattened into this one rather than nested.
    /// </summary>
    public void Mount(IContentSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(source, this))
            throw new ArgumentException("A content source stack cannot be mounted into itself.", nameof(source));

        if (source is ContentSourceStack nested)
        {
            // Flatten: a nested stack would be walked as one entry at its own
            // priority, so its members could not interleave with this stack's,
            // and its strictness would silently override the mounting stack's.
            IContentSource[] members = nested._sources;
            for (int i = 0; i < members.Length; i++)
                Mount(members[i]);
            return;
        }

        IContentSource[] current = _sources;
        var next = new IContentSource[current.Length + 1];

        // Stable insertion: the new source goes after every source of equal or
        // higher priority, so mount order breaks ties.
        int at = current.Length;
        for (int i = 0; i < current.Length; i++)
        {
            if (current[i].Priority < source.Priority)
            {
                at = i;
                break;
            }
        }

        Array.Copy(current, next, at);
        next[at] = source;
        Array.Copy(current, at, next, at + 1, current.Length - at);
        _sources = next;
    }

    /// <inheritdoc/>
    /// <exception cref="FileNotFoundException">
    /// No source has the content and this stack is <see cref="Strict"/>.
    /// </exception>
    public bool TryOpen(string path, [NotNullWhen(true)] out ContentBlob? blob)
    {
        IContentSource[] sources = _sources;
        for (int i = 0; i < sources.Length; i++)
        {
            if (sources[i].TryOpen(path, out blob))
                return true;
        }

        if (Strict)
        {
            throw new FileNotFoundException(
                $"Content '{path}' was not found in any of the {sources.Length} mounted source(s).", path);
        }

        blob = null;
        return false;
    }

    /// <inheritdoc/>
    public bool Exists(string path)
    {
        IContentSource[] sources = _sources;
        for (int i = 0; i < sources.Length; i++)
        {
            if (sources[i].Exists(path)) return true;
        }
        return false;
    }

    /// <inheritdoc/>
    public bool TryGetWatchPath(string path, [NotNullWhen(true)] out string? fullPath)
    {
        IContentSource[] sources = _sources;
        for (int i = 0; i < sources.Length; i++)
        {
            if (sources[i].TryGetWatchPath(path, out fullPath)) return true;
        }

        fullPath = null;
        return false;
    }

    /// <inheritdoc/>
    public void TryEnumerate(string prefix, string extension, List<string> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        IContentSource[] sources = _sources;
        if (sources.Length == 1)
        {
            sources[0].TryEnumerate(prefix, extension, results);
            return;
        }

        // First hit wins here too: a path a higher-priority source serves must
        // appear once, not once per source that happens to hold a copy of it.
        // Only this call's own additions are deduplicated — what the caller had
        // in the list already is the caller's business.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < sources.Length; i++)
        {
            int before = results.Count;
            sources[i].TryEnumerate(prefix, extension, results);
            for (int j = before; j < results.Count; j++)
            {
                if (seen.Add(results[j])) continue;

                results.RemoveAt(j);
                j--;
            }
        }
    }

    /// <summary>
    /// One line naming every mounted source in resolution order — what the
    /// engine logs at start-up, because the first question when content resolves
    /// wrongly is always which source answered.
    /// </summary>
    public string Describe()
    {
        IContentSource[] sources = _sources;
        if (sources.Length == 0) return "(no content sources mounted)";

        var builder = new StringBuilder();
        for (int i = 0; i < sources.Length; i++)
        {
            if (i > 0) builder.Append(" -> ");
            builder.Append(sources[i].ToString()).Append(" [priority ").Append(sources[i].Priority).Append(']');
        }

        if (Strict) builder.Append(" (strict)");
        return builder.ToString();
    }

    /// <inheritdoc/>
    public override string ToString() => Describe();
}
