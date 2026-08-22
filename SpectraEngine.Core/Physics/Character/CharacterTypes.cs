using System;
using System.Numerics;

namespace SpectraEngine.Core.Physics.Character;

/// <summary>A capsule in world space: two hemisphere centres and a radius.</summary>
public readonly struct CharacterCapsule
{
    public CharacterCapsule(Vector3 center1, Vector3 center2, float radius)
    {
        Center1 = center1;
        Center2 = center2;
        Radius = radius;
    }

    /// <summary>The lower hemisphere centre.</summary>
    public Vector3 Center1 { get; }

    /// <summary>The upper hemisphere centre.</summary>
    public Vector3 Center2 { get; }

    public float Radius { get; }

    /// <summary>
    /// Builds a standing capsule from a FEET position and a tip-to-tip height —
    /// the form every caller wants, because a character's authored position is
    /// where it stands, not where its middle is.
    /// </summary>
    public static CharacterCapsule FromFeet(Vector3 feet, float height, float radius) =>
        new(feet + new Vector3(0f, radius, 0f),
            feet + new Vector3(0f, height - radius, 0f),
            radius);

    /// <summary>This capsule translated by <paramref name="delta"/>.</summary>
    public CharacterCapsule Translated(Vector3 delta) =>
        new(Center1 + delta, Center2 + delta, Radius);
}

/// <summary>One contact constraint acting on the mover.</summary>
/// <remarks>
/// <para>
/// <b>The plane's <c>D</c> is pre-baked so that
/// <see cref="Plane.DotCoordinate"/> against a TRANSLATION is the capsule's
/// surface separation after moving by it.</b> That works because separation is
/// affine in the translation:
/// <c>sep(delta) = min(n·c1, n·c2) + D − r = n·delta + sep(0)</c>, so storing
/// <c>sep(0)</c> in <c>D</c> turns the plane into the separation function
/// itself. The solver then never needs to know what shape it is pushing.
/// </para>
/// <para>
/// The normal points OUT of the obstacle, toward free space.
/// </para>
/// </remarks>
public struct CharacterContactPlane
{
    /// <summary>Outward unit normal; <c>D</c> is the separation at zero translation.</summary>
    public Plane Plane;

    /// <summary>How far this plane may push. <see cref="float.MaxValue"/> is perfectly rigid.</summary>
    public float PushLimit;

    /// <summary>Written by the solver, read by velocity clipping. Zero means the plane never engaged.</summary>
    public float Push;

    /// <summary>Whether this plane removes velocity driving into it. False makes it a soft plane.</summary>
    public bool ClipVelocity;

    public static CharacterContactPlane Rigid(Vector3 normal, float separation) => new()
    {
        Plane = new Plane(normal, separation),
        PushLimit = float.MaxValue,
        Push = 0f,
        ClipVelocity = true,
    };
}

/// <summary>
/// What produced a contact plane — kept PARALLEL to the plane array, never
/// inside it.
/// </summary>
/// <remarks>
/// Keeping <see cref="CharacterContactPlane"/> blittable is what would let a
/// native solver consume the plane array with no copy, and this side carries
/// everything a gameplay layer wants instead: which node you are standing on
/// (for moving platforms), which brush face you touched (for per-face surface
/// materials), and where.
/// </remarks>
public readonly struct CharacterContactSource
{
    public Scene.SceneNode? Node { get; init; }

    public Bsp.Brush? Brush { get; init; }

    /// <summary>Index into the brush's planes, or −1 for a cavity wall from a subtractive brush.</summary>
    public int PlaneIndex { get; init; }

    /// <summary>The closest point on the obstacle, in world space.</summary>
    public Vector3 Point { get; init; }
}

/// <summary>What a character query is allowed to see.</summary>
public readonly record struct CharacterQueryFilter
{
    /// <summary>The character's own node, never collided with.</summary>
    public Scene.SceneNode? Self { get; init; }

    /// <summary>Whether live <see cref="Scene.BrushKind.Part"/> brushes are gathered as well as world geometry.</summary>
    public bool IncludeParts { get; init; }

    /// <summary>The default: world geometry plus part brushes, nothing ignored.</summary>
    public static CharacterQueryFilter Default => new() { IncludeParts = true };
}

/// <summary>Buttons a character command can carry.</summary>
[Flags]
public enum CharacterButtons : byte
{
    None = 0,
    Jump = 1 << 0,
    Sprint = 1 << 1,
    Crouch = 1 << 2,
}

/// <summary>
/// One tick's worth of intent. <b>Per TICK, never per frame.</b>
/// </summary>
/// <remarks>
/// <para>
/// The engine's frame loop samples input once and then runs zero to five fixed
/// ticks, so a command is replayed across the ticks of one frame. That makes
/// edge-triggered inputs the whole problem: a jump held for one frame would
/// fire on every tick of a catch-up frame and launch the character through the
/// ceiling. The mover therefore derives edges from <em>its own previous
/// button state</em> rather than from the command, which is what Quake and
/// Source do and what makes replay exact.
/// </para>
/// <para>
/// Movement axes are quantised to a byte range on purpose: it is what a
/// networked command has to be anyway, and discovering that later would change
/// every recorded trajectory.
/// </para>
/// </remarks>
public readonly record struct CharacterCommand
{
    /// <summary>Forward axis, −127..127.</summary>
    public sbyte MoveForward { get; init; }

    /// <summary>Strafe axis, −127..127.</summary>
    public sbyte MoveStrafe { get; init; }

    /// <summary>View yaw in radians. The mover takes its direction from HERE, never from a camera.</summary>
    public float Yaw { get; init; }

    /// <summary>View pitch in radians. Carried for the view, deliberately not used for movement.</summary>
    public float Pitch { get; init; }

    public CharacterButtons Buttons { get; init; }

    /// <summary>Normalised forward axis in −1..1.</summary>
    public float ForwardAxis => MoveForward / 127f;

    /// <summary>Normalised strafe axis in −1..1.</summary>
    public float StrafeAxis => MoveStrafe / 127f;

    public static sbyte Axis(float value) =>
        (sbyte)Math.Clamp((int)MathF.Round(value * 127f), -127, 127);
}

/// <summary>
/// The mover's entire state — a plain struct, copyable in one assignment.
/// </summary>
/// <remarks>
/// <b>That it is a struct is a requirement, not a convenience.</b> The mover is
/// the one thing a networked client predicts and replays, which means capturing
/// and restoring its state has to be free and exact. A class here, or any
/// reference reaching out of it, would make rollback a rewrite instead of a
/// copy.
/// </remarks>
public struct CharacterState
{
    /// <summary>Feet position in world space.</summary>
    public Vector3 Position;

    public Vector3 Velocity;

    /// <summary>The surface normal of whatever is being stood on. Up when airborne.</summary>
    public Vector3 GroundNormal;

    public bool Grounded;

    /// <summary>Buttons from the previous tick, so edges are derived from state and replay exactly.</summary>
    public CharacterButtons PrevButtons;

    /// <summary>Ticks spent airborne, saturating. Drives coyote time.</summary>
    public byte AirTicks;

    /// <summary>Ticks a buffered jump stays live, so a jump pressed just before landing still fires.</summary>
    public byte JumpBufferTicks;

    /// <summary>Ticks during which ground detection is suppressed after a jump.</summary>
    public byte GroundSuppressTicks;

    /// <summary>How far the last step probe lifted the character. Render-only, for eye smoothing.</summary>
    public float SteppedUpBy;

    /// <summary>The node being stood on, when it is a part brush that can move.</summary>
    public Guid GroundNodeId;

    public static CharacterState AtFeet(Vector3 feet) => new()
    {
        Position = feet,
        Velocity = Vector3.Zero,
        GroundNormal = Vector3.UnitY,
        Grounded = false,
    };
}

/// <summary>
/// Every tuning constant, in spectraunits, where one unit is one metre.
/// </summary>
/// <remarks>
/// <para>
/// A mutable class rather than constants because these are what a designer
/// changes, and most of them are feel rather than physics. The defaults are
/// chosen against a real human: 1.8 m tall, walking at 4.5 m/s, jumping 1.2 m,
/// stepping 0.45 m.
/// </para>
/// <para>
/// <b>Gravity is deliberately NOT here.</b> It is read from
/// <see cref="PhysicsDefaults.Gravity"/>, because a character falling at one
/// rate and a dropped crate at another is a divergence no test catches and
/// every player feels.
/// </para>
/// </remarks>
public sealed class CharacterTuning
{
    // --- Body ---------------------------------------------------------------

    /// <summary>Capsule radius. 0.70 across — slim enough to pass a 1.0 doorway with clearance.</summary>
    public float Radius { get; set; } = 0.35f;

    /// <summary>Tip-to-tip standing height. Literally 1.8 m.</summary>
    public float StandHeight { get; set; } = 1.80f;

    /// <summary>Eye height above the feet. 0.9 of stature, so ducking clips your view before your head.</summary>
    public float EyeHeight { get; set; } = 1.62f;

    // --- Motion -------------------------------------------------------------

    /// <summary>Ground speed. Matches Roblox's default 16 studs/s at its documented 0.28 m/stud.</summary>
    public float WalkSpeed { get; set; } = 4.5f;

    public float SprintMultiplier { get; set; } = 1.6f;

    /// <summary>Reaches walk speed in about five ticks — below that, movement feels mushy.</summary>
    public float GroundAcceleration { get; set; } = 50f;

    public float AirAcceleration { get; set; } = 12f;

    /// <summary>
    /// The cap applied to the wish speed INSIDE the acceleration projection.
    /// <b>The one knob that changes the game's identity</b>: at 1.0 you steer in
    /// the air but cannot chain speed; near walk speed you get strafe-jumping.
    /// </summary>
    public float AirSpeedCap { get; set; } = 1.0f;

    public float GroundFriction { get; set; } = 8f;

    /// <summary>Below this speed friction is applied as if at this speed, so a character actually stops.</summary>
    public float StopSpeed { get; set; } = 1.0f;

    public float GravityScale { get; set; } = 1.0f;

    /// <summary>Roughly human terminal velocity. Bounds the broadphase volume rather than preventing tunnelling.</summary>
    public float MaxFallSpeed { get; set; } = 60f;

    // --- Jumping ------------------------------------------------------------

    /// <summary>Peak height of a jump, from which the initial velocity is derived.</summary>
    public float JumpHeight { get; set; } = 1.20f;

    /// <summary>Ticks after leaving ground during which a jump still works.</summary>
    public byte CoyoteTicks { get; set; } = 6;

    /// <summary>Ticks a jump pressed in mid-air stays buffered, so landing-and-jumping is forgiving.</summary>
    public byte JumpBufferTicks { get; set; } = 6;

    /// <summary>Ticks after a jump during which ground detection is suppressed, so the snap cannot undo it.</summary>
    public byte GroundSuppressTicks { get; set; } = 4;

    // --- Ground -------------------------------------------------------------

    /// <summary>Steepest walkable slope. 46° — steeper reads as a wall.</summary>
    public float MaxSlopeAngleDegrees { get; set; } = 46f;

    /// <summary>Highest ledge the step probe will climb.</summary>
    public float StepHeight { get; set; } = 0.45f;

    /// <summary>How far down the character reaches for ground when it would otherwise leave a surface.</summary>
    public float GroundSnapDistance { get; set; } = 0.35f;

    // --- Solver -------------------------------------------------------------

    /// <summary>The rest offset every contact is expressed against.</summary>
    public float SkinWidth { get; set; } = 0.010f;

    public int MaxSlideIterations { get; set; } = 4;

    public int MaxDepenetrationIterations { get; set; } = 4;

    /// <summary>Caps a single tick's depenetration, so a deeply buried character crawls out instead of being fired.</summary>
    public float MaxPushPerTick { get; set; } = 0.5f;

    /// <summary>Derived: the minimum ground-normal Y for a surface to count as walkable.</summary>
    public float MinGroundNormalY => MathF.Cos(MaxSlopeAngleDegrees * MathF.PI / 180f);

    /// <summary>Derived from <see cref="JumpHeight"/> and the shared gravity, so the two can never disagree.</summary>
    public float JumpVelocity =>
        MathF.Sqrt(2f * MathF.Abs(PhysicsDefaults.Gravity.Y) * GravityScale * JumpHeight);
}
