using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Input;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Editing.Cameras;

/// <summary>
/// The viewport's navigation model: an orbit camera around a focus point, with
/// right/alt-drag to orbit, middle-drag to pan, the wheel to dolly
/// <em>toward whatever is under the cursor</em>, and a verb that frames the
/// selection. It drives the scene's existing <see cref="Camera"/> rather than
/// replacing it, so a host can switch between this and
/// <c>FlyCameraController</c> — or run neither — without the rest of the engine
/// noticing.
/// </summary>
/// <remarks>
/// <b>Focus, distance and two angles are the authority; the camera's position
/// is derived.</b> Every frame ends with
/// <c>Position = Focus − Forward · Distance</c>, and nothing ever reads the
/// position back to recover the orbit. That inversion is what keeps the orbit
/// working at open-world coordinates: at x ≈ 10⁶ a <c>float</c> has roughly
/// 0.06 units of resolution, so re-deriving a yaw/pitch/distance from
/// <c>Position − Focus</c> each frame would feed that quantization back into
/// the angles and the view would visibly crawl while the mouse sat still. Held
/// as the authority instead, the angles are exact and only the final sum
/// carries the coordinate's error — which is the same error every other
/// world-space position at that magnitude carries.
/// <para>
/// <b>Zoom goes toward the cursor, not toward the screen centre.</b> The wheel
/// scales <see cref="TargetDistance"/> and slides <see cref="TargetFocus"/>
/// along the cursor ray by exactly the amount that leaves the world point under
/// the cursor projecting to the same pixel (see <see cref="ApplyZoom"/> for the
/// derivation). This is the single detail that separates a 3D editor that feels
/// right from one that fights you: without it, closing in on a corner of the
/// level means alternating wheel and pan forever.
/// </para>
/// <para>
/// <b>Damping.</b> Orbit, pan, zoom and framing all write <em>target</em>
/// state; the live state chases it with the standard frame-rate-independent
/// exponential filter <c>α = 1 − e^(−Δt/τ)</c>, where τ is
/// <see cref="SmoothingTimeConstant"/> — the time to close about 63% of the
/// remaining gap, defaulting to <see cref="DefaultSmoothingTimeConstant"/>
/// seconds. Distance is damped geometrically (in log space) because zoom is
/// applied multiplicatively, so a dolly from 2 to 200 units eases at a constant
/// <em>proportional</em> rate instead of crawling at the near end. τ = 0 turns
/// damping off entirely, which is what the tests and any deterministic host
/// want. The invariants zoom-to-cursor and framing guarantee hold at the
/// target; the damped frames in between are a blend of two valid orbit states.
/// </para>
/// <para>
/// <b>No keyboard vocabulary lives here</b>, for the same reason it does not
/// live in <see cref="GizmoController"/>: the host resolves its own key into an
/// <see cref="EditorCameraCommand"/> (<see cref="EditorCameraShortcuts"/>
/// carries the recommended defaults, F for frame-selection) and calls
/// <see cref="Apply"/>. Pointer input arrives only through
/// <see cref="EditorInputFrame"/>, so this assembly still names no backend
/// type.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only — it reads the scene's selection and
/// spatial bounds and writes the camera. Steady-state
/// <see cref="Update(in EditorInputFrame)"/> allocates nothing; framing
/// allocates nothing either (bounds come from the spatial index).
/// </para>
/// </remarks>
public sealed class EditorCameraController
{
    /// <summary>
    /// Default <see cref="SmoothingTimeConstant"/>, in seconds. Long enough to
    /// take the stair-steps out of a wheel notch, short enough that the camera
    /// is where you asked for it before you can react — about five frames at
    /// 60 Hz.
    /// </summary>
    public const float DefaultSmoothingTimeConstant = 0.08f;

    /// <summary>The orbit distance a freshly adopted camera starts at, in world units.</summary>
    public const float DefaultDistance = 10f;

    // Same limit the Camera's own Pitch setter clamps to: exactly vertical
    // collapses the basis Cross to zero, so the target must never be allowed to
    // park there either (a clamped camera chasing an unclamped target would
    // stick and then jump).
    private const float PitchLimit = MathF.PI / 2f - 0.01f;

    // A cursor ray this close to perpendicular to the view axis has no
    // well-defined crossing of the focus plane; zoom falls back to the centre.
    private const float ParallelEpsilon = 1e-4f;

    private readonly List<SceneNode> _framingScratch = [];

    private Vector2 _lastCursor;
    private bool _wasOrbiting;
    private bool _wasPanning;

    /// <summary>
    /// Creates a controller over a scene, adopting that scene's camera as it
    /// currently stands: the focus is placed <see cref="DefaultDistance"/> units
    /// straight ahead, so the very first orbit turns around what the camera was
    /// already looking at.
    /// </summary>
    public EditorCameraController(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        Scene = scene;
        AdoptCamera(DefaultDistance);
    }

    /// <summary>The scene whose camera this drives and whose selection it frames.</summary>
    public Scene Scene { get; }

    /// <summary>The camera being driven — the scene's own.</summary>
    public Camera Camera => Scene.Camera;

    // --- Live orbit state ----------------------------------------------------

    /// <summary>The point the camera currently orbits, in world space.</summary>
    public Vector3 Focus { get; private set; }

    /// <summary>The camera's current distance from <see cref="Focus"/>, in world units.</summary>
    public float Distance { get; private set; } = DefaultDistance;

    /// <summary>The camera's current yaw, in radians.</summary>
    public float Yaw { get; private set; }

    /// <summary>The camera's current pitch, in radians, clamped short of vertical.</summary>
    public float Pitch { get; private set; }

    // --- Target orbit state --------------------------------------------------

    /// <summary>Where the focus is heading. Equal to <see cref="Focus"/> once the camera has settled.</summary>
    public Vector3 TargetFocus { get; private set; }

    /// <summary>The distance the dolly is heading for, in world units.</summary>
    public float TargetDistance { get; private set; } = DefaultDistance;

    /// <summary>The yaw the orbit is heading for, in radians.</summary>
    public float TargetYaw { get; private set; }

    /// <summary>The pitch the orbit is heading for, in radians.</summary>
    public float TargetPitch { get; private set; }

    /// <summary>True while the live state has not yet caught up with the target.</summary>
    public bool IsSettling =>
        Focus != TargetFocus || Distance != TargetDistance ||
        Yaw != TargetYaw || Pitch != TargetPitch;

    // --- Tuning --------------------------------------------------------------

    /// <summary>
    /// The exponential filter's time constant τ, in seconds — see the type
    /// remarks. Zero disables damping, making every gesture land on its target
    /// within the frame that produced it.
    /// </summary>
    public float SmoothingTimeConstant { get; set; } = DefaultSmoothingTimeConstant;

    /// <summary>Radians of orbit per pixel of drag.</summary>
    public float OrbitSensitivity { get; set; } = 0.006f;

    /// <summary>
    /// How much one wheel notch multiplies (or divides) the orbit distance by.
    /// 1.15 is roughly a 15% step, which is a comfortable ten notches per
    /// order of magnitude.
    /// </summary>
    public float ZoomStep { get; set; } = 1.15f;

    /// <summary>The closest the camera may orbit, in world units.</summary>
    public float MinDistance { get; set; } = 0.05f;

    /// <summary>The furthest the camera may orbit, in world units.</summary>
    public float MaxDistance { get; set; } = 1e7f;

    /// <summary>
    /// Whether the wheel dollies toward the point under the cursor (the
    /// default) or straight down the view axis. Off is the "classic" behaviour
    /// and exists mostly so the difference is demonstrable.
    /// </summary>
    public bool ZoomToCursor { get; set; } = true;

    /// <summary>
    /// How much slack <see cref="FrameBounds"/> leaves around what it frames —
    /// 1.15 puts a comfortable margin between the selection and the viewport
    /// edge. Values below 1 would crop it.
    /// </summary>
    public float FrameMargin { get; set; } = 1.15f;

    /// <summary>The button that orbits on its own.</summary>
    public PointerButtons OrbitButton { get; set; } = PointerButtons.Right;

    /// <summary>The button that orbits when <see cref="AlternateOrbitModifier"/> is held.</summary>
    public PointerButtons AlternateOrbitButton { get; set; } = PointerButtons.Left;

    /// <summary>The modifier that turns <see cref="AlternateOrbitButton"/> into an orbit.</summary>
    public KeyModifiers AlternateOrbitModifier { get; set; } = KeyModifiers.Alt;

    /// <summary>The button that pans.</summary>
    public PointerButtons PanButton { get; set; } = PointerButtons.Middle;

    /// <summary>True while an orbit drag is in progress.</summary>
    public bool IsOrbiting => _wasOrbiting;

    /// <summary>True while a pan drag is in progress.</summary>
    public bool IsPanning => _wasPanning;

    // --- Per-frame update ----------------------------------------------------

    /// <summary>
    /// Consumes one frame of pointer input — orbit, pan, wheel — damps the live
    /// orbit toward its target, and writes the result to the camera. Returns
    /// true when the camera moved this frame, so a host can skip redundant
    /// work when it did not.
    /// </summary>
    /// <remarks>
    /// A host that also runs a gizmo should not call this while the gizmo is
    /// dragging: the same buttons mean different things mid-manipulation (right
    /// -click cancels the drag rather than orbiting).
    /// <see cref="Viewport.ViewportInteractionController"/> already arbitrates
    /// that — and tells this controller about every frame it withheld, through
    /// <see cref="SuspendNavigation"/>, which is what stops the skipped travel
    /// from arriving all at once afterwards.
    /// </remarks>
    public bool Update(in EditorInputFrame frame)
    {
        Vector2 cursor = frame.CursorPosition;
        Vector2 delta = cursor - _lastCursor;
        _lastCursor = cursor;

        bool orbiting =
            frame.IsDown(OrbitButton) ||
            (frame.IsDown(AlternateOrbitButton) && frame.HasModifiers(AlternateOrbitModifier));
        // Orbit wins a tie: a user holding both is asking for the gesture that
        // needs the finer control.
        bool panning = !orbiting && frame.IsDown(PanButton);

        // Skip the press frame, exactly as the fly camera does: the cursor may
        // have travelled a long way since the last frame that tracked it, and
        // applying that as a drag delta snaps the view on every button press.
        if (orbiting && _wasOrbiting)
            ApplyOrbit(delta);
        else if (panning && _wasPanning)
            ApplyPan(delta, frame.ViewportSize);

        _wasOrbiting = orbiting;
        _wasPanning = panning;

        if (frame.ScrollDelta.Y != 0f)
            ApplyZoom(frame.ScrollDelta.Y, cursor, frame.ViewportSize);

        return Settle(frame.DeltaTime);
    }

    /// <summary>
    /// Forgets any orbit or pan in progress, so the next
    /// <see cref="Update(in EditorInputFrame)"/> re-anchors on that frame's
    /// cursor instead of measuring against the last one it happened to see.
    /// </summary>
    /// <remarks>
    /// <b>Every frame on which the host skips <see cref="Update"/> must call
    /// this.</b> The drag delta is <c>cursor − _lastCursor</c>, and
    /// <c>_lastCursor</c> only advances inside <see cref="Update"/>: a
    /// controller that sat out a gizmo drag, a marquee, or a minimized window
    /// with the orbit button still down would otherwise apply the entire
    /// withheld cursor travel as a single step the moment it runs again — a
    /// three-hundred-pixel gesture arriving as one 120° snap on a frame where
    /// the cursor did not move at all. Dropping the gesture latches turns that
    /// frame into a press frame, which the controller already knows to skip.
    /// <see cref="Viewport.ViewportInteractionController"/> does this for its
    /// own <see cref="Viewport.ViewportInteractionController.CameraController"/>;
    /// a host driving this class directly owns the same obligation.
    /// </remarks>
    public void SuspendNavigation()
    {
        _wasOrbiting = false;
        _wasPanning = false;
    }

    /// <summary>
    /// Turns the orbit by a drag in pixels: dragging right sweeps the view
    /// right (carrying the camera around the focus), dragging down tips it
    /// down — the same sense as the fly camera's look, so switching controllers
    /// does not invert the mouse.
    /// </summary>
    public void ApplyOrbit(Vector2 pixelDelta)
    {
        TargetYaw += pixelDelta.X * OrbitSensitivity;
        TargetPitch = Math.Clamp(TargetPitch - pixelDelta.Y * OrbitSensitivity, -PitchLimit, PitchLimit);
        // Keep both angles in the same turn so a long session cannot walk the
        // yaw out to a magnitude where a radian costs precision. Shifting the
        // live angle by the identical multiple of 2π preserves the gap the
        // damping is closing, so nothing spins on the way past ±π.
        NormalizeYaw();
    }

    /// <summary>
    /// Slides the focus in the camera's own view plane so the world appears to
    /// follow the cursor: the world point under the cursor when the pan began
    /// stays under it, to the accuracy of the constant-depth approximation
    /// (the drag is tracked in the plane through the focus, which is what every
    /// editor does).
    /// </summary>
    public void ApplyPan(Vector2 pixelDelta, Vector2 viewportSize)
    {
        if (viewportSize.Y <= 0f)
            return;

        // The same perspective pixel→world scale the gizmos size themselves
        // with, evaluated at the focus depth — so a pan drags the same number
        // of world units per pixel as the thing you are looking at moves.
        float worldPerPixel = GizmoMath.WorldPerPixel(Camera, viewportSize.Y, TargetDistance);

        // Cursor right ⇒ world right ⇒ camera left; screen y grows down, so
        // cursor down ⇒ world down ⇒ camera up.
        TargetFocus +=
            Camera.Right * (-pixelDelta.X * worldPerPixel) +
            Camera.Up * (pixelDelta.Y * worldPerPixel);
    }

    /// <summary>
    /// Dollies by <paramref name="notches"/> wheel steps (positive = toward the
    /// scene) and, when <see cref="ZoomToCursor"/> is on, slides the focus so
    /// the world point under <paramref name="cursorPosition"/> keeps projecting
    /// to that pixel.
    /// </summary>
    /// <remarks>
    /// <b>The derivation.</b> Let <c>F</c> be the camera's forward axis,
    /// <c>d</c> the orbit distance, and <c>P</c> the point where the cursor ray
    /// crosses the plane through the focus perpendicular to <c>F</c>. Write
    /// <c>v = P − Focus</c>, which lies in that plane. A point's screen offset
    /// is its camera-relative offset perpendicular to <c>F</c>, divided by its
    /// depth. Before the dolly <c>P</c> sits at perpendicular offset <c>v</c>
    /// and depth <c>d</c>. Scaling the distance by <c>s</c> and moving the
    /// focus to <c>Focus + v·(1 − s)</c> puts <c>P</c> at perpendicular offset
    /// <c>v·s</c> and depth <c>d·s</c> — the same ratio, hence the same pixel.
    /// The focus therefore travels <em>along the cursor ray's own direction in
    /// the focus plane</em>, which is precisely "zoom toward the cursor", and
    /// the displacement it adds is small even when the focus itself is a
    /// million units from the origin.
    /// </remarks>
    public void ApplyZoom(float notches, Vector2 cursorPosition, Vector2 viewportSize)
    {
        float scale = MathF.Pow(ZoomStep, -notches);
        float newDistance = Math.Clamp(TargetDistance * scale, MinDistance, MaxDistance);
        // Re-derive the ratio from the CLAMPED distance: at the stops the focus
        // must stop sliding too, or the camera keeps creeping toward the cursor
        // while appearing not to zoom.
        float applied = TargetDistance > 0f ? newDistance / TargetDistance : 1f;
        TargetDistance = newDistance;

        if (!ZoomToCursor || applied == 1f || viewportSize.X <= 0f || viewportSize.Y <= 0f)
            return;

        if (TryFocusPlanePoint(cursorPosition, viewportSize, out Vector3 point))
            TargetFocus += (point - TargetFocus) * (1f - applied);
    }

    // --- Framing -------------------------------------------------------------

    /// <summary>
    /// Frames the current selection: centres the orbit on the union of the
    /// selected nodes' world bounds and pulls back far enough that the whole of
    /// it fits in view, with <see cref="FrameMargin"/> to spare. Returns false
    /// and changes nothing when the selection is empty or carries no bounds at
    /// all (a selection of pure group nodes).
    /// </summary>
    /// <remarks>
    /// Bounds come from the scene's spatial index — the same boxes culling,
    /// raycasts and the selection highlight use — so what gets framed is
    /// exactly what is drawn highlighted. A selected node with no bounds (a
    /// pure group) contributes its origin, so framing a group still puts it on
    /// screen.
    /// </remarks>
    public bool FrameSelection()
    {
        IReadOnlyList<SceneNode> items = Scene.Selection.Items;
        if (items.Count == 0)
            return false;

        if (!TryUnionBounds(items, out Aabb bounds))
            return false;

        FrameBounds(bounds);
        return true;
    }

    /// <summary>
    /// Frames every spatial node in the scene. Returns false for a scene with
    /// no spatial nodes.
    /// </summary>
    public bool FrameAll()
    {
        // A graph walk, not a frustum query: framing everything must include
        // what is currently off screen, which is exactly what a frustum
        // excludes. This runs once per keypress, never per frame, so the
        // traversal's enumerator is affordable here.
        _framingScratch.Clear();
        foreach (SceneNode node in Scene.Nodes)
        {
            if (Scene.TryGetWorldBounds(node, out _))
                _framingScratch.Add(node);
        }

        if (!TryUnionBounds(_framingScratch, out Aabb bounds))
            return false;

        FrameBounds(bounds);
        _framingScratch.Clear();
        return true;
    }

    /// <summary>
    /// Aims the orbit at <paramref name="bounds"/>: the focus goes to its
    /// centre and the distance to the smallest one that keeps the box's
    /// bounding sphere inside both the horizontal and the vertical field of
    /// view, times <see cref="FrameMargin"/>.
    /// </summary>
    /// <remarks>
    /// Fitting the bounding <em>sphere</em> rather than the box makes the
    /// result independent of the viewing angle — the camera does not lurch when
    /// you frame the same selection from a different side — and it is
    /// conservative, so the box is guaranteed inside the four side planes.
    /// <para>
    /// The clip planes are left alone. Framing something bigger than
    /// <c>Camera.FarPlane</c> therefore puts the camera at the right distance
    /// but lets the far plane cut the geometry; a host that wants to frame
    /// whole cities should widen the depth range itself, which is a rendering
    /// decision this controller has no business making.
    /// </para>
    /// </remarks>
    public void FrameBounds(Aabb bounds)
    {
        Vector3 center = bounds.Center;
        float radius = 0.5f * bounds.Size.Length();

        // A degenerate (point) selection still needs somewhere to sit: keep the
        // distance we already have rather than diving to MinDistance.
        if (radius <= 0f)
        {
            TargetFocus = center;
            return;
        }

        float halfVertical = Camera.FieldOfView * 0.5f;
        // The horizontal half-angle of a symmetric perspective frustum, from the
        // vertical one and the aspect ratio.
        float halfHorizontal = MathF.Atan(MathF.Tan(halfVertical) * MathF.Max(Camera.AspectRatio, 1e-4f));
        float halfAngle = MathF.Min(halfVertical, halfHorizontal);

        // Distance at which a sphere of `radius` exactly touches the cone of
        // half-angle `halfAngle`: d = r / sin(halfAngle).
        float distance = radius / MathF.Max(MathF.Sin(halfAngle), 1e-4f) * MathF.Max(FrameMargin, 1e-3f);

        TargetFocus = center;
        TargetDistance = Math.Clamp(distance, MinDistance, MaxDistance);
    }

    /// <summary>
    /// Applies one navigation verb and reports whether it changed anything, so
    /// a host can fall through to another binding when it did not — the same
    /// contract as <see cref="GizmoController.Apply"/>.
    /// </summary>
    public bool Apply(EditorCameraCommand command) => command switch
    {
        EditorCameraCommand.FrameSelection => FrameSelection(),
        EditorCameraCommand.FrameAll => FrameAll(),
        _ => false,
    };

    // --- Direct control ------------------------------------------------------

    /// <summary>
    /// Places the orbit outright — target and live state together — and writes
    /// the camera immediately, with no damping. For loading a saved viewport,
    /// or for a test that needs a known pose.
    /// </summary>
    public void SetOrbit(Vector3 focus, float distance, float yaw, float pitch)
    {
        TargetFocus = focus;
        TargetDistance = Math.Clamp(distance, MinDistance, MaxDistance);
        TargetYaw = yaw;
        TargetPitch = Math.Clamp(pitch, -PitchLimit, PitchLimit);
        NormalizeYaw();
        SnapToTarget();
    }

    /// <summary>
    /// Adopts the camera's current position and orientation as the orbit's live
    /// <em>and</em> target state, placing the focus <paramref name="distance"/>
    /// units straight ahead of it. The handover from a fly camera.
    /// </summary>
    public void AdoptCamera(float distance = DefaultDistance)
    {
        float clamped = Math.Clamp(distance, MinDistance, MaxDistance);
        SetOrbit(Camera.Position + Camera.Forward * clamped, clamped, Camera.Yaw, Camera.Pitch);
    }

    /// <summary>
    /// Drops the live state onto the target and writes the camera — the
    /// instant-arrival form of <see cref="Update(in EditorInputFrame)"/>.
    /// </summary>
    public void SnapToTarget()
    {
        Focus = TargetFocus;
        Distance = TargetDistance;
        Yaw = TargetYaw;
        Pitch = TargetPitch;
        WriteCamera();
    }

    // --- Internals -----------------------------------------------------------

    // Damps the live state toward the target and writes the camera. Returns
    // whether anything actually moved.
    private bool Settle(float deltaTime)
    {
        if (!IsSettling)
            return false;

        float alpha = SmoothingAlpha(deltaTime);
        if (alpha >= 1f)
        {
            SnapToTarget();
            return true;
        }

        Focus += (TargetFocus - Focus) * alpha;
        Yaw += (TargetYaw - Yaw) * alpha;
        Pitch += (TargetPitch - Pitch) * alpha;
        // Geometric, because zoom is applied multiplicatively (see the type
        // remarks): equal fractions of the remaining ratio per unit time.
        Distance = Distance > 0f && TargetDistance > 0f
            ? Distance * MathF.Pow(TargetDistance / Distance, alpha)
            : TargetDistance;

        WriteCamera();
        return true;
    }

    // The standard frame-rate-independent exponential filter. A zero or
    // negative time constant, a non-positive frame time, or a frame long enough
    // that the filter would overshoot all collapse to "arrive now".
    private float SmoothingAlpha(float deltaTime)
    {
        if (SmoothingTimeConstant <= 0f || deltaTime <= 0f)
            return 1f;

        return 1f - MathF.Exp(-deltaTime / SmoothingTimeConstant);
    }

    private void WriteCamera()
    {
        // Angles first: the camera recomputes its basis on assignment, and the
        // position below is expressed in that basis.
        Camera.Yaw = Yaw;
        Camera.Pitch = Pitch;
        Camera.Position = Focus - Camera.Forward * Distance;
    }

    // Slides both angles by the same multiple of a full turn, which leaves the
    // gap the damping is closing untouched while keeping the magnitudes small.
    private void NormalizeYaw()
    {
        const float Turn = MathF.Tau;
        float shift = MathF.Floor((TargetYaw + MathF.PI) / Turn) * Turn;
        if (shift == 0f)
            return;

        TargetYaw -= shift;
        Yaw -= shift;
    }

    // Where the cursor ray crosses the plane through the TARGET focus
    // perpendicular to the view axis. Uses the live camera's ray (the only one
    // there is) — while the camera is still settling that is an approximation,
    // and an exact answer once it has arrived, which is the state the wheel is
    // normally used from.
    private bool TryFocusPlanePoint(Vector2 cursorPosition, Vector2 viewportSize, out Vector3 point)
    {
        Ray3 ray = Camera.ScreenPointToRay(cursorPosition, viewportSize);
        Vector3 forward = Camera.Forward;

        float denominator = Vector3.Dot(ray.Direction, forward);
        if (MathF.Abs(denominator) < ParallelEpsilon)
        {
            point = default;
            return false;
        }

        // Both operands sit near the camera, so this difference stays small even
        // when the focus is at an open-world coordinate.
        float travel = Vector3.Dot(TargetFocus - ray.Origin, forward) / denominator;
        point = ray.PointAt(travel);
        return true;
    }

    private bool TryUnionBounds(IReadOnlyList<SceneNode> nodes, out Aabb bounds)
    {
        bool any = false;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (int i = 0; i < nodes.Count; i++)
        {
            SceneNode node = nodes[i];
            if (Scene.TryGetWorldBounds(node, out Aabb box))
            {
                min = Vector3.Min(min, box.Min);
                max = Vector3.Max(max, box.Max);
            }
            else
            {
                // A pure group has no bounds but does have a place; framing a
                // selection of groups should still put them on screen.
                Vector3 origin = node.WorldPosition;
                min = Vector3.Min(min, origin);
                max = Vector3.Max(max, origin);
            }
            any = true;
        }

        bounds = any ? new Aabb(min, max) : default;
        return any;
    }
}
