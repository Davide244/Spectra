using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Commands;
using SpectraEngine.Editing.Undo;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// <see cref="UndoStack"/> history semantics: the cursor walks a linear
/// timeline, a new command invalidates everything redoable behind it, the ring
/// evicts the oldest entry once <see cref="UndoStack.Capacity"/> is reached,
/// and <see cref="UndoStack.Changed"/> fires exactly once per operation that
/// altered what the history offers.
/// </summary>
public sealed class UndoStackTests
{
    [Fact]
    public void A_fresh_stack_can_neither_undo_nor_redo()
    {
        var (scene, _) = CreateScene();
        var stack = new UndoStack(scene);

        stack.CanUndo.ShouldBeFalse();
        stack.CanRedo.ShouldBeFalse();
        stack.Count.ShouldBe(0);
        stack.Undo().ShouldBeFalse();
        stack.Redo().ShouldBeFalse();
        stack.UndoName.ShouldBeNull();
        stack.RedoName.ShouldBeNull();
    }

    [Fact]
    public void Undo_and_redo_walk_a_multi_step_history_in_order()
    {
        var (scene, node) = CreateScene();
        var stack = new UndoStack(scene);
        MoveTo(stack, node, 1f);
        MoveTo(stack, node, 2f);
        MoveTo(stack, node, 3f);

        stack.Count.ShouldBe(3);
        stack.UndoCount.ShouldBe(3);

        stack.Undo();
        node.LocalPosition.X.ShouldBe(2f);
        stack.Undo();
        node.LocalPosition.X.ShouldBe(1f);
        stack.Undo();
        node.LocalPosition.X.ShouldBe(0f);

        stack.CanUndo.ShouldBeFalse();
        stack.RedoCount.ShouldBe(3);

        stack.Redo();
        stack.Redo();
        stack.Redo();
        node.LocalPosition.X.ShouldBe(3f);
        stack.CanRedo.ShouldBeFalse();
    }

    [Fact]
    public void A_new_command_invalidates_the_redo_tail()
    {
        var (scene, node) = CreateScene();
        var stack = new UndoStack(scene);
        MoveTo(stack, node, 1f);
        MoveTo(stack, node, 2f);
        stack.Undo();
        stack.Undo();
        stack.RedoCount.ShouldBe(2);

        MoveTo(stack, node, 9f);

        // The timeline is linear, not a tree: the two undone entries are gone.
        stack.CanRedo.ShouldBeFalse();
        stack.RedoCount.ShouldBe(0);
        stack.Count.ShouldBe(1);
        stack.UndoCount.ShouldBe(1);

        stack.Undo();
        node.LocalPosition.X.ShouldBe(0f);
    }

    [Fact]
    public void History_is_bounded_and_evicts_the_oldest_entry()
    {
        var (scene, node) = CreateScene();
        var stack = new UndoStack(scene, capacity: 3);

        for (int i = 1; i <= 5; i++)
            MoveTo(stack, node, i);

        stack.Capacity.ShouldBe(3);
        stack.Count.ShouldBe(3);
        stack.UndoCount.ShouldBe(3);

        // Only the last three edits survive: undoing all of them lands on the
        // before-state of edit #3, i.e. x = 2. Edits #1 and #2 fell off the
        // back and are permanently un-undoable.
        stack.Undo();
        stack.Undo();
        stack.Undo();
        node.LocalPosition.X.ShouldBe(2f);
        stack.CanUndo.ShouldBeFalse();

        // The ring is still coherent after wrapping: redo walks back up.
        stack.Redo();
        stack.Redo();
        stack.Redo();
        node.LocalPosition.X.ShouldBe(5f);
    }

    [Fact]
    public void Eviction_keeps_working_after_the_ring_wraps_many_times()
    {
        var (scene, node) = CreateScene();
        var stack = new UndoStack(scene, capacity: 4);

        for (int i = 1; i <= 100; i++)
            MoveTo(stack, node, i);

        stack.Count.ShouldBe(4);
        for (int i = 0; i < 4; i++)
            stack.Undo().ShouldBeTrue();

        // Four retained entries ending at #100 means the oldest retained is
        // #97, whose before-state is x = 96.
        node.LocalPosition.X.ShouldBe(96f);
        stack.CanUndo.ShouldBeFalse();
    }

    [Fact]
    public void Capacity_of_one_keeps_only_the_latest_edit()
    {
        var (scene, node) = CreateScene();
        var stack = new UndoStack(scene, capacity: 1);

        MoveTo(stack, node, 1f);
        MoveTo(stack, node, 2f);

        stack.Count.ShouldBe(1);
        stack.Undo().ShouldBeTrue();
        node.LocalPosition.X.ShouldBe(1f);
        stack.CanUndo.ShouldBeFalse();
    }

    [Fact]
    public void Changed_fires_once_per_history_altering_operation()
    {
        var (scene, node) = CreateScene();
        var stack = new UndoStack(scene);
        int fired = 0;
        stack.Changed += () => fired++;

        MoveTo(stack, node, 1f);
        fired.ShouldBe(1);

        stack.Undo();
        fired.ShouldBe(2);

        stack.Redo();
        fired.ShouldBe(3);

        // Refused operations change nothing and announce nothing.
        stack.Redo().ShouldBeFalse();
        fired.ShouldBe(3);

        stack.Clear();
        fired.ShouldBe(4);

        // Clearing an already empty history is silent.
        stack.Clear();
        fired.ShouldBe(4);
    }

    [Fact]
    public void Clear_drops_history_without_touching_the_scene()
    {
        var (scene, node) = CreateScene();
        var stack = new UndoStack(scene);
        MoveTo(stack, node, 4f);

        stack.Clear();

        stack.Count.ShouldBe(0);
        stack.CanUndo.ShouldBeFalse();
        stack.CanRedo.ShouldBeFalse();
        node.LocalPosition.X.ShouldBe(4f);
    }

    [Fact]
    public void Undo_and_redo_names_describe_the_pending_entry()
    {
        var (scene, node) = CreateScene();
        var stack = new UndoStack(scene);
        stack.Execute(SetTransformCommand.Move(node, Vector3.UnitX));

        stack.UndoName.ShouldBe("Transform");
        stack.RedoName.ShouldBeNull();

        stack.Undo();
        stack.UndoName.ShouldBeNull();
        stack.RedoName.ShouldBe("Transform");
    }

    [Fact]
    public void Cancelling_restores_a_node_that_left_the_scene_during_the_gesture()
    {
        // A cancel throws its commands away afterwards, so it is the ONLY thing
        // that can ever put such a node back. Undo's "not in the scene, never
        // mind" rule is right for replaying history behind an undone delete and
        // catastrophic here: the node would stay at its mid-drag value forever,
        // with no history entry naming the pre-gesture one.
        var (scene, node) = CreateScene();
        node.LocalPosition = new Vector3(3f, 3f, 3f);
        var stack = new UndoStack(scene);

        stack.BeginTransaction("Move");
        SetTransformCommand command = SetTransformCommand.Move(node, new Vector3(8f, 3f, 3f));
        stack.Record(command);
        command.Do(scene);
        node.LocalPosition.ShouldBe(new Vector3(8f, 3f, 3f));

        scene.Root.RemoveChild(node);
        scene.TryFindById(node.Id, out _).ShouldBeFalse();

        stack.CancelTransaction();

        node.LocalPosition.ShouldBe(new Vector3(3f, 3f, 3f));
        stack.Count.ShouldBe(0);
        stack.IsTransactionOpen.ShouldBeFalse();

        // And it is still restored once the node comes back — the roll-back
        // wrote the node itself, not a scene-side copy of it.
        scene.Root.AddChild(node);
        node.LocalPosition.ShouldBe(new Vector3(3f, 3f, 3f));
    }

    [Fact]
    public void An_ordinary_undo_still_ignores_a_node_that_is_not_in_the_scene()
    {
        // The inverse guarantee: RollBack's reach must not leak into Undo, or
        // history behind a still-undone delete would start writing to corpses.
        var (scene, node) = CreateScene();
        var stack = new UndoStack(scene);
        stack.Execute(SetTransformCommand.Move(node, new Vector3(5f, 0f, 0f)));

        scene.Root.RemoveChild(node);
        stack.Undo().ShouldBeTrue();

        node.LocalPosition.ShouldBe(new Vector3(5f, 0f, 0f));
    }

    [Fact]
    public void Capacity_below_one_is_rejected()
    {
        var (scene, _) = CreateScene();
        Should.Throw<ArgumentOutOfRangeException>(() => new UndoStack(scene, capacity: 0));
    }

    internal static (Scene Scene, SceneNode Node) CreateScene()
    {
        var scene = new Scene("Editing");
        return (scene, scene.Root.CreateChild("Box"));
    }

    // Applies an absolute move through the stack, recording the node's current
    // position as the before-state.
    internal static void MoveTo(UndoStack stack, SceneNode node, float x) =>
        stack.Execute(SetTransformCommand.Move(node, new Vector3(x, 0f, 0f)));
}
