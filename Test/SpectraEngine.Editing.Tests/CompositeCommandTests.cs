using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Commands;
using System.Collections.Generic;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// <see cref="CompositeCommand"/> ordering: children run forward on Do and in
/// reverse on Undo (the only order that survives commands whose effects depend
/// on each other), and the child list is copied at construction so a caller's
/// scratch buffer can be reused.
/// </summary>
public sealed class CompositeCommandTests
{
    [Fact]
    public void Do_runs_children_forward_and_Undo_runs_them_in_reverse()
    {
        var scene = new Scene("Editing");
        var log = new List<string>();
        var composite = new CompositeCommand(
            "Group",
            new RecordingCommand("a", log),
            new RecordingCommand("b", log),
            new RecordingCommand("c", log));

        composite.Do(scene);
        composite.Undo(scene);

        log.ShouldBe(new[] { "do:a", "do:b", "do:c", "undo:c", "undo:b", "undo:a" });
    }

    [Fact]
    public void Children_are_copied_so_the_caller_can_reuse_its_buffer()
    {
        var scene = new Scene("Editing");
        var log = new List<string>();
        var source = new List<IEditorCommand> { new RecordingCommand("a", log) };

        var composite = new CompositeCommand("Group", source);
        source.Clear();
        source.Add(new RecordingCommand("intruder", log));

        composite.Do(scene);

        composite.Count.ShouldBe(1);
        log.ShouldBe(new[] { "do:a" });
    }

    [Fact]
    public void Nulls_are_rejected()
    {
        Should.Throw<ArgumentException>(() => new CompositeCommand("Group", new IEditorCommand[] { null! }));
    }

    private sealed class RecordingCommand(string id, List<string> log) : IEditorCommand
    {
        public string Name => id;

        public void Do(Scene scene) => log.Add($"do:{id}");

        public void Undo(Scene scene) => log.Add($"undo:{id}");
    }
}
