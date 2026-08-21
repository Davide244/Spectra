namespace SpectraEngine.Editing.Selection;

/// <summary>
/// How much of a node the marquee has to cover before it counts as selected.
/// </summary>
public enum BoxSelectMode
{
    /// <summary>
    /// A node is selected when its projected bounds <em>touch</em> the
    /// rectangle. The default, and what every level editor does: sweeping
    /// loosely across a row of props picks them all up.
    /// </summary>
    Intersect,

    /// <summary>
    /// A node is selected only when its projected bounds lie entirely inside
    /// the rectangle — the precise mode, for pulling a few objects out of a
    /// crowd without catching their neighbours.
    /// </summary>
    Contain,
}
