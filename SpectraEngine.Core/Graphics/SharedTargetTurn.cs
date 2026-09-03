using Microsoft.Extensions.Logging;
using Silk.NET.DXGI;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// Hands a retired shared target's keyed mutex to the consumer, so a turn still
/// queued against it can be taken.
/// </summary>
/// <remarks>
/// <para>
/// <b>One expression of the offer, because both D3D backends owe it and they
/// differ only in where the mutex comes from.</b> On D3D11 it is the retired
/// present target's own texture; on D3D12 it is the retired bridge surface's.
/// What the two do with it is identical, and two copies of a keyed-mutex
/// bracket is exactly the kind of thing that gets corrected in one of them.
/// </para>
/// <para>
/// Why a retired target needs answering at all is on
/// <see cref="SharedTargetRetirement.OfferTurns"/>. Render thread only, like
/// every other keyed-mutex call in the engine.
/// </para>
/// </remarks>
internal static unsafe class SharedTargetTurn
{
    /// <summary>
    /// <c>WAIT_TIMEOUT</c>, which is a SUCCESS-coded HRESULT: <c>hr &lt; 0</c>
    /// reads it as an acquisition. Every acquire in the engine has to test it
    /// by value.
    /// </summary>
    private const int WaitTimeout = 0x00000102;

    /// <summary><c>WAIT_ABANDONED</c>: the previous holder went away holding it.</summary>
    private const int WaitAbandoned = 0x00000080;

    /// <summary>
    /// Takes <paramref name="mutex"/> if it is free and immediately hands it to
    /// the consumer. Does nothing at all when it is not free.
    /// </summary>
    /// <remarks>
    /// <b>Timeout zero, deliberately.</b> This runs once per frame per retired
    /// generation on the render thread, and the case it is for is rare: waiting
    /// even a millisecond here would spend a frame's budget answering a turn
    /// that in the ordinary case does not exist. Not free means the consumer
    /// already holds the key, which is the state this is trying to produce.
    /// </remarks>
    internal static void Offer(IDXGIKeyedMutex* mutex, ILogger logger, int generation)
    {
        if (mutex is null) return;

        int hr = mutex->AcquireSync(Renderer.SharedProducerKey, 0u);

        // The ordinary outcome once the offer has been taken up: the consumer
        // is holding the key, which is precisely what was wanted.
        if (hr == WaitTimeout) return;

        if (hr == WaitAbandoned)
        {
            logger.LogDebug(
                "Retired shared target generation {Generation} had an abandoned key; answering it anyway.",
                generation);
        }
        else if (hr < 0)
        {
            // Debug rather than a warning: the target is retired, so the only
            // consequence of not answering is that a consumer still queued
            // against it waits, and the next frame offers again.
            logger.LogDebug(
                "Could not take retired shared target generation {Generation}'s key to offer it: 0x{Hr:X8}.",
                generation, hr);
            return;
        }

        hr = mutex->ReleaseSync(Renderer.SharedConsumerKey);
        if (hr < 0)
        {
            logger.LogWarning(
                "Could not hand retired shared target generation {Generation}'s key to the consumer: " +
                "0x{Hr:X8}. A turn queued against it will not complete.",
                generation, hr);
        }
    }
}
