using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Physics;
using SpectraEngine.Physics.Box3D;
using SpectraEngine.Physics.Box3D.Native;

namespace SpectraEngine.Physics.Tests;

/// <summary>
/// Brushes becoming collision hulls — the join between the two halves of the
/// engine, and the place where a quiet mistake becomes a player walking through
/// a wall that renders correctly.
/// </summary>
public sealed class BrushHullTests
{
    private static bool NativeAvailable =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "box3d.dll"));

    private static void RequireNative() =>
        Assert.SkipWhen(
            !NativeAvailable,
            "box3d.dll is not present beside the test binary — build it with: native/build-box3d.ps1");

    // --- Point collection (no native library needed) ------------------------

    [Fact]
    public void A_box_brush_yields_its_eight_corners_once_each()
    {
        // Every corner is shared by three faces, so the raw vertex stream repeats
        // each one three times. The pre-check counts UNIQUE vertices, so the
        // welding has to happen before it — this is that.
        Brush box = Brush.CreateBox(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f));

        List<B3Vec3> points = BrushHullBuilder.CollectPoints(box);

        points.Count.ShouldBe(8);
        box.LocalFaces.Count.ShouldBe(6);
    }

    [Fact]
    public void A_box_brush_is_comfortably_inside_every_limit()
    {
        // V=8, F=6, so E=12 — two orders of magnitude under the caps. Recorded
        // so the numbers in the refusal messages have a reference point.
        Brush box = Brush.CreateBox(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f));

        BrushHullBuilder.CheckLimits(8, 6).ShouldBe(HullRefusal.None);
        BrushHullBuilder.CheckLimits(
            BrushHullBuilder.CollectPoints(box).Count, box.LocalFaces.Count)
            .ShouldBe(HullRefusal.None);
    }

    [Fact]
    public void The_edge_limit_binds_before_the_vertex_and_face_limits_do()
    {
        // The finding this whole pre-check exists for. Both counts are legal on
        // their own; their SUM is not, because the library's real check is on
        // edges and Euler ties the three together.
        BrushHullBuilder.CheckLimits(100, 100).ShouldBe(HullRefusal.TooManyEdges);

        // Under the vertex cap and under the face cap individually...
        (100 <= BrushHullBuilder.MaxVertices).ShouldBeTrue();
        (100 <= BrushHullBuilder.MaxFaces).ShouldBeTrue();
        // ...yet over the one that actually decides.
        (100 + 100 > BrushHullBuilder.MaxVerticesPlusFaces).ShouldBeTrue();
    }

    [Fact]
    public void Each_limit_is_reported_as_itself()
    {
        BrushHullBuilder.CheckLimits(3, 6).ShouldBe(HullRefusal.Degenerate);
        BrushHullBuilder.CheckLimits(8, 3).ShouldBe(HullRefusal.Degenerate);
        BrushHullBuilder.CheckLimits(200, 6).ShouldBe(HullRefusal.TooManyVertices);
        BrushHullBuilder.CheckLimits(8, 200).ShouldBe(HullRefusal.TooManyFaces);
        BrushHullBuilder.CheckLimits(66, 66).ShouldBe(HullRefusal.TooManyEdges);
    }

    // --- Against the real library -------------------------------------------

    [Fact]
    public void A_box_brush_becomes_a_hull()
    {
        RequireNative();
        Brush box = Brush.CreateBox(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f));

        HullRefusal refusal = BrushHullBuilder.TryCreate(box, out nint hull, out string detail);

        try
        {
            refusal.ShouldBe(HullRefusal.None, detail);
            hull.ShouldNotBe(0);
        }
        finally
        {
            BrushHullBuilder.Destroy(hull);
        }
    }

    [Fact]
    public void The_hull_bounds_match_the_brush_it_came_from()
    {
        // The strongest cheap check that the POINTS crossed the boundary
        // correctly: a wrong layout or a wrong count would still produce a hull,
        // just not this one.
        RequireNative();
        Brush box = Brush.CreateBox(new Vector3(-2f, -0.5f, -3f), new Vector3(2f, 0.5f, 3f));

        BrushHullBuilder.TryCreate(box, out nint hull, out string detail)
            .ShouldBe(HullRefusal.None, detail);

        try
        {
            var identity = new B3Transform { P = default, Q = B3Quat.From(Quaternion.Identity) };
            B3Aabb bounds = B3.ComputeHullAABB(hull, identity);

            bounds.LowerBound.X.ShouldBe(-2f, 1e-3f);
            bounds.LowerBound.Y.ShouldBe(-0.5f, 1e-3f);
            bounds.LowerBound.Z.ShouldBe(-3f, 1e-3f);
            bounds.UpperBound.X.ShouldBe(2f, 1e-3f);
            bounds.UpperBound.Y.ShouldBe(0.5f, 1e-3f);
            bounds.UpperBound.Z.ShouldBe(3f, 1e-3f);
        }
        finally
        {
            BrushHullBuilder.Destroy(hull);
        }
    }

    [Fact]
    public void A_wedge_brush_becomes_a_hull_too()
    {
        // Brushes are arbitrary convex plane sets, not just boxes. A wedge is
        // V=6, F=5 — and it exercises a face that is a triangle rather than a
        // quad.
        RequireNative();
        var planes = new[]
        {
            new Plane(new Vector3(0f, -1f, 0f), -1f),
            new Plane(new Vector3(0f, 0f, -1f), -1f),
            new Plane(new Vector3(0f, 0f, 1f), -1f),
            new Plane(new Vector3(-1f, 0f, 0f), -1f),
            Plane.Normalize(new Plane(new Vector3(1f, 1f, 0f), -1.4142f)),
        };
        var wedge = new Brush(planes);

        HullRefusal refusal = BrushHullBuilder.TryCreate(wedge, out nint hull, out string detail);

        try
        {
            refusal.ShouldBe(HullRefusal.None, detail);
            hull.ShouldNotBe(0);
        }
        finally
        {
            BrushHullBuilder.Destroy(hull);
        }
    }

    [Fact]
    public void Destroying_a_null_hull_is_safe_here_even_though_it_is_not_in_the_library()
    {
        // b3DestroyHull dereferences its argument before any null check, so this
        // guard is what lets callers treat "no hull" as an ordinary state rather
        // than a branch they must remember.
        RequireNative();

        Should.NotThrow(() => BrushHullBuilder.Destroy(0));
    }

    [Fact]
    public void A_hull_attaches_to_a_static_body_and_the_world_steps_with_it()
    {
        // End to end: brush -> points -> hull -> shape on a static body -> a
        // world that steps. This is the shape the static-world sync takes, one
        // chunk cell at a time.
        RequireNative();
        B3.SetLengthUnitsPerMeter(PhysicsDefaults.MetresPerUnit);

        B3WorldDef worldDef = B3.DefaultWorldDef();
        worldDef.Gravity = B3Vec3.From(PhysicsDefaults.Gravity);
        B3WorldId world = B3.CreateWorld(in worldDef);
        nint hull = 0;

        try
        {
            world.Index1.ShouldNotBe((ushort)0);

            Brush floor = Brush.CreateBox(new Vector3(-8f, -0.5f, -8f), new Vector3(8f, 0.5f, 8f));
            BrushHullBuilder.TryCreate(floor, out hull, out string detail)
                .ShouldBe(HullRefusal.None, detail);

            B3BodyDef bodyDef = B3.DefaultBodyDef();
            bodyDef.Type = B3BodyType.Static;
            B3BodyId body = B3.CreateBody(world, in bodyDef);
            body.Index1.ShouldNotBe(0);
            B3.Body_GetType(body).ShouldBe(B3BodyType.Static);

            B3ShapeDef shapeDef = B3.DefaultShapeDef();
            B3ShapeId shape = B3.CreateHullShape(body, in shapeDef, hull);

            shape.Index1.ShouldNotBe(0, "a zeroed shape id is how attachment reports failure");
            B3.Shape_IsValid(shape).ShouldBeTrue();
            B3.Shape_GetBody(shape).Index1.ShouldBe(body.Index1);
            B3.Body_GetShapeCount(body).ShouldBe(1);

            for (int tick = 0; tick < 10; tick++)
                B3.World_Step(world, PhysicsDefaults.FixedDeltaTime, 4);

            B3.Shape_IsValid(shape).ShouldBeTrue("stepping must not invalidate a static shape");
        }
        finally
        {
            BrushHullBuilder.Destroy(hull);
            B3.DestroyWorld(world);
        }
    }

    [Fact]
    public void A_shape_outlives_the_hull_it_was_built_from()
    {
        // THE QUESTION THIS SETTLES: does attaching a hull COPY it into the
        // world, or does the shape keep pointing at our allocation? The answer
        // decides whether the static-world sync can free hulls at the end of a
        // sync or must refcount them for the lifetime of every shape — a real
        // memory cost across a large map.
        //
        // The experiment: destroy the hull BEFORE stepping, then drop a box on
        // the shape. If the shape were still referencing freed memory this
        // would fall through, land wrong, or crash.
        RequireNative();
        B3.SetLengthUnitsPerMeter(PhysicsDefaults.MetresPerUnit);

        B3WorldDef worldDef = B3.DefaultWorldDef();
        worldDef.Gravity = B3Vec3.From(PhysicsDefaults.Gravity);
        B3WorldId world = B3.CreateWorld(in worldDef);

        try
        {
            Brush floorBrush = Brush.CreateBox(new Vector3(-8f, -0.5f, -8f), new Vector3(8f, 0.5f, 8f));
            BrushHullBuilder.TryCreate(floorBrush, out nint floorHull, out string detail)
                .ShouldBe(HullRefusal.None, detail);

            B3BodyDef floorDef = B3.DefaultBodyDef();
            floorDef.Type = B3BodyType.Static;
            floorDef.Position = new B3Pos(0f, -0.5f, 0f);
            B3BodyId floorBody = B3.CreateBody(world, in floorDef);
            B3ShapeDef floorShapeDef = B3.DefaultShapeDef();
            B3ShapeId floorShape = B3.CreateHullShape(floorBody, in floorShapeDef, floorHull);
            floorShape.Index1.ShouldNotBe(0);

            // The whole point: released while the shape is still in use.
            BrushHullBuilder.Destroy(floorHull);

            Brush boxBrush = Brush.CreateBox(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, 0.5f, 0.5f));
            BrushHullBuilder.TryCreate(boxBrush, out nint boxHull, out string boxDetail)
                .ShouldBe(HullRefusal.None, boxDetail);
            B3BodyDef boxDef = B3.DefaultBodyDef();
            boxDef.Type = B3BodyType.Dynamic;
            boxDef.Position = new B3Pos(0f, 4f, 0f);
            B3BodyId boxBody = B3.CreateBody(world, in boxDef);
            B3ShapeDef boxShapeDef = B3.DefaultShapeDef();
            B3.CreateHullShape(boxBody, in boxShapeDef, boxHull).Index1.ShouldNotBe(0);
            B3.Body_ApplyMassFromShapes(boxBody);
            BrushHullBuilder.Destroy(boxHull);

            for (int tick = 0; tick < PhysicsDefaults.TicksPerSecond * 3; tick++)
                B3.World_Step(world, PhysicsDefaults.FixedDeltaTime, 4);

            B3.Body_GetTransform(boxBody).P.Y.ShouldBeInRange(
                0.4f, 0.6f,
                "the box fell through or landed wrong, so a shape does NOT copy its hull — " +
                "the sync must keep hulls alive for the lifetime of every shape built from them");
        }
        finally
        {
            B3.DestroyWorld(world);
        }
    }

    [Fact]
    public void A_dynamic_body_falls_and_comes_to_rest_on_a_brush_floor()
    {
        // The one that proves the whole chain does something physical: authored
        // brush geometry, a real solver, gravity in spectraunits, and a fixed
        // timestep. If the units decision were wrong this would settle at the
        // wrong height or take the wrong time.
        RequireNative();
        B3.SetLengthUnitsPerMeter(PhysicsDefaults.MetresPerUnit);

        B3WorldDef worldDef = B3.DefaultWorldDef();
        worldDef.Gravity = B3Vec3.From(PhysicsDefaults.Gravity);
        B3WorldId world = B3.CreateWorld(in worldDef);
        nint floorHull = 0;
        nint boxHull = 0;

        try
        {
            // The floor brush is authored SYMMETRIC about its own origin and
            // placed by the BODY, which is how the real path works: the node's
            // world matrix is a brush's placement and Brush.Transform is ignored
            // for node-attached brushes. Authoring it as [-1, 0] and expecting
            // the hull to carry that offset is the trap — CreateBox centres the
            // solid and banks the translation in a property nothing here reads.
            Brush floorBrush = Brush.CreateBox(new Vector3(-8f, -0.5f, -8f), new Vector3(8f, 0.5f, 8f));
            BrushHullBuilder.TryCreate(floorBrush, out floorHull, out string floorDetail)
                .ShouldBe(HullRefusal.None, floorDetail);

            B3BodyDef floorDef = B3.DefaultBodyDef();
            floorDef.Type = B3BodyType.Static;
            floorDef.Position = new B3Pos(0f, -0.5f, 0f);   // top face lands on y = 0
            B3BodyId floorBody = B3.CreateBody(world, in floorDef);
            B3ShapeDef floorShape = B3.DefaultShapeDef();
            B3.CreateHullShape(floorBody, in floorShape, floorHull).Index1.ShouldNotBe(0);

            // A half-unit cube dropped from 5 units up.
            Brush boxBrush = Brush.CreateBox(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, 0.5f, 0.5f));
            BrushHullBuilder.TryCreate(boxBrush, out boxHull, out string boxDetail)
                .ShouldBe(HullRefusal.None, boxDetail);

            B3BodyDef boxDef = B3.DefaultBodyDef();
            boxDef.Type = B3BodyType.Dynamic;
            boxDef.Position = new B3Pos(0f, 5f, 0f);
            B3BodyId boxBody = B3.CreateBody(world, in boxDef);
            B3ShapeDef boxShape = B3.DefaultShapeDef();
            B3.CreateHullShape(boxBody, in boxShape, boxHull).Index1.ShouldNotBe(0);
            B3.Body_ApplyMassFromShapes(boxBody);

            // Three seconds of fixed ticks: ~1 s to fall 5 units under 9.81,
            // plus plenty to settle and sleep.
            for (int tick = 0; tick < PhysicsDefaults.TicksPerSecond * 3; tick++)
                B3.World_Step(world, PhysicsDefaults.FixedDeltaTime, 4);

            float restY = B3.Body_GetTransform(boxBody).P.Y;

            // Half-extent 0.5 resting on a floor whose top is y = 0, allowing
            // for the solver's contact slop.
            restY.ShouldBeInRange(0.4f, 0.6f);
        }
        finally
        {
            BrushHullBuilder.Destroy(floorHull);
            BrushHullBuilder.Destroy(boxHull);
            B3.DestroyWorld(world);
        }
    }
}
