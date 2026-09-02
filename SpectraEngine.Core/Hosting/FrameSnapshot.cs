using SpectraEngine.Core.Inspection;
using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;

namespace SpectraEngine.Core.Hosting;

/// <summary>
/// Everything a UI thread is allowed to know about a frame the engine has
/// finished: values only, no live objects, safe to hold for as long as it likes.
/// </summary>
/// <remarks>
/// <b>Immutable because the alternative is a race on every property read.</b>
/// The render thread owns the scene, the selection and the renderer, and starts
/// changing them again the instant the frame ends. A UI binding that read
/// through to any of them would be reading a moving object from the wrong
/// thread; copying the handful of values a panel actually shows costs a small
/// allocation at the publish rate and removes the whole class of problem.
/// <para>
/// <b>The UI is eventually consistent, and this is where that starts.</b> A
/// snapshot describes a frame that is already over. An inspector shows its own
/// local edit immediately and reconciles when the next snapshot confirms it;
/// treating a snapshot as the current truth would make every text box fight the
/// engine.
/// </para>
/// <para>
/// <b>Selection is ids, not nodes</b>, for the same reason
/// <see cref="SceneChange"/> is: a <c>SceneNode</c> cannot leave the render
/// thread. A shell that wants more than an id asks through
/// <see cref="EngineHost.EnqueueCommand"/>.
/// </para>
/// </remarks>
public sealed class FrameSnapshot
{
    /// <summary>The empty snapshot, for a host that has not seen a frame yet.</summary>
    public static FrameSnapshot Empty { get; } = new();

    /// <summary>How many frames the engine has completed, counted from one.</summary>
    public long FrameNumber { get; init; }

    /// <summary>The smoothed frame time in milliseconds, as the engine reports it.</summary>
    public double FrameTimeMs { get; init; }

    /// <summary>The smoothed frame rate.</summary>
    public double Fps { get; init; }

    /// <summary>The ids of every currently selected node, in selection order.</summary>
    public IReadOnlyList<Guid> SelectedIds { get; init; } = Array.Empty<Guid>();

    /// <summary>
    /// The active tool, as the editor names it (<c>move</c>, <c>rotate</c>,
    /// <c>resize</c>), or null when the host installed no editor.
    /// </summary>
    public string? GizmoModeName { get; init; }

    /// <summary>The manipulator's handle style (<c>Studio</c>, <c>Classic</c>), or null.</summary>
    public string? GizmoStyleName { get; init; }

    /// <summary>The axis frame drags resolve against (<c>world</c>, <c>local</c>), or null.</summary>
    public string? GizmoOrientationName { get; init; }

    /// <summary>Whether the live manipulator quantises its drags.</summary>
    public bool SnapEnabled { get; init; }

    /// <summary>
    /// The live manipulator's snap increment, in world units for move and
    /// resize and in degrees for rotate. Which unit applies is decided by
    /// <see cref="GizmoModeName"/>, so a UI showing one must show the other.
    /// </summary>
    public float SnapIncrement { get; init; }

    /// <summary>
    /// The move tool's snap increment in world units, whichever tool is live.
    /// </summary>
    /// <remarks>
    /// All three per-tool increments ride every snapshot, unlike
    /// <see cref="SnapIncrement"/>, which reports only the live tool's. A
    /// command surface shows the move grid and the rotate angle side by side —
    /// Studio's own top bar does — and a UI that could only see the live tool's
    /// value would have to switch tools to read the other one.
    /// </remarks>
    public float MoveSnapIncrement { get; init; }

    /// <summary>The rotate tool's snap increment in degrees. See <see cref="MoveSnapIncrement"/>.</summary>
    public float RotateSnapIncrement { get; init; }

    /// <summary>The resize tool's snap increment in world units. See <see cref="MoveSnapIncrement"/>.</summary>
    public float ResizeSnapIncrement { get; init; }

    /// <summary>Which camera is driving, as the editor names it, or null when there is no editor.</summary>
    public string? NavigationModeName { get; init; }

    /// <summary>
    /// When the ground grid shows — "auto" (during move and resize gestures),
    /// "on", or "off" — or null when there is no editor. Reported so the View
    /// menu's checkmark follows the editor rather than its own last click.
    /// </summary>
    public string? GridModeName { get; init; }

    /// <summary>
    /// Whether play mode is active: the character has the camera and the cursor,
    /// and the editor is suspended.
    /// </summary>
    public bool IsPlaying { get; init; }

    /// <summary>
    /// Whether play mode can be entered at all — the engine built a character
    /// over the active scene. False until the scene has loaded, so a Play
    /// button can disable itself instead of silently doing nothing.
    /// </summary>
    public bool CanPlay { get; init; }

    /// <summary>The debug visualisations currently drawn over the scene.</summary>
    /// <remarks>
    /// On the snapshot because the F1–F5 keys flip the same flags: a View menu
    /// that tracked only its own clicks would drift from the keyboard the first
    /// time somebody pressed one, and a checkbox that cannot show the real
    /// state is worse than no checkbox.
    /// </remarks>
    public DebugVisualization DebugFlags { get; init; }

    /// <summary>The rendering pipeline currently drawing the scene, or null before the renderer reported one.</summary>
    public string? PipelineName { get; init; }

    /// <summary>
    /// Every pipeline the running backend registered, in registration order.
    /// The set is fixed for the renderer's life, so the same list instance
    /// rides every snapshot and costs nothing to carry.
    /// </summary>
    public IReadOnlyList<string> PipelineNames { get; init; } = Array.Empty<string>();

    /// <summary>How many edits can be undone.</summary>
    public int UndoDepth { get; init; }

    /// <summary>How many undone edits can be redone.</summary>
    public int RedoDepth { get; init; }

    /// <summary>How many static-world compiles have landed for the active scene.</summary>
    public int StaticWorldCompileCount { get; init; }

    /// <summary>
    /// Why the static world stopped recompiling, or null when it is current.
    /// </summary>
    /// <remarks>
    /// A shell that does not show this leaves the user editing a level that has
    /// silently stopped rebuilding. See <c>Scene.StaticWorldDefect</c>.
    /// </remarks>
    public string? StaticWorldDefect { get; init; }

    /// <summary>
    /// The selection's editable properties, merged across every selected node.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Values, like everything else here.</b> A row carries no
    /// <c>SceneNode</c>, no <c>Brush</c> and no asset handle, because a UI
    /// holding one of those would be holding something the render thread
    /// mutates the instant the frame ends.
    /// </para>
    /// <para>
    /// <b>On the snapshot rather than fetched on demand, and that is what makes
    /// a panel follow a gizmo drag.</b> A drag moves the node every frame, and a
    /// panel that only refreshed when the selection changed would sit there
    /// showing the position the object had before it was picked up. The cost is
    /// a dozen struct rows per publish, at the snapshot's own ~30 Hz rather than
    /// per frame.
    /// </para>
    /// </remarks>
    public IReadOnlyList<PropertyRow> SelectionProperties { get; init; } = Array.Empty<PropertyRow>();

    /// <summary>
    /// The structural changes since the previous snapshot, in the order they
    /// happened. Empty on a frame where nothing moved in the graph, which is
    /// most of them.
    /// </summary>
    public IReadOnlyList<SceneChange> Changes { get; init; } = Array.Empty<SceneChange>();

    /// <summary>
    /// True when more changes happened than the log could hold, so
    /// <see cref="Changes"/> is incomplete and a view must rebuild rather than
    /// replay. Also set for the first snapshot after a scene swap.
    /// </summary>
    /// <remarks>
    /// Reported rather than hidden, because a tree view fed a partial log looks
    /// correct and is wrong, which is the failure this engine's standing rule
    /// about silent degradation exists to prevent.
    /// </remarks>
    public bool ChangesOverflowed { get; init; }
}
