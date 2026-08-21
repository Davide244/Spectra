using Silk.NET.Core.Native;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// The engine's COM reference-counting rules in one place: wrap a freshly
/// created pointer with <see cref="Own"/> (which hands the creation reference
/// over, so exactly one reference is outstanding) and let go of a field with
/// <see cref="Release"/> (which disposes it <em>and clears it</em>).
/// </summary>
/// <remarks>
/// <b>Silk.NET's <c>ComPtr&lt;T&gt;</c> constructor calls AddRef.</b> It has WRL
/// semantics — construction shares a pointer rather than adopting one — so
/// <c>new ComPtr&lt;T&gt;(p)</c> on a pointer that a <c>Create*</c> or
/// <c>QueryInterface</c> call already returned with a reference count of one
/// leaves the object at <b>two</b>. Disposing the <c>ComPtr</c> then drops it to
/// one, not zero, and the object never dies.
/// <para>
/// For most resources that is "only" a leak. For a swap chain's back buffers it
/// is a crash: DXGI refuses <c>ResizeBuffers</c> with
/// <c>DXGI_ERROR_INVALID_CALL</c> while <em>any</em> reference to a back buffer
/// is outstanding ("Swapchain cannot be resized unless all outstanding buffer
/// references have been released"), so every window resize failed on D3D12 and
/// the old code turned that HRESULT straight into a dead render thread. In D3D11
/// the same trap hides one level down: the leaked reference is on the render
/// target <em>view</em>, which holds its own reference to the back buffer.
/// </para>
/// <para>
/// Use <see cref="Own"/> at <b>every</b> site that wraps a pointer it just
/// created — there is no such thing as a resource too small to leak: the D3D11
/// mesh path alone (a vertex buffer, an index buffer and an input layout per
/// mesh, recreated for every chunk the static-world recompile touches) grew the
/// process by 2.2 GB in thirty seconds of the ordinary demo. Reading the raw
/// pointer afterwards stays valid — the returned <c>ComPtr</c> owns the one
/// remaining reference for as long as it lives.
/// </para>
/// <para>
/// The mirror-image rule is <see cref="Release"/>. With one reference instead of
/// two, releasing twice is no longer masked: it is an over-release, and
/// <c>ComPtr&lt;T&gt;.Dispose()</c> does <em>not</em> null the handle it just
/// released. Both renderers can genuinely reach a second release of the same
/// field — <c>ReleaseBackBufferViews</c> runs on the resize path and again at
/// shutdown, a device loss throws out of the resize between the two, and
/// <c>Engine.RenderLoop</c>'s crash handler calls <c>Shutdown</c> a second time
/// when the first one threw — so every field release goes through
/// <see cref="Release"/> and is idempotent.
/// </para>
/// </remarks>
internal static unsafe class ComOwnership
{
    /// <summary>
    /// Takes ownership of <paramref name="raw"/>: wraps it and releases the
    /// caller's reference. A null pointer round-trips as an empty
    /// <see cref="ComPtr{T}"/>.
    /// </summary>
    internal static ComPtr<T> Own<T>(T* raw) where T : unmanaged, IComVtbl<T>
    {
        if (raw is null)
            return default;

        var owned = new ComPtr<T>(raw);
        ((IUnknown*)raw)->Release();
        return owned;
    }

    /// <summary>
    /// Releases <paramref name="field"/>'s reference and clears it, so calling
    /// this again — or disposing the field afterwards — does nothing. An
    /// already-empty field is a no-op.
    /// </summary>
    /// <remarks>
    /// <c>ComPtr&lt;T&gt;.Dispose()</c> leaves the raw handle in place, which
    /// makes a plain <c>field.Dispose()</c> safe only for a field nothing can
    /// reach twice. Renderer fields are not that: see the class remarks.
    /// </remarks>
    internal static void Release<T>(ref ComPtr<T> field) where T : unmanaged, IComVtbl<T>
    {
        if (field.Handle is null)
            return;

        field.Dispose();
        field = default;
    }
}
