using System.Runtime.InteropServices;

namespace SpectraEngine.Core.Maps.Compiled;

/// <summary>
/// One 32-byte section-table record: where a section's bytes are and what they
/// claim to be.
/// </summary>
/// <remarks>
/// <para><b>An unknown <see cref="Kind"/> is SKIPPED, not refused.</b> That is
/// the most important structural decision in the format: it is what lets a
/// lightmap, a navmesh or an audio-occlusion section be written by a later cooker
/// with no version bump, and it is the same stance <c>.smodel</c> takes. Bounds
/// and alignment are still checked for every record, known or not, because a
/// section a reader steps over is still a claim about where the file's bytes are
/// and letting an unknown one describe an impossible region would turn the
/// forward-compatibility mechanism into a way to smuggle a malformed file past the
/// gate.</para>
/// <para><b><see cref="UncompressedSize"/> equals <see cref="Size"/> whenever the
/// section is stored as written</b>, rather than being zero for that case. Zero
/// would make "not compressed" and "empty" the same bytes, and an empty section is
/// legal here: the reserved codes are emitted at length zero precisely so they are
/// claimed.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct ScmapSection
{
    /// <summary>The four-character code naming what this section holds.</summary>
    public readonly uint Kind;

    /// <summary>
    /// This section's own version, independent of the file's.
    /// </summary>
    /// <remarks>
    /// Per section rather than per file so a change to one section's record shape
    /// does not invalidate every compiled map in a project. It is currently
    /// unused by any reader and written as 1 for every section: a reader that
    /// gated on it before any section had two versions would be enforcing a rule
    /// with no content.
    /// </remarks>
    public readonly ushort Version;

    /// <summary>Per-section properties. See <see cref="ScmapSectionFlags"/>.</summary>
    public readonly ushort Flags;

    /// <summary>Absolute offset of the section's first byte. 16-byte aligned.</summary>
    public readonly ulong Offset;

    /// <summary>Bytes stored in the file.</summary>
    public readonly ulong Size;

    /// <summary>Bytes after decoding; equal to <see cref="Size"/> when stored as written.</summary>
    public readonly ulong UncompressedSize;

    /// <summary>Builds one section-table record. Every field is assigned.</summary>
    public ScmapSection(uint kind, ulong offset, ulong size, ushort version = 1, ScmapSectionFlags flags = ScmapSectionFlags.None)
    {
        Kind = kind;
        Version = version;
        Flags = (ushort)flags;
        Offset = offset;
        Size = size;
        UncompressedSize = size;
    }

    /// <summary>The section properties, as the enum rather than as the raw word.</summary>
    public ScmapSectionFlags SectionFlags => (ScmapSectionFlags)Flags;
}
