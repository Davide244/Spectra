using SpectraEngine.Core.Scene;
using System;
using System.Numerics;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The euler conversion a property panel shows, against the quaternion the
/// scene stores.
/// </summary>
/// <remarks>
/// <b>The claim is about the ROTATION, never about the numbers.</b> Euler
/// triples are three-to-one, so a test that compared the angles that went in
/// against the angles that came out would be pinning one arbitrary branch of a
/// many-to-one map. What has to hold is that the rotation survives: feed the
/// angles back and the quaternion means the same thing, which is checked by
/// rotating basis vectors rather than by comparing components (q and -q are the
/// same rotation).
/// </remarks>
public sealed class EulerAnglesTests
{
    private static void ShouldRotateAlike(Quaternion expected, Quaternion actual, string because)
    {
        // Three non-parallel probes pin a rotation completely, and comparing
        // where they land dodges the q == -q double cover that a component-wise
        // comparison trips over.
        foreach (Vector3 probe in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
        {
            Vector3 want = Vector3.Transform(probe, expected);
            Vector3 got = Vector3.Transform(probe, actual);
            Vector3.Distance(want, got).ShouldBeLessThan(1e-4f, because);
        }
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(90f, 0f, 0f)]
    [InlineData(0f, 45f, 0f)]
    [InlineData(0f, 0f, 30f)]
    [InlineData(35f, 20f, -15f)]
    [InlineData(-170f, 12f, 175f)]
    [InlineData(12.5f, -47.25f, 88f)]
    public void A_rotation_survives_a_trip_through_euler_angles(float yaw, float pitch, float roll)
    {
        Quaternion original = new EulerAngles(yaw, pitch, roll).ToQuaternion();

        Quaternion round = EulerAngles.FromQuaternion(original).ToQuaternion();

        ShouldRotateAlike(original, round, $"yaw {yaw}, pitch {pitch}, roll {roll}");
    }

    [Fact]
    public void The_convention_matches_the_one_the_engine_already_writes_in_code()
    {
        // Three call sites build rotations with CreateFromYawPitchRoll. If this
        // used a different order, an angle typed into a panel would disagree
        // with the same angle written in SceneManager, and nothing would report
        // it.
        var angles = new EulerAngles(Yaw: 25f, Pitch: -40f, Roll: 10f);

        Quaternion expected = Quaternion.CreateFromYawPitchRoll(
            25f * MathF.PI / 180f, -40f * MathF.PI / 180f, 10f * MathF.PI / 180f);

        ShouldRotateAlike(expected, angles.ToQuaternion(), "the yaw/pitch/roll order is the engine's");
    }

    [Theory]
    [InlineData(90f)]
    [InlineData(-90f)]
    public void Gimbal_lock_still_produces_the_rotation_that_was_asked_for(float pitch)
    {
        // At +/-90 the yaw and roll axes align, so only their sum is
        // recoverable and the numbers legitimately come back redistributed.
        // What must not happen is the rotation changing, or an asin outside
        // [-1, 1] producing NaN and quietly poisoning the transform.
        Quaternion original = new EulerAngles(Yaw: 30f, Pitch: pitch, Roll: 20f).ToQuaternion();

        EulerAngles extracted = EulerAngles.FromQuaternion(original);

        float.IsNaN(extracted.Yaw).ShouldBeFalse();
        float.IsNaN(extracted.Pitch).ShouldBeFalse();
        float.IsNaN(extracted.Roll).ShouldBeFalse();
        ShouldRotateAlike(original, extracted.ToQuaternion(), $"at pitch {pitch}");
    }

    [Fact]
    public void A_drifted_quaternion_is_normalised_rather_than_producing_nonsense()
    {
        // A rotation composed over many frames drifts off the unit sphere, and
        // asin is undefined past 1. Unguarded that is a NaN that spreads into
        // every transform downstream with nothing reporting where it started.
        Quaternion drifted = new EulerAngles(10f, 20f, 30f).ToQuaternion() * 1.05f;

        EulerAngles extracted = EulerAngles.FromQuaternion(drifted);

        float.IsNaN(extracted.Pitch).ShouldBeFalse();
        ShouldRotateAlike(Quaternion.Normalize(drifted), extracted.ToQuaternion(), "drifted input");
    }

    [Fact]
    public void The_display_vector_is_pitch_yaw_roll_in_x_y_z()
    {
        // X is rotation about x, Y about y, Z about z. Disagreeing with that in
        // a three-field row would be a trap rather than a preference.
        var angles = new EulerAngles(Yaw: 1f, Pitch: 2f, Roll: 3f);

        angles.AsDegrees.ShouldBe(new Vector3(2f, 1f, 3f));
        EulerAngles.FromDegrees(angles.AsDegrees).ShouldBe(angles);
    }
}
