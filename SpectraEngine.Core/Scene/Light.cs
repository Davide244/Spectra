using System;
using System.Numerics;

namespace SpectraEngine.Core.Scene;

/// <summary>What shape a light's emission has.</summary>
public enum LightKind
{
    /// <summary>
    /// Parallel rays from infinitely far away: a sun. Position is ignored and
    /// the node's forward axis is the direction the light travels.
    /// </summary>
    Directional,

    /// <summary>
    /// Radiates from the node's position in every direction, falling off with
    /// distance and stopping at <see cref="Light.Range"/>.
    /// </summary>
    Point,

    /// <summary>
    /// A cone from the node's position along its forward axis, with a soft edge
    /// between <see cref="Light.InnerAngle"/> and <see cref="Light.OuterAngle"/>.
    /// </summary>
    Spot,

    /// <summary>
    /// A one-sided rectangle emitting from its whole surface, facing along the
    /// node's forward axis and sized by <see cref="Light.Width"/> and
    /// <see cref="Light.Height"/>.
    /// </summary>
    /// <remarks>
    /// <b>This is the "surface light" shape.</b> A panel flush on a wall or a
    /// ceiling, which is what a real light fitting is and what a point light in
    /// the same place cannot look like: a point casts one hard shadow from a
    /// dimensionless origin, and an area light's soft, directional wash is the
    /// entire difference.
    /// </remarks>
    Rect,

    /// <summary>
    /// A one-sided disc, as <see cref="Rect"/> but circular and sized by
    /// <see cref="Light.Radius"/>.
    /// </summary>
    Disc,
}

/// <summary>
/// A light attached to a <see cref="SceneNode"/>, parallel to
/// <see cref="MeshRenderer"/>: the node supplies the position and orientation,
/// this supplies everything else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Colour is linear, and intensity is separate from it.</b> Everything the
/// engine shades with is linear light, so a light's colour is a linear triple
/// and not a display colour; <c>ColorSpace.SrgbToLinear</c> is what turns a
/// picked colour into one. Keeping intensity out of the colour means a light can
/// be brightened past 1 without its hue drifting, which is exactly what the HDR
/// target and the tone curve exist to carry.
/// </para>
/// <para>
/// <b>Mutable, unlike a <c>Brush</c>.</b> A brush is immutable because the CSG
/// caches key on its identity; a light feeds nothing derived, so animating one
/// is an ordinary property write and costs no recompile.
/// </para>
/// </remarks>
public sealed class Light
{
    private float _intensity = 1f;
    private float _range = 10f;
    private float _innerAngle = 25f;
    private float _outerAngle = 35f;
    private float _width = 1f;
    private float _height = 1f;
    private float _radius = 0.5f;

    /// <summary>Directional or point. See <see cref="LightKind"/>.</summary>
    public LightKind Kind { get; set; } = LightKind.Directional;

    /// <summary>Linear RGB colour. Not a display colour; see the remarks.</summary>
    public Vector3 Color { get; set; } = Vector3.One;

    /// <summary>Multiplier on <see cref="Color"/>. Negative values are refused.</summary>
    public float Intensity
    {
        get => _intensity;
        set => _intensity = value >= 0f
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Light intensity cannot be negative.");
    }

    /// <summary>
    /// Distance at which a point light's contribution reaches zero. Ignored by a
    /// directional light, which has no falloff.
    /// </summary>
    /// <remarks>
    /// A hard cutoff rather than a physical inverse-square tail, because a light
    /// that never quite reaches zero can never be culled: every light would
    /// affect every surface and the nearest-N selection would be arbitrary
    /// rather than merely limited.
    /// </remarks>
    public float Range
    {
        get => _range;
        set => _range = value > 0f
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Light range must be positive.");
    }

    /// <summary>Whether this light contributes at all. A disabled light is collected by nothing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// A spot light's fully-lit half-angle, in degrees. Everything within this
    /// of the cone axis gets the light's full strength.
    /// </summary>
    /// <remarks>
    /// <b>Half-angles, not full ones, and degrees rather than a cosine.</b> A
    /// half-angle is what the geometry actually is - the angle from the axis to
    /// the cone's edge - and it is what a gizmo drags; the cosine the shader
    /// wants is derived at upload time, where it belongs, because a cosine typed
    /// into a property panel is a number nobody can picture.
    /// <para>
    /// Clamped rather than refused, unlike range and intensity: an angle has a
    /// meaningful ceiling (a half-angle past 90 degrees is a cone pointing
    /// backwards, which is not a shape anyone means) and a value pushed past it
    /// by a drag should stop rather than throw from inside a transaction.
    /// </para>
    /// </remarks>
    public float InnerAngle
    {
        get => _innerAngle;
        set => _innerAngle = Math.Clamp(value, 0f, MaxConeAngle);
    }

    /// <summary>
    /// A spot light's outer half-angle, in degrees: the edge of the cone, past
    /// which nothing is lit.
    /// </summary>
    /// <remarks>
    /// <b>Never smaller than <see cref="InnerAngle"/>.</b> An outer angle inside
    /// the inner one inverts the falloff, which produces a cone that is dark in
    /// the middle and bright at the rim - a picture with no error attached.
    /// The clamp is applied on read as well as on write, because the two
    /// properties can be set in either order and enforcing it only on write
    /// would make the result depend on which was assigned last.
    /// </remarks>
    public float OuterAngle
    {
        get => Math.Max(_outerAngle, _innerAngle);
        set => _outerAngle = Math.Clamp(value, 0f, MaxConeAngle);
    }

    /// <summary>A rect light's width in world units, along the node's right axis.</summary>
    public float Width
    {
        get => _width;
        set => _width = Math.Max(value, MinimumExtent);
    }

    /// <summary>A rect light's height in world units, along the node's up axis.</summary>
    public float Height
    {
        get => _height;
        set => _height = Math.Max(value, MinimumExtent);
    }

    /// <summary>A disc light's radius in world units.</summary>
    public float Radius
    {
        get => _radius;
        set => _radius = Math.Max(value, MinimumExtent);
    }

    /// <summary>The largest half-angle a cone may have.</summary>
    public const float MaxConeAngle = 89f;

    /// <summary>
    /// The smallest an area light's extent may be.
    /// </summary>
    /// <remarks>
    /// Not zero, and the shader depends on it: the representative-point
    /// approximation divides by the shape's own size, so a zero extent is a
    /// division by zero that renders as a black or infinite pixel rather than
    /// throwing. At this size an area light is a point light to within a pixel,
    /// which is the graceful degradation the pixel tests assert.
    /// </remarks>
    public const float MinimumExtent = 0.001f;

    /// <summary>A new light carrying the same settings.</summary>
    /// <remarks>
    /// <b>This exists because a light is the one MUTABLE payload a node can
    /// carry.</b> Duplicating a node shares its mesh by reference (a
    /// <c>MeshRenderer</c> is immutable and its GPU resources are
    /// renderer-owned) and gives it its own brush (see
    /// <c>Brush.CloneShape</c>). A light shared the way a mesh is shared would
    /// mean dimming the copy dims the original, with nothing anywhere to say
    /// why, so a duplicate takes a copy.
    /// </remarks>
    public Light Clone() => new()
    {
        Kind = Kind,
        Color = Color,
        Intensity = Intensity,
        Range = Range,
        Enabled = Enabled,
        InnerAngle = InnerAngle,
        OuterAngle = OuterAngle,
        Width = Width,
        Height = Height,
        Radius = Radius,
    };

    /// <summary>
    /// The node rotation that makes a directional light travel along
    /// <paramref name="travelDirection"/>. A sun wants a direction with a
    /// negative Y, because that is the way sunlight goes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists because euler angles get the sign wrong silently.</b> A
    /// directional light takes its direction from the node's forward axis, so
    /// authoring one means picking a yaw and a pitch whose composed +Z axis
    /// happens to point where the light should go, and the pitch that reads
    /// like "tilted down" produces a forward axis tilted UP, because the axis
    /// is the third row of the rotation and rotating +Z about +X by a negative
    /// angle raises it. The demo's own sun shone upward from below for exactly
    /// this reason: nothing errors, nothing warns, and the scene is merely
    /// darker than it should be with the lit side facing away from every camera
    /// anyone points at it.
    /// </para>
    /// <para>
    /// Stating the direction removes the arithmetic. The roll is unconstrained
    /// and arbitrary: a directional light is rotationally symmetric about its
    /// own axis, so any rotation carrying +Z to the direction is as good as any
    /// other.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The direction has no length.</exception>
    public static Quaternion RotationForDirection(Vector3 travelDirection)
    {
        if (travelDirection.LengthSquared() < 1e-12f)
            throw new ArgumentException("A light direction needs a length.", nameof(travelDirection));

        Vector3 forward = Vector3.Normalize(travelDirection);

        // Any reference that is not parallel to the direction will do; world up
        // fails exactly for a light pointing straight down, which is the most
        // likely direction anybody asks for.
        Vector3 reference = MathF.Abs(forward.Y) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
        Vector3 right = Vector3.Normalize(Vector3.Cross(reference, forward));
        Vector3 up = Vector3.Cross(forward, right);

        // Rows, because the engine's convention is row vectors: v * M. The third
        // row is what a node's world matrix reports as its forward axis, and is
        // what Scene.CollectLights reads.
        var basis = new Matrix4x4(
            right.X, right.Y, right.Z, 0f,
            up.X, up.Y, up.Z, 0f,
            forward.X, forward.Y, forward.Z, 0f,
            0f, 0f, 0f, 1f);

        return Quaternion.CreateFromRotationMatrix(basis);
    }
}
