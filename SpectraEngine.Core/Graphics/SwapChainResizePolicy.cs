using Silk.NET.Maths;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// The decision half of both D3D backends' resize path, pulled out as a pure
/// function so the two cannot drift apart on it and so the state machine can be
/// tested without a GPU.
/// </summary>
/// <remarks>
/// The <em>doing</em> half — releasing views, calling <c>ResizeBuffers</c>,
/// rebuilding views — stays in the backends, because that is all it is. What is
/// easy to get subtly wrong, and what actually crashed, is deciding <b>whether
/// to touch the swap chain at all</b> on a given frame.
/// </remarks>
internal static class SwapChainResizePolicy
{
    /// <summary>
    /// Whether this frame should resize the swap chain.
    /// </summary>
    /// <param name="requested">
    /// The engine's framebuffer-size latch, read exactly once by the caller —
    /// the size the resize will be performed at, even if the main thread
    /// publishes another one mid-call.
    /// </param>
    /// <param name="current">The size the swap chain's buffers actually have.</param>
    /// <param name="lastFailed">
    /// The size a previous <c>ResizeBuffers</c> refused, if any. Retrying it
    /// every frame would re-fail every frame and drown the log; the caller
    /// clears it on the next success, so a size that failed once is retried
    /// after any other size has landed.
    /// </param>
    /// <param name="swapChainAlive">False before creation and after teardown.</param>
    /// <param name="deviceLost">
    /// True once the device is gone. Nothing may be asked of it after that; the
    /// run is already ending on its own diagnosis.
    /// </param>
    /// <remarks>
    /// <b>The degenerate case is a skip, not a clamp.</b> A minimised window
    /// reports a 0×0 framebuffer, <c>ResizeBuffers</c> rejects a zero extent,
    /// and there is nothing on screen to present to. Skipping leaves both the
    /// buffers and the recorded size alone, so restoring to the pre-minimise
    /// size is a no-op and restoring to a different one resizes exactly once.
    /// </remarks>
    internal static bool ShouldResize(
        Vector2D<int> requested,
        Vector2D<int> current,
        Vector2D<int>? lastFailed,
        bool swapChainAlive,
        bool deviceLost)
    {
        if (requested == current) return false;
        if (!swapChainAlive || deviceLost) return false;
        if (requested.X <= 0 || requested.Y <= 0) return false;
        if (lastFailed == requested) return false;
        return true;
    }
}
