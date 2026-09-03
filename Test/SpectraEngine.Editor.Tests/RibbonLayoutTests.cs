using Microsoft.Extensions.Logging.Abstractions;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Cameras;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Hosting;
using SpectraEngine.Editor.Shell;
using SpectraEngine.Editor.Shell.Ribbon;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SpectraEngine.Editor.Tests;

/// <summary>
/// The ribbon's roster, and the three findings that retired the previous tab
/// strip.
/// </summary>
/// <remarks>
/// <para>
/// The old Home / Model / View strip was killed by three things: two of its
/// pages carried the same six verbs, Frame was written out three times, and
/// Insert - the one thing a first session needs - sat on the tab nobody
/// opened. The owner reopened the decision, so this file is where each of
/// those stops being a warning in a comment and becomes something that fails a
/// build.
/// </para>
/// <para>
/// <b>None of this needs a window.</b> The roster is data and the collapse
/// machine is a pure function over a value, which is exactly why they were
/// built that way: a duplicate verb hidden inside two click handlers would be
/// invisible to every test that could be written.
/// </para>
/// </remarks>
public sealed class RibbonLayoutTests
{
    // ─── Finding 1: no verb on two tabs ──────────────────

    [Fact]
    public void No_verb_appears_on_more_than_one_tab()
    {
        // THE TEST THIS WHOLE DESIGN EXISTS FOR. A RibbonVerb is a value, so
        // this is a set comparison rather than a review habit; the previous
        // strip's Home and Model pages carried six verbs each way and nothing
        // anywhere said so.
        var seen = new Dictionary<RibbonVerb, string>();
        var offenders = new List<string>();

        foreach (RibbonTab tab in RibbonLayout.Tabs)
        {
            foreach (RibbonItem item in RibbonLayout.ItemsOf(tab))
            {
                if (seen.TryGetValue(item.Verb, out string? firstTab))
                {
                    if (!string.Equals(firstTab, tab.Id, StringComparison.Ordinal))
                        offenders.Add($"{item.Verb.Kind}:{item.Id} is on both '{firstTab}' and '{tab.Id}'");
                }
                else
                {
                    seen[item.Verb] = tab.Id;
                }
            }
        }

        offenders.ShouldBeEmpty(
            "a verb on two tabs is what made the previous tab strip's pages interchangeable, " +
            "which taught users within one session that switching does nothing");
    }

    [Fact]
    public void A_verb_that_must_always_be_reachable_is_on_no_tab()
    {
        // A collapse can hide every page, so anything whose ABSENCE is
        // dangerous cannot be tab-scoped. Undo is the recovery verb for the
        // destructive verbs the Build page carries.
        var onTabs = new HashSet<RibbonVerb>(
            RibbonLayout.Tabs.SelectMany(RibbonLayout.ItemsOf).Select(i => i.Verb));

        foreach (RibbonItem item in RibbonLayout.AlwaysVisible)
        {
            onTabs.ShouldNotContain(
                item.Verb,
                $"'{item.Id}' is always visible on the tab strip and must not also be on a page");
        }
    }

    [Fact]
    public void Undo_and_redo_are_the_always_visible_pair()
    {
        // Play is outside this surface entirely, in the menu row's far corner,
        // so the ribbon's own always-visible list is exactly the history pair.
        // Written down because a later stage adding a third entry should have
        // to justify it here.
        RibbonLayout.AlwaysVisible.Select(i => i.Verb).ShouldBe(
        [
            RibbonVerb.Of(EditorHostCommand.Undo),
            RibbonVerb.Of(EditorHostCommand.Redo),
        ]);
    }

    [Fact]
    public void Every_item_id_is_unique_across_the_whole_roster()
    {
        // Ids are what the markup carries and what the click handler resolves,
        // so two items sharing one would make a control post the other's verb.
        List<string> ids =
        [
            .. RibbonLayout.AlwaysVisible.Select(i => i.Id),
            .. RibbonLayout.Tabs.SelectMany(RibbonLayout.ItemsOf).Select(i => i.Id),
        ];

        ids.GroupBy(id => id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ShouldBeEmpty();
    }

    // ─── Finding 2: Insert is not buried ─────────────────

    [Fact]
    public void Insert_is_the_first_group_of_the_tab_a_session_opens_on()
    {
        RibbonTab? start = RibbonLayout.FindTab(RibbonLayout.DefaultTabId);
        start.ShouldNotBeNull();

        start.Groups[0].Caption.ShouldBe(
            "Insert",
            "the previous strip put the one thing a first session needs on the tab nobody opened");

        // And every insert verb the shell offers on the ribbon is in it,
        // rather than scattered so that only some of them are on the first
        // page somebody sees.
        foreach (RibbonTab tab in RibbonLayout.Tabs)
        {
            foreach (RibbonItem item in RibbonLayout.ItemsOf(tab))
            {
                if (item.Verb.Kind != RibbonVerbKind.Insert)
                    continue;

                tab.Id.ShouldBe(RibbonLayout.DefaultTabId, $"'{item.Id}' is an insert");
            }
        }
    }

    [Fact]
    public void A_session_opens_on_the_default_tab_however_the_last_one_ended()
    {
        // The pin persists; the page does not. A shell that reopened on View
        // would start every session with Insert hidden behind a click, which is
        // finding 2 arriving by another route.
        RibbonSurface.Create(expanded: true).ActiveTabId.ShouldBe(RibbonLayout.DefaultTabId);
        RibbonSurface.Create(expanded: false).ActiveTabId.ShouldBe(RibbonLayout.DefaultTabId);
    }

    // ─── Finding 3: switching does something ─────────────

    [Fact]
    public void Every_tab_carries_enough_to_be_worth_switching_to()
    {
        // THE FLOOR IS EIGHT CONTROLS IN THREE GROUPS, and the number is
        // argued rather than picked: fewer than three groups is a cluster, and
        // a cluster belongs beside another cluster on one row rather than
        // behind a click - which is precisely what the single command bar this
        // replaced already was. A page that cannot clear this is the retired
        // strip's thin third page.
        foreach (RibbonTab tab in RibbonLayout.Tabs)
        {
            IReadOnlyList<RibbonItem> items = RibbonLayout.ItemsOf(tab);

            items.Count.ShouldBeGreaterThanOrEqualTo(8, $"'{tab.Id}' is too thin to be a page");
            tab.Groups.Count.ShouldBeGreaterThanOrEqualTo(3, $"'{tab.Id}' is one cluster, not a page");

            foreach (RibbonGroup group in tab.Groups)
                group.Items.ShouldNotBeEmpty($"'{tab.Id}' has an empty group named {group.Caption}");
        }
    }

    [Fact]
    public void Two_tabs_is_the_count_and_a_third_has_to_argue_for_itself()
    {
        // Deliberately pinned. The verbs this shell has fill two pages
        // properly; a third would have to be padded out of the document verbs
        // the File menu already owns, and a thin page is the retired strip
        // again. A later stage adding one changes this line and states why.
        RibbonLayout.Tabs.Count.ShouldBe(2);
        RibbonLayout.Tabs.Select(t => t.Id).ShouldBe([RibbonLayout.DefaultTabId, RibbonLayout.ViewTabId]);
    }

    [Fact]
    public void The_two_pages_divide_on_a_real_axis()
    {
        // Build changes the level; View changes only how you look at it. Said
        // as a property: nothing on the View page is an insert or a structural
        // edit, and nothing on the Build page is a camera verb or an overlay.
        RibbonTab view = RibbonLayout.FindTab(RibbonLayout.ViewTabId)!;
        RibbonTab build = RibbonLayout.FindTab(RibbonLayout.DefaultTabId)!;

        foreach (RibbonItem item in RibbonLayout.ItemsOf(view))
        {
            item.Verb.Kind.ShouldNotBe(RibbonVerbKind.Insert, item.Id);
            if (item.Verb.Kind == RibbonVerbKind.Host)
            {
                item.Verb.Host.ShouldBeOneOf(
                    EditorHostCommand.GridAuto, EditorHostCommand.GridOn, EditorHostCommand.GridOff);
            }
        }

        foreach (RibbonItem item in RibbonLayout.ItemsOf(build))
        {
            item.Verb.Kind.ShouldNotBe(RibbonVerbKind.Camera, item.Id);
            item.Verb.Kind.ShouldNotBe(RibbonVerbKind.Debug, item.Id);
        }
    }

    // ─── Every control resolves to an existing verb ──────

    [Fact]
    public void Every_ribbon_control_names_a_verb_the_editor_already_has()
    {
        foreach (RibbonItem item in AllItems())
        {
            item.Id.ShouldNotBeNullOrWhiteSpace();
            item.Label.ShouldNotBeNullOrWhiteSpace();
            item.Verb.Kind.ShouldNotBe(RibbonVerbKind.None, item.Id);

            switch (item.Verb.Kind)
            {
                case RibbonVerbKind.Host:
                    Enum.IsDefined(item.Verb.Host).ShouldBeTrue(item.Id);
                    break;

                case RibbonVerbKind.Gizmo:
                    Enum.IsDefined(item.Verb.Gizmo).ShouldBeTrue(item.Id);
                    break;

                case RibbonVerbKind.Camera:
                    Enum.IsDefined(item.Verb.Camera).ShouldBeTrue(item.Id);
                    break;

                case RibbonVerbKind.Insert:
                    Enum.IsDefined(item.Verb.Insert).ShouldBeTrue(item.Id);
                    break;

                case RibbonVerbKind.Debug:
                    // A single declared flag, never a combination: the button
                    // toggles exactly one overlay and its lit state reads one.
                    Enum.IsDefined(item.Verb.Debug).ShouldBeTrue(item.Id);
                    item.Verb.Debug.ShouldNotBe(DebugVisualization.None, item.Id);
                    break;

                case RibbonVerbKind.Toggle:
                    (GizmoCommand off, GizmoCommand on) = RibbonLayout.CommandsFor(item.Verb.Toggle);
                    off.ShouldNotBe(on, item.Id);
                    Enum.IsDefined(off).ShouldBeTrue(item.Id);
                    Enum.IsDefined(on).ShouldBeTrue(item.Id);
                    break;
            }
        }
    }

    [Fact]
    public void There_is_exactly_one_snap_increment_field()
    {
        // The one control whose verb carries a NUMBER rather than naming a
        // state. Two of them would be the three boxes labelled mv / rot / sz
        // this shell already retired once.
        AllItems().Count(i => i.Verb.Kind == RibbonVerbKind.SnapIncrement).ShouldBe(1);
    }

    [Fact]
    public void There_is_exactly_one_split_button_and_it_places_an_entity()
    {
        // A split button is the one control here with two hit regions, and the
        // arrangement only stays legible while there is one of them: a row of
        // them turns a caret from "this one is different" into decoration.
        List<RibbonItem> splits = AllItems()
            .Where(i => i.Kind == RibbonControlKind.Split)
            .ToList();

        splits.Count.ShouldBe(1);
        splits[0].Verb.Kind.ShouldBe(RibbonVerbKind.InsertEntity);
        splits[0].Size.ShouldBe(RibbonItemSize.Large);
    }

    [Fact]
    public void The_entity_class_list_is_not_in_the_roster()
    {
        // THE ONE PLACE THE ROSTER DELIBERATELY DOES NOT NAME WHAT A CONTROL
        // DOES. Every other verb here names something this build knows at
        // compile time; an entity class comes from the project's own .sentdef,
        // so the split names the CONTROL and its caret's entries are built at
        // open time from the live session's parsed catalogue.
        //
        // That is a real weakening of "the roster is the single source of
        // truth", so it is written down as a test rather than left in a
        // comment: exactly one item may carry a verb with no payload, and no
        // item may name a class.
        AllItems()
            .Count(i => i.Verb.Kind == RibbonVerbKind.InsertEntity)
            .ShouldBe(1, "the class is session state, so only the control is in the roster");

        // The built-in classes, which are what a roster entry would most
        // plausibly have been written as.
        foreach (string className in new[] { "logic_relay", "logic_timer", "math_counter" })
        {
            AllItems()
                .Any(i => i.Id.Contains(className, StringComparison.Ordinal)
                       || i.Label.Contains(className, StringComparison.Ordinal))
                .ShouldBeFalse($"'{className}' is a project's data, not this build's roster");
        }
    }

    [Fact]
    public void The_splits_caret_carries_no_tag_of_its_own()
    {
        // It opens a list rather than posting a verb, so it is not a roster
        // entry - and the page's validator only inspects controls that have a
        // Tag, which is what lets the split be two Buttons under one id. A Tag
        // on the caret would make the page draw an id twice and refuse the
        // window, which is the right failure and worth pinning the shape of.
        string markup = File.ReadAllText(Path.Combine(RibbonFolder(), "RibbonBuildTab.axaml"));

        int splitStart = markup.IndexOf("Classes=\"rsplit\"", StringComparison.Ordinal);
        splitStart.ShouldBeGreaterThan(-1, "the split button should be drawn");

        int splitEnd = markup.IndexOf("</Border>", splitStart, StringComparison.Ordinal);
        string split = markup[splitStart..splitEnd];

        Regex.Matches(split, "Tag=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ShouldBe(["insert.entity"], "only the main half names a verb");
    }

    [Fact]
    public void Finding_an_item_by_its_id_is_how_a_click_resolves()
    {
        // The click handler's only input is the control's Tag, so an id the
        // roster does not know must resolve to nothing rather than to
        // something.
        RibbonLayout.FindItem("insert.block")!.Verb.ShouldBe(RibbonVerb.Of(InsertKind.WorldBrush));
        RibbonLayout.FindItem("history.undo")!.Verb.ShouldBe(RibbonVerb.Of(EditorHostCommand.Undo));
        RibbonLayout.FindItem("camera.frameall")!.Verb.ShouldBe(RibbonVerb.Of(EditorCameraCommand.FrameAll));
        RibbonLayout.FindItem("nothing.at.all").ShouldBeNull();
        RibbonLayout.FindItem(null).ShouldBeNull();
    }

    // ─── The roster against what is actually drawn ───────

    // Which markup file draws which page. Mirrors the window's own page table;
    // a tab with no entry here fails the test below rather than going
    // unchecked.
    private static readonly (string TabId, string File)[] PageFiles =
    [
        (RibbonLayout.DefaultTabId, "RibbonBuildTab.axaml"),
        (RibbonLayout.ViewTabId, "RibbonViewTab.axaml"),
    ];

    [Fact]
    public void Every_page_draws_exactly_the_controls_its_roster_promises()
    {
        // The runtime half of this refuses the window at construction; this is
        // the CI half, which reads the same fact out of the sources without an
        // Avalonia application. Both directions matter and they fail
        // differently: a tagged control the roster has never heard of is a
        // button that does nothing, and a roster entry with no control is a
        // verb the tests above believe is on screen and is not.
        RibbonLayout.Tabs.Select(t => t.Id)
            .ShouldBe(PageFiles.Select(p => p.TabId), "every tab needs a markup file listed here");

        foreach ((string tabId, string file) in PageFiles)
        {
            RibbonTab tab = RibbonLayout.FindTab(tabId)!;
            List<string> tags = TagsIn(Path.Combine(RibbonFolder(), file));

            tags.GroupBy(t => t, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ShouldBeEmpty($"{file} draws an id more than once");

            tags.Order(StringComparer.Ordinal).ShouldBe(
                RibbonLayout.ItemsOf(tab).Select(i => i.Id).Order(StringComparer.Ordinal),
                $"{file} and the '{tabId}' roster must name the same controls");
        }
    }

    [Fact]
    public void The_always_visible_pair_is_drawn_on_the_strip_itself()
    {
        // Not on a page, and therefore not in a page file: the strip lives in
        // the window, which is what makes it survive a collapse.
        string window = File.ReadAllText(Path.Combine(SourceRoot(), "SpectraEngine.Editor", "MainWindow.axaml"));
        List<string> pageTags =
        [
            .. PageFiles.SelectMany(p => TagsIn(Path.Combine(RibbonFolder(), p.File))),
        ];

        foreach (RibbonItem item in RibbonLayout.AlwaysVisible)
        {
            window.ShouldContain($"Tag=\"{item.Id}\"", customMessage: $"{item.Id} must be on the tab strip");
            pageTags.ShouldNotContain(item.Id, $"{item.Id} must not also be drawn on a page");
        }
    }

    // ─── The size hierarchy, and the width it costs ──────

    /// <summary>
    /// The window's own <c>MinWidth</c>. A page has to fit inside it, because
    /// nothing here scrolls or collapses a group.
    /// </summary>
    private const double WindowMinimum = 1180.0;

    /// <summary>What each control kind measures, from the theme's own numbers.</summary>
    private static double WidthOf(RibbonItem item) => item.Kind switch
    {
        // Button.chip.compact plus its two words; the widest is "Handles Studio".
        RibbonControlKind.Chip => 106.0,

        // TextBox.field.num at 58 plus a unit label and the stepper column.
        RibbonControlKind.Field => 100.0,

        // Both steppers stack inside the field's own row.
        RibbonControlKind.Stepper => 0.0,

        _ => item.Size == RibbonItemSize.Large ? 64.0 : 96.0,
    };

    [Fact]
    public void A_page_fits_the_window_the_shell_refuses_to_go_below()
    {
        // THE FLOOR THE SIZE HIERARCHY NEEDED. A large button is three times a
        // small row's share of the width, so a group that grows one is a group
        // that can push the last one off the end - and the way that presents is
        // a control nobody can find, on a window nobody resized, on somebody
        // else's monitor. Arithmetic here rather than a screenshot there.
        //
        // Small rows stack three to a column, so a group's small items cost
        // ceil(n / 3) columns rather than n rows. Deliberately generous per
        // item: the point is a bound that fails before the layout does, not a
        // measurement of it.
        foreach (RibbonTab tab in RibbonLayout.Tabs)
        {
            double width = 0;

            foreach (RibbonGroup group in tab.Groups)
            {
                double large = group.Items
                    .Where(i => i.Size == RibbonItemSize.Large)
                    .Sum(WidthOf);

                // Everything that is not a large button lives in a column, and
                // a column holds three rows.
                List<RibbonItem> small = group.Items
                    .Where(i => i.Size != RibbonItemSize.Large && WidthOf(i) > 0)
                    .ToList();

                double columns = 0;
                for (int i = 0; i < small.Count; i += 3)
                    columns += small.Skip(i).Take(3).Max(WidthOf);

                width += large + columns + 10; // StackPanel.ribbongroup's own margin
            }

            width += (tab.Groups.Count - 1) * 5; // the rules between them

            width.ShouldBeLessThan(
                WindowMinimum,
                $"the '{tab.Id}' page must fit the window's MinWidth of {WindowMinimum}");
        }
    }

    [Fact]
    public void Every_page_leads_with_a_large_control()
    {
        // A page whose first group is all small rows has no headline, which is
        // the state the whole surface was in before the hierarchy existed: the
        // eye lands somewhere arbitrary and the page reads as a list.
        foreach (RibbonTab tab in RibbonLayout.Tabs)
        {
            tab.Groups[0].Items
                .Any(i => i.Size == RibbonItemSize.Large)
                .ShouldBeTrue($"the first group of '{tab.Id}' should lead with a large control");
        }
    }

    [Fact]
    public void A_large_label_cannot_wrap_to_a_third_line()
    {
        // Button.rbig is 64 wide and wraps to two lines of 13 inside its 66,
        // and MaxLines is 2 - so a third line is not clipped visibly, it is
        // silently dropped.
        //
        // TEN IS MEASURED, NOT CHOSEN. The cap was twelve, and twelve is what
        // let "Everything" ship at a 58px button where it broke mid-word to
        // "Everythin / g" - a single word longer than the line has no word
        // boundary to wrap at, so TextWrapping cuts it wherever it runs out.
        // The button is 64 now and holds that word with two pixels to spare,
        // which makes ten the honest cap rather than a round number.
        var offenders = new List<string>();

        foreach (RibbonItem item in AllItems().Where(i => i.Size == RibbonItemSize.Large))
        {
            if (item.Label.Length > 10)
                offenders.Add($"{item.Id}: '{item.Label}' is {item.Label.Length} characters");
        }

        offenders.ShouldBeEmpty("a large button's label must fit two lines at 64px");
    }

    [Fact]
    public void Every_control_wears_the_class_its_declared_kind_requires()
    {
        // THE SECOND HALF OF THE WELD. The Tag check above refuses a control
        // the roster has never heard of and a roster entry with no control;
        // between those two a page could still draw ANY control it liked under
        // a valid id. A check row rendered as a plain button looks finished,
        // posts the right verb and has no lit state at all - which is a control
        // that lies about what the engine is doing.
        //
        // The runtime half refuses the window at construction. This is the CI
        // half, reading the same fact out of the sources.
        var offenders = new List<string>();

        foreach ((string tabId, string file) in PageFiles)
        {
            RibbonTab tab = RibbonLayout.FindTab(tabId)!;
            string markup = File.ReadAllText(Path.Combine(RibbonFolder(), file));

            foreach (RibbonItem item in RibbonLayout.ItemsOf(tab))
            {
                string required = RibbonLayout.RequiredClass(item);
                if (!ClassesOn(markup, item.Id).Contains(required, StringComparer.Ordinal))
                    offenders.Add($"{file}: {item.Id} is a {item.Kind} and must wear '{required}'");
            }
        }

        offenders.ShouldBeEmpty("a declared kind must be the control that is actually drawn");
    }

    [Fact]
    public void A_lit_control_is_bound_to_something_that_lights_it()
    {
        // A Toggle, a Check and a Radio all exist to SHOW a state, and every
        // one of them in this shell is a Button wearing Classes.active rather
        // than a real ToggleButton - the deliberate refusal recorded in
        // Controls.axaml, because a two-way toggle bound to engine state
        // flickers when the snapshot corrects it. The cost of that choice is
        // that forgetting the binding is silent: the control works, posts its
        // verb, and never lights.
        var offenders = new List<string>();

        foreach ((string tabId, string file) in PageFiles)
        {
            RibbonTab tab = RibbonLayout.FindTab(tabId)!;
            string markup = File.ReadAllText(Path.Combine(RibbonFolder(), file));

            foreach (RibbonItem item in RibbonLayout.ItemsOf(tab))
            {
                if (item.Kind is not (RibbonControlKind.Toggle or RibbonControlKind.Check
                    or RibbonControlKind.Radio))
                {
                    continue;
                }

                if (!ElementFor(markup, item.Id).Contains("Classes.active=", StringComparison.Ordinal))
                    offenders.Add($"{file}: {item.Id} is a {item.Kind} with nothing to light it");
            }
        }

        offenders.ShouldBeEmpty("a Toggle, Check or Radio must bind Classes.active");
    }

    /// <summary>
    /// The whole opening tag of the element carrying <paramref name="id"/>, as
    /// text.
    /// </summary>
    /// <remarks>
    /// Crude, and deliberately so: parsing XAML properly would pull in the
    /// framework this project does not reference, and every claim these tests
    /// make is about text a person reads. A tag is taken from the element's
    /// <c>&lt;</c> to its first <c>&gt;</c>, which is enough because every
    /// ribbon control opens on one element and closes later.
    /// </remarks>
    private static string ElementFor(string markup, string id)
    {
        int tag = markup.IndexOf($"Tag=\"{id}\"", StringComparison.Ordinal);
        if (tag < 0) return string.Empty;

        int open = markup.LastIndexOf('<', tag);
        int close = markup.IndexOf('>', tag);
        if (open < 0 || close < 0) return string.Empty;

        return markup[open..close];
    }

    /// <summary>The class list on the element carrying <paramref name="id"/>.</summary>
    private static IReadOnlyList<string> ClassesOn(string markup, string id)
    {
        Match m = Regex.Match(ElementFor(markup, id), "Classes=\"([^\"]*)\"");
        return m.Success
            ? m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : [];
    }

    private static IEnumerable<RibbonItem> AllItems() =>
        RibbonLayout.AlwaysVisible.Concat(RibbonLayout.Tabs.SelectMany(RibbonLayout.ItemsOf));

    private static List<string> TagsIn(string file)
    {
        File.Exists(file).ShouldBeTrue($"expected the ribbon page markup at {file}");

        return Regex.Matches(File.ReadAllText(file), "Tag=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToList();
    }

    private static string RibbonFolder() =>
        Path.Combine(SourceRoot(), "SpectraEngine.Editor", "Shell", "Ribbon");

    // The same walk ContentRoot uses: the nearest ancestor holding a solution
    // file is the repo root. These tests only ever run out of the repo.
    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("*.slnx").Length > 0 || dir.GetFiles("*.sln").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("no solution file above the test binary");
    }
}

/// <summary>
/// The collapse state machine, and the one thing about it that is persisted.
/// </summary>
/// <remarks>
/// Four transitions over a value, because every interesting case is a
/// COMBINATION: clicking the active tab means "switch page" while expanded and
/// "put that page away" while collapsed, expanding must not leave a flyout
/// behind it, and a command invoked out of a flyout closes it. None of that is
/// reachable from a test if it lives as three booleans inside a window.
/// </remarks>
public sealed class RibbonSurfaceTests
{
    [Fact]
    public void Expanded_shows_the_active_page_inline()
    {
        RibbonSurfaceState state = RibbonSurface.Create(expanded: true);

        RibbonSurface.HostFor(state).ShouldBe(RibbonBodyHost.Inline);
        state.FlyoutOpen.ShouldBeFalse();
    }

    [Fact]
    public void Collapsed_shows_nothing_but_the_strip()
    {
        RibbonSurfaceState state = RibbonSurface.Create(expanded: false);

        RibbonSurface.HostFor(state).ShouldBe(RibbonBodyHost.None);
    }

    [Fact]
    public void Selecting_a_tab_while_expanded_is_navigation()
    {
        RibbonSurfaceState state = RibbonSurface.Create(expanded: true);
        state = RibbonSurface.SelectTab(state, RibbonLayout.ViewTabId);

        state.ActiveTabId.ShouldBe(RibbonLayout.ViewTabId);
        state.FlyoutOpen.ShouldBeFalse("an expanded ribbon has nowhere to fly a page out to");
        RibbonSurface.HostFor(state).ShouldBe(RibbonBodyHost.Inline);
    }

    [Fact]
    public void Selecting_a_tab_while_collapsed_flies_that_page_out()
    {
        RibbonSurfaceState state = RibbonSurface.Create(expanded: false);
        state = RibbonSurface.SelectTab(state, RibbonLayout.ViewTabId);

        state.ActiveTabId.ShouldBe(RibbonLayout.ViewTabId);
        RibbonSurface.HostFor(state).ShouldBe(RibbonBodyHost.Flyout);
        state.Expanded.ShouldBeFalse("flying a page out is not the same as pinning it open");
    }

    [Fact]
    public void Clicking_the_tab_that_is_already_flown_out_puts_it_away()
    {
        // The only way a keyboard-free user closes a flyout without invoking
        // something.
        RibbonSurfaceState state = RibbonSurface.Create(expanded: false);
        state = RibbonSurface.SelectTab(state, RibbonLayout.DefaultTabId);
        state = RibbonSurface.SelectTab(state, RibbonLayout.DefaultTabId);

        RibbonSurface.HostFor(state).ShouldBe(RibbonBodyHost.None);
    }

    [Fact]
    public void Clicking_the_other_tab_while_one_is_flown_out_switches_pages()
    {
        RibbonSurfaceState state = RibbonSurface.Create(expanded: false);
        state = RibbonSurface.SelectTab(state, RibbonLayout.DefaultTabId);
        state = RibbonSurface.SelectTab(state, RibbonLayout.ViewTabId);

        state.ActiveTabId.ShouldBe(RibbonLayout.ViewTabId);
        RibbonSurface.HostFor(state).ShouldBe(RibbonBodyHost.Flyout);
    }

    [Fact]
    public void An_unknown_tab_id_changes_nothing()
    {
        // A stale control must not leave the strip pointing at a page that
        // does not exist, which would put the body host on a null page.
        RibbonSurfaceState state = RibbonSurface.Create(expanded: true);
        RibbonSurface.SelectTab(state, "retired").ShouldBe(state);
        RibbonSurface.SelectTab(state, null).ShouldBe(state);
    }

    [Fact]
    public void Expanding_closes_a_flyout_rather_than_leaving_two_copies_of_one_page()
    {
        RibbonSurfaceState state = RibbonSurface.Create(expanded: false);
        state = RibbonSurface.SelectTab(state, RibbonLayout.ViewTabId);
        state = RibbonSurface.SetExpanded(state, expanded: true);

        state.FlyoutOpen.ShouldBeFalse();
        RibbonSurface.HostFor(state).ShouldBe(RibbonBodyHost.Inline);
        state.ActiveTabId.ShouldBe(RibbonLayout.ViewTabId, "expanding keeps the page that was showing");
    }

    [Fact]
    public void Setting_the_pin_is_idempotent_because_it_is_a_set_verb()
    {
        // The same rule every displayed state in this shell follows: a toggle
        // sent against a stale view flips the wrong way exactly when the user
        // clicks fastest.
        RibbonSurfaceState state = RibbonSurface.Create(expanded: true);
        RibbonSurface.SetExpanded(RibbonSurface.SetExpanded(state, true), true).ShouldBe(state);
    }

    [Fact]
    public void Invoking_a_command_closes_a_flown_out_page_and_leaves_a_pinned_one()
    {
        RibbonSurfaceState collapsed = RibbonSurface.SelectTab(
            RibbonSurface.Create(expanded: false), RibbonLayout.DefaultTabId);
        RibbonSurface.HostFor(RibbonSurface.Invoke(collapsed)).ShouldBe(RibbonBodyHost.None);

        RibbonSurfaceState pinned = RibbonSurface.Create(expanded: true);
        RibbonSurface.Invoke(pinned).ShouldBe(pinned, "a pinned ribbon does not put itself away");
    }

    [Fact]
    public void Dismissing_closes_a_flyout_and_touches_nothing_else()
    {
        RibbonSurfaceState state = RibbonSurface.SelectTab(
            RibbonSurface.Create(expanded: false), RibbonLayout.ViewTabId);

        RibbonSurfaceState dismissed = RibbonSurface.Dismiss(state);
        dismissed.FlyoutOpen.ShouldBeFalse();
        dismissed.ActiveTabId.ShouldBe(RibbonLayout.ViewTabId);
        dismissed.Expanded.ShouldBeFalse();
    }

    [Fact]
    public void The_pin_survives_a_restart_and_the_active_page_deliberately_does_not()
    {
        string path = Path.Combine(
            Path.GetTempPath(), "spectra-tests", Path.GetRandomFileName(), "editor.json");

        var settings = new EditorSettings();
        settings.RibbonExpanded.ShouldBeTrue("the ribbon ships open, showing what it can do");

        settings.SetRibbonExpanded(false);
        settings.Save(path, NullLogger.Instance);

        EditorSettings reloaded = EditorSettings.Load(path, NullLogger.Instance);
        reloaded.RibbonExpanded.ShouldBeFalse();

        // And a session built from it still opens on the page carrying Insert.
        RibbonSurfaceState state = RibbonSurface.Create(reloaded.RibbonExpanded);
        state.Expanded.ShouldBeFalse();
        state.ActiveTabId.ShouldBe(RibbonLayout.DefaultTabId);
    }

    [Fact]
    public void A_settings_file_that_never_heard_of_the_ribbon_opens_it()
    {
        // Open is the conservative fallback: a collapsed ribbon read out of a
        // file this build cannot make sense of would hide the command surface
        // with no explanation on screen.
        string dir = Path.Combine(Path.GetTempPath(), "spectra-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "editor.json");
        File.WriteAllText(path, """{"recentProjects":[]}""");

        EditorSettings.Load(path, NullLogger.Instance).RibbonExpanded.ShouldBeTrue();
    }
}
