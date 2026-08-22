using System;
using System.Numerics;
using Microsoft.Extensions.Logging;
using Silk.NET.Input;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Core.Physics.Character;

/// <summary>
/// Drives <see cref="CharacterMover"/> from a keyboard, a mouse and a
/// <see cref="Camera"/>: the layer that turns a proven mover into something a
/// person can walk around in.
/// </summary>
/// <remarks>
/// <para>
/// <b>It lives in Core, beside <see cref="FlyCameraController"/>, and names the
/// windowing backend's key enum for the same reason that one does.</b> A
/// character controller is engine furniture that a shipped game needs — unlike
/// gizmos and undo, which is why <em>those</em> are behind an interface in
/// another assembly. Re-hosting it against a different input stack means
/// replacing this class, not the mover below it.
/// </para>
/// <para>
/// <b>The frame/tick split is the whole of the design.</b> The engine samples
/// input once per frame and then runs zero to five fixed ticks, so
/// <see cref="BeginFrame"/> builds ONE command and every tick of that frame
/// replays it. That is only safe because the command carries no edges: the yaw
/// and pitch it carries are absolute angles rather than mouse deltas, so
/// replaying them is idempotent, and the jump edge is derived by the mover from
/// its own previous button state rather than from the command. Accumulating
/// mouse motion per tick instead would multiply your look speed by the frame's
/// tick count, which reads as a mouse that gets faster when the machine gets
/// slower.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only, like the scene it reads and the camera
/// it writes.
/// </para>
/// </remarks>
public sealed class FirstPersonController
{
    // The camera clamps pitch to this itself; mirroring the value here rather
    // than letting the stored angle run past it is what stops a dead zone.
    // Push into the ceiling for a second with an unclamped mirror and the next
    // several degrees of downward motion only unwind an angle nothing ever
    // applied, so the view stops responding for no reason a player can see.
    private const float PitchLimit = MathF.PI / 2f - 0.01f;

    // How fast the eye catches up after a step. One tick is ~16 ms, so 60 ms is
    // roughly four ticks: long enough to turn a 0.25 riser into a rise rather
    // than a jolt, short enough that the eye is not still climbing the previous
    // step when it reaches the next one on a staircase.
    private const float EyeSmoothingSeconds = 0.06f;

    private readonly Camera _camera;
    private readonly InputManager _input;
    private readonly ICharacterCollisionSource _source;
    private readonly ILogger _logger;

    private CharacterState _state;
    private CharacterCommand _command;

    private float _yaw;
    private float _pitch;

    // How far BEHIND its true height the eye currently is, worked off over
    // EyeSmoothingSeconds. Render-only: it never reaches the mover, so a
    // replayed tick produces the same physics whatever the eye is doing.
    private float _eyeLag;

    // The two poses render interpolation blends between: the feet before the
    // last tick and the feet after it. Simulation runs at 60 Hz and frames do
    // not, so without this the view advances in 60 Hz jerks however fast the
    // renderer is going — every tick's motion arriving as one jump across a
    // dozen identical frames.
    private Vector3 _renderPrevious;
    private Vector3 _renderPosition;

    // Camera pose borrowed on entry and handed back on exit, so toggling play
    // mode does not cost the viewpoint the user had navigated to.
    private Vector3 _restoreCameraPosition;
    private float _restoreCameraYaw;
    private float _restoreCameraPitch;

    private int _respawns;

    /// <summary>
    /// Builds a controller over a scene's camera. Nothing happens until
    /// <see cref="Enter"/>.
    /// </summary>
    public FirstPersonController(
        ILogger logger,
        Scene.Scene scene,
        InputManager input,
        CharacterTuning? tuning = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(input);

        _logger = logger;
        _camera = scene.Camera;
        _input = input;
        Tuning = tuning ?? new CharacterTuning();

        // The plane-set source, not a hull source: this is what makes a doorway
        // cut by a subtractive brush walkable, because it evaluates
        // ⋃additive \ ⋃subtractive per query rather than approximating each
        // brush by its uncut convex hull.
        var brushSource = new BrushPlaneCollisionSource(scene, Tuning);
        _source = brushSource;
        Collision = brushSource;

        _state = CharacterState.AtFeet(Vector3.Zero);
    }

    /// <summary>Every movement constant, live — editing one takes effect next tick.</summary>
    public CharacterTuning Tuning { get; }

    /// <summary>The brush-plane source, for the counters it discloses.</summary>
    public BrushPlaneCollisionSource Collision { get; }

    /// <summary>Whether the character is being simulated and owns the camera.</summary>
    public bool Active { get; private set; }

    /// <summary>Feet position, velocity and ground state — the whole of what is simulated.</summary>
    public CharacterState State => _state;

    /// <summary>Where <see cref="Enter"/> and the fall-out guard put the character.</summary>
    public Vector3 SpawnPosition { get; set; }

    /// <summary>The yaw <see cref="Enter"/> starts with, in radians.</summary>
    public float SpawnYaw { get; set; }

    /// <summary>Below this height the character is respawned rather than left falling.</summary>
    public float FallOutHeight { get; set; } = -1000f;

    /// <summary>Radians of look per pixel of mouse motion.</summary>
    public float LookSensitivity { get; set; } = 0.0022f;

    /// <summary>Times the fall-out guard has fired since the process started.</summary>
    public int Respawns => _respawns;

    /// <summary>Horizontal speed in spectraunits per second — what a speedometer would read.</summary>
    public float HorizontalSpeed => new Vector2(_state.Velocity.X, _state.Velocity.Z).Length();

    /// <summary>Takes the camera, locks the cursor, and puts the character at its spawn.</summary>
    public void Enter()
    {
        if (Active)
            return;

        _restoreCameraPosition = _camera.Position;
        _restoreCameraYaw = _camera.Yaw;
        _restoreCameraPitch = _camera.Pitch;

        _state = CharacterState.AtFeet(SpawnPosition);
        _command = default;
        _yaw = SpawnYaw;
        _pitch = 0f;
        _eyeLag = 0f;
        _renderPrevious = SpawnPosition;
        _renderPosition = SpawnPosition;
        Active = true;

        _input.RequestCursorMode(Input.CursorMode.Locked);
        UpdateView(0d, 1f);

        _logger.LogInformation(
            "Play mode ON: WASD walks, Shift sprints, Space jumps, mouse looks, Escape or the toggle key " +
            "leaves. Spawned at ({X:0.0}, {Y:0.0}, {Z:0.0}); {Speed:0.0} sunit/s walk, {Jump:0.00} sunit jump, " +
            "{Step:0.00} sunit step, {Slope:0} degree slope limit",
            SpawnPosition.X, SpawnPosition.Y, SpawnPosition.Z,
            Tuning.WalkSpeed, Tuning.JumpHeight, Tuning.StepHeight, Tuning.MaxSlopeAngleDegrees);
    }

    /// <summary>Releases the cursor and hands the camera back exactly as it was found.</summary>
    public void Exit()
    {
        if (!Active)
            return;

        Active = false;
        _input.RequestCursorMode(Input.CursorMode.Normal);

        _camera.Position = _restoreCameraPosition;
        _camera.Yaw = _restoreCameraYaw;
        _camera.Pitch = _restoreCameraPitch;

        _logger.LogInformation("Play mode OFF: camera restored to where it was left");
    }

    /// <summary>Enters if idle, leaves if active.</summary>
    public void Toggle()
    {
        if (Active) Exit();
        else Enter();
    }

    /// <summary>
    /// Samples the frame's input into the one command every tick of this frame
    /// will replay.
    /// </summary>
    public void BeginFrame(double deltaTime)
    {
        if (!Active)
            return;

        // Look only while the cursor is genuinely captured. The lock is a
        // request applied by the window thread a frame or two later, so acting
        // on motion before it lands would fold the cursor's walk to the window
        // centre into the view as one violent flick.
        if (_input.IsCursorLocked)
        {
            Vector2 delta = _input.MouseDelta;
            _yaw += delta.X * LookSensitivity;
            _pitch = Math.Clamp(_pitch - delta.Y * LookSensitivity, -PitchLimit, PitchLimit);

            // Keep yaw in a sane range rather than letting it grow without
            // bound: at float precision a few hours of spinning would start to
            // quantise the angle visibly.
            if (_yaw > MathF.PI) _yaw -= MathF.Tau;
            else if (_yaw < -MathF.PI) _yaw += MathF.Tau;
        }

        var buttons = CharacterButtons.None;
        if (_input.IsKeyDown(Key.Space))
            buttons |= CharacterButtons.Jump;
        if (_input.IsKeyDown(Key.ShiftLeft) || _input.IsKeyDown(Key.ShiftRight))
            buttons |= CharacterButtons.Sprint;

        // Crouch is deliberately unbound: CharacterButtons carries the flag but
        // the mover does nothing with it yet, and a key that visibly does
        // nothing is worse than a key that is documented as absent.

        float forward = (_input.IsKeyDown(Key.W) ? 1f : 0f) - (_input.IsKeyDown(Key.S) ? 1f : 0f);
        float strafe = (_input.IsKeyDown(Key.D) ? 1f : 0f) - (_input.IsKeyDown(Key.A) ? 1f : 0f);

        _command = new CharacterCommand
        {
            MoveForward = CharacterCommand.Axis(forward),
            MoveStrafe = CharacterCommand.Axis(strafe),
            Yaw = _yaw,
            Pitch = _pitch,
            Buttons = buttons,
        };
    }

    /// <summary>Advances the character by one fixed tick.</summary>
    public void Tick(float deltaTime)
    {
        if (!Active)
            return;

        // Captured before the tick, so the pair always brackets exactly one
        // step. Several ticks in one frame simply leave this at the last one.
        _renderPrevious = _state.Position;

        CharacterMover.Tick(ref _state, in _command, _source, Tuning, deltaTime);

        // Carried, not assigned: a staircase can step twice inside one frame's
        // tick budget, and taking only the last one would let the eye jump the
        // riser it skipped.
        _eyeLag += _state.SteppedUpBy;

        if (_state.Position.Y < FallOutHeight)
            Respawn();
    }

    /// <summary>
    /// Puts the eye where the head is. Render-only, once per frame, after the
    /// last tick.
    /// </summary>
    /// <param name="deltaTime">The frame's duration, for the step smoothing.</param>
    /// <param name="alpha">
    /// How far this frame sits between the last two ticks, in <c>[0, 1)</c> —
    /// the engine's own <c>FixedTickAccumulator.Alpha</c>. It costs up to one
    /// tick of view latency and buys a view that moves at the frame rate instead
    /// of at the tick rate, which is the same trade the render poses already
    /// make for every other body.
    /// </param>
    public void UpdateView(double deltaTime, float alpha)
    {
        if (!Active)
            return;

        // Exponential catch-up rather than a fixed rate, so one tall step and
        // one short step both feel like the same motion rather than the same
        // duration.
        if (_eyeLag > 0f)
        {
            _eyeLag *= MathF.Exp(-(float)deltaTime / EyeSmoothingSeconds);
            if (_eyeLag < 1e-3f)
                _eyeLag = 0f;
        }

        _renderPosition = Vector3.Lerp(_renderPrevious, _state.Position, Math.Clamp(alpha, 0f, 1f));

        _camera.Position = _renderPosition + new Vector3(0f, Tuning.EyeHeight - _eyeLag, 0f);
        _camera.Yaw = _yaw;
        _camera.Pitch = _pitch;
    }

    /// <summary>
    /// Draws the capsule and its ground normal. Useful only from outside the
    /// head — switch to a free camera first, which is exactly what the play-mode
    /// toggle is for.
    /// </summary>
    public void Draw(DebugDraw output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!Active)
            return;

        // Drawn at the INTERPOLATED pose, like the eye: an overlay that stutters
        // against a world the camera is smooth against reads as the overlay being
        // wrong rather than as the sample rate it actually is.
        var capsule = CharacterCapsule.FromFeet(_renderPosition, Tuning.StandHeight, Tuning.Radius);
        Vector3 color = _state.Grounded ? new Vector3(0.2f, 1f, 0.4f) : new Vector3(1f, 0.7f, 0.2f);

        DrawCapsule(output, in capsule, color);

        if (_state.Grounded)
        {
            output.Arrow(_renderPosition, _renderPosition + _state.GroundNormal * 0.75f,
                new Vector3(0.3f, 0.6f, 1f));
        }

        // The velocity is what tells you whether a wall is being slid along or
        // merely stood against, which the capsule alone cannot.
        Vector3 velocity = _state.Velocity;
        if (velocity.LengthSquared() > 1e-4f)
            output.Arrow(_renderPosition, _renderPosition + velocity * 0.2f, new Vector3(1f, 0.3f, 0.8f));
    }

    private void Respawn()
    {
        _respawns++;
        _logger.LogWarning(
            "Character fell below y={Limit:0.0} and was respawned (respawn {Count})",
            FallOutHeight, _respawns);

        _state = CharacterState.AtFeet(SpawnPosition);
        _eyeLag = 0f;

        // Both ends of the blend, or the frame after a respawn renders the
        // character sliding across the level from wherever it fell.
        _renderPrevious = SpawnPosition;
        _renderPosition = SpawnPosition;
    }

    // Two rings and four uprights, plus a pair of arcs per cap. Enough to read
    // the pose and the radius at a glance without turning the line buffer into
    // the frame's biggest draw.
    private static void DrawCapsule(DebugDraw output, in CharacterCapsule capsule, Vector3 color)
    {
        const int Segments = 16;
        float radius = capsule.Radius;

        Ring(output, capsule.Center1, radius, color, Segments);
        Ring(output, capsule.Center2, radius, color, Segments);

        for (int i = 0; i < 4; i++)
        {
            float angle = i * MathF.Tau / 4f;
            var offset = new Vector3(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius);
            output.Line(capsule.Center1 + offset, capsule.Center2 + offset, color);
        }

        Arc(output, capsule.Center1, radius, Vector3.UnitX, -Vector3.UnitY, color, Segments / 2);
        Arc(output, capsule.Center1, radius, Vector3.UnitZ, -Vector3.UnitY, color, Segments / 2);
        Arc(output, capsule.Center2, radius, Vector3.UnitX, Vector3.UnitY, color, Segments / 2);
        Arc(output, capsule.Center2, radius, Vector3.UnitZ, Vector3.UnitY, color, Segments / 2);
    }

    private static void Ring(DebugDraw output, Vector3 center, float radius, Vector3 color, int segments)
    {
        Vector3 previous = center + new Vector3(radius, 0f, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float angle = i * MathF.Tau / segments;
            Vector3 next = center + new Vector3(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius);
            output.Line(previous, next, color);
            previous = next;
        }
    }

    // A half-turn from `from` around to `to`, both unit and perpendicular.
    private static void Arc(
        DebugDraw output, Vector3 center, float radius, Vector3 from, Vector3 to, Vector3 color, int segments)
    {
        Vector3 previous = center + from * radius;
        for (int i = 1; i <= segments; i++)
        {
            float angle = i * MathF.PI / segments;
            Vector3 next = center + (from * MathF.Cos(angle) + to * MathF.Sin(angle)) * radius;
            output.Line(previous, next, color);
            previous = next;
        }
    }
}
