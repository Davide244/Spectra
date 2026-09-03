using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Cameras;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Hosting;
using System;
using System.Collections.Generic;

namespace SpectraEngine.Editor.Shell.Ribbon;

/// <summary>
/// One control on the ribbon: the id its markup carries, the word on it, and
/// the verb it posts.
/// </summary>
/// <param name="Id">
/// Stable, lower case, dotted. The SAME string is the control's <c>Tag</c> in
/// the tab's markup, which is what welds the roster to what is on screen: the
/// tab view validates its own tree against this roster at construction, and
/// <c>RibbonLayoutTests</c> re-checks it from the sources without needing a
/// window.
/// </param>
/// <param name="Label">The word on the control. Sentence case, never a glyph alone.</param>
/// <param name="Verb">The existing verb it resolves to.</param>
public sealed record RibbonItem(string Id, string Label, RibbonVerb Verb);

/// <summary>One captioned box of controls inside a tab.</summary>
/// <param name="Caption">
/// Names the box. Sentence case at label size, never letter-spaced uppercase:
/// a ribbon group caption is exactly where that habit reaches, and uppercase is
/// measurably slower to read because it destroys the word shape a reader
/// recognises before reading any letters.
/// </param>
public sealed record RibbonGroup(string Caption, IReadOnlyList<RibbonItem> Items);

/// <summary>One page of the command surface.</summary>
/// <param name="Id">Matches the markup file that renders it.</param>
/// <param name="Title">The word on the tab.</param>
public sealed record RibbonTab(string Id, string Title, IReadOnlyList<RibbonGroup> Groups);

/// <summary>
/// The ribbon's roster: which verbs are on which tab, which are never on one,
/// and which tab a session opens on.
/// </summary>
/// <remarks>
/// <para>
/// <b>THE TAB STRIP WAS RETIRED ONCE, ON EVIDENCE, AND THIS IS BUILT SO THE
/// EVIDENCE CANNOT REPEAT.</b> The old Home / Model / View strip died of three
/// things: Home and Model carried the same six verbs, Frame was written out
/// three times, and Insert - the one thing a first session needs - sat on the
/// tab nobody opened. The owner reopened the decision; the answer is not to
/// argue with those findings but to make each of them a property of this file.
/// </para>
/// <list type="number">
/// <item><b>No verb appears on two tabs.</b> A <see cref="RibbonVerb"/> is a
/// value, so this is a set comparison rather than a review habit, and
/// <c>RibbonLayoutTests</c> fails the build over it.</item>
/// <item><b>Insert is the first group of the DEFAULT tab</b>
/// (<see cref="DefaultTabId"/>), which is also the tab every launch opens on -
/// the active tab is deliberately NOT persisted, so no session can start with
/// Insert hidden behind a click.</item>
/// <item><b>Switching does something substantial.</b> Two tabs, and they divide
/// on a real axis: Build changes the level, View changes only how you look at
/// it. The floors in the tests exist so a future thin tab is a build failure
/// rather than a taste argument.</item>
/// </list>
/// <para>
/// <b>Two tabs, not three, and that is the honest count.</b> The verbs this
/// shell has fill two pages properly. A third would have to be padded out of
/// the document verbs the File menu already owns, and a thin tab is the retired
/// strip again.
/// </para>
/// <para>
/// <b><see cref="AlwaysVisible"/> is the part of the surface a collapse may not
/// take away.</b> A tab-scoped verb is one click plus a collapse state away, so
/// anything whose ABSENCE is dangerous cannot live on a tab: Play is already
/// outside this surface entirely (the menu row's far corner), and undo and redo
/// join it on the tab strip itself, because undo is the recovery verb for the
/// destructive verbs the Build tab carries and it must not be hidden by the
/// same click that hides them.
/// </para>
/// </remarks>
public static class RibbonLayout
{
    /// <summary>The tab a session opens on, every launch.</summary>
    public const string DefaultTabId = "build";

    /// <summary>The other one.</summary>
    public const string ViewTabId = "view";

    /// <summary>
    /// Undo and redo: on the tab strip, visible in both the expanded and the
    /// collapsed state, and on no tab.
    /// </summary>
    public static IReadOnlyList<RibbonItem> AlwaysVisible { get; } =
    [
        new("history.undo", "Undo", RibbonVerb.Of(EditorHostCommand.Undo)),
        new("history.redo", "Redo", RibbonVerb.Of(EditorHostCommand.Redo)),
    ];

    /// <summary>The pages, in strip order.</summary>
    public static IReadOnlyList<RibbonTab> Tabs { get; } =
    [
        new(DefaultTabId, "Build",
        [
            // FIRST GROUP OF THE FIRST TAB, and that placement is the whole
            // answer to the second finding. A new user's first question is
            // "how do I put something in the world"; the retired strip's
            // answer was on a tab they never opened.
            new RibbonGroup("Insert",
            [
                new RibbonItem("insert.block", "Block", RibbonVerb.Of(InsertKind.WorldBrush)),
                new RibbonItem("insert.part", "Part", RibbonVerb.Of(InsertKind.PartBrush)),
                new RibbonItem("insert.cut", "Cut", RibbonVerb.Of(InsertKind.SubtractiveBrush)),
                new RibbonItem("insert.light", "Light", RibbonVerb.Of(InsertKind.PointLight)),
                new RibbonItem("insert.panel", "Panel", RibbonVerb.Of(InsertKind.SurfaceLight)),
                new RibbonItem("insert.group", "Group", RibbonVerb.Of(InsertKind.Group)),
            ]),

            new RibbonGroup("Transform",
            [
                new RibbonItem("tool.move", "Move", RibbonVerb.Of(GizmoCommand.UseTranslate)),
                new RibbonItem("tool.rotate", "Rotate", RibbonVerb.Of(GizmoCommand.UseRotate)),
                new RibbonItem("tool.size", "Size", RibbonVerb.Of(GizmoCommand.UseScale)),
                new RibbonItem("choice.axes", "Axes", RibbonVerb.Of(RibbonToggle.Axes)),
                new RibbonItem("choice.handles", "Handles", RibbonVerb.Of(RibbonToggle.Handles)),
            ]),

            new RibbonGroup("Snap",
            [
                new RibbonItem("choice.snap", "Snap", RibbonVerb.Of(RibbonToggle.Snap)),
                new RibbonItem("snap.increment", "Increment", RibbonVerb.SnapIncrement()),
                new RibbonItem("snap.finer", "Finer", RibbonVerb.Of(GizmoCommand.FinerSnap)),
                new RibbonItem("snap.coarser", "Coarser", RibbonVerb.Of(GizmoCommand.CoarserSnap)),
            ]),

            // The four verbs that LEFT the one-row bar because they were four
            // anonymous glyphs in a row. They come back labelled, which is the
            // only form in which they were ever worth the space.
            new RibbonGroup("Arrange",
            [
                new RibbonItem("edit.duplicate", "Duplicate", RibbonVerb.Of(EditorHostCommand.Duplicate)),
                new RibbonItem("edit.delete", "Delete", RibbonVerb.Of(EditorHostCommand.Delete)),
                new RibbonItem("edit.convert", "Convert", RibbonVerb.Of(EditorHostCommand.ToggleBrushKind)),
                new RibbonItem("edit.group", "Group", RibbonVerb.Of(EditorHostCommand.Group)),
                new RibbonItem("edit.ungroup", "Ungroup", RibbonVerb.Of(EditorHostCommand.Ungroup)),
            ]),
        ]),

        new(ViewTabId, "View",
        [
            new RibbonGroup("Frame",
            [
                new RibbonItem("camera.frame", "Selection", RibbonVerb.Of(EditorCameraCommand.FrameSelection)),
                // Reachable from nothing at all before the ribbon: no key, no
                // menu, no button. The verb has existed since the editor camera
                // did.
                new RibbonItem("camera.frameall", "Everything", RibbonVerb.Of(EditorCameraCommand.FrameAll)),
            ]),

            new RibbonGroup("Ground grid",
            [
                new RibbonItem("grid.auto", "Auto", RibbonVerb.Of(EditorHostCommand.GridAuto)),
                new RibbonItem("grid.on", "Always", RibbonVerb.Of(EditorHostCommand.GridOn)),
                new RibbonItem("grid.off", "Off", RibbonVerb.Of(EditorHostCommand.GridOff)),
            ]),

            // Five verbs whose only route was a menu. A latched overlay with no
            // visible switch is the failure the viewport's standing chips were
            // built for; this is the switch.
            new RibbonGroup("Overlays",
            [
                new RibbonItem("overlay.wireframe", "Wireframe", RibbonVerb.Of(DebugVisualization.Wireframe)),
                new RibbonItem("overlay.vertices", "Vertices", RibbonVerb.Of(DebugVisualization.Vertices)),
                new RibbonItem("overlay.bounds", "Bounds", RibbonVerb.Of(DebugVisualization.Aabbs)),
                new RibbonItem("overlay.normals", "Normals", RibbonVerb.Of(DebugVisualization.Normals)),
                new RibbonItem("overlay.axes", "Node axes", RibbonVerb.Of(DebugVisualization.SceneGraph)),
            ]),
        ]),
    ];

    /// <summary>Every item on one tab, groups flattened, in reading order.</summary>
    public static IReadOnlyList<RibbonItem> ItemsOf(RibbonTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        var items = new List<RibbonItem>();
        foreach (RibbonGroup group in tab.Groups)
            items.AddRange(group.Items);

        return items;
    }

    /// <summary>The tab with this id, or null.</summary>
    public static RibbonTab? FindTab(string? id)
    {
        if (id is null)
            return null;

        foreach (RibbonTab tab in Tabs)
        {
            if (string.Equals(tab.Id, id, StringComparison.Ordinal))
                return tab;
        }

        return null;
    }

    /// <summary>
    /// The item with this id, wherever it lives - a tab or the always-visible
    /// strip.
    /// </summary>
    /// <remarks>
    /// This is what a click handler calls with the control's own
    /// <c>Tag</c>, so a control the roster does not know about resolves to
    /// nothing and posts nothing rather than posting the wrong verb.
    /// </remarks>
    public static RibbonItem? FindItem(string? id)
    {
        if (id is null)
            return null;

        foreach (RibbonItem item in AlwaysVisible)
        {
            if (string.Equals(item.Id, id, StringComparison.Ordinal))
                return item;
        }

        foreach (RibbonTab tab in Tabs)
        {
            foreach (RibbonGroup group in tab.Groups)
            {
                foreach (RibbonItem item in group.Items)
                {
                    if (string.Equals(item.Id, id, StringComparison.Ordinal))
                        return item;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The two idempotent verbs a two-way choice resolves between: the one it
    /// posts when the choice is currently OFF, and the one it posts when it is
    /// on.
    /// </summary>
    /// <remarks>
    /// Expressed once, here, so the dispatcher and the tests read the same
    /// pairing. "Currently on" means the non-default half: local axes, Classic
    /// handles, snapping enabled.
    /// </remarks>
    public static (GizmoCommand WhenOff, GizmoCommand WhenOn) CommandsFor(RibbonToggle toggle) => toggle switch
    {
        RibbonToggle.Axes => (GizmoCommand.UseWorldOrientation, GizmoCommand.UseLocalOrientation),
        RibbonToggle.Handles => (GizmoCommand.UseStudioStyle, GizmoCommand.UseClassicStyle),
        RibbonToggle.Snap => (GizmoCommand.DisableSnap, GizmoCommand.EnableSnap),
        _ => throw new ArgumentOutOfRangeException(nameof(toggle)),
    };
}
