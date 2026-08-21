using System;
using System.Numerics;

namespace SpectraEngine.Core.Scene;

/// <summary>
/// A free-standing view/projection source for a <see cref="Scene"/>.
/// Maintains an orthonormal basis from yaw/pitch angles. The view and
/// projection matrices are cached and rebuilt only when an input changed,
/// because they are read once per renderable per frame; the combined
/// view-projection and its inverse (frustum extraction, picking rays) share
/// the same lazy scheme.
/// </summary>
public sealed class Camera
{
    // Pitch stops just short of ±90°: exactly vertical makes Forward parallel
    // to world up, the Cross in RecomputeBasis collapses to zero, and
    // normalizing that zero vector would NaN the whole basis and view matrix.
    private const float PitchLimit = MathF.PI / 2f - 0.01f;

    private Vector3 _position = new(0f, 0f, 3f);
    private float _fieldOfView = MathF.PI / 3f;
    private float _aspectRatio = 16f / 9f;
    private float _nearPlane = 0.1f;
    private float _farPlane = 1000f;
    private float _yaw = -MathF.PI / 2f;
    private float _pitch;

    private Matrix4x4 _view;
    private Matrix4x4 _projection;
    private bool _viewDirty = true;
    private bool _projectionDirty = true;

    // The combined view-projection and its inverse (for unprojection) depend on
    // BOTH matrices, and the View/Projection getters clear their own flags on
    // read — so the pair gets its own flag, set at every site that dirties
    // either input. Render thread only, like all camera state — the flags are
    // deliberately unsynchronized because scene updates are single-threaded.
    private Matrix4x4 _viewProjection;
    private Matrix4x4 _inverseViewProjection;
    private bool _viewProjectionDirty = true;

    public Vector3 Position
    {
        get => _position;
        set
        {
            if (value == _position) return;
            _position = value;
            _viewDirty = true;
            _viewProjectionDirty = true;
        }
    }

    public Vector3 Forward { get; private set; } = -Vector3.UnitZ;
    public Vector3 Up { get; private set; } = Vector3.UnitY;
    public Vector3 Right { get; private set; } = Vector3.UnitX;

    public float FieldOfView
    {
        get => _fieldOfView;
        set
        {
            if (value == _fieldOfView) return;
            _fieldOfView = value;
            _projectionDirty = true;
            _viewProjectionDirty = true;
        }
    }

    public float AspectRatio
    {
        get => _aspectRatio;
        set
        {
            // Pipelines write this every frame; the early-out keeps the cached
            // projection valid as long as the framebuffer size is stable.
            if (value == _aspectRatio) return;
            _aspectRatio = value;
            _projectionDirty = true;
            _viewProjectionDirty = true;
        }
    }

    public float NearPlane
    {
        get => _nearPlane;
        set
        {
            if (value == _nearPlane) return;
            _nearPlane = value;
            _projectionDirty = true;
            _viewProjectionDirty = true;
        }
    }

    public float FarPlane
    {
        get => _farPlane;
        set
        {
            if (value == _farPlane) return;
            _farPlane = value;
            _projectionDirty = true;
            _viewProjectionDirty = true;
        }
    }

    public float Yaw
    {
        get => _yaw;
        set
        {
            if (value == _yaw) return;
            _yaw = value;
            RecomputeBasis();
        }
    }

    public float Pitch
    {
        get => _pitch;
        set
        {
            float clamped = Math.Clamp(value, -PitchLimit, PitchLimit);
            if (clamped == _pitch) return;
            _pitch = clamped;
            RecomputeBasis();
        }
    }

    public Matrix4x4 View
    {
        get
        {
            if (_viewDirty)
            {
                _view = Matrix4x4.CreateLookAt(_position, _position + Forward, Up);
                _viewDirty = false;
            }
            return _view;
        }
    }

    public Matrix4x4 Projection
    {
        get
        {
            if (_projectionDirty)
            {
                _projection = Matrix4x4.CreatePerspectiveFieldOfView(
                    _fieldOfView, _aspectRatio, _nearPlane, _farPlane);
                _projectionDirty = false;
            }
            return _projection;
        }
    }

    public void LookAt(Vector3 target)
    {
        var offset = target - _position;
        if (offset.LengthSquared() < 1e-12f)
            return; // no defined direction — keep the current orientation

        var direction = Vector3.Normalize(offset);
        _yaw = MathF.Atan2(direction.Z, direction.X);
        // Same clamp as the Pitch setter: a straight-up/straight-down target
        // would otherwise put the basis into the degenerate ±90° case.
        _pitch = Math.Clamp(MathF.Asin(direction.Y), -PitchLimit, PitchLimit);
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
        _viewDirty = true;
        _viewProjectionDirty = true;
    }

    /// <summary>
    /// The combined world→clip matrix in the engine's row-vector convention:
    /// <c>clip = world · View · Projection</c>. Cached (together with its
    /// inverse) behind the same dirty-flag scheme as <see cref="View"/> and
    /// <see cref="Projection"/>, so repeated per-frame reads are free.
    /// </summary>
    public Matrix4x4 GetViewProjection()
    {
        EnsureViewProjection();
        return _viewProjection;
    }

    /// <summary>
    /// The camera's current view frustum in world space, for visibility
    /// queries and culling. A stack-only value snapshot — cheap to build, no
    /// allocation.
    /// </summary>
    public Frustum GetFrustum() => Frustum.FromViewProjection(GetViewProjection());

    /// <summary>
    /// Builds the world-space picking ray through a screen point.
    /// Convention: <paramref name="screenPos"/> is in pixels with the origin at
    /// the TOP-LEFT of the viewport and y growing DOWNWARD — i.e. raw window
    /// mouse coordinates; <paramref name="viewportSize"/> is the viewport extent
    /// in the same units. The ray origin lies on the near plane and the
    /// normalized direction points toward the far plane through that pixel.
    /// </summary>
    public Ray3 ScreenPointToRay(Vector2 screenPos, Vector2 viewportSize)
    {
        // Pixel → NDC: x maps to [-1, 1] left→right; y is flipped because
        // screen y grows down while NDC y grows up.
        float ndcX = 2f * screenPos.X / viewportSize.X - 1f;
        float ndcY = 1f - 2f * screenPos.Y / viewportSize.Y;

        EnsureViewProjection();

        // CreatePerspectiveFieldOfView maps depth to the D3D-style [0, 1] clip
        // range, so the near plane unprojects at z = 0 and the far plane at 1.
        Vector3 nearWorld = UnprojectNdc(ndcX, ndcY, 0f);
        Vector3 farWorld = UnprojectNdc(ndcX, ndcY, 1f);

        // far − near rather than near − Position for the direction: both
        // unprojected points carry absolute float error proportional to their
        // distance, and the long near→far baseline divides that error away.
        return new Ray3(nearWorld, Vector3.Normalize(farWorld - nearWorld));
    }

    /// <summary>Maps an NDC point back to world space through the cached inverse view-projection.</summary>
    private Vector3 UnprojectNdc(float x, float y, float z)
    {
        Vector4 h = Vector4.Transform(new Vector4(x, y, z, 1f), _inverseViewProjection);
        // The inverse of a perspective transform lands at w ≠ 1; dividing by w
        // restores the affine world-space point (the unproject counterpart of
        // the forward perspective divide).
        return new Vector3(h.X, h.Y, h.Z) / h.W;
    }

    private void EnsureViewProjection()
    {
        if (!_viewProjectionDirty) return;

        _viewProjection = View * Projection;
        // A perspective view-projection built from valid parameters is always
        // invertible; the guard only keeps degenerate inputs (e.g. zero fov)
        // from poisoning later unprojections with NaNs.
        if (!Matrix4x4.Invert(_viewProjection, out _inverseViewProjection))
            _inverseViewProjection = Matrix4x4.Identity;
        _viewProjectionDirty = false;
    }
}
