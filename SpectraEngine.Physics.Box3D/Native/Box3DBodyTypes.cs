using System.Runtime.InteropServices;

namespace SpectraEngine.Physics.Box3D.Native;

/// <summary>What drives a body's transform.</summary>
/// <remarks>
/// Maps onto the engine's own three authorities: <c>Static</c> is authored and
/// never written back; <c>Kinematic</c> is driven by the scene through target
/// transforms; only <c>Dynamic</c> is written back <em>from</em> physics.
/// </remarks>
public enum B3BodyType
{
    /// <summary>Never moves. One per occupied chunk cell carries the world's hulls.</summary>
    Static = 0,

    /// <summary>Moved by the scene, not by the solver. Platforms, part brushes, the character's presence body.</summary>
    Kinematic = 1,

    /// <summary>Moved by the solver. The only kind whose transform is drained back into the scene.</summary>
    Dynamic = 2,
}

/// <summary>Per-axis motion constraints.</summary>
/// <remarks>Six C <c>bool</c>s, so six bytes — see <see cref="B3WorldDef"/> on why they are not C# bools.</remarks>
[StructLayout(LayoutKind.Sequential)]
public struct B3MotionLocks
{
    public byte LinearX;
    public byte LinearY;
    public byte LinearZ;
    public byte AngularX;
    public byte AngularY;
    public byte AngularZ;
}

/// <summary>The parameters a body is created from.</summary>
/// <remarks>
/// Take it from <c>b3DefaultBodyDef()</c> and mutate — never construct one.
/// See <see cref="B3WorldDef"/> for why: the trailing construction cookie is
/// checked only in Debug, so a hand-built def is silently accepted in a
/// shipping build.
/// <para>
/// <see cref="Name"/> is a <c>const char*</c> the library does not copy. Leave
/// it null; a managed string marshalled here would be freed while the body
/// still points at it.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct B3BodyDef
{
    public B3BodyType Type;
    public B3Pos Position;
    public B3Quat Rotation;
    public B3Vec3 LinearVelocity;
    public B3Vec3 AngularVelocity;
    public float LinearDamping;
    public float AngularDamping;
    public float GravityScale;
    public float SleepThreshold;

    /// <summary>Borrowed <c>const char*</c>, never copied by the library. Keep null.</summary>
    public nint Name;

    /// <summary>
    /// The engine's back-reference slot. Holds a dense int index plus one — not
    /// a pinned handle — so teardown ordering stays simple and nothing is
    /// allocated per body.
    /// </summary>
    public nint UserData;

    public B3MotionLocks MotionLocks;
    public byte EnableSleep;
    public byte IsAwake;
    public byte IsBullet;
    public byte IsEnabled;
    public byte AllowFastRotation;
    public byte EnableContactRecycling;

    /// <summary>The library's construction cookie. Never assign.</summary>
    public int InternalValue;
}

/// <summary>Collision filtering bits.</summary>
/// <remarks>
/// <b>64 bits of category and mask</b> — which is exactly why the engine's own
/// <c>CollisionGroups</c> registry caps at 64 named groups rather than Roblox's
/// 32: that is what the representation actually affords.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct B3Filter
{
    public ulong CategoryBits;
    public ulong MaskBits;
    public int GroupIndex;
}

/// <summary>Surface response for one shape.</summary>
/// <remarks>
/// A hull carries exactly ONE of these, which is why per-face friction cannot
/// be expressed through the solver: a six-textured brush resolves its face
/// material engine-side from the contact normal instead, and the hull's base
/// material only needs to be sane.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct B3SurfaceMaterial
{
    public float Friction;
    public float Restitution;
    public float RollingResistance;
    public B3Vec3 TangentVelocity;
    public ulong UserMaterialId;
    public uint CustomColor;

    /// <summary>Explicit tail padding in the C struct. Present so the layout matches; never read.</summary>
    public uint Padding;
}

/// <summary>The parameters a shape is created from.</summary>
/// <remarks>
/// Take it from <c>b3DefaultShapeDef()</c> and mutate. <see cref="Materials"/>
/// is a borrowed array pointer and <see cref="Name"/> a borrowed string — leave
/// both null and set <see cref="BaseMaterial"/> instead.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct B3ShapeDef
{
    /// <summary>Borrowed <c>const char*</c>, never copied. Keep null.</summary>
    public nint Name;

    public nint UserData;

    /// <summary>Borrowed array pointer for per-face materials. Keep null; use <see cref="BaseMaterial"/>.</summary>
    public nint Materials;

    public int MaterialCount;
    public B3SurfaceMaterial BaseMaterial;
    public float Density;
    public float ExplosionScale;
    public B3Filter Filter;
    public byte EnableCustomFiltering;
    public byte IsSensor;
    public byte EnableSensorEvents;
    public byte EnableContactEvents;
    public byte EnableHitEvents;
    public byte EnablePreSolveEvents;
    public byte InvokeContactCreation;

    /// <summary>
    /// Recompute the body's mass when this shape is added. <b>Set it off while
    /// batching a chunk's hulls</b> and recompute once at the end — a static
    /// body has no mass to recompute anyway, and leaving it on makes a
    /// hundred-brush cell do a hundred redundant passes.
    /// </summary>
    public byte UpdateBodyMass;

    public byte EnableSpeculativeContact;

    /// <summary>The library's construction cookie. Never assign.</summary>
    public int InternalValue;
}
