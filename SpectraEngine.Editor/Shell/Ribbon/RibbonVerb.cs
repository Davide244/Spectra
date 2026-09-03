using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Cameras;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Hosting;

namespace SpectraEngine.Editor.Shell.Ribbon;

/// <summary>
/// Which family of existing verb a ribbon control resolves to.
/// </summary>
public enum RibbonVerbKind
{
    /// <summary>Nothing. Never valid on a roster item.</summary>
    None,

    /// <summary><see cref="EditorHostCommand"/>.</summary>
    Host,

    /// <summary><see cref="GizmoCommand"/>.</summary>
    Gizmo,

    /// <summary><see cref="EditorCameraCommand"/>.</summary>
    Camera,

    /// <summary><see cref="InsertKind"/>, through <c>SceneEditorHost.Insert</c>.</summary>
    Insert,

    /// <summary>
    /// One <see cref="DebugVisualization"/> flag, through
    /// <c>EngineHost.RequestDebugVisualization</c>.
    /// </summary>
    Debug,

    /// <summary>
    /// A two-way choice whose target verb depends on the state it is in. See
    /// <see cref="RibbonToggle"/>.
    /// </summary>
    Toggle,

    /// <summary>
    /// The snap increment field, through <c>SceneEditorHost.SetSnapIncrement</c>.
    /// The one ribbon control whose verb carries a NUMBER rather than naming a
    /// state, which is exactly why it is a field and not a button.
    /// </summary>
    SnapIncrement,
}

/// <summary>
/// A two-way choice the ribbon offers. Each resolves to one of a PAIR of
/// existing idempotent verbs, chosen from the state the shell is displaying.
/// </summary>
/// <remarks>
/// <b>A pair of set verbs, never a toggle verb.</b> A toggle sent against a
/// snapshot one publish stale flips the wrong way exactly when the user clicks
/// fastest; a verb that names its target state cannot. The keyboard keeps the
/// toggles, which is right, because a key press carries no displayed state to
/// disagree with.
/// </remarks>
public enum RibbonToggle
{
    /// <summary>World or local drag axes.</summary>
    Axes,

    /// <summary>Studio or Classic manipulator handles.</summary>
    Handles,

    /// <summary>Snapping on or off.</summary>
    Snap,
}

/// <summary>
/// Exactly one existing editor verb, named by a ribbon control.
/// </summary>
/// <remarks>
/// <para>
/// <b>A closed union rather than a delegate or a string, because the roster is
/// the thing the tests read.</b> The defect that killed the previous tab strip -
/// two tabs carrying the same verbs - is only mechanically checkable if a verb
/// is a VALUE that compares equal to itself across tabs, so this is a record
/// struct and <c>RibbonLayoutTests</c> compares it. A click handler carrying a
/// lambda would make the same defect invisible again.
/// </para>
/// <para>
/// <b>Nothing here is a new verb.</b> Every case names a member of an enum that
/// already existed and already had a keyboard route and a menu route; the
/// ribbon is a third route onto the same <c>SceneEditorHost.Apply</c> surface,
/// never a second command path. That is the property <c>ROADMAP.md</c>'s H4
/// bullet asks to protect.
/// </para>
/// </remarks>
public readonly record struct RibbonVerb(
    RibbonVerbKind Kind,
    EditorHostCommand Host,
    GizmoCommand Gizmo,
    EditorCameraCommand Camera,
    InsertKind Insert,
    DebugVisualization Debug,
    RibbonToggle Toggle)
{
    /// <summary>A host verb: history, a structural edit, a grid mode.</summary>
    public static RibbonVerb Of(EditorHostCommand command) =>
        new(RibbonVerbKind.Host, command, default, default, default, default, default);

    /// <summary>A manipulator verb.</summary>
    public static RibbonVerb Of(GizmoCommand command) =>
        new(RibbonVerbKind.Gizmo, default, command, default, default, default, default);

    /// <summary>A camera verb.</summary>
    public static RibbonVerb Of(EditorCameraCommand command) =>
        new(RibbonVerbKind.Camera, default, default, command, default, default, default);

    /// <summary>An insert.</summary>
    public static RibbonVerb Of(InsertKind kind) =>
        new(RibbonVerbKind.Insert, default, default, default, kind, default, default);

    /// <summary>One debug overlay flag.</summary>
    public static RibbonVerb Of(DebugVisualization flag) =>
        new(RibbonVerbKind.Debug, default, default, default, default, flag, default);

    /// <summary>A two-way choice.</summary>
    public static RibbonVerb Of(RibbonToggle toggle) =>
        new(RibbonVerbKind.Toggle, default, default, default, default, default, toggle);

    /// <summary>The snap increment field.</summary>
    public static RibbonVerb SnapIncrement() =>
        new(RibbonVerbKind.SnapIncrement, default, default, default, default, default, default);
}
