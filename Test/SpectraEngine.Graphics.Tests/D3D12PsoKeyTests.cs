using System;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.D3D12;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// Pipeline-state cache identity, tested without a device.
/// </summary>
/// <remarks>
/// <para>
/// The failure this guards is quiet and machine-dependent: a cached pipeline
/// handed back for a draw that wanted different state. D3D12 bakes the vertex
/// layout, fill mode, topology class, depth and blend state and the whole render
/// target configuration into a pipeline object, so every one of those has to
/// separate two cache entries. A field left out of the key does not fail at
/// build time and does not always fail at run time; the debug layer catches some
/// mismatches and not others.
/// </para>
/// <para>
/// These need no GPU, which is the point. The key is a value type and its
/// equality is ordinary code, so the one thing most likely to rot is the one
/// thing testable everywhere.
/// </para>
/// </remarks>
public sealed class D3D12PsoKeyTests
{
    [Fact]
    public void Two_keys_describing_the_same_draw_are_equal()
    {
        D3D12PsoKey a = Key();
        D3D12PsoKey b = Key();

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void The_depth_mode_separates_two_keys()
    {
        // The case that motivated this milestone. Depth used to be a mutable
        // flag on the program, read while building a pipeline but absent from
        // the key, so an overlay draw and an opaque draw through the same
        // program would have shared one pipeline.
        Assert.NotEqual(Key(depth: DepthMode.TestWrite), Key(depth: DepthMode.None));
        Assert.NotEqual(Key(depth: DepthMode.TestWrite), Key(depth: DepthMode.TestNoWrite));
    }

    [Fact]
    public void The_blend_mode_separates_two_keys()
    {
        Assert.NotEqual(Key(blend: BlendMode.Opaque), Key(blend: BlendMode.AlphaBlend));
    }

    [Theory]
    [InlineData(Format.FormatR8G8B8A8UnormSrgb, 1u, Format.FormatD24UnormS8Uint, 1u)]  // sRGB view, R2
    [InlineData(Format.FormatR16G16B16A16Float, 1u, Format.FormatD24UnormS8Uint, 1u)]  // HDR offscreen, R4
    [InlineData(Format.FormatR8G8B8A8Unorm, 0u, Format.FormatD32Float, 1u)]            // depth only, R6
    [InlineData(Format.FormatR8G8B8A8Unorm, 1u, Format.FormatD24UnormS8Uint, 4u)]      // MSAA, R11
    public void Every_part_of_the_target_configuration_separates_two_keys(
        Format color, uint count, Format depth, uint samples)
    {
        // One case per downstream milestone that will make this field vary. If
        // any of them stopped separating keys, that milestone would render
        // through a pipeline compiled for the back buffer.
        var target = new D3D12TargetState(color, count, depth, samples);
        Assert.NotEqual(Key(), Key(target: target));
    }

    [Fact]
    public void The_fill_mode_and_topology_separate_two_keys()
    {
        Assert.NotEqual(Key(fill: FillMode.Solid), Key(fill: FillMode.Wireframe));
        Assert.NotEqual(
            Key(topology: PrimitiveTopologyType.Triangle),
            Key(topology: PrimitiveTopologyType.Line));
    }

    [Fact]
    public void The_vertex_layout_is_compared_element_by_element_not_by_its_hash()
    {
        // Two layouts that differ only in one element's format. Keying on the
        // layout's precomputed hash would let a collision return a pipeline
        // built for a different vertex layout, which is corrupt geometry that
        // reproduces on one machine and not another.
        D3D12VertexLayout standard = Layout(Format.FormatR32G32B32Float);
        D3D12VertexLayout altered = Layout(Format.FormatR32G32Float);

        Assert.NotEqual(Key(layout: standard), Key(layout: altered));

        // And two structurally identical layouts that are different objects must
        // still collapse to one cache entry, or every mesh compiles its own
        // pipeline.
        Assert.Equal(
            Key(layout: Layout(Format.FormatR32G32B32Float)),
            Key(layout: Layout(Format.FormatR32G32B32Float)));
    }

    [Fact]
    public void A_key_survives_a_round_trip_through_a_dictionary()
    {
        // What the cache actually does: bucket by hash, then compare.
        var cache = new System.Collections.Generic.Dictionary<D3D12PsoKey, int>
        {
            [Key()] = 1,
            [Key(depth: DepthMode.None)] = 2,
        };

        Assert.Equal(2, cache.Count);
        Assert.Equal(1, cache[Key()]);
        Assert.Equal(2, cache[Key(depth: DepthMode.None)]);
    }

    private static D3D12PsoKey Key(
        D3D12VertexLayout? layout = null,
        FillMode fill = FillMode.Solid,
        PrimitiveTopologyType topology = PrimitiveTopologyType.Triangle,
        DepthMode depth = DepthMode.TestWrite,
        BlendMode blend = BlendMode.Opaque,
        D3D12TargetState? target = null)
    {
        D3D12TargetState state = target ?? D3D12TargetState.BackBuffer;
        return new D3D12PsoKey(layout ?? Layout(Format.FormatR32G32B32Float), fill, topology, depth, blend, in state);
    }

    private static D3D12VertexLayout Layout(Format firstElementFormat) => new(
    [
        new D3D12VertexLayout.Element(0, firstElementFormat, 0),
        new D3D12VertexLayout.Element(1, Format.FormatR32G32Float, 12),
    ], strideBytes: 32);
}
