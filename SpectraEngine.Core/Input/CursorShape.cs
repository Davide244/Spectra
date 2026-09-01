namespace SpectraEngine.Core.Input;

/// <summary>
/// What the pointer LOOKS like, in the engine's own backend-neutral vocabulary
/// - the same reason <see cref="CursorMode"/>, <see cref="PointerButtons"/> and
/// <see cref="KeyModifiers"/> exist rather than a windowing backend's enums
/// being passed around.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate from <see cref="CursorMode"/>, because they answer different
/// questions.</b> Mode is whether the pointer is visible, hidden or captured;
/// shape is what it means. A freelook wants Locked and has no shape at all; a
/// gizmo handle wants Arrow-visible and a Grab shape. Collapsing them would make
/// "show a resize cursor" and "capture the mouse" one setting.
/// </para>
/// <para>
/// <b>Deliberately small and closed.</b> Every value here exists because
/// something in the editor genuinely needs it, and every one maps to a stock OS
/// cursor - except <see cref="Rotate"/>, which has no <c>IDC_</c> constant on
/// Windows and degrades to <see cref="SizeAll"/>. <b>That degradation lives in
/// the backend, never in the editing layer</b>: a tool asking for a rotate
/// cursor and getting a move cursor is a platform limitation, and a tool that
/// had to know about it would be naming a platform.
/// </para>
/// </remarks>
public enum CursorShape
{
    /// <summary>The ordinary pointer. The resting state.</summary>
    Arrow,

    /// <summary>Precision, for a marquee.</summary>
    Crosshair,

    /// <summary>Something here can be picked up.</summary>
    Grab,

    /// <summary>Something is being dragged right now.</summary>
    Grabbing,

    /// <summary>Sizing left and right.</summary>
    SizeWestEast,

    /// <summary>Sizing up and down.</summary>
    SizeNorthSouth,

    /// <summary>Sizing along the top-left to bottom-right diagonal.</summary>
    SizeNorthWestSouthEast,

    /// <summary>Sizing along the top-right to bottom-left diagonal.</summary>
    SizeNorthEastSouthWest,

    /// <summary>Moving in every direction: a pan, an orbit, a free move.</summary>
    SizeAll,

    /// <summary>Turning. Degrades to <see cref="SizeAll"/> where the OS has no such cursor.</summary>
    Rotate,

    /// <summary>This gesture is refused - the editor is suspended.</summary>
    No,
}

/// <summary>
/// The cursor-SHAPE request seam, in the exact shape of
/// <see cref="ICursorLock"/>: a caller on any thread asks, and the thread that
/// owns the window applies it later.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is a latch and not a setter</b> is the same answer the mode latch
/// gives: cursor calls belong to the thread that owns the window, and the tools
/// that know what shape is wanted run on the render thread. It is also what
/// lets an EMBEDDED host work at all - a shell whose viewport is a native child
/// applies the shape from its own window procedure, and one whose viewport is a
/// composited surface applies it from the framework's; neither is something the
/// editing layer could do or should know about.
/// </para>
/// <para>
/// <b>Threading:</b> both members are safe from any thread. Applying the
/// request is not, and is deliberately not part of this interface.
/// </para>
/// </remarks>
public interface ICursorShape
{
    /// <summary>
    /// Asks for <paramref name="shape"/>. Last write wins; requesting the shape
    /// already applied is free.
    /// </summary>
    void RequestCursorShape(CursorShape shape);

    /// <summary>
    /// The shape most recently requested. A host reads this when the OS asks it
    /// what the cursor should be.
    /// </summary>
    /// <remarks>
    /// The REQUEST rather than what has been applied, unlike
    /// <see cref="ICursorLock.CursorMode"/>, and the difference is deliberate: a
    /// mode is a state transition somebody may need to wait on, while a shape is
    /// re-asserted by the OS on every mouse move and the only useful answer is
    /// the newest one.
    /// </remarks>
    CursorShape CursorShape { get; }
}
