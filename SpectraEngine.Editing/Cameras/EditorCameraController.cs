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
/// The viewport's navigation model, shaped after Roblox Studio: <b>hold the
/// right mouse button to lock the cursor and look around in place, and fly with
/// the movement keys while you do</b>. Middle-drag pans, the wheel zooms toward
/// whatever is under the cursor, the wheel <em>while looking</em> trims the fly
/// speed, Alt turns a drag into a classic orbit, and a verb frames the
/// selection. It drives the scene's existing <see cref="Camera"/> rather than
/// replacing it, so a host can switch between this and <c>FlyCameraController</c>
/// — or run neither — without the rest of the engine noticing.
/// </summary>
/// <remarks>
/// <b>Freelook is the primary gesture, orbit is the modifier.</b> That is the
/// whole difference from a Blender/Maya-shaped viewport, and it is a semantic
/// one rather than a keybinding one: plain right-drag <em>rotates the camera
/// about its own position</em> and leaves the position exactly where it was,
/// where an orbit would have swung the position around a point in the world.
/// Both gestures still exist — Alt+right-drag (or Alt+left-drag) orbits, which
/// is what a Studio user reaches for right after pressing F — but the
/// unmodified drag now belongs to freelook.
/// <para>
/// <b>Position, yaw, pitch and distance are all authoritative; the focus is the
/// one derived quantity — except when it isn't.</b> The state is a pair,
/// <see cref="Position"/> and <see cref="Focus"/>, held consistent with
/// <c>Focus = Position + Forward · Distance</c>, and <em>every gesture writes the
/// pair from whichever half it must hold exactly</em>:
/// <list type="bullet">
///   <item><description>
///     freelook and fly hold <see cref="Position"/> and recompute the focus,
///   </description></item>
///   <item><description>
///     orbit holds <see cref="Focus"/> and recomputes the position,
///   </description></item>
///   <item><description>
///     pan, zoom and framing write both from the same small displacement.
///   </description></item>
/// </list>
/// This is what keeps the maths exact at open-world coordinates. At x ≈ 10⁶ a
/// <c>float</c> resolves to about 0.06 units, so a freelook implemented as
/// "subtract the focus offset, rotate, add it back" would inject a fresh
/// rounding into the position on <em>every frame the mouse moved</em> — the
/// camera would visibly crawl while you turned on the spot. Holding the half the
/// gesture promises not to move means that half is never recomputed at all, and
/// only the derived half carries the error every other world-space position at
/// that magnitude already carries.
/// </para>
/// <para>
/// <b>Zoom goes toward the cursor, not toward the screen centre.</b> The wheel
/// scales <see cref="TargetDistance"/> and slides the eye along the cursor ray
/// by exactly the amount that leaves the world point under the cursor projecting
/// to the same pixel (see <see cref="ApplyZoom"/> for the derivation). This is
/// the single detail that separates a 3D editor that feels right from one that
/// fights you: without it, closing in on a corner of the level means alternating
/// wheel and pan forever.
/// </para>
/// <para>
/// <b>The wheel means something else while the look button is held.</b> It trims
/// <see cref="FlySpeed"/> instead of zooming, and the new speed persists for the
/// rest of the session — a real Studio behaviour that people navigate large
/// places by, and the reason the property is not reset by
/// <see cref="SuspendNavigation"/> or by a mode toggle.
/// </para>
/// <para>
/// <b>Damping.</b> Look, orbit, pan, fly, zoom and framing all write
/// <em>target</em> state; the live state chases it with the standard
/// frame-rate-independent exponential filter <c>α = 1 − e^(−Δt/τ)</c>, where τ is
/// <see cref="SmoothingTimeConstant"/> — the time to close about 63% of the
/// remaining gap, defaulting to <see cref="DefaultSmoothingTimeConstant"/>
/// seconds. Distance is damped geometrically (in log space) because zoom is
/// applied multiplicatively, so a dolly from 2 to 200 units eases at a constant
/// <em>proportional</em> rate instead of crawling at the near end. τ = 0 turns
/// damping off entirely, which is what the tests and any deterministic host
/// want. The invariants zoom-to-cursor and framing guarantee hold at the target;
/// the damped frames in between are a blend of two valid poses.
/// </para>
/// <para>
/// <b>No keyboard vocabulary lives here</b>, for the same reason it does not
/// live in <see cref="GizmoController"/>: the host resolves its own keys into an
/// <see cref="EditorCameraCommand"/> (<see cref="EditorCameraShortcuts"/>
/// carries the recommended defaults, F for frame-selection) or into an
/// <see cref="EditorNavigationInput"/> axis, and this assembly still names no
/// backend type — not even for the cursor lock, which is requested through
/// <see cref="Core.Input.ICursorLock"/>.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only — it reads the scene's selection and
/// spatial bounds and writes the camera. The cursor lock is the one thing it
/// touches that belongs to another thread, and that crosses over as a request
/// rather than as a call. Steady-state
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

    /// <summary>The fly speed a fresh controller starts at, in world units per second.</summary>
    public const float DefaultFlySpeed = 12f;

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
    private bool _wasCursorLocked;
    private bool _cursorLockRequested;
    private EditorNavigationGesture _gesture;
    private EditorNavigationGesture _lastGesture;
    private float _flySpeed = DefaultFlySpeed;

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

    /// <summary>
    /// Where cursor-lock requests are sent when a freelook begins and ends, or
    /// null to run without a real lock (headless tests, a host that captures the
    /// pointer some other way). See <see cref="Core.Input.ICursorLock"/> for why
    /// this is a request rather than a call.
    /// </summary>
    public ICursorLock? CursorLock { get; set; }

    // --- Live pose -----------------------------------------------------------

    /// <summary>The camera's current position in world space — what freelook turns about.</summary>
    public Vector3 Position { get; private set; }

    /// <summary>The point the camera currently orbits, in world space.</summary>
    public Vector3 Focus { get; private set; }

    /// <summary>The camera's current distance from <see cref="Focus"/>, in world units.</summary>
    public float Distance { get; private set; } = DefaultDistance;

    /// <summary>The camera's current yaw, in radians.</summary>
    public float Yaw { get; private set; }

    /// <summary>The camera's current pitch, in radians, clamped short of vertical.</summary>
    public float Pitch { get; private set; }

    // --- Target pose ---------------------------------------------------------

    /// <summary>Where the camera is heading. Equal to <see cref="Position"/> once it has settled.</summary>
    public Vector3 TargetPosition { get; private set; }

    /// <summary>Where the focus is heading. Equal to <see cref="Focus"/> once the camera has settled.</summary>
    public Vector3 TargetFocus { get; private set; }

    /// <summary>The distance the dolly is heading for, in world units.</summary>
    public float TargetDistance { get; private set; } = DefaultDistance;

    /// <summary>The yaw the look is heading for, in radians.</summary>
    public float TargetYaw { get; private set; }

    /// <summary>The pitch the look is heading for, in radians.</summary>
    public float TargetPitch { get; private set; }

    /// <summary>
    /// True while the live pose has not yet caught up with the target. The
    /// derived <see cref="Focus"/> is deliberately not part of the test: it is
    /// recomputed from the damped pose each frame, so comparing it would never
    /// settle.
    /// </summary>
    public bool IsSettling =>
        Position != TargetPosition || Distance != TargetDistance ||
        Yaw != TargetYaw || Pitch != TargetPitch;

    // --- Tuning --------------------------------------------------------------

    /// <summary>
    /// The exponential filter's time constant τ, in seconds — see the type
    /// remarks. Zero disables damping, making every gesture land on its target
    /// within the frame that produced it.
    /// </summary>
    /// <remarks>
    /// This is the WHEEL-AND-FLIGHT constant: it damps the dolly (where it was
    /// tuned — it is what takes the stair-steps out of a wheel notch) and the
    /// residual glide after a gesture ends. While a pointer gesture is live the
    /// look and move channels use <see cref="PointerSmoothingTimeConstant"/>
    /// instead — see that property for why one constant was wrong.
    /// </remarks>
    public float SmoothingTimeConstant { get; set; } = DefaultSmoothingTimeConstant;

    /// <summary>
    /// The time constant for the look and move channels WHILE a pointer
    /// gesture drives them, in seconds. Never more than
    /// <see cref="SmoothingTimeConstant"/>, so zeroing that one still disables
    /// all damping (which is what the tests and a deterministic host rely on).
    /// </summary>
    /// <remarks>
    /// <b>This split is what un-floats the camera, and it exists because one
    /// constant cannot serve both masters.</b> τ = 80 ms was tuned for the
    /// wheel, where the input arrives as discrete notches and the filter turns
    /// stairs into a dolly. A pointer is already smooth, so the same filter on
    /// freelook is pure lag: the view trails a moving hand by τ's worth of
    /// travel (~8° at an ordinary sweep) and keeps gliding for 3–5τ after the
    /// hand stops — the exact "transition on a drag-path value" the shell's
    /// motion rules ban, and the single largest contributor to the editor
    /// feeling sluggish. Twenty milliseconds keeps the micro-smoothing that
    /// absorbs mouse-report quantisation while staying under anyone's
    /// perception of lag.
    /// </remarks>
    public float PointerSmoothingTimeConstant { get; set; } = 0.02f;

    /// <summary>Radians of freelook per pixel of mouse motion.</summary>
    public float LookSensitivity { get; set; } = 0.0035f;

    /// <summary>Radians of orbit per pixel of drag.</summary>
    public float OrbitSensitivity { get; set; } = 0.006f;

    /// <summary>
    /// How fast the movement keys fly the camera, in world units per second.
    /// Persisted across gestures and mode switches — the wheel trims it while
    /// the look button is held, and users rely on that setting sticking.
    /// </summary>
    public float FlySpeed
    {
        get => _flySpeed;
        set => _flySpeed = Math.Clamp(value, MinFlySpeed, MaxFlySpeed);
    }

    /// <summary>The slowest <see cref="FlySpeed"/> the wheel may trim down to.</summary>
    public float MinFlySpeed { get; set; } = 0.05f;

    /// <summary>The fastest <see cref="FlySpeed"/> the wheel may trim up to.</summary>
    public float MaxFlySpeed { get; set; } = 5000f;

    /// <summary>
    /// How much one wheel notch multiplies (or divides) <see cref="FlySpeed"/>
    /// by while the look button is held. 1.2 gives about thirteen notches per
    /// order of magnitude, which crosses a large level in a couple of flicks.
    /// </summary>
    public float FlySpeedStep { get; set; } = 1.2f;

    /// <summary>How much the boost modifier multiplies <see cref="FlySpeed"/> by.</summary>
    public float BoostMultiplier { get; set; } = 3f;

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

    /// <summary>The button that starts a freelook, and locks the cursor while it is held.</summary>
    public PointerButtons FreeLookButton { get; set; } = PointerButtons.Right;

    /// <summary>The button that orbits when <see cref="OrbitModifier"/> is held.</summary>
    public PointerButtons OrbitButton { get; set; } = PointerButtons.Right;

    /// <summary>
    /// The modifier that turns <see cref="OrbitButton"/> into an orbit instead
    /// of a freelook.
    /// </summary>
    public KeyModifiers OrbitModifier { get; set; } = KeyModifiers.Alt;

    /// <summary>The button that orbits when <see cref="AlternateOrbitModifier"/> is held.</summary>
    public PointerButtons AlternateOrbitButton { get; set; } = PointerButtons.Left;

    /// <summary>The modifier that turns <see cref="AlternateOrbitButton"/> into an orbit.</summary>
    public KeyModifiers AlternateOrbitModifier { get; set; } = KeyModifiers.Alt;

    /// <summary>The button that pans.</summary>
    public PointerButtons PanButton { get; set; } = PointerButtons.Middle;

    /// <summary>Which navigation gesture owns the pointer right now.</summary>
    public EditorNavigationGesture ActiveGesture => _gesture;

    /// <summary>
    /// True while a gesture is in progress, so the viewport must not start one
    /// of its own. The viewport asks this through <see cref="OwnsPointer"/>,
    /// which also accounts for the frame the navigation button comes up.
    /// </summary>
    public bool IsNavigating => _gesture != EditorNavigationGesture.None;

    /// <summary>True while a freelook is in progress.</summary>
    public bool IsFreeLooking => _gesture == EditorNavigationGesture.FreeLook;

    /// <summary>True while an orbit drag is in progress.</summary>
    public bool IsOrbiting => _gesture == EditorNavigationGesture.Orbit;

    /// <summary>True while a pan drag is in progress.</summary>
    public bool IsPanning => _gesture == EditorNavigationGesture.Pan;

    /// <summary>
    /// True while this controller is asking for a locked cursor. Reflects the
    /// <em>request</em>; whether it has landed is
    /// <see cref="Core.Input.ICursorLock.IsCursorLocked"/>.
    /// </summary>
    public bool IsCursorLockRequested => _cursorLockRequested;

    // --- Per-frame update ----------------------------------------------------

    /// <summary>
    /// Consumes one frame of input — look, orbit, pan, wheel and the movement
    /// axis — damps the live pose toward its target, and writes the result to
    /// the camera. Returns true when the camera moved this frame, so a host can
    /// skip redundant work when it did not.
    /// </summary>
    /// <remarks>
    /// <b>Gesture precedence, highest first:</b> orbit (a button plus its
    /// modifier), then freelook, then pan. Orbit outranks freelook because it is
    /// the one that had to be asked for explicitly; freelook outranks pan
    /// because a user holding both wants the gesture with the finer control.
    /// Exactly one is live per frame.
    /// <para>
    /// A host that also runs a gizmo should not call this while the gizmo is
    /// dragging: the same buttons mean different things mid-manipulation.
    /// <see cref="Viewport.ViewportInteractionController"/> already arbitrates
    /// that — and tells this controller about every frame it withheld, through
    /// <see cref="SuspendNavigation"/>, which is what stops the skipped travel
    /// from arriving all at once afterwards.
    /// </para>
    /// </remarks>
    public bool Update(in EditorInputFrame frame)
    {
        Vector2 delta = MeasureMotion(in frame);

        _lastGesture = _gesture;
        _gesture = ClassifyGesture(in frame);

        // The residue a released pointer gesture leaves keeps settling at the
        // POINTER constant: the hand was tracking at 20 ms, and braking to the
        // 80 ms flight constant on the release frame turns a flick's last few
        // degrees into a third of a second of ooze — a shortened rerun of the
        // exact glide the split exists to remove. A wheel notch or a framing
        // flight hands the settle back to the flight constant below.
        if (_gesture != EditorNavigationGesture.None)
            _pointerSettle = true;

        // The cursor is captured for as long as — and only as long as — a
        // freelook is live. Requested on the press frame, so it is usually in
        // effect by the second frame of the gesture; until then the gesture runs
        // off the ordinary absolute cursor, which is still perfectly valid.
        SetCursorLock(_gesture == EditorNavigationGesture.FreeLook);

        // Skip the frame a gesture starts on, exactly as the fly camera does:
        // the cursor may have travelled a long way since the last frame that
        // tracked it, and applying that as a drag delta snaps the view on every
        // button press. A gesture that changed kind mid-hold (Alt pressed while
        // right is down) counts as a start for the same reason.
        if (_gesture == _lastGesture)
        {
            switch (_gesture)
            {
                case EditorNavigationGesture.FreeLook:
                    ApplyFreeLook(delta);
                    break;
                case EditorNavigationGesture.Orbit:
                    ApplyOrbit(delta);
                    break;
                case EditorNavigationGesture.Pan:
                    ApplyPan(delta, frame.ViewportSize);
                    break;
            }
        }

        if (frame.ScrollDelta.Y != 0f)
        {
            // Held look button ⇒ the wheel is a speed trim, not a zoom. Tested
            // against the button rather than against the gesture so that the
            // notch on the very press frame — and one arriving while Alt has
            // temporarily turned the drag into an orbit — still means speed.
            if (frame.IsDown(FreeLookButton))
            {
                AdjustFlySpeed(frame.ScrollDelta.Y);
            }
            else
            {
                // A notch can still land on the frame the button came up but the
                // lock has not lifted yet, and the reported cursor is a frozen
                // leftover on that frame. Zoom down the view axis instead of
                // toward a pixel that means nothing.
                Vector2 target = frame.IsCursorLocked ? frame.ViewportSize * 0.5f : frame.CursorPosition;
                ApplyZoom(frame.ScrollDelta.Y, target, frame.ViewportSize);

                // Zoom-to-cursor moves the position target too, and that
                // motion wants the wheel's own glide.
                _pointerSettle = false;
            }
        }

        if (!frame.Navigation.IsIdle)
            ApplyMove(frame.Navigation, frame.DeltaTime);

        return Settle(frame.DeltaTime);
    }

    /// <summary>
    /// Whether the press arriving on this frame belongs to navigation, so the
    /// viewport must not interpret it as a pick, a marquee or a handle grab.
    /// A pure function of the frame — it mutates nothing.
    /// </summary>
    /// <remarks>
    /// This is the half of the arbitration that this class owns: the viewport
    /// owns "a gesture already in progress keeps the pointer", and this owns
    /// "these button/modifier combinations were always mine". Without it,
    /// Alt+left-drag would be claimed as a box select before the orbit ever saw
    /// it — which is precisely the bug this method exists to make impossible to
    /// reintroduce silently.
    /// </remarks>
    public bool ClaimsPress(in EditorInputFrame frame) =>
        frame.WasPressed(FreeLookButton) ||
        frame.WasPressed(PanButton) ||
        (frame.WasPressed(OrbitButton) && frame.HasModifiers(OrbitModifier)) ||
        (frame.WasPressed(AlternateOrbitButton) && frame.HasModifiers(AlternateOrbitModifier));

    /// <summary>
    /// Whether navigation owns this frame's pointer outright — either because
    /// the press arriving now belongs to it (<see cref="ClaimsPress"/>) or
    /// because a gesture that began on an earlier frame is still being held.
    /// The complete question the viewport has to ask before it interprets a
    /// press; a pure function of the frame and the gesture latch.
    /// </summary>
    /// <remarks>
    /// <b><see cref="ClaimsPress"/> alone is not the whole answer, and the gap
    /// is a definition rather than a race.</b> Claiming tests <em>press
    /// edges</em>, so it describes only the frame a gesture starts on: a left
    /// click landing on the third frame of a middle-drag pan, or on a freelook
    /// frame before the cursor-lock latch has landed, is not claimed by anything
    /// and used to be read as a pick — starting a gizmo drag, opening an undo
    /// transaction, and killing the navigation gesture that was already using
    /// the pointer. The held half is what <see cref="IsNavigating"/> reports.
    /// <para>
    /// The hold is tested against <em>this frame's</em> buttons, not merely
    /// against the latch, so the frame the navigation button comes up hands the
    /// pointer straight back: on that frame <see cref="Update"/> would end the
    /// gesture anyway, and answering "still mine" would swallow a press the user
    /// legitimately made.
    /// </para>
    /// </remarks>
    public bool OwnsPointer(in EditorInputFrame frame) =>
        ClaimsPress(in frame) ||
        (IsNavigating && ClassifyGesture(in frame) != EditorNavigationGesture.None);

    /// <summary>
    /// Forgets any gesture in progress and releases the cursor, so the next
    /// <see cref="Update(in EditorInputFrame)"/> re-anchors on that frame's
    /// cursor instead of measuring against the last one it happened to see.
    /// </summary>
    /// <remarks>
    /// <b>Every frame on which the host skips <see cref="Update"/> must call
    /// this.</b> The drag delta is <c>cursor − _lastCursor</c>, and
    /// <c>_lastCursor</c> only advances inside <see cref="Update"/>: a
    /// controller that sat out a gizmo drag, a marquee, or a minimized window
    /// with the look button still down would otherwise apply the entire withheld
    /// cursor travel as a single step the moment it runs again — a
    /// three-hundred-pixel gesture arriving as one 60° snap on a frame where the
    /// cursor did not move at all. Dropping the gesture latch turns that frame
    /// into a start frame, which the controller already knows to skip.
    /// <see cref="Viewport.ViewportInteractionController"/> does this for its
    /// own <see cref="Viewport.ViewportInteractionController.CameraController"/>;
    /// a host driving this class directly owns the same obligation.
    /// <para>
    /// Releasing the cursor here is the other half: a viewport that stops
    /// running the camera — because the window was minimized, or focus went
    /// somewhere else — must not leave the pointer captured and invisible over
    /// a window that is no longer looking at it.
    /// </para>
    /// </remarks>
    public void SuspendNavigation()
    {
        _gesture = EditorNavigationGesture.None;
        _lastGesture = EditorNavigationGesture.None;
        SetCursorLock(false);
    }

    /// <summary>
    /// Turns the camera in place by a mouse motion in pixels: moving right
    /// sweeps the view right, moving down tips it down — the same sense as the
    /// fly camera's look, so switching controllers does not invert the mouse.
    /// <b>The camera's position does not change.</b>
    /// </summary>
    public void ApplyFreeLook(Vector2 pixelDelta)
    {
        TargetYaw += pixelDelta.X * LookSensitivity;
        TargetPitch = Math.Clamp(TargetPitch - pixelDelta.Y * LookSensitivity, -PitchLimit, PitchLimit);
        NormalizeYaw();

        // Position is the half held exactly (see the type remarks); the focus
        // rides along in front of the new facing.
        TargetFocus = TargetPosition + TargetForward() * TargetDistance;
    }

    /// <summary>
    /// Swings the camera around <see cref="TargetFocus"/> at a fixed distance.
    /// The classic editor orbit, now reached through a modifier.
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

        // Focus is the half held exactly here — the mirror image of freelook.
        TargetPosition = TargetFocus - TargetForward() * TargetDistance;
    }

    /// <summary>
    /// Flies the camera along <paramref name="navigation"/>'s axis for
    /// <paramref name="deltaTime"/> seconds: X and Z run along the camera's own
    /// right and forward, Y along <em>world</em> up so rise and fall stay
    /// vertical however the camera is pitched.
    /// </summary>
    /// <remarks>
    /// The axis is normalized, so a diagonal is not faster than a straight line,
    /// and the whole displacement is added to the position and the focus
    /// together — a translation moves both halves of the pose by construction.
    /// </remarks>
    public void ApplyMove(in EditorNavigationInput navigation, float deltaTime)
    {
        if (navigation.IsIdle || deltaTime <= 0f)
            return;

        Basis(TargetYaw, TargetPitch, out Vector3 forward, out Vector3 right);
        Vector3 direction =
            right * navigation.Move.X +
            Vector3.UnitY * navigation.Move.Y +
            forward * navigation.Move.Z;

        float length = direction.Length();
        if (length <= 0f)
            return;

        float speed = navigation.Boost ? FlySpeed * MathF.Max(BoostMultiplier, 0f) : FlySpeed;
        Vector3 step = direction / length * (speed * deltaTime);

        TargetPosition += step;
        TargetFocus += step;
    }

    /// <summary>
    /// Trims <see cref="FlySpeed"/> by <paramref name="notches"/> wheel steps
    /// (positive = faster), multiplicatively and clamped. The new speed
    /// persists.
    /// </summary>
    public void AdjustFlySpeed(float notches)
    {
        float step = MathF.Max(FlySpeedStep, 1.0001f);
        FlySpeed = _flySpeed * MathF.Pow(step, notches);
    }

    /// <summary>
    /// Slides the camera in its own view plane so the world appears to follow
    /// the cursor: the world point under the cursor when the pan began stays
    /// under it, to the accuracy of the constant-depth approximation (the drag
    /// is tracked in the plane through the focus, which is what every editor
    /// does).
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
        Vector3 step =
            Camera.Right * (-pixelDelta.X * worldPerPixel) +
            Camera.Up * (pixelDelta.Y * worldPerPixel);

        TargetFocus += step;
        TargetPosition += step;
    }

    /// <summary>
    /// Dollies by <paramref name="notches"/> wheel steps (positive = toward the
    /// scene) and, when <see cref="ZoomToCursor"/> is on, slides the eye so
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
    /// Substituting <c>Position = Focus − F·d</c> turns the matching eye
    /// movement into <c>Position + (P − Position)·(1 − s)</c>: the eye simply
    /// travels the same fraction of the way toward the point under the cursor,
    /// which is both cheaper and — being a small displacement added to a large
    /// coordinate rather than a difference of two large ones — better
    /// conditioned a million units from the origin.
    /// </remarks>
    public void ApplyZoom(float notches, Vector2 cursorPosition, Vector2 viewportSize)
    {
        float scale = MathF.Pow(ZoomStep, -notches);
        float newDistance = Math.Clamp(TargetDistance * scale, MinDistance, MaxDistance);
        // Re-derive the ratio from the CLAMPED distance: at the stops the eye
        // must stop sliding too, or the camera keeps creeping toward the cursor
        // while appearing not to zoom.
        float applied = TargetDistance > 0f ? newDistance / TargetDistance : 1f;
        TargetDistance = newDistance;

        // Already at a stop: nothing moved, and re-deriving the position from
        // the focus would add a rounding to a coordinate that did not change.
        if (applied == 1f)
            return;

        if (ZoomToCursor && viewportSize.X > 0f && viewportSize.Y > 0f &&
            TryFocusPlanePoint(cursorPosition, viewportSize, out Vector3 point))
        {
            TargetFocus += (point - TargetFocus) * (1f - applied);
            TargetPosition += (point - TargetPosition) * (1f - applied);
            return;
        }

        // Classic axial dolly: the focus stays put and the eye moves in to the
        // new distance along the view axis.
        TargetPosition = TargetFocus - TargetForward() * TargetDistance;
    }

    // --- Framing -------------------------------------------------------------

    /// <summary>
    /// Frames the current selection: centres the view on the union of the
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
    /// Aims the camera at <paramref name="bounds"/>: the focus goes to its
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
        if (radius > 0f)
        {
            float halfVertical = Camera.FieldOfView * 0.5f;
            // The horizontal half-angle of a symmetric perspective frustum, from
            // the vertical one and the aspect ratio.
            float halfHorizontal = MathF.Atan(MathF.Tan(halfVertical) * MathF.Max(Camera.AspectRatio, 1e-4f));
            float halfAngle = MathF.Min(halfVertical, halfHorizontal);

            // Distance at which a sphere of `radius` exactly touches the cone of
            // half-angle `halfAngle`: d = r / sin(halfAngle).
            float distance = radius / MathF.Max(MathF.Sin(halfAngle), 1e-4f) * MathF.Max(FrameMargin, 1e-3f);
            TargetDistance = Math.Clamp(distance, MinDistance, MaxDistance);
        }

        TargetFocus = center;
        TargetPosition = TargetFocus - TargetForward() * TargetDistance;

        // A framing flight is the glide the long constant was made for.
        _pointerSettle = false;
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
    /// Places the camera outright by its <em>focus</em> — target and live state
    /// together — and writes the camera immediately, with no damping. For
    /// loading a saved viewport, or for a test that needs a known orbit.
    /// </summary>
    public void SetOrbit(Vector3 focus, float distance, float yaw, float pitch)
    {
        TargetFocus = focus;
        TargetDistance = Math.Clamp(distance, MinDistance, MaxDistance);
        TargetYaw = yaw;
        TargetPitch = Math.Clamp(pitch, -PitchLimit, PitchLimit);
        NormalizeYaw();
        TargetPosition = TargetFocus - TargetForward() * TargetDistance;
        SnapToTarget();
    }

    /// <summary>
    /// Places the camera outright by its <em>position</em> — the freelook-shaped
    /// counterpart of <see cref="SetOrbit"/>, which puts the focus
    /// <paramref name="distance"/> units straight ahead.
    /// </summary>
    public void SetPose(Vector3 position, float yaw, float pitch, float distance = DefaultDistance)
    {
        TargetDistance = Math.Clamp(distance, MinDistance, MaxDistance);
        TargetYaw = yaw;
        TargetPitch = Math.Clamp(pitch, -PitchLimit, PitchLimit);
        NormalizeYaw();
        TargetPosition = position;
        TargetFocus = TargetPosition + TargetForward() * TargetDistance;
        SnapToTarget();
    }

    /// <summary>
    /// Adopts the camera's current position and orientation as the live
    /// <em>and</em> target pose, placing the focus <paramref name="distance"/>
    /// units straight ahead of it. The handover from a fly camera.
    /// </summary>
    public void AdoptCamera(float distance = DefaultDistance) =>
        SetPose(Camera.Position, Camera.Yaw, Camera.Pitch, distance);

    /// <summary>
    /// Drops the live state onto the target and writes the camera — the
    /// instant-arrival form of <see cref="Update(in EditorInputFrame)"/>.
    /// </summary>
    public void SnapToTarget()
    {
        Position = TargetPosition;
        Focus = TargetFocus;
        Distance = TargetDistance;
        Yaw = TargetYaw;
        Pitch = TargetPitch;
        WriteCamera();
    }

    // --- Internals -----------------------------------------------------------

    // Which gesture this frame's held buttons ask for, in the precedence the
    // Update remarks give: orbit (a button plus its modifier), then freelook,
    // then pan. Shared with OwnsPointer rather than inlined into Update, so the
    // viewport's "is the camera still holding the pointer?" question can never
    // drift away from what Update would actually do with the same frame.
    private EditorNavigationGesture ClassifyGesture(in EditorInputFrame frame)
    {
        if ((frame.IsDown(OrbitButton) && frame.HasModifiers(OrbitModifier)) ||
            (frame.IsDown(AlternateOrbitButton) && frame.HasModifiers(AlternateOrbitModifier)))
        {
            return EditorNavigationGesture.Orbit;
        }

        if (frame.IsDown(FreeLookButton))
            return EditorNavigationGesture.FreeLook;

        return frame.IsDown(PanButton) ? EditorNavigationGesture.Pan : EditorNavigationGesture.None;
    }

    // This frame's cursor motion, and the bookkeeping that keeps it honest
    // across a lock transition.
    private Vector2 MeasureMotion(in EditorInputFrame frame)
    {
        // A captured cursor has no absolute position (the backend reports a
        // virtual one that walks off the screen), so relative motion is the only
        // signal there is. Unlocked, differencing the absolute position is what
        // every existing host and test supplies, so that stays the default.
        Vector2 delta = frame.IsCursorLocked ? frame.CursorDelta : frame.CursorPosition - _lastCursor;

        // The frame the lock lands on — either direction — carries the backend's
        // own teleport, not the user's hand. Drop it.
        if (frame.IsCursorLocked != _wasCursorLocked)
            delta = Vector2.Zero;

        _lastCursor = frame.CursorPosition;
        _wasCursorLocked = frame.IsCursorLocked;
        return delta;
    }

    // Raises or clears the cursor-lock request, and only on a real change, so a
    // controller with no ICursorLock attached costs one comparison per frame.
    private void SetCursorLock(bool wanted)
    {
        if (wanted == _cursorLockRequested)
            return;

        _cursorLockRequested = wanted;
        CursorLock?.RequestCursorMode(wanted ? CursorMode.Locked : CursorMode.Normal);
    }

    // Damps the live state toward the target and writes the camera. Returns
    // whether anything actually moved.
    //
    // Whether the look/move channels are settling POINTER-driven motion: set
    // while a navigation gesture is live and kept through its release residue,
    // handed back to the flight constant by a wheel zoom or a framing flight.
    private bool _pointerSettle;

    // TWO alphas, not one: the look and move channels follow the pointer while
    // a gesture is live or its residue is still draining
    // (PointerSmoothingTimeConstant), and the dolly always keeps the wheel's
    // own constant — the wheel is the channel the long constant was tuned for,
    // and a live orbit must not stiffen a zoom that is still gliding. A
    // framing flight and a zoom hand the look/move channels back to the flight
    // constant.
    private bool Settle(float deltaTime)
    {
        if (!IsSettling)
            return false;

        float lookMoveTau = _pointerSettle
            ? MathF.Min(PointerSmoothingTimeConstant, SmoothingTimeConstant)
            : SmoothingTimeConstant;

        float alphaMove = SmoothingAlpha(deltaTime, lookMoveTau);
        float alphaZoom = SmoothingAlpha(deltaTime, SmoothingTimeConstant);

        if (alphaMove >= 1f && alphaZoom >= 1f)
        {
            SnapToTarget();
            return true;
        }

        Position += (TargetPosition - Position) * alphaMove;
        Yaw += (TargetYaw - Yaw) * alphaMove;
        Pitch += (TargetPitch - Pitch) * alphaMove;
        // Geometric, because zoom is applied multiplicatively (see the type
        // remarks): equal fractions of the remaining ratio per unit time.
        Distance = Distance > 0f && TargetDistance > 0f
            ? Distance * MathF.Pow(TargetDistance / Distance, alphaZoom)
            : TargetDistance;

        // An alpha of one leaves float residue behind (gap * 1 added to the
        // live value is exact, but the geometric dolly is not), so a channel
        // that asked to arrive now is pinned to its target outright.
        if (alphaMove >= 1f)
        {
            Position = TargetPosition;
            Yaw = TargetYaw;
            Pitch = TargetPitch;
        }
        if (alphaZoom >= 1f)
            Distance = TargetDistance;

        // The focus is the derived half of a damped pose: it is what the camera
        // is actually looking at this frame, not a blend of two focus points.
        Basis(Yaw, Pitch, out Vector3 forward, out _);
        Focus = Position + forward * Distance;

        WriteCamera();
        return true;
    }

    // The standard frame-rate-independent exponential filter. A zero or
    // negative time constant, a non-positive frame time, or a frame long enough
    // that the filter would overshoot all collapse to "arrive now".
    private static float SmoothingAlpha(float deltaTime, float timeConstant)
    {
        if (timeConstant <= 0f || deltaTime <= 0f)
            return 1f;

        return 1f - MathF.Exp(-deltaTime / timeConstant);
    }

    private void WriteCamera()
    {
        // Angles first: the camera recomputes its basis on assignment, and it is
        // that basis the rest of the frame reads.
        Camera.Yaw = Yaw;
        Camera.Pitch = Pitch;
        Camera.Position = Position;
    }

    // The forward axis of the TARGET pose. Every gesture works in target space,
    // so it must not read Camera.Forward (which is one damped frame behind).
    private Vector3 TargetForward()
    {
        Basis(TargetYaw, TargetPitch, out Vector3 forward, out _);
        return forward;
    }

    // The same basis construction Camera.RecomputeBasis uses. Duplicated rather
    // than read back off the camera because the target pose has not been written
    // to it yet — and writing a provisional pose just to read the basis would
    // dirty the camera's matrices twice a frame.
    private static void Basis(float yaw, float pitch, out Vector3 forward, out Vector3 right)
    {
        float cosPitch = MathF.Cos(pitch);
        forward = Vector3.Normalize(new Vector3(
            MathF.Cos(yaw) * cosPitch,
            MathF.Sin(pitch),
            MathF.Sin(yaw) * cosPitch));
        right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
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
