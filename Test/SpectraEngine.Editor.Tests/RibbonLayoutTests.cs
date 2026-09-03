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
