using System;
using System.Numerics;

namespace SpectraEngine.Core.Graphics;

public sealed class Camera
{
    public Vector3 Position { get; set; } = new(0f, 0f, 3f);
    public Vector3 Forward { get; private set; } = -Vector3.UnitZ;
    public Vector3 Up { get; private set; } = Vector3.UnitY;
    public Vector3 Right { get; private set; } = Vector3.UnitX;

    public float FieldOfView { get; set; } = MathF.PI / 3f;
    public float AspectRatio { get; set; } = 16f / 9f;
    public float NearPlane { get; set; } = 0.1f;
    public float FarPlane { get; set; } = 1000f;

    private float _yaw = -MathF.PI / 2f;
    private float _pitch = 0f;

    public float Yaw
    {
        get => _yaw;
        set { _yaw = value; RecomputeBasis(); }
    }

    public float Pitch
    {
        get => _pitch;
        set
        {
            const float limit = MathF.PI / 2f - 0.01f;
            _pitch = Math.Clamp(value, -limit, limit);
            RecomputeBasis();
        }
    }

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Position + Forward, Up);

    public Matrix4x4 Projection =>
        Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, AspectRatio, NearPlane, FarPlane);

    public void LookAt(Vector3 target)
    {
        var direction = Vector3.Normalize(target - Position);
        _pitch = MathF.Asin(direction.Y);
        _yaw = MathF.Atan2(direction.Z, direction.X);
        RecomputeBasis();
    }

    private void RecomputeBasis()
    {
        var cosPitch = MathF.Cos(_pitch);
        Forward = Vector3.Normalize(new Vector3(
            MathF.Cos(_yaw) * cosPitch,
            MathF.Sin(_pitch),
            MathF.Sin(_yaw) * cosPitch));
        Right = Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));
        Up = Vector3.Normalize(Vector3.Cross(Right, Forward));
    }
}
