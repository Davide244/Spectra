using SpectraEngine.Core.Graphics;

namespace SpectraEngine.Core.Scene;

/// <summary>
/// The seam through which the engine runs an editing layer it deliberately
/// cannot reference: one per-frame update, one overlay draw, and the handful of
/// counters the host's periodic stats line reports.
/// </summary>
/// <remarks>
/// <b>Why an interface at all.</b> <c>SpectraEngine.Editing</c> references Core,
/// so Core cannot reference it back — that dependency direction is the whole
/// point of the editing layer being its own assembly (gizmo, undo and tool code
/// must never end up inside a shipped AOT game binary). The engine loop still
/// has to drive the editor once per frame, so it drives it through this
/// interface and the host — the executable, which is free to reference both —
/// supplies the implementation via
/// <see cref="SceneManager.EditorFactory"/>.
/// <para>
/// <b>Nothing here names an editing type.</b> The counters are plain numbers and
/// a mode label rather than a <c>GizmoMode</c>, so adding a tool never touches
/// Core. <see cref="GizmoModeName"/> must return an interned constant, not a
/// freshly formatted string: the host reads it from a logging path that is
/// otherwise allocation-free.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only, like the <see cref="Scene"/> it edits
/// and the <see cref="DebugDraw"/> it fills.
/// </para>
/// </remarks>
public interface ISceneEditor
{
    /// <summary>
    /// Advances the editor by one frame: snapshots input, runs the viewport's
    /// tools, and drives the editor camera.
    /// </summary>
    /// <param name="deltaTime">The frame's duration in seconds.</param>
    /// <returns>
    /// True when the editor drove the viewport camera this frame, so the host
    /// leaves its own camera controller parked. False hands navigation back —
    /// which is what makes an editor-camera/fly-camera toggle a one-line
    /// decision in the host loop instead of a mode flag threaded through the
    /// engine.
    /// </returns>
    bool Update(double deltaTime);

    /// <summary>
    /// Pushes the editor's overlay — manipulator handles, marquee — into this
    /// frame's debug line buffer. Called after the buffer has been cleared and
    /// before the draw list is built.
    /// </summary>
    void Draw(DebugDraw output);

    /// <summary>How many nodes are selected.</summary>
    int SelectionCount { get; }

    /// <summary>
    /// A stable, allocation-free label for the live manipulator ("Translate",
    /// "Rotate", "Resize").
    /// </summary>
    string GizmoModeName { get; }

    /// <summary>
    /// A stable, allocation-free label for the navigation model currently
    /// driving the viewport camera ("freelook", "fly camera", …).
    /// </summary>
    /// <remarks>
    /// It is in the periodic stats line for the same reason the gizmo mode is:
    /// which camera is driving is invisible to a headless smoke run, and it is
    /// the difference between "the editor navigation works" and "the editor
    /// navigation was never switched on". Interned constants only — the stats
    /// line is otherwise allocation-free.
    /// </remarks>
    string NavigationModeName { get; }

    /// <summary>How many edits can currently be undone.</summary>
    int UndoDepth { get; }

    /// <summary>How many undone edits can currently be redone.</summary>
    int RedoDepth { get; }
}
