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
    /// Instances written since the last <see cref="BeginFrame"/>.
    /// </summary>
    public int Cursor { get; protected set; }

    /// <summary>How many more instances fit before the buffer is full.</summary>
    public int Remaining => Capacity - Cursor;

    /// <summary>
    /// Starts a frame's writes, discarding what the buffer held.
    /// </summary>
    /// <remarks>
    /// <b>Once per frame, not once per pass.</b> A pass that writes more than
    /// once per frame must APPEND, because on D3D12 the whole frame is a single
    /// command list submitted at the end: a second write at the same offset
    /// retroactively changes what every draw already recorded will read. That is
    /// exactly the bug the shadow pass shipped with, where four cascades each
    /// wrote at zero and the first three ended up drawing the fourth's
    /// transforms. D3D11 renames on <c>WriteDiscard</c> and GL orphans, so both
    /// were correct and only D3D12 was wrong, silently.
    /// </remarks>
    public void BeginFrame()
    {
        Cursor = 0;
        OnBeginFrame();
    }

    /// <summary>Backend hook for <see cref="BeginFrame"/>; discards prior contents where that is how the API renames.</summary>
    protected virtual void OnBeginFrame() { }

    /// <summary>
    /// Appends <paramref name="instanceCount"/> instances and returns the index
    /// of the first, for use as a draw's <c>firstInstance</c>.
    /// </summary>
    /// <remarks>
    /// Render thread only, and before the draws that read the range. Callers
    /// must check <see cref="Remaining"/>: an append past capacity throws rather
    /// than wrapping, because a wrap would silently draw one pass's geometry
    /// with another's transforms.
    /// </remarks>
    public abstract int Append(ReadOnlySpan<float> data, int instanceCount);

    /// <summary>
    /// Replaces the buffer's contents with <paramref name="data"/>: the
    /// single-write case, equivalent to <see cref="BeginFrame"/> then
    /// <see cref="Append"/>.
    /// </summary>
    public void Update(ReadOnlySpan<float> data, int instanceCount)
    {
        BeginFrame();
        Append(data, instanceCount);
    }

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
        ArgumentOutOfRangeException.ThrowIfGreaterThan(instanceCount, Remaining);

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
