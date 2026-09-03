using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Cameras;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Hosting;
using System;
using System.Collections.Generic;

namespace SpectraEngine.Editor.Shell.Ribbon;

/// <summary>
/// How much room a ribbon control takes, which on this surface is the same
/// question as how important the verb is.
/// </summary>
/// <remarks>
/// <b>An Office ribbon is a size hierarchy before it is anything else, and the
/// first version of this surface had none.</b> Thirty controls, all
/// <c>Button.seg</c> at 26px, so "insert a block" and "toggle the wireframe
/// overlay" were the same object at the same weight and nothing on the surface
/// said what it was for. Declared here rather than left to the markup because
/// it is what the width floor is computed from: a group that no longer fits the
/// window's own minimum is then a failing test rather than something somebody
/// notices in a screenshot.
/// </remarks>
public enum RibbonItemSize
{
    /// <summary>A 22px row: a 16px glyph and a word, three to a column.</summary>
    Small,

    /// <summary>A 58x66 button: a 32px glyph over up to two lines of label.</summary>
    Large,
}

/// <summary>
/// What kind of control a ribbon item is, which decides what it needs wired
/// rather than how it is drawn.
/// </summary>
/// <remarks>
/// <para>
/// <b>Behavioural, not cosmetic, which is why it lives in the roster.</b> A
/// <see cref="Check"/> needs a lit state bound to something; a <see cref="Radio"/>
/// is one of a set whose exclusivity comes from the verbs rather than the
/// controls; a <see cref="Field"/> posts no click at all; a <see cref="Split"/>
/// has two hit regions. Pixel geometry stays in the markup - this is the part a
/// test can hold.
/// </para>
/// <para>
/// <see cref="RibbonLayout.RequiredClass"/> maps a declared kind onto the style
/// class its control must wear, and the page checks it at construction. Before
/// that, a page could draw any control it liked under a valid <c>Tag</c>: a
/// check row quietly rendered as a plain button looks finished and has no lit
/// state, which is a control that lies about what the engine is doing.
/// </para>
/// </remarks>
public enum RibbonControlKind
{
    /// <summary>Posts its verb and nothing else.</summary>
    Button,

    /// <summary>Posts a set-verb and shows whether it is the live one.</summary>
    Toggle,

    /// <summary>One of a mutually exclusive set, each a set-verb.</summary>
    Radio,

    /// <summary>An independent on/off, drawn as a box and a tick.</summary>
    Check,

    /// <summary>A two-way choice showing its current value as a word.</summary>
    Chip,

    /// <summary>Carries a typed value; commits through focus and Enter, never a click.</summary>
    Field,

    /// <summary>Half of a stepper pair beside a field.</summary>
    Stepper,

    /// <summary>A main action plus a caret that opens a list.</summary>
    Split,
}

/// <summary>
/// One control on the ribbon: the id its markup carries, the word on it, the
/// verb it posts, and what shape it takes.
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
/// <param name="Size">
/// How much room it takes. A <see cref="RibbonItemSize.Large"/> label is capped
/// at twelve characters by a test, because the button is 58 wide and wraps to
/// two lines of 13: a third line has nowhere to go and <c>MaxLines</c> would
/// silently eat it.
/// </param>
/// <param name="Kind">What it needs wired. See <see cref="RibbonControlKind"/>.</param>
public sealed record RibbonItem(
    string Id,
    string Label,
    RibbonVerb Verb,
    RibbonItemSize Size = RibbonItemSize.Small,
    RibbonControlKind Kind = RibbonControlKind.Button);

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
            //
            // THE FOUR THINGS A LEVEL IS MADE OF GET LARGE BUTTONS and the rest
            // is a column beside them: blocks, parts, cuts and lights, against
            // a panel and a container that each place something into one of
            // those. Their glyphs carry the node-kind tints the palette has had
            // since the first pass and had spent in exactly one 15px tree
            // glyph - which is why four insert buttons in a row used to read
            // as four grey outlines.
            new RibbonGroup("Insert",
            [
                new RibbonItem("insert.block", "Block", RibbonVerb.Of(InsertKind.WorldBrush),
                    RibbonItemSize.Large),
                new RibbonItem("insert.part", "Part", RibbonVerb.Of(InsertKind.PartBrush),
                    RibbonItemSize.Large),
                new RibbonItem("insert.cut", "Cut", RibbonVerb.Of(InsertKind.SubtractiveBrush),
                    RibbonItemSize.Large),
                new RibbonItem("insert.light", "Light", RibbonVerb.Of(InsertKind.PointLight),
                    RibbonItemSize.Large),
                new RibbonItem("insert.panel", "Panel", RibbonVerb.Of(InsertKind.SurfaceLight)),
                new RibbonItem("insert.group", "Group", RibbonVerb.Of(InsertKind.Group)),

                // THE ONE SPLIT BUTTON, and the only Office idiom on this
                // surface that cannot be faked with what was already here: a
                // main half that places the last class used, and a caret that
                // opens the list. The caret names no verb and therefore carries
                // no Tag - it opens something rather than doing something - so
                // the roster describes one control and the validator sees one.
                //
                // Its flyout entries are deliberately NOT roster items. The
                // roster is compile-time data and an entity class comes from
                // the project's own .sentdef, so a build cannot know them; a
                // test says so, because a roster that quietly grew a dynamic
                // entry would make every claim in this file about a fixed set
                // untrue.
                new RibbonItem("insert.entity", "Entity", RibbonVerb.InsertEntity(),
                    RibbonItemSize.Large, RibbonControlKind.Split),
            ]),

            // The live tool is the most-read state on this surface, so it gets
            // the second set of large buttons and they light amber like every
            // other "this one is running" in the shell. The two-way choices
            // beside them stay chips, because "world" is not the enabled state
            // of "local" and a lit icon cannot say which of two words is
            // current.
            new RibbonGroup("Transform",
            [
                new RibbonItem("tool.move", "Move", RibbonVerb.Of(GizmoCommand.UseTranslate),
                    RibbonItemSize.Large, RibbonControlKind.Toggle),
                new RibbonItem("tool.rotate", "Rotate", RibbonVerb.Of(GizmoCommand.UseRotate),
                    RibbonItemSize.Large, RibbonControlKind.Toggle),
                new RibbonItem("tool.size", "Size", RibbonVerb.Of(GizmoCommand.UseScale),
                    RibbonItemSize.Large, RibbonControlKind.Toggle),
                new RibbonItem("choice.axes", "Axes", RibbonVerb.Of(RibbonToggle.Axes),
                    RibbonItemSize.Small, RibbonControlKind.Chip),
                new RibbonItem("choice.handles", "Handles", RibbonVerb.Of(RibbonToggle.Handles),
                    RibbonItemSize.Small, RibbonControlKind.Chip),
            ]),

            // A field with a stepper pair beside it, which is Office's spinner
            // and is what these two verbs have always been: [ and ] step one
            // number, and a pair of arrows against the box holding it says so
            // without a word.
            new RibbonGroup("Snap",
            [
                new RibbonItem("choice.snap", "Snap", RibbonVerb.Of(RibbonToggle.Snap),
                    RibbonItemSize.Small, RibbonControlKind.Check),
                new RibbonItem("snap.increment", "Increment", RibbonVerb.SnapIncrement(),
                    RibbonItemSize.Small, RibbonControlKind.Field),
                new RibbonItem("snap.finer", "Finer", RibbonVerb.Of(GizmoCommand.FinerSnap),
                    RibbonItemSize.Small, RibbonControlKind.Stepper),
                new RibbonItem("snap.coarser", "Coarser", RibbonVerb.Of(GizmoCommand.CoarserSnap),
                    RibbonItemSize.Small, RibbonControlKind.Stepper),
            ]),

            // The four verbs that LEFT the one-row bar because they were four
            // anonymous glyphs in a row. Duplicate leads at full size because
            // it is the one of the five somebody reaches for repeatedly.
            new RibbonGroup("Arrange",
            [
                new RibbonItem("edit.duplicate", "Duplicate", RibbonVerb.Of(EditorHostCommand.Duplicate),
                    RibbonItemSize.Large),
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
                new RibbonItem("camera.frame", "Selection", RibbonVerb.Of(EditorCameraCommand.FrameSelection),
                    RibbonItemSize.Large),
                // Reachable from nothing at all before the ribbon: no key, no
                // menu, no button. The verb has existed since the editor camera
                // did.
                new RibbonItem("camera.frameall", "Everything", RibbonVerb.Of(EditorCameraCommand.FrameAll),
                    RibbonItemSize.Large),
            ]),

            // A RADIO COLUMN rather than a dropdown, and the difference is what
            // is visible at rest: three modes, one lit, all three readable
            // without opening anything. A dropdown showing "auto" hides the
            // fact that there are two other answers. The exclusivity is a
            // property of the three set-verbs, never of the controls.
            new RibbonGroup("Ground grid",
            [
                new RibbonItem("grid.auto", "Auto", RibbonVerb.Of(EditorHostCommand.GridAuto),
                    RibbonItemSize.Small, RibbonControlKind.Radio),
                new RibbonItem("grid.on", "Always", RibbonVerb.Of(EditorHostCommand.GridOn),
                    RibbonItemSize.Small, RibbonControlKind.Radio),
                new RibbonItem("grid.off", "Off", RibbonVerb.Of(EditorHostCommand.GridOff),
                    RibbonItemSize.Small, RibbonControlKind.Radio),
            ]),

            // Five verbs whose only route was a menu. A latched overlay with no
            // visible switch is the failure the viewport's standing chips were
            // built for; this is the switch.
            //
            // CHECK ROWS, which is Office's Show group and is also how the
            // icons come back here. The first version of this page carried no
            // glyphs at all, and its comment was right about why - five
            // ambiguous 16px outlines in a row are a grey texture rather than
            // five icons. A box and a tick is not an ambiguous outline: it says
            // on or off and leaves the word to say which overlay.
            new RibbonGroup("Overlays",
            [
                new RibbonItem("overlay.wireframe", "Wireframe", RibbonVerb.Of(DebugVisualization.Wireframe),
                    RibbonItemSize.Small, RibbonControlKind.Check),
                new RibbonItem("overlay.vertices", "Vertices", RibbonVerb.Of(DebugVisualization.Vertices),
                    RibbonItemSize.Small, RibbonControlKind.Check),
                new RibbonItem("overlay.bounds", "Bounds", RibbonVerb.Of(DebugVisualization.Aabbs),
                    RibbonItemSize.Small, RibbonControlKind.Check),
                new RibbonItem("overlay.normals", "Normals", RibbonVerb.Of(DebugVisualization.Normals),
                    RibbonItemSize.Small, RibbonControlKind.Check),
                new RibbonItem("overlay.axes", "Node axes", RibbonVerb.Of(DebugVisualization.SceneGraph),
                    RibbonItemSize.Small, RibbonControlKind.Check),
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
    /// The style class a control must wear to realize its declared
    /// <see cref="RibbonControlKind"/>, given its <see cref="RibbonItemSize"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The second half of the weld, and the half that was missing.</b> The
    /// <c>Tag</c> check already refused a control the roster has never heard of
    /// and a roster entry with no control; between those two a page could still
    /// draw ANY control it liked under a valid id. A check row quietly rendered
    /// as a plain button looks finished, posts the right verb and has no lit
    /// state at all - a control that lies about what the engine is doing, which
    /// is exactly the failure the standing overlay chips exist to prevent.
    /// </para>
    /// <para>
    /// Expressed once, here, so the page's runtime validator and
    /// <c>RibbonLayoutTests</c> reading the markup as text cannot disagree
    /// about what a kind looks like.
    /// </para>
    /// </remarks>
    public static string RequiredClass(RibbonItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return item.Kind switch
        {
            RibbonControlKind.Check => "rcheck",
            RibbonControlKind.Radio => "rradio",
            RibbonControlKind.Chip => "chip",
            RibbonControlKind.Field => "field",
            RibbonControlKind.Stepper => "rspin",

            // The split's main half carries the Tag and is an ordinary large
            // button; the caret beside it names no verb, so it carries no Tag
            // and this never sees it.
            RibbonControlKind.Split => "rbig",

            _ => item.Size == RibbonItemSize.Large ? "rbig" : "rsmall",
        };
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
