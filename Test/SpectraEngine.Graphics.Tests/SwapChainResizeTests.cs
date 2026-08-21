using Silk.NET.Maths;
using SpectraEngine.Core.Graphics;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// The swap-chain resize decision and the HRESULT classification behind it —
/// the two pieces of the D3D resize path that are pure logic, and the two that
/// were wrong when a fullscreen transition killed the render thread.
/// </summary>
/// <remarks>
/// The GPU half (release views → <c>ResizeBuffers</c> → rebuild views) cannot
/// be unit-tested; a real repro run is the gate for that.
/// </remarks>
public sealed class SwapChainResizeTests
{
    private static Vector2D<int> Size(int x, int y) => new(x, y);

    private static bool ShouldResize(
        Vector2D<int> requested,
        Vector2D<int> current,
        Vector2D<int>? lastFailed = null,
        bool swapChainAlive = true,
        bool deviceLost = false) =>
        SwapChainResizePolicy.ShouldResize(requested, current, lastFailed, swapChainAlive, deviceLost);

    [Fact]
    public void A_changed_size_resizes()
    {
        ShouldResize(Size(1600, 900), Size(1280, 720)).ShouldBeTrue();
    }

    [Fact]
    public void The_same_size_does_not()
    {
        ShouldResize(Size(1280, 720), Size(1280, 720)).ShouldBeFalse();
    }

    [Fact]
    public void A_minimised_window_is_skipped_rather_than_clamped()
    {
        // GLFW reports 0x0 while minimised and ResizeBuffers rejects a zero
        // extent; skipping keeps the existing buffers valid.
        ShouldResize(Size(0, 0), Size(1280, 720)).ShouldBeFalse();
        ShouldResize(Size(1280, 0), Size(1280, 720)).ShouldBeFalse();
        ShouldResize(Size(0, 720), Size(1280, 720)).ShouldBeFalse();
        ShouldResize(Size(-4, -4), Size(1280, 720)).ShouldBeFalse();
    }

    [Fact]
    public void Restoring_to_the_pre_minimise_size_needs_no_resize_at_all()
    {
        // The skip above leaves the recorded size untouched, which is exactly
        // what makes the restore free: the buffers were never released.
        ShouldResize(Size(1280, 720), Size(1280, 720)).ShouldBeFalse();
    }

    [Fact]
    public void Restoring_to_a_different_size_resizes_once()
    {
        ShouldResize(Size(1920, 1080), Size(1280, 720)).ShouldBeTrue();
    }

    [Fact]
    public void A_size_that_already_failed_is_not_retried_every_frame()
    {
        ShouldResize(Size(1600, 900), Size(1280, 720), lastFailed: Size(1600, 900)).ShouldBeFalse();
    }

    [Fact]
    public void But_any_other_size_still_is()
    {
        // Which is what lets a user drag past a bad size and carry on, and — as
        // the caller clears the memo on the next success — come back to it.
        ShouldResize(Size(1601, 900), Size(1280, 720), lastFailed: Size(1600, 900)).ShouldBeTrue();
    }

    [Fact]
    public void Nothing_is_asked_of_a_missing_swap_chain_or_a_lost_device()
    {
        ShouldResize(Size(1600, 900), Size(1280, 720), swapChainAlive: false).ShouldBeFalse();
        ShouldResize(Size(1600, 900), Size(1280, 720), deviceLost: true).ShouldBeFalse();
    }

    [Fact]
    public void Device_removed_and_device_reset_are_device_loss_not_resize_failures()
    {
        DxgiInterop.IsDeviceLost(DxgiInterop.ErrorDeviceRemoved).ShouldBeTrue();
        DxgiInterop.IsDeviceLost(DxgiInterop.ErrorDeviceReset).ShouldBeTrue();
        DxgiInterop.IsDeviceLost(DxgiInterop.ErrorDeviceHung).ShouldBeTrue();
        DxgiInterop.IsDeviceLost(DxgiInterop.ErrorDriverInternalError).ShouldBeTrue();
        DxgiInterop.IsDeviceLost(DxgiInterop.DdiErrorDeviceRemoved).ShouldBeTrue();
    }

    [Fact]
    public void Invalid_call_is_recoverable_and_must_not_be_mistaken_for_device_loss()
    {
        // This is the HRESULT the reported crash carried. Treating it as a lost
        // device would end the run; treating it as fatal at all was the bug.
        DxgiInterop.IsDeviceLost(DxgiInterop.ErrorInvalidCall).ShouldBeFalse();
        DxgiInterop.IsDeviceLost(0).ShouldBeFalse();
    }

    [Fact]
    public void Known_hresults_are_named_in_the_log_line()
    {
        DxgiInterop.Describe(DxgiInterop.ErrorInvalidCall).ShouldBe("DXGI_ERROR_INVALID_CALL");
        DxgiInterop.Describe(DxgiInterop.ErrorDeviceRemoved).ShouldBe("DXGI_ERROR_DEVICE_REMOVED");
        DxgiInterop.Describe(0).ShouldBe("S_OK");
        DxgiInterop.Describe(unchecked((int)0x8000FFFF)).ShouldBe("unknown HRESULT");
    }

    [Fact]
    public void Alt_enter_is_suppressed_with_the_documented_dxgi_flag()
    {
        // DXGI_MWA_NO_ALT_ENTER == (1 << 1). Silk.NET names no MWA constant, so
        // the value is ours to get right — and getting it wrong would silently
        // leave DXGI driving fullscreen on the window thread.
        DxgiInterop.MwaNoAltEnter.ShouldBe(2u);
        DxgiInterop.MwaNoWindowChanges.ShouldBe(1u);
        DxgiInterop.MwaNoPrintScreen.ShouldBe(4u);
    }
}
