using SpectraEngine.Core.Scene;
using System;
using System.Numerics;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// One frame's world-space shape of <em>any</em> of the three manipulators:
/// where the pivot is, which frame the handles are laid out in, how far out each
/// handle stands, how big one handle is in world units, and the camera basis the
/// screen-facing parts are built in.
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
/// proportions differ, and those now come from <see cref="GizmoStyle"/>. Three
/// copies of this struct would be three chances for picking and drawing to drift
/// apart in different ways.
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
/// <b>Where a handle STANDS and how big it IS are two different questions, and
/// only the second one is always a screen-space constant.</b> A classic gizmo
/// answers both with <see cref="AxisLength"/>. A Studio-style gizmo stands its
/// handles on the faces of the selection's own box
/// (<see cref="GizmoStyle.HandlesStandOffBounds"/>), which is what makes "this
/// face moves, that one stays" legible, so the distance out is a property of the
/// selection while the handle's size stays a property of the screen. That is
/// what <see cref="PositiveReach"/> and <see cref="NegativeReach"/> carry, and
/// why they are separate: the two ends of an axis are not the same distance from
/// the pivot once the pivot stops being the centre of the box, which is exactly
/// what a face-anchored resize makes happen mid-drag.
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

    // Nullable only so that default(GizmoGeometry), which every "no selection"
    // path returns, answers style questions instead of throwing. Style hands
    // back the classic preset for one; it is behind the camera and zero-length
    // anyway, so nothing draws or picks from it.
    private readonly GizmoStyle? _style;

    private GizmoGeometry(
        Vector3 pivot,
        Vector3 axisX,
        Vector3 axisY,
        Vector3 axisZ,
        float handleLength,
        Vector3 positiveReach,
        Vector3 negativeReach,
        float worldPerPixel,
        float viewDepth,
        Vector3 viewRight,
        Vector3 viewUp,
        Vector3 viewNormal,
        GizmoStyle style,
        GizmoMode mode)
    {
        Pivot = pivot;
        AxisX = axisX;
        AxisY = axisY;
        AxisZ = axisZ;
        AxisLength = handleLength;
        PositiveReach = positiveReach;
        NegativeReach = negativeReach;
        WorldPerPixel = worldPerPixel;
        ViewDepth = viewDepth;
        ViewRight = viewRight;
        ViewUp = viewUp;
        ViewNormal = viewNormal;
        _style = style;
        Mode = mode;
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
    /// The world length one handle is quoted against, chosen so it covers a
    /// fixed number of pixels at the pivot's depth. Every proportion (arrowhead,
    /// cube, ring, plane quad) is a multiple of it, and in a style whose handles
    /// do not stand off the bounds it is also how far out they stand.
    /// </summary>
    public float AxisLength { get; }

    /// <summary>
    /// How far from the pivot the +x, +y and +z handles stand, in world units.
    /// Equal to <see cref="AxisLength"/> on every component unless the style
    /// stands its handles on the selection's box.
    /// </summary>
    public Vector3 PositiveReach { get; }

    /// <summary>
    /// How far from the pivot the −x, −y and −z handles stand, in world units.
    /// See <see cref="PositiveReach"/>; the two differ only when the pivot is
    /// not the centre of the box the handles stand on.
    /// </summary>
    public Vector3 NegativeReach { get; }

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
    /// The style this geometry was laid out in: the roster it offers and every
    /// proportion it is drawn at. Never null.
    /// </summary>
    public GizmoStyle Style => _style ?? GizmoStyle.Classic;

    /// <summary>
    /// Which tool this geometry was built for. The rosters differ per tool (a
    /// rotate gizmo has no negative rings and no plane quads), so the handle
    /// queries need to know which one is asking.
    /// </summary>
    public GizmoMode Mode { get; }

    /// <summary>
    /// True when the pivot sits at or behind the camera plane, where the gizmo
    /// projects to nothing coherent. Callers skip drawing and picking rather
    /// than rendering a mirrored gizmo behind the viewer.
    /// </summary>
    public bool IsBehindCamera => ViewDepth <= 0f;

    /// <summary>The last axis handle this geometry's style and tool offer; see <see cref="GizmoStyle.LastAxisHandle"/>.</summary>
    public GizmoHandle LastAxisHandle =>
        Mode == GizmoMode.Rotate ? GizmoHandle.AxisZ : Style.LastAxisHandle;

    /// <summary>Whether this geometry's style and tool offer <paramref name="handle"/> at all.</summary>
    public bool Offers(GizmoHandle handle) => Style.Offers(handle, Mode);

    /// <summary>How far from the pivot a translate plane quad's near corner sits.</summary>
    public float PlaneOffset => AxisLength * Style.PlaneOffsetFactor;

    /// <summary>The edge length of a translate plane quad.</summary>
    public float PlaneSize => AxisLength * Style.PlaneSizeFactor;

    /// <summary>The radius of the screen-facing centre disc (translate) / uniform cube (scale).</summary>
    public float ScreenRadius => AxisLength * Style.ScreenRadiusFactor;

    /// <summary>
    /// The nominal radius of one rotate axis ring. The radius a ring is actually
    /// drawn and picked at comes from <see cref="TryGetRing"/>, which sizes it to
    /// the selection in a style whose handles stand off the bounds; the two are
    /// equal in every other style.
    /// </summary>
    public float RingRadius => AxisLength * Style.RingRadiusFactor;

    /// <summary>The radius of the rotate gizmo's view-aligned outer ring.</summary>
    public float ScreenRingRadius => AxisLength * Style.ScreenRingRadiusFactor;

    /// <summary>The half-extent of a scale gizmo's cube handle.</summary>
    public float HandleBoxRadius => AxisLength * Style.HandleBoxRadiusFactor;

    /// <summary>The length of a translate arrowhead.</summary>
    public float HeadLength => AxisLength * Style.HeadLengthFactor;

    /// <summary>The radius of a translate arrowhead's base.</summary>
    public float HeadRadius => AxisLength * Style.HeadRadiusFactor;

    /// <summary>
    /// Builds the gizmo's geometry for one frame in the classic style, with no
    /// selection box to stand handles on. The shape this engine drew before
    /// styles existed, bit for bit.
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
        Camera camera, Vector3 pivot, Quaternion frame, Vector2 viewportSize, float pixelSize) =>
        Build(
            camera, pivot, frame, viewportSize, pixelSize,
            GizmoStyle.Classic, GizmoMode.Translate, Vector3.Zero, Vector3.Zero);

    /// <summary>
    /// Builds the gizmo's geometry for one frame.
    /// </summary>
    /// <param name="camera">The viewport camera; supplies the basis and the perspective scale.</param>
    /// <param name="pivot">World-space selection pivot.</param>
    /// <param name="frame">The rotation taking the frame's axes to world space.</param>
    /// <param name="viewportSize">Viewport extent in pixels.</param>
    /// <param name="pixelSize">Desired on-screen handle length in pixels.</param>
    /// <param name="style">The manipulator style: the roster and every proportion.</param>
    /// <param name="mode">Which tool this geometry is for.</param>
    /// <param name="positiveExtent">
    /// Distance from the pivot to the selection box's +x/+y/+z faces, along the
    /// frame's own axes. Ignored unless the style stands its handles off the
    /// bounds; negative components are treated as zero.
    /// </param>
    /// <param name="negativeExtent">
    /// Distance from the pivot to the selection box's −x/−y/−z faces, as a
    /// positive quantity. See <paramref name="positiveExtent"/>.
    /// </param>
    public static GizmoGeometry Build(
        Camera camera,
        Vector3 pivot,
        Quaternion frame,
        Vector2 viewportSize,
        float pixelSize,
        GizmoStyle style,
        GizmoMode mode,
        Vector3 positiveExtent,
        Vector3 negativeExtent)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(style);

        float viewDepth = GizmoMath.ViewDepth(camera, pivot);

        // Scale from a depth floored at the near plane. A pivot at or behind
        // the camera would otherwise produce a zero or negative world scale,
        // and every downstream length (arrow, quad, ring radius) would collapse
        // or invert. IsBehindCamera is how callers find out; the floor is only
        // here so the struct is never poisoned with a non-finite size.
        float scaleDepth = MathF.Max(viewDepth, camera.NearPlane);
        float worldPerPixel = GizmoMath.WorldPerPixel(camera, viewportSize.Y, scaleDepth);
        float handleLength = worldPerPixel * pixelSize;

        Vector3 positiveReach;
        Vector3 negativeReach;
        if (style.HandlesStandOffBounds)
        {
            // The gap is a pixel quantity for the same reason the handle's size
            // is: it has to look the same at any distance. What it measures is
            // the clearance between the face and the NEAR end of the handle, so
            // the handle's own body has to be added on top of it, and how much
            // body there is depends on the tool. Measuring to the handle's centre
            // (or to an arrow's tip) instead buries the near half of every
            // handle in the surface it is supposed to be standing on.
            float gap = worldPerPixel * style.BoundsGapPixels + mode switch
            {
                GizmoMode.Scale => handleLength * style.HandleBoxRadiusFactor,
                GizmoMode.Translate => handleLength * style.ShaftLengthFactor,
                _ => 0f,
            };

            float floor = handleLength * style.MinimumReachFactor;
            positiveReach = Reach(positiveExtent, gap, floor);
            negativeReach = Reach(negativeExtent, gap, floor);
        }
        else
        {
            positiveReach = new Vector3(handleLength);
            negativeReach = positiveReach;
        }

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
            handleLength,
            positiveReach,
            negativeReach,
            worldPerPixel,
            viewDepth,
            camera.Right,
            camera.Up,
            camera.Forward,
            style,
            mode);
    }

    /// <summary>
    /// The frame direction an axis handle points in, negatives included;
    /// <see cref="Vector3.Zero"/> for any other handle. The frame-aware
    /// counterpart of <see cref="GizmoHandles.AxisDirection"/>.
    /// </summary>
    public Vector3 Axis(GizmoHandle handle) => handle switch
    {
        GizmoHandle.AxisX => AxisX,
        GizmoHandle.AxisY => AxisY,
        GizmoHandle.AxisZ => AxisZ,
        GizmoHandle.AxisNegX => -AxisX,
        GizmoHandle.AxisNegY => -AxisY,
        GizmoHandle.AxisNegZ => -AxisZ,
        _ => Vector3.Zero,
    };

    /// <summary>
    /// How far from the pivot an axis handle stands, in world units; zero for a
    /// non-axis handle.
    /// </summary>
    public float AxisReach(GizmoHandle handle)
    {
        GizmoHandle positive = GizmoHandles.PositiveAxis(handle);
        if (positive == GizmoHandle.None)
            return 0f;

        return Component(GizmoHandles.IsNegativeAxis(handle) ? NegativeReach : PositiveReach, positive);
    }

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
    /// <remarks>
    /// Answered for the handle's axis, not for its direction: the −x arrowhead's
    /// blades are built in the same y/z plane the +x one's are, and it is only
    /// the shaft that runs the other way.
    /// </remarks>
    public void AxisPerpendiculars(GizmoHandle handle, out Vector3 first, out Vector3 second)
    {
        switch (GizmoHandles.PositiveAxis(handle))
        {
            case GizmoHandle.AxisX: first = AxisY; second = AxisZ; break;
            case GizmoHandle.AxisY: first = AxisZ; second = AxisX; break;
            case GizmoHandle.AxisZ: first = AxisX; second = AxisY; break;
            default: first = Vector3.Zero; second = Vector3.Zero; break;
        }
    }

    /// <summary>
    /// The world-space segment of an axis handle's shaft, running outward to the
    /// point the handle stands at. Returns false for a handle this geometry's
    /// style and tool do not offer, or one whose shaft has no length.
    /// </summary>
    /// <remarks>
    /// In a style whose handles do not stand off the bounds the shaft is the
    /// whole handle, so this is the pivot to the arrow's tip. Where they do, it
    /// is a stub reaching back from the handle toward the object, and the tip is
    /// still the far end.
    /// </remarks>
    public bool TryGetAxisSegment(GizmoHandle handle, out Vector3 start, out Vector3 end)
    {
        start = Pivot;
        end = Pivot;

        if (!GizmoHandles.IsAxis(handle) || !Offers(handle))
            return false;

        float reach = AxisReach(handle);
        float shaft = MathF.Min(reach, AxisLength * Style.ShaftLengthFactor);
        if (shaft <= 0f)
            return false;

        Vector3 direction = Axis(handle);
        start = Pivot + direction * (reach - shaft);
        end = Pivot + direction * reach;
        return true;
    }

    /// <summary>
    /// The world-space rectangle of a plane handle: a square of
    /// <see cref="PlaneSize"/> spanning the handle's two frame axes, pushed
    /// <see cref="PlaneOffset"/> along both of them so it sits in the quadrant
    /// between the arrows rather than on top of them. Returns false for a
    /// non-plane handle, and for a style that offers none.
    /// </summary>
    public bool TryGetPlaneQuad(
        GizmoHandle handle, out Vector3 corner, out Vector3 firstAxis, out Vector3 secondAxis, out float size)
    {
        if (!GizmoHandles.IsPlane(handle) || !Offers(handle))
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
    /// frame axis; <see cref="GizmoHandle.Screen"/> gives the view axis at the
    /// larger <see cref="ScreenRingRadius"/>. Returns false for any other
    /// handle, and for one the style does not offer.
    /// </summary>
    /// <remarks>
    /// A ring's radius follows the selection wherever handles stand off the
    /// bounds: it is the largest reach in the ring's own plane, so the ring
    /// encircles what it turns rather than cutting through it.
    /// </remarks>
    public bool TryGetRing(GizmoHandle handle, out Vector3 axis, out float radius)
    {
        axis = Vector3.Zero;
        radius = 0f;

        if (!Offers(handle))
            return false;

        if (GizmoHandles.IsPositiveAxis(handle))
        {
            axis = Axis(handle);
            radius = AxisRingRadius(handle);
            return true;
        }

        if (handle == GizmoHandle.Screen)
        {
            axis = ViewNormal;
            radius = ScreenRingRadius;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The centre of a scale handle's cube: at the point its axis handle stands
    /// at, or the pivot for the uniform <see cref="GizmoHandle.Screen"/> handle.
    /// Returns false for any other handle, and for one the style does not offer.
    /// </summary>
    public bool TryGetHandleBox(GizmoHandle handle, out Vector3 centre, out float radius)
    {
        centre = Pivot;
        radius = 0f;

        if (!Offers(handle))
            return false;

        if (GizmoHandles.IsAxis(handle))
        {
            centre = Pivot + Axis(handle) * AxisReach(handle);
            radius = HandleBoxRadius;
            return true;
        }

        if (handle == GizmoHandle.Screen)
        {
            radius = ScreenRadius;
            return true;
        }

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

    private float AxisRingRadius(GizmoHandle handle)
    {
        if (!Style.HandlesStandOffBounds)
            return RingRadius;

        PerpendicularHandles(handle, out GizmoHandle first, out GizmoHandle second);
        float radius = MathF.Max(
            MathF.Max(AxisReach(first), AxisReach(GizmoHandles.Opposite(first))),
            MathF.Max(AxisReach(second), AxisReach(GizmoHandles.Opposite(second))));

        return MathF.Max(radius, AxisLength * Style.MinimumReachFactor);
    }

    private static void PerpendicularHandles(GizmoHandle handle, out GizmoHandle first, out GizmoHandle second)
    {
        switch (GizmoHandles.PositiveAxis(handle))
        {
            case GizmoHandle.AxisX: first = GizmoHandle.AxisY; second = GizmoHandle.AxisZ; break;
            case GizmoHandle.AxisY: first = GizmoHandle.AxisZ; second = GizmoHandle.AxisX; break;
            default: first = GizmoHandle.AxisX; second = GizmoHandle.AxisY; break;
        }
    }

    private static Vector3 Reach(Vector3 extent, float gap, float floor) => new(
        MathF.Max(MathF.Max(extent.X, 0f) + gap, floor),
        MathF.Max(MathF.Max(extent.Y, 0f) + gap, floor),
        MathF.Max(MathF.Max(extent.Z, 0f) + gap, floor));

    private static float Component(Vector3 value, GizmoHandle positiveAxis) => positiveAxis switch
    {
        GizmoHandle.AxisX => value.X,
        GizmoHandle.AxisY => value.Y,
        _ => value.Z,
    };
}
