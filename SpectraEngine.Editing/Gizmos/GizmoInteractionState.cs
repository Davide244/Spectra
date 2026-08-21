namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// Where a manipulator is in its interaction cycle: idle → hovering → dragging,
/// and back to idle when the drag commits or cancels.
/// </summary>
public enum GizmoInteractionState
{
    /// <summary>
    /// Nothing selected, or the cursor is nowhere near a handle. The gizmo may
    /// still be drawn — it just is not offering anything.
    /// </summary>
    Idle,

    /// <summary>
    /// The cursor is over a handle, which is drawn highlighted. A press of the
    /// drag button here starts a drag.
    /// </summary>
    Hovering,

    /// <summary>
    /// A handle is held and the selection is following the cursor along its
    /// constraint. An undo transaction is open for the whole of this state.
    /// </summary>
    Dragging,
}

/// <summary>
/// What one <see cref="GizmoTool.Update"/> call did — the frame-by-frame
/// narration of the state machine, for a host that wants to drive a status bar,
/// a cursor shape, or a test.
/// </summary>
public enum GizmoUpdateResult
{
    /// <summary>Nothing happened: no selection, or the cursor is off the gizmo.</summary>
    None,

    /// <summary>The cursor is over a handle and no drag started this frame.</summary>
    Hovering,

    /// <summary>A drag started this frame; the undo transaction is now open.</summary>
    DragBegan,

    /// <summary>A drag is in progress and the selection moved (or held still) this frame.</summary>
    DragUpdated,

    /// <summary>
    /// The drag ended this frame and its movement landed in the undo history as
    /// a single entry. A drag that finished exactly where it started commits
    /// nothing and reports <see cref="DragCancelled"/> instead.
    /// </summary>
    DragCommitted,

    /// <summary>
    /// The drag ended this frame and every node was restored to the transform
    /// it had at the grab; nothing landed in the undo history.
    /// </summary>
    DragCancelled,
}
