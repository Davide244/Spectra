using Silk.NET.Core.Native;
using SpectraEngine.Core.Graphics;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// The reference-counting contract the whole D3D resource story rests on,
/// proved against a hand-built COM object rather than a GPU: Silk.NET's
/// <see cref="ComPtr{T}"/> constructor AddRefs, so wrapping a freshly created
/// pointer without handing the creation reference over leaves the object at two
/// references and it never dies.
/// </summary>
/// <remarks>
/// This needs no device and no driver — a COM object is a vtable pointer and a
/// counter, and that is exactly what <see cref="FakeComObject"/> is. The bug
/// this pins down cost 2.2 GB of process memory per 30 seconds on D3D11 (every
/// mesh's vertex and index buffer outlived its <c>Dispose</c>) and turned every
/// D3D12 window resize into <c>DXGI_ERROR_INVALID_CALL</c>.
/// </remarks>
public sealed unsafe class ComOwnershipTests
{
    [Fact]
    public void The_ComPtr_constructor_AddRefs_which_is_the_whole_reason_Own_exists()
    {
        using var obj = new FakeComObject();
        obj.RefCount.ShouldBe(1u, "a freshly created COM object starts at one reference");

        var shared = new ComPtr<IUnknown>(obj.Pointer);
        obj.RefCount.ShouldBe(2u, "ComPtr has WRL semantics: it shares a pointer, it does not adopt one");

        shared.Dispose();
        obj.RefCount.ShouldBe(1u, "disposing the ComPtr only drops the reference it added itself");
    }

    [Fact]
    public void Own_leaves_exactly_one_reference_and_disposing_it_destroys_the_object()
    {
        using var obj = new FakeComObject();

        var owned = ComOwnership.Own(obj.Pointer);
        obj.RefCount.ShouldBe(1u, "Own hands the creation reference over rather than adding to it");
        ((nint)owned.Handle).ShouldBe((nint)obj.Pointer, "the raw pointer stays usable through the owning ComPtr");

        owned.Dispose();
        obj.RefCount.ShouldBe(0u, "the last reference goes, so the GPU resource actually dies");
    }

    [Fact]
    public void A_disposed_ComPtr_keeps_its_handle_which_is_why_Release_exists()
    {
        // This is the trap on the other side of the fix. With two references a
        // second Dispose was harmlessly absorbed; with one it is an
        // over-release of a freed object — and ComPtr does NOT null the handle
        // it just released, so it will happily release it again.
        using var obj = new FakeComObject();

        var owned = ComOwnership.Own(obj.Pointer);
        // Own itself releases once (that is the handover), so count from here.
        uint releasesAfterOwn = obj.ReleaseCount;

        owned.Dispose();
        ((nint)owned.Handle).ShouldNotBe((nint)0, "ComPtr.Dispose leaves the handle in place");

        owned.Dispose();
        (obj.ReleaseCount - releasesAfterOwn)
            .ShouldBe(2u, "the second Dispose really did release the object again");
    }

    [Fact]
    public void Release_clears_the_field_so_releasing_twice_is_a_no_op()
    {
        // Load-bearing for both renderers: ReleaseBackBufferViews runs on the
        // resize path and again at shutdown (a device loss throws between the
        // two), and Engine.RenderLoop's crash handler calls Shutdown a second
        // time when the first one threw.
        using var obj = new FakeComObject();

        var field = ComOwnership.Own(obj.Pointer);
        uint releasesAfterOwn = obj.ReleaseCount;

        ComOwnership.Release(ref field);
        ComOwnership.Release(ref field);
        field.Dispose();

        (obj.ReleaseCount - releasesAfterOwn)
            .ShouldBe(1u, "exactly one release, no matter how many times the field is let go");
        obj.RefCount.ShouldBe(0u);
        ((nint)field.Handle).ShouldBe((nint)0);
    }

    [Fact]
    public void Releasing_an_empty_field_touches_nothing()
    {
        ComPtr<IUnknown> empty = default;
        ComOwnership.Release(ref empty);
        ((nint)empty.Handle).ShouldBe((nint)0);
    }

    [Fact]
    public void A_null_pointer_round_trips_as_an_empty_ComPtr()
    {
        // Every Own site sits under a ThrowHResult, but the QueryInterface
        // probes (info queues) are allowed to come back empty.
        var owned = ComOwnership.Own((IUnknown*)null);
        ((nint)owned.Handle).ShouldBe((nint)0);
    }

    /// <summary>
    /// A minimal COM object: a vtable of three function pointers
    /// (QueryInterface/AddRef/Release) in front of a reference count, allocated
    /// in native memory so a real <see cref="ComPtr{T}"/> can point at it.
    /// Starts at one reference, exactly like a <c>Create*</c> result.
    /// </summary>
    private sealed unsafe class FakeComObject : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Layout
        {
            public void** Vtbl;
            public uint RefCount;
            public uint ReleaseCount;
        }

        private readonly Layout* _object;
        private readonly void** _vtbl;
        private bool _disposed;

        internal FakeComObject()
        {
            _vtbl = (void**)NativeMemory.AllocZeroed(3, (nuint)sizeof(void*));
            _vtbl[0] = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)&QueryInterface;
            _vtbl[1] = (delegate* unmanaged[Stdcall]<void*, uint>)&AddRef;
            _vtbl[2] = (delegate* unmanaged[Stdcall]<void*, uint>)&Release;

            _object = (Layout*)NativeMemory.AllocZeroed((nuint)sizeof(Layout));
            _object->Vtbl = _vtbl;
            _object->RefCount = 1;
        }

        /// <summary>The object as the COM pointer a creation call would have returned.</summary>
        internal IUnknown* Pointer => (IUnknown*)_object;

        /// <summary>Live reference count — zero means the object was released for the last time.</summary>
        internal uint RefCount => _object->RefCount;

        /// <summary>
        /// How many times Release was called, counted separately from
        /// <see cref="RefCount"/> so an over-release of an already-dead object
        /// is visible rather than clamped away.
        /// </summary>
        internal uint ReleaseCount => _object->ReleaseCount;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int QueryInterface(void* self, Guid* riid, void** ppv)
        {
            // Nothing under test queries this fake for another interface;
            // E_NOINTERFACE keeps it honest rather than handing out itself.
            if (ppv is not null) *ppv = null;
            return unchecked((int)0x80004002);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint AddRef(void* self) => ++((Layout*)self)->RefCount;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint Release(void* self)
        {
            // Deliberately does NOT free at zero: the tests read the counters
            // afterwards, and the fixture owns the memory.
            ((Layout*)self)->ReleaseCount++;
            ref uint count = ref ((Layout*)self)->RefCount;
            if (count > 0) count--;
            return count;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            NativeMemory.Free(_object);
            NativeMemory.Free(_vtbl);
        }
    }
}
