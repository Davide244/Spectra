using Microsoft.Extensions.Logging;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.D3D12;
using SpectraShade.Compiler;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Xunit;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// The D3D12 composited present path, against a real driver: the D3D11On12
/// bridge, the keyed mutex on the texture it owns, and the debug layer staying
/// silent across the wrapped-resource bracket.
/// </summary>
/// <remarks>
/// <para>
/// <b>A bridge and not a direct handle, because that was measured.</b>
/// <c>--interop-probe</c> imports real textures rather than reading capability
/// flags, and on this machine the compositor refuses a D3D12-created handle with
/// <c>E_NOINTERFACE</c> inside its own import while a D3D11On12 device over the
/// same D3D12 device works. So nothing here asks a D3D12 resource for a shared
/// handle; the frame lands in an ordinary private target and one copy per frame
/// carries it across.
/// </para>
/// <para>
/// <b>The debug-layer assertion is the actual gate on this stage.</b> A wrapped
/// resource acquired from a state it is not in, or released back into one the
/// next frame's barrier will not expect, is a D3D12 resource-state error and
/// nothing else reports it: there is no swap chain to present, no offscreen
/// probe, and the picture would be right on this machine and wrong on another.
/// The pixel round-trip proves the copy happened; the counter proves it was
/// legal.
/// </para>
/// </remarks>
[Collection(D3DDeviceCollection.Name)]
public sealed unsafe class SharedTargetD3D12BridgeTests(SharedTargetD3D12Fixture fixture)
{
    private void Require() => Assert.SkipWhen(
        !fixture.Available,
        $"no usable D3D12 device in this process: {fixture.UnavailableReason}");

    [Fact]
    public void A_composited_surface_brings_a_device_up_with_no_swap_chain()
    {
        // The split's own assertion. A surface the engine does not present to
        // still needs a device, a queue, a command list, the base shaders and
        // every pipeline; only the chain and the back-buffer views drop out -
        // and the queue matters MORE here than on a window, because it is the
        // queue the bridge records its copy into.
        Require();

        fixture.Renderer.CurrentPipelineName.ShouldNotBe("None");
        fixture.Renderer.DebugLayerErrorCount.ShouldBe(0, fixture.Diagnostics);
    }

    [Fact]
    public void The_bridge_hands_out_an_nt_handle_with_its_size_and_generation()
    {
        Require();

        fixture.Renderer.TryGetSharedHandle(out Renderer.SharedTargetHandle handle).ShouldBeTrue();

        handle.NtHandle.ShouldNotBe(0);
        handle.Width.ShouldBe(SharedTargetD3D12Fixture.Width);
        handle.Height.ShouldBe(SharedTargetD3D12Fixture.Height);
        handle.Generation.ShouldBeGreaterThan(0, "zero is reserved for no target at all");
    }

    [Fact]
    public void The_handle_opens_on_a_second_device_that_knows_nothing_about_this_renderer()
    {
        // The whole point of routing through D3D11 at all: this is the claim a
        // D3D12-created handle failed, and no amount of reading creation flags
        // establishes it.
        Require();
        fixture.Renderer.TryGetSharedHandle(out Renderer.SharedTargetHandle handle).ShouldBeTrue();

        using var consumer = new ConsumerDevice(handle.NtHandle);

        consumer.Width.ShouldBe((uint)handle.Width);
        consumer.Height.ShouldBe((uint)handle.Height);
    }

    [Fact]
    public void The_imported_resource_is_unorm_so_the_engines_srgb_write_is_not_encoded_twice()
    {
        // Same claim the D3D11 path makes, and it holds here for the same reason
        // rather than a parallel one: the shared texture is built by D3D11's own
        // CreateRenderTargetTexture, so the UNORM-resource plus sRGB-view split
        // is one decision in one place. The D3D12 side has already encoded on
        // its own sRGB render-target view, and the copy across is a bit copy.
        Require();
        fixture.Renderer.TryGetSharedHandle(out Renderer.SharedTargetHandle handle).ShouldBeTrue();

        using var consumer = new ConsumerDevice(handle.NtHandle);

        consumer.Format.ShouldBe(
            Format.FormatR8G8B8A8Unorm,
            "the shared RESOURCE must not be sRGB-typed; only the views over it are");
    }

    [Fact]
    public void The_producer_and_the_consumer_take_turns_on_keys_zero_and_one()
    {
        // The numbers are pure convention with nothing in the API to enforce
        // them, and the two backends must agree about them or a host wired for
        // one deadlocks on the other.
        Require();
        fixture.Renderer.TryGetSharedHandle(out Renderer.SharedTargetHandle handle).ShouldBeTrue();

        using var consumer = new ConsumerDevice(handle.NtHandle);

        fixture.Renderer.BeginSharedWrite(1000).ShouldBeTrue();
        fixture.Renderer.EndSharedWrite();

        consumer.Acquire(Renderer.SharedConsumerKey, 1000).ShouldBe(0);
        consumer.Release(Renderer.SharedProducerKey).ShouldBe(0);

        // And round again, because a protocol that works once and deadlocks on
        // the second turn is exactly what releasing the wrong key looks like.
        fixture.Renderer.BeginSharedWrite(1000).ShouldBeTrue();
        fixture.Renderer.EndSharedWrite();
        consumer.Acquire(Renderer.SharedConsumerKey, 1000).ShouldBe(0);
        consumer.Release(Renderer.SharedProducerKey).ShouldBe(0);
    }

    [Fact]
    public void A_turn_the_consumer_never_takes_times_out_instead_of_blocking_the_render_thread()
    {
        // AcquireSync's timeout is WAIT_TIMEOUT, 0x00000102 - a POSITIVE
        // HRESULT, so the ordinary `hr < 0` failure test reads a stalled
        // consumer as a successful acquisition and the frame copies into a
        // texture the consumer is still reading. This is the assertion that
        // catches that, on a backend where the same mistake is a fresh
        // opportunity.
        Require();
        fixture.Renderer.TryGetSharedHandle(out Renderer.SharedTargetHandle handle).ShouldBeTrue();

        using var consumer = new ConsumerDevice(handle.NtHandle);

        fixture.Renderer.BeginSharedWrite(1000).ShouldBeTrue();
        fixture.Renderer.EndSharedWrite();
        consumer.Acquire(Renderer.SharedConsumerKey, 1000).ShouldBe(0);

        try
        {
            var waited = Stopwatch.StartNew();
            fixture.Renderer.BeginSharedWrite(50).ShouldBeFalse();
            waited.Stop();

            // Generous, because a machine under test load is not a stopwatch.
            // What it rules out is a wait that never returns at all.
            waited.ElapsedMilliseconds.ShouldBeLessThan(2000);
        }
        finally
        {
            consumer.Release(Renderer.SharedProducerKey).ShouldBe(0);
        }

        // And the skip is a skip, not a poisoning: the next frame goes through.
        fixture.Renderer.BeginSharedWrite(1000).ShouldBeTrue();
        fixture.Renderer.EndSharedWrite();
        consumer.Acquire(Renderer.SharedConsumerKey, 1000).ShouldBe(0);
        consumer.Release(Renderer.SharedProducerKey).ShouldBe(0);
    }

    [Fact]
    public void A_frame_published_through_the_bridge_arrives_on_the_other_device_encoded_once()
    {
        // Three claims in one readback, and they are only true together. The
        // bytes arriving at all is the wrapped resource and the copy working;
        // arriving with the right value is the sRGB render-target view on the
        // D3D12 side; the debug layer staying quiet is the state bracket. Linear
        // 0.5 encodes to sRGB 0.7354, which is 188 of 255 - so 128 would mean
        // the target's view never encoded and the compositor would show a
        // picture too dark, and 0 would mean the copy never landed.
        Require();
        fixture.Renderer.TryGetSharedHandle(out Renderer.SharedTargetHandle handle).ShouldBeTrue();

        using var consumer = new ConsumerDevice(handle.NtHandle);

        int errorsBefore = fixture.Renderer.DebugLayerErrorCount;

        fixture.WriteAndPublish(new Vector4(0.5f, 0f, 1f, 1f));

        (byte r, byte g, byte b, byte a) = consumer.ReadFirstPixel();

        r.ShouldBeInRange((byte)184, (byte)192, "128 would mean the sRGB render-target view never encoded at all");
        g.ShouldBe((byte)0);
        b.ShouldBe((byte)255);
        a.ShouldBe((byte)255);

        fixture.Renderer.DebugLayerErrorCount.ShouldBe(errorsBefore, fixture.Diagnostics);
    }

    [Fact]
    public void The_debug_layer_stays_silent_across_a_bridged_frame()
    {
        // The gate, stated on its own so a failure names the right thing. It is
        // skipped rather than passed when the layer is not running, because
        // zero-and-off and zero-and-clean are the same number and mean opposite
        // things - a green run on a machine with no Graphics Tools would be
        // proof of nothing at all.
        Require();
        Assert.SkipWhen(
            !fixture.Renderer.DebugLayerActive,
            "the D3D12 validation layer is not running, so a zero error count proves nothing");

        fixture.Renderer.TryGetSharedHandle(out Renderer.SharedTargetHandle handle).ShouldBeTrue();
        using var consumer = new ConsumerDevice(handle.NtHandle);

        int errorsBefore = fixture.Renderer.DebugLayerErrorCount;

        // Several, because the state bracket's failure mode is cumulative: a
        // release that puts the resource back into the wrong state is legal on
        // the frame that does it and wrong on the frame after. The consumer
        // takes its turn between them for the reason the timeout test states -
        // a producer that publishes twice with nobody consuming is skipping the
        // second one, which would make this measure nothing.
        for (int i = 0; i < 4; i++)
        {
            fixture.WriteAndPublish(new Vector4(0f, 1f, 0f, 1f));
            TakeTurn(consumer);
        }

        fixture.Renderer.DebugLayerErrorCount.ShouldBe(
            errorsBefore,
            "a wrapped resource acquired from a state it is not in is reported here and nowhere else: "
                + fixture.Diagnostics);
    }

    [Fact]
    public void A_resize_mints_a_new_generation_and_a_new_handle()
    {
        // What the consumer's re-import is keyed on. The old handle staying
        // valid is not enough and is not the point: it names a resource pair
        // being retired, and the generation is the only thing that says so.
        Require();

        fixture.Renderer.TryGetSharedHandle(out Renderer.SharedTargetHandle before).ShouldBeTrue();
        try
        {
            fixture.Resize(SharedTargetD3D12Fixture.Width + 32, SharedTargetD3D12Fixture.Height + 16);

            fixture.Renderer.TryGetSharedHandle(out Renderer.SharedTargetHandle after).ShouldBeTrue();

            after.Generation.ShouldBeGreaterThan(before.Generation);
            after.NtHandle.ShouldNotBe(before.NtHandle);
            after.Width.ShouldBe(SharedTargetD3D12Fixture.Width + 32);
            after.Height.ShouldBe(SharedTargetD3D12Fixture.Height + 16);

            // The retired pair is held rather than freed until the consumer says
            // it is done, so the acknowledgement must be accepted and must not
            // disturb the live generation.
            Should.NotThrow(() => fixture.Renderer.NotifySharedTargetReleased(before.Generation));
            fixture.Renderer.TryGetSharedHandle(out Renderer.SharedTargetHandle still).ShouldBeTrue();
            still.ShouldBe(after);

            // And the fresh pair works, which is what says the bridge rebuilt
            // its alias rather than keeping one that names the retired target.
            using var consumer = new ConsumerDevice(after.NtHandle);
            fixture.WriteAndPublish(new Vector4(0f, 1f, 0f, 1f));
            (byte r, byte g, byte b, _) = consumer.ReadFirstPixel();
            g.ShouldBe((byte)255);
            r.ShouldBe((byte)0);
            b.ShouldBe((byte)0);
        }
        finally
        {
            // Back to the size every other test in this class expects.
            fixture.Resize(SharedTargetD3D12Fixture.Width, SharedTargetD3D12Fixture.Height);
            fixture.Renderer.TryGetSharedHandle(out Renderer.SharedTargetHandle restored);
            fixture.Renderer.NotifySharedTargetReleased(restored.Generation - 1);
        }

        fixture.Renderer.DebugLayerErrorCount.ShouldBe(0, fixture.Diagnostics);
    }

    [Fact]
    public void A_shared_target_write_needs_no_swap_chain_to_end_its_frame()
    {
        // Present is skipped every frame on a composited surface, which is
        // exactly why the wait and the drain had to move OUT of the swap-chain
        // guard: the upload ring rewinds per recording, the mesh buffer pool
        // hands freed buffers straight back out, and the descriptor rings are
        // swapped here - all three are safe only because the GPU is idle at this
        // point. Dropping the wait because there is nothing to present corrupts
        // every one of them, and this is what says it did not happen.
        Require();
        fixture.Renderer.TryGetSharedHandle(out Renderer.SharedTargetHandle handle).ShouldBeTrue();
        using var consumer = new ConsumerDevice(handle.NtHandle);

        int errorsBefore = fixture.Renderer.DebugLayerErrorCount;

        for (int i = 0; i < 4; i++)
        {
            fixture.WriteAndPublish(new Vector4(0f, 0f, 0f, 1f));
            TakeTurn(consumer);
        }

        fixture.Renderer.DebugLayerErrorCount.ShouldBe(errorsBefore, fixture.Diagnostics);
    }

    [Fact]
    public void The_bridged_route_and_an_ordinary_srgb_target_encode_a_colour_the_same_way()
    {
        // The `--viewport-compare` claim, and it carries MORE here than on
        // D3D11: over there the shared texture is the thing the frame is written
        // into, and here the frame lands in a private D3D12 target that the
        // bridge copies across. That copy is between an _SRGB-typed resource and
        // a _UNORM one within one format family, so it must be a bit copy and
        // not a conversion - and a conversion would produce a washed-out picture
        // with no error, no HRESULT and nothing on the debug layer.
        Require();

        var linear = new Vector4(0.5f, 0.25f, 0.75f, 1f);
        RenderTarget reference = CreateReferenceTarget();
        try
        {
            fixture.Renderer.ClearForTest(reference, linear);
            fixture.WriteAndPublish(linear);

            ViewportCompare.Reading reading = CompareSharedAgainst(reference);

            reading.MaxDelta.ShouldBeLessThanOrEqualTo(ViewportCompare.Threshold, reading.ToString());
            reading.Passes.ShouldBeTrue(reading.ToString());
        }
        finally
        {
            fixture.Renderer.DestroyRenderTarget(reference);
        }
    }

    [Fact]
    public void A_double_encode_on_the_bridged_route_is_caught_rather_than_absorbed()
    {
        // A gate never seen to fail is not known to work, so the defect is
        // MANUFACTURED: the present target is written the value that has already
        // been through the transfer function once, so its own sRGB view applies
        // it a second time and the bridge faithfully copies the washed-out
        // result across. That is precisely the shape of the failure this whole
        // probe exists for.
        Require();

        var linear = new Vector4(0.5f, 0.25f, 0.75f, 1f);
        Vector3 alreadyEncoded = ColorSpace.LinearToSrgb(new Vector3(linear.X, linear.Y, linear.Z));

        RenderTarget reference = CreateReferenceTarget();
        try
        {
            fixture.Renderer.ClearForTest(reference, linear);
            fixture.WriteAndPublish(new Vector4(alreadyEncoded, 1f));

            ViewportCompare.Reading reading = CompareSharedAgainst(reference);

            reading.Passes.ShouldBeFalse(
                "a transfer function applied twice must not be inside the tolerance: " + reading);
            reading.MaxDelta.ShouldBeGreaterThan(
                ViewportCompare.Threshold * 10,
                "the failure this guards is tens of levels, not a rounding difference: " + reading);
        }
        finally
        {
            fixture.Renderer.DestroyRenderTarget(reference);
        }
    }

    /// <summary>
    /// An ordinary sRGB colour target the size of the shared one: byte for byte
    /// what the window's back buffer holds on this backend.
    /// </summary>
    private RenderTarget CreateReferenceTarget() => fixture.Renderer.CreateRenderTarget(new RenderTargetDesc(
        SharedTargetD3D12Fixture.Width, SharedTargetD3D12Fixture.Height,
        TextureFormat.Rgba8, TextureColorSpace.Srgb, Depth: false));

    /// <summary>
    /// Reads both pictures back and compares them. The shared read goes through
    /// the BRIDGE's texture rather than the present target, which is the only
    /// place the copy can be observed - and it takes the consumer's turn, which
    /// is what every test in this class that publishes owes the ones after it.
    /// </summary>
    private ViewportCompare.Reading CompareSharedAgainst(RenderTarget reference)
    {
        var window = new byte[reference.Width * reference.Height * 4];
        fixture.Renderer.ReadTargetPixels(reference, window);

        var shared = new byte[window.Length];
        fixture.Renderer.TryReadSharedPixels(shared, 1000)
            .ShouldBeTrue("the shared target's key never came back");

        return ViewportCompare.Compare(window, shared);
    }

    /// <summary>
    /// The consumer's half of one frame: acquire key 1, release key 0.
    /// </summary>
    /// <remarks>
    /// Every test that publishes more than once owes this between publishes.
    /// The producer's key comes back only when the consumer hands it over, so a
    /// test that skips it is measuring a skipped write - and leaves the mutex
    /// standing on key 1 for every test that runs after it, which is a whole
    /// class going red for one test's arrangement.
    /// </remarks>
    private static void TakeTurn(ConsumerDevice consumer)
    {
        consumer.Acquire(Renderer.SharedConsumerKey, 1000).ShouldBe(0);
        consumer.Release(Renderer.SharedProducerKey).ShouldBe(0);
    }
}

/// <summary>
/// A <see cref="D3D12Renderer"/> initialized against a composited surface, or a
/// recorded reason why not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Self-skipping rather than failing.</b> A machine with no D3D12 driver is
/// not a broken build, and a suite that goes red on one teaches people to ignore
/// it.
/// </para>
/// <para>
/// <b>But the skip is decided by ONE question asked first, and nothing after it
/// is caught.</b> Wrapping the whole construction in a catch looks like the same
/// thing and is the opposite: the D3D11 fixture did exactly that at first, and a
/// real defect - a null device pointer out of a successful create - was reported
/// as "no device on this machine" on a machine with two. In particular the
/// D3D11On12 bridge is NOT part of the availability question: an 11On12 device
/// that cannot be created on a machine with a working D3D12 one is a defect and
/// is allowed to be a failure.
/// </para>
/// <para>
/// Registered on <see cref="D3DDeviceCollection"/>, beside the D3D11 fixture and
/// not in a collection of its own: see that type for the measured reason.
/// </para>
/// </remarks>
public sealed unsafe class SharedTargetD3D12Fixture : IDisposable
{
    /// <summary>Big enough to be a real target, small enough to cost nothing.</summary>
    public const int Width = 64;

    public const int Height = 48;

    private readonly D3D12Renderer? _renderer;
    private readonly CompositedSurface _surface = new(Width, Height);
    private readonly RecordingLogger _log = new();

    public SharedTargetD3D12Fixture()
    {
        if (!DeviceIsAvailable(out string reason))
        {
            UnavailableReason = reason;
            Available = false;
            return;
        }

        var renderer = new D3D12Renderer(_log, new SpectraShadeCompiler());

        // The engine publishes this from the main thread before the render
        // thread starts, so a renderer that has never been told its size is not
        // a state the engine can be in - and on a composited surface it is the
        // ONLY size there is, since there is no swap chain to ask.
        renderer.SetFramebufferSize(new Vector2D<int>(Width, Height));
        renderer.Initialize(_surface);

        _renderer = renderer;
        Available = true;
        UnavailableReason = string.Empty;
    }

    /// <summary>
    /// Whether this machine can make a D3D12 device at all, asked with a
    /// throwaway one so the answer cannot be confused with anything the engine
    /// does afterwards.
    /// </summary>
    private static bool DeviceIsAvailable(out string reason)
    {
        ID3D12Device* device = null;
        Guid guid = ID3D12Device.Guid;

        int hr = Silk.NET.Direct3D12.D3D12.GetApi().CreateDevice(
            (IUnknown*)null, D3DFeatureLevel.Level110, &guid, (void**)&device);

        if (device is not null) ((IUnknown*)device)->Release();

        reason = hr < 0 || device is null
            ? $"D3D12CreateDevice returned 0x{hr:X8}"
            : string.Empty;
        return reason.Length == 0;
    }

    public bool Available { get; }

    public string UnavailableReason { get; }

    public D3D12Renderer Renderer => _renderer
        ?? throw new InvalidOperationException("No D3D12 device; the test should have skipped.");

    /// <summary>
    /// Everything the renderer logged at warning or above, newest last, so a
    /// failed debug-layer assertion says what the layer actually complained
    /// about.
    /// </summary>
    /// <remarks>
    /// A counter that says "1" and nothing else is the least useful possible
    /// form of this gate: the message names the resource and the state, and
    /// without it the only way to read one is to attach a native debugger.
    /// </remarks>
    public string Diagnostics => _log.Text;

    /// <summary>
    /// Clears the present target to <paramref name="color"/>, publishes it
    /// through the bridge, and ends the frame the way the engine does.
    /// </summary>
    /// <remarks>
    /// The <see cref="Present"/> is not decoration: on a composited surface it
    /// is the only thing that waits on the fence and drains the debug layer, so
    /// leaving it out would make every debug-layer assertion in this class read
    /// a counter nothing had updated.
    /// </remarks>
    public void WriteAndPublish(Vector4 color)
    {
        Renderer.WriteAndPublishForTest(color);
        Present();
    }

    /// <summary>Ends a frame: the fence wait, the ring maintenance and the debug-layer drain.</summary>
    public void Present() => Renderer.Present(_surface);

    /// <summary>
    /// Moves the size latch and pumps one frame's worth of target maintenance,
    /// which is what a host resize does.
    /// </summary>
    public void Resize(int width, int height)
    {
        Renderer.SetFramebufferSize(new Vector2D<int>(width, height));

        // Rendering a frame is how the engine picks a resize up, and a frame
        // needs a scene. The target maintenance is reachable on its own, and
        // driving exactly it is what keeps this a test of the target rather than
        // of the pipelines.
        Renderer.EnsurePresentTargetForTest();
    }

    public void Dispose() => _renderer?.Shutdown();

    /// <summary>
    /// A surface with no window, no handle and no GL context: exactly what an
    /// embedded host that composites the engine's output offers.
    /// </summary>
    private sealed class CompositedSurface(int width, int height) : IRenderSurface
    {
        public RenderSurfaceKind Kind => RenderSurfaceKind.Composited;
        public nint NativeHandle => 0;
        public IGLContext? GLContext => null;
        public Vector2D<int> PixelSize => new(width, height);

        public event Action<Vector2D<int>>? Resized
        {
            add { }
            remove { }
        }
    }

    /// <summary>Keeps every warning and error the renderer logs, so a gate can quote them.</summary>
    private sealed class RecordingLogger : ILogger<Renderer>
    {
        private readonly List<string> _lines = [];

        internal string Text => _lines.Count == 0
            ? "(the renderer logged nothing above Information)"
            : string.Join(Environment.NewLine, _lines);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Information and below are dropped rather than kept: the D3D12 renderer
        // logs a line per shader and per target, and a gate's failure message
        // that has to be scrolled is one nobody reads.
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                _lines.Add($"{logLevel}: {formatter(state, exception)}");
        }
    }
}
