using System.Numerics;

namespace SpectraEngine.Editing.Input;

/// <summary>
/// One frame's worth of "where the viewport camera should fly", already resolved
/// out of whatever keys the host binds: a movement axis and a boost flag.
/// </summary>
/// <remarks>
/// <b>This is how WASD reaches the camera without the editing layer learning a
/// keyboard.</b> The same rule that keeps <see cref="EditorInputFrame"/> free of
/// <c>Silk.NET.Input.Key</c> applies to movement: the host owns the keymap, so
/// it resolves W/A/S/D/Q/E — or the arrow cluster, or a gamepad stick, or a
/// WinUI accelerator — into this neutral axis and hands it over. Nothing below
/// the seam can tell which it was.
/// <para>
/// <b>The axis is in camera-relative units, not world ones</b>, because that is
/// the vocabulary the binding is written in ("forward" is a key, not a
/// direction). <see cref="Cameras.EditorCameraController"/> maps
/// <see cref="Move"/>.X and .Z onto the camera's own right/forward basis and
/// <see cref="Move"/>.Y onto <em>world</em> up — which is what makes Q/E (or
/// Space/Ctrl) rise and fall vertically rather than along a pitched camera's
/// tilted up axis. A camera-relative up would make "go up" mean "go up and
/// backwards" the moment you looked down, which is exactly the complaint every
/// engine that got this wrong receives.
/// </para>
/// <para>
/// Components are expected in [-1, 1]; the controller normalizes, so holding W
/// and D together is not faster than holding W alone.
/// </para>
/// </remarks>
public readonly struct EditorNavigationInput
{
    /// <summary>
    /// Creates a navigation input from an already-resolved axis.
    /// </summary>
    /// <param name="move">
    /// X = right, Y = world up, Z = forward. Each component in [-1, 1].
    /// </param>
    /// <param name="boost">True while the "move faster" modifier is held.</param>
    public EditorNavigationInput(Vector3 move, bool boost = false)
    {
        Move = move;
        Boost = boost;
    }

    /// <summary>
    /// The movement axis: X right, Y world up, Z forward. Zero on a frame with
    /// no movement key held, which is the default value of this struct.
    /// </summary>
    public Vector3 Move { get; }

    /// <summary>
    /// True while the sprint modifier is held — Shift, by convention.
    /// </summary>
    public bool Boost { get; }

    /// <summary>
    /// True when no movement is being asked for, so the camera can skip the
    /// whole fly path without a square root.
    /// </summary>
    public bool IsIdle => Move == Vector3.Zero;

    /// <summary>
    /// Builds an axis from six independent held-key booleans — the shape a host
    /// polling a keyboard actually has. Opposing keys cancel, so holding both
    /// W and S stands still rather than jittering.
    /// </summary>
    public static EditorNavigationInput FromKeys(
        bool forward, bool back, bool left, bool right, bool up, bool down, bool boost = false)
    {
        var move = new Vector3(
            (right ? 1f : 0f) - (left ? 1f : 0f),
            (up ? 1f : 0f) - (down ? 1f : 0f),
            (forward ? 1f : 0f) - (back ? 1f : 0f));

        return new EditorNavigationInput(move, boost);
    }
}
