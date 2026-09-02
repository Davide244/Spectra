using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;

namespace SpectraEngine.Core.Entities;

/// <summary>
/// The live entity graph over one <see cref="Scene"/>: builds an
/// <see cref="Entity"/> per node carrying <see cref="EntityData"/>, resolves
/// their wiring, and drains their think wakeups and output events in a
/// deterministic total order.
/// </summary>
/// <remarks>
/// <para>
/// <b>Instances exist only while a world is active.</b> The authored data on the
/// nodes is the document; this is a projection of it that is built by
/// <see cref="Activate"/> and thrown away by <see cref="Deactivate"/>, and it
/// never writes back. That is what makes stopping a session free of state
/// capture: nothing gameplay did is in the document to be undone.
/// </para>
/// <para>
/// <b>Activation is PHASED, and the phases are the feature.</b> Construct every
/// instance and parse its keyvalues; then build the name index and take the
/// runtime copies of every connection; then spawn everything; then activate
/// everything. A single-pass activate means the first entity's spawn fires at
/// targets that do not exist yet - which does not throw, it simply delivers
/// nothing, and the level is subtly wrong in a way that depends on node order.
/// </para>
/// <para>
/// <b>Render thread only</b>, like the scene it reads. <see cref="Tick"/> is
/// meant to run after the scene's own update and before the static-world
/// compile is pumped, so an entity that moves a world brush gets that brush's
/// cells dirtied in the same frame.
/// </para>
/// </remarks>
public sealed class EntityWorld
{
    private readonly Scene.Scene _scene;
    private readonly ILogger _logger;
    private readonly EntityCatalog _catalog;

    private readonly List<Entity> _entities = [];
    private readonly EntityEventQueue _queue = new();

    // Reused across dispatches. Safe because nothing an input handler can call
    // resolves a target: FireOutput queues, it never delivers.
    private readonly List<Entity> _resolved = [];

    private readonly List<SceneNode> _pendingSpawn = [];
    private readonly List<SceneNode> _spawnScratch = [];
    private readonly List<Entity> _pendingDespawn = [];
    private readonly List<Entity> _despawnScratch = [];

    // Once per CLASS NAME, not once per entity and not once per attempt: a class
    // this build does not have is usually a whole game's worth of entities at
    // once, and per-attempt reporting turns one fact into a log nobody reads.
    private readonly HashSet<string> _warnedMissingClasses = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warnedPlaceholderInputs = new(StringComparer.Ordinal);

    private TargetNameIndex? _index;
    private long _sequence;
    private float _time;
    private int _maxDispatchesPerTick = 4096;

    /// <param name="scene">The scene whose nodes carry the authored entities.</param>
    /// <param name="logger">Where refusals, missing classes and budget trips go.</param>
    /// <param name="catalog">
    /// The classes this world can build, or null for
    /// <see cref="EntityCatalog.Shared"/>.
    /// </param>
    public EntityWorld(Scene.Scene scene, ILogger logger, EntityCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(logger);

        _scene = scene;
        _logger = logger;
        _catalog = catalog ?? EntityCatalog.Shared;
    }

    /// <summary>The scene this world runs over.</summary>
    public Scene.Scene Scene => _scene;

    /// <summary>The classes this world can build.</summary>
    public EntityCatalog Catalog => _catalog;

    /// <summary>Whether <see cref="Activate"/> has run and <see cref="Deactivate"/> has not.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Seconds of ticked time since <see cref="Activate"/>.</summary>
    public float Time => _time;

    /// <summary>
    /// Every live entity, in the traversal order they were built in. Empty while
    /// the world is inactive.
    /// </summary>
    public IReadOnlyList<Entity> Entities => _entities;

    /// <summary>
    /// The name index, or null while the world is inactive - which is the honest
    /// answer: names resolve to runtime instances, and there are none.
    /// </summary>
    public TargetNameIndex? Index => _index;

    /// <summary>
    /// How many events one <see cref="Tick"/> may dispatch before it decides the
    /// cascade is runaway.
    /// </summary>
    /// <remarks>
    /// <b>Without this, the first mutual relay a user wires hangs the render
    /// thread with no clue why.</b> Two zero-delay relays pointed at each other
    /// are three clicks to build and are an infinite loop inside one tick; the
    /// budget turns that into an error message naming the entity, which is the
    /// difference between a bug report and a frozen editor.
    /// </remarks>
    public int MaxDispatchesPerTick
    {
        get => _maxDispatchesPerTick;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxDispatchesPerTick = value;
        }
    }

    /// <summary>How many ticks have hit <see cref="MaxDispatchesPerTick"/>.</summary>
    public int DispatchBudgetTripCount { get; private set; }

    /// <summary>How many events a budget trip has discarded over this world's life.</summary>
    public int DiscardedEventCount { get; private set; }

    /// <summary>How many events the most recent <see cref="Tick"/> dispatched.</summary>
    public int LastTickDispatchCount { get; private set; }

    /// <summary>Events waiting for their time to come.</summary>
    public int PendingEventCount => _queue.Count;

    /// <summary>Nodes queued for a deferred spawn.</summary>
    public int PendingSpawnCount => _pendingSpawn.Count;

    /// <summary>Entities queued for a deferred despawn.</summary>
    public int PendingDespawnCount => _pendingDespawn.Count;

    /// <summary>
    /// Builds every entity in the scene and brings it to life, in four phases.
    /// </summary>
    /// <exception cref="InvalidOperationException">The world is already active.</exception>
    public void Activate()
    {
        if (IsActive)
            throw new InvalidOperationException("This entity world is already active.");

        _time = 0f;
        _sequence = 0;
        _queue.Clear();
        _entities.Clear();
        _pendingSpawn.Clear();
        _pendingDespawn.Clear();

        // PHASE 1 - construct and parse. Nothing may look another entity up
        // here: the index does not exist yet, which is deliberate rather than
        // incidental. Keyvalue parsing must not depend on anybody else having
        // parsed theirs.
        foreach (SceneNode node in _scene.Root.Traverse())
        {
            if (node.Entity is not { } data)
                continue;

            Entity entity = Build(node, data);
            _entities.Add(entity);
            ParseKeyvalues(entity, data);
        }

        // PHASE 2 - identity, then wiring. Registering every entity before any
        // connection is taken is what makes a target name resolvable from the
        // first spawn onwards.
        _index = new TargetNameIndex(_scene);
        for (int i = 0; i < _entities.Count; i++)
            _index.Register(_entities[i]);
        for (int i = 0; i < _entities.Count; i++)
            _entities[i].BuildOutputs(_entities[i].Data.Connections);

        // Live before OnSpawn, so a spawn may schedule a think or fire an
        // output. Both queue; neither is delivered inside the walk.
        IsActive = true;

        // PHASE 3 - spawn, in traversal order.
        for (int i = 0; i < _entities.Count; i++)
            _entities[i].OnSpawn();

        // PHASE 4 - activate, once every spawn has finished. An entity may
        // depend on another's spawn work here, which phase 3 cannot promise.
        for (int i = 0; i < _entities.Count; i++)
            _entities[i].OnActivate();
    }

    /// <summary>
    /// Runs <see cref="Entity.OnRemove"/> on everything, unsubscribes from the
    /// scene and drops every instance. Harmless on an inactive world.
    /// </summary>
    public void Deactivate()
    {
        if (!IsActive)
            return;

        IsActive = false;

        for (int i = 0; i < _entities.Count; i++)
            _entities[i].OnRemove();

        _index?.Dispose();
        _index = null;

        _entities.Clear();
        _queue.Clear();
        _pendingSpawn.Clear();
        _pendingDespawn.Clear();
        _resolved.Clear();
    }

    /// <summary>
    /// Advances the world by <paramref name="fixedDt"/> seconds and drains
    /// everything now due, then drains the deferred spawn and despawn queues.
    /// </summary>
    /// <exception cref="InvalidOperationException">The world is not active.</exception>
    public void Tick(float fixedDt)
    {
        if (!IsActive)
            throw new InvalidOperationException("Tick on an entity world that is not active.");

        _time += fixedDt;

        int dispatched = 0;
        while (_queue.TryPeek(out EntityEvent next) && next.Time <= _time)
        {
            if (dispatched >= _maxDispatchesPerTick)
            {
                TripDispatchBudget(next);
                break;
            }

            _queue.TryPop(out EntityEvent due);
            dispatched++;
            Dispatch(due);
        }

        LastTickDispatchCount = dispatched;
        DrainDeferred();
    }

    /// <summary>
    /// Asks for an entity to be built for <paramref name="node"/> at the end of
    /// the current tick.
    /// </summary>
    /// <remarks>
    /// <b>This exists because a scene event handler must not mutate the
    /// graph.</b> The index's handlers run inside the scene's ownership walk,
    /// where an add or a remove corrupts the traversal in progress, so anything
    /// that wants to react by spawning says so here and is served after the
    /// tick's dispatch loop has finished. Nothing in the engine calls this yet;
    /// it is here so the trigger work that will need it does not have to
    /// redesign the tick to get it.
    /// </remarks>
    public void QueueSpawn(SceneNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _pendingSpawn.Add(node);
    }

    /// <summary>Asks for <paramref name="entity"/> to be removed at the end of the current tick.</summary>
    public void QueueDespawn(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        _pendingDespawn.Add(entity);
    }

    // --- what entities call back into ---------------------------------------

    internal void ScheduleThink(Entity entity, float time, int serial) =>
        _queue.Push(new EntityEvent
        {
            // A non-finite time orders against nothing (every comparison with a
            // NaN is false), which would corrupt the heap's ordering rather than
            // merely mis-time one think. "Now" is the only answer that keeps the
            // queue a queue.
            Time = float.IsFinite(time) ? time : _time,
            Sequence = _sequence++,
            Kind = EntityEventKind.Think,
            Entity = entity,
            ThinkSerial = serial,
            TargetName = "",
            Input = "",
            Parameter = "",
            Output = "",
        });

    internal void ScheduleOutput(
        Entity caller,
        Entity? activator,
        string output,
        in EntityConnection wire,
        string? parameterOverride)
    {
        // A negative delay is a past time and a NaN one is a value no comparison
        // orders; both become "now", because an event that can never be due is
        // an event that vanishes with nothing reporting it.
        float delay = wire.Delay > 0f ? wire.Delay : 0f;

        _queue.Push(new EntityEvent
        {
            Time = _time + delay,
            Sequence = _sequence++,
            Kind = EntityEventKind.Input,
            Entity = caller,
            Activator = activator,
            TargetName = wire.TargetName,
            Input = wire.Input,
            Parameter = parameterOverride ?? wire.Parameter,
            Output = output,
        });
    }

    internal void ReportRefusedKeyvalue(Entity entity, string key, string value) =>
        _logger.LogWarning(
            "Entity '{TargetName}' ({ClassName}) cannot read keyvalue '{Key}' = '{Value}'; keeping the default.",
            entity.TargetName, entity.ClassName, key, value);

    internal void ReportPlaceholderInput(Entity entity, string input)
    {
        if (!_warnedPlaceholderInputs.Add(entity.ClassName))
            return;

        _logger.LogWarning(
            "Entity class '{ClassName}' is not registered in this build, so '{TargetName}' cannot accept " +
            "'{Input}' or any other input. Its data is kept and re-saves unchanged.",
            entity.ClassName, entity.TargetName, input);
    }

    // --- internals -----------------------------------------------------------

    private Entity Build(SceneNode node, EntityData data)
    {
        Entity entity;
        if (data.ClassName.Length > 0 && _catalog.TryCreate(data.ClassName, out Entity? created))
        {
            entity = created;
        }
        else
        {
            entity = new PlaceholderEntity();
            if (data.ClassName.Length > 0 && _warnedMissingClasses.Add(data.ClassName))
            {
                _logger.LogWarning(
                    "No entity class named '{ClassName}' is registered; '{NodeName}' keeps its data as a " +
                    "placeholder and behaves as nothing.",
                    data.ClassName, node.Name);
            }
        }

        entity.Bind(node, this, data);
        return entity;
    }

    private void ParseKeyvalues(Entity entity, EntityData data)
    {
        IReadOnlyList<KeyValuePair<string, string>> keyvalues = data.Keyvalues;
        for (int i = 0; i < keyvalues.Count; i++)
        {
            KeyValuePair<string, string> pair = keyvalues[i];
            if (entity.ParseKeyValue(pair.Key, pair.Value))
                continue;

            // Debug, not a warning: a map legitimately carries keys this build
            // has no property for - editor-only members, a newer game's fields,
            // a placeholder's entire keyvalue list - and they are preserved
            // rather than lost, so nothing is wrong.
            _logger.LogDebug(
                "Entity '{TargetName}' ({ClassName}) has no property '{Key}'; the value is kept but unused.",
                entity.TargetName, entity.ClassName, pair.Key);
        }
    }

    private void Dispatch(in EntityEvent due)
    {
        if (due.Kind == EntityEventKind.Think)
        {
            Entity thinker = due.Entity!;
            // Superseded by a later SetNextThink or a CancelThink. A heap cannot
            // remove an entry, so this is where a stale one dies.
            if (thinker.ThinkSerial != due.ThinkSerial)
                return;

            thinker.Think();
            return;
        }

        _resolved.Clear();
        // Self IS the caller for a connection: the entity a wire leaves is the
        // entity firing it.
        _index!.Resolve(due.TargetName, due.Entity, due.Activator, due.Entity, _resolved);

        if (_resolved.Count == 0)
        {
            _logger.LogDebug(
                "Output '{Output}' names '{TargetName}', which matches nothing right now; '{Input}' was not sent.",
                due.Output, due.TargetName, due.Input);
            return;
        }

        var context = new EntityInputContext(due.Activator, due.Entity, due.Parameter);
        for (int i = 0; i < _resolved.Count; i++)
        {
            Entity target = _resolved[i];
            if (target.AcceptInput(due.Input, ref context))
                continue;

            // The entity decides what to report about a refusal it can explain
            // (a placeholder says its class is missing, once per class). This
            // line is the fallback for a class that simply has no such input.
            _logger.LogDebug(
                "Entity '{TargetName}' ({ClassName}) has no input '{Input}'.",
                target.TargetName, target.ClassName, due.Input);
        }
    }

    private void TripDispatchBudget(in EntityEvent offender)
    {
        DispatchBudgetTripCount++;

        string offenderName = offender.Kind == EntityEventKind.Think
            ? offender.Entity?.TargetName ?? ""
            : offender.TargetName;
        string what = offender.Kind == EntityEventKind.Think
            ? "a think"
            : $"output '{offender.Output}' sending '{offender.Input}'";

        _logger.LogError(
            "Entity dispatch budget of {Budget} was exhausted in one tick; the cascade was still firing " +
            "{What} at '{TargetName}'. Everything still due this tick has been dropped. This is what a " +
            "zero-delay loop between two entities looks like.",
            _maxDispatchesPerTick, what, offenderName);

        // Drop the runaway rather than leaving it queued. Everything due at this
        // instant IS the cascade; anything scheduled for later is ordinary work
        // and is left alone, so a level with one bad relay keeps running.
        int discarded = 0;
        while (_queue.TryPeek(out EntityEvent next) && next.Time <= _time)
        {
            _queue.TryPop(out _);
            discarded++;
        }

        DiscardedEventCount += discarded;
    }

    private void DrainDeferred()
    {
        // Swapped into scratch lists before draining, so work queued BY the
        // drain waits for the next tick instead of extending this one forever.
        if (_pendingDespawn.Count > 0)
        {
            _despawnScratch.AddRange(_pendingDespawn);
            _pendingDespawn.Clear();

            for (int i = 0; i < _despawnScratch.Count; i++)
                Despawn(_despawnScratch[i]);

            _despawnScratch.Clear();
        }

        // Despawns first: a spawn that reuses a name must land after the entity
        // that was holding it has gone, or the two overlap for one tick and a
        // wire fires at both.
        if (_pendingSpawn.Count > 0)
        {
            _spawnScratch.AddRange(_pendingSpawn);
            _pendingSpawn.Clear();

            for (int i = 0; i < _spawnScratch.Count; i++)
                Spawn(_spawnScratch[i]);

            _spawnScratch.Clear();
        }
    }

    private void Spawn(SceneNode node)
    {
        if (node.Entity is not { } data)
            return;

        // Re-checked rather than assumed: the node may have been queued twice,
        // or may already have been built by Activate.
        if (_index!.TryGetByNodeId(node.Id, out _))
            return;

        Entity entity = Build(node, data);
        _entities.Add(entity);
        ParseKeyvalues(entity, data);
        _index.Register(entity);
        entity.BuildOutputs(data.Connections);
        entity.OnSpawn();
        entity.OnActivate();
    }

    private void Despawn(Entity entity)
    {
        int at = _entities.IndexOf(entity);
        if (at < 0)
            return;

        entity.OnRemove();
        _index!.Unregister(entity);
        _entities.RemoveAt(at);
    }
}
