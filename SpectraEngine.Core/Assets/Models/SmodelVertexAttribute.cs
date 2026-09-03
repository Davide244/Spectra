using System.Runtime.InteropServices;

namespace SpectraEngine.Core.Assets.Models;

/// <summary>
/// One attribute of a cooked vertex layout, exactly as its eight bytes sit in a
/// <c>VTXL</c> section.
/// </summary>
/// <remarks>
/// <para><b><c>Pack = 1</c> is what makes this struct the format rather than a
/// description of it.</b> Raw bytes out of a mapped view are cast into an array
/// of these, so the CLR inserting padding would silently shift every attribute
/// after the first; with it there is no padding at all and every one of the eight
/// bytes is a declared field. The size is pinned by a test, because a field
/// reordered by an edit compiles cleanly and produces a reader that parses the
/// same file into different numbers.</para>
/// <para><b><see cref="ByteOffset"/> is stated rather than accumulated.</b> A
/// reader that summed the preceding attributes' widths would have to know each
/// component type's size, which is exactly the knowledge this format defers to
/// the cook, and it would silently disagree with a layout carrying padding.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct SmodelVertexAttribute
{
    /// <summary>What this attribute means. See <see cref="SmodelSemantic"/>.</summary>
    public readonly SmodelSemantic Semantic;

    /// <summary>The element type of each component.</summary>
    public readonly SmodelComponentType ComponentType;

    /// <summary>How many components: 3 for a position, 2 for a UV, 4 for a tangent.</summary>
    public readonly byte ComponentCount;

    /// <summary>Per-attribute flags. None defined in v1; written zero.</summary>
    public readonly byte Flags;

    /// <summary>Where this attribute starts within one vertex.</summary>
    public readonly ushort ByteOffset;

    /// <summary>Reserved, written zero, present so the record is a round eight bytes.</summary>
    public readonly ushort Reserved;

    /// <summary>Builds one attribute record. Every field is assigned.</summary>
    public SmodelVertexAttribute(
        SmodelSemantic semantic,
        SmodelComponentType componentType,
        byte componentCount,
        ushort byteOffset,
        byte flags = 0,
        ushort reserved = 0)
    {
        Semantic = semantic;
        ComponentType = componentType;
        ComponentCount = componentCount;
        Flags = flags;
        ByteOffset = byteOffset;
        Reserved = reserved;
    }
}
