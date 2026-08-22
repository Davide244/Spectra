using System;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace SpectraEngine.Core.Graphics.D3D12;

/// <summary>
/// The render-target configuration a pipeline state is compiled against:
/// colour format, how many colour targets, depth format, and sample count.
/// </summary>
/// <remarks>
/// <para>
/// D3D12 bakes all four into the pipeline state object, so a pipeline built for
/// one target configuration is invalid for another. Every draw in the engine
/// currently goes to the back buffer, which is why a single hardcoded
/// configuration worked. It stops working the moment anything renders somewhere
/// else, and the four things that will are all scheduled: an sRGB back-buffer
/// view, offscreen targets for post-processing, depth-only targets for shadow
/// maps, and multisampled targets.
/// </para>
/// <para>
/// Zero colour targets is legal and is what a depth-only shadow pass uses.
/// </para>
/// </remarks>
internal readonly record struct D3D12TargetState(
    Format ColorFormat,
    uint RenderTargetCount,
    Format DepthFormat,
    uint SampleCount)
{
    /// <summary>The window's back buffer with the shared depth buffer: what every draw uses today.</summary>
    public static D3D12TargetState BackBuffer => new(
        D3D12Renderer.BackBufferFormat, 1, D3D12Renderer.DepthFormat, 1);
}

/// <summary>
/// The full identity of a compiled pipeline state: everything D3D12 bakes into
/// one and therefore everything that must distinguish two cache entries.
/// </summary>
/// <remarks>
/// <para>
/// <b>Equality is structural, never hash-based.</b> The hash only picks a
/// bucket. Comparing hashes instead would let a collision return a pipeline
/// built for a different vertex layout or a different target format, and the
/// symptom of that is corrupted or missing geometry that reproduces on one
/// machine and not another.
/// </para>
/// <para>
/// <b>The vertex layout is compared element by element</b> for the same reason:
/// the layout carries its own precomputed hash for bucketing, and using it as
/// identity would have exactly the collision problem described above.
/// </para>
/// </remarks>
internal readonly struct D3D12PsoKey : IEquatable<D3D12PsoKey>
{
    public readonly D3D12VertexLayout Layout;
    public readonly FillMode Fill;
    public readonly PrimitiveTopologyType Topology;
    public readonly DepthMode Depth;
    public readonly BlendMode Blend;
    public readonly D3D12TargetState Target;

    public D3D12PsoKey(
        D3D12VertexLayout layout,
        FillMode fill,
        PrimitiveTopologyType topology,
        DepthMode depth,
        BlendMode blend,
        in D3D12TargetState target)
    {
        Layout = layout;
        Fill = fill;
        Topology = topology;
        Depth = depth;
        Blend = blend;
        Target = target;
    }

    public bool Equals(D3D12PsoKey other) =>
        Fill == other.Fill
        && Topology == other.Topology
        && Depth == other.Depth
        && Blend == other.Blend
        && Target == other.Target
        && Layout.StrideBytes == other.Layout.StrideBytes
        && Layout.Elements.AsSpan().SequenceEqual(other.Layout.Elements);

    public override bool Equals(object? obj) => obj is D3D12PsoKey other && Equals(other);

    // Bucketing only. Equals above is the identity, and HashCode.Combine takes
    // at most eight arguments, so the target state contributes as one nested
    // hash rather than four fields.
    public override int GetHashCode() =>
        HashCode.Combine(Layout.Key, Fill, Topology, Depth, Blend, Target.GetHashCode());
}
