using System;

namespace SpectraEngine.Core.Graphics.D3D11;

/// <summary>
/// The last SRV/sampler pair issued to each pixel-shader register, so
/// <see cref="D3D11ShaderProgram.SetTexture"/> can skip re-sending state the
/// context already holds. Materials re-apply the same textures every draw, so
/// on a steady frame most SetTexture calls repeat the previous pointers
/// exactly and the skip removes two context calls per texture per draw.
/// </summary>
/// <remarks>
/// <b>This is CONTEXT state, not program state, and it lives beside the
/// context on purpose.</b> The skip is only sound while the cache agrees with
/// the context's actual register contents, and those are cleared behind every
/// program's back: BeginPass nulls the SRV slots before each offscreen pass
/// and ClearState wipes everything on the resize path. Every such site must
/// call <see cref="Reset"/>, or the skip serves a stale answer and the next
/// pass samples a null SRV. That failure is uniquely silent: D3D11 defines a
/// null SRV read as zeros, so nothing throws, the debug layer says nothing,
/// and the picture is simply wrong. A per-program cache with no reset was
/// exactly this bug, and it shipped while every smoke gate stayed green.
/// </remarks>
internal sealed class D3D11BindCache
{
    /// <summary>
    /// Registers tracked, matching the range UnbindPixelShaderResources
    /// clears. A register outside it is never skipped.
    /// </summary>
    internal const int TrackedSlots = 8;

    private readonly (nint Srv, nint Sampler)[] _slots = new (nint, nint)[TrackedSlots];

    /// <summary>
    /// Records the pair about to be bound to <paramref name="slot"/> and
    /// returns whether the bind must actually be issued. False only when the
    /// context already holds exactly this pair.
    /// </summary>
    public bool MustBind(uint slot, nint srv, nint sampler)
    {
        if (slot >= TrackedSlots)
            return true;

        if (_slots[slot].Srv == srv && _slots[slot].Sampler == sampler)
            return false;

        _slots[slot] = (srv, sampler);
        return true;
    }

    /// <summary>
    /// Forgets every recorded pair, so the next bind of each slot is issued.
    /// Owed by every site that clears the context's shader-resource slots.
    /// </summary>
    public void Reset() => Array.Clear(_slots);
}
