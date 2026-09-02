using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Entities;
using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;

namespace SpectraEngine.Entities.Tests;

/// <summary>
/// The scaffolding the behaviour tests stand a real
/// <see cref="EntityWorld"/> up with.
/// </summary>
/// <remarks>
/// <b>A private catalogue per test, never <see cref="EntityCatalog.Shared"/>.</b>
/// The shared one freezes on its first read and refuses a duplicate name, which
/// is right for a process-wide registry fed by module initializers and wrong for
/// a test suite: the first test to run would freeze it for every test after it.
/// The generated classes go in by hand through their own generated schemas, so
/// what runs here is exactly what the generator produced.
/// </remarks>
internal static class EntityRuntime
{
    /// <summary>A catalogue holding the built-in classes and the recorder.</summary>
    public static EntityCatalog Catalog(List<string> log)
    {
        var catalog = new EntityCatalog();
        catalog.Add(LogicRelay.SpectraSchema, static () => new LogicRelay());
        catalog.Add(LogicTimer.SpectraSchema, static () => new LogicTimer());
        catalog.Add(MathCounter.SpectraSchema, static () => new MathCounter());
        catalog.Add(new EntitySchema("test_recorder"), () => new RecordingEntity(log));
        return catalog;
    }

    /// <summary>Attaches an entity of <paramref name="className"/> to a fresh child node.</summary>
    public static SceneNode Place(SceneNode parent, string name, string className)
    {
        SceneNode node = parent.CreateChild(name);
        node.Entity = new EntityData(className);
        return node;
    }

    /// <summary>Adds one authored wire to a node's entity data.</summary>
    public static void Wire(
        SceneNode node,
        string output,
        string targetName,
        string input,
        string parameter = "",
        float delay = 0f) =>
        node.Entity!.Connections.Add(
            new EntityConnection(output, targetName, input, parameter, delay, EntityConnection.Infinite));

    /// <summary>The live entity built for <paramref name="node"/>.</summary>
    public static T Live<T>(EntityWorld world, SceneNode node)
        where T : Entity
    {
        world.Index.ShouldNotBeNull();
        world.Index!.TryGetByNodeId(node.Id, out Entity? entity).ShouldBeTrue();
        return entity.ShouldBeOfType<T>();
    }

    /// <summary>
    /// Delivers one input the way <see cref="EntityWorld"/> delivers one, which
    /// is a direct call into the generated dispatch switch.
    /// </summary>
    public static bool Send(Entity entity, string input, string parameter = "")
    {
        var context = new EntityInputContext(null, null, parameter);
        return entity.AcceptInput(input, ref context);
    }
}

/// <summary>Writes one line per input it receives, so a test can count deliveries.</summary>
/// <remarks>
/// Hand-written rather than generated: it is the instrument, not the subject, and
/// a generated observer would make a failure ambiguous between the two.
/// </remarks>
internal sealed class RecordingEntity : Entity
{
    private readonly List<string> _log;

    public RecordingEntity(List<string> log) => _log = log;

    public override bool AcceptInput(string input, ref EntityInputContext context)
    {
        _log.Add($"{TargetName}:{input}:{context.Parameter}");
        return true;
    }
}

/// <summary>Captures log lines so a test can assert on what was reported.</summary>
internal sealed class CapturingLogger : ILogger
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<string> MessagesAt(LogLevel level) =>
        _entries.Where(entry => entry.Level == level).Select(entry => entry.Message).ToArray();

    public string Describe() => _entries.Count == 0
        ? "(no log entries)"
        : string.Join(Environment.NewLine, _entries.Select(entry => $"[{entry.Level}] {entry.Message}"));

    public bool IsEnabled(LogLevel logLevel) => true;

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        _entries.Add((logLevel, formatter(state, exception)));
}
