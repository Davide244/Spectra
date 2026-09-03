using System;

namespace SpectraEngine.Core.Assets.Models;

/// <summary>
/// The fixed byte geometry of a <c>.smodel</c> file, stated once for the cook
/// rule that writes one and the reader here.
/// </summary>
/// <remarks>
/// <para><b>Two expressions of one layout diverge</b>, which is the lesson
/// <see cref="Packs.PackFormat"/> and <c>ShaderFileLayout</c> both already
/// record: a writer that computes a section start from its own running cursor
/// and a reader that recomputes it from a literal agree exactly until one of
/// them is edited, and then disagree as a read into the middle of somebody
/// else's bytes rather than as an exception. Both sides take their arithmetic
/// from here.</para>
/// <para><b>The section table sits at a fixed offset, unlike <c>.spack</c>'s,
/// and that follows from the versioning doctrine rather than from an
/// oversight.</b> A pack carries <c>EntryTableOffset</c> as a header field so a
/// v2 header can grow without a version bump, because a pack is mounted by
/// readers of many ages. A <c>.smodel</c> is a cooked artifact and versions the
/// strict way: a reader seeing a version it does not implement refuses the file
/// outright and says recook, so a v2 that moved the table would already be
/// unreachable by this code and an explicit offset would buy nothing.</para>
/// </remarks>
public static class SmodelFormat
{
    /// <summary>The cooked extension, dot included.</summary>
    public const string FileExtension = ".smodel";

    /// <summary>
    /// File magic, <c>"SMDL"</c>. Stored as a little-endian <see cref="uint"/>,
    /// so the first four bytes on disk read <c>S M D L</c> in a hex dump.
    /// </summary>
    /// <remarks>
    /// The four-byte abbreviation is <c>SMDL</c>; the extension is always spelled
    /// <c>.smodel</c>. They are deliberately different lengths and neither is a
    /// typo for the other.
    /// </remarks>
    public const uint Magic = 'S' | ('M' << 8) | ('D' << 16) | ((uint)'L' << 24);

    /// <summary>Bytes in the header, which lives at offset 0.</summary>
    /// <remarks>
    /// The declared fields run to 0x2C and the remaining 20 bytes are reserved
    /// and zero-filled by the writer. The reader does not assert that they are
    /// zero: a v2 that spends them raises the format version, which this reader
    /// refuses before it ever looks at them, so the assertion could only ever
    /// fire on a writer bug it has no words to describe.
    /// </remarks>
    public const int HeaderSize = 64;

    /// <summary>Absolute offset of the first section-table record.</summary>
    public const int SectionTableOffset = HeaderSize;

    /// <summary>Bytes in one section-table record, fixed stride.</summary>
    public const int SectionSize = 24;

    /// <summary>
    /// The smallest legal file: a header and nothing else. Such a file is still
    /// refused, but by the required-section check rather than by a length check,
    /// because the two failures want to say different things.
    /// </summary>
    public const int MinimumFileSize = HeaderSize;

    /// <summary>
    /// Alignment every section starts on, asserted at load rather than assumed.
    /// </summary>
    /// <remarks>
    /// The whole point of the format is that <c>VBUF</c>, <c>IBUF</c> and the
    /// collision plane array are reinterpreted in place out of a mapped view as
    /// <c>float</c>, <c>uint</c> and <see cref="System.Numerics.Plane"/>, and a
    /// <c>Plane</c> may not straddle a 16-byte boundary. It is the same number
    /// <see cref="Packs.PackFormat.PayloadAlignment"/> carries, for the same
    /// reason, and the two compose: a pack payload starts 16-byte aligned, so a
    /// section 16-byte aligned within the file is 16-byte aligned in the mapping
    /// as well.
    /// </remarks>
    public const int PayloadAlignment = 16;

    /// <summary>Bytes in one <see cref="SmodelVertexAttribute"/> record.</summary>
    public const int VertexAttributeSize = 8;

    /// <summary>Bytes of fixed preamble in <c>VTXL</c>: attribute count and stride.</summary>
    public const int VertexLayoutPreambleSize = 8;

    /// <summary>Bytes in one <see cref="SmodelSubmesh"/> record.</summary>
    public const int SubmeshSize = 40;

    /// <summary>Bytes in one <see cref="SmodelLod"/> record.</summary>
    public const int LodSize = 12;

    /// <summary>Bytes in one <see cref="SmodelJoint"/> record.</summary>
    public const int JointSize = 56;

    /// <summary>Bytes in one <see cref="SmodelCollisionHull"/> record.</summary>
    public const int CollisionHullSize = 8;

    /// <summary>Bytes of fixed preamble in <c>COLL</c>: the hull count.</summary>
    public const int CollisionPreambleSize = 4;

    /// <summary>Bytes in one collision plane, which is a <c>System.Numerics.Plane</c>.</summary>
    public const int CollisionPlaneSize = 16;

    /// <summary>
    /// The fewest planes a hull may carry, because that is the fewest
    /// <c>Brush</c>'s constructor accepts.
    /// </summary>
    /// <remarks>
    /// Cooked collision exists to become <c>Brush</c> instances and ride the
    /// character mover's plane-set path with no new collision code, so a hull
    /// <c>Brush</c> would throw on is refused here, where the file can be named,
    /// rather than at the call site months later where only a hull index is in
    /// hand.
    /// </remarks>
    public const int MinimumHullPlanes = 4;

    /// <summary>
    /// What a name offset holds when a record has no name. Zero cannot serve,
    /// because zero is the first record's legitimate offset.
    /// </summary>
    /// <remarks>
    /// Deliberately the same sentinel as
    /// <see cref="Packs.PackFormat.NameOffsetAbsent"/>: a name blob is a name
    /// blob, and two spellings of absent is how one of them ends up read as an
    /// offset.
    /// </remarks>
    public const uint NameOffsetAbsent = 0xFFFFFFFFu;

    /// <summary>Section <c>VTXL</c>: the vertex layout the file's vertices are in.</summary>
    public const uint VertexLayoutSection = 'V' | ('T' << 8) | ('X' << 16) | ((uint)'L' << 24);

    /// <summary>Section <c>VBUF</c>: one interleaved vertex buffer for the whole model.</summary>
    public const uint VertexBufferSection = 'V' | ('B' << 8) | ('U' << 16) | ((uint)'F' << 24);

    /// <summary>Section <c>IBUF</c>: one index buffer for the whole model.</summary>
    public const uint IndexBufferSection = 'I' | ('B' << 8) | ('U' << 16) | ((uint)'F' << 24);

    /// <summary>Section <c>SUBM</c>: submeshes, as index ranges into <c>IBUF</c>.</summary>
    public const uint SubmeshSection = 'S' | ('U' << 8) | ('B' << 16) | ((uint)'M' << 24);

    /// <summary>Section <c>LODS</c>: levels of detail, as submesh ranges.</summary>
    public const uint LodSection = 'L' | ('O' << 8) | ('D' << 16) | ((uint)'S' << 24);

    /// <summary>Section <c>SKEL</c>: the joint hierarchy and its inverse bind matrices.</summary>
    public const uint SkeletonSection = 'S' | ('K' << 8) | ('E' << 16) | ((uint)'L' << 24);

    /// <summary>Section <c>COLL</c>: collision as convex hulls expressed as plane sets.</summary>
    public const uint CollisionSection = 'C' | ('O' << 8) | ('L' << 16) | ((uint)'L' << 24);

    /// <summary>Section <c>NAME</c>: the string blob every name offset indexes.</summary>
    public const uint NameSection = 'N' | ('A' << 8) | ('M' << 16) | ((uint)'E' << 24);

    /// <summary>
    /// Section <c>ANIM</c>, reserved and never written: animation clips live in a
    /// separate file, because one skeleton with many clips is the normal case and
    /// welding clips into the mesh forces a mesh recook when a clip changes. It is
    /// named here so the FourCC cannot be spent on something else, and so this
    /// reader treats it as an unknown section rather than as a mistake.
    /// </summary>
    public const uint AnimationSection = 'A' | ('N' << 8) | ('I' << 16) | ((uint)'M' << 24);

    /// <summary>
    /// The layout identity a header stamps: FNV-1a over each attribute's
    /// <c>(semantic, component count)</c> pair, in declaration order.
    /// </summary>
    /// <remarks>
    /// <para><b>Over the pairs and nothing else</b>, which is what the format
    /// specification asks for. It answers "does a shader see the same
    /// attributes", and two layouts differing only in where their padding sits
    /// are the same question to a shader. A stride change is caught by the
    /// <c>VBUF</c> length check and a wholesale change in what compiled geometry
    /// means is caught by <c>GeometryFormatVersion</c>, so this value is the
    /// precise report rather than the whole gate.
    /// </para>
    /// <para>Its job at load is a self-consistency check: a header stamped from
    /// one layout over a <c>VTXL</c> carrying another is a writer that was edited
    /// in one place and not the other, and the symptom without this check is a
    /// misinterpreted vertex buffer rather than an exception.</para>
    /// </remarks>
    public static uint ComputeVertexLayoutId(ReadOnlySpan<SmodelVertexAttribute> attributes)
    {
        const uint offsetBasis = 2166136261u;
        const uint prime = 16777619u;

        uint hash = offsetBasis;
        for (int i = 0; i < attributes.Length; i++)
        {
            hash = (hash ^ (byte)attributes[i].Semantic) * prime;
            hash = (hash ^ attributes[i].ComponentCount) * prime;
        }

        return hash;
    }

    /// <summary>
    /// Rounds <paramref name="value"/> up to the next multiple of
    /// <paramref name="alignment"/>, which must be a power of two.
    /// </summary>
    /// <remarks>
    /// One implementation, borrowed from the container this format ships inside:
    /// a second copy of alignment arithmetic is exactly the kind that gets fixed
    /// in one place and not in the other.
    /// </remarks>
    public static long AlignUp(long value, int alignment) => Packs.PackFormat.AlignUp(value, alignment);

    /// <summary>
    /// Renders a FourCC as the four characters it reads as, for a message.
    /// </summary>
    /// <remarks>
    /// Non-printable bytes become <c>?</c> rather than being emitted raw: an
    /// unknown section's FourCC arrives from a file that may be arbitrary bytes,
    /// and a control character in an exception message is how a log line stops
    /// being greppable.
    /// </remarks>
    public static string DescribeFourCc(uint fourCc)
    {
        Span<char> chars = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            char c = (char)((fourCc >> (i * 8)) & 0xFF);
            chars[i] = c is >= ' ' and <= '~' ? c : '?';
        }

        return new string(chars);
    }

    /// <summary>
    /// Refuses to read a <c>.smodel</c> on a big-endian machine.
    /// </summary>
    /// <remarks>
    /// The whole zero-copy premise is <c>MemoryMarshal.Cast</c> over raw mapped
    /// bytes, which is endianness-native by construction. A byte-swapping reader
    /// would have to copy every vertex, that is, do the one thing this format
    /// exists to avoid, so the honest answer is to refuse loudly rather than to
    /// pretend.
    /// </remarks>
    /// <exception cref="PlatformNotSupportedException">The machine is big-endian.</exception>
    public static void RequireLittleEndian()
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException(
                "The .smodel format is little-endian only: its vertex, index and collision payloads are " +
                "reinterpreted in place, so a big-endian host would have to copy and byte-swap all of it.");
        }
    }
}
