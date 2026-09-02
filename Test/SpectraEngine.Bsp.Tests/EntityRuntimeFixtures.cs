using SpectraEngine.Core.Entities;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The entity classes the runtime tests wire together, and the scaffolding that
/// stands a world up over a scene.
/// </summary>
/// <remarks>
/// <b>Every test gets its OWN catalogue.</b> <see cref="EntityCatalog.Shared"/>
/// freezes on its first read and refuses a duplicate class name, both of which
/// are exactly right for a process-wide registry fed by module initializers and
/// exactly wrong for a test suite: the first test to run would freeze it for
/// every test after, and two test classes registering the same name would fail
/// in whichever order the runner happened to pick.
/// </remarks>
internal static class EntityRuntime
{
    /// <summary>A catalogue holding the fixture classes, sharing one delivery log.</summary>
    public static EntityCatalog Catalog(List<string> log)
    {
        var catalog = new EntityCatalog();
        catalog.Add(new EntitySchema("recorder"), () => new RecordingEntity(log));
        catalog.Add(new EntitySchema("relay"), () => new RelayEntity());
        catalog.Add(new EntitySchema("speedster"), () => new SpeedEntity());
        catalog.Add(new EntitySchema("lifecycle"), () => new LifecycleEntity(log));
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
        float delay = 0f,
        int timesToFire = EntityConnection.Infinite) =>
        node.Entity!.Connections.Add(
            new EntityConnection(output, targetName, input, parameter, delay, timesToFire));

    /// <summary>The live entity built for <paramref name="node"/>.</summary>
    public static Entity Live(EntityWorld world, SceneNode node)
    {
        world.Index.ShouldNotBeNull();
        world.Index!.TryGetByNodeId(node.Id, out Entity? entity).ShouldBeTrue();
        return entity!;
    }
}

/// <summary>
/// Writes one line per input it receives, so a test can assert on delivery ORDER
/// as well as on delivery.
/// </summary>
internal sealed class RecordingEntity : Entity
{
    private readonly List<string> _log;

    public RecordingEntity(List<string> log) => _log = log;

    /// <summary>A per-instance label, so two entities sharing a name are still told apart.</summary>
    public string Tag { get; private set; } = "";

    public string Label => Tag.Length > 0 ? Tag : TargetName;

    public override bool ParseKeyValue(string key, string value)
    {
        if (!string.Equals(key, "tag", StringComparison.Ordinal))
            return false;

        Tag = value;
        return true;
    }

    public override bool AcceptInput(string input, ref EntityInputContext context)
    {
        _log.Add(
            $"{Label}:{input}:{context.Parameter}:" +
            $"{context.Activator?.TargetName ?? "-"}:{context.Caller?.TargetName ?? "-"}");
        return true;
    }
}

/// <summary>
/// Writes one line per lifecycle callback, so a test can assert the ORDER of the
/// activation phases rather than only their effects.
/// </summary>
internal sealed class LifecycleEntity : Entity
{
    private readonly List<string> _log;

    public LifecycleEntity(List<string> log) => _log = log;

    protected internal override void OnSpawn() => _log.Add($"spawn:{TargetName}");

    protected internal override void OnActivate() => _log.Add($"activate:{TargetName}");

    protected internal override void OnRemove() => _log.Add($"remove:{TargetName}");
}

/// <summary>Turns one input straight back into one output, at whatever delay its wires state.</summary>
internal sealed class RelayEntity : Entity
{
    public int Triggers { get; private set; }

    public override bool AcceptInput(string input, ref EntityInputContext context)
    {
        if (!string.Equals(input, "Trigger", StringComparison.Ordinal))
            return false;

        Triggers++;
        FireOutput("OnTrigger", context.Activator);
        return true;
    }
}

/// <summary>
/// Carries one float keyvalue, and reports a value it cannot read rather than
/// throwing over it.
/// </summary>
internal sealed class SpeedEntity : Entity
{
    public float Speed { get; private set; } = 100f;

    public override bool ParseKeyValue(string key, string value)
    {
        if (!string.Equals(key, "speed", StringComparison.Ordinal))
            return false;

        if (!KeyvalueWire.TryParseFloat(value, out float parsed))
        {
            // Recognised the key, could not read the value: the default stands
            // and the load continues.
            RefuseKeyvalue(key, value);
            return true;
        }

        Speed = parsed;
        return true;
    }
}
