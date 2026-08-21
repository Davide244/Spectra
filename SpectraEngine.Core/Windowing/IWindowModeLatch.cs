namespace SpectraEngine.Core.Windowing;

/// <summary>
/// The window-mode <em>request</em> seam: a caller on any thread asks to be
/// windowed or borderless-fullscreen, and the thread that owns the window
/// applies it later. Nothing here touches the window — that is the entire
/// point, and it is the same shape as
/// <see cref="Input.ICursorLock"/>.
/// </summary>
/// <remarks>
/// <b>Why a latch and not a setter.</b> Window calls are window-thread affine
/// (GLFW gives no thread-safety guarantee at all), exactly like the window
/// title, the framebuffer size and the cursor mode, each of which this engine
/// already latches. The F11 handler — and any future editor menu item — runs on
/// the <em>render</em> thread, so it cannot resize or undecorate the window
/// itself.
/// <para>
/// <b>Why the engine owns fullscreen at all.</b> Left to itself, DXGI handles
/// Alt+Enter inside the window procedure and performs its own
/// <c>SetFullscreenState</c> transition there — on the main thread, while the
/// render thread is presenting and resizing the very same swap chain. That
/// unsynchronised transition is what made <c>ResizeBuffers</c> return
/// <c>DXGI_ERROR_INVALID_CALL</c> and take the render thread down. Both D3D
/// backends now call <c>MakeWindowAssociation(..., DXGI_MWA_NO_ALT_ENTER)</c>
/// so DXGI never does that again, and fullscreen becomes what this latch
/// does: ordinary window-state work, which is why the OpenGL backend gets the
/// same toggle without a line of backend code.
/// </para>
/// <para>
/// <b>Threading:</b> every member here is safe from any thread. Applying the
/// request is not, and is deliberately not part of this interface.
/// </para>
/// </remarks>
public interface IWindowModeLatch
{
    /// <summary>
    /// Asks for <paramref name="mode"/>. Returns immediately; the mode takes
    /// effect on a later pass of whichever thread owns the window. Requesting
    /// the mode that is already applied is free and idempotent.
    /// </summary>
    void RequestWindowMode(WindowMode mode);

    /// <summary>
    /// Flips the <em>requested</em> mode — so two toggles between two applies
    /// cancel out, which is what a user hammering F11 means.
    /// </summary>
    void ToggleFullscreen();

    /// <summary>
    /// The mode actually applied to the window — not the one last requested.
    /// A caller that needs to know whether its request landed reads this.
    /// </summary>
    WindowMode WindowMode { get; }

    /// <summary>The mode last asked for, whether or not it has been applied yet.</summary>
    WindowMode RequestedWindowMode { get; }
}
