using System;
using System.Numerics;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Commands;
using SpectraEngine.Editing.Input;
using SpectraEngine.Editing.Undo;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>Which of a light's own handles a press landed on.</summary>
public enum LightHandle
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>The reach ring: drag out to grow the light's range.</summary>
    Range,

    /// <summary>The aim knob: drag to point a directional light.</summary>
    Aim,

    /// <summary>A spot's outer half-angle: the edge of the cone.</summary>
    ConeOuter,

    /// <summary>A spot's inner half-angle: where the falloff begins.</summary>
    ConeInner,

    /// <summary>A rect light's width.</summary>
    Width,

    /// <summary>A rect light's height.</summary>
    Height,

    /// <summary>A disc light's radius.</summary>
    Radius,
}

/// <summary>
/// The light's own manipulator: reach and aim, dragged in the viewport rather
/// than typed into the inspector.
/// </summary>
/// <remarks>
/// <para>
/// <b>A standalone tool, deliberately NOT a fourth <c>GizmoMode</c>.</b> Adding
/// one would touch twelve switches with no exhaustiveness check anywhere, each
/// with a silent <c>_ =&gt;</c> default - and it would be wrong on its own
/// terms: range and aim are PAYLOAD edits, not transform edits, and you need to
/// move a lamp constantly, so a mode would force a keypress between two halves
/// of one job. Deriving from <see cref="GizmoTool"/> drags <c>Mode</c> back in,
/// because it is abstract there and feeds <c>GizmoGeometry.Build</c>. The honest
/// cost of standing alone is the hit-grab-drag-commit machine below; it buys
/// zero silent-default sites and no change to <c>SceneEditorHost</c>,
/// <c>FrameSnapshot</c>, <c>ISceneEditor</c>, <c>Engine</c>, <c>ShellModel</c>,
/// <c>MainWindow</c> or the keyboard reference.
/// </para>
/// <para>
/// <b>It runs BESIDE the transform gizmo, not instead of it.</b> The transform
/// handles are tested first and win every tie, so a lamp is still moved,
/// rotated and resized by the tool the user already knows; these handles sit
/// where those do not.
/// </para>
/// <para>
/// <b>Every drag frame recomputes from the GRAB CAPTURE</b>, never from the
/// previous frame - the rule the whole gizmo spine already follows, so rounding
/// and snapping leave no residue and a cancel restores exactly.
/// </para>
/// <para>
/// <b>No fourth snap ladder.</b> A range is a length, so it quantises on the
/// translate tool's grid; there is nothing new to configure and nothing that
/// can drift out of step with the grid drawn on the floor.
/// </para>
/// </remarks>
public sealed class LightGizmo
{
    private readonly Scene _scene;
    private readonly UndoStack _undo;

    private SceneNode? _node;
    private LightHandle _handle;
    private SetLightCommand? _command;

    // The grab capture: everything a drag frame needs, taken once. Every drag
    // frame recomputes from THIS, never from the previous frame, so rounding and
    // snapping leave no residue and a cancel restores exactly.
    private float _grabScalar;
    private Vector2 _grabCursor;
    private Vector2 _grabAxis;
    private float _grabWorldPerPixel;

    public LightGizmo(Scene scene, UndoStack undo)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _undo = undo ?? throw new ArgumentNullException(nameof(undo));
    }

    /// <summary>Whether the tool draws and answers presses at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The snap policy for the range handle. Shared with the move tool.</summary>
    public SnapSettings? Snap { get; set; }

    /// <summary>Whether a drag is in progress.</summary>
    public bool IsDragging => _handle != LightHandle.None;

    /// <summary>The handle the cursor is over, or <see cref="LightHandle.None"/>.</summary>
    public LightHandle Hovered { get; private set; }

    /// <summary>How close, in screen pixels, a press must be to grab a handle.</summary>
    public float GrabPixels { get; set; } = 8f;

    /// <summary>The handle knob's radius in screen pixels.</summary>
    public const float KnobPixels = 5f;

    /// <summary>How far the aim knob sits from the lamp, in screen pixels.</summary>
    public const float AimReachPixels = 74f;

    /// <summary>
    /// Advances the tool by one frame.
    /// </summary>
    /// <param name="frame">This frame's input.</param>
    /// <param name="cancelRequested">Escape, or a viewport that lost focus.</param>
    /// <returns>True while this tool owns the pointer.</returns>
    public bool Update(in EditorInputFrame frame, bool cancelRequested)
    {
        if (IsDragging)
        {
            if (cancelRequested)
            {
                Cancel();
                return false;
            }

            if (!frame.IsDown(PointerButtons.Left))
            {
                Commit();
                return false;
            }

            Drag(in frame);
            return true;
        }

        Hovered = Enabled ? Pick(in frame, out _) : LightHandle.None;

        if (Hovered == LightHandle.None || !frame.WasPressed(PointerButtons.Left))
            return false;

        return TryBeginDrag(in frame);
    }

    /// <summary>
    /// Which handle a press at this frame's cursor would grab, and on which
    /// node. Pure - the hover oracle, and the same answer the press uses.
    /// </summary>
    public LightHandle Pick(in EditorInputFrame frame, out SceneNode? node)
    {
        node = null;

        if (!Enabled || !frame.IsPointerUsable || !TrySoleLight(out SceneNode? lamp, out Light? light))
            return LightHandle.None;

        Camera camera = _scene.Camera;
        Vector2 viewport = frame.ViewportSize;
        Vector3 at = lamp!.WorldPosition;

        if (!TryProject(camera, at, viewport, out Vector2 origin))
            return LightHandle.None;

        node = lamp;
        float grab = GrabPixels + KnobPixels;

        // The AIM knob first, because it sits away from the lamp while the range
        // ring passes through wherever the reach happens to land - and at some
        // camera angles the two coincide. Aim is the one that moves the light's
        // meaning, so it wins.
        if (light!.Kind == LightKind.Directional)
        {
            if (TryAimKnob(camera, lamp, viewport, out Vector2 knob) &&
                Vector2.Distance(frame.CursorPosition, knob) <= grab)
            {
                return LightHandle.Aim;
            }

            // A sun has no reach to drag.
            return LightHandle.None;
        }

        // The SHAPE handles before the range one, because a spot's cone rim and
        // its reach ring can coincide at some angles and the shape is the more
        // specific answer - the same "most specific wins" the gizmo handles
        // already use.
        foreach (LightHandle candidate in ShapeHandles(light.Kind))
        {
            if (TryKnobWorld(camera, lamp, light, candidate, viewport, out Vector3 world) &&
                TryProject(camera, world, viewport, out Vector2 knob) &&
                Vector2.Distance(frame.CursorPosition, knob) <= grab)
            {
                return candidate;
            }
        }

        // The range handle is a knob on the reach ring, placed along screen
        // RIGHT: one unambiguous direction, so the drag axis and the handle
        // agree without the user having to work out which way the ring faces.
        if (TryKnobWorld(camera, lamp, light, LightHandle.Range, viewport, out Vector3 rangeWorld) &&
            TryProject(camera, rangeWorld, viewport, out Vector2 rangeKnob) &&
            Vector2.Distance(frame.CursorPosition, rangeKnob) <= grab)
        {
            return LightHandle.Range;
        }

        node = null;
        return LightHandle.None;
    }

    // Which extra handles a kind offers, in pick order. A kind with none simply
    // yields nothing, which is what makes the loop above kind-agnostic.
    private static LightHandle[] ShapeHandles(LightKind kind) => kind switch
    {
        LightKind.Spot => [LightHandle.ConeInner, LightHandle.ConeOuter],
        LightKind.Rect => [LightHandle.Width, LightHandle.Height],
        LightKind.Disc => [LightHandle.Radius],
        _ => [],
    };

    /// <summary>Draws the handles for the selected light, if there is exactly one.</summary>
    public void Draw(DebugDraw output, Vector2 viewportSize)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (!Enabled || !TrySoleLight(out SceneNode? lamp, out Light? light))
            return;

        Camera camera = _scene.Camera;
        Vector3 at = lamp!.WorldPosition;

        if (light!.Kind == LightKind.Directional)
        {
            if (TryAimKnobWorld(camera, lamp, viewportSize, out Vector3 knob))
            {
                DrawKnob(output, knob, camera, viewportSize,
                    Colour(LightHandle.Aim), out _);
            }

            return;
        }

        if (TryKnobWorld(camera, lamp, light, LightHandle.Range, viewportSize, out Vector3 rangeKnob))
        {
            // The line back to the lamp is what says the knob BELONGS to it: a
            // floating dot beside a light icon is a second light.
            output.Line(at, rangeKnob, Colour(LightHandle.Range) * 0.5f);
            DrawKnob(output, rangeKnob, camera, viewportSize, Colour(LightHandle.Range), out _);
        }

        foreach (LightHandle handle in ShapeHandles(light.Kind))
        {
            if (!TryKnobWorld(camera, lamp, light, handle, viewportSize, out Vector3 knob))
                continue;

            output.Line(at, knob, Colour(handle) * 0.4f);
            DrawKnob(output, knob, camera, viewportSize, Colour(handle), out _);
        }
    }

    /// <summary>Abandons a drag in progress. Idempotent.</summary>
    public void Reset()
    {
        if (IsDragging)
            Cancel();

        Hovered = LightHandle.None;
    }

    // --- The gesture ---------------------------------------------------------

    private bool TryBeginDrag(in EditorInputFrame frame)
    {
        if (Pick(in frame, out SceneNode? lamp) is var handle &&
            (handle == LightHandle.None || lamp?.Light is null))
        {
            return false;
        }

        _node = lamp;
        _handle = handle;
        _grabCursor = frame.CursorPosition;
        _grabScalar = ScalarOf(lamp!.Light!, handle);
        _grabWorldPerPixel = GizmoMath.WorldPerPixel(
            _scene.Camera, frame.ViewportSize.Y,
            MathF.Max(GizmoMath.ViewDepth(_scene.Camera, lamp.WorldPosition), 0.01f));

        // The drag axis is the direction the KNOB moves on screen when the
        // scalar grows, measured once from the knob's own placement. Without
        // this every handle would drag along screen-right, which is right for
        // the range knob (it is placed along screen-right) and wrong for a
        // height knob placed along the light's own up axis.
        _grabAxis = ScreenAxisFor(lamp, lamp.Light!, handle, frame.ViewportSize);

        // ONE transaction for the whole gesture, exactly as the transform tools
        // do it: a drag that pushed a command per frame would need sixty
        // Ctrl+Z presses to undo one adjustment.
        _undo.BeginTransaction(TransactionName(handle));
        _command = null;
        return true;
    }

    private void Drag(in EditorInputFrame frame)
    {
        if (_node?.Light is not { } light)
            return;

        if (_handle == LightHandle.Aim)
        {
            DragAim(in frame);
            return;
        }

        DragScalar(in frame, light);
    }

    private void DragScalar(in EditorInputFrame frame, Light light)
    {
        // From the GRAB, never from the previous frame: the drag's travel along
        // this handle's own screen axis.
        float travelPixels = Vector2.Dot(frame.CursorPosition - _grabCursor, _grabAxis);

        bool angular = _handle is LightHandle.ConeInner or LightHandle.ConeOuter;

        // DEGREES per pixel for an angle, WORLD UNITS per pixel for a length -
        // and the length conversion is the lamp's own depth, so a knob follows
        // the cursor exactly whatever the camera distance.
        float value = angular
            ? _grabScalar + (travelPixels * DegreesPerPixel)
            : _grabScalar + (travelPixels * _grabWorldPerPixel);

        // IsActiveWith, not Enabled: Alt inverts the snap for the duration of a
        // gesture, and asking the setting directly would ignore the modifier
        // the user is holding right now. An ANGLE takes the rotate ladder and a
        // length takes the move one - the same two units the tools already have,
        // and no third ladder to keep in step.
        SnapSettings? snap = angular ? AngleSnap : Snap;
        if (snap is { } live && live.IsActiveWith(frame.Modifiers))
            value = live.SnapScalar(value);

        // CLAMPED HERE, not in the command. Light's own setters throw on a range
        // at or below zero, so a command carrying one would throw from inside
        // Do, halfway through an open transaction, leaving the history open and
        // the scene half-edited. Running the cursor past the end means "as far
        // as it goes"; the typed field in the inspector still gets a refusal,
        // which is the right answer there.
        Record(Apply(SetLightCommand.Settings.From(light), _handle, value));
    }

    // How fast a cone opens under the cursor. A quarter of a degree per pixel
    // puts the whole 0-89 range inside a comfortable drag without making the
    // rim jump between frames.
    private const float DegreesPerPixel = 0.25f;

    /// <summary>
    /// The snap ladder for angular handles. Shared with the ROTATE tool, for
    /// the same reason the lengths share the move tool's.
    /// </summary>
    public SnapSettings? AngleSnap { get; set; }

    private static float ScalarOf(Light light, LightHandle handle) => handle switch
    {
        LightHandle.ConeInner => light.InnerAngle,
        LightHandle.ConeOuter => light.OuterAngle,
        LightHandle.Width => light.Width,
        LightHandle.Height => light.Height,
        LightHandle.Radius => light.Radius,
        _ => light.Range,
    };

    private static SetLightCommand.Settings Apply(
        SetLightCommand.Settings settings, LightHandle handle, float value) => handle switch
    {
        // The angles clamp in Light's own setters (0..89, and outer never below
        // inner), and the extents clamp to a minimum there too - so what is
        // guarded HERE is only the one that throws.
        LightHandle.ConeInner => settings with { InnerAngle = value },
        LightHandle.ConeOuter => settings with { OuterAngle = value },
        LightHandle.Width => settings with { Width = MathF.Max(value, Light.MinimumExtent) },
        LightHandle.Height => settings with { Height = MathF.Max(value, Light.MinimumExtent) },
        LightHandle.Radius => settings with { Radius = MathF.Max(value, Light.MinimumExtent) },
        _ => settings with { Range = MathF.Max(value, MinimumRange) },
    };

    private static string TransactionName(LightHandle handle) => handle switch
    {
        LightHandle.Aim => "Aim light",
        LightHandle.ConeInner or LightHandle.ConeOuter => "Light cone",
        LightHandle.Width or LightHandle.Height or LightHandle.Radius => "Light size",
        _ => "Light range",
    };

    private void DragAim(in EditorInputFrame frame)
    {
        if (_node is not { } lamp)
            return;

        Camera camera = _scene.Camera;
        Vector3 at = lamp.WorldPosition;

        // The cursor ray, taken at the lamp's own view depth: the knob follows
        // the pointer on the plane through the lamp facing the camera, which is
        // the only interpretation under which dragging left aims left.
        Ray3 ray = camera.ScreenPointToRay(frame.CursorPosition, frame.ViewportSize);
        float depth = GizmoMath.ViewDepth(camera, at);
        if (depth <= 0.01f)
            return;

        float denominator = Vector3.Dot(ray.Direction, camera.Forward);
        if (MathF.Abs(denominator) < 1e-4f)
            return;

        float t = (depth - Vector3.Dot(ray.Origin - camera.Position, camera.Forward)) / denominator;
        Vector3 target = ray.Origin + (ray.Direction * t);

        Vector3 travel = target - at;
        if (travel.LengthSquared() < 1e-8f)
            return;

        // RotationForDirection takes the direction the light TRAVELS, which is
        // away from the lamp toward the knob. Backwards gives a sun shining out
        // of the ground, which is silent, dark, and exactly what that method's
        // own remarks were written to prevent.
        Quaternion rotation = Light.RotationForDirection(Vector3.Normalize(travel));

        var command = new SetLocalTransformCommand(
            lamp.Id,
            lamp.LocalTransform,
            lamp.LocalTransform with { Rotation = rotation })
        {
            Name = "Aim light",
        };

        _undo.Execute(command);
    }

    private void Record(SetLightCommand.Settings after)
    {
        if (_node is not { } lamp)
            return;

        // One command retargeted per frame rather than one command per frame:
        // SetLightCommand coalesces, so the transaction holds a single entry
        // whose before-state is the grab's.
        if (_command is { } existing)
        {
            existing.SetAfter(after);
            existing.Do(_scene);
            return;
        }

        _command = SetLightCommand.Capture(lamp, after);
        _undo.Execute(_command);
    }

    private void Commit()
    {
        _handle = LightHandle.None;
        _node = null;
        _command = null;

        // A drag that moved nothing records nothing: CommitTransaction lands no
        // history entry for an empty transaction, which is what makes a click
        // that turned out not to move anything free.
        _undo.CommitTransaction();
    }

    private void Cancel()
    {
        _handle = LightHandle.None;
        _node = null;
        _command = null;
        _undo.CancelTransaction();
    }

    /// <summary>
    /// The smallest range a drag may produce.
    /// </summary>
    /// <remarks>
    /// Not zero: <see cref="Light.Range"/> refuses anything at or below it, and
    /// a light with no reach is indistinguishable from one that is switched off
    /// while being much harder to notice.
    /// </remarks>
    public const float MinimumRange = 0.05f;

    // --- Placement -----------------------------------------------------------

    // The tool acts on a SINGLE selected light. A multi-light drag would have to
    // decide whether the ranges move together or converge, and both answers are
    // wrong for half the cases; the inspector's bulk edit already covers "make
    // these all the same".
    private bool TrySoleLight(out SceneNode? node, out Light? light)
    {
        node = null;
        light = null;

        var selection = _scene.Selection.Items;
        if (selection.Count != 1 || selection[0].Light is not { } only)
            return false;

        node = selection[0];
        light = only;
        return true;
    }

    private static Vector3 Colour(LightHandle handle) => handle switch
    {
        LightHandle.Aim => new Vector3(1f, 0.85f, 0.35f),

        // The cone knobs share a hue and differ in value, the same
        // one-colour-two-weights rule the selection outline follows: inner and
        // outer are two ends of one quantity, not two quantities.
        LightHandle.ConeOuter => new Vector3(0.55f, 1f, 0.75f),
        LightHandle.ConeInner => new Vector3(0.28f, 0.55f, 0.4f),

        LightHandle.Width or LightHandle.Height or LightHandle.Radius => new Vector3(1f, 0.6f, 0.85f),

        _ => new Vector3(0.6f, 0.9f, 1f),
    };

    private static bool TryProject(Camera camera, Vector3 world, Vector2 viewport, out Vector2 screen)
    {
        screen = default;

        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), camera.GetViewProjection());
        if (clip.W <= 1e-4f)
            return false;

        screen = new Vector2(
            ((clip.X / clip.W) + 1f) * 0.5f * viewport.X,
            (1f - (clip.Y / clip.W)) * 0.5f * viewport.Y);

        return true;
    }

    /// <summary>
    /// Where one handle's knob sits in the world.
    /// </summary>
    /// <remarks>
    /// <b>One function, used by the pick AND the draw.</b> Two would drift, and
    /// the symptom of drift is grabbing something other than what is on screen -
    /// the least reportable class of bug there is, and the same reason the light
    /// icon's radius is one shared constant.
    /// </remarks>
    private static bool TryKnobWorld(
        Camera camera, SceneNode lamp, Light light, LightHandle handle, Vector2 viewport, out Vector3 knob)
    {
        knob = default;
        if (viewport.Y <= 0f)
            return false;

        Vector3 at = lamp.WorldPosition;

        switch (handle)
        {
            case LightHandle.Range:
                // Along SCREEN right, not a world axis: one unambiguous
                // direction, so the drag and the handle agree without the user
                // working out which way the reach ring faces.
                knob = at + (camera.Right * light.Range);
                return true;

            case LightHandle.Aim:
                return TryAimKnobWorld(camera, lamp, viewport, out knob);
        }

        // Everything else is a SHAPE handle and lives on the light's own basis,
        // because that is what the shape is drawn in: a width knob has to sit on
        // the panel's edge, not somewhere on screen beside it.
        Basis(lamp, out Vector3 forward, out Vector3 right, out Vector3 up);

        switch (handle)
        {
            case LightHandle.Width:
                knob = at + (right * light.Width * 0.5f);
                return true;

            case LightHandle.Height:
                knob = at + (up * light.Height * 0.5f);
                return true;

            case LightHandle.Radius:
                knob = at + (right * light.Radius);
                return true;

            case LightHandle.ConeOuter:
            case LightHandle.ConeInner:
            {
                float degrees = handle == LightHandle.ConeOuter ? light.OuterAngle : light.InnerAngle;
                float radians = degrees * (MathF.PI / 180f);

                // ON the cone's rim at the light's own reach, which is where the
                // overlay draws that ring - so the knob is a point of the shape
                // rather than a marker floating near it.
                knob = at
                    + (forward * light.Range * MathF.Cos(radians))
                    + (right * light.Range * MathF.Sin(radians));

                return true;
            }
        }

        return false;
    }

    private static void Basis(SceneNode node, out Vector3 forward, out Vector3 right, out Vector3 up)
    {
        Matrix4x4 world = node.WorldMatrix;
        forward = Vector3.Normalize(new Vector3(world.M31, world.M32, world.M33));
        right = Vector3.Normalize(new Vector3(world.M11, world.M12, world.M13));
        up = Vector3.Normalize(new Vector3(world.M21, world.M22, world.M23));
    }

    /// <summary>
    /// The direction, in SCREEN pixels, that this handle's knob moves when its
    /// scalar grows.
    /// </summary>
    /// <remarks>
    /// Measured from the knob's own placement rather than assumed, because the
    /// handles do not share an axis: the range knob is placed along screen
    /// right, a height knob along the light's own up axis, and a cone knob
    /// along the rim. Assuming screen-right for all of them makes four of the
    /// six drag sideways when the user pulls them outward.
    /// </remarks>
    private Vector2 ScreenAxisFor(SceneNode lamp, Light light, LightHandle handle, Vector2 viewport)
    {
        Camera camera = _scene.Camera;

        if (!TryKnobWorld(camera, lamp, light, handle, viewport, out Vector3 here) ||
            !TryProject(camera, here, viewport, out Vector2 a))
        {
            return new Vector2(1f, 0f);
        }

        // A probe one per cent larger, projected: the finite difference IS the
        // screen direction, and it costs two projections at grab time rather
        // than a derivation per handle kind.
        float scalar = ScalarOf(light, handle);
        float probe = MathF.Max(scalar * 1.01f, scalar + 0.01f);

        Light widened = light.Clone();
        Apply(SetLightCommand.Settings.From(widened), handle, probe).ApplyTo(widened);

        if (!TryKnobWorld(camera, lamp, widened, handle, viewport, out Vector3 there) ||
            !TryProject(camera, there, viewport, out Vector2 b))
        {
            return new Vector2(1f, 0f);
        }

        Vector2 delta = b - a;
        return delta.LengthSquared() < 1e-6f ? new Vector2(1f, 0f) : Vector2.Normalize(delta);
    }

    private static bool TryAimKnobWorld(Camera camera, SceneNode lamp, Vector2 viewport, out Vector3 knob)
    {
        knob = default;
        if (viewport.Y <= 0f)
            return false;

        Vector3 at = lamp.WorldPosition;
        float depth = GizmoMath.ViewDepth(camera, at);
        if (depth <= 0.01f)
            return false;

        Matrix4x4 world = lamp.WorldMatrix;
        var travel = new Vector3(world.M31, world.M32, world.M33);
        if (travel.LengthSquared() < 1e-8f)
            return false;

        // A CONSTANT SCREEN distance from the lamp, not a constant world one:
        // the knob has to stay grabbable whatever the camera is doing, and the
        // aim it expresses is a direction, which has no length to be faithful
        // to. Deliberately not the range, either - a light whose reach changes
        // would otherwise move the knob that aims it.
        float reach = AimReachPixels * GizmoMath.WorldPerPixel(camera, viewport.Y, depth);
        knob = at + (Vector3.Normalize(travel) * reach);
        return true;
    }

    private static bool TryAimKnob(Camera camera, SceneNode lamp, Vector2 viewport, out Vector2 knob)
    {
        knob = default;
        return TryAimKnobWorld(camera, lamp, viewport, out Vector3 world)
            && TryProject(camera, world, viewport, out knob);
    }

    private void DrawKnob(
        DebugDraw output, Vector3 at, Camera camera, Vector2 viewport, Vector3 colour, out float radius)
    {
        float depth = MathF.Max(GizmoMath.ViewDepth(camera, at), 0.01f);
        radius = KnobPixels * GizmoMath.WorldPerPixel(camera, viewport.Y, depth);

        Vector3 right = camera.Right * radius;
        Vector3 up = camera.Up * radius;

        // A filled-looking diamond: four edges plus the two diagonals, which at
        // ten pixels across reads as solid without needing a triangle path the
        // line renderer does not have.
        output.Line(at + right, at + up, colour);
        output.Line(at + up, at - right, colour);
        output.Line(at - right, at - up, colour);
        output.Line(at - up, at + right, colour);
        output.Line(at - right, at + right, colour);
        output.Line(at - up, at + up, colour);
    }
}
