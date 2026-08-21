using SpectraEngine.Core.Input;
using System.Collections.Generic;

namespace SpectraEngine.Editing.Cameras;

/// <summary>
/// The recommended default keyboard bindings for the viewport camera, expressed
/// as key <em>names</em> — the same scheme, and the same reasoning, as
/// <see cref="Gizmos.GizmoShortcuts"/>: a host resolves its own key to a name,
/// asks here, and calls <see cref="EditorCameraController.Apply"/> with what
/// comes back.
/// </summary>
/// <remarks>
/// <b>THE BINDINGS.</b> <b>F</b> frames the selection — Blender, Maya, Unity,
/// Unreal and Godot all agree, which makes it about as close to a universal
/// binding as 3D editing has. <b>Shift+F</b> frames the whole scene, following
/// the same "widen the same verb" convention Unity uses; the modifier is passed
/// separately rather than baked into a key name, because modifier state already
/// travels with the input frame.
/// <para>
/// The table is a default, not a policy: a host with its own keymap should read
/// its own bindings and use this only as a fallback. Matching is ordinal and
/// case-insensitive, and the table is a plain switch — nothing reflects or
/// allocates.
/// </para>
/// </remarks>
public static class EditorCameraShortcuts
{
    /// <summary>
    /// Resolves a key name plus the modifiers held with it to the navigation
    /// verb it is bound to by default, or returns false when the key is not
    /// bound.
    /// </summary>
    /// <param name="keyName">The key's name as the host's input stack spells it — <c>"F"</c>.</param>
    /// <param name="modifiers">Modifiers held at the time of the press.</param>
    /// <param name="command">The verb the key is bound to, when this returns true.</param>
    public static bool TryResolve(string? keyName, KeyModifiers modifiers, out EditorCameraCommand command)
    {
        switch (keyName?.ToUpperInvariant())
        {
            case "F":
                command = (modifiers & KeyModifiers.Shift) != 0
                    ? EditorCameraCommand.FrameAll
                    : EditorCameraCommand.FrameSelection;
                return true;

            default:
                command = default;
                return false;
        }
    }

    /// <summary>
    /// Resolves a key name with no modifiers held. The common case, and the
    /// overload a host without a modifier-aware keymap can call.
    /// </summary>
    public static bool TryResolve(string? keyName, out EditorCameraCommand command) =>
        TryResolve(keyName, KeyModifiers.None, out command);

    /// <summary>
    /// The default bindings as data, for a keymap editor or a help overlay.
    /// These are <em>display</em> strings — the modifier prefix is spelled out
    /// for a human reader, whereas
    /// <see cref="TryResolve(string?, KeyModifiers, out EditorCameraCommand)"/>
    /// takes the bare key name and the modifier set separately.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, EditorCameraCommand>> Defaults { get; } =
    [
        new("F", EditorCameraCommand.FrameSelection),
        new("Shift+F", EditorCameraCommand.FrameAll),
    ];
}
