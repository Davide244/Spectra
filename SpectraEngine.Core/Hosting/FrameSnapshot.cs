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
    /// The active tool, as the editor names it (for example <c>move/Studio</c>),
    /// or null when the host installed no editor.
    /// </summary>
    public string? GizmoModeName { get; init; }

    /// <summary>Which camera is driving, as the editor names it, or null when there is no editor.</summary>
    public string? NavigationModeName { get; init; }

    /// <summary>How many edits can be undone.</summary>
    public int UndoDepth { get; init; }

    /// <summary>How many undone edits can be redone.</summary>
    public int RedoDepth { get; init; }

    /// <summary>How many static-world compiles have landed for the active scene.</summary>
    public int StaticWorldCompileCount { get; init; }

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
