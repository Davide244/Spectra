using System.Numerics;

namespace SpectraEngine.Core.Physics;

/// <summary>
/// The tick rate and the world constants, stated once, in spectraunits.
/// </summary>
/// <remarks>
/// <para>
/// <b>One spectraunit (<c>sunit</c>) is one metre.</b> That decision is the
/// reason this file is short: a backend whose length unit is also the metre
/// needs no conversion at the binding boundary and runs its collision and
/// constraint tolerances at exactly the scale they were tuned for. What still
/// has to be restated per engine is anything whose unit is not a pure length —
/// gravity, sleep thresholds, density — which is what lives here.
/// </para>
/// <para>
/// <b>A ported Roblox place must scale its constants, and would have had to
/// under any answer.</b> Roblox is internally inconsistent about its own stud:
/// the Creator Hub documents 0.28 m/stud, while its default gravity of
/// 196.2 studs/s² against 9.81 m/s² implies 0.05 m/stud — a factor of 5.6
/// between two official figures. Multiply by <see cref="MetresPerRobloxStud"/>
/// to follow the documentation or by <see cref="MetresPerRobloxGravityStud"/>
/// to reproduce the behaviour; pick one per project and stay consistent.
/// </para>
/// </remarks>
public static class PhysicsDefaults
{
    /// <summary>Metres in one spectraunit. One, by decision — see the type remarks.</summary>
    public const float MetresPerUnit = 1f;

    /// <summary>Roblox's <em>documented</em> stud, for porting arithmetic.</summary>
    public const float MetresPerRobloxStud = 0.28f;

    /// <summary>
    /// The stud Roblox's <em>default gravity</em> implies, which disagrees with
    /// the documented one by a factor of 5.6. Recorded so a porter can pick the
    /// one that matches what they are porting — numbers or feel — rather than
    /// discovering the inconsistency themselves.
    /// </summary>
    public const float MetresPerRobloxGravityStud = 0.05f;

    /// <summary>Earth gravity, in sunits per second squared.</summary>
    public static readonly Vector3 Gravity = new(0f, -9.81f, 0f);

    /// <summary>
    /// Simulation ticks per second. Fixed, and deliberately independent of the
    /// frame rate: a step whose size varies with frame time makes the
    /// simulation a function of how fast the machine is, which breaks
    /// determinism, replay and any future server reconciliation.
    /// </summary>
    public const int TicksPerSecond = 60;

    /// <summary>Seconds in one tick.</summary>
    public const float FixedDeltaTime = 1f / TicksPerSecond;

    /// <summary>
    /// The most ticks one frame may run before the accumulator gives up and
    /// drops the remaining time.
    /// </summary>
    /// <remarks>
    /// <b>The cap is what stops the spiral of death.</b> If a frame takes longer
    /// than the ticks it owes, the debt grows, which makes the next frame
    /// longer still — an unrecoverable slide that presents as a hard freeze
    /// rather than as a slowdown. Dropping simulated time is the lesser evil and
    /// is visible (things move slower than wall-clock) instead of fatal.
    /// </remarks>
    public const int MaxTicksPerFrame = 5;

    /// <summary>
    /// Speed below which a body may be put to sleep, in sunits per second.
    /// Restated here rather than inherited because a backend's own default is
    /// in metres per second and only <em>lengths</em> rescale automatically.
    /// </summary>
    public const float SleepThreshold = 0.05f;

    /// <summary>Default body density, in kg per cubic sunit.</summary>
    public const float Density = 1000f;
}
