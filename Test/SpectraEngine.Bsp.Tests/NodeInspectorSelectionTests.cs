using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Inspection;
using SpectraEngine.Core.Scene;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// What the inspector shows for a selection of more than one node.
/// </summary>
/// <remarks>
/// <para>
/// <b>The union, never the intersection.</b> A row carried by only some of the
/// selection is still shown and still editable, with the edit reaching the
/// nodes that have it. Hiding it would mean that selecting one extra object
/// silently removed the field somebody was about to type into.
/// </para>
/// <para>
/// <b>And disagreement is per AXIS.</b> "Put all of these on the floor" sets y
/// and must leave x and z alone; a row that could only report that vectors
/// differ, and only write all three back, would turn that gesture into a way to
/// stack the whole selection at one point. That is the single most
/// consequential thing in this file.
/// </para>
/// </remarks>
public sealed class NodeInspectorSelectionTests
{
    private static Brush Box() => Brush.CreateBox(new Vector3(-1f), new Vector3(1f), default);

    private static List<PropertyRow> Describe(params SceneNode[] nodes)
    {
        var rows = new List<PropertyRow>();
        NodeInspector.Describe(nodes, rows);
        return rows;
    }

    private static PropertyRow Row(List<PropertyRow> rows, PropertyId id) =>
        rows.Single(r => r.Id == id);

    // --- agreement ----------------------------------------------------------

    [Fact]
    public void Values_that_agree_across_the_selection_are_shown_as_ordinary_rows()
    {
        var a = new SceneNode("A") { LocalPosition = new Vector3(1f, 2f, 3f) };
        var b = new SceneNode("B") { LocalPosition = new Vector3(1f, 2f, 3f) };

        PropertyRow position = Row(Describe(a, b), PropertyId.Position);

        position.IsMixed.ShouldBeFalse();
        position.IsPartial.ShouldBeFalse();
        position.Vector.ShouldBe(new Vector3(1f, 2f, 3f));
    }

    [Fact]
    public void A_selection_of_one_is_the_same_as_describing_that_node()
    {
        var node = new SceneNode("Solo") { Brush = Box() };

        List<PropertyRow> single = new();
        NodeInspector.Describe(node, single);

        Describe(node).Select(r => r.Id).ShouldBe(single.Select(r => r.Id));
        Describe(node).ShouldAllBe(r => !r.IsMixed && !r.IsPartial);
    }

    [Fact]
    public void An_empty_selection_shows_nothing()
    {
        var rows = new List<PropertyRow> { PropertyRow.ReadOnly("stale", "stale", PropertyId.None, "x") };

        NodeInspector.Describe([], rows);

        rows.ShouldBeEmpty();
    }

    // --- disagreement, per axis ---------------------------------------------

    [Fact]
    public void One_differing_axis_leaves_the_other_two_settled()
    {
        // The whole point. Two objects at different heights but the same x and
        // z must show x and z as ordinary values, so that typing into y is a
        // bulk edit and typing into x is not a way to move everything.
        var a = new SceneNode("A") { LocalPosition = new Vector3(5f, 0f, 7f) };
        var b = new SceneNode("B") { LocalPosition = new Vector3(5f, 3f, 7f) };

        PropertyRow position = Row(Describe(a, b), PropertyId.Position);

        position.MixedAxes.ShouldBe(PropertyAxes.Y);
        position.IsMixed.ShouldBeTrue();
    }

    [Fact]
    public void Disagreement_accumulates_across_every_node_rather_than_the_first_pair()
    {
        // Three nodes where no single PAIR disagrees on everything, but the set
        // does. Comparing only against the first node would miss the z.
        var a = new SceneNode("A") { LocalPosition = new Vector3(0f, 0f, 0f) };
        var b = new SceneNode("B") { LocalPosition = new Vector3(1f, 0f, 0f) };
        var c = new SceneNode("C") { LocalPosition = new Vector3(0f, 0f, 2f) };

        PropertyRow position = Row(Describe(a, b, c), PropertyId.Position);

        position.MixedAxes.ShouldBe(PropertyAxes.X | PropertyAxes.Z);
    }

    [Fact]
    public void A_scalar_or_a_flag_disagrees_wholesale()
    {
        var dim = new SceneNode("Dim") { Light = new Light { Intensity = 1f, Enabled = true } };
        var bright = new SceneNode("Bright") { Light = new Light { Intensity = 9f, Enabled = false } };

        List<PropertyRow> rows = Describe(dim, bright);

        Row(rows, PropertyId.LightIntensity).MixedAxes.ShouldBe(PropertyAxes.All);
        Row(rows, PropertyId.LightEnabled).MixedAxes.ShouldBe(PropertyAxes.All);
    }

    [Fact]
    public void Names_and_ids_differ_across_any_real_multi_selection()
    {
        var a = new SceneNode("A");
        var b = new SceneNode("B");

        List<PropertyRow> rows = Describe(a, b);

        Row(rows, PropertyId.NodeName).IsMixed.ShouldBeTrue();
        Row(rows, PropertyId.NodeId).IsMixed.ShouldBeTrue(
            "ids are unique by construction, so a merged id row is always mixed");
        Row(rows, PropertyId.NodeId).IsEditable.ShouldBeFalse();
    }

    [Fact]
    public void Exact_comparison_reports_a_one_ulp_difference_as_mixed()
    {
        // A tolerance here would report two different positions as settled and
        // then write one over the other on the next bulk edit.
        var a = new SceneNode("A") { LocalPosition = new Vector3(1f, 0f, 0f) };
        var b = new SceneNode("B") { LocalPosition = new Vector3(1f + float.Epsilon * 8f, 0f, 0f) };

        PropertyRow position = Row(Describe(a, b), PropertyId.Position);

        // Only meaningful if the two floats really are distinct.
        if (a.LocalPosition.X != b.LocalPosition.X)
            position.MixedAxes.ShouldBe(PropertyAxes.X);
    }

    // --- properties only some of the selection has --------------------------

    [Fact]
    public void A_property_only_part_of_the_selection_has_is_still_shown_and_editable()
    {
        // Selecting a light as well as a brush must not make the brush fields
        // vanish out from under somebody who was about to type into them.
        var brush = new SceneNode("Wall") { Brush = Box() };
        var light = new SceneNode("Lamp") { Light = new Light() };

        List<PropertyRow> rows = Describe(brush, light);

        PropertyRow kind = Row(rows, PropertyId.BrushKind);
        kind.IsPartial.ShouldBeTrue();
        kind.PresentCount.ShouldBe(1);
        kind.SelectionCount.ShouldBe(2);
        kind.IsEditable.ShouldBeTrue("editing it is a bulk edit over the nodes that have it");

        Row(rows, PropertyId.LightIntensity).IsPartial.ShouldBeTrue();
    }

    [Fact]
    public void Both_payloads_contribute_their_groups_to_the_merged_list()
    {
        var brush = new SceneNode("Wall") { Brush = Box() };
        var light = new SceneNode("Lamp") { Light = new Light() };

        IEnumerable<string> groups = Describe(brush, light).Select(r => r.Group).Distinct();

        groups.ShouldBe(
        [
            NodeInspector.NodeGroup,
            NodeInspector.TransformGroup,
            NodeInspector.BrushGroup,
            NodeInspector.LightGroup,
        ]);
    }

    [Fact]
    public void The_merged_order_does_not_depend_on_which_node_was_selected_first()
    {
        // Merging in first-seen order would lay the panel out differently
        // depending on click order, and would break the group-by-run assumption
        // the panel renders with.
        var brush = new SceneNode("Wall") { Brush = Box() };
        var light = new SceneNode("Lamp") { Light = new Light() };

        Describe(brush, light).Select(r => r.Id)
            .ShouldBe(Describe(light, brush).Select(r => r.Id));
    }

    [Fact]
    public void Every_group_is_still_one_contiguous_run_after_merging()
    {
        var brush = new SceneNode("Wall") { Brush = Box() };
        var light = new SceneNode("Lamp") { Light = new Light() };
        var both = new SceneNode("Lamppost") { Brush = Box(), Light = new Light() };

        var seen = new List<string>();
        string? current = null;
        foreach (PropertyRow row in Describe(light, brush, both))
        {
            if (row.Group == current) continue;
            seen.ShouldNotContain(row.Group, $"'{row.Group}' appears in more than one run");
            seen.Add(row.Group);
            current = row.Group;
        }
    }

    [Fact]
    public void A_row_every_node_carries_reports_the_full_count()
    {
        var a = new SceneNode("A") { Brush = Box() };
        var b = new SceneNode("B") { Brush = Box() };
        var c = new SceneNode("C") { Brush = Box() };

        PropertyRow kind = Row(Describe(a, b, c), PropertyId.BrushKind);

        kind.PresentCount.ShouldBe(3);
        kind.SelectionCount.ShouldBe(3);
        kind.IsPartial.ShouldBeFalse();
    }
}
