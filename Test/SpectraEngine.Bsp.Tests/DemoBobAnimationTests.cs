using System.Numerics;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The demo's bobbing brush must never win an argument with an edit. The
/// animation is the frame's LAST writer — the engine runs the editing layer
/// first and the demo update after it (see <c>Engine.Run</c>) — so an
/// animation that recomputed its pose from a rest position captured at load
/// would silently discard every gizmo drag, undo and redo aimed at that node,
/// within the same frame it was made. The tool would look broken rather than
/// the demo, which is why this is pinned rather than commented.
/// </summary>
public sealed class DemoBobAnimationTests
{
    private const float Amplitude = 0.5f;
    private const double PeriodSeconds = 4.0;

    // A quarter period: the sine is at its peak, so the animation is
    // contributing its full amplitude and any "adopt the raw position" bug
    // shows up at maximum size.
    private const double PeakTime = PeriodSeconds / 4.0;

    [Fact]
    public void The_bob_moves_the_node_when_nobody_else_does()
    {
        (SceneNode node, DemoBobAnimation bob) = CreateBob(new Vector3(-2f, 0.1f, -2f));

        bob.Advance(PeakTime);

        node.LocalPosition.Y.ShouldBe(0.1f + Amplitude, 1e-5f);
        node.LocalPosition.X.ShouldBe(-2f);
        node.LocalPosition.Z.ShouldBe(-2f);
    }

    [Fact]
    public void An_external_edit_survives_the_next_frame()
    {
        (SceneNode node, DemoBobAnimation bob) = CreateBob(new Vector3(-2f, 0.1f, -2f));
        bob.Advance(PeakTime);

        // What a gizmo drag does: write an absolute local position.
        var edited = new Vector3(5f, 3f, -7f);
        node.LocalPosition = edited;
        bob.Advance(PeakTime);

        // Same phase, so the animation contributes exactly what it did before
        // the edit — the node must therefore still be exactly where the editor
        // put it, not back at the rest pose captured at load.
        node.LocalPosition.ShouldBe(edited);
    }

    [Fact]
    public void An_external_edit_is_not_snapped_by_the_animations_own_offset()
    {
        // The obvious fix — adopt the edited position as the new rest pose —
        // re-adds the current bob on top of it and jumps the node by up to a
        // full amplitude on the frame after the edit. That jump is precisely
        // the kind of unexplained pop this animation must not produce.
        (SceneNode node, DemoBobAnimation bob) = CreateBob(Vector3.Zero);
        bob.Advance(PeakTime);

        var edited = new Vector3(0f, 10f, 0f);
        node.LocalPosition = edited;
        bob.Advance(PeakTime + 0.001);

        float jump = Vector3.Distance(node.LocalPosition, edited);
        jump.ShouldBeLessThan(0.01f);
    }

    [Fact]
    public void The_bob_carries_on_from_the_edited_pose()
    {
        (SceneNode node, DemoBobAnimation bob) = CreateBob(Vector3.Zero);
        bob.Advance(PeakTime);          // +amplitude
        node.LocalPosition = new Vector3(0f, 10f, 0f);
        bob.Advance(PeakTime);          // adopt

        bob.Advance(PeakTime * 3.0);    // three quarter periods on: -amplitude

        // The edit re-centred the bob, so the swing is measured from the new
        // rest pose (10 - amplitude, because the edit landed on a peak).
        node.LocalPosition.Y.ShouldBe(10f - 2f * Amplitude, 1e-4f);
    }

    [Fact]
    public void An_undo_back_to_the_original_pose_is_honoured_too()
    {
        // The undo path writes the node's captured pre-drag transform, which
        // equals the rest pose plus whatever bob was applied at capture time.
        // Nothing about it may be treated as "the animation's own write".
        (SceneNode node, DemoBobAnimation bob) = CreateBob(Vector3.Zero);
        bob.Advance(PeakTime);
        Vector3 captured = node.LocalPosition;

        node.LocalPosition = new Vector3(4f, 4f, 4f);
        bob.Advance(PeakTime);
        node.LocalPosition = captured;
        bob.Advance(PeakTime);

        node.LocalPosition.ShouldBe(captured);
    }

    [Fact]
    public void The_bob_keeps_dirtying_the_static_world()
    {
        // The animation exists to keep the async recompile pipeline exercised
        // every frame; a version that stopped writing would still pass every
        // assertion above and quietly delete that coverage.
        var scene = new Scene("Test");
        SceneNode node = scene.Root.CreateChild("PillarA");
        node.Brush = Brush.CreateBox(
            new Vector3(-0.2f, -1.1f, -0.2f), new Vector3(0.2f, 1.1f, 0.2f));
        var bob = new DemoBobAnimation(node, Amplitude, PeriodSeconds);
        scene.RebuildStaticWorld(new FakeRenderer());
        scene.StaticWorldDirty.ShouldBeFalse();

        bob.Advance(PeakTime);

        scene.StaticWorldDirty.ShouldBeTrue();
    }

    private static (SceneNode Node, DemoBobAnimation Bob) CreateBob(Vector3 rest)
    {
        var scene = new Scene("Test");
        SceneNode node = scene.Root.CreateChild("PillarA");
        node.LocalPosition = rest;
        return (node, new DemoBobAnimation(node, Amplitude, PeriodSeconds));
    }
}
