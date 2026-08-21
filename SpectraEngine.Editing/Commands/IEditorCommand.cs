using SpectraEngine.Core.Scene;

namespace SpectraEngine.Editing.Commands;

/// <summary>
/// One reversible editor operation. Commands are the only sanctioned way for
/// editor tools to change a scene, because they are what the
/// <see cref="Undo.UndoStack"/> can replay in both directions.
/// </summary>
/// <remarks>
/// <b>Address nodes by <see cref="SceneNode.Id"/>, never by reference.</b> Undo
/// of a delete destroys the old instance and recreates the node under the same
/// id, so any object reference a command captured would be pointing at a corpse
/// by the time the command is undone. Commands therefore store ids and resolve
/// them through <c>Scene.TryFindById</c> at execution time — and treat a miss
/// as a no-op rather than an error, since a node may legitimately be absent
/// while a stretch of history sits undone.
/// <para>
/// <b>Capture absolute before/after values, not deltas and not scene
/// snapshots.</b> Absolute values make Do/Undo idempotent, which matters
/// concretely here: the transform setters early-out on exact equality and the
/// CSG compile caches key on exact placement values, so re-applying a value the
/// scene already has costs a comparison and dirties nothing. Deltas would drift
/// and would break under coalescing; snapshots would make every edit O(scene).
/// </para>
/// <para>
/// <b>Threading:</b> render thread only, like the <see cref="Scene"/> the
/// commands mutate.
/// </para>
/// </remarks>
public interface IEditorCommand
{
    /// <summary>
    /// Human-readable label for this operation, for an "Undo &lt;name&gt;" menu
    /// item. Describes the operation, not the target.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Applies the command's <em>after</em> state to <paramref name="scene"/>.
    /// Must be idempotent, and must do nothing when the target node is not
    /// currently in the scene.
    /// </summary>
    void Do(Scene scene);

    /// <summary>
    /// Restores the command's <em>before</em> state on <paramref name="scene"/>.
    /// Same idempotence and missing-target rules as <see cref="Do"/>.
    /// </summary>
    void Undo(Scene scene);

    /// <summary>
    /// Restores the before-state for an <em>abandoned gesture</em> rather than
    /// for a history step: the roll-back
    /// <see cref="Undo.UndoStack.CancelTransaction"/> performs, whose contract
    /// is that the scene ends up exactly as the gesture found it. Defaults to
    /// <see cref="Undo"/>, which is right for any command whose rollback is
    /// already unconditional.
    /// </summary>
    /// <remarks>
    /// <b>The missing-target rule inverts here, and that is the whole point.</b>
    /// <see cref="Undo"/> must no-op on a node that is not in the scene, because
    /// history behind a still-undone delete legitimately names absent nodes and
    /// will get its chance again when the delete is redone. A cancel gets no
    /// second chance: it <em>discards</em> its commands afterwards, so a node
    /// that left the scene mid-gesture — a host removing it, a scene reload —
    /// would be stranded at a half-dragged value with nothing left anywhere that
    /// could restore it. An implementation that can still reach such a node
    /// (because it remembers what it last wrote to) should do so here, and only
    /// here.
    /// </remarks>
    void RollBack(Scene scene) => Undo(scene);
}
