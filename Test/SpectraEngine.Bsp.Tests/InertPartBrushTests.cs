using System.Numerics;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The one combination of the two brush bits that cancels itself.
/// </summary>
/// <remarks>
/// <para>
/// <c>BrushKind</c> decides admission and <c>BrushOperation</c> decides sign,
/// and they are independent on purpose. But <see cref="BrushKind.Part"/> means
/// "not in the placement list", and a subtractive brush does its entire job
/// from inside that list: the two together produce a brush that carves nothing
/// and, because only additive parts get a mesh, draws nothing either.
/// </para>
/// <para>
/// <b>The failure is total silence.</b> No exception, no geometry, no hole, and
/// nothing in the scene that looks different from a brush that was never
/// created. Counting it is the difference between a mistake an author can see
/// and one they cannot.
/// </para>
/// </remarks>
public sealed class InertPartBrushTests
{
    private static SceneNode AddBrush(Scene scene, string name, BrushKind kind, BrushOperation operation)
    {
        SceneNode node = scene.Root.CreateChild(name);
        node.BrushKind = kind;
        node.Brush = Brush
            .CreateBox(new Vector3(-1f, -1f, -1f), Vector3.One)
            .WithOperation(operation);
        return node;
    }

    [Theory]
    [InlineData(BrushKind.World, BrushOperation.Additive, 0)]
    [InlineData(BrushKind.World, BrushOperation.Subtractive, 0)]
    [InlineData(BrushKind.Part, BrushOperation.Additive, 0)]
    [InlineData(BrushKind.Part, BrushOperation.Subtractive, 1)]
    public void Only_a_subtractive_part_is_inert(BrushKind kind, BrushOperation operation, int expected)
    {
        var scene = new Scene("kinds");
        AddBrush(scene, "B", kind, operation);

        scene.InertPartBrushCount.ShouldBe(expected);
    }

    [Fact]
    public void Converting_a_negative_to_a_part_makes_it_inert_and_back_again()
    {
        // This is the path that actually produces one: the editor converts a
        // whole selection between world geometry and parts in one command, and
        // a doorway cut caught up in that selection stops being a doorway.
        var scene = new Scene("convert");
        SceneNode doorway = AddBrush(scene, "DoorwayCut", BrushKind.World, BrushOperation.Subtractive);
        scene.InertPartBrushCount.ShouldBe(0);

        doorway.BrushKind = BrushKind.Part;
        scene.InertPartBrushCount.ShouldBe(1);

        doorway.BrushKind = BrushKind.World;
        scene.InertPartBrushCount.ShouldBe(0);
    }

    [Fact]
    public void An_inert_part_is_in_neither_the_draw_list_nor_the_carve()
    {
        // Both halves asserted, because the count above would be satisfied by a
        // brush that was merely mislabelled rather than genuinely absent.
        var scene = new Scene("absent");
        SceneNode node = AddBrush(scene, "Nothing", BrushKind.Part, BrushOperation.Subtractive);

        scene.PartBrushNodes.ShouldNotContain(node);
        node.BrushKind.ShouldBe(BrushKind.Part);
        scene.InertPartBrushCount.ShouldBe(1);
    }
}
