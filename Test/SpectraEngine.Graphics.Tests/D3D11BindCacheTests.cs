using System;
using System.IO;
using SpectraEngine.Core.Graphics.D3D11;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// The context-level SRV/sampler skip cache behind D3D11's SetTexture.
/// </summary>
/// <remarks>
/// <para>
/// <b>The reset contract is the whole point of these tests.</b> A predecessor
/// of this cache lived per program and was never reset, so after BeginPass
/// nulled the context's SRV slots for an offscreen pass, SetTexture kept
/// skipping the rebind and every later pass sampled null SRVs. D3D11 defines
/// that read as zeros: nothing threw, the debug layer stayed silent, the
/// offscreen probe (which reads error counts) stayed green, and the screen
/// probe (a raycast) cannot see pixels. A wrong picture with every gate
/// passing is the most expensive kind of bug this engine has had, so the
/// invariant lives in a pure class and is pinned here without a device.
/// </para>
/// </remarks>
public sealed class D3D11BindCacheTests
{
    [Fact]
    public void The_first_bind_of_a_slot_is_issued()
    {
        var cache = new D3D11BindCache();
        cache.MustBind(0, srv: 0x10, sampler: 0x20).ShouldBeTrue();
    }

    [Fact]
    public void Repeating_the_same_pair_is_skipped()
    {
        // The steady state: materials re-apply the same textures every draw.
        var cache = new D3D11BindCache();
        cache.MustBind(0, 0x10, 0x20);
        cache.MustBind(0, 0x10, 0x20).ShouldBeFalse();
    }

    [Fact]
    public void Changing_either_half_of_the_pair_rebinds()
    {
        // SRV and sampler share a register by the shader generator's layout,
        // so a texture swapping only its sampler state still needs the bind.
        var cache = new D3D11BindCache();
        cache.MustBind(0, 0x10, 0x20);
        cache.MustBind(0, 0x11, 0x20).ShouldBeTrue("a different SRV must be bound");
        cache.MustBind(0, 0x11, 0x21).ShouldBeTrue("a different sampler must be bound");
    }

    [Fact]
    public void Slots_are_independent()
    {
        var cache = new D3D11BindCache();
        cache.MustBind(0, 0x10, 0x20);
        cache.MustBind(5, 0x10, 0x20).ShouldBeTrue("slot 5 has never seen this pair");
    }

    [Fact]
    public void Reset_forces_every_slot_to_rebind()
    {
        // The regression this class exists to prevent: after the context's
        // slots are cleared (offscreen BeginPass, ClearState on resize), a
        // skipped rebind samples null and renders a silently wrong picture.
        var cache = new D3D11BindCache();
        cache.MustBind(0, 0x10, 0x20);
        cache.MustBind(3, 0x30, 0x40);

        cache.Reset();

        cache.MustBind(0, 0x10, 0x20).ShouldBeTrue();
        cache.MustBind(3, 0x30, 0x40).ShouldBeTrue();
    }

    [Fact]
    public void A_slot_outside_the_tracked_range_always_binds()
    {
        // The unbind clears exactly TrackedSlots registers, so anything past
        // them has no reset site and must never be skipped.
        var cache = new D3D11BindCache();
        uint outside = D3D11BindCache.TrackedSlots;
        cache.MustBind(outside, 0x10, 0x20).ShouldBeTrue();
        cache.MustBind(outside, 0x10, 0x20).ShouldBeTrue("no skip without a reset contract");
    }

    [Fact]
    public void Every_context_clearing_site_in_the_renderer_resets_the_cache()
    {
        // The shipped bug was not the cache, it was the WIRING: the context's
        // slots were cleared and no reset followed. The wiring cannot run
        // headlessly (it needs a device), so it is enforced the way the
        // ComPtr ownership rule is: in the source. Both context-clearing
        // sites must be followed by a cache reset within a few lines.
        string source = File.ReadAllText(RendererSourcePath());

        int unbind = source.IndexOf("PSSetShaderResources(0, Slots, none);", StringComparison.Ordinal);
        unbind.ShouldBeGreaterThanOrEqualTo(0, "the SRV unbind site moved; update this test with it");
        NearbyReset(source, unbind).ShouldBeTrue(
            "UnbindPixelShaderResources cleared the context's slots without resetting the bind cache; " +
            "the next pass will skip a needed rebind and silently sample null");

        int clearState = source.IndexOf("ClearState();", StringComparison.Ordinal);
        clearState.ShouldBeGreaterThanOrEqualTo(0, "the resize path's ClearState moved; update this test with it");
        NearbyReset(source, clearState).ShouldBeTrue(
            "the resize path's ClearState wiped the context without resetting the bind cache");
    }

    // A reset within the following ~5 lines of the clearing call.
    private static bool NearbyReset(string source, int fromIndex)
    {
        int window = Math.Min(source.Length - fromIndex, 500);
        return source.AsSpan(fromIndex, window).IndexOf("_bindCache.Reset();".AsSpan(), StringComparison.Ordinal) >= 0;
    }

    // The same repo-root walk ComPtrOwnershipConventionTests uses.
    private static string RendererSourcePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("*.slnx").Length > 0 || dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "SpectraEngine.Core", "Graphics", "D3D11", "D3D11Renderer.cs");
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"No solution file above {AppContext.BaseDirectory}; this source-convention test needs the repo.");
    }
}
