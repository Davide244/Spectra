namespace SpectraEngine.Editing.Viewport;

/// <summary>
/// What a press in the viewport turned out to mean. Exactly one of these owns
/// the pointer from the press until the release, which is the whole point: box
/// select, gizmo manipulation and object dragging must never be live at the
/// same time.
/// </summary>
/// <remarks>
/// <b>The rule, in one line: what is under the cursor at the moment of the
/// press decides, and nothing after that changes its mind.</b> A gizmo handle
/// manipulates; empty space box-selects; anything else selects and moves. That
/// ordering is the arbitration — handles win over the objects they are drawn on
/// top of, because the handle is deliberately in front of the object, and
/// objects win over empty space because a marquee that started on a solid is
/// not what anyone meant.
/// </remarks>
public enum ViewportDragMode
{
    /// <summary>Nothing has claimed the pointer. Camera navigation is free to run.</summary>
    None,

    /// <summary>
    /// The press landed on a gizmo handle; the manipulator owns the gesture.
    /// </summary>
    Manipulate,

    /// <summary>
    /// The press landed on empty space; a marquee is being dragged and will
    /// change the selection on release.
    /// </summary>
    BoxSelect,

    /// <summary>
    /// The press landed on an object: the selection changed on the press, and
    /// the object is now following the cursor through the move tool's free-move
    /// handle. A gesture that ends without moving collapses back to a plain
    /// click-select.
    /// </summary>
    SelectAndMove,
}
