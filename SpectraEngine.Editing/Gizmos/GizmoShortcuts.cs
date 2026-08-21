using System;
using System.Collections.Generic;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// The recommended default keyboard bindings for the manipulator, expressed as
/// key <em>names</em> so that the editing layer can carry them without naming a
/// backend's key enum.
/// </summary>
/// <remarks>
/// <b>Why names and not a key type.</b> This assembly must not reference the
/// windowing backend (that is what makes re-hosting the viewport a swap rather
/// than a rewrite), and inventing a second full keyboard enum next to Silk.NET's
/// would be a maintenance tax for no gain. A host resolves its own key to a name
/// — <c>Key.W.ToString()</c> for the Silk-backed viewport, a WinUI
/// <c>VirtualKey</c>'s name for an Uno-hosted one — asks
/// <see cref="TryResolve"/>, and calls <see cref="GizmoController.Apply"/> with
/// what comes back. Matching is ordinal and case-insensitive, and the table is a
/// plain switch, so nothing here reflects or allocates.
/// <para>
/// <b>THE BINDINGS, and why these.</b> Two families are offered at once, because
/// the two editors this engine is aimed at disagree and both audiences are real:
/// <list type="bullet">
///   <item><description>
///     <b>2 / 3 / 4 — move, resize, rotate.</b> Roblox Studio's exact numbers
///     (1 is Select there, which this engine's viewport also treats as "no
///     tool"). A Studio user's fingers already know these, including the fact
///     that resize comes before rotate.
///   </description></item>
///   <item><description>
///     <b>W / E / R — move, rotate, resize.</b> The Unity/Unreal/Godot row, which
///     is what almost every other 3D tool a level designer touches uses, and
///     which sits under the left hand next to WASD navigation — the way a Hammer
///     user already flies the viewport.
///   </description></item>
///   <item><description>
///     <b>X — world/local.</b> Unreal binds this idea to a toolbar globe and
///     Roblox to a checkbox, so there is no strong incumbent key; X is free,
///     adjacent to the WER row, and mnemonic for "axes".
///   </description></item>
///   <item><description>
///     <b>G — snap on/off, and [ / ] — finer/coarser.</b> The bracket pair is the
///     near-universal "step this setting" binding, and G is Hammer's snap-to-grid
///     toggle.
///   </description></item>
///   <item><description>
///     <b>Escape — cancel the drag in progress.</b> Universal, and the same verb
///     the tools already accept through <c>Update</c>'s cancel flag.
///   </description></item>
/// </list>
/// The table is a default, not a policy: a host with its own keymap should read
/// its own bindings and use this only as the fallback.
/// </para>
/// </remarks>
public static class GizmoShortcuts
{
    /// <summary>
    /// Resolves a key name to the verb it is bound to by default, or returns
    /// false when the key is not bound.
    /// </summary>
    /// <param name="keyName">
    /// The key's name as the host's input stack spells it — <c>"W"</c>,
    /// <c>"Number2"</c>, <c>"Escape"</c>. Matched case-insensitively, and the
    /// common spellings of the digit rows are all accepted so a host does not
    /// have to normalise first.
    /// </param>
    /// <param name="command">The verb the key is bound to, when this returns true.</param>
    public static bool TryResolve(string? keyName, out GizmoCommand command)
    {
        switch (keyName?.ToUpperInvariant())
        {
            // Roblox Studio's tool row. Silk.NET spells the number row
            // "Number2" and the keypad "Keypad2"; WinUI says "Number2"; a raw
            // character host says "2". All three mean the same key to a user.
            case "2" or "NUMBER2" or "D2" or "KEYPAD2":
                command = GizmoCommand.UseTranslate;
                return true;
            case "3" or "NUMBER3" or "D3" or "KEYPAD3":
                command = GizmoCommand.UseScale;
                return true;
            case "4" or "NUMBER4" or "D4" or "KEYPAD4":
                command = GizmoCommand.UseRotate;
                return true;

            // The Unity/Unreal/Godot row.
            case "W":
                command = GizmoCommand.UseTranslate;
                return true;
            case "E":
                command = GizmoCommand.UseRotate;
                return true;
            case "R":
                command = GizmoCommand.UseScale;
                return true;

            case "X":
                command = GizmoCommand.ToggleOrientation;
                return true;

            case "G":
                command = GizmoCommand.ToggleSnap;
                return true;
            case "[" or "LEFTBRACKET" or "OEM4":
                command = GizmoCommand.FinerSnap;
                return true;
            case "]" or "RIGHTBRACKET" or "OEM6":
                command = GizmoCommand.CoarserSnap;
                return true;

            case "ESCAPE" or "ESC":
                command = GizmoCommand.Cancel;
                return true;

            default:
                command = default;
                return false;
        }
    }

    /// <summary>
    /// The default bindings as data, for a keymap editor or a help overlay that
    /// wants to show them. Key names are given in the spelling
    /// <see cref="TryResolve"/> documents; a verb appears once per key bound to
    /// it.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, GizmoCommand>> Defaults { get; } =
    [
        new("2", GizmoCommand.UseTranslate),
        new("3", GizmoCommand.UseScale),
        new("4", GizmoCommand.UseRotate),
        new("W", GizmoCommand.UseTranslate),
        new("E", GizmoCommand.UseRotate),
        new("R", GizmoCommand.UseScale),
        new("X", GizmoCommand.ToggleOrientation),
        new("G", GizmoCommand.ToggleSnap),
        new("[", GizmoCommand.FinerSnap),
        new("]", GizmoCommand.CoarserSnap),
        new("Escape", GizmoCommand.Cancel),
    ];
}
