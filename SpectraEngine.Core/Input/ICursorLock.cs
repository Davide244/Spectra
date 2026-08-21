namespace SpectraEngine.Core.Input;

/// <summary>
/// The cursor-mode <em>request</em> seam: a caller on any thread asks for a
/// cursor mode, and the thread that owns the window applies it later. Nothing
/// here touches the window — that is the entire point.
/// </summary>
/// <remarks>
/// <b>Why a latch and not a setter.</b> GLFW cursor calls are window-thread
/// affine, exactly like the window title and the framebuffer size, and this
/// engine has already been bitten by violating that (see the title latch in
/// <c>Engine.Run</c> and the framebuffer latch in <c>Renderer</c>). The editor
/// camera that wants the cursor locked runs on the <em>render</em> thread, so it
/// cannot set the mode itself. It calls
/// <see cref="RequestCursorMode(CursorMode)"/>; the main thread applies the
/// pending request during its event pump; and the request only becomes fact when
/// <see cref="CursorMode"/> reports it back.
/// <para>
/// <b>Lock semantics.</b> Entering <see cref="Input.CursorMode.Locked"/> hides
/// the cursor and remembers where it was; leaving it puts the cursor back there.
/// While locked the OS reports no meaningful absolute position, so
/// <see cref="IsCursorLocked"/> is the flag that tells every absolute-position
/// consumer — picking, gizmo hit-testing, the marquee — to stand down and read
/// relative motion instead.
/// </para>
/// <para>
/// <b>Threading:</b> <see cref="RequestCursorMode(CursorMode)"/>,
/// <see cref="CursorMode"/> and <see cref="IsCursorLocked"/> are safe from any
/// thread. Applying the request is not, and is not part of this interface.
/// </para>
/// </remarks>
public interface ICursorLock
{
    /// <summary>
    /// Asks for <paramref name="mode"/>. Returns immediately; the mode takes
    /// effect on a later pass of whichever thread owns the window. Requesting
    /// the mode that is already applied is free and idempotent.
    /// </summary>
    void RequestCursorMode(CursorMode mode);

    /// <summary>
    /// The mode that has actually been applied to the window — not the one last
    /// requested. A caller that needs to know whether its lock has landed reads
    /// this.
    /// </summary>
    CursorMode CursorMode { get; }

    /// <summary>
    /// True while <see cref="CursorMode"/> is
    /// <see cref="Input.CursorMode.Locked"/>: absolute cursor position is
    /// meaningless and only relative motion may be consumed.
    /// </summary>
    bool IsCursorLocked { get; }
}
