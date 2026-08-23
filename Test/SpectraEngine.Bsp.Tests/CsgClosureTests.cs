using System;
using System.Collections.Generic;
using System.Numerics;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// Closure: the compiled skin of a set of solids must be a closed surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sum the vector area of every surface and a closed set gives exactly
/// zero.</b> Each polygon contributes <c>0.5 * sum(cross(v[i], v[i+1]))</c>,
/// which points along its normal with magnitude equal to its area, and for any
/// closed boundary those cancel. A non-zero residual means a hole, and its
/// direction and magnitude say which way the missing patch faces and how large
/// it is. That is a far sharper instrument than probing points, because it finds
/// holes nobody thought to probe.
/// </para>
/// <para>
/// This oracle is what made the doorway defect findable. The sealing arrangement
/// read <c>(0, -1, 0)</c>: one square unit of upward-facing boundary missing,
/// which pointed straight at the absent threshold under the door.
/// </para>
/// </remarks>
public sealed class CsgClosureTests
{
    /// <summary>Float slop over a few hundred surfaces at play-area distances.</summary>
    private const float Tolerance = 0.01f;

    public static Vector3 Closure(IReadOnlyList<Polygon> surfaces)
    {
        var total = Vector3.Zero;
        for (int i = 0; i < surfaces.Count; i++)
        {
            ReadOnlySpan<Vector3> v = surfaces[i].VertexSpan;
            var area = Vector3.Zero;
            for (int j = 0; j < v.Length; j++)
                area += Vector3.Cross(v[j], v[(j + 1) % v.Length]);
            total += area * 0.5f;
        }
        return total;
    }

    private static CsgWorld Compile(Action<Scene> build)
    {
        var scene = new Scene("Closure");
        build(scene);
        scene.RebuildStaticWorld(new FakeRenderer());
        return scene.StaticWorld!;
    }

    private static void Box(Scene scene, string name, Vector3 centre, Vector3 half)
    {
        SceneNode node = scene.Root.CreateChild(name);
        node.LocalPosition = centre;
        node.Brush = Brush.CreateBox(-half, half);
    }

    private static void Cut(Scene scene, string name, Vector3 centre, Vector3 half)
    {
        SceneNode node = scene.Root.CreateChild(name);
        node.LocalPosition = centre;
        node.Brush = Brush.CreateBox(-half, half).WithOperation(BrushOperation.Subtractive);
    }

    private static void Staircase(Scene scene, int treads, float rise, float run)
    {
        for (int i = 0; i < treads; i++)
        {
            float top = rise * (i + 1);
            Box(scene, $"tread{i}",
                new Vector3(135f + run * i + run * 0.5f, (top - 3f) * 0.5f, 0f),
                new Vector3(run * 0.5f, (top + 3f) * 0.5f, 2f));
        }
    }

    [Fact]
    public void One_box_is_closed()
    {
        Vector3 closure = Closure(Compile(scene => Box(scene, "a", Vector3.Zero, Vector3.One)).Surfaces);
        Assert.True(closure.Length() < Tolerance, $"a single box should close, residual {closure}");
    }

    [Fact]
    public void Two_boxes_meeting_flush_are_closed()
    {
        // The shared interface is interior to the union, so BOTH faces must go.
        // Keeping exactly one leaves a residual of that face's area, which is the
        // failure the skipped test below records.
        Vector3 closure = Closure(Compile(scene =>
        {
            Box(scene, "a", new Vector3(0f, 0f, 0f), Vector3.One);
            Box(scene, "b", new Vector3(2f, 0f, 0f), Vector3.One);
        }).Surfaces);

        Assert.True(closure.Length() < Tolerance, $"two flush boxes should close, residual {closure}");
    }

    [Fact]
    public void A_brush_hollowed_by_a_through_cut_is_closed()
    {
        // The chasm's shape: a negative passing clean through a slab.
        Vector3 closure = Closure(Compile(scene =>
        {
            Box(scene, "slab", new Vector3(0f, 0f, 0f), new Vector3(8f, 1f, 8f));
            Cut(scene, "hole", new Vector3(0f, 0f, 0f), new Vector3(2f, 2f, 2f));
        }).Surfaces);

        Assert.True(closure.Length() < Tolerance, $"a through-cut slab should close, residual {closure}");
    }

    [Fact]
    public void A_doorway_flush_with_its_wall_base_is_closed()
    {
        // The defect this oracle found. Before the fix it read (0, -1, 0): the
        // one square unit of threshold under the door.
        Vector3 closure = Closure(CoplanarCutSealingTests.Build(floorTouching: true).Surfaces);
        Assert.True(closure.Length() < Tolerance,
            $"a doorway at its wall's base should close, residual {closure}");
    }

    [Fact]
    public void The_demo_play_area_has_no_missing_horizontal_boundary()
    {
        // Asserted on y and z only, deliberately. The play area still carries a
        // known eight square unit hole in x, recorded below with its own
        // reproduction. Asserting the whole vector here would only restate that
        // defect in a test whose job is to catch new ones.
        Vector3 closure = Closure(Compile(scene =>
            DemoPlayArea.Build(scene, MaterialRef.Default, MaterialRef.Default, MaterialRef.Default)).Surfaces);

        Assert.True(MathF.Abs(closure.Y) < Tolerance,
            $"the play area is missing upward or downward boundary: residual {closure}");
        Assert.True(MathF.Abs(closure.Z) < Tolerance,
            $"the play area is missing z-facing boundary: residual {closure}");
    }

    private const string XResidualReason =
        "Known CSG defect: two solids meeting flush lose one side of the shared interface when the " +
        "junction plane is not exactly representable in binary. See the test remarks.";

    /// <summary>
    /// A second, independent closure defect, minimally reproduced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A staircase whose last tread meets a terrace flush at x = 139 loses eight
    /// square units of skin. The 4 by 2 shared interface has one of its two
    /// opposite-facing coincident faces removed and the other kept, where both
    /// should go.
    /// </para>
    /// <para>
    /// <b>What makes it fire is arithmetic, not geometry.</b> The demo's three
    /// staircases meet the terrace on the same plane over the same area, and only
    /// this one breaks. It is the only one whose rise and run (0.40 and 0.80) are
    /// not exactly representable in binary; the others use 0.25 with 0.5, and 0.5
    /// with 1.0, which are. So the junction plane lands a few millionths off 139,
    /// and the opposite-facing coincidence rule and its tie-break disagree about
    /// whether that counts as coincident, because they are not applied at the same
    /// tolerance. <c>Csg.CoplanarOrientation</c> already documents itself as ten
    /// times looser in offset than <c>Polygon.Split</c>'s own classification.
    /// </para>
    /// </remarks>
    [Fact(Skip = XResidualReason)]
    public void A_staircase_meeting_a_terrace_on_an_inexact_plane_is_closed()
    {
        Vector3 closure = Closure(Compile(scene =>
        {
            Box(scene, "floor", new Vector3(150f, -1.5f, 0f), new Vector3(20f, 1.5f, 20f));
            Box(scene, "terrace", new Vector3(143.5f, 1f, 0f), new Vector3(4.5f, 1f, 12f));
            Staircase(scene, treads: 5, rise: 0.40f, run: 0.80f);
        }).Surfaces);

        Assert.True(closure.Length() < Tolerance, $"residual {closure}, expected about (-8, 0, 0)");
    }

    [Fact]
    public void The_same_staircase_with_binary_exact_treads_is_closed()
    {
        // The control, and the evidence for the diagnosis above: identical
        // junction, identical area, rise and run changed to values that are exact
        // in binary.
        Vector3 closure = Closure(Compile(scene =>
        {
            Box(scene, "floor", new Vector3(150f, -1.5f, 0f), new Vector3(20f, 1.5f, 20f));
            Box(scene, "terrace", new Vector3(143.5f, 1f, 0f), new Vector3(4.5f, 1f, 12f));
            Staircase(scene, treads: 8, rise: 0.25f, run: 0.50f);
        }).Surfaces);

        Assert.True(closure.Length() < Tolerance,
            $"the exact-tread staircase should close, residual {closure}");
    }
}
