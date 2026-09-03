using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Silk.NET.Maths;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.D3D11;
using SpectraShade.Compiler;
using System;
using System.Diagnostics;
using System.Numerics;
using Xunit;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// The D3D11 shared present target, against a real driver: the NT handle, the
/// keyed mutex, and the view format that stops the picture being encoded twice.
/// </summary>
/// <remarks>
/// <para>
/// <b>No compositor anywhere in here, deliberately.</b> The thing that gates
/// replacing the editor viewport's native child is whether a texture this engine
/// rendered can be imported and synchronised by another device at all, and that
/// question is answered by a second plain D3D11 device in this process. Bringing
/// a UI framework in would test the framework as well and would make every
/// failure ambiguous between the two.
/// </para>
/// <para>
/// <b>Every failure here is silent by construction</b>, which is why they are
/// asserted with bytes rather than reasoned about. A keyed-mutex resource
/// touched without its key completes with <c>S_OK</c> and writes nothing. An
/// <c>AcquireSync</c> timeout is a SUCCESS-coded HRESULT. A resource shared as
/// sRGB rather than UNORM produces a washed-out picture and no diagnostic. None
/// of the three raises anything on the debug layer.
/// </para>
/// </remarks>
[Collection(SharedTargetD3D11Collection.Name)]
public sealed unsafe class SharedTargetD3D11Tests(SharedTargetD3D11Fixture fixture)
{
    private void Require() => Assert.SkipWhen(
        !fixture.Available,
        $"no usable D3D11 device in this process: {fixture.UnavailableReason}");

    [Fact]
    public void A_composited_surface_brings_a_device_up_with_no_swap_chain()
    {
        // The split's own assertion. A surface the engine does not present to
        // still needs a device, an immediate context, the base shaders and every
        // pipeline; only the chain and the back-buffer views drop out.
        Require();

        fixture.Renderer.CurrentPipelineName.ShouldNotBe("None");
        fixture.Renderer.DebugLayerErrorCount.ShouldBe(0);
    }

    [Fact]
    public void A_composited_surface_hands_out_an_nt_handle_with_its_size_and_generation()
    {
        Require();

        fixture.Renderer.TryGetSharedHandle(out Renderer.SharedTargetHandle handle).ShouldBeTrue();

        handle.NtHandle.ShouldNotBe(0);
        handle.Width.ShouldBe(SharedTargetD3D11Fixture.Width);
        handle.Height.ShouldBe(SharedTargetD3D11Fixture.Height);
        handle.Generation.ShouldBeGreaterThan(0, "zero is reserved for no target at all");
    }

    [Fact]
    public void The_handle_opens_on_a_second_device_that_knows_nothing_about_this_renderer()
    {
        // The whole point of a shared handle, and the one claim no amount of
        // reading the creation flags can establish.
        Require();
        fixture.Renderer.TryGetSharedHandle(out Renderer.SharedTargetHandle handle).ShouldBeTrue();

        using var consumer = new ConsumerDevice(handle.NtHandle);

        consumer.Width.ShouldBe((uint)handle.Width);
        consumer.Height.ShouldBe((uint)handle.Height);
    }

    [Fact]
    public void The_imported_resource_is_unorm_so_the_engines_srgb_write_is_not_encoded_twice()
    {
        // The defence against a washed-out picture, asserted on the side that
        // matters: what an outside importer SEES is the resource's format, and
        // the sRGB encode belongs on this side's render-target view alone. If
        // the resource were sRGB-typed, a consumer that decodes on sample would
        // decode a value the engine already encoded, and nothing anywhere would
        // report it.
        Require();
        fixture.Renderer.TryGetSharedHandle(out Renderer.SharedTargetHandle handle).ShouldBeTrue();

        using var consumer = new ConsumerDevice(handle.NtHandle);

        consumer.Format.ShouldBe(
            Format.FormatR8G8B8A8Unorm,
            "the shared RESOURCE must not be sRGB-typed; only the render-target view over it is");
    }

    [Fact]
    public void The_producer_and_the_consumer_take_turns_on_keys_zero_and_one()
    {
        // The numbers are pure convention with nothing in the API to enforce
        // them, so a side that picked the other pair deadlocks on its second
        // frame with nothing reporting a disagreement. Both halves run here so
        // the convention is pinned rather than described.
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
        // A consumer that is not being drawn never acquires and never releases,
        // and that is a steady state rather than an event: a render thread that
        // waited for it would stall the whole engine on a turn that may never
        // come. It has to come back false, and quickly.
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
    public void A_clear_written_under_the_key_arrives_on_the_other_device_encoded_once()
    {
        // Two claims in one readback, because they are only true together. The
        // bytes arriving at all is the handle and the mutex working; the value
        // they arrive as is the view format. Linear 0.5 encodes to sRGB 0.7354,
        // which is 188 of 255 - so 128 would mean the render-target view never
        // encoded, and the compositor would show a picture too dark.
        Require();
        fixture.Renderer.TryGetSharedHandle(out Renderer.SharedTargetHandle handle).ShouldBeTrue();

        using var consumer = new ConsumerDevice(handle.NtHandle);

        int errorsBefore = fixture.Renderer.DebugLayerErrorCount;

        fixture.Renderer.BeginSharedWrite(1000).ShouldBeTrue();
        try
        {
            fixture.Renderer.BeginPass(
                fixture.Renderer.PresentTargetForTest,
                PassClear.To(new Vector4(0.5f, 0f, 1f, 1f)));
            fixture.Renderer.EndPass();
        }
        finally
        {
            fixture.Renderer.EndSharedWrite();
        }

        (byte r, byte g, byte b, byte a) = consumer.ReadFirstPixel();

        r.ShouldBeInRange((byte)184, (byte)192, "128 would mean the sRGB render-target view never encoded at all");
        g.ShouldBe((byte)0);
        b.ShouldBe((byte)255);
        a.ShouldBe((byte)255);

        // The other half of what a shared write can get wrong renders a picture
        // and reports nothing, so the layer is the only witness there is on a
        // surface with no swap chain and no offscreen probe.
        fixture.Renderer.DebugLayerErrorCount.ShouldBe(errorsBefore);
    }

    [Fact]
    public void Writing_without_the_key_silently_writes_nothing()
    {
        // The measurement the write bracket exists for, kept as a test because
        // it is the single most confusing thing about this API: the clear
        // succeeds, the debug layer says nothing, and the texture does not
        // change. Anyone who moves the bracket to cover the draws alone gets
        // exactly this, and would have no other way to find out.
        Require();
        fixture.Renderer.TryGetSharedHandle(out Renderer.SharedTargetHandle handle).ShouldBeTrue();

        using var consumer = new ConsumerDevice(handle.NtHandle);

        // A known state, written properly.
        fixture.Renderer.BeginSharedWrite(1000).ShouldBeTrue();
        try
        {
            fixture.Renderer.BeginPass(fixture.Renderer.PresentTargetForTest, PassClear.To(new Vector4(0f, 1f, 0f, 1f)));
            fixture.Renderer.EndPass();
        }
        finally
        {
            fixture.Renderer.EndSharedWrite();
        }

        // The same clear again with a different colour and no key at all.
        int errorsBefore = fixture.Renderer.DebugLayerErrorCount;
        fixture.Renderer.BeginPass(fixture.Renderer.PresentTargetForTest, PassClear.To(new Vector4(1f, 0f, 0f, 1f)));
        fixture.Renderer.EndPass();

        (byte r, byte g, byte b, _) = consumer.ReadFirstPixel();

        g.ShouldBe((byte)255, "the keyless clear must not have landed");
        r.ShouldBe((byte)0);
        b.ShouldBe((byte)0);
        fixture.Renderer.DebugLayerErrorCount.ShouldBe(errorsBefore, "and it raises nothing, which is the whole problem");
    }

    [Fact]
    public void A_shared_target_refuses_to_be_resized_in_place()
    {
        // Every other target in the engine swaps its GPU resource inside the
        // wrapper so materials sampling it survive. A shared one cannot: the
        // consumer imported the handle, and a handle is not swappable. It is
        // recreated under a new generation instead.
        Require();

        RenderTarget target = fixture.Renderer.PresentTargetForTest.ShouldNotBeNull();

        Should.Throw<InvalidOperationException>(() => target.Resize(target.Width + 16, target.Height + 16))
            .Message.ShouldContain("generation");
    }

    [Fact]
    public void A_resize_mints_a_new_generation_and_a_new_handle()
    {
        // What the consumer's re-import is keyed on. The old handle staying
        // valid is not enough and is not the point: it names a resource that is
        // being retired, and the generation is the only thing that says so.
        Require();

        fixture.Renderer.TryGetSharedHandle(out Renderer.SharedTargetHandle before).ShouldBeTrue();
        try
        {
            fixture.Resize(SharedTargetD3D11Fixture.Width + 32, SharedTargetD3D11Fixture.Height + 16);

            fixture.Renderer.TryGetSharedHandle(out Renderer.SharedTargetHandle after).ShouldBeTrue();

            after.Generation.ShouldBeGreaterThan(before.Generation);
            after.NtHandle.ShouldNotBe(before.NtHandle);
            after.Width.ShouldBe(SharedTargetD3D11Fixture.Width + 32);
            after.Height.ShouldBe(SharedTargetD3D11Fixture.Height + 16);

            // The retired one is held rather than freed until the consumer says
            // it is done, so the acknowledgement must be accepted and must not
            // disturb the live generation.
            Should.NotThrow(() => fixture.Renderer.NotifySharedTargetReleased(before.Generation));
            fixture.Renderer.TryGetSharedHandle(out Renderer.SharedTargetHandle still).ShouldBeTrue();
            still.ShouldBe(after);
        }
        finally
        {
            // Back to the size every other test in this class expects.
            fixture.Resize(SharedTargetD3D11Fixture.Width, SharedTargetD3D11Fixture.Height);
            fixture.Renderer.TryGetSharedHandle(out Renderer.SharedTargetHandle restored);
            fixture.Renderer.NotifySharedTargetReleased(restored.Generation - 1);
        }

        fixture.Renderer.DebugLayerErrorCount.ShouldBe(0);
    }
}

/// <summary>Serialises every test class in this assembly that brings up a D3D11 device.</summary>
/// <remarks>
/// <para>
/// <b>Two reasons, and the second one was measured the hard way.</b> The
/// shared-target tests take turns on a single keyed mutex, so running two of
/// them at once would have each measuring the other's turn. And <b>two classes
/// acquiring Silk.NET's D3D11 and D3DCompiler APIs concurrently race</b>: the
/// full suite intermittently produced a <c>D3D11CreateDevice</c> that reported
/// success and left the device pointer null, which surfaced as a
/// <c>NullReferenceException</c> from the next <c>QueryInterface</c>. It
/// reproduced only with the whole assembly running in parallel, never with the
/// two classes paired, and never with <c>-parallel none</c>.
/// </para>
/// <para>
/// Same remedy <see cref="GlRendererCollection"/> already uses for the GL
/// context, and the same reason: one collection is what stops two classes
/// driving one process-global thing at once.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class SharedTargetD3D11Collection : ICollectionFixture<SharedTargetD3D11Fixture>
{
    public const string Name = "D3D11 device";
}

/// <summary>
/// A <see cref="D3D11Renderer"/> initialized against a composited surface, or a
/// recorded reason why not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Self-skipping rather than failing.</b> A machine with no D3D11 driver is
/// not a broken build, and a suite that goes red on one teaches people to ignore
/// it.
/// </para>
/// <para>
/// <b>But the skip is decided by ONE question asked first, and nothing after it
/// is caught.</b> Wrapping the whole construction in a catch looks like the same
/// thing and is the opposite: the first version of this file did exactly that,
/// and a real defect - a null device pointer out of a successful
/// <c>D3D11CreateDevice</c> - was reported as "no D3D11 device on this machine"
/// on a machine with two. A blanket catch turns every future backend bug into a
/// green run with ten skips in it. So availability is measured with a throwaway
/// device, and past that point a failure is a defect and is allowed to be one.
/// </para>
/// </remarks>
public sealed unsafe class SharedTargetD3D11Fixture : IDisposable
{
    /// <summary>Big enough to be a real target, small enough to cost nothing.</summary>
    public const int Width = 64;

    public const int Height = 48;

    private readonly D3D11Renderer? _renderer;

    public SharedTargetD3D11Fixture()
    {
        if (!DeviceIsAvailable(out string reason))
        {
            UnavailableReason = reason;
            Available = false;
            return;
        }

        var renderer = new D3D11Renderer(NullLogger<Renderer>.Instance, new SpectraShadeCompiler());

        // The engine publishes this from the main thread before the render
        // thread starts, so a renderer that has never been told its size is
        // not a state the engine can be in - and on a composited surface it
        // is the ONLY size there is, since there is no swap chain to ask.
        renderer.SetFramebufferSize(new Vector2D<int>(Width, Height));
        renderer.Initialize(new CompositedSurface(Width, Height));

        _renderer = renderer;
        Available = true;
        UnavailableReason = string.Empty;
    }

    /// <summary>
    /// Whether this machine can make a D3D11 device at all, asked with a
    /// throwaway one so the answer cannot be confused with anything the engine
    /// does afterwards.
    /// </summary>
    private static bool DeviceIsAvailable(out string reason)
    {
        ID3D11Device* device = null;
        ID3D11DeviceContext* context = null;

        int hr = Silk.NET.Direct3D11.D3D11.GetApi(null).CreateDevice(
            (IDXGIAdapter*)null, D3DDriverType.Hardware, (nint)0, 0u,
            (D3DFeatureLevel*)null, 0u, Silk.NET.Direct3D11.D3D11.SdkVersion,
            &device, null, &context);

        if (context is not null) context->Release();
        if (device is not null) device->Release();

        reason = hr < 0 || device is null
            ? $"D3D11CreateDevice returned 0x{hr:X8}"
            : string.Empty;
        return reason.Length == 0;
    }

    public bool Available { get; }

    public string UnavailableReason { get; }

    public D3D11Renderer Renderer => _renderer
        ?? throw new InvalidOperationException("No D3D11 device; the test should have skipped.");

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
}

/// <summary>
/// A second, plain D3D11 device that imports the engine's handle and takes its
/// turn on the keyed mutex: the compositor's side of the contract, with no
/// compositor.
/// </summary>
internal sealed unsafe class ConsumerDevice : IDisposable
{
    private readonly Silk.NET.Direct3D11.D3D11 _api = Silk.NET.Direct3D11.D3D11.GetApi();
    private ComPtr<ID3D11Device> _device;
    private ComPtr<ID3D11DeviceContext> _context;
    private ComPtr<ID3D11Texture2D> _opened;
    private ComPtr<IDXGIKeyedMutex> _mutex;

    internal ConsumerDevice(nint sharedHandle)
    {
        D3DFeatureLevel* levels = stackalloc D3DFeatureLevel[1] { D3DFeatureLevel.Level110 };
        D3DFeatureLevel chosen = default;
        ID3D11Device* device = null;
        ID3D11DeviceContext* context = null;

        // The system default adapter, which is the one the renderer took too. A
        // shared handle only opens on the adapter that created it, so a
        // mismatched pick here would measure a cross-adapter refusal and report
        // it as a broken handle.
        SilkMarshal.ThrowHResult(_api.CreateDevice(
            (IDXGIAdapter*)null, D3DDriverType.Hardware, 0, (uint)CreateDeviceFlag.BgraSupport,
            levels, 1, Silk.NET.Direct3D11.D3D11.SdkVersion, &device, &chosen, &context));
        _device = Own(device);
        _context = Own(context);

        // OpenSharedResource1, not OpenSharedResource: the NT-handle form is a
        // different entry point on a different interface, and the legacy one
        // takes a global handle this texture does not have.
        ID3D11Device1* device1 = null;
        Guid device1Guid = ID3D11Device1.Guid;
        SilkMarshal.ThrowHResult(((ID3D11Device*)_device.Handle)->QueryInterface(&device1Guid, (void**)&device1));
        ComPtr<ID3D11Device1> asDevice1 = Own(device1);
        try
        {
            ID3D11Texture2D* opened = null;
            Guid textureGuid = ID3D11Texture2D.Guid;
            SilkMarshal.ThrowHResult(((ID3D11Device1*)asDevice1.Handle)->OpenSharedResource1(
                (void*)sharedHandle, &textureGuid, (void**)&opened));
            _opened = Own(opened);
        }
        finally
        {
            Release(ref asDevice1);
        }

        Texture2DDesc desc = default;
        ((ID3D11Texture2D*)_opened.Handle)->GetDesc(&desc);
        Width = desc.Width;
        Height = desc.Height;
        Format = desc.Format;

        IDXGIKeyedMutex* mutex = null;
        Guid mutexGuid = IDXGIKeyedMutex.Guid;
        SilkMarshal.ThrowHResult(((ID3D11Texture2D*)_opened.Handle)->QueryInterface(&mutexGuid, (void**)&mutex));
        _mutex = Own(mutex);
    }

    internal uint Width { get; }

    internal uint Height { get; }

    /// <summary>The format an importer sees, which is the resource's own and never a view's.</summary>
    internal Format Format { get; }

    internal int Acquire(ulong key, uint timeoutMs) =>
        ((IDXGIKeyedMutex*)_mutex.Handle)->AcquireSync(key, timeoutMs);

    internal int Release(ulong key) => ((IDXGIKeyedMutex*)_mutex.Handle)->ReleaseSync(key);

    /// <summary>
    /// Takes its turn, copies the imported texture into a staging surface on
    /// THIS device, and reads texel (0, 0).
    /// </summary>
    /// <remarks>
    /// A staging copy rather than a map of the shared texture itself: a shared
    /// resource is created on the default heap and cannot be mapped, and the
    /// copy is also what proves the consumer's own device can read it rather
    /// than merely hold a pointer to it.
    /// </remarks>
    internal (byte R, byte G, byte B, byte A) ReadFirstPixel()
    {
        SilkMarshal.ThrowHResult(Acquire(Renderer.SharedConsumerKey, 1000));
        try
        {
            var desc = new Texture2DDesc
            {
                Width = Width,
                Height = Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format,
                SampleDesc = new SampleDesc(1, 0),
                Usage = Usage.Staging,
                BindFlags = 0,
                CPUAccessFlags = (uint)CpuAccessFlag.Read,
                MiscFlags = 0,
            };

            ID3D11Texture2D* staging = null;
            SilkMarshal.ThrowHResult(((ID3D11Device*)_device.Handle)->CreateTexture2D(&desc, null, &staging));
            ComPtr<ID3D11Texture2D> owned = Own(staging);
            try
            {
                var ctx = (ID3D11DeviceContext*)_context.Handle;
                ctx->CopyResource((ID3D11Resource*)owned.Handle, (ID3D11Resource*)_opened.Handle);

                MappedSubresource mapped = default;
                SilkMarshal.ThrowHResult(ctx->Map((ID3D11Resource*)owned.Handle, 0, Map.Read, 0, &mapped));
                byte* p = (byte*)mapped.PData;
                var pixel = (p[0], p[1], p[2], p[3]);
                ctx->Unmap((ID3D11Resource*)owned.Handle, 0);
                return pixel;
            }
            finally
            {
                Release(ref owned);
            }
        }
        finally
        {
            SilkMarshal.ThrowHResult(Release(Renderer.SharedProducerKey));
        }
    }

    public void Dispose()
    {
        Release(ref _mutex);
        Release(ref _opened);
        Release(ref _context);
        Release(ref _device);
    }

    // The engine's ComOwnership rule, restated because that type is internal to
    // Core's graphics layer and this is a test's own COM. Silk's ComPtr
    // constructor AddRefs rather than adopting, so wrapping a pointer a Create*
    // or QueryInterface call already returned at one leaves it at two.
    private static ComPtr<T> Own<T>(T* raw) where T : unmanaged, IComVtbl<T>
    {
        if (raw is null) return default;
        var owned = new ComPtr<T>(raw);
        ((IUnknown*)raw)->Release();
        return owned;
    }

    private static void Release<T>(ref ComPtr<T> field) where T : unmanaged, IComVtbl<T>
    {
        if (field.Handle is null) return;
        field.Dispose();
        field = default;
    }
}
