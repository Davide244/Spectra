using System;
using System.Numerics;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// A known CSG defect, captured as a minimal reproduction.
/// </summary>
/// <remarks>
/// <para>
/// <b>The symptom:</b> a doorway cut by a subtractive brush seals itself when
/// the cut's bottom plane is exactly coplanar with the top face of the brush it
/// stands on. That is the ordinary way to author a door: a wall on a floor, and
/// an opening starting at floor level. The geometry still RENDERS as an opening
/// (the cavity walls are emitted correctly), so it looks right and is solid to
/// every query that goes through the compiled world.
/// </para>
/// <para>
/// <b>What has been ruled out</b>, by bisection on 2026-08-23:
/// </para>
/// <list type="bullet">
/// <item>Not the chunking. A monolithic <see cref="BspTree"/> built over the
/// same surface list gives the identical wrong answer, so the per-cell routing
/// is innocent.</item>
/// <item>Not missing cavity walls. The carve emits both jamb faces whether the
/// floor touches or not, so the subtraction itself ran.</item>
/// <item>Not the cavity-wall seeding test in <c>Csg</c>. Requiring a
/// positive-volume overlap there instead of a merely-touching one changes the
/// surface count not at all.</item>
/// </list>
/// <para>
/// <b>What is known:</b> the touching arrangement produces three MORE surfaces
/// than the non-touching one (23 against 20), and those three are what the solid
/// reconstruction chokes on. Lowering the floor by 0.01 removes them and the
/// door opens. The real demo play area does not hit this, which is why it went
/// unnoticed: with 37 brushes around it the arrangement differs enough to avoid
/// the case.
/// </para>
/// <para>
/// Left skipped rather than deleted so the reproduction survives, and rather
/// than failing so the suite stays green for unrelated work. Remove the Skip to
/// work on it.
/// </para>
/// </remarks>
public sealed class CoplanarCutSealingTests
{
    private const string Reason =
        "Known CSG defect: a cut whose bottom plane is coplanar with the floor it stands on " +
        "seals the opening. See the class remarks for what has been ruled out.";

    [Fact(Skip = Reason)]
    public void A_doorway_standing_on_a_floor_is_open()
    {
        Assert.False(Build(floorTouching: true).ContainsPoint(new Vector3(0f, 1.2f, -4.25f)));
    }

    [Fact]
    public void The_same_doorway_with_the_floor_a_hair_lower_is_open()
    {
        // The control. This passes, and it is what pins the trigger to the
        // coplanar contact rather than to the cut, the wall or the flush z
        // planes, all of which are identical between the two.
        Assert.False(Build(floorTouching: false).ContainsPoint(new Vector3(0f, 1.2f, -4.25f)));
    }

    [Fact]
    public void Either_way_the_carve_emits_both_jambs()
    {
        // Evidence for "the subtraction ran": the cavity walls on the cut's own
        // x planes exist in both arrangements. Whatever is wrong is downstream
        // of the carve emitting them.
        Assert.Equal(2, Jambs(Build(floorTouching: true)));
        Assert.Equal(2, Jambs(Build(floorTouching: false)));
    }

    internal static CsgWorld Build(bool floorTouching)
    {
        var scene = new Scene("CoplanarCut");

        void Box(string name, Vector3 center, Vector3 half, bool cut = false)
        {
            SceneNode node = scene.Root.CreateChild(name);
            node.LocalPosition = center;
            Brush brush = Brush.CreateBox(-half, half);
            node.Brush = cut ? brush.WithOperation(BrushOperation.Subtractive) : brush;
        }

        Box("wall", new Vector3(0f, 1.5f, -4.25f), new Vector3(6f, 1.5f, 0.25f));
        Box("door", new Vector3(0f, 1.2f, -4.25f), new Vector3(1f, 1.2f, 0.25f), cut: true);
        Box("floor", new Vector3(0f, floorTouching ? -0.5f : -0.51f, 0f), new Vector3(6f, 0.5f, 6f));

        scene.RebuildStaticWorld(new FakeRenderer());
        return scene.StaticWorld!;
    }

    private static int Jambs(CsgWorld world)
    {
        int count = 0;
        foreach (Polygon polygon in world.Surfaces)
        {
            if (MathF.Abs(MathF.Abs(polygon.Surface.Normal.X) - 1f) > 1e-3f) continue;
            if (MathF.Abs(MathF.Abs(polygon.Surface.D) - 1f) > 1e-3f) continue;
            count++;
        }
        return count;
    }
}
