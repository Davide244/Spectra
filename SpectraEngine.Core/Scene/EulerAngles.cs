using System;
using System.Numerics;

namespace SpectraEngine.Core.Scene;

/// <summary>
/// Yaw, pitch and roll in degrees, and the conversion to and from the
/// quaternion the scene actually stores.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists for people, not for the engine.</b> Nothing in the scene
/// graph, the physics or the renderer wants euler angles: rotations are stored
/// and composed as quaternions throughout, because that is what interpolates
/// and concatenates without drifting. But a quaternion is four numbers with no
/// individually meaningful component, so a property panel showing one is a
/// panel nobody can type into.
/// </para>
/// <para>
/// <b>The convention is <c>Quaternion.CreateFromYawPitchRoll</c>'s, because
/// that is the one already used in the tree.</b> Yaw is around Y, pitch around
/// X, roll around Z, applied roll then pitch then yaw. Picking a different
/// order here would make an angle typed into a panel disagree with the same
/// angle written in code.
/// </para>
/// <para>
/// <b>The round trip is exact in ROTATION, not in numbers, and that is
/// inherent.</b> Euler triples are three-to-one: (0, 0, 0) and (180, 180, 180)
/// name rotations that differ by nothing a renderer can see, and at
/// pitch = +/-90 the yaw and roll axes align so only their sum is recoverable.
/// The extraction below picks the canonical branch and folds the whole rotation
/// into yaw at that singularity, which is what every editor does; the
/// consequence a user sees is that typing 90 into pitch may read back with the
/// other two numbers redistributed.
/// </para>
/// </remarks>
public readonly record struct EulerAngles(float Yaw, float Pitch, float Roll)
{
    /// <summary>The angles as a vector of degrees, in pitch/yaw/roll display order.</summary>
    /// <remarks>
    /// X is pitch, Y is yaw, Z is roll, so the vector reads as rotation about
    /// the x, y and z axes in that order. That is what a three-field row is
    /// labelled with everywhere else in this industry, and disagreeing with it
    /// would be a trap rather than a preference.
    /// </remarks>
    public Vector3 AsDegrees => new(Pitch, Yaw, Roll);

    /// <summary>Builds from a pitch/yaw/roll degree vector.</summary>
    public static EulerAngles FromDegrees(Vector3 degrees) => new(degrees.Y, degrees.X, degrees.Z);

    /// <summary>The quaternion these angles name.</summary>
    public Quaternion ToQuaternion() => Quaternion.CreateFromYawPitchRoll(
        Yaw * MathF.PI / 180f, Pitch * MathF.PI / 180f, Roll * MathF.PI / 180f);

    /// <summary>
    /// Extracts the canonical angles of a rotation.
    /// </summary>
    /// <remarks>
    /// Normalised first: the caller's quaternion may have drifted, and the
    /// <c>asin</c> below is undefined outside [-1, 1], which a drifted
    /// quaternion reaches. The clamp is a second guard for the case where
    /// rounding puts it a hair past the limit even after normalising.
    /// </remarks>
    public static EulerAngles FromQuaternion(Quaternion rotation)
    {
        Quaternion q = Quaternion.Normalize(rotation);

        // sin(pitch) from the rotation matrix's m23 term, for the yaw-pitch-roll
        // (Y, X, Z) order CreateFromYawPitchRoll composes in.
        float sinPitch = 2f * ((q.W * q.X) - (q.Y * q.Z));
        sinPitch = Math.Clamp(sinPitch, -1f, 1f);
        float pitch = MathF.Asin(sinPitch);

        float yaw, roll;
        if (MathF.Abs(sinPitch) > 0.99999f)
        {
            // Gimbal lock: the yaw and roll axes are parallel, so only their sum
            // is recoverable. Fold it into yaw and leave roll at zero, which is
            // the choice that keeps a level-designer's "spin this about Y" case
            // reading back the way they typed it.
            yaw = 2f * MathF.Atan2(q.Y, q.W);
            roll = 0f;
        }
        else
        {
            yaw = MathF.Atan2(2f * ((q.W * q.Y) + (q.X * q.Z)),
                              1f - (2f * ((q.X * q.X) + (q.Y * q.Y))));
            roll = MathF.Atan2(2f * ((q.W * q.Z) + (q.X * q.Y)),
                               1f - (2f * ((q.X * q.X) + (q.Z * q.Z))));
        }

        const float ToDegrees = 180f / MathF.PI;
        return new EulerAngles(yaw * ToDegrees, pitch * ToDegrees, roll * ToDegrees);
    }
}
