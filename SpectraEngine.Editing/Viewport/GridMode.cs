namespace SpectraEngine.Editing.Viewport;

/// <summary>
/// When the ground grid shows.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Auto"/> is the default, and it is what makes the grid an
/// ANSWER rather than a texture.</b> The grid's one job is to say "these are
/// the squares this object will land on", and that question only exists while
/// a gesture that snaps on the move grid is live. At rest it is decoration
/// competing with the level for the same pixels, which is exactly the
/// "loads and unloads in chunks" complaint: nobody watches a grid they are not
/// using, until it changes, and then the change is all they see.
/// </para>
/// <para>
/// Set semantics through <c>EditorHostCommand</c> (<c>GridAuto</c> /
/// <c>GridOn</c> / <c>GridOff</c>), never a toggle, for the reason every host
/// verb a control displays follows that rule: a toggle sent against a stale
/// snapshot flips the wrong way exactly when the user clicks fastest.
/// </para>
/// </remarks>
public enum GridMode
{
    /// <summary>
    /// Shown while a move or resize gesture is live — the gestures whose
    /// snapping the grid draws — and faded out otherwise.
    /// </summary>
    Auto,

    /// <summary>Always drawn.</summary>
    On,

    /// <summary>Never drawn.</summary>
    Off,
}
