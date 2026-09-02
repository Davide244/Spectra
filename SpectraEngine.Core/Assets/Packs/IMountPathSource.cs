using System.Collections.Generic;

namespace SpectraEngine.Core.Assets.Packs;

/// <summary>
/// One logical path a source decides, and whether the decision is a deletion.
/// </summary>
public readonly record struct MountPath(string Path, bool IsTombstone);

/// <summary>
/// A content source that can list every logical path it DECIDES, deletions
/// included.
/// </summary>
/// <remarks>
/// <para><b>Why this is not just <c>TryEnumerate</c>.</b> Enumeration answers
/// "what can be served", which is the right answer for a content browser and the
/// wrong one for a mount stack: a tombstone serves nothing and is the entire
/// mechanism by which a higher band removes content a lower one shipped, so a
/// flatten built from enumeration alone would never see a deletion and the
/// tombstone would silently do nothing.</para>
/// <para>It is optional. A source that does not implement it is flattened from
/// its enumeration, which is correct for anything that cannot express a deletion
/// in the first place — the loose file tree, for one.</para>
/// </remarks>
public interface IMountPathSource
{
    /// <summary>Appends every path this source decides to <paramref name="results"/>.</summary>
    void EnumerateMountPaths(List<MountPath> results);
}
