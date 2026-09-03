using System;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// Somebody else's device holding the other end of the shared target's keyed
/// mutex: it acquires <see cref="Renderer.SharedConsumerKey"/>, snapshots the
/// texture and hands <see cref="Renderer.SharedProducerKey"/> back.
/// </summary>
/// <remarks>
/// <para>
/// <b>A SECOND device is the whole point, and one thread is not a substitute.</b>
/// A keyed mutex is owned per DEVICE, not per thread: a device that already
/// holds the mutex is refused with <c>DXGI_ERROR_INVALID_CALL</c> rather than
/// made to wait, so a consumer running on the renderer's own device cannot
/// overlap the producer's frame at all - which is exactly the overlap being
/// measured. Measured, not reasoned about: the first version of
/// <see cref="SharedPacingProbe"/> took its turn on the renderer's device from
/// its own thread and reported that error several times a second.
/// </para>
/// <para>
/// <b>Internal, because nothing in a game or a shell is on this side.</b> A real
/// consumer is a compositor with its own device and its own import; this exists
/// so a run with no compositor can BE one, which is what makes the composited
/// viewport's pacing measurable without a window, a driver import and a person
/// with a mouse. It is the asynchronous sibling of
/// <see cref="Renderer.TakeSharedConsumerTurn"/>, which stands in for a
/// consumer from the render thread and therefore never overlaps a frame.
/// </para>
/// </remarks>
internal interface ISharedTargetConsumer : IDisposable
{
    /// <summary>
    /// Takes the consumer's turn: acquire, snapshot, release. False means the
    /// turn never arrived within <paramref name="timeoutMs"/>, or the acquire
    /// failed.
    /// </summary>
    /// <remarks>
    /// A timeout here means the PRODUCER has not released key 1 - it is not
    /// rendering, or it is still inside its frame - and is not an error for the
    /// same reason a timeout on the producer's side is not one.
    /// </remarks>
    bool TakeTurn(int timeoutMs);
}
