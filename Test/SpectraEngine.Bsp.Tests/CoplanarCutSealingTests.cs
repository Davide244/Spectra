using System;
using System.Numerics;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// A doorway cut to the base of the wall it stands in, which used to compile as
/// solid.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect took two conditions together, and an earlier version of these
/// remarks named only one of them.</b> The subtractive brush had to cut flush
/// through the wall's own bottom plane, so that the opening reached the wall's
/// base; AND another additive brush, the floor, had to reach that same plane.
/// Move either one by a hundredth of a unit and the opening compiled correctly.
/// </para>
/// <para>
/// <b>The cause was a hole in the skin, not a mistake in the subtraction.</b>
/// Both jamb faces were emitted the whole time. What went missing was the single
/// square unit of threshold under the doorway: the wall's own bottom face was
/// deleted over the cut by the flush-through-cut rule, and the cavity wall that
/// should have replaced it was killed at seeding, because a seed lying on one of
/// the cut brush's own planes is coplanar and <c>Split</c> reports it on the
/// front. Nobody was left to bound the cavity, so the solid reconstruction
/// leaked through the hole and joined the floor's interior to the doorway.
/// </para>
/// <para>
/// Fixed in <c>Csg.CarveFragment</c>, which now re-emits the hollowed remainder
/// when a fragment was buried across a coincident plane and rests on a cavity
/// wall the seeding could not keep. <see cref="CsgClosureTests"/> carries the
/// oracle that found it and would catch the next one.
/// </para>
/// </remarks>
public sealed class CoplanarCutSealingTests
{
    [Fact]
    public void A_doorway_cut_to_the_base_of_its_wall_is_open()
    {
        Assert.False(Build(floorTouching: true).ContainsPoint(new Vector3(0f, 1.2f, -4.25f)));
    }

    [Fact]
    public void The_same_doorway_with_the_floor_a_hair_lower_is_open()
    {
        // The control that isolated the second condition. It passed throughout,
        // and it is what pinned the trigger to the floor reaching the wall's base
        // rather than to the cut, the wall, or the flush z planes, all of which
        // are identical between the two arrangements.
        Assert.False(Build(floorTouching: false).ContainsPoint(new Vector3(0f, 1.2f, -4.25f)));
    }

    [Fact]
    public void Either_way_the_carve_emits_both_jambs()
    {
        // Evidence that the subtraction itself always ran. The cavity walls on
        // the cut's own x planes were present in both arrangements even while one
        // of them compiled solid, which is what ruled out the carve early and
        // sent the search to the skin instead.
        Assert.Equal(2, Jambs(Build(floorTouching: true)));
        Assert.Equal(2, Jambs(Build(floorTouching: false)));
    }

    [Fact]
    public void The_threshold_under_the_doorway_exists()
    {
        // The surface whose absence was the whole defect: one upward-facing
        // square unit at the wall's base, spanning the opening.
        CsgWorld world = Build(floorTouching: true);

        int thresholds = 0;
        foreach (Polygon polygon in world.Surfaces)
        {
            if (polygon.Surface.Normal.Y < 0.99f)
                continue;
            if (MathF.Abs(polygon.Bounds.Min.Y) > 1e-3f)
                continue;

            // Inside the doorway's footprint: x within +/-1, z across the wall.
            if (polygon.Bounds.Min.X >= -1.001f && polygon.Bounds.Max.X <= 1.001f &&
                polygon.Bounds.Min.Z >= -4.501f && polygon.Bounds.Max.Z <= -3.999f)
            {
                thresholds++;
            }
        }

        Assert.True(thresholds >= 1,
            "the doorway has no floor at its base, so the compiled skin is open there");
    }

    public static CsgWorld Build(bool floorTouching)
    {
        var scene = new Scene("CoplanarCut");

        void Box(string name, Vector3 center, Vector3 half, bool cut = false)
        {
            SceneNode node = scene.Root.CreateChild(name);
            node.LocalPosition = center;
            Brush brush = Brush.CreateBox(-half, half);
            node.Brush = cut ? brush.WithOperation(BrushOperation.Subtractive) : brush;
        }

        // Wall y in [0, 3]; the cut reaches its base at y = 0 and passes flush
        // through its thickness in z. The floor's top is either exactly 0 or a
        // hundredth below it.
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
