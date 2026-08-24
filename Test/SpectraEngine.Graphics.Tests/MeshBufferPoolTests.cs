using SpectraEngine.Core.Graphics.D3D12;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// The bucket function behind D3D12's mesh buffer pool.
/// </summary>
/// <remarks>
/// <para>
/// <b>The pool exists because CreateCommittedResource is expensive.</b> Measured
/// at roughly 480 microseconds per mesh on this backend against 24 on D3D11's
/// CreateBuffer, which cost 7 ms of a 10 ms frame while the static-world
/// compiler was landing new chunk meshes every frame.
/// </para>
/// <para>
/// Bucketing is what makes the pool actually hit: chunk meshes change size by a
/// few triangles between compiles, so exact-size matching would recycle almost
/// nothing and allocate almost everything. Needs no GPU to check.
/// </para>
/// </remarks>
public sealed class MeshBufferPoolTests
{
    [Theory]
    [InlineData(0u, 256u)]
    [InlineData(1u, 256u)]
    [InlineData(256u, 256u)]
    [InlineData(257u, 512u)]
    [InlineData(1000u, 1024u)]
    [InlineData(1024u, 1024u)]
    [InlineData(1025u, 2048u)]
    [InlineData(100_000u, 131_072u)]
    public void A_request_rounds_up_to_its_bucket(uint requested, uint expected)
    {
        // Never smaller than asked for, or the buffer would be too small for the
        // data written into it; never below 256, which is D3D12's buffer
        // alignment.
        D3D12Renderer.MeshBufferBucket(requested).ShouldBe(expected);
    }

    [Fact]
    public void Sizes_that_differ_by_a_few_triangles_share_a_bucket()
    {
        // The whole point. A chunk recompiled after a brush moved a centimetre
        // emits a slightly different vertex count, and a pool keyed on the exact
        // byte count would miss every single time.
        const uint baseline = 40_000;
        uint bucket = D3D12Renderer.MeshBufferBucket(baseline);

        for (uint delta = 0; delta < 4_000; delta += 137)
            D3D12Renderer.MeshBufferBucket(baseline + delta).ShouldBe(bucket);
    }
}
