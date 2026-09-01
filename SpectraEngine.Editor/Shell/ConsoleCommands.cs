using SpectraEngine.Core.Hosting;
using SpectraEngine.Editing.Cameras;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Hosting;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SpectraEngine.Editor.Shell;

/// <summary>
/// What one typed console line resolved to.
/// </summary>
/// <param name="Reply">What to print. Never empty.</param>
/// <param name="Severity">How to print it.</param>
public readonly record struct ConsoleResult(string Reply, OutputSeverity Severity)
{
    /// <summary>It ran.</summary>
    public static ConsoleResult Ok(string reply) => new(reply, OutputSeverity.Info);

    /// <summary>It did not.</summary>
    public static ConsoleResult Fail(string reply) => new(reply, OutputSeverity.Error);
}

/// <summary>
/// A command line over the verbs the editor already has.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no console SUBSYSTEM, and this is deliberately not pretending to
/// be one.</b> No cvars, no bindings, no cfg files - <c>docs/console.md</c>'s
/// whole C-series is unbuilt. A Console tab with nothing behind it would be
/// exactly the failure the shell's own rules warn about: a control that does
/// nothing teaches, within one session, that this application's controls are
/// decorative. So it ships as a line over the verbs that DO exist -
/// <see cref="EditorHostCommand"/>, <see cref="GizmoCommand"/>,
/// <see cref="EditorCameraCommand"/>, <see cref="InsertKind"/> and the snap
/// increments - which is genuinely useful on day one and is the right shape for
/// the cvar console to grow into.
/// </para>
/// <para>
/// <b>A hand-written name table, never <c>Enum.Parse</c>.</b> The same AOT
/// discipline <c>GizmoShortcuts.TryResolve</c> follows: reflection over enum
/// names is exactly what trimming removes, and it would fail at runtime in a
/// published build having worked in every debug run.
/// </para>
/// <para>
/// <b>Every entry is a verb a key chord or a button also sends.</b> That is the
/// rule that keeps this from becoming a second path into the editor: the console
/// resolves a name to a verb and posts it, and the verb behaves identically
/// however it was asked for - including being refused while play mode owns the
/// scene.
/// </para>
/// </remarks>
public sealed class ConsoleCommands
{
    private readonly Func<EditorHostCommand, bool> _postHost;
    private readonly Func<GizmoCommand, bool> _postGizmo;
    private readonly Func<EditorCameraCommand, bool> _postCamera;
    private readonly Func<InsertKind, bool> _insert;
    private readonly Func<GizmoMode, float, bool> _setSnap;
    private readonly Func<string, bool> _setPipeline;
    private readonly Action<bool> _setPlaying;

    public ConsoleCommands(
        Func<EditorHostCommand, bool> postHost,
        Func<GizmoCommand, bool> postGizmo,
        Func<EditorCameraCommand, bool> postCamera,
        Func<InsertKind, bool> insert,
        Func<GizmoMode, float, bool> setSnap,
        Func<string, bool> setPipeline,
        Action<bool> setPlaying)
    {
        _postHost = postHost;
        _postGizmo = postGizmo;
        _postCamera = postCamera;
        _insert = insert;
        _setSnap = setSnap;
        _setPipeline = setPipeline;
        _setPlaying = setPlaying;
    }

    // ─── The table ───────────────────────────────────────
    //
    // Ordered as it prints in `help`: what you do to objects, then what you do
    // to the tools, then the view. Names are lower case and hyphenless because
    // that is what people type; aliases exist only where two obvious names
    // compete for one verb.

    private static readonly (string Name, string Help)[] Listing =
    [
        ("help", "list every command"),
        ("clear", "empty the output"),
        ("block", "insert a world brush at the view centre"),
        ("part", "insert a part brush"),
        ("cut", "insert a subtractive brush"),
        ("light", "insert a point light"),
        ("panel", "insert a surface light on what you are looking at"),
        ("group", "group the selection"),
        ("ungroup", "ungroup the selection"),
        ("duplicate", "duplicate the selection"),
        ("delete", "delete the selection"),
        ("undo", "undo one edit"),
        ("redo", "redo one edit"),
        ("kind", "flip the selection between world and part"),
        ("move", "use the move tool"),
        ("rotate", "use the rotate tool"),
        ("resize", "use the resize tool"),
        ("snap", "snap on | off"),
        ("grid <n>", "set the move grid, in world units"),
        ("angle <n>", "set the rotate snap, in degrees"),
        ("step <n>", "set the resize step, in world units"),
        ("axes", "axes world | local"),
        ("handles", "handles studio | classic"),
        ("frame", "frame the selection"),
        ("nav", "swap the editor camera for the fly camera"),
        ("play", "play on | off"),
        ("pipeline <name>", "switch renderer pipeline"),
    ];

    /// <summary>Runs one typed line.</summary>
    public ConsoleResult Execute(string line)
    {
        string[] parts = (line ?? string.Empty).Split(
            ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
            return ConsoleResult.Ok(string.Empty);

        string verb = parts[0].ToLowerInvariant();
        string? arg = parts.Length > 1 ? parts[1].ToLowerInvariant() : null;

        return verb switch
        {
            "help" or "?" => ConsoleResult.Ok(BuildHelp()),
            "clear" or "cls" => ConsoleResult.Ok(ClearMarker),

            "block" or "brush" => Insert(InsertKind.WorldBrush, "block"),
            "part" => Insert(InsertKind.PartBrush, "part"),
            "cut" or "subtract" => Insert(InsertKind.SubtractiveBrush, "cut"),
            "light" => Insert(InsertKind.PointLight, "light"),
            "panel" or "surfacelight" => Insert(InsertKind.SurfaceLight, "surface light"),

            "group" => Host(EditorHostCommand.Group, "grouped"),
            "ungroup" => Host(EditorHostCommand.Ungroup, "ungrouped"),
            "duplicate" or "dup" => Host(EditorHostCommand.Duplicate, "duplicated"),
            "delete" or "del" => Host(EditorHostCommand.Delete, "deleted"),
            "undo" => Host(EditorHostCommand.Undo, "undone"),
            "redo" => Host(EditorHostCommand.Redo, "redone"),
            "kind" => Host(EditorHostCommand.ToggleBrushKind, "brush kind flipped"),
            "nav" => Host(EditorHostCommand.ToggleNavigation, "navigation swapped"),

            "move" => Gizmo(GizmoCommand.UseTranslate, "move"),
            "rotate" => Gizmo(GizmoCommand.UseRotate, "rotate"),
            "resize" or "scale" => Gizmo(GizmoCommand.UseScale, "resize"),

            "snap" => OnOff(arg, on => Gizmo(
                on ? GizmoCommand.EnableSnap : GizmoCommand.DisableSnap,
                on ? "snap on" : "snap off")),

            "axes" => arg switch
            {
                "world" => Gizmo(GizmoCommand.UseWorldOrientation, "axes world"),
                "local" => Gizmo(GizmoCommand.UseLocalOrientation, "axes local"),
                _ => ConsoleResult.Fail("axes takes world or local"),
            },

            "handles" => arg switch
            {
                "studio" => Gizmo(GizmoCommand.UseStudioStyle, "handles Studio"),
                "classic" => Gizmo(GizmoCommand.UseClassicStyle, "handles Classic"),
                _ => ConsoleResult.Fail("handles takes studio or classic"),
            },

            "grid" => Snap(GizmoMode.Translate, arg, "grid", "su"),
            "angle" => Snap(GizmoMode.Rotate, arg, "angle", "deg"),
            "step" => Snap(GizmoMode.Scale, arg, "step", "su"),

            "frame" or "f" => _postCamera(EditorCameraCommand.FrameSelection)
                ? ConsoleResult.Ok("framed the selection")
                : ConsoleResult.Fail(NoSession),

            "play" => OnOff(arg, on =>
            {
                _setPlaying(on);
                return ConsoleResult.Ok(on ? "play requested" : "stop requested");
            }),

            "pipeline" => arg is null
                ? ConsoleResult.Fail("pipeline takes a name; try forward, deferred or wireframe")
                : _setPipeline(arg)
                    ? ConsoleResult.Ok($"pipeline {arg}")
                    : ConsoleResult.Fail(NoSession),

            _ => ConsoleResult.Fail($"no command named '{verb}'. Type help."),
        };
    }

    /// <summary>
    /// The reply text that means "empty the output rather than printing this".
    /// </summary>
    /// <remarks>
    /// A sentinel rather than a second return channel, because <c>clear</c> is
    /// the only command in the table whose effect is on the log itself and one
    /// more field on every result would be carried by every other command for
    /// nothing.
    /// </remarks>
    public const string ClearMarker = " clear";

    private const string NoSession = "nothing is open";

    private ConsoleResult Insert(InsertKind kind, string what) =>
        _insert(kind) ? ConsoleResult.Ok($"inserted a {what}") : ConsoleResult.Fail(NoSession);

    private ConsoleResult Host(EditorHostCommand command, string done) =>
        _postHost(command) ? ConsoleResult.Ok(done) : ConsoleResult.Fail(NoSession);

    private ConsoleResult Gizmo(GizmoCommand command, string done) =>
        _postGizmo(command) ? ConsoleResult.Ok(done) : ConsoleResult.Fail(NoSession);

    private ConsoleResult Snap(GizmoMode tool, string? arg, string name, string unit)
    {
        if (arg is null)
            return ConsoleResult.Fail($"{name} takes a number");

        // Invariant, because a console is typed by a person who expects a dot
        // whatever their machine's locale does with decimal separators - and
        // because a value that parses on one machine and not another is the
        // worst kind of report to receive.
        if (!float.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ||
            value <= 0f || !float.IsFinite(value))
        {
            return ConsoleResult.Fail($"'{arg}' is not a positive number");
        }

        return _setSnap(tool, value)
            ? ConsoleResult.Ok($"{name} {value.ToString("0.###", CultureInfo.InvariantCulture)} {unit}")
            : ConsoleResult.Fail(NoSession);
    }

    // "on"/"off", and a bare verb means ON. Never a toggle: the console reads
    // the same as the buttons, and set semantics is what makes both safe.
    private static ConsoleResult OnOff(string? arg, Func<bool, ConsoleResult> apply) => arg switch
    {
        null or "on" or "1" or "true" => apply(true),
        "off" or "0" or "false" => apply(false),
        _ => ConsoleResult.Fail($"'{arg}' is not on or off"),
    };

    private static string BuildHelp()
    {
        var sb = new StringBuilder();
        sb.Append("commands:");

        foreach ((string name, string help) in Listing)
            sb.Append('\n').Append("  ").Append(name.PadRight(16)).Append(help);

        return sb.ToString();
    }

    /// <summary>Every command name, for completion.</summary>
    public static IEnumerable<string> Names
    {
        get
        {
            foreach ((string name, _) in Listing)
                yield return name.Split(' ')[0];
        }
    }
}
