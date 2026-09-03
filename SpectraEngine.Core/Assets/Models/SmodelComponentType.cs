namespace SpectraEngine.Core.Assets.Models;

/// <summary>
/// The element type of one vertex attribute's components.
/// </summary>
/// <remarks>
/// <para><b>v1 writes <see cref="Float32"/> and only <see cref="Float32"/>,</b>
/// because <c>VTXL</c> states its stride in <em>floats</em> and
/// <c>Renderer.CreateMesh</c> takes a <c>ReadOnlySpan&lt;float&gt;</c>: an
/// interleaved buffer carrying anything else has nowhere to go without a
/// conversion pass, which is the work the cook is supposed to have already
/// done.</para>
/// <para>The narrower types are named anyway, and not enforced, because
/// <see cref="SmodelSemantic.BlendIndices"/> genuinely wants a <c>u8</c> or a
/// <c>u16</c> and reserving the numbering now is free. The reader surfaces
/// whatever the file declares rather than refusing it, so the decision about what
/// a consumer can actually upload stays with the consumer, which is the only
/// layer that knows.</para>
/// </remarks>
public enum SmodelComponentType : byte
{
    /// <summary>IEEE 754 binary32. The only type v1 cooks.</summary>
    Float32 = 0,

    /// <summary>Unsigned 8-bit integer. Reserved for blend indices.</summary>
    UInt8 = 1,

    /// <summary>Unsigned 16-bit integer. Reserved for blend indices.</summary>
    UInt16 = 2,

    /// <summary>Unsigned 32-bit integer. Reserved.</summary>
    UInt32 = 3,
}
