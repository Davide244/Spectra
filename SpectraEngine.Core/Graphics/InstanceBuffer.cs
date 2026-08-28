using System;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// A renderer-owned buffer of per-instance vertex data, rewritten each frame and
/// consumed by <see cref="Mesh.DrawInstanced"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Engine-owned and reused, never one per batch.</b> A batch is discovered
/// during the view build and drawn microseconds later, so allocating a GPU
/// buffer per batch would replace N cheap draws with N expensive allocations,
/// which is the trade the D3D12 mesh pool exists to avoid one level down. One
/// buffer that grows to the largest batch the frame contains costs one
/// allocation in the whole run.
/// </para>
/// <para>
/// <b>Growth is the caller's decision, because it is a GPU allocation.</b>
/// <see cref="Capacity"/> is stated rather than silently expanded on
/// <see cref="Update"/>: a buffer quietly reallocating mid-frame is a stall
/// nothing reports, and on D3D12 it is a resource the open command list may
/// still reference. <c>EnsureCapacity</c> on the renderer is where growth
/// happens, between frames.
/// </para>
/// </remarks>
public abstract class InstanceBuffer : IDisposable
{
    /// <summary>How many instances this buffer can hold.</summary>
    public int Capacity { get; protected set; }

    /// <summary>Floats per instance, fixed at creation by the layout.</summary>
    public int FloatsPerInstance { get; protected set; }

    /// <summary>
    /// Replaces the buffer's contents with <paramref name="data"/>, which must
    /// hold <c>instanceCount * FloatsPerInstance</c> floats.
    /// </summary>
    /// <remarks>
    /// Render thread only, and before the draws that read it. The data is
    /// discarded and rewritten rather than appended to, because a batch's
    /// matrices are rebuilt from the view every frame and there is nothing in
    /// the previous frame's contents worth keeping.
    /// </remarks>
    public abstract void Update(ReadOnlySpan<float> data, int instanceCount);

    /// <inheritdoc/>
    public abstract void Dispose();

    /// <summary>
    /// Throws if <paramref name="data"/> does not describe exactly
    /// <paramref name="instanceCount"/> instances, or if the count exceeds
    /// <see cref="Capacity"/>. Shared by every backend's <see cref="Update"/>.
    /// </summary>
    /// <remarks>
    /// Checked rather than clamped. A short span silently drawn as N instances
    /// reads past the data the caller supplied, and an over-capacity count
    /// draws instances from whatever the buffer held before: both render
    /// something, neither reports anything.
    /// </remarks>
    protected void ValidateUpdate(ReadOnlySpan<float> data, int instanceCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(instanceCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(instanceCount, Capacity);

        int expected = instanceCount * FloatsPerInstance;
        if (data.Length != expected)
        {
            throw new ArgumentException(
                $"Expected {expected} floats for {instanceCount} instance(s) " +
                $"of {FloatsPerInstance} floats each, got {data.Length}.",
                nameof(data));
        }
    }
}
