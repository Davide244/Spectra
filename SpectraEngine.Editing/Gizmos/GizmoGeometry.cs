using SpectraEngine.Core.Scene;
using System;
using System.Numerics;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// One frame's world-space shape of <em>any</em> of the three manipulators:
/// where the pivot is, which frame the handles are laid out in, how big one
/// handle is in world units, and the camera basis the screen-facing parts are
/// built in.
/// </summary>
/// <remarks>
/// <b>Rendering and hit-testing both read this and nothing else</b>, which is
/// what guarantees you can only grab what you can see. It is rebuilt from the
/// camera every frame — a gizmo that kept stale geometry would drift out from
/// under the cursor as the view moved — and it is a readonly struct passed by
/// <c>in</c>, so rebuilding one allocates nothing.
/// <para>
/// <b>One geometry type for translate, rotate and scale</b> rather than three.
/// The constant-screen-size solve, the frame basis, the camera basis and the
/// world-length-to-pixels conversion are identical for all three tools; only the
/// proportions differ, and those are a handful of factors of
/// <see cref="AxisLength"/> exposed as properties below. Three copies of this
/// struct would be three chances for picking and drawing to drift apart in
/// different ways.
/// </para>
/// <para>
/// <b>The gizmo has a constant screen size.</b> <see cref="AxisLength"/> is
/// derived from the pivot's depth in front of the camera through
/// <see cref="GizmoMath.WorldPerPixel"/>, so an arrow drawn for a brush two
/// units away and one drawn for a brush twenty thousand units away cover the
/// same <see cref="Build"/>-supplied number of pixels. Without that, a
/// manipulator in an open world shrinks below the pick tolerance long before
/// the thing it manipulates does.
/// </para>
/// <para>
/// <b>The frame is not always the world.</b> <see cref="AxisX"/>/<see cref="AxisY"/>/
/// <see cref="AxisZ"/> are the orthonormal basis the handles are laid out in:
/// the world axes in <see cref="GizmoOrientation.World"/>, the reference node's
/// world rotation in <see cref="GizmoOrientation.Local"/>. Everything that used
/// to read <see cref="GizmoHandles.AxisDirection"/> reads
/// <see cref="Axis(GizmoHandle)"/> instead, so the same code drives both
/// orientations.
/// </para>
/// <para>
/// <b>Threading:</b> built and consumed on the render thread, which owns the
/// camera and the scene — the same rule as <c>Scene</c>.
/// </para>
/// </remarks>
public readonly struct GizmoGeometry
{
    /// <summary>
    /// The default on-screen length of one handle, in pixels — the length of a
    /// translate arrow, and the length the other tools' proportions are quoted
    /// against. Sized so the whole gizmo is comfortably grabbable without
    /// swallowing the object it sits on.
    /// </summary>
    public const float DefaultPixelSize = 96f;

    // Translate proportions, in units of AxisLength, so the pixel size stays
    // the single knob. The plane quads start well clear of the centre disc and
    // stop well short of the arrowheads, which is what keeps the three handle
    // families separable under the pick tolerance.
    private const float PlaneOffsetFactor = 0.32f;
    private const float PlaneSizeFactor = 0.26f;
    private const float ScreenRadiusFactor = 0.14f;

    // Rotate proportions. The axis rings sit just inside where a translate arrow
    // would end, so switching modes does not make the gizmo jump size; the
    // view-axis ring sits outside all three so it is grabbable everywhere along
    // its circumference instead of only where no axis ring crosses it.
    private const float RingRadiusFactor = 0.86f;
    private const float ScreenRingRadiusFactor = 1.1f;

    // Scale proportions: a cube handle at the end of each axis, sized so its
    // silhouette is a comfortably-larger target than the shaft it caps.
    private const float HandleBoxRadiusFactor = 0.075f;

    private GizmoGeometry(
        Vector3 pivot,
        Vector3 axisX,
        Vector3 axisY,
        Vector3 axisZ,
        float handleLength,
        float worldPerPixel,
        float viewDepth,
        Vector3 viewRight,
        Vector3 viewUp,
        Vector3 viewNormal)
    {
        Pivot = pivot;
        AxisX = axisX;
        AxisY = axisY;
        AxisZ = axisZ;
        AxisLength = handleLength;
        WorldPerPixel = worldPerPixel;
        ViewDepth = viewDepth;
        ViewRight = viewRight;
        ViewUp = viewUp;
        ViewNormal = viewNormal;
    }

    /// <summary>The world-space point the gizmo is centred on — the selection pivot.</summary>
    public Vector3 Pivot { get; }

    /// <summary>The frame's first axis: world +x, or the reference node's local +x rotated into world space.</summary>
    public Vector3 AxisX { get; }

    /// <summary>The frame's second axis. See <see cref="AxisX"/>.</summary>
    public Vector3 AxisY { get; }

    /// <summary>The frame's third axis. See <see cref="AxisX"/>.</summary>
    public Vector3 AxisZ { get; }

    /// <summary>
    /// The world length of one handle, chosen so it covers a fixed number of
    /// pixels at the pivot's depth.
    /// </summary>
    public float AxisLength { get; }

    /// <summary>
    /// World units per viewport pixel at the pivot's depth. Hit-testing divides
    /// a world miss distance by this to get a pixel distance, which is what
    /// makes the pick tolerance a screen-space quantity.
    /// </summary>
    /// <remarks>
    /// Evaluated once, at the pivot, rather than per query point: the gizmo
    /// spans about a hundred pixels, so the depth (and therefore the scale)
    /// varies by a fraction of a percent across it — far below the pick
    /// tolerance — and a single value keeps picking exactly consistent with the
    /// size the gizmo was drawn at.
    /// </remarks>
    public float WorldPerPixel { get; }

    /// <summary>
    /// The pivot's depth along the camera's view axis, unclamped: negative when
    /// the selection is behind the camera. See <see cref="IsBehindCamera"/>.
    /// </summary>
    public float ViewDepth { get; }

    /// <summary>The camera's right vector, for building screen-facing geometry.</summary>
    public Vector3 ViewRight { get; }

    /// <summary>The camera's up vector, for building screen-facing geometry.</summary>
    public Vector3 ViewUp { get; }

    /// <summary>
    /// The camera's forward vector — the normal of the screen handle's
    /// constraint plane, and the axis the view-aligned rotate ring spins about.
    /// </summary>
    public Vector3 ViewNormal { get; }

    /// <summary>
    /// True when the pivot sits at or behind the camera plane, where the gizmo
    /// projects to nothing coherent. Callers skip drawing and picking rather
    /// than rendering a mirrored gizmo behind the viewer.
    /// </summary>
    public bool IsBehindCamera => ViewDepth <= 0f;

    /// <summary>How far from the pivot a translate plane quad's near corner sits.</summary>
    public float PlaneOffset => AxisLength * PlaneOffsetFactor;

    /// <summary>The edge length of a translate plane quad.</summary>
    public float PlaneSize => AxisLength * PlaneSizeFactor;

    /// <summary>The radius of the screen-facing centre disc (translate) / uniform cube (scale).</summary>
    public float ScreenRadius => AxisLength * ScreenRadiusFactor;

    /// <summary>The radius of one rotate axis ring.</summary>
    public float RingRadius => AxisLength * RingRadiusFactor;

    /// <summary>The radius of the rotate gizmo's view-aligned outer ring.</summary>
    public float ScreenRingRadius => AxisLength * ScreenRingRadiusFactor;

    /// <summary>The half-extent of a scale gizmo's cube handle.</summary>
    public float HandleBoxRadius => AxisLength * HandleBoxRadiusFactor;

    /// <summary>
    /// Builds the gizmo's geometry for one frame.
    /// </summary>
    /// <param name="camera">The viewport camera; supplies the basis and the perspective scale.</param>
    /// <param name="pivot">World-space selection pivot.</param>
    /// <param name="frame">
    /// The rotation taking the frame's axes to world space:
    /// <see cref="Quaternion.Identity"/> for a world-aligned gizmo, a node's
    /// world rotation for a local-aligned one.
    /// </param>
    /// <param name="viewportSize">Viewport extent in pixels.</param>
    /// <param name="pixelSize">Desired on-screen handle length in pixels.</param>
    public static GizmoGeometry Build(
        Camera camera, Vector3 pivot, Quaternion frame, Vector2 viewportSize, float pixelSize)
    {
        ArgumentNullException.ThrowIfNull(camera);

        float viewDepth = GizmoMath.ViewDepth(camera, pivot);

        // Scale from a depth floored at the near plane. A pivot at or behind
        // the camera would otherwise produce a zero or negative world scale,
        // and every downstream length (arrow, quad, ring radius) would collapse
        // or invert. IsBehindCamera is how callers find out; the floor is only
        // here so the struct is never poisoned with a non-finite size.
        float scaleDepth = MathF.Max(viewDepth, camera.NearPlane);
        float worldPerPixel = GizmoMath.WorldPerPixel(camera, viewportSize.Y, scaleDepth);

        // Identity is the overwhelmingly common case (world orientation), and
        // skipping the three rotations keeps it exactly as cheap as it was
        // before the frame existed — and exactly bit-identical, which is what
        // keeps world-mode drags reproducible.
        bool identity = frame == Quaternion.Identity;
        return new GizmoGeometry(
            pivot,
            identity ? Vector3.UnitX : Vector3.Transform(Vector3.UnitX, frame),
            identity ? Vector3.UnitY : Vector3.Transform(Vector3.UnitY, frame),
            identity ? Vector3.UnitZ : Vector3.Transform(Vector3.UnitZ, frame),
            worldPerPixel * pixelSize,
            worldPerPixel,
            viewDepth,
            camera.Right,
            camera.Up,
            camera.Forward);
    }

    /// <summary>
    /// The frame axis an axis handle stands for; <see cref="Vector3.Zero"/> for
    /// any other handle. The frame-aware counterpart of
    /// <see cref="GizmoHandles.AxisDirection"/>.
    /// </summary>
    public Vector3 Axis(GizmoHandle handle) => handle switch
    {
        GizmoHandle.AxisX => AxisX,
        GizmoHandle.AxisY => AxisY,
        GizmoHandle.AxisZ => AxisZ,
        _ => Vector3.Zero,
    };

    /// <summary>
    /// The unit normal of a plane handle's constraint plane in this frame — the
    /// frame axis the handle does <em>not</em> span. For
    /// <see cref="GizmoHandle.Screen"/> this is <see cref="ViewNormal"/>, whose
    /// plane really is the camera's; <see cref="Vector3.Zero"/> otherwise.
    /// </summary>
    public Vector3 PlaneNormal(GizmoHandle handle) => handle switch
    {
        GizmoHandle.PlaneYZ => AxisX,
        GizmoHandle.PlaneZX => AxisY,
        GizmoHandle.PlaneXY => AxisZ,
        GizmoHandle.Screen => ViewNormal,
        _ => Vector3.Zero,
    };

    /// <summary>
    /// The two frame axes a plane handle spans, in the order its name gives
    /// them. Both are <see cref="Vector3.Zero"/> for a non-plane handle.
    /// </summary>
    public void PlaneAxes(GizmoHandle handle, out Vector3 first, out Vector3 second)
    {
        switch (handle)
        {
            case GizmoHandle.PlaneYZ: first = AxisY; second = AxisZ; break;
            case GizmoHandle.PlaneZX: first = AxisZ; second = AxisX; break;
            case GizmoHandle.PlaneXY: first = AxisX; second = AxisY; break;
            default: first = Vector3.Zero; second = Vector3.Zero; break;
        }
    }

    /// <summary>
    /// Two frame axes perpendicular to an axis handle's own axis — the frame an
    /// arrowhead, a cube handle, or a rotate ring is built in. Both are
    /// <see cref="Vector3.Zero"/> for a non-axis handle.
    /// </summary>
    public void AxisPerpendiculars(GizmoHandle handle, out Vector3 first, out Vector3 second)
    {
        switch (handle)
        {
            case GizmoHandle.AxisX: first = AxisY; second = AxisZ; break;
            case GizmoHandle.AxisY: first = AxisZ; second = AxisX; break;
            case GizmoHandle.AxisZ: first = AxisX; second = AxisY; break;
            default: first = Vector3.Zero; second = Vector3.Zero; break;
        }
    }

    /// <summary>
    /// The world-space segment of an axis handle, from the pivot to its tip.
    /// Returns false for a non-axis handle.
    /// </summary>
    public bool TryGetAxisSegment(GizmoHandle handle, out Vector3 start, out Vector3 end)
    {
        if (!GizmoHandles.IsAxis(handle))
        {
            start = Pivot;
            end = Pivot;
            return false;
        }

        start = Pivot;
        end = Pivot + Axis(handle) * AxisLength;
        return true;
    }

    /// <summary>
    /// The world-space rectangle of a plane handle: a square of
    /// <see cref="PlaneSize"/> spanning the handle's two frame axes, pushed
    /// <see cref="PlaneOffset"/> along both of them so it sits in the quadrant
    /// between the arrows rather than on top of them. Returns false for a
    /// non-plane handle.
    /// </summary>
    public bool TryGetPlaneQuad(
        GizmoHandle handle, out Vector3 corner, out Vector3 firstAxis, out Vector3 secondAxis, out float size)
    {
        if (!GizmoHandles.IsPlane(handle))
        {
            corner = Pivot;
            firstAxis = Vector3.Zero;
            secondAxis = Vector3.Zero;
            size = 0f;
            return false;
        }

        PlaneAxes(handle, out firstAxis, out secondAxis);
        float offset = PlaneOffset;
        corner = Pivot + firstAxis * offset + secondAxis * offset;
        size = PlaneSize;
        return true;
    }

    /// <summary>
    /// The circle a rotate handle spins about: its centre (always the pivot),
    /// the unit axis it turns around, and its radius. Axis handles give their
    /// frame axis at <see cref="RingRadius"/>;
    /// <see cref="GizmoHandle.Screen"/> gives the view axis at the larger
    /// <see cref="ScreenRingRadius"/>. Returns false for any other handle.
    /// </summary>
    public bool TryGetRing(GizmoHandle handle, out Vector3 axis, out float radius)
    {
        if (GizmoHandles.IsAxis(handle))
        {
            axis = Axis(handle);
            radius = RingRadius;
            return true;
        }

        if (handle == GizmoHandle.Screen)
        {
            axis = ViewNormal;
            radius = ScreenRingRadius;
            return true;
        }

        axis = Vector3.Zero;
        radius = 0f;
        return false;
    }

    /// <summary>
    /// The centre of a scale handle's cube: at the tip of its frame axis for an
    /// axis handle, at the pivot for the uniform
    /// <see cref="GizmoHandle.Screen"/> handle. Returns false for any other
    /// handle.
    /// </summary>
    public bool TryGetHandleBox(GizmoHandle handle, out Vector3 centre, out float radius)
    {
        if (GizmoHandles.IsAxis(handle))
        {
            centre = Pivot + Axis(handle) * AxisLength;
            radius = HandleBoxRadius;
            return true;
        }

        if (handle == GizmoHandle.Screen)
        {
            centre = Pivot;
            radius = ScreenRadius;
            return true;
        }

        centre = Pivot;
        radius = 0f;
        return false;
    }

    /// <summary>
    /// Converts a world-space length at the pivot's depth into viewport pixels.
    /// Returns <see cref="float.PositiveInfinity"/> for a degenerate viewport,
    /// so a length can never accidentally test as "within tolerance" when there
    /// is no viewport to be within.
    /// </summary>
    public float WorldToPixels(float worldLength) =>
        WorldPerPixel > 0f ? worldLength / WorldPerPixel : float.PositiveInfinity;
}
