using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Inspection;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Commands;
using SpectraEngine.Editing.Undo;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// Writing a property-panel edit back to a selection.
/// </summary>
/// <remarks>
/// <para>
/// <b>The panel commits on Enter and on losing focus</b>, so tabbing through
/// fields without changing anything is an ordinary thing for a person to do.
/// Two rules follow, and both are tested here: one commit is one history entry
/// however many nodes it touched, and a commit that changes nothing records
/// nothing.
/// </para>
/// <para>
/// <b>The axis mask is the consequential part.</b> Editing the y of a mixed
/// position has to leave x and z as each node had them; writing the whole
/// vector back would stack the selection at one point.
/// </para>
/// </remarks>
public sealed class PropertyEditorTests
{
    private static Brush Box(float half = 1f) =>
        Brush.CreateBox(new Vector3(-half), new Vector3(half), default);

    private sealed class Rig
    {
        public Rig(params SceneNode[] nodes)
        {
            Scene = new Scene("props");
            foreach (SceneNode node in nodes)
                Scene.Root.AddChild(node);
            Undo = new UndoStack(Scene);
            Nodes = nodes;
        }

        public Scene Scene { get; }
        public UndoStack Undo { get; }
        public IReadOnlyList<SceneNode> Nodes { get; }

        public int Apply(PropertyEdit edit) => PropertyEditor.Apply(Undo, Nodes, edit);
    }

    // --- history shape ------------------------------------------------------

    [Fact]
    public void A_bulk_edit_over_many_nodes_is_one_undo_entry()
    {
        // Fifty entries would take fifty Ctrl+Z presses to undo one thing the
        // user did once.
        var a = new SceneNode("A") { LocalPosition = new Vector3(0f, 1f, 0f) };
        var b = new SceneNode("B") { LocalPosition = new Vector3(0f, 2f, 0f) };
        var c = new SceneNode("C") { LocalPosition = new Vector3(0f, 3f, 0f) };
        var rig = new Rig(a, b, c);

        rig.Apply(new PropertyEdit
        {
            Id = PropertyId.Position,
            Axes = PropertyAxes.Y,
            Vector = new Vector3(0f, 0f, 0f),
        }).ShouldBe(3);

        rig.Undo.UndoCount.ShouldBe(1);

        rig.Undo.Undo().ShouldBeTrue();
        a.LocalPosition.Y.ShouldBe(1f);
        b.LocalPosition.Y.ShouldBe(2f);
        c.LocalPosition.Y.ShouldBe(3f);
    }

    [Fact]
    public void A_commit_that_changes_nothing_records_nothing()
    {
        // Tabbing through fields must not fill the history with entries that
        // undo to themselves.
        var node = new SceneNode("A") { LocalPosition = new Vector3(4f, 5f, 6f) };
        var rig = new Rig(node);

        rig.Apply(new PropertyEdit { Id = PropertyId.Position, Vector = new Vector3(4f, 5f, 6f) })
            .ShouldBe(0);

        rig.Undo.UndoCount.ShouldBe(0);
        rig.Undo.CanUndo.ShouldBeFalse();
    }

    [Fact]
    public void Only_the_nodes_that_actually_differ_record_a_command()
    {
        var settled = new SceneNode("Settled") { LocalPosition = new Vector3(0f, 0f, 0f) };
        var odd = new SceneNode("Odd") { LocalPosition = new Vector3(0f, 9f, 0f) };
        var rig = new Rig(settled, odd);

        rig.Apply(new PropertyEdit
        {
            Id = PropertyId.Position,
            Axes = PropertyAxes.Y,
            Vector = Vector3.Zero,
        }).ShouldBe(1, "the settled node already holds the value");
    }

    // --- the axis mask ------------------------------------------------------

    [Fact]
    public void Editing_one_axis_leaves_the_others_as_each_node_had_them()
    {
        // The single most consequential behaviour here: without the mask this
        // is a way to stack the whole selection at one point.
        var a = new SceneNode("A") { LocalPosition = new Vector3(-5f, 1f, 3f) };
        var b = new SceneNode("B") { LocalPosition = new Vector3(7f, 2f, -8f) };
        var rig = new Rig(a, b);

        rig.Apply(new PropertyEdit
        {
            Id = PropertyId.Position,
            Axes = PropertyAxes.Y,
            Vector = new Vector3(999f, 0f, 999f),
        });

        a.LocalPosition.ShouldBe(new Vector3(-5f, 0f, 3f));
        b.LocalPosition.ShouldBe(new Vector3(7f, 0f, -8f));
    }

    [Fact]
    public void A_rotation_is_merged_in_degrees_rather_than_on_the_quaternion()
    {
        // A quaternion has no separable components, so editing the yaw of a
        // mixed selection can only leave each node's pitch alone if the merge
        // happens in the euler view.
        var a = new SceneNode("A")
        {
            LocalRotation = new EulerAngles(Yaw: 0f, Pitch: 30f, Roll: 0f).ToQuaternion(),
        };
        var b = new SceneNode("B")
        {
            LocalRotation = new EulerAngles(Yaw: 0f, Pitch: -45f, Roll: 0f).ToQuaternion(),
        };
        var rig = new Rig(a, b);

        rig.Apply(new PropertyEdit
        {
            Id = PropertyId.Rotation,
            Axes = PropertyAxes.Y,
            Vector = new Vector3(0f, 90f, 0f),
        });

        EulerAngles.FromQuaternion(a.LocalRotation).Pitch.ShouldBe(30f, 0.01f);
        EulerAngles.FromQuaternion(a.LocalRotation).Yaw.ShouldBe(90f, 0.01f);
        EulerAngles.FromQuaternion(b.LocalRotation).Pitch.ShouldBe(-45f, 0.01f);
        EulerAngles.FromQuaternion(b.LocalRotation).Yaw.ShouldBe(90f, 0.01f);
    }

    // --- reaching only the nodes that carry the property --------------------

    [Fact]
    public void An_edit_reaches_only_the_nodes_that_carry_the_property()
    {
        // A brush field edited while a light is also selected is a bulk edit
        // over the brushes, not an error and not a no-op.
        var brush = new SceneNode("Wall") { Brush = Box() };
        var light = new SceneNode("Lamp") { Light = new Light() };
        var rig = new Rig(brush, light);

        rig.Apply(new PropertyEdit { Id = PropertyId.BrushKind, Text = "Part" }).ShouldBe(1);

        brush.BrushKind.ShouldBe(BrushKind.Part);
        light.BrushKind.ShouldBe(BrushKind.World, "a node with no brush is untouched");
    }

    [Fact]
    public void A_read_only_property_is_ignored_rather_than_throwing()
    {
        // The panel never offers these, so reaching here means a caller built an
        // edit by hand; a throw would be a crash rather than a correction.
        var node = new SceneNode("A");
        var rig = new Rig(node);

        rig.Apply(new PropertyEdit { Id = PropertyId.NodeId, Text = "nonsense" }).ShouldBe(0);
        rig.Apply(new PropertyEdit { Id = PropertyId.MeshModel, Text = "x.obj" }).ShouldBe(0);
        rig.Undo.UndoCount.ShouldBe(0);
    }

    // --- values the payload would refuse ------------------------------------

    [Fact]
    public void A_light_range_of_zero_is_refused_before_anything_is_written()
    {
        // Light.Range throws on anything not strictly positive. A command
        // carrying zero would throw from inside Do, halfway through a
        // transaction, leaving the history open and the scene half-edited.
        var node = new SceneNode("Sun") { Light = new Light { Range = 10f } };
        var rig = new Rig(node);

        Should.NotThrow(() => rig.Apply(new PropertyEdit { Id = PropertyId.LightRange, Number = 0f }))
            .ShouldBe(0);

        node.Light!.Range.ShouldBe(10f);
        rig.Undo.UndoCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(-1f)]
    public void An_intensity_the_light_would_refuse_is_refused_here(float intensity)
    {
        var node = new SceneNode("Sun") { Light = new Light { Intensity = 4f } };
        var rig = new Rig(node);

        Should.NotThrow(() => rig.Apply(new PropertyEdit
        {
            Id = PropertyId.LightIntensity,
            Number = intensity,
        })).ShouldBe(0);

        node.Light!.Intensity.ShouldBe(4f);
    }

    [Fact]
    public void An_empty_name_is_refused()
    {
        // It would leave a row in the tree with nothing to click.
        var node = new SceneNode("Wall");
        var rig = new Rig(node);

        rig.Apply(new PropertyEdit { Id = PropertyId.NodeName, Text = "   " }).ShouldBe(0);

        node.Name.ShouldBe("Wall");
    }

    // --- payload edits ------------------------------------------------------

    [Fact]
    public void A_light_edit_undoes_by_value_rather_than_by_reference()
    {
        // Light is the one MUTABLE payload. A command holding the instance and
        // restoring the pointer would restore an object whose fields the redo
        // had already overwritten, so undo would appear to do nothing.
        var node = new SceneNode("Sun") { Light = new Light { Intensity = 2f } };
        var rig = new Rig(node);

        rig.Apply(new PropertyEdit { Id = PropertyId.LightIntensity, Number = 11f }).ShouldBe(1);
        node.Light!.Intensity.ShouldBe(11f);

        rig.Undo.Undo().ShouldBeTrue();
        node.Light!.Intensity.ShouldBe(2f);

        rig.Undo.Redo().ShouldBeTrue();
        node.Light!.Intensity.ShouldBe(11f);
    }

    [Fact]
    public void A_brush_size_is_typed_as_a_size_and_becomes_the_factor_the_brush_wants()
    {
        // One typed number is the same world measurement on every object in the
        // selection, whatever each already measured. That is the whole reason
        // the row is a size rather than a scale.
        var small = new SceneNode("Small") { Brush = Box(0.5f) };   // 1 unit across
        var large = new SceneNode("Large") { Brush = Box(3f) };     // 6 units across
        var rig = new Rig(small, large);

        rig.Apply(new PropertyEdit
        {
            Id = PropertyId.BrushSize,
            Axes = PropertyAxes.X,
            Vector = new Vector3(4f, 0f, 0f),
        }).ShouldBe(2);

        Aabb smallBounds = small.Brush!.LocalBounds;
        Aabb largeBounds = large.Brush!.LocalBounds;
        (smallBounds.Max.X - smallBounds.Min.X).ShouldBe(4f, 0.001f);
        (largeBounds.Max.X - largeBounds.Min.X).ShouldBe(4f, 0.001f);

        // The untouched axes keep each brush's own measurement.
        (smallBounds.Max.Y - smallBounds.Min.Y).ShouldBe(1f, 0.001f);
        (largeBounds.Max.Y - largeBounds.Min.Y).ShouldBe(6f, 0.001f);
    }

    [Fact]
    public void A_brush_operation_edit_swaps_the_brush_and_undoes_cleanly()
    {
        var node = new SceneNode("Doorway") { Brush = Box() };
        var rig = new Rig(node);
        Brush original = node.Brush!;

        rig.Apply(new PropertyEdit { Id = PropertyId.BrushOperation, Text = "Subtractive" }).ShouldBe(1);
        node.Brush!.Operation.ShouldBe(BrushOperation.Subtractive);

        rig.Undo.Undo().ShouldBeTrue();
        node.Brush.ShouldBeSameAs(original,
            "undo restores the instance, which is what the carve cache keys on");
    }

    [Fact]
    public void A_size_of_zero_is_refused_rather_than_collapsing_the_brush()
    {
        var node = new SceneNode("Wall") { Brush = Box() };
        var rig = new Rig(node);

        rig.Apply(new PropertyEdit
        {
            Id = PropertyId.BrushSize,
            Axes = PropertyAxes.X,
            Vector = Vector3.Zero,
        }).ShouldBe(0);

        Aabb bounds = node.Brush!.LocalBounds;
        (bounds.Max.X - bounds.Min.X).ShouldBe(2f, 0.001f);
    }
}
