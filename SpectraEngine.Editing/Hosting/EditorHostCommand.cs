namespace SpectraEngine.Editing.Hosting;

/// <summary>
/// The editor verbs that belong to the HOST rather than to a manipulator or a
/// camera: history, structural edits, and the two mode toggles.
/// </summary>
/// <remarks>
/// <b>A third command enum, beside <c>GizmoCommand</c> and
/// <c>EditorCameraCommand</c>, because these are the verbs neither of those
/// owns.</b> Undo is not a gizmo concern, a duplicate is not a camera concern,
/// and folding them into either would make that type know about the other two.
/// <para>
/// <b>It exists so a UI can drive the editor without a keyboard.</b> Every one
/// of these was reachable only as a key chord inside
/// <see cref="SceneEditorHost"/>; a shell with a toolbar and a menu needs the
/// same verbs, and synthesising fake key presses to reach them would be a
/// second input path that can drift from the real one.
/// </para>
/// <para>
/// <b>Threading:</b> every verb here mutates the scene, so
/// <see cref="SceneEditorHost.Apply(EditorHostCommand)"/> is render-thread only,
/// like everything else that touches it. A UI thread reaches it through
/// <c>EngineHost.EnqueueCommand</c>.
/// </para>
/// </remarks>
public enum EditorHostCommand
{
    /// <summary>Steps one entry back through the undo history.</summary>
    Undo,

    /// <summary>Steps one entry forward, if nothing has invalidated the redo stack.</summary>
    Redo,

    /// <summary>Copies the selection's roots and selects the copies.</summary>
    Duplicate,

    /// <summary>Removes the selection's roots.</summary>
    Delete,

    /// <summary>Puts the selection's roots under one new parent node.</summary>
    Group,

    /// <summary>Dissolves the selected groups, keeping their children in place.</summary>
    Ungroup,

    /// <summary>
    /// Converts the selected brushes between world geometry and parts. A mixed
    /// selection normalises rather than flipping node by node.
    /// </summary>
    ToggleBrushKind,

    /// <summary>
    /// Swaps between the editor's own freelook camera and the engine's fly
    /// camera.
    /// </summary>
    ToggleNavigation,

    /// <summary>
    /// Selects the root's direct children — everything in the scene, at the
    /// granularity a group move wants.
    /// </summary>
    /// <remarks>
    /// Top-level nodes rather than the whole graph, deliberately: moving them
    /// moves everything anyway, a selection of every descendant would make the
    /// structural verbs' root-filtering do the same reduction the slow way,
    /// and the property union — rebuilt per publish while selected — would
    /// scale with the graph instead of with what the user can see in the tree.
    /// </remarks>
    SelectAll,

    /// <summary>Empties the selection.</summary>
    ClearSelection,

    /// <summary>
    /// Ground grid shows during move and resize gestures only — the default.
    /// </summary>
    /// <remarks>
    /// Three SET verbs rather than a cycle, the same rule every displayed
    /// state follows: a cycle sent against a snapshot one publish stale lands
    /// on the wrong mode exactly when the user clicks fastest.
    /// </remarks>
    GridAuto,

    /// <summary>Ground grid always drawn.</summary>
    GridOn,

    /// <summary>Ground grid never drawn.</summary>
    GridOff,
}
