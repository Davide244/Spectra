using System;
using System.Numerics;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Physics;
using SpectraEngine.Core.Physics.Character;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The demo's obstacle course, walked headlessly by the real mover over the
/// real compiled world.
/// </summary>
/// <remarks>
/// <para>
/// <b>These test the CONTENT, which nothing else does.</b>
/// <see cref="CharacterMoverTests"/> builds one-box worlds to pin the mover's
/// behaviour; these build the actual course a person is going to walk and ask
/// whether it works. Both kinds are needed and they fail differently: a mover
/// regression breaks the first set, and a course authored a hair too tight, too
/// tall or too steep breaks only this one — silently, as a door you cannot fit
/// through or a stair you cannot climb, which is exactly the kind of thing that
/// gets discovered by a human twenty minutes into playing.
/// </para>
/// <para>
/// Every assertion is about a number that was chosen in
/// <see cref="DemoPlayArea"/> against a tuning default. If a tuning default
/// moves, these are the tests that say which parts of the level stopped being
/// usable.
/// </para>
/// </remarks>
public sealed class DemoPlayAreaTests
{
    private const float Dt = PhysicsDefaults.FixedDeltaTime;

    // Yaw is (cos, 0, sin), so 0 walks +x and π/2 walks +z.
    private const float East = 0f;
    private const float West = MathF.PI;
    private const float South = MathF.PI / 2f;
    private const float North = -MathF.PI / 2f;

    // --- The ground the whole course stands on -------------------------------

    [Fact]
    public void The_spawn_point_is_standing_on_solid_ground()
    {
        var course = new Course();
        CharacterState state = course.Spawn();
        state = course.Settle(state, 30);

        Assert.True(state.Grounded, "the character should be standing at the spawn point");

        // A resting character sits exactly one skin width above the surface, by
        // contract: the sweep stops where separation equals SkinWidth, so the
        // mover never has to do backoff arithmetic of its own.
        Assert.Equal(course.Tuning.SkinWidth, state.Position.Y, 4);
    }

    [Fact]
    public void The_perimeter_wall_stops_a_character_walking_off_the_edge()
    {
        var course = new Course();
        CharacterState state = course.Settle(course.Spawn(), 10);

        // West from the spawn is the boundary, four units away.
        state = course.Walk(state, West, 120);

        Assert.True(state.Position.X > 130.5f,
            $"the west wall should have stopped the character, but it reached x={state.Position.X:0.000}");
        Assert.True(state.Grounded, "the character should still be on the floor at the wall");
    }

    // --- Stairs: two must climb, one must refuse ------------------------------
    //
    // All three staircases climb 2.0 over 4.0 of run and differ only in rise, so
    // these three tests together bracket StepHeight rather than merely sampling
    // near it.

    [Theory]
    [InlineData(-8f, "gentle (0.25 rise)")]
    [InlineData(0f, "at the limit (0.40 rise)")]
    public void A_staircase_within_the_step_height_is_climbed(float z, string description)
    {
        var course = new Course();
        CharacterState state = course.Settle(Course.SpawnAt(new Vector3(133f, 0.05f, z)), 10);

        state = course.Walk(state, East, 150);

        Assert.True(state.Position.Y > 1.9f,
            $"the {description} staircase should have been climbed to the terrace, " +
            $"but the character reached only y={state.Position.Y:0.000}");
    }

    [Fact]
    public void A_staircase_above_the_step_height_refuses_to_be_climbed()
    {
        var course = new Course();
        CharacterState state = course.Settle(Course.SpawnAt(new Vector3(133f, 0.05f, 8f)), 10);

        state = course.Walk(state, East, 150);

        // A 0.50 riser against a 0.45 step height: the first one must stop the
        // character dead. It may ride up nothing at all, so the assertion is that
        // it never reaches the second tread.
        Assert.True(state.Position.Y < 0.5f,
            $"a 0.50 riser exceeds the 0.45 step height and must not be climbable, " +
            $"but the character reached y={state.Position.Y:0.000}");
        Assert.True(state.Position.X < 136.5f,
            $"the character should be stopped at the first riser, not past it at x={state.Position.X:0.000}");
    }

    // --- The doorway: the reason the source is a plane set --------------------

    [Fact]
    public void The_terrace_doorway_can_be_walked_through()
    {
        var course = new Course();

        // On the terrace, west of the wall, lined up with the opening.
        CharacterState state = course.Settle(Course.SpawnAt(new Vector3(141.5f, 2.05f, 0f)), 20);
        Assert.True(state.Grounded, "the character should be standing on the terrace");

        state = course.Walk(state, East, 90);

        Assert.True(state.Position.X > 144.5f,
            $"the doorway is 1.4 x 2.2 and the character is 0.7 across and 1.8 tall, so it must pass " +
            $"through — it stopped at x={state.Position.X:0.000}");
        Assert.True(state.Grounded, "the character should still be on the terrace after the doorway");
    }

    [Fact]
    public void The_wall_beside_the_doorway_is_solid()
    {
        var course = new Course();
        CharacterState state = course.Settle(Course.SpawnAt(new Vector3(141.5f, 2.05f, 4f)), 20);

        state = course.Walk(state, East, 90);

        // The wall's west face is at x = 143.3; a 0.35 radius plus a skin stops
        // the centre a shade before it.
        Assert.True(state.Position.X < 143.0f,
            $"the wall beside the opening must block, but the character reached x={state.Position.X:0.000}");
    }

    [Fact]
    public void The_tunnel_has_a_ceiling_that_is_solid()
    {
        var course = new Course();

        // Inside the tunnel, which is 2.2 tall with a subtractive brush's cavity
        // wall above it rather than open sky.
        CharacterState state = course.Settle(Course.SpawnAt(new Vector3(146f, 2.05f, 8f)), 20);
        Assert.True(state.Grounded, "the character should be standing inside the tunnel");

        // Jump: 1.2 of jump height against 0.4 of headroom means the ceiling has
        // to stop it. Without one, the character sails up through the block.
        state = course.Step(state, East, jump: true);
        for (int i = 0; i < 40; i++)
            state = course.Step(state, East);

        Assert.True(state.Position.Y < 2.6f,
            $"the tunnel ceiling must stop a jump inside it, but the character reached " +
            $"y={state.Position.Y:0.000}");
    }

    // --- Ramps: two must walk, one must refuse --------------------------------

    [Theory]
    [InlineData(-17f, 25)]
    [InlineData(-13f, 40)]
    public void A_ramp_within_the_slope_limit_can_be_walked_up(float z, int degrees)
    {
        var course = new Course();
        CharacterState state = course.Settle(Course.SpawnAt(new Vector3(150f, 0.05f, z)), 10);

        state = course.Walk(state, East, 150);

        Assert.True(state.Position.Y > 1.9f,
            $"the {degrees} degree ramp is inside the 46 degree slope limit and must be walkable, " +
            $"but the character reached only y={state.Position.Y:0.000}");
        Assert.True(state.Grounded, "the character should be standing on the platform at the top");
    }

    [Fact]
    public void A_ramp_beyond_the_slope_limit_is_not_walkable()
    {
        var course = new Course();
        CharacterState state = course.Settle(Course.SpawnAt(new Vector3(150f, 0.05f, -8.5f)), 10);

        state = course.Walk(state, East, 150);

        Assert.True(state.Position.Y < 1.0f,
            $"a 55 degree ramp exceeds the 46 degree slope limit and must not be walkable, " +
            $"but the character reached y={state.Position.Y:0.000}");
    }

    [Fact]
    public void Cresting_a_ramp_does_not_launch_the_character()
    {
        var course = new Course();
        CharacterState state = course.Settle(Course.SpawnAt(new Vector3(150f, 0.05f, -13f)), 10);

        // Walk up and over the 40 degree ramp, checking every tick from the
        // moment the top is reached. A ground snap that gates on downward
        // velocity throws the character into the air exactly here — you arrive
        // at the crest with a real upward velocity, which is the case that looks
        // fine on flat ground and fails on every ramp in the level.
        float highest = 0f;
        bool everAirborneOnTop = false;

        for (int i = 0; i < 200; i++)
        {
            state = course.Step(state, East, forward: 1f);

            // The window is the platform's own surface: before 156 the character
            // is still on the ramp, and after 161 it is approaching the platform's
            // east edge at 162, where leaving the ground is simply what walking
            // off a ledge does.
            if (state.Position.X < 156f || state.Position.X > 161f)
                continue;

            highest = MathF.Max(highest, state.Position.Y);
            if (!state.Grounded)
                everAirborneOnTop = true;
        }

        Assert.False(everAirborneOnTop,
            "the character left the ground after cresting the ramp — the ground snap is not holding");
        Assert.True(highest < 2.1f,
            $"the character rose to y={highest:0.000} on the platform, which is above its top at 2.0");
        Assert.True(highest > 1.9f, "the character never reached the platform at all");
    }

    // --- The chasm -----------------------------------------------------------

    [Fact]
    public void The_chasm_goes_all_the_way_through_the_floor()
    {
        var course = new Course();

        // Dropped in from above. There is no bottom: the cut runs past the
        // slab's underside, so this must keep falling rather than land.
        CharacterState state = Course.SpawnAt(new Vector3(153.5f, 2f, 10f));
        state = course.Settle(state, 180);

        Assert.False(state.Grounded, "the chasm must have no floor to stand on");
        Assert.True(state.Position.Y < DemoPlayArea.FallOutHeight,
            $"three seconds of falling should be well past the fall-out height, " +
            $"but the character is at y={state.Position.Y:0.0}");
    }

    [Fact]
    public void The_chasm_can_be_jumped_at_walking_speed()
    {
        var course = new Course();

        // Run-up on the floor east of the terrace, heading for the chasm's west
        // lip at x = 152.
        CharacterState state = course.Settle(Course.SpawnAt(new Vector3(148.5f, 0.05f, 10f)), 10);

        bool jumped = false;
        for (int i = 0; i < 120; i++)
        {
            bool jumpNow = !jumped && state.Position.X > 151.2f;
            state = course.Step(state, East, forward: 1f, jump: jumpNow);
            if (jumpNow)
                jumped = true;
        }

        Assert.True(jumped, "the character never reached the near lip of the chasm");
        Assert.True(state.Position.X > 155.4f,
            $"a 3.0 gap must be clearable at walk speed with a 1.2 jump, but the character " +
            $"ended at x={state.Position.X:0.000}, y={state.Position.Y:0.000}");
        Assert.True(state.Grounded, "the character should have landed on the far side");
    }

    // --- The part brush -------------------------------------------------------

    [Fact]
    public void The_part_brush_platform_is_solid()
    {
        var course = new Course();

        // Dropped onto the part platform, whose top is at y = 1.0. It is the only
        // geometry in the course that reaches the mover through the live spatial
        // index rather than the compiled world — if that lane is broken, this
        // falls through to the floor at y = 0.
        CharacterState state = Course.SpawnAt(new Vector3(164f, 3f, -3f));
        state = course.Settle(state, 120);

        Assert.True(state.Grounded, "the character should be standing on the part brush");
        Assert.Equal(1f + course.Tuning.SkinWidth, state.Position.Y, 4);
    }

    // --- The floor has no holes in it -----------------------------------------

    [Fact]
    public void No_direction_walked_from_the_spawn_leaves_the_world()
    {
        var course = new Course();

        // Sixteen headings, three hundred ticks each: five seconds of walking in
        // every direction from the spawn. Nothing here asserts where you end up —
        // the point is that you never end up falling, which is what an unnoticed
        // gap between two brushes produces and what no amount of staring at the
        // level in an editor reveals.
        for (int i = 0; i < 16; i++)
        {
            float yaw = i * MathF.Tau / 16f;
            CharacterState state = course.Settle(course.Spawn(), 10);

            for (int tick = 0; tick < 300; tick++)
            {
                state = course.Step(state, yaw, forward: 1f);

                // The chasm is the one authored way out of the world, and
                // walking into it is the correct outcome rather than a hole
                // nobody meant to leave. Stop this heading there instead of
                // pretending the fall is a failure.
                if (InsideChasm(state.Position))
                    break;

                Assert.True(state.Position.Y > DemoPlayArea.FallOutHeight,
                    $"walking at yaw {yaw:0.00} rad fell out of the world at tick {tick}, " +
                    $"y={state.Position.Y:0.000}, x={state.Position.X:0.0}, z={state.Position.Z:0.0}");
            }
        }
    }

    // --- The region cache -----------------------------------------------------

    [Fact]
    public void Walking_does_not_rebuild_the_world_lane_every_tick()
    {
        var course = new Course();
        CharacterState state = course.Settle(course.Spawn(), 10);

        int afterSettle = course.Source.WorldLaneRebuilds;
        state = course.Walk(state, South, 300);

        int rebuilds = course.Source.WorldLaneRebuilds - afterSettle;

        // Five seconds of walking covers about 22 units, which is inside one
        // region margin — so at most a couple of rebuilds. A count that tracks
        // the tick count means the region is not holding and every tick is
        // paying an O(region) rebuild it should be amortising over thousands.
        Assert.True(rebuilds <= 2,
            $"the world lane was rebuilt {rebuilds} times over 300 ticks of walking");
    }

    private static bool InsideChasm(Vector3 position) =>
        position.X > 151.5f && position.X < 155.5f &&
        position.Z > 1.5f && position.Z < 18.5f;

    /// <summary>The real play area, compiled, with a mover driven over it.</summary>
    private sealed class Course
    {
        private readonly Scene _scene = new("PlayAreaTest");

        public Course()
        {
            DemoPlayArea.Build(_scene, MaterialRef.Default, MaterialRef.Default, MaterialRef.Default);
            _scene.RebuildStaticWorld(new FakeRenderer());
            Source = new BrushPlaneCollisionSource(_scene, Tuning);
        }

        public CharacterTuning Tuning { get; } = new();

        public BrushPlaneCollisionSource Source { get; }

        public CharacterState Spawn() => CharacterState.AtFeet(DemoPlayArea.Spawn);

        public static CharacterState SpawnAt(Vector3 feet) => CharacterState.AtFeet(feet);

        public CharacterState Step(
            CharacterState state, float yaw, float forward = 0f, bool jump = false, bool sprint = false)
        {
            var buttons = CharacterButtons.None;
            if (jump) buttons |= CharacterButtons.Jump;
            if (sprint) buttons |= CharacterButtons.Sprint;

            var command = new CharacterCommand
            {
                MoveForward = CharacterCommand.Axis(forward),
                Yaw = yaw,
                Buttons = buttons,
            };

            CharacterMover.Tick(ref state, in command, Source, Tuning, Dt);
            return state;
        }

        public CharacterState Settle(CharacterState state, int ticks)
        {
            for (int i = 0; i < ticks; i++)
                state = Step(state, 0f);
            return state;
        }

        public CharacterState Walk(CharacterState state, float yaw, int ticks)
        {
            for (int i = 0; i < ticks; i++)
                state = Step(state, yaw, forward: 1f);
            return state;
        }
    }
}
