using System;
using System.Numerics;

namespace SpectraEngine.Core.Physics.Character;

/// <summary>
/// The four things a character mover needs from the world: sweep a capsule,
/// gather the planes touching it, solve those planes, and clip velocity.
/// </summary>
/// <remarks>
/// <para>
/// <b>The mover algorithm is this engine's, permanently.</b> Physics libraries
/// ship read-only mover primitives and document nothing about stairs, ground
/// snap or slope limits — Box3D and Jolt are the same shape here — so the
/// algorithm was always going to be ours. What a backend can supply is this
/// seam's first two methods, and the swap is a constructor argument.
/// </para>
/// <para>
/// <b>The last two carry shared implementations on purpose.</b> Two backends
/// with two solvers would resolve identical plane sets to different positions,
/// which is exactly the divergence that would invalidate every tuned constant
/// the day the source was swapped. One solver, shared, removes that axis
/// entirely.
/// </para>
/// </remarks>
public interface ICharacterCollisionSource
{
    /// <summary>
    /// Changes only BETWEEN frames, never between the ticks of one frame.
    /// </summary>
    /// <remarks>
    /// A predicted frame records this; a replay that crosses a change is
    /// refused rather than being silently wrong about a world that moved under
    /// it.
    /// </remarks>
    int Revision { get; }

    /// <summary>The rest offset every method's contract is expressed against.</summary>
    float SkinWidth { get; }

    /// <summary>
    /// Selects the geometry a tick can possibly touch, so every sweep and
    /// gather within it shares one broad phase.
    /// </summary>
    /// <remarks>
    /// Sound because nothing in the scene moves during a tick: kinematic
    /// targets are pushed before the mover runs, and the static world only ever
    /// swaps between frames. Doing it per query instead would repeat the same
    /// broadphase eleven times a tick for an answer that cannot have changed.
    /// </remarks>
    void BeginTick(in Bsp.Aabb volume, in CharacterQueryFilter filter);

    /// <summary>
    /// The fraction of <paramref name="translation"/> travelled before first
    /// contact; 1 when unobstructed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The fraction stops where the capsule's SURFACE separation equals
    /// <see cref="SkinWidth"/>, not zero</b> — so a mover that advances by it
    /// and does no backoff arithmetic of its own always rests clear.
    /// </para>
    /// <para>
    /// <b>A capsule already overlapping returns 0 and still reports a plane.</b>
    /// It never reports "no hit", which is the trap a naive ray test falls into
    /// and the reason a character spawned inside geometry would otherwise sweep
    /// straight through the world.
    /// </para>
    /// </remarks>
    float SweepCapsule(
        in CharacterCapsule capsule,
        Vector3 translation,
        in CharacterQueryFilter filter,
        out CharacterContactPlane plane,
        out CharacterContactSource source);

    /// <summary>
    /// Every contact plane whose separation is at or below
    /// <paramref name="maxSeparation"/>, deepest first; returns how many were
    /// written.
    /// </summary>
    /// <remarks>
    /// On overflow it keeps the deepest and reports the rest through
    /// <see cref="DroppedPlanes"/> — disclosed, never silently truncated.
    /// Allocation-free: the spans are the caller's, and the mover stack-allocates
    /// them.
    /// </remarks>
    int GatherPlanes(
        in CharacterCapsule capsule,
        float maxSeparation,
        in CharacterQueryFilter filter,
        Span<CharacterContactPlane> planes,
        Span<CharacterContactSource> sources);

    /// <summary>Contact planes dropped for want of space since this source was created.</summary>
    int DroppedPlanes { get; }

    /// <summary>
    /// The largest translation toward <paramref name="targetDelta"/> that leaves
    /// every plane satisfied, writing each plane's realised push.
    /// </summary>
    CharacterPlaneSolveResult SolvePlanes(Vector3 targetDelta, Span<CharacterContactPlane> planes) =>
        CharacterPlaneSolver.Solve(targetDelta, planes);

    /// <summary>Removes the components of <paramref name="velocity"/> driving into any engaged plane.</summary>
    Vector3 ClipVelocity(Vector3 velocity, ReadOnlySpan<CharacterContactPlane> planes) =>
        CharacterPlaneSolver.ClipVelocity(velocity, planes);
}

/// <summary>The outcome of a plane solve.</summary>
public readonly record struct CharacterPlaneSolveResult(Vector3 Delta, int IterationCount);

/// <summary>
/// The shared plane solver: accumulated-push Gauss-Seidel, transcribed from
/// Box3D's own mover so a future native source resolves identically.
/// </summary>
/// <remarks>
/// The iteration count and tolerance are the library's, deliberately. A
/// different cap would make any future parity test between two sources
/// meaningless, and the numbers are not tuning — they are the definition of
/// what "solved" means.
/// </remarks>
public static class CharacterPlaneSolver
{
    /// <summary>Solver iterations. Box3D's own count.</summary>
    public const int Iterations = 20;

    /// <summary>Convergence tolerance, in world units. Box3D's linear slop.</summary>
    public const float Tolerance = 0.005f;

    /// <summary>
    /// Finds the translation nearest <paramref name="targetDelta"/> that leaves
    /// every plane's separation non-negative.
    /// </summary>
    /// <remarks>
    /// <b>Accumulated push, not per-iteration push.</b> Each plane tracks the
    /// total it has contributed, so relaxing one plane can give back push
    /// another plane no longer needs — which is what lets a character in a
    /// corner settle instead of being ratcheted outward by whichever plane was
    /// visited last.
    /// </remarks>
    public static CharacterPlaneSolveResult Solve(Vector3 targetDelta, Span<CharacterContactPlane> planes)
    {
        for (int i = 0; i < planes.Length; i++)
            planes[i].Push = 0f;

        Vector3 delta = targetDelta;
        int iteration = 0;

        for (; iteration < Iterations; iteration++)
        {
            float maxCorrection = 0f;

            for (int i = 0; i < planes.Length; i++)
            {
                // The plane's D is the separation at zero translation, so this
                // dot IS the separation after moving by delta.
                float separation = Plane.DotCoordinate(planes[i].Plane, delta);
                float correction = -separation;

                float oldPush = planes[i].Push;
                float newPush = Math.Clamp(oldPush + correction, 0f, planes[i].PushLimit);
                correction = newPush - oldPush;
                planes[i].Push = newPush;

                delta += planes[i].Plane.Normal * correction;
                maxCorrection = MathF.Max(maxCorrection, MathF.Abs(correction));
            }

            if (maxCorrection < Tolerance)
            {
                iteration++;
                break;
            }
        }

        return new CharacterPlaneSolveResult(delta, iteration);
    }

    /// <summary>
    /// Removes velocity driving into any plane that actually engaged.
    /// </summary>
    /// <remarks>
    /// <b>Planes with zero push are skipped.</b> A plane the solve never needed
    /// is a surface the character is near but not resting on, and clipping
    /// against it would brake for walls that are not in the way.
    /// </remarks>
    public static Vector3 ClipVelocity(Vector3 velocity, ReadOnlySpan<CharacterContactPlane> planes)
    {
        for (int i = 0; i < planes.Length; i++)
        {
            if (!planes[i].ClipVelocity || planes[i].Push == 0f)
                continue;

            float into = Vector3.Dot(velocity, planes[i].Plane.Normal);
            if (into < 0f)
                velocity -= planes[i].Plane.Normal * into;
        }

        return velocity;
    }
}
