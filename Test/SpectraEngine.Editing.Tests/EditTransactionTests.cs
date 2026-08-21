using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Commands;
using SpectraEngine.Editing.Undo;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// Transaction semantics: a simulated multi-frame gizmo drag coalesces into a
/// single undo entry that spans the whole gesture, a multi-node drag lands one
/// composite entry, cancelling rolls the scene back and records nothing, and
/// the history refuses undo/redo while a gesture is open.
/// </summary>
public sealed class EditTransactionTests
{
    private const int DragFrames = 60;

    [Fact]
    public void A_multi_frame_drag_coalesces_into_one_undo_entry()
    {
        var (scene, node) = UndoStackTests.CreateScene();
        var stack = new UndoStack(scene);

        SimulateDrag(stack, node, DragFrames);

        // Sixty per-frame commands, one history entry.
        stack.Count.ShouldBe(1);
        stack.UndoCount.ShouldBe(1);

        node.LocalPosition.ShouldBe(new Vector3(DragFrames, 0f, 0f));

        // And the entry spans the WHOLE gesture: the coalesced command kept the
        // before-state captured on the grab frame, not the previous frame's.
        stack.Undo().ShouldBeTrue();
        node.LocalPosition.ShouldBe(Vector3.Zero);
        stack.CanUndo.ShouldBeFalse();

        stack.Redo().ShouldBeTrue();
        node.LocalPosition.ShouldBe(new Vector3(DragFrames, 0f, 0f));
    }

    [Fact]
    public void A_drag_of_several_nodes_commits_as_one_composite_entry()
    {
        var scene = new Scene("Editing");
        var a = scene.Root.CreateChild("A");
        var b = scene.Root.CreateChild("B");
        var c = scene.Root.CreateChild("C");
        var stack = new UndoStack(scene);

        stack.BeginTransaction("Move Selection");
        for (int frame = 1; frame <= 10; frame++)
        {
            RecordFrame(stack, a, frame);
            RecordFrame(stack, b, frame * 2);
            RecordFrame(stack, c, frame * 3);
        }
        stack.CommitTransaction();

        // One entry per gesture, holding one coalesced command per node.
        stack.Count.ShouldBe(1);

        stack.Undo().ShouldBeTrue();
        a.LocalPosition.ShouldBe(Vector3.Zero);
        b.LocalPosition.ShouldBe(Vector3.Zero);
        c.LocalPosition.ShouldBe(Vector3.Zero);

        stack.Redo().ShouldBeTrue();
        a.LocalPosition.X.ShouldBe(10f);
        b.LocalPosition.X.ShouldBe(20f);
        c.LocalPosition.X.ShouldBe(30f);
    }

    [Fact]
    public void Recording_into_an_open_transaction_does_not_touch_history()
    {
        var (scene, node) = UndoStackTests.CreateScene();
        var stack = new UndoStack(scene);
        int fired = 0;
        stack.Changed += () => fired++;

        stack.BeginTransaction("Move");
        fired.ShouldBe(1); // opening flips CanUndo/CanRedo — UI must hear it

        for (int frame = 1; frame <= 5; frame++)
            RecordFrame(stack, node, frame);

        stack.Count.ShouldBe(0);
        fired.ShouldBe(1); // per-frame records announce nothing

        stack.CommitTransaction();
        stack.Count.ShouldBe(1);
        fired.ShouldBe(2);
    }

    [Fact]
    public void Undo_and_redo_are_refused_while_a_transaction_is_open()
    {
        var (scene, node) = UndoStackTests.CreateScene();
        var stack = new UndoStack(scene);
        UndoStackTests.MoveTo(stack, node, 5f);

        stack.BeginTransaction("Move");

        stack.CanUndo.ShouldBeFalse();
        stack.CanRedo.ShouldBeFalse();
        stack.Undo().ShouldBeFalse();
        stack.Redo().ShouldBeFalse();

        stack.CommitTransaction();
        stack.CanUndo.ShouldBeTrue();
    }

    [Fact]
    public void Cancelling_rolls_the_drag_back_and_records_nothing()
    {
        var (scene, node) = UndoStackTests.CreateScene();
        node.LocalPosition = new Vector3(3f, 3f, 3f);
        var stack = new UndoStack(scene);

        stack.BeginTransaction("Move");
        for (int frame = 1; frame <= 8; frame++)
            RecordFrame(stack, node, frame);
        node.LocalPosition.X.ShouldBe(8f);

        stack.CancelTransaction();

        // Escape during a drag: the scene is back where the grab started and
        // the history never heard about the gesture.
        node.LocalPosition.ShouldBe(new Vector3(3f, 3f, 3f));
        stack.Count.ShouldBe(0);
        stack.CanUndo.ShouldBeFalse();
        stack.IsTransactionOpen.ShouldBeFalse();
    }

    [Fact]
    public void Cancelling_a_multi_node_drag_rolls_every_node_back()
    {
        var scene = new Scene("Editing");
        var a = scene.Root.CreateChild("A");
        var b = scene.Root.CreateChild("B");
        var stack = new UndoStack(scene);

        stack.BeginTransaction("Move Selection");
        RecordFrame(stack, a, 1f);
        RecordFrame(stack, b, 2f);
        RecordFrame(stack, a, 3f);
        RecordFrame(stack, b, 4f);
        stack.CancelTransaction();

        a.LocalPosition.ShouldBe(Vector3.Zero);
        b.LocalPosition.ShouldBe(Vector3.Zero);
        stack.Count.ShouldBe(0);
    }

    [Fact]
    public void An_empty_transaction_commits_nothing_but_still_reopens_undo()
    {
        var (scene, node) = UndoStackTests.CreateScene();
        var stack = new UndoStack(scene);
        UndoStackTests.MoveTo(stack, node, 1f);

        // A click that grabbed the gizmo but never moved it.
        stack.BeginTransaction("Move");
        stack.CommitTransaction();

        stack.Count.ShouldBe(1);
        stack.CanUndo.ShouldBeTrue();
    }

    [Fact]
    public void A_single_command_transaction_lands_the_command_itself()
    {
        var (scene, node) = UndoStackTests.CreateScene();
        var stack = new UndoStack(scene);

        stack.BeginTransaction("Move Selection");
        RecordFrame(stack, node, 1f);
        stack.CommitTransaction();

        // No composite wrapper for a one-node gesture — the command keeps its
        // own name in the undo menu.
        stack.UndoName.ShouldBe("Transform");
    }

    [Fact]
    public void A_new_gesture_never_coalesces_into_a_committed_one()
    {
        var (scene, node) = UndoStackTests.CreateScene();
        var stack = new UndoStack(scene);

        SimulateDrag(stack, node, 3);
        SimulateDrag(stack, node, 3);

        // Two gestures, two entries — coalescing is scoped to the open
        // transaction, never to committed history.
        stack.Count.ShouldBe(2);
    }

    [Fact]
    public void Transactions_do_not_nest()
    {
        var (scene, _) = UndoStackTests.CreateScene();
        var stack = new UndoStack(scene);
        stack.BeginTransaction("Outer");

        Should.Throw<InvalidOperationException>(() => stack.BeginTransaction("Inner"));
    }

    [Fact]
    public void Committing_or_cancelling_without_an_open_transaction_throws()
    {
        var (scene, _) = UndoStackTests.CreateScene();
        var stack = new UndoStack(scene);

        Should.Throw<InvalidOperationException>(stack.CommitTransaction);
        Should.Throw<InvalidOperationException>(stack.CancelTransaction);
    }

    [Fact]
    public void Clearing_history_mid_transaction_is_refused()
    {
        var (scene, _) = UndoStackTests.CreateScene();
        var stack = new UndoStack(scene);
        stack.BeginTransaction("Move");

        Should.Throw<InvalidOperationException>(stack.Clear);
    }

    // One drag: grab, N frames of live movement + recording, release.
    private static void SimulateDrag(UndoStack stack, SceneNode node, int frames)
    {
        stack.BeginTransaction("Move");
        for (int frame = 1; frame <= frames; frame++)
            RecordFrame(stack, node, frame);
        stack.CommitTransaction();
    }

    // One frame of a live drag: capture the before-state, move the node itself
    // (the gizmo does this so the viewport updates immediately), then record.
    private static void RecordFrame(UndoStack stack, SceneNode node, float x)
    {
        var target = new Vector3(x, 0f, 0f);
        var command = SetTransformCommand.Move(node, target);
        node.LocalPosition = target;
        stack.Record(command);
    }
}
