using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace SpectraEngine.Core.Assets.Models;

/// <summary>
/// A validated <c>.smodel</c>, as spans into the bytes it was read from.
/// </summary>
/// <remarks>
/// <para><b>A <c>ref struct</c>, because every span here points into somebody
/// else's buffer.</b> The buffer is normally a memory-mapped view of a pack, and
/// unmapping a view while a span into it is alive is an access violation rather
/// than an exception: no managed stack, no catch block. Being a ref struct is what
/// makes it impossible to park one of these in a field, hand it to a lambda or
/// await across it, so the model provably cannot outlive the mapping the caller
/// is holding open.</para>
/// <para><b>Nothing here is copied and nothing here is parsed twice.</b> Every
/// table is the file's own bytes reinterpreted in place, which is the entire
/// reason the format lays its records out at fixed strides on aligned offsets.
/// The validation that makes that safe has already run, once, in
/// <see cref="SmodelReader"/>.</para>
/// <para><b>The format and geometry versions are not exposed, deliberately.</b>
/// They are gates rather than data: the reader refuses any file that does not
/// carry this engine's own values, so a consumer reading them back could only
/// ever see the constants it already has.</para>
/// </remarks>
public readonly ref struct SmodelModel
{
    /// <summary>What the file was called, for a message raised at use time.</summary>
    public readonly string Source;

    /// <summary>Whole-model properties as declared in the header.</summary>
    public readonly SmodelFlags Flags;

    /// <summary>
    /// The header's stamp of its own vertex layout, checked against
    /// <c>VTXL</c> at read. A consumer compares it with the id of the layout the
    /// renderer wants, and either hands <see cref="Vertices"/> straight to
    /// <c>CreateMesh</c> or stride-copies.
    /// </summary>
    public readonly uint VertexLayoutId;

    /// <summary>Model-local minimum corner, which feeds mesh bounds with no vertex walk.</summary>
    public readonly Vector3 BoundsMin;

    /// <summary>Model-local maximum corner.</summary>
    public readonly Vector3 BoundsMax;

    /// <summary>The layout <see cref="Vertices"/> is interleaved in.</summary>
    public readonly ReadOnlySpan<SmodelVertexAttribute> VertexAttributes;

    /// <summary>Floats per vertex, which is the stride the layout describes.</summary>
    public readonly uint VertexStrideFloats;

    /// <summary>The one interleaved vertex buffer, shared by every submesh and every LOD.</summary>
    public readonly ReadOnlySpan<float> Vertices;

    /// <summary>The index buffer when it is 16-bit; empty when it is not.</summary>
    public readonly ReadOnlySpan<ushort> Indices16;

    /// <summary>The index buffer when it is 32-bit; empty when it is not.</summary>
    public readonly ReadOnlySpan<uint> Indices32;

    /// <summary>Submeshes, as index ranges into the one index buffer.</summary>
    public readonly ReadOnlySpan<SmodelSubmesh> Submeshes;

    /// <summary>Levels of detail, as submesh ranges. Empty when the model has none.</summary>
    public readonly ReadOnlySpan<SmodelLod> Lods;

    /// <summary>Skeleton joints in parent-before-child order. Empty when there is no skeleton.</summary>
    public readonly ReadOnlySpan<SmodelJoint> Joints;

    /// <summary>Collision hulls. Empty when the model carries no collision.</summary>
    public readonly ReadOnlySpan<SmodelCollisionHull> CollisionHulls;

    /// <summary>
    /// Every collision plane of every hull, flat. A hull's own planes come from
    /// <see cref="PlanesOf"/> rather than from arithmetic at the call site.
    /// </summary>
    public readonly ReadOnlySpan<Plane> CollisionPlanes;

    /// <summary>The string blob every name offset indexes. Empty when there is none.</summary>
    public readonly ReadOnlySpan<byte> Names;

    /// <summary>
    /// How many sections the reader did not recognise and stepped over.
    /// </summary>
    /// <remarks>
    /// Exposed because skipping is the format's forward-compatibility mechanism,
    /// and a mechanism with no observable effect is one a test cannot tell from a
    /// reader that silently mis-parsed the table and found nothing.
    /// </remarks>
    public readonly int SkippedSectionCount;

    internal SmodelModel(
        string source,
        SmodelFlags flags,
        uint vertexLayoutId,
        Vector3 boundsMin,
        Vector3 boundsMax,
        ReadOnlySpan<SmodelVertexAttribute> vertexAttributes,
        uint vertexStrideFloats,
        ReadOnlySpan<float> vertices,
        ReadOnlySpan<ushort> indices16,
        ReadOnlySpan<uint> indices32,
        ReadOnlySpan<SmodelSubmesh> submeshes,
        ReadOnlySpan<SmodelLod> lods,
        ReadOnlySpan<SmodelJoint> joints,
        ReadOnlySpan<SmodelCollisionHull> collisionHulls,
        ReadOnlySpan<Plane> collisionPlanes,
        ReadOnlySpan<byte> names,
        int skippedSectionCount)
    {
        Source = source;
        Flags = flags;
        VertexLayoutId = vertexLayoutId;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
        VertexAttributes = vertexAttributes;
        VertexStrideFloats = vertexStrideFloats;
        Vertices = vertices;
        Indices16 = indices16;
        Indices32 = indices32;
        Submeshes = submeshes;
        Lods = lods;
        Joints = joints;
        CollisionHulls = collisionHulls;
        CollisionPlanes = collisionPlanes;
        Names = names;
        SkippedSectionCount = skippedSectionCount;
    }

    /// <summary>Whether the index buffer is 32-bit.</summary>
    public bool Index32 => (Flags & SmodelFlags.Index32) != 0;

    /// <summary>How many vertices the buffer holds.</summary>
    public int VertexCount => VertexStrideFloats == 0 ? 0 : Vertices.Length / (int)VertexStrideFloats;

    /// <summary>How many indices the buffer holds, whichever width it is.</summary>
    public int IndexCount => Index32 ? Indices32.Length : Indices16.Length;

    /// <summary>Whether a skeleton is present.</summary>
    /// <remarks>
    /// Derived from the section table rather than from
    /// <see cref="SmodelFlags.HasSkeleton"/>, because the table is what actually
    /// carries the joints. The flag is checked against this at read, so by here
    /// the two provably agree.
    /// </remarks>
    public bool HasSkeleton => !Joints.IsEmpty;

    /// <summary>Whether collision hulls are present.</summary>
    public bool HasCollision => !CollisionHulls.IsEmpty;

    /// <summary>
    /// One index, widened to <c>uint</c> whichever width the file stores.
    /// </summary>
    /// <remarks>
    /// <c>Renderer.CreateMesh</c> takes <c>ReadOnlySpan&lt;uint&gt;</c> only, so a
    /// 16-bit file widens on the way to the GPU. Recording the true width from
    /// day one is what keeps a native 16-bit upload path open later; this
    /// accessor is what a consumer walks indices with in the meantime.
    /// </remarks>
    public uint IndexAt(int index) => Index32 ? Indices32[index] : Indices16[index];

    /// <summary>The planes bounding one hull.</summary>
    /// <remarks>
    /// The slice lives here rather than at each call site because a hull's range
    /// was validated against the plane array once, at read, and re-deriving it
    /// with arithmetic somewhere else is how the validated version stops being
    /// the one that runs.
    /// </remarks>
    public ReadOnlySpan<Plane> PlanesOf(in SmodelCollisionHull hull) =>
        CollisionPlanes.Slice((int)hull.PlaneStart, (int)hull.PlaneCount);

    /// <summary>
    /// The name at <paramref name="nameOffset"/>, or the empty string when the
    /// offset is <see cref="SmodelFormat.NameOffsetAbsent"/>.
    /// </summary>
    /// <remarks>
    /// Read through the record's own <c>u16</c> length prefix, the same shape the
    /// pack's name table uses. Every offset the format itself stores was bounds
    /// checked at read; this still validates, because the parameter is a
    /// <c>uint</c> a caller may have computed.
    /// </remarks>
    /// <exception cref="SmodelFormatException">The offset is not a name record.</exception>
    public string GetName(uint nameOffset)
    {
        if (nameOffset == SmodelFormat.NameOffsetAbsent) return string.Empty;

        SmodelReader.RequireNameRecord(Source, Names, nameOffset, "a name offset");

        ReadOnlySpan<byte> record = Names[(int)nameOffset..];
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(record);
        return Encoding.UTF8.GetString(record.Slice(sizeof(ushort), length));
    }
}
