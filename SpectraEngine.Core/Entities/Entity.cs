using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;

namespace SpectraEngine.Core.Entities;

/// <summary>
/// One live entity: the behaviour built from a node's <see cref="EntityData"/>
/// while an <see cref="EntityWorld"/> is active.
/// </summary>
/// <remarks>
/// <para>
/// <b>An instance is built FROM the authored data and never writes back into
/// it.</b> <see cref="EntityData"/> is the document model - the thing the editor
/// shows at rest, the thing the map format round-trips byte for byte - and this
/// is a runtime projection of it that exists only between
/// <see cref="EntityWorld.Activate"/> and <see cref="EntityWorld.Deactivate"/>.
/// That is what lets a property panel work with no runtime running, and it is
/// why stopping a session needs no state to be captured first: there is nothing
/// in the document that gameplay could have changed.
/// </para>
/// <para>
/// <b><see cref="TargetName"/> IS <see cref="SceneNode.Name"/>.</b> One
/// identity, one field, no shadow copy to drift: a rename in the scene tree is
/// a rename in the entity graph, and the index hears about it through
/// <c>Scene.NodeRenamed</c> rather than through a second setter somebody has to
/// remember to call. Duplicate names are legal, exactly as they are in the tree,
/// and firing at a name fires every match.
/// </para>
/// <para>
/// <b>Render thread only</b>, like the scene it reads and edits.
/// </para>
/// </remarks>
public abstract class Entity
{
    // First-appearance order over the authored connection list, which is the
    // order a level's file states and therefore the only order two engines
    // reading the same map can agree on.
    private readonly List<EntityOutput> _outputs = [];

    private float _nextThinkTime = float.PositiveInfinity;
    private int _thinkSerial;

    /// <summary>
    /// The node this entity IS. Set by the world before anything else on this
    /// instance runs, and repointed when the node is destroyed and restored
    /// under the same id (undo of a delete builds a NEW object with the old id).
    /// </summary>
    public SceneNode Node { get; private set; } = null!;

    /// <summary>The world running this entity.</summary>
    public EntityWorld World { get; private set; } = null!;

    /// <summary>
    /// The authored data this instance was built from. READ ONLY as far as the
    /// runtime is concerned: see the type remarks.
    /// </summary>
    public EntityData Data { get; private set; } = null!;

    /// <summary>The class this entity is, as the map spells it.</summary>
    public string ClassName => Data.ClassName;

    /// <summary>
    /// The name other entities' outputs address this one by, which is the node's
    /// own name and nothing else.
    /// </summary>
    public string TargetName => Node.Name;

    /// <summary>
    /// This entity's outputs and the runtime copy of the wires leaving them, in
    /// first-appearance order over the authored connection list.
    /// </summary>
    public IReadOnlyList<EntityOutput> Outputs => _outputs;

    /// <summary>
    /// The world time this entity next thinks at, or
    /// <see cref="float.PositiveInfinity"/> when it has no think pending.
    /// </summary>
    public float NextThinkTime => _nextThinkTime;

    // Bumped by every SetNextThink and every CancelThink. A heap cannot remove
    // an entry, so a rescheduled think leaves the superseded one behind; the
    // serial is what tells the dispatcher to drop it instead of thinking twice.
    internal int ThinkSerial => _thinkSerial;

    // The name this entity is currently listed under in the target-name index,
    // or null while it is not listed at all (its node has left the graph). The
    // index owns this field: a rename event carries the node's NEW name, so
    // without the old one recorded here the stale bucket entry could not be
    // found and the entity would answer to both names forever.
    internal string? IndexedName { get; set; }

    /// <summary>Called once, after every entity in the world exists and has parsed its keyvalues.</summary>
    /// <remarks>
    /// <b>Every target name in the world already resolves by the time this
    /// runs</b>, which is the entire reason activation is phased. Firing an
    /// output here is legal; the event is queued and delivered on the first
    /// tick, never during the walk.
    /// </remarks>
    protected internal virtual void OnSpawn()
    {
    }

    /// <summary>Called once, after every entity in the world has spawned.</summary>
    /// <remarks>
    /// The second phase exists so an entity may depend on another having
    /// finished its own spawn work, which <see cref="OnSpawn"/> cannot promise:
    /// spawn runs in traversal order, and half the world has not spawned yet
    /// when the first node's turn comes.
    /// </remarks>
    protected internal virtual void OnActivate()
    {
    }

    /// <summary>Called once, when the world deactivates or this entity is despawned.</summary>
    protected internal virtual void OnRemove()
    {
    }

    /// <summary>Called at the time <see cref="SetNextThink"/> asked for.</summary>
    /// <remarks>
    /// A think does NOT reschedule itself. An entity that wants to run again
    /// says so, which is what keeps a one-shot from needing a cancel and what
    /// makes "stop thinking" the absence of a call rather than a call.
    /// </remarks>
    protected internal virtual void Think()
    {
    }

    /// <summary>
    /// Reads one authored keyvalue. Returns whether this class RECOGNISES
    /// <paramref name="key"/>, which is not the same question as whether the
    /// value was usable.
    /// </summary>
    /// <remarks>
    /// <b>A recognised key carrying an unreadable value returns true and keeps
    /// the default</b>, after reporting it through <see cref="RefuseKeyvalue"/>.
    /// A map may legally carry a string nothing can parse - it was authored
    /// against a different build, or a person edited the file by hand - and
    /// throwing here takes down the load of an entire level over one field.
    /// Returning false means only "no such property on this class", which the
    /// world logs at debug level because a map carrying keys this build has no
    /// use for is ordinary.
    /// </remarks>
    public virtual bool ParseKeyValue(string key, string value) => false;

    /// <summary>
    /// Receives one input. Returns whether this class recognised
    /// <paramref name="input"/>.
    /// </summary>
    public virtual bool AcceptInput(string input, ref EntityInputContext context) => false;

    /// <summary>
    /// Asks to be woken at world time <paramref name="time"/>, replacing any
    /// think already pending. A time already past fires on the next tick.
    /// </summary>
    public void SetNextThink(float time)
    {
        _nextThinkTime = time;
        _thinkSerial++;
        World.ScheduleThink(this, time, _thinkSerial);
    }

    /// <summary>Asks to be woken <paramref name="delay"/> seconds from now.</summary>
    public void SetNextThinkIn(float delay) => SetNextThink(World.Time + delay);

    /// <summary>Drops any pending think.</summary>
    public void CancelThink()
    {
        // The queued entry stays in the heap and is discarded when it surfaces:
        // bumping the serial is the cancel.
        _thinkSerial++;
        _nextThinkTime = float.PositiveInfinity;
    }

    /// <summary>
    /// Fires <paramref name="output"/> along this entity's runtime copy of its
    /// wires. Every wire schedules an event; nothing is delivered inside this
    /// call.
    /// </summary>
    /// <remarks>
    /// <b>Queued rather than delivered, always, even at zero delay.</b> A
    /// synchronous delivery would let an input handler run inside the middle of
    /// whatever fired it, and the recursion depth of a cascade would then be a
    /// stack the render thread has to survive rather than a counter the tick can
    /// bound.
    /// </remarks>
    /// <param name="output">The output's name.</param>
    /// <param name="activator">
    /// Whoever started the chain, carried on to every entity the wires reach.
    /// Null means this entity started it.
    /// </param>
    /// <param name="parameterOverride">
    /// Replaces the parameter each wire authored, or null to send what the map
    /// says. Null and empty are different: empty is a real, deliberately blank
    /// argument.
    /// </param>
    public void FireOutput(string output, Entity? activator = null, string? parameterOverride = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        // An output nothing is wired to is not an error, it is the ordinary
        // state of most outputs in most levels.
        FindOutput(output)?.Fire(this, activator, parameterOverride);
    }

    /// <summary>This entity's <paramref name="output"/>, or null if nothing wires it.</summary>
    public EntityOutput? FindOutput(string output)
    {
        for (int i = 0; i < _outputs.Count; i++)
        {
            if (string.Equals(_outputs[i].Name, output, StringComparison.Ordinal))
                return _outputs[i];
        }

        return null;
    }

    /// <summary>
    /// Reports that <paramref name="key"/> was recognised and its value could
    /// not be read, so the default stands.
    /// </summary>
    protected void RefuseKeyvalue(string key, string value) =>
        World.ReportRefusedKeyvalue(this, key, value);

    // Binding is a method rather than three settable properties so an instance
    // cannot exist half-bound: World is dereferenced by SetNextThink and
    // FireOutput, and Data by ClassName.
    internal void Bind(SceneNode node, EntityWorld world, EntityData data)
    {
        Node = node;
        World = world;
        Data = data;
    }

    // The node was destroyed and restored under the same id, so the reference
    // this instance holds is stale. Only the index calls this, from its
    // NodeAdded handler.
    internal void RebindNode(SceneNode node) => Node = node;

    // THE RUNTIME COPY. Every wire's remaining fire count lives here and is
    // decremented here; the authored list is never touched, because a
    // decremented TimesToFire written back into EntityData is document
    // corruption that survives to the next save and is invisible until somebody
    // diffs the map.
    internal void BuildOutputs(IReadOnlyList<EntityConnection> connections)
    {
        _outputs.Clear();
        for (int i = 0; i < connections.Count; i++)
        {
            EntityConnection wire = connections[i];
            EntityOutput? output = FindOutput(wire.Output);
            if (output is null)
            {
                output = new EntityOutput(wire.Output);
                _outputs.Add(output);
            }

            output.Add(wire);
        }
    }
}
