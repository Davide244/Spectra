using System;

namespace SpectraEngine.Core.Assets.Models;

/// <summary>
/// The one vertex layout this engine's cook writes and this engine's loader
/// accepts without a conversion: position, normal, UV0, eight interleaved
/// floats.
/// </summary>
/// <remarks>
/// <para><b>It is stated once, here, because a cooker and a loader that each
/// spell it out are two spellings of one fact.</b> The writer builds
/// <c>VTXL</c> from <see cref="Attributes"/> and the loader compares the file's
/// stamped <c>VertexLayoutId</c> against <see cref="LayoutId"/>; with two copies
/// the two would agree until one was edited, and then disagree as a
/// misinterpreted vertex buffer rather than as an exception.</para>
/// <para><b>The offsets come from <see cref="ModelVertexLayout"/> rather than
/// from literals</b>, so this layout and the arrays <c>ModelImporter</c>
/// produces cannot drift apart: they are the same eight floats, and the loose
/// and cooked paths hand the same shape to
/// <c>VertexAttribute.StandardLayout</c>.</para>
/// <para><b>A file declaring any other layout is refused rather than
/// stride-copied, and that is a stated limit of v1.</b> The format was designed
/// so a reader can compare and then either hand <c>VBUF</c> straight to
/// <c>CreateMesh</c> or convert; only the first half exists, because nothing
/// writes a second layout yet and an untested conversion path is worth less than
/// an honest refusal. The conversion becomes real when
/// <c>VertexAttribute.StandardLayout</c> grows a tangent, and the refusal is
/// what makes that moment visible.</para>
/// </remarks>
public static class SmodelStandardLayout
{
    private static readonly SmodelVertexAttribute[] _attributes =
    [
        new(SmodelSemantic.Position, SmodelComponentType.Float32, 3,
            (ushort)(ModelVertexLayout.PositionOffset * sizeof(float))),
        new(SmodelSemantic.Normal, SmodelComponentType.Float32, 3,
            (ushort)(ModelVertexLayout.NormalOffset * sizeof(float))),
        new(SmodelSemantic.Uv0, SmodelComponentType.Float32, 2,
            (ushort)(ModelVertexLayout.TexCoordOffset * sizeof(float))),
    ];

    /// <summary>The three attributes, in declaration order.</summary>
    public static ReadOnlySpan<SmodelVertexAttribute> Attributes => _attributes;

    /// <summary>Floats per vertex, which is what <c>VTXL</c> stores as its stride.</summary>
    public const uint StrideFloats = (uint)ModelVertexLayout.FloatsPerVertex;

    /// <summary>
    /// The identity a header stamps for this layout, computed once.
    /// </summary>
    public static uint LayoutId { get; } = SmodelFormat.ComputeVertexLayoutId(_attributes);
}
