using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Commands;
using SpectraEngine.Editing.Undo;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// Transform command semantics: absolute before/after values round-trip
/// exactly through do/undo/redo, edits are addressed by
/// <see cref="SceneNode.Id"/> rather than by object reference (so a node
/// destroyed and recreated under the same id is still the right target), and a
/// command whose target is not in the scene is a silent no-op.
/// </summary>
public sealed class SetTransformCommandTests
{
    private static readonly Vector3 StartPosition = new(1.25f, -3.5f, 7.75f);
    private static readonly Vector3 EndPosition = new(-11.5f, 0.125f, 42f);

    [Fact]
    public void Execute_then_undo_then_redo_restores_exact_positions()
    {
        var (scene, node) = CreateSceneWithNode();
        var stack = new UndoStack(scene);

        stack.Execute(SetTransformCommand.Move(node, EndPosition));
        node.LocalPosition.ShouldBe(EndPosition);

        stack.Undo().ShouldBeTrue();
        // Exact, not approximate: the command replays the captured value, it
        // does not integrate a delta.
        node.LocalPosition.ShouldBe(StartPosition);

        stack.Redo().ShouldBeTrue();
        node.LocalPosition.ShouldBe(EndPosition);
    }

    [Fact]
    public void Rotation_round_trips_exactly_and_leaves_position_alone()
    {
        var (scene, node) = CreateSceneWithNode();
        var stack = new UndoStack(scene);
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.1f);

        stack.Execute(SetTransformCommand.Rotate(node, rotation));

        node.LocalRotation.ShouldBe(rotation);
        node.LocalPosition.ShouldBe(StartPosition);

        stack.Undo().ShouldBeTrue();
        node.LocalRotation.ShouldBe(Quaternion.Identity);
        node.LocalPosition.ShouldBe(StartPosition);
    }

    [Fact]
    public void Move_never_touches_scale()
    {
        var (scene, node) = CreateSceneWithNode();
        node.LocalScale = new Vector3(2f, 3f, 4f);
        var stack = new UndoStack(scene);

        stack.Execute(SetTransformCommand.Move(node, EndPosition));
        stack.Undo();

        // Brush nodes must stay rigid; the move command owns position and
        // rotation only.
        node.LocalScale.ShouldBe(new Vector3(2f, 3f, 4f));
    }

    [Fact]
    public void Local_transform_command_round_trips_all_three_components()
    {
        var (scene, node) = CreateSceneWithNode();
        var stack = new UndoStack(scene);
        var after = new Transform
        {
            Position = EndPosition,
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.25f),
            Scale = new Vector3(5f, 6f, 7f),
        };

        stack.Execute(SetLocalTransformCommand.Capture(node, after));

        node.LocalPosition.ShouldBe(after.Position);
        node.LocalRotation.ShouldBe(after.Rotation);
        node.LocalScale.ShouldBe(after.Scale);

        stack.Undo().ShouldBeTrue();
        node.LocalPosition.ShouldBe(StartPosition);
        node.LocalRotation.ShouldBe(Quaternion.Identity);
        node.LocalScale.ShouldBe(Vector3.One);
    }

    [Fact]
    public void Undo_targets_a_node_recreated_under_the_same_id()
    {
        var (scene, node) = CreateSceneWithNode();
        var stack = new UndoStack(scene);
        Guid id = node.Id;

        stack.Execute(SetTransformCommand.Move(node, EndPosition));

        // Simulate what an undo of a delete does: destroy the instance and
        // recreate the node under the same identity, already carrying the
        // post-edit value.
        scene.Root.RemoveChild(node);
        var recreated = new SceneNode("Box", id) { LocalPosition = EndPosition };
        scene.Root.AddChild(recreated);

        stack.Undo().ShouldBeTrue();

        // The command found the LIVE node by id; the stale instance an
        // object-reference-addressed command would have edited is untouched.
        recreated.LocalPosition.ShouldBe(StartPosition);
        node.LocalPosition.ShouldBe(EndPosition);
    }

    [Fact]
    public void Undo_of_a_command_whose_node_left_the_scene_is_a_silent_no_op()
    {
        var (scene, node) = CreateSceneWithNode();
        var stack = new UndoStack(scene);
        stack.Execute(SetTransformCommand.Move(node, EndPosition));

        scene.Root.RemoveChild(node);

        // Legitimate: history behind a still-undone delete names nodes that are
        // not currently attached. It must not throw, and must not resurrect a
        // value onto the detached instance.
        stack.Undo().ShouldBeTrue();
        node.LocalPosition.ShouldBe(EndPosition);
    }

    [Fact]
    public void Applying_a_command_twice_is_idempotent_and_dirties_nothing_the_second_time()
    {
        var (scene, node) = CreateSceneWithNode();
        var command = SetTransformCommand.Move(node, EndPosition);

        int fired = 0;
        scene.NodeTransformChanged += _ => fired++;

        command.Do(scene);
        command.Do(scene);

        // Absolute values, not deltas: the second Do writes the value the node
        // already holds, the setter's equality early-out swallows it, and no
        // static-world dirtying or change event follows. This is exactly why a
        // live-dragged tool can Record without double-applying anything.
        node.LocalPosition.ShouldBe(EndPosition);
        fired.ShouldBe(1);
    }

    [Fact]
    public void Absorb_keeps_the_original_before_and_takes_the_newest_after()
    {
        var (scene, node) = CreateSceneWithNode();
        var first = SetTransformCommand.Move(node, new Vector3(5f, 0f, 0f));
        node.LocalPosition = new Vector3(5f, 0f, 0f);
        var second = SetTransformCommand.Move(node, EndPosition);

        first.TryAbsorb(second).ShouldBeTrue();

        first.BeforePosition.ShouldBe(StartPosition);
        first.AfterPosition.ShouldBe(EndPosition);
    }

    [Fact]
    public void Absorb_refuses_a_command_targeting_a_different_node()
    {
        var (scene, node) = CreateSceneWithNode();
        var other = scene.Root.CreateChild("Other");

        var first = SetTransformCommand.Move(node, EndPosition);
        var second = SetTransformCommand.Move(other, EndPosition);

        first.TryAbsorb(second).ShouldBeFalse();
    }

    private static (Scene Scene, SceneNode Node) CreateSceneWithNode()
    {
        var scene = new Scene("Editing");
        var node = scene.Root.CreateChild("Box");
        node.LocalPosition = StartPosition;
        return (scene, node);
    }
}
