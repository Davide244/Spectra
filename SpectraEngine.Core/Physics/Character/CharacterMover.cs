using System;
using System.Numerics;
using SpectraEngine.Core.Bsp;

namespace SpectraEngine.Core.Physics.Character;

/// <summary>
/// The per-tick character movement algorithm: accelerate, jump, fall, sweep and
/// slide, step, depenetrate, find ground.
/// </summary>
/// <remarks>
/// <para>
/// <b>A pure function of (state, command, collision source, tuning, dt).</b> No
/// clock, no allocation, no scene-graph write, no camera read. That is what
/// makes the state a struct copy and rollback a copy rather than a rewrite —
/// and it is a constraint most character controllers violate on their first
/// line by reading input or time directly.
/// </para>
/// <para>
/// The algorithm is this engine's rather than a physics library's, because no
/// physics library ships one: they provide read-only primitives and document
/// nothing about stairs, ground snap or slope limits.
/// </para>
/// </remarks>
public static class CharacterMover
{
    /// <summary>Contact planes one tick may hold. A corner is three; this covers a corner, a jamb and a step.</summary>
    public const int MaxContactPlanes = 8;

    /// <summary>Padding added to the tick's query volume, covering skin width and a tick of solver error.</summary>
    public const float BroadphaseMargin = 0.5f;

    /// <summary>Two contact planes closer than this in NORMAL are one plane, for clipping purposes.</summary>
    /// <remarks>
    /// Offset deliberately does not enter it, though an earlier version of this
    /// comment said it did. The planes collected here feed
    /// <see cref="ICharacterCollisionSource.ClipVelocity"/> and nothing else,
    /// and that reads only the normal, so two parallel planes at different
    /// offsets clip identically and keeping both would be wasted budget. The
    /// depenetration pass, which does care about offset, gathers its own planes
    /// and never sees these.
    /// </remarks>
    private const float PlaneDedupeDot = 0.999f;

    /// <summary>Advances the character by exactly one fixed tick.</summary>
    public static void Tick(
        ref CharacterState state,
        in CharacterCommand command,
        ICharacterCollisionSource source,
        CharacterTuning tuning,
        float dt)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tuning);

        var filter = CharacterQueryFilter.Default;

        // --- Step 0: edges from STATE, never from the command -------------
        // The frame loop samples input once and then runs up to five ticks, so
        // a command is replayed. Deriving edges from the mover's own previous
        // buttons is what stops one jump press firing five times on a catch-up
        // frame, and it replays exactly.
        bool jumpPressed = (command.Buttons & CharacterButtons.Jump) != 0
            && (state.PrevButtons & CharacterButtons.Jump) == 0;
        state.PrevButtons = command.Buttons;

        bool wasGrounded = state.Grounded;
        state.SteppedUpBy = 0f;

        // --- Step 0b: one broad phase for the whole tick -------------------
        source.BeginTick(TickVolume(in state, tuning, dt), in filter);

        // --- Step 1: wish direction, from the COMMAND's yaw ----------------
        // Pitch is deliberately excluded: looking up must not slow you down and
        // looking down must not walk you into the floor.
        float cos = MathF.Cos(command.Yaw);
        float sin = MathF.Sin(command.Yaw);
        var forward = new Vector3(cos, 0f, sin);
        var right = new Vector3(-sin, 0f, cos);

        Vector3 wish = forward * command.ForwardAxis + right * command.StrafeAxis;
        float wishLength = wish.Length();
        if (wishLength > 1f)
            wish /= wishLength;
        else if (wishLength > 1e-4f)
            wish = Vector3.Normalize(wish);

        if (state.Grounded && wish != Vector3.Zero)
        {
            // Along the slope, not through it — otherwise a ramp decelerates you
            // for no reason a player can see.
            wish -= state.GroundNormal * Vector3.Dot(wish, state.GroundNormal);
            if (wish.LengthSquared() > 1e-8f)
                wish = Vector3.Normalize(wish);
        }

        // --- Step 2: friction, then accelerate -----------------------------
        bool sprinting = (command.Buttons & CharacterButtons.Sprint) != 0;
        float targetSpeed = tuning.WalkSpeed * (sprinting ? tuning.SprintMultiplier : 1f);

        if (state.Grounded)
        {
            var horizontal = new Vector3(state.Velocity.X, 0f, state.Velocity.Z);
            float speed = horizontal.Length();
            if (speed > 0f)
            {
                // StopSpeed is what makes a nearly-stopped character actually
                // stop instead of asymptoting toward zero forever, which reads
                // as standing on ice.
                float drop = MathF.Max(speed, tuning.StopSpeed) * tuning.GroundFriction * dt;
                float scale = MathF.Max(0f, speed - drop) / speed;
                state.Velocity.X *= scale;
                state.Velocity.Z *= scale;
            }

            Accelerate(ref state.Velocity, wish, targetSpeed, tuning.GroundAcceleration, dt);
        }
        else
        {
            // The cap goes on the WISH SPEED inside the projection, not on the
            // resulting velocity. That is the difference between steering in the
            // air and accumulating speed by strafing.
            Accelerate(ref state.Velocity, wish, MathF.Min(targetSpeed, tuning.AirSpeedCap),
                tuning.AirAcceleration, dt);
        }

        // --- Step 3: jump, before gravity so it gets its full velocity ------
        bool jumpedThisTick = false;
        bool wantJump = jumpPressed || state.JumpBufferTicks > 0;
        bool canJump = state.Grounded || state.AirTicks <= tuning.CoyoteTicks;

        if (wantJump && canJump && state.GroundSuppressTicks == 0)
        {
            state.Velocity.Y = tuning.JumpVelocity;
            // Clearing the ground reference here is load-bearing: leaving it set
            // makes the ground snap below undo the jump on the very same tick.
            state.Grounded = false;
            state.GroundNormal = Vector3.UnitY;
            state.GroundNodeId = default;
            state.JumpBufferTicks = 0;
            state.AirTicks = byte.MaxValue;
            state.GroundSuppressTicks = tuning.GroundSuppressTicks;
            jumpedThisTick = true;
        }
        else if (jumpPressed)
        {
            state.JumpBufferTicks = tuning.JumpBufferTicks;
        }
        else if (state.JumpBufferTicks > 0)
        {
            state.JumpBufferTicks--;
        }

        // --- Step 4: gravity ------------------------------------------------
        if (!state.Grounded)
        {
            state.Velocity += PhysicsDefaults.Gravity * tuning.GravityScale * dt;
            if (state.Velocity.Y < -tuning.MaxFallSpeed)
                state.Velocity.Y = -tuning.MaxFallSpeed;
        }

        // --- Step 5: sweep and slide ----------------------------------------
        Span<CharacterContactPlane> planes = stackalloc CharacterContactPlane[MaxContactPlanes];
        // Not stackalloc: a contact source carries the node and brush it came
        // from, so it is a managed type. Thread-static rather than per-call, so
        // the steady state still allocates nothing.
        Span<CharacterContactSource> sources = SourceScratch();
        int planeCount = 0;

        Vector3 remaining = state.Velocity * dt;
        Vector3 originalHorizontal = new(remaining.X, 0f, remaining.Z);

        bool blockedByWall = false;
        Vector3 blockingNormal = Vector3.UnitY;

        for (int iteration = 0; iteration < tuning.MaxSlideIterations; iteration++)
        {
            if (remaining.LengthSquared() < tuning.SkinWidth * tuning.SkinWidth)
                break;

            float fraction = source.SweepCapsule(
                CapsuleAt(in state, tuning), remaining, in filter,
                out CharacterContactPlane plane, out _);

            state.Position += remaining * fraction;

            if (fraction >= 1f)
                break;

            // BLOCKED IS SET FROM THE SWEEP, NOT FROM LOOP EXHAUSTION. Reading
            // it off the last iteration means the step probe below never runs on
            // an ordinary staircase — a character walks into the first riser,
            // slides to a stop in one iteration, and never tries to climb.
            if (plane.Plane.Normal.Y < tuning.MinGroundNormalY)
            {
                blockedByWall = true;
                blockingNormal = plane.Plane.Normal;
            }

            AddPlane(planes, ref planeCount, plane);
            remaining = source.ClipVelocity(remaining * (1f - fraction), planes[..planeCount]);
            state.Velocity = source.ClipVelocity(state.Velocity, planes[..planeCount]);
        }

        // --- Step 6: step probe ----------------------------------------------
        if (blockedByWall && wasGrounded && !jumpedThisTick)
        {
            TryStepUp(ref state, source, tuning, in filter, originalHorizontal, blockingNormal);
        }

        // --- Step 7: depenetrate ---------------------------------------------
        // Target delta is ZERO on purpose: the sweep already moved us, and
        // solving toward a non-zero target makes the push fight the slide, which
        // is the classic source of jitter against a wall.
        for (int iteration = 0; iteration < tuning.MaxDepenetrationIterations; iteration++)
        {
            int count = source.GatherPlanes(
                CapsuleAt(in state, tuning), 0f, in filter, planes, sources);
            if (count == 0)
                break;

            Vector3 push = source.SolvePlanes(Vector3.Zero, planes[..count]).Delta;
            float pushLength = push.Length();
            if (pushLength < 1e-5f)
                break;

            if (pushLength > tuning.MaxPushPerTick)
                push = push / pushLength * tuning.MaxPushPerTick;

            state.Position += push;
            state.Velocity = source.ClipVelocity(state.Velocity, planes[..count]);
        }

        // --- Step 8: ground check and snap ------------------------------------
        GroundCheck(ref state, source, tuning, in filter, wasGrounded, jumpedThisTick, planes, sources);

        if (state.GroundSuppressTicks > 0)
            state.GroundSuppressTicks--;

        if (state.Grounded)
            state.AirTicks = 0;
        else if (state.AirTicks < byte.MaxValue)
            state.AirTicks++;
    }

    [ThreadStatic]
    private static CharacterContactSource[]? _sourceScratch;

    private static Span<CharacterContactSource> SourceScratch() =>
        _sourceScratch ??= new CharacterContactSource[MaxContactPlanes];

    /// <summary>The capsule the character currently occupies.</summary>
    public static CharacterCapsule CapsuleAt(in CharacterState state, CharacterTuning tuning) =>
        CharacterCapsule.FromFeet(state.Position, tuning.StandHeight, tuning.Radius);

    // Quake's projection form: add only what is missing along the wish
    // direction. A plain clamp would let a character strafe past its own top
    // speed, and a plain add would never converge.
    private static void Accelerate(
        ref Vector3 velocity, Vector3 wish, float wishSpeed, float acceleration, float dt)
    {
        if (wish == Vector3.Zero || wishSpeed <= 0f)
            return;

        float current = Vector3.Dot(velocity, wish);
        float add = wishSpeed - current;
        if (add <= 0f)
            return;

        velocity += wish * MathF.Min(acceleration * dt * wishSpeed, add);
    }

    private static void TryStepUp(
        ref CharacterState state,
        ICharacterCollisionSource source,
        CharacterTuning tuning,
        in CharacterQueryFilter filter,
        Vector3 horizontal,
        Vector3 blockingNormal)
    {
        if (horizontal.LengthSquared() < 1e-8f)
            return;

        Vector3 savedPosition = state.Position;
        Vector3 savedVelocity = state.Velocity;

        // Up.
        var up = new Vector3(0f, tuning.StepHeight, 0f);
        float upFraction = source.SweepCapsule(
            CapsuleAt(in state, tuning), up, in filter, out _, out _);
        state.Position += up * upFraction;

        // Forward — along the ORIGINAL horizontal travel, not the clipped one.
        // Using the clipped vector means stepping along the wall you just hit
        // instead of over it.
        //
        // AND FAR ENOUGH THAT THE DOWN PROBE LANDS ON SOMETHING WALKABLE — but
        // no further, because whatever this advances is COMMITTED.
        //
        // One tick of walking is about 0.075 units, and a capsule advanced only
        // that far lands on the step's EDGE with a normal around 0.58: correctly
        // not walkable, so the probe rejects and the character is stuck against a
        // step it could obviously climb. That much of the original reasoning was
        // right. What was wrong was the conclusion — that the probe must carry
        // the axis all the way over the tread, i.e. a full radius. A radius is
        // five ticks of walking teleported forward in one, per riser, which
        // climbs a 0.5-tread staircase at two and a half times walking speed in
        // visible lurches.
        //
        // The real bound is geometric and much smaller. Resting against a riser,
        // the axis is at most r + skin behind the ledge's top edge. After rising,
        // the down sweep meets that edge with normal.Y = dy/r, which reaches the
        // slope limit once the axis is within r·sin(limit) of it — so the advance
        // actually needed is at most
        //
        //     (r + skin) − r·sin(limit) = r·(1 − sin(limit)) + skin
        //
        // 0.11 for the defaults, not 0.37. One and a half ticks, and then the
        // character simply WALKS UP THE EDGE as if it were a slope, because a
        // walkable edge contact is exactly what the ordinary ground path already
        // handles. That is also what a capsule physically does, and it is what
        // makes a staircase feel like a ramp instead of a row of hops.
        float desired = horizontal.Length();
        float sinSlopeLimit = MathF.Sqrt(
            MathF.Max(0f, 1f - tuning.MinGroundNormalY * tuning.MinGroundNormalY));
        float minimumProbe = tuning.Radius * (1f - sinSlopeLimit) + tuning.SkinWidth * 4f;
        float probeDistance = MathF.Max(desired, minimumProbe);
        Vector3 probeForward = horizontal / desired * probeDistance;

        float forwardFraction = source.SweepCapsule(
            CapsuleAt(in state, tuning), probeForward, in filter, out _, out _);
        state.Position += probeForward * forwardFraction;

        // Down, slightly further than we rose so the landing is found.
        var down = new Vector3(0f, -(tuning.StepHeight * upFraction + tuning.SkinWidth * 2f), 0f);
        float downFraction = source.SweepCapsule(
            CapsuleAt(in state, tuning), down, in filter,
            out CharacterContactPlane landing, out CharacterContactSource landingSource);

        bool landed = downFraction < 1f;
        bool walkable = landed && landing.Plane.Normal.Y >= tuning.MinGroundNormalY;
        bool progressed = forwardFraction > 1e-3f;

        // THE LEDGE'S OWN HEIGHT, not the capsule's — and without this,
        // StepHeight is not the number it claims to be.
        //
        // The capsule's bottom is a hemisphere, so after rising StepHeight it can
        // still creep forward and come to rest against the top EDGE of a taller
        // riser. That contact's normal points up and away from the corner, and
        // for any riser under about StepHeight + r(1 - cos slopeLimit) it lands
        // inside the slope limit — so the probe accepts, the character is perched
        // on the corner at exactly StepHeight, and next tick it walks the
        // remainder as if the edge were a ramp. Measured effect with the
        // defaults: a documented 0.45 step height climbing 0.50 risers all day,
        // which is not a rounding error, it is a fifth again.
        //
        // Bounding the CONTACT POINT is what makes the constant mean what it
        // says: "the highest ledge the step probe will climb". The skin width is
        // there because savedPosition is already a skin above the surface being
        // left.
        bool withinStep = landed
            && landingSource.Point.Y <= savedPosition.Y + tuning.StepHeight + tuning.SkinWidth;

        if (!landed || !walkable || !progressed || !withinStep)
        {
            // Discarding the whole probe on failure is what makes it safe to be
            // wrong: a rejected step leaves the character exactly where the
            // slide left it.
            state.Position = savedPosition;
            state.Velocity = savedVelocity;
            return;
        }

        state.Position += down * downFraction;
        state.SteppedUpBy = MathF.Max(0f, state.Position.Y - savedPosition.Y);
        state.Grounded = true;
        state.GroundNormal = landing.Plane.Normal;

        // Horizontal velocity is deliberately NOT re-clipped by a successful
        // step. Re-clipping is what makes stairs feel like a series of stops
        // instead of a ramp.
        _ = blockingNormal;
    }

    private static void GroundCheck(
        ref CharacterState state,
        ICharacterCollisionSource source,
        CharacterTuning tuning,
        in CharacterQueryFilter filter,
        bool wasGrounded,
        bool jumpedThisTick,
        Span<CharacterContactPlane> planes,
        Span<CharacterContactSource> sources)
    {
        if (jumpedThisTick)
        {
            state.Grounded = false;
            return;
        }

        int count = source.GatherPlanes(
            CapsuleAt(in state, tuning), tuning.SkinWidth * 2f, in filter, planes, sources);

        int best = -1;
        for (int i = 0; i < count; i++)
        {
            if (planes[i].Plane.Normal.Y < tuning.MinGroundNormalY)
                continue;
            if (best < 0 || planes[i].Plane.Normal.Y > planes[best].Plane.Normal.Y)
                best = i;
        }

        Vector3 groundNormal = best >= 0 ? planes[best].Plane.Normal : Vector3.UnitY;
        bool found = best >= 0;
        Guid groundNode = best >= 0 ? sources[best].Node?.Id ?? default : default;

        // THE SNAP, and note what does NOT gate it: a downward velocity. Walking
        // up a ramp gives the character a genuine upward velocity, so gating on
        // "falling" means cresting any ramp launches it on a ballistic arc —
        // which is the single most visible feel bug this whole algorithm can
        // have. Having been grounded and not having jumped is the correct gate;
        // the suppress counter already covers the jump case.
        if (!found && wasGrounded && state.GroundSuppressTicks == 0)
        {
            var down = new Vector3(0f, -tuning.GroundSnapDistance, 0f);
            float fraction = source.SweepCapsule(
                CapsuleAt(in state, tuning), down, in filter,
                out CharacterContactPlane probe, out CharacterContactSource probeSource);

            if (fraction < 1f && probe.Plane.Normal.Y >= tuning.MinGroundNormalY)
            {
                state.Position += down * fraction;
                groundNormal = probe.Plane.Normal;
                groundNode = probeSource.Node?.Id ?? default;
                found = true;
            }
        }

        state.Grounded = found;
        if (!found)
            return;

        state.GroundNormal = groundNormal;
        state.GroundNodeId = groundNode;

        // Remove the velocity driving into the ground — including the residual
        // UPWARD component a ramp just imparted, which is the other half of not
        // launching off crests.
        float into = Vector3.Dot(state.Velocity, groundNormal);
        if (into < 0f)
            state.Velocity -= groundNormal * into;
        else if (state.Velocity.Y > 0f && into > 0f)
            state.Velocity -= groundNormal * into;
    }

    // Everything the tick can possibly reach: the capsule, where it is going,
    // how far it might step up and how far it might snap down.
    private static Aabb TickVolume(in CharacterState state, CharacterTuning tuning, float dt)
    {
        CharacterCapsule capsule = CapsuleAt(in state, tuning);
        Vector3 travel = state.Velocity * dt;

        Vector3 min = Vector3.Min(capsule.Center1, capsule.Center2);
        Vector3 max = Vector3.Max(capsule.Center1, capsule.Center2);

        min = Vector3.Min(min, min + travel);
        max = Vector3.Max(max, max + travel);

        float pad = capsule.Radius + BroadphaseMargin;
        min -= new Vector3(pad, pad + tuning.StepHeight + tuning.GroundSnapDistance, pad);
        max += new Vector3(pad, pad + tuning.StepHeight, pad);

        return new Aabb(min, max);
    }

    // Two abutting coplanar brush faces would otherwise contribute two identical
    // planes, and the solver's accumulated push would double — which is a real
    // mechanism for catching on a seam between brushes.
    private static void AddPlane(
        Span<CharacterContactPlane> planes, ref int count, in CharacterContactPlane plane)
    {
        for (int i = 0; i < count; i++)
        {
            if (Vector3.Dot(planes[i].Plane.Normal, plane.Plane.Normal) > PlaneDedupeDot)
                return;
        }

        if (count >= planes.Length)
            return;

        planes[count] = plane;
        // Marked engaged so velocity clipping does not skip a plane the sweep
        // genuinely stopped against.
        planes[count].Push = 1f;
        count++;
    }
}
