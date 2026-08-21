namespace SpectraEngine.Core.Scene;

/// <summary>
/// How a batch of nodes combines with the selection already in a
/// <see cref="SelectionSet"/> — the data form of the modifier key a viewport
/// was holding when the gesture ended.
/// </summary>
/// <remarks>
/// One enum rather than one per layer: the editor resolves Shift/Ctrl into
/// these values and hands them to <see cref="SelectionSet.Apply"/>, so there is
/// no second vocabulary to keep in sync. The mapping every editor uses —
/// nothing held replaces, Shift adds, Ctrl toggles — lives above this type, in
/// the editing layer, because it is a keymap decision.
/// </remarks>
public enum SelectionUpdate
{
    /// <summary>The batch becomes the selection; whatever was selected is dropped.</summary>
    Replace,

    /// <summary>The batch is added to the selection; nothing is dropped.</summary>
    Add,

    /// <summary>
    /// Each node in the batch flips: selected ones are dropped, unselected ones
    /// are added. Nodes outside the batch are untouched.
    /// </summary>
    Toggle,
}
