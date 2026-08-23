using Xunit;
using System;
using System.IO;
using SpectraEngine.Core.Physics;
using SpectraEngine.Physics.Box3D.Native;

namespace SpectraEngine.Physics.Tests;

/// <summary>
/// The binding against the real library: does a world come up, step at a fixed
/// timestep, and go down again.
/// </summary>
/// <remarks>
/// <para>
/// <b>These need <c>box3d.dll</c>, which the ABI tests deliberately do not.</b>
/// The DLL is build output of the pinned submodule rather than a committed
/// binary, so a fresh clone that has not run <c>native/build-box3d.ps1</c> does
/// not have one. When it is missing these <em>skip with the reason and the
/// command</em> — a named skip, visible in the run summary. They must never
/// quietly pass, which would report a green binding nobody exercised.
/// </para>
/// </remarks>
[Collection(NativeWorldCollection.Name)]
public sealed class Box3DWorldTests
{
    private static bool NativeAvailable =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "box3d.dll"));

    private static void RequireNative() =>
        Assert.SkipWhen(
            !NativeAvailable,
            "box3d.dll is not present beside the test binary. It is build output of the " +
            "pinned submodule, not a committed binary — build it with: native/build-box3d.ps1");

    [Fact]
    public void The_loaded_library_is_the_float_build()
    {
        // The single most important assertion in this file. Every managed struct
        // in the binding assumes the float build; a double DLL silently widens
        // positions and every struct containing one. This is the only runtime
        // proof that the library on disk matches the layouts in the manifest.
        RequireNative();

        B3.IsDoublePrecision().ShouldBeFalse(
            "the loaded box3d.dll was built with BOX3D_DOUBLE_PRECISION, which invalidates " +
            "every struct layout this binding declares");
    }

    [Fact]
    public void The_library_reports_its_own_version()
    {
        // Also the cheapest end-to-end proof that a struct RETURNED BY VALUE
        // crosses the boundary correctly: 12 bytes comes back through a hidden
        // return pointer, and getting that wrong would garble this.
        RequireNative();

        B3Version version = B3.GetVersion();

        version.Major.ShouldBe(0);
        version.Minor.ShouldBe(2, "the pinned commit is past the v0.1.0 tag — trust the library, not upstream's CMakeLists");
    }

    [Fact]
    public void One_world_unit_is_one_metre()
    {
        // The units decision, exercised against the real library rather than
        // asserted in a doc.
        RequireNative();

        B3.SetLengthUnitsPerMeter(PhysicsDefaults.MetresPerUnit);

        B3.GetLengthUnitsPerMeter().ShouldBe(1f);
    }

    [Fact]
    public void A_default_world_def_arrives_serial()
    {
        // The library's three-way branch takes the external-task path when the
        // worker count is non-zero AND both callbacks are non-null, and spawns
        // its own OS threads when the count exceeds one. The default def sits in
        // neither case — and this test is what notices if that ever changes
        // under a moved pin.
        RequireNative();

        B3WorldDef def = B3.DefaultWorldDef();

        def.WorkerCount.ShouldBe(0u);
        def.EnqueueTask.ShouldBe(0);
        def.FinishTask.ShouldBe(0);
        def.InternalValue.ShouldNotBe(0, "the construction cookie must arrive set — never hand-build a def");
    }

    [Fact]
    public void A_world_comes_up_steps_and_goes_down()
    {
        RequireNative();

        B3.SetLengthUnitsPerMeter(PhysicsDefaults.MetresPerUnit);
        B3WorldDef def = B3.DefaultWorldDef();
        def.Gravity = B3Vec3.From(PhysicsDefaults.Gravity);

        int worldsBefore = B3.GetWorldCount();
        B3WorldId world = B3.CreateWorld(in def);

        try
        {
            world.Index1.ShouldNotBe((ushort)0, "a zeroed id is how world creation reports failure in Release");
            B3.World_IsValid(world).ShouldBeTrue();
            B3.GetWorldCount().ShouldBe(worldsBefore + 1);

            // The serial branch is observable, so assert it rather than trusting
            // the def: a world that quietly spawned threads would still pass
            // every other test here.
            B3.World_GetWorkerCount(world).ShouldBe(1);

            B3.World_GetGravity(world).ToVector3().Y.ShouldBe(PhysicsDefaults.Gravity.Y, 1e-4f);

            // Fixed step, always — the frame delta must never reach here.
            for (int tick = 0; tick < 10; tick++)
                B3.World_Step(world, PhysicsDefaults.FixedDeltaTime, 4);

            B3.World_IsValid(world).ShouldBeTrue("stepping an empty world must not invalidate it");
        }
        finally
        {
            B3.DestroyWorld(world);
        }

        B3.World_IsValid(world).ShouldBeFalse();
        B3.GetWorldCount().ShouldBe(worldsBefore, "the world count must return to where it started");
    }

    [Fact]
    public void Gravity_survives_a_round_trip_through_the_boundary()
    {
        // b3Vec3 passes by value in and comes back by value out. A layout or
        // convention mistake here would show up as gravity pointing somewhere
        // unexpected — which reads as a physics tuning problem, not a binding
        // one, and is therefore worth pinning.
        RequireNative();

        B3WorldDef def = B3.DefaultWorldDef();
        B3WorldId world = B3.CreateWorld(in def);

        try
        {
            var expected = new B3Vec3(1.5f, -9.81f, 0.25f);
            B3.World_SetGravity(world, expected);

            B3Vec3 actual = B3.World_GetGravity(world);

            actual.X.ShouldBe(expected.X, 1e-6f);
            actual.Y.ShouldBe(expected.Y, 1e-6f);
            actual.Z.ShouldBe(expected.Z, 1e-6f);
        }
        finally
        {
            B3.DestroyWorld(world);
        }
    }
}
