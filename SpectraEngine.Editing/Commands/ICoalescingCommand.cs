namespace SpectraEngine.Editing.Commands;

/// <summary>
/// A command that can absorb a later command of the same kind targeting the
/// same node, keeping its own <em>before</em> state and adopting the newer
/// <em>after</em> state.
/// </summary>
/// <remarks>
/// This is what turns a gizmo drag into a single undo entry. A drag pushes one
/// command per frame; inside an open transaction the
/// <see cref="Undo.UndoStack"/> offers each new command to the entries already
/// recorded, and an absorb collapses them — so a 60-frame drag of one node ends
/// as one command whose before/after span the whole gesture, and a drag of five
/// nodes ends as five, one per node.
/// <para>
/// Absorbing is only ever attempted inside an open transaction. Two separate
/// user gestures must stay two undo entries, so committed history is never
/// collapsed.
/// </para>
/// </remarks>
public interface ICoalescingCommand : IEditorCommand
{
    /// <summary>
    /// Absorbs <paramref name="newer"/> if it is the same kind of edit on the
    /// same node, and returns true when it did. Returning true means the caller
    /// must drop <paramref name="newer"/>: this command now stands for both.
    /// Implementations keep their original before-state — that is the point.
    /// </summary>
    bool TryAbsorb(IEditorCommand newer);
}
