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
    /// Abandons whatever gesture is in progress and gives up any input capture,
    /// because the host is about to stop calling <see cref="Update"/>.
    /// </summary>
    /// <remarks>
    /// <b>Not the same as simply not being updated.</b> An editor that is mid
    /// gesture holds real state a stalled frame loop cannot resolve: an open
    /// undo transaction that will never be committed, a manipulator holding a
    /// drag capture, and — worst — a cursor lock its camera requested and only
    /// its own update would release. Play mode taking the frame away without
    /// this leaves an edit half-applied in the history and, if the editor camera
    /// was looking at the time, two subsystems asking the window for opposite
    /// cursor modes on alternating frames.
    /// <para>
    /// Idempotent, and free when nothing is in progress.
    /// </para>
    /// </remarks>
    void Suspend();

    /// <summary>
    /// Hands the frame back: the host is about to resume calling
    /// <see cref="Update"/>.
    /// </summary>
    /// <remarks>
    /// <b>The pair matters because a UI's view of play mode is stale.</b> A
    /// shell gates its own editing surfaces on a snapshot up to a publish
    /// interval old, so a click landing in that window enqueues an edit that
    /// arrives at a suspended editor and is applied to a scene the player is
    /// standing in. The editor knowing it is suspended is what turns that into
    /// a logged refusal instead; the alternative, trusting every caller to
    /// have fresh state, is a race nobody can close from the outside.
    /// <para>
    /// Idempotent, like <see cref="Suspend"/>.
    /// </para>
    /// </remarks>
    void Resume();

    /// <summary>
    /// Pushes the editor's overlay — manipulator handles, marquee — into this
    /// frame's debug line buffer. Called after the buffer has been cleared and
    /// before the draw list is built.
    /// </summary>
    void Draw(DebugDraw output);

    /// <summary>
    /// Pushes the editor's WORLD overlay - the ground grid, the origin - into
    /// this frame's depth-tested line buffer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A second lane, because the two kinds of editor line want opposite
    /// depth rules.</b> <see cref="Draw"/>'s output is chrome: handles and a
    /// selection outline that must never be hidden by the geometry they
    /// describe, or a handle you can see is a handle you cannot pick. What goes
    /// here is world content - a grid at y = 0 that must be occluded by the
    /// floor it lies under, because a grid drawn through the walls of a room is
    /// not a grid, it is a fault.
    /// </para>
    /// <para>
    /// These lines go through the tone curve and <see cref="Draw"/>'s do not,
    /// which follows from the same distinction: a grid is lit scene content and
    /// should dim as exposure rises, while a handle is a display colour. Colours
    /// pushed here are therefore authored in LINEAR light.
    /// </para>
    /// </remarks>
    void DrawWorld(DebugDraw output);

    /// <summary>How many nodes are selected.</summary>
    int SelectionCount { get; }

    /// <summary>
    /// A stable, allocation-free label for the live manipulator ("move",
    /// "rotate", "resize").
    /// </summary>
    /// <remarks>
    /// <b>The tool alone, with the handle style reported separately</b> by
    /// <see cref="GizmoStyleName"/>. They used to be one combined label, which
    /// reads fine in a log line and is useless to a toolbar: three buttons need
    /// to know which one is lit, and splitting a string to find out is a
    /// contract nobody wrote down. Callers that want the old text compose the
    /// two, which is what the periodic stats line does.
    /// </remarks>
    string GizmoModeName { get; }

    /// <summary>
    /// A stable, allocation-free label for the manipulator's handle style
    /// ("Studio", "Classic").
    /// </summary>
    /// <remarks>
    /// Worth reporting on its own because the two styles disagree about what a
    /// resize holds still and about how many handles exist, so "resize" without
    /// it does not say what the next drag will do.
    /// </remarks>
    string GizmoStyleName { get; }

    /// <summary>
    /// A stable, allocation-free label for the axis frame drags resolve against
    /// ("world", "local").
    /// </summary>
    string GizmoOrientationName { get; }

    /// <summary>Whether the live manipulator quantises its drags.</summary>
    bool SnapEnabled { get; }

    /// <summary>
    /// The live manipulator's snap increment, in whatever unit it edits: world
    /// units for move and resize, degrees for rotate.
    /// </summary>
    /// <remarks>
    /// The unit differs per tool on purpose (all three snaps are absolute
    /// quantities of the thing being edited, never a multiplier), so a UI
    /// showing this must show which tool is live beside it.
    /// </remarks>
    float SnapIncrement { get; }

    /// <summary>The move tool's snap increment in world units, whichever tool is live.</summary>
    /// <remarks>
    /// All three tools' increments are exposed by name, beside the live-tool
    /// value above, because a command surface shows the move grid and the
    /// rotate angle side by side and cannot switch tools to read them.
    /// </remarks>
    float MoveSnapIncrement { get; }

    /// <summary>The rotate tool's snap increment in degrees, whichever tool is live.</summary>
    float RotateSnapIncrement { get; }

    /// <summary>The resize tool's snap increment in world units, whichever tool is live.</summary>
    float ResizeSnapIncrement { get; }

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

    /// <summary>
    /// When the ground grid shows, as an interned literal: <c>"auto"</c>
    /// (during move and resize gestures — the default), <c>"on"</c>, or
    /// <c>"off"</c>.
    /// </summary>
    /// <remarks>
    /// Reported back like every other state a control displays, so a menu's
    /// checkmark follows the editor rather than its own last click.
    /// </remarks>
    string GridModeName { get; }

    /// <summary>
    /// True while a gesture is in flight: a manipulator drag, a marquee, or a
    /// value being scrubbed in a property panel.
    /// </summary>
    /// <remarks>
    /// <b>The host publishes snapshots faster while this is true</b>
    /// (<see cref="Hosting.EngineHost.InteractiveSnapshotInterval"/>), because a
    /// drag is the one case where the resting publish rate is visibly wrong: the
    /// object moves at the frame rate and every number describing it steps at
    /// thirty a second, which reads as the panel being broken.
    /// <para>
    /// Deliberately NOT "the user is doing something" - a camera is excluded,
    /// because navigating changes nothing any panel displays, and paying four
    /// times the snapshot rate to say so would be the cost with none of the
    /// benefit.
    /// </para>
    /// </remarks>
    bool IsInteracting { get; }

    /// <summary>How many edits can currently be undone.</summary>
    int UndoDepth { get; }

    /// <summary>How many undone edits can currently be redone.</summary>
    int RedoDepth { get; }
}
