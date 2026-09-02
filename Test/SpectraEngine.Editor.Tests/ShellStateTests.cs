using SpectraEngine.Core.Hosting;
using SpectraEngine.Editor.Shell;
using System.Collections.Generic;
using System.Globalization;

namespace SpectraEngine.Editor.Tests;

/// <summary>
/// The shell's two new pieces of local state: the bounded optimistic value
/// every command-bar control now holds, and the output log the status line
/// became a view of.
/// </summary>
/// <remarks>
/// Headless, with no window and no engine, because both are plain models. That
/// is the point of them being models: the behaviour that matters - a control
/// lighting on the click and the engine still winning - is arithmetic over an
/// echo, and arithmetic can be pinned.
/// </remarks>
public sealed class OptimisticValueTests
{
    [Fact]
    public void A_request_is_displayed_at_once()
    {
        var value = new OptimisticValue<string>("move");

        Assert.True(value.Request("rotate"));
        Assert.Equal("rotate", value.Value);
        Assert.True(value.HasPending);
    }

    [Fact]
    public void An_agreeing_echo_hands_authority_back_to_the_engine()
    {
        var value = new OptimisticValue<string>("move");
        value.Request("rotate");

        value.Apply("rotate");
        Assert.False(value.HasPending);

        // With nothing pending, the engine is authoritative immediately: this
        // is the case where something OTHER than the user changed the value -
        // a key press, another panel, the engine itself.
        value.Apply("resize");
        Assert.Equal("resize", value.Value);
    }

    [Fact]
    public void A_stale_echo_is_ignored_while_the_request_is_in_flight()
    {
        var value = new OptimisticValue<string>("move") { HoldTicks = 6 };
        value.Request("rotate");

        // Five snapshots still describing frames from before the click. The
        // displayed value must not flicker back and forth across them.
        for (int i = 0; i < 5; i++)
        {
            Assert.False(value.Apply("move"));
            Assert.Equal("rotate", value.Value);
        }
    }

    [Fact]
    public void The_engine_wins_once_the_hold_expires()
    {
        var value = new OptimisticValue<string>("move") { HoldTicks = 3 };
        value.Request("rotate");

        value.Apply("move");
        value.Apply("move");
        Assert.Equal("rotate", value.Value);

        // Three disagreeing snapshots is not lag any more, it is a refusal -
        // play mode owns the scene, or the editor is mid-gesture - and it has
        // to become visible or the user clicks again.
        Assert.True(value.Apply("move"));
        Assert.Equal("move", value.Value);
        Assert.False(value.HasPending);
    }

    [Fact]
    public void A_second_request_replaces_the_first_rather_than_queueing()
    {
        var value = new OptimisticValue<string>("move") { HoldTicks = 3 };
        value.Request("rotate");
        value.Apply("move");

        value.Request("resize");
        Assert.Equal("resize", value.Value);

        // The hold restarted, so the two ticks already spent against the
        // previous request do not count against this one.
        value.Apply("move");
        value.Apply("move");
        Assert.Equal("resize", value.Value);
    }

    [Fact]
    public void Undo_and_redo_depth_move_together()
    {
        var value = new OptimisticValue<(int Undo, int Redo)>((4, 1));

        value.Request((3, 2));
        Assert.Equal((3, 2), value.Value);

        // Predicting only half of it would light the redo button against a
        // depth that had not moved.
        value.Apply((3, 2));
        Assert.False(value.HasPending);
    }

    [Fact]
    public void A_reset_drops_the_pending_request()
    {
        var value = new OptimisticValue<string>("move");
        value.Request("rotate");

        // A session closing: the engine those requests were aimed at is gone,
        // and holding them would make the next session ignore its own first
        // snapshots.
        value.Reset("move");

        Assert.False(value.HasPending);
        Assert.Equal("move", value.Value);
        Assert.True(value.Apply("resize"));
    }
}

public sealed class OutputLogTests
{
    [Fact]
    public void Errors_and_warnings_are_counted_separately()
    {
        var log = new OutputLog();

        log.Append(OutputSeverity.Info, "opened");
        log.Append(OutputSeverity.Warning, "a texture is missing");
        log.Append(OutputSeverity.Error, "the save failed");
        log.Append(OutputSeverity.Error, "and again");

        Assert.Equal(2, log.ErrorCount);
        Assert.Equal(1, log.WarningCount);
        Assert.Equal("2 errors, 1 warning", log.ProblemSummary);
    }

    [Fact]
    public void An_empty_line_is_not_recorded()
    {
        var log = new OutputLog();
        log.Append(OutputSeverity.Info, "   ");
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void The_oldest_lines_go_first_and_their_counts_go_with_them()
    {
        var log = new OutputLog();

        // One error, then a full capacity of ordinary lines pushing it out. The
        // count has to follow the entry, or a log that has scrolled past an old
        // failure claims problems it can no longer show.
        log.Append(OutputSeverity.Error, "the first failure");
        for (int i = 0; i < OutputLog.Capacity; i++)
            log.Append(OutputSeverity.Info, $"line {i}");

        Assert.Equal(OutputLog.Capacity, log.Entries.Count);
        Assert.Equal(0, log.ErrorCount);
        Assert.DoesNotContain(log.Entries, e => e.Text == "the first failure");
    }
}

public sealed class ConsoleCommandsTests
{
    private static ConsoleCommands Build(
        List<string> log,
        bool sessionOpen = true)
    {
        return new ConsoleCommands(
            postHost: c => Record(log, $"host:{c}", sessionOpen),
            postGizmo: c => Record(log, $"gizmo:{c}", sessionOpen),
            postCamera: c => Record(log, $"camera:{c}", sessionOpen),
            insert: k => Record(log, $"insert:{k}", sessionOpen),
            // Formatted INVARIANTLY here, deliberately: this machine's culture
            // writes 0,25, and a test that recorded the culture's rendering
            // would pass on an English machine and fail here while the code
            // under test was correct either way. The parse being invariant is
            // the thing being pinned.
            setSnap: (tool, value) => Record(
                log, $"snap:{tool}={value.ToString(CultureInfo.InvariantCulture)}", sessionOpen),
            setPipeline: n => Record(log, $"pipeline:{n}", sessionOpen),
            setPlaying: p => log.Add($"play:{p}"));

        static bool Record(List<string> log, string entry, bool ok)
        {
            if (ok)
                log.Add(entry);

            return ok;
        }
    }

    [Fact]
    public void A_verb_resolves_to_the_command_a_button_would_send()
    {
        List<string> log = [];
        ConsoleResult result = Build(log).Execute("duplicate");

        Assert.Equal(OutputSeverity.Info, result.Severity);
        Assert.Equal(["host:Duplicate"], log);
    }

    [Fact]
    public void An_unknown_verb_is_an_error_that_names_itself()
    {
        List<string> log = [];
        ConsoleResult result = Build(log).Execute("frobnicate");

        Assert.Equal(OutputSeverity.Error, result.Severity);
        Assert.Contains("frobnicate", result.Reply);
        Assert.Empty(log);
    }

    [Fact]
    public void Snap_takes_an_invariant_number_and_refuses_anything_else()
    {
        List<string> log = [];
        ConsoleCommands console = Build(log);

        Assert.Equal(OutputSeverity.Info, console.Execute("grid 0.25").Severity);
        Assert.Equal(["snap:Translate=0.25"], log);

        // Zero, negative and non-numeric are all refused rather than clamped:
        // a grid of zero is not a smaller grid, it is a division by zero
        // somewhere downstream.
        Assert.Equal(OutputSeverity.Error, console.Execute("grid 0").Severity);
        Assert.Equal(OutputSeverity.Error, console.Execute("grid -2").Severity);
        Assert.Equal(OutputSeverity.Error, console.Execute("grid wide").Severity);
        Assert.Single(log);
    }

    [Fact]
    public void On_and_off_are_set_verbs_and_a_bare_verb_means_on()
    {
        List<string> log = [];
        ConsoleCommands console = Build(log);

        console.Execute("snap");
        console.Execute("snap off");
        console.Execute("snap on");

        // Never a toggle. The console has to agree with the button, and the
        // button posts a SET so a stale echo cannot flip it the wrong way.
        Assert.Equal(["gizmo:EnableSnap", "gizmo:DisableSnap", "gizmo:EnableSnap"], log);
    }

    [Fact]
    public void Nothing_open_is_reported_rather_than_appearing_to_work()
    {
        List<string> log = [];
        ConsoleResult result = Build(log, sessionOpen: false).Execute("block");

        Assert.Equal(OutputSeverity.Error, result.Severity);
        Assert.Empty(log);
    }

    [Fact]
    public void Help_names_every_command_the_table_offers()
    {
        List<string> log = [];
        string help = Build(log).Execute("help").Reply;

        foreach (string name in ConsoleCommands.Names)
            Assert.Contains(name, help);
    }

    [Fact]
    public void Clear_asks_for_the_log_to_be_emptied_rather_than_printing()
    {
        List<string> log = [];
        Assert.Equal(ConsoleCommands.ClearMarker, Build(log).Execute("clear").Reply);
    }
}

/// <summary>
/// The graphics detector's standing slot. Once the viewport is a texture handed
/// to something else there is no offscreen probe behind it, and this counter is
/// the only continuous report of a missing barrier or a pipeline state bound to
/// a format it was not compiled for - both of which draw a picture, so the
/// viewport itself can never show them.
/// </summary>
/// <remarks>
/// Headless: <c>ApplySnapshot</c> is a copy from an immutable value into fields,
/// which is exactly what makes binding to the model safe in the first place.
/// </remarks>
public sealed class DebugLayerStatusTests
{
    private static ShellModel Apply(int errors, bool active)
    {
        var model = new ShellModel();
        model.ApplySnapshot(new FrameSnapshot
        {
            DebugLayerErrorCount = errors,
            DebugLayerActive = active,
        });
        return model;
    }

    [Fact]
    public void A_clean_session_shows_nothing()
    {
        ShellModel model = Apply(errors: 0, active: true);

        Assert.False(model.HasDebugLayerErrors);
        Assert.True(model.DebugLayerClean);
    }

    [Fact]
    public void A_reported_error_takes_the_standing_slot_and_names_its_count()
    {
        ShellModel model = Apply(errors: 3, active: true);

        Assert.True(model.HasDebugLayerErrors);
        Assert.Equal("3 graphics errors", model.DebugLayerLabel);
        Assert.Contains("3", model.DebugLayerTip);

        // The slot is standing state, not a message: nothing the shell writes
        // to the message line may displace it, and nothing it says may land
        // there either.
        Assert.False(model.HasMessage);
    }

    [Fact]
    public void One_error_is_not_pluralised()
    {
        Assert.Equal("1 graphics error", Apply(errors: 1, active: true).DebugLayerLabel);
    }

    [Fact]
    public void A_layer_that_is_not_running_does_not_read_as_clean()
    {
        // The whole reason the snapshot carries both fields. On D3D the count
        // exists only while validation runs, so zero-and-off and zero-and-clean
        // are the same number and mean opposite things: "nothing is watching"
        // must not display as "nothing is wrong".
        ShellModel model = Apply(errors: 0, active: false);

        Assert.False(model.DebugLayerClean);
        Assert.False(model.HasDebugLayerErrors);
        Assert.Contains("not running", model.DebugLayerTip);
    }

    [Fact]
    public void The_count_going_back_to_zero_clears_the_slot()
    {
        // A fresh session against the same shell: the previous run's count must
        // not stand over a renderer that has reported nothing.
        var model = new ShellModel();
        model.ApplySnapshot(new FrameSnapshot { DebugLayerErrorCount = 2, DebugLayerActive = true });
        Assert.True(model.HasDebugLayerErrors);

        model.ApplySnapshot(new FrameSnapshot { DebugLayerErrorCount = 0, DebugLayerActive = true });
        Assert.False(model.HasDebugLayerErrors);
        Assert.True(model.DebugLayerClean);
    }
}
