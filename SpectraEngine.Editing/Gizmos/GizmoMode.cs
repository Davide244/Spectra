namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// Which manipulator the viewport is currently offering: move, rotate, or
/// resize. Exactly one is live at a time — see <see cref="GizmoController"/>.
/// </summary>
public enum GizmoMode
{
    /// <summary>The move tool (<see cref="TranslateGizmo"/>).</summary>
    Translate,

    /// <summary>The rotate tool (<see cref="RotateGizmo"/>).</summary>
    Rotate,

    /// <summary>The resize tool (<see cref="ScaleGizmo"/>).</summary>
    Scale,
}

/// <summary>
/// Which frame a gizmo lays its handles out in — the world axes, or the
/// selection's own.
/// </summary>
/// <remarks>
/// Translate and rotate honour both. <see cref="ScaleGizmo"/> deliberately does
/// not offer the choice: a non-uniform scale is only meaningful along the axes
/// of the thing being scaled, and applying world-axis factors to a rotated
/// object is a shear, not a resize. See the remarks on <see cref="ScaleGizmo"/>.
/// </remarks>
public enum GizmoOrientation
{
    /// <summary>
    /// Handles lie along the world axes, whatever the selection's rotation. The
    /// default, and what grid snapping is expressed in.
    /// </summary>
    World,

    /// <summary>
    /// Handles lie along the reference node's own axes — the last-selected
    /// node's world rotation.
    /// </summary>
    Local,
}

/// <summary>
/// A verb the viewport can hand a <see cref="GizmoController"/>: the actions a
/// keyboard shortcut, a toolbar button, or a menu item ultimately means.
/// </summary>
/// <remarks>
/// <b>This exists so the editing layer never learns a keyboard vocabulary.</b>
/// <c>EditorInputFrame</c> carries buttons and modifiers but no keys, precisely
/// so re-hosting the viewport is a swap of the input adapter; a gizmo that
/// switch-cased on a key enum would break that seam (and would have to name a
/// Silk.NET type to do it). The host owns the keymap, resolves a keypress to one
/// of these, and calls <see cref="GizmoController.Apply"/>.
/// <see cref="GizmoShortcuts"/> carries the recommended default bindings so
/// every host agrees without the enum having to know what a key is.
/// </remarks>
public enum GizmoCommand
{
    /// <summary>Switch to the move tool.</summary>
    UseTranslate,

    /// <summary>Switch to the rotate tool.</summary>
    UseRotate,

    /// <summary>Switch to the resize tool.</summary>
    UseScale,

    /// <summary>Advance to the next mode, wrapping — for a single cycling key.</summary>
    CycleMode,

    /// <summary>Flip between world- and local-aligned handles.</summary>
    ToggleOrientation,

    /// <summary>
    /// Flip between the two built-in manipulator styles, Studio and Classic.
    /// See <see cref="GizmoStyle"/>.
    /// </summary>
    ToggleStyle,

    /// <summary>Turn snapping on or off for the tools that snap.</summary>
    ToggleSnap,

    // The Use*/Enable*/Disable* verbs below are the toggles' idempotent
    // halves, for controls that name a state rather than a flip: a dropdown, a
    // segmented pair, a checkbox. A toggle sent against a snapshot one publish
    // stale flips the wrong way exactly when the user clicks fastest; a verb
    // that names its target state cannot. Keyboard chords keep the toggles.

    /// <summary>Lay the handles along the world axes. Idempotent.</summary>
    UseWorldOrientation,

    /// <summary>Lay the handles along the reference node's own axes. Idempotent.</summary>
    UseLocalOrientation,

    /// <summary>Wear the Studio manipulator style. Idempotent. See <see cref="GizmoStyle"/>.</summary>
    UseStudioStyle,

    /// <summary>Wear the Classic manipulator style. Idempotent. See <see cref="GizmoStyle"/>.</summary>
    UseClassicStyle,

    /// <summary>Turn snapping on. Idempotent.</summary>
    EnableSnap,

    /// <summary>Turn snapping off. Idempotent.</summary>
    DisableSnap,

    /// <summary>Step every snap increment one rung finer.</summary>
    FinerSnap,

    /// <summary>Step every snap increment one rung coarser.</summary>
    CoarserSnap,

    /// <summary>Abort an in-progress drag, restoring the selection exactly.</summary>
    Cancel,
}
