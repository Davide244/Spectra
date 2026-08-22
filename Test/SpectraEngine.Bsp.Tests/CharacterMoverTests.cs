using System;
using System.Numerics;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Physics;
using SpectraEngine.Core.Physics.Character;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The first-person character mover, driven headlessly against real compiled
/// brush geometry.
/// </summary>
/// <remarks>
/// <para>
/// <b>What these tests can and cannot prove.</b> They pin the things with a
/// right answer: does the character stand on the floor at the right height,
/// does it stop at a wall, does it climb a 0.40 step and refuse a 0.60 one,
/// does it stay on the ground cresting a ramp, does a jump reach its authored
/// height, does one jump press fire once across a five-tick catch-up frame.
/// </para>
/// <para>
/// They cannot prove it <em>feels</em> right. Acceleration, friction and
/// air control are chosen numbers, and catching on seams or jitter against a
/// wall are the failure modes that only show up under a human hand. Those are
/// playtesting, and saying so here is more honest than writing a test that
/// asserts a constant equals itself.
/// </para>
/// </remarks>
public sealed class CharacterMoverTests
{
    private const float Dt = PhysicsDefaults.FixedDeltaTime;

    // --- The collision source ----------------------------------------------

    [Fact]
    public void A_capsule_resting_on_a_floor_reports_the_floor_as_ground()
    {
        var world = new TestWorld();
        world.AddBox("floor", new Vector3(-8f, -1f, -8f), new Vector3(8f, 0f, 8f));
        world.Compile();

        CharacterState state = world.Settle(CharacterState.AtFeet(new Vector3(0f, 2f, 0f)), 120);

        state.Grounded.ShouldBeTrue();
        state.Position.Y.ShouldBe(0f, 0.05f);
        state.GroundNormal.Y.ShouldBeGreaterThan(0.99f);
    }

    [Fact]
    public void A_character_does_not_pass_through_a_wall()
    {
        var world = new TestWorld();
        world.AddBox("floor", new Vector3(-8f, -1f, -8f), new Vector3(8f, 0f, 8f));
        world.AddBox("wall", new Vector3(2f, 0f, -8f), new Vector3(2.5f, 3f, 8f));
        world.Compile();

        CharacterState state = world.Settle(CharacterState.AtFeet(new Vector3(0f, 0.1f, 0f)), 30);
        state = world.Walk(state, forward: 1f, yaw: 0f, ticks: 180);

        // Facing +x at yaw 0; the wall's near face is x = 2, so the capsule
        // centre stops a radius plus skin short of it.
        state.Position.X.ShouldBeLessThan(2f - world.Tuning.Radius + 0.05f);
        state.Position.X.ShouldBeGreaterThan(0.5f, "the character should have actually moved");
    }

    [Fact]
    public void The_corner_of_a_brush_does_not_block_early()
    {
        // The regression this whole narrow phase exists for. A plane-only
        // contact test measures against the SHARP offset polytope, which at a
        // 0.35 radius puts a quarter-unit of phantom solid diagonally off every
        // box corner. Walking diagonally past a pillar corner would catch on
        // nothing.
        var source = TestWorld.SourceFor(
            out CharacterTuning tuning,
            ("pillar", new Vector3(-0.5f, -1f, -0.5f), new Vector3(0.5f, 3f, 0.5f)));

        // A capsule diagonally out from the corner at (0.5, 0.5), 0.5 away
        // along the diagonal — well clear of a 0.35 radius, but INSIDE the
        // sharp-corner offset polytope.
        var feet = new Vector3(0.5f + 0.354f, 0f, 0.5f + 0.354f);
        CharacterCapsule capsule = CharacterCapsule.FromFeet(feet, tuning.StandHeight, tuning.Radius);

        source.BeginTick(Volume(capsule), CharacterQueryFilter.Default);
        Span<CharacterContactPlane> planes = stackalloc CharacterContactPlane[8];
        var sources = new CharacterContactSource[8];

        int count = source.GatherPlanes(in capsule, 0f, CharacterQueryFilter.Default, planes, sources);

        count.ShouldBe(0, "the true distance to the corner is 0.5, well outside a 0.35 radius");
    }

    [Fact]
    public void A_doorway_cut_by_a_subtractive_brush_is_walkable()
    {
        // THE HEADLINE. A convex hull per additive brush cannot express the bite
        // a negative takes out of it, so a hull-based source has this doorway
        // solid. A plane-set source covers A \ N exactly, so the character walks
        // through.
        var world = new TestWorld();
        world.AddBox("floor", new Vector3(-8f, -1f, -8f), new Vector3(8f, 0f, 8f));
        world.AddBox("wall", new Vector3(-4f, 0f, 1f), new Vector3(4f, 3f, 1.5f));
        world.AddNegative("doorway", new Vector3(-0.6f, 0f, 0.5f), new Vector3(0.6f, 2.2f, 2f));
        world.Compile();

        CharacterState state = world.Settle(CharacterState.AtFeet(new Vector3(0f, 0.1f, -1f)), 30);
        state = world.Walk(state, forward: 1f, yaw: MathF.PI / 2f, ticks: 180);

        state.Position.Z.ShouldBeGreaterThan(
            2f, "the character should have walked through the doorway, not into the wall");
    }

    [Fact]
    public void A_wall_beside_the_doorway_still_blocks()
    {
        // The other half: covering must not open holes nobody authored.
        var world = new TestWorld();
        world.AddBox("floor", new Vector3(-8f, -1f, -8f), new Vector3(8f, 0f, 8f));
        world.AddBox("wall", new Vector3(-4f, 0f, 1f), new Vector3(4f, 3f, 1.5f));
        world.AddNegative("doorway", new Vector3(-0.6f, 0f, 0.5f), new Vector3(0.6f, 2.2f, 2f));
        world.Compile();

        // Start well to the side of the opening.
        CharacterState state = world.Settle(CharacterState.AtFeet(new Vector3(3f, 0.1f, -1f)), 30);
        state = world.Walk(state, forward: 1f, yaw: MathF.PI / 2f, ticks: 180);

        state.Position.Z.ShouldBeLessThan(1f, "the solid part of the wall must still stop the character");
    }

    [Fact]
    public void A_subtractive_part_brush_does_not_drill_through_a_world_wall()
    {
        // (Part, Subtractive) is a legal, inert state — the flying projectile of
        // the destruction design. Letting it into the cover would have it
        // opening a moving, invisible hole in every wall it passed.
        var world = new TestWorld();
        world.AddBox("floor", new Vector3(-8f, -1f, -8f), new Vector3(8f, 0f, 8f));
        world.AddBox("wall", new Vector3(-4f, 0f, 1f), new Vector3(4f, 3f, 1.5f));
        world.Compile();

        SceneNode ghost = world.Scene.Root.CreateChild("ghost");
        ghost.BrushKind = BrushKind.Part;
        ghost.LocalPosition = new Vector3(0f, 1f, 1.25f);
        ghost.Brush = Brush
            .CreateBox(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f))
            .WithOperation(BrushOperation.Subtractive);

        CharacterState state = world.Settle(CharacterState.AtFeet(new Vector3(0f, 0.1f, -1f)), 30);
        state = world.Walk(state, forward: 1f, yaw: MathF.PI / 2f, ticks: 180);

        state.Position.Z.ShouldBeLessThan(1f, "an inert part negative must not open the wall");
    }

    // --- Stairs and slopes --------------------------------------------------

    [Fact]
    public void A_step_below_the_limit_is_climbed()
    {
        var world = new TestWorld();
        world.AddBox("floor", new Vector3(-8f, -1f, -8f), new Vector3(8f, 0f, 8f));
        world.AddBox("step", new Vector3(1f, 0f, -8f), new Vector3(8f, 0.40f, 8f));
        world.Compile();

        // 60 ticks is about 4.5 units of walking — onto the step and along it,
        // and deliberately NOT far enough to reach its far edge at x = 8. A
        // longer walk would have the character step up, cross the whole step and
        // fall off the end, which asserts nothing about stepping.
        CharacterState state = world.Settle(CharacterState.AtFeet(new Vector3(0f, 0.1f, 0f)), 30);
        state = world.Walk(state, forward: 1f, yaw: 0f, ticks: 60);

        state.Position.Y.ShouldBeGreaterThan(0.35f, "the character should be standing on the step");
        state.Position.X.ShouldBeGreaterThan(1.5f, "and should have walked onto it");
    }

    [Fact]
    public void A_ledge_above_the_limit_blocks()
    {
        var world = new TestWorld();
        world.AddBox("floor", new Vector3(-8f, -1f, -8f), new Vector3(8f, 0f, 8f));
        world.AddBox("ledge", new Vector3(1f, 0f, -8f), new Vector3(8f, 0.70f, 8f));
        world.Compile();

        CharacterState state = world.Settle(CharacterState.AtFeet(new Vector3(0f, 0.1f, 0f)), 30);
        state = world.Walk(state, forward: 1f, yaw: 0f, ticks: 60);

        state.Position.Y.ShouldBeLessThan(0.2f, "0.70 is above StepHeight and must not be climbed");
        state.Position.X.ShouldBeLessThan(1f);
    }

    [Fact]
    public void Cresting_a_ramp_does_not_launch_the_character()
    {
        // Walking UP a ramp gives the character a genuine upward velocity, so a
        // ground snap gated on "falling" refuses to run exactly when it is
        // needed and the character sails off the top. Zero airborne ticks after
        // the crest is the assertion.
        var world = new TestWorld();
        world.AddBox("floor", new Vector3(-12f, -1f, -8f), new Vector3(0f, 0f, 8f));
        world.AddRamp("ramp", from: new Vector3(0f, 0f, 0f), run: 4f, rise: 2f, halfWidth: 8f);
        world.AddBox("top", new Vector3(4f, 1f, -8f), new Vector3(12f, 2f, 8f));
        world.Compile();

        CharacterState state = world.Settle(CharacterState.AtFeet(new Vector3(-2f, 0.1f, 0f)), 30);

        // 100 ticks at sprint is about 12 units, which crosses the ramp and
        // settles on the platform without reaching its far edge at x = 12.
        int airborneTicks = 0;
        for (int tick = 0; tick < 100; tick++)
        {
            state = world.Step(state, forward: 1f, yaw: 0f, sprint: true);
            if (state.Position.X > 4.2f && !state.Grounded)
                airborneTicks++;
        }

        state.Position.X.ShouldBeGreaterThan(5f, "the character should have crossed the ramp");
        airborneTicks.ShouldBe(0, "cresting a ramp must not put the character in the air");
    }

    // --- Jumping -------------------------------------------------------------

    [Fact]
    public void A_jump_reaches_roughly_its_authored_height()
    {
        var world = new TestWorld();
        world.AddBox("floor", new Vector3(-8f, -1f, -8f), new Vector3(8f, 0f, 8f));
        world.Compile();

        CharacterState state = world.Settle(CharacterState.AtFeet(new Vector3(0f, 0.1f, 0f)), 60);
        float startY = state.Position.Y;

        state = world.Step(state, jump: true);
        float peak = state.Position.Y;
        for (int tick = 0; tick < 120; tick++)
        {
            state = world.Step(state);
            peak = MathF.Max(peak, state.Position.Y);
        }

        (peak - startY).ShouldBeInRange(
            world.Tuning.JumpHeight * 0.8f, world.Tuning.JumpHeight * 1.2f);
        state.Grounded.ShouldBeTrue("and it should have come back down");
    }

    [Fact]
    public void One_jump_press_fires_once_across_a_catch_up_frame()
    {
        // The frame loop samples input once and can then run five ticks. If the
        // edge came from the command rather than from state, one press would
        // fire five times and launch the character through the ceiling.
        var world = new TestWorld();
        world.AddBox("floor", new Vector3(-8f, -1f, -8f), new Vector3(8f, 0f, 8f));
        world.Compile();

        CharacterState state = world.Settle(CharacterState.AtFeet(new Vector3(0f, 0.1f, 0f)), 60);

        // The same held-jump command, five ticks in a row — one frame's worth of
        // catch-up.
        for (int tick = 0; tick < 5; tick++)
            state = world.Step(state, jump: true);

        float singleJumpPeak = world.Tuning.JumpVelocity;
        state.Velocity.Y.ShouldBeLessThan(
            singleJumpPeak + 0.01f, "the jump must not have been applied more than once");
    }

    [Fact]
    public void Holding_jump_does_not_re_fire_without_releasing()
    {
        var world = new TestWorld();
        world.AddBox("floor", new Vector3(-8f, -1f, -8f), new Vector3(8f, 0f, 8f));
        world.Compile();

        CharacterState state = world.Settle(CharacterState.AtFeet(new Vector3(0f, 0.1f, 0f)), 60);

        // Hold jump through a landing: it must not bounce.
        int jumps = 0;
        bool wasGrounded = state.Grounded;
        for (int tick = 0; tick < 240; tick++)
        {
            state = world.Step(state, jump: true);
            if (wasGrounded && !state.Grounded)
                jumps++;
            wasGrounded = state.Grounded;
        }

        jumps.ShouldBe(1, "a held jump must fire once, not on every landing");
    }

    // --- Purity and state ----------------------------------------------------

    [Fact]
    public void The_mover_is_deterministic()
    {
        // The property rollback will later depend on: identical state plus
        // identical commands produce an identical result, every time.
        var world = new TestWorld();
        world.AddBox("floor", new Vector3(-8f, -1f, -8f), new Vector3(8f, 0f, 8f));
        world.AddBox("wall", new Vector3(2f, 0f, -8f), new Vector3(2.5f, 3f, 8f));
        world.Compile();

        CharacterState start = world.Settle(CharacterState.AtFeet(new Vector3(0f, 0.1f, 0f)), 30);

        CharacterState a = world.Walk(start, forward: 1f, yaw: 0.3f, ticks: 120);
        CharacterState b = world.Walk(start, forward: 1f, yaw: 0.3f, ticks: 120);

        a.Position.ShouldBe(b.Position);
        a.Velocity.ShouldBe(b.Velocity);
        a.Grounded.ShouldBe(b.Grounded);
    }

    [Fact]
    public void The_state_is_a_plain_struct_that_copies()
    {
        // Capturing and restoring must be an assignment. A reference reaching
        // out of the state would make rollback a rewrite instead of a copy.
        typeof(CharacterState).IsValueType.ShouldBeTrue();

        var world = new TestWorld();
        world.AddBox("floor", new Vector3(-8f, -1f, -8f), new Vector3(8f, 0f, 8f));
        world.Compile();

        CharacterState state = world.Settle(CharacterState.AtFeet(new Vector3(0f, 0.1f, 0f)), 30);
        CharacterState captured = state;

        state = world.Walk(state, forward: 1f, yaw: 0f, ticks: 60);
        state.Position.ShouldNotBe(captured.Position);

        state = captured;   // restore is one assignment
        state.Position.ShouldBe(captured.Position);
    }

    [Fact]
    public void A_character_spawned_inside_geometry_is_pushed_out_rather_than_falling_through()
    {
        var world = new TestWorld();
        world.AddBox("block", new Vector3(-2f, -2f, -2f), new Vector3(2f, 2f, 2f));
        world.Compile();

        // Feet at the block's centre: buried a full body deep.
        CharacterState state = world.Settle(CharacterState.AtFeet(Vector3.Zero), 240);

        bool outside = state.Position.Y > 1.9f
            || MathF.Abs(state.Position.X) > 2f
            || MathF.Abs(state.Position.Z) > 2f;
        outside.ShouldBeTrue($"the character stayed buried at {state.Position}");
    }

    private static Aabb Volume(in CharacterCapsule capsule)
    {
        Vector3 min = Vector3.Min(capsule.Center1, capsule.Center2) - new Vector3(capsule.Radius + 1f);
        Vector3 max = Vector3.Max(capsule.Center1, capsule.Center2) + new Vector3(capsule.Radius + 1f);
        return new Aabb(min, max);
    }

    /// <summary>A scene, a compiled static world and a collision source over it.</summary>
    private sealed class TestWorld
    {
        public Scene Scene { get; } = new("CharacterTest");

        public CharacterTuning Tuning { get; } = new();

        private BrushPlaneCollisionSource? _source;

        public void AddBox(string name, Vector3 min, Vector3 max)
        {
            SceneNode node = Scene.Root.CreateChild(name);
            Vector3 center = (min + max) * 0.5f;
            Vector3 half = (max - min) * 0.5f;
            node.LocalPosition = center;
            node.Brush = Brush.CreateBox(-half, half);
        }

        public void AddNegative(string name, Vector3 min, Vector3 max)
        {
            SceneNode node = Scene.Root.CreateChild(name);
            Vector3 center = (min + max) * 0.5f;
            Vector3 half = (max - min) * 0.5f;
            node.LocalPosition = center;
            node.Brush = Brush.CreateBox(-half, half).WithOperation(BrushOperation.Subtractive);
        }

        /// <summary>A wedge rising from <paramref name="from"/> over <paramref name="run"/>.</summary>
        public void AddRamp(string name, Vector3 from, float run, float rise, float halfWidth)
        {
            float length = MathF.Sqrt(run * run + rise * rise);
            var slope = new Plane(new Vector3(-rise / length, run / length, 0f), 0f);

            var planes = new[]
            {
                new Plane(new Vector3(1f, 0f, 0f), -run),
                new Plane(new Vector3(-1f, 0f, 0f), 0f),
                new Plane(new Vector3(0f, -1f, 0f), -1f),
                new Plane(new Vector3(0f, 0f, 1f), -halfWidth),
                new Plane(new Vector3(0f, 0f, -1f), -halfWidth),
                slope,
            };

            SceneNode node = Scene.Root.CreateChild(name);
            node.LocalPosition = from;
            node.Brush = new Brush(planes);
        }

        public void Compile()
        {
            Scene.RebuildStaticWorld(new FakeRenderer());
            _source = new BrushPlaneCollisionSource(Scene, Tuning);
        }

        public static BrushPlaneCollisionSource SourceFor(
            out CharacterTuning tuning, params (string Name, Vector3 Min, Vector3 Max)[] boxes)
        {
            var world = new TestWorld();
            foreach ((string name, Vector3 min, Vector3 max) in boxes)
                world.AddBox(name, min, max);
            world.Compile();
            tuning = world.Tuning;
            return world._source!;
        }

        public CharacterState Step(
            CharacterState state, float forward = 0f, float strafe = 0f, float yaw = 0f,
            bool jump = false, bool sprint = false)
        {
            var buttons = CharacterButtons.None;
            if (jump)
                buttons |= CharacterButtons.Jump;
            if (sprint)
                buttons |= CharacterButtons.Sprint;

            var command = new CharacterCommand
            {
                MoveForward = CharacterCommand.Axis(forward),
                MoveStrafe = CharacterCommand.Axis(strafe),
                Yaw = yaw,
                Buttons = buttons,
            };

            CharacterMover.Tick(ref state, in command, _source!, Tuning, Dt);
            return state;
        }

        public CharacterState Settle(CharacterState state, int ticks)
        {
            for (int i = 0; i < ticks; i++)
                state = Step(state);
            return state;
        }

        public CharacterState Walk(CharacterState state, float forward, float yaw, int ticks)
        {
            for (int i = 0; i < ticks; i++)
                state = Step(state, forward: forward, yaw: yaw);
            return state;
        }
    }
}
