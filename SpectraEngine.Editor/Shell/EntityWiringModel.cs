using SpectraEngine.Core.Entities;
using SpectraEngine.Core.Inspection;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace SpectraEngine.Editor.Shell;

/// <summary>
/// One wire in the Outputs section: which output fires it, what it sends and to
/// whom.
/// </summary>
/// <remarks>
/// <para>
/// <b>On the panel's existing commit contract, not a second one.</b> Every
/// text cell here is a <see cref="PropertyFieldModel"/>, so Enter and losing
/// focus commit, Escape reverts, a focused cell stops taking refreshes, and
/// text that will not parse reverts rather than sticking. The output dropdown
/// applies on the click and guards its refresh, exactly as a choice row does.
/// </para>
/// <para>
/// <b>Every commit posts the WHOLE list.</b> The command carries absolute
/// arrays - see <c>SetEntityConnectionsCommand</c> for why a delta cannot be
/// replayed - so a row's job is to hold its own six values and let the section
/// above it gather them.
/// </para>
/// </remarks>
public sealed class ConnectionRowModel : ObservableObject
{
    private readonly Action _changed;
    private string _output = string.Empty;
    private string _target = string.Empty;
    private string _input = string.Empty;
    private string _parameter = string.Empty;
    private float _delay;
    private int _times = EntityConnection.Infinite;
    private bool _targetResolves = true;
    private bool _applyingRefresh;
    private IReadOnlyList<string> _outputChoices = [];

    internal ConnectionRowModel(Action changed)
    {
        _changed = changed;

        // PropertyId.None and PropertyAxes.All: these cells belong to no
        // inspector row and write no component of a vector. The field model is
        // reused for its COMMIT CONTRACT, which is the whole point - a second
        // implementation of "a focused box stops taking refreshes" is a second
        // thing that can drift from the first.
        TargetField = new PropertyFieldModel(
            PropertyId.None, PropertyAxes.All, string.Empty, CommitTarget);
        InputField = new PropertyFieldModel(
            PropertyId.None, PropertyAxes.All, string.Empty, CommitInput);

        // The one cell where an empty value is a value: "send no argument" is
        // both legal and common, so a field that reverted a cleared box could
        // never take a parameter back out once one had been typed.
        ParameterField = new PropertyFieldModel(
            PropertyId.None, PropertyAxes.All, string.Empty, CommitParameter)
        { AllowsEmpty = true };

        DelayField = new PropertyFieldModel(
            PropertyId.None, PropertyAxes.All, string.Empty, CommitDelay) { Unit = "s" };
        TimesField = new PropertyFieldModel(
            PropertyId.None, PropertyAxes.All, string.Empty, CommitTimes);

        // The output's OTHER editor, for a class no schema declares. A field
        // model rather than a string bound two-way, because a two-way Text
        // binding writes per keystroke: typing "OnFoo" would post five wiring
        // edits and put five entries in the history for one word.
        OutputField = new PropertyFieldModel(
            PropertyId.None, PropertyAxes.All, string.Empty, CommitOutput)
        { AllowsEmpty = true };
    }

    /// <summary>Who to send to. Free text in v1; an entity picker is deferred.</summary>
    public PropertyFieldModel TargetField { get; }

    /// <summary>Which input to send. Free text in v1; cross-entity validation is deferred.</summary>
    public PropertyFieldModel InputField { get; }

    /// <summary>The argument to send, empty for none.</summary>
    public PropertyFieldModel ParameterField { get; }

    /// <summary>Seconds to wait before sending.</summary>
    public PropertyFieldModel DelayField { get; }

    /// <summary>How many times this wire may fire, or -1 for forever.</summary>
    public PropertyFieldModel TimesField { get; }

    /// <summary>
    /// The output, typed rather than picked, for a class nothing declares.
    /// </summary>
    public PropertyFieldModel OutputField { get; }

    /// <summary>
    /// The output that fires this wire, as the dropdown edits it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Guarded against its own refresh</b>, because assigning the published
    /// value back into a dropdown is indistinguishable from a user picking it,
    /// and would post an edit per publish for as long as an entity stayed
    /// selected.
    /// </para>
    /// <para>
    /// <b>An empty assignment is refused, and that is not tidiness.</b> A
    /// <c>ComboBox</c> clears its own <c>SelectedItem</c> when its item source
    /// is replaced, and a two-way binding delivers that here looking exactly
    /// like a click - so a refresh that widened the choice list would post a
    /// wire with no output at all. Nothing in the list is ever empty, so no
    /// real choice is lost by refusing.
    /// </para>
    /// </remarks>
    public string Output
    {
        get => _output;
        set
        {
            if (_applyingRefresh || string.IsNullOrEmpty(value))
                return;

            if (!Set(ref _output, value))
                return;

            OutputField.Refresh(value, mixed: false);
            Raise(nameof(HasOutputChoices));
            _changed();
        }
    }

    /// <summary>
    /// The outputs offered by the dropdown: exactly what the class declares.
    /// </summary>
    /// <remarks>
    /// <b>EXACTLY what it declares, and never widened by the authored value.</b>
    /// That was the first design, and it is wrong in a way that only a running
    /// shell shows: replacing a bound item source makes the control discard its
    /// selection, and a binding will not re-push a value it has already pushed,
    /// so the dropdown sits permanently blank over a model that knows the
    /// answer. Handing it the schema's own list instance means the source never
    /// changes at all after the row is built, and the whole failure mode is
    /// gone rather than compensated for. The authored value is not lost: a
    /// value the class does not declare is shown in a text box instead, which
    /// is also the more honest reading - a menu should offer what the class
    /// really has.
    /// </remarks>
    public IReadOnlyList<string> OutputChoices
    {
        get => _outputChoices;
        private set
        {
            if (!ReferenceEquals(_outputChoices, value))
            {
                _outputChoices = value;
                Raise();
                Raise(nameof(HasOutputChoices));
            }
        }
    }

    /// <summary>
    /// Whether this wire's output can be PICKED, so the row shows a dropdown
    /// rather than a text box.
    /// </summary>
    /// <remarks>
    /// <b>A class nothing declares gets a typed field, and so does an output
    /// the class does not name.</b> The design is "a dropdown from the schema",
    /// and a dropdown that cannot show its own value is worse than a plain
    /// field: it would render blank and then write that blank back the first
    /// time somebody touched the row beside it. A map may legitimately carry
    /// either - a class this build has no schema for, or an output a newer
    /// version of the class dropped - and both must stay visible and editable,
    /// because keeping the payload as strings is exactly what that is for.
    /// </remarks>
    public bool HasOutputChoices => Declares(_outputChoices, _output);

    /// <summary>Whether anything in the scene answers to this wire's target.</summary>
    public bool TargetResolves
    {
        get => _targetResolves;
        private set
        {
            if (Set(ref _targetResolves, value))
            {
                Raise(nameof(HasTargetWarning));
                Raise(nameof(TargetWarning));
            }
        }
    }

    /// <summary>Whether to show the amber warning line under this wire.</summary>
    public bool HasTargetWarning => !_targetResolves;

    /// <summary>
    /// Why the target does not resolve, in words.
    /// </summary>
    /// <remarks>
    /// <b>Amber - the shell's STATE colour - and the wire is kept.</b> Not the
    /// accent, which means selection, and not the danger colour, which means an
    /// error: the map loader already keeps a wire whose target is missing
    /// rather than dropping it, because the target may be spawned later or may
    /// belong to a level that is not open, and a panel that quietly disagreed
    /// with the loader would be the one place a person's wiring disappeared.
    /// </remarks>
    public string TargetWarning => _targetResolves
        ? string.Empty
        : _target.Length == 0
            ? "No target set"
            : $"Nothing here is named '{_target}'";

    /// <summary>This row as the value the command writes.</summary>
    public EntityConnection ToConnection() =>
        new(_output, _target, _input, _parameter, _delay, _times);

    /// <summary>Takes a fresh value from a published snapshot.</summary>
    internal void Refresh(EntityConnectionInfo info, IReadOnlyList<string> declared)
    {
        EntityConnection wire = info.Wire;

        _target = wire.TargetName;
        _input = wire.Input;
        _parameter = wire.Parameter;
        _delay = wire.Delay;
        _times = wire.TimesToFire;

        // The choices FIRST and the value second, because a dropdown cannot
        // select an item its list does not hold yet - and both inside the
        // guard, since replacing the list is what makes the control clear its
        // own selection. After the row's first refresh the list is the schema's
        // own instance and stops changing, which is the point.
        bool pickable = HasOutputChoices;

        _applyingRefresh = true;
        OutputChoices = declared;
        Set(ref _output, wire.Output, nameof(Output));
        _applyingRefresh = false;

        // Guarded, because Refresh runs for every wire on every pump and an
        // unconditional raise is binding churn for a panel that has not
        // changed - the same rule the property rows above follow. It CAN move
        // without the choices moving: an engine echo that changes the output to
        // one the class does not declare flips this row to a text box.
        if (pickable != HasOutputChoices)
            Raise(nameof(HasOutputChoices));

        OutputField.Refresh(wire.Output, mixed: false);
        TargetField.Refresh(wire.TargetName, mixed: false);
        InputField.Refresh(wire.Input, mixed: false);
        ParameterField.Refresh(wire.Parameter, mixed: false);
        DelayField.Refresh(PropertyFieldModel.Format(wire.Delay), mixed: false);
        TimesField.Refresh(wire.TimesToFire.ToString(CultureInfo.InvariantCulture), mixed: false);

        TargetResolves = info.TargetResolves;
    }

    private static bool Declares(IReadOnlyList<string> declared, string output)
    {
        for (int i = 0; i < declared.Count; i++)
        {
            if (string.Equals(declared[i], output, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private void CommitOutput(PropertyFieldModel field, string typed)
    {
        if (!Set(ref _output, typed, nameof(Output)))
            return;

        // Typing a name the class DOES declare flips this row from the text box
        // back to the dropdown, so the switch has to be announced here as well
        // as on a refresh.
        Raise(nameof(HasOutputChoices));
        _changed();
    }

    private void CommitTarget(PropertyFieldModel field, string typed)
    {
        _target = typed;
        Raise(nameof(TargetWarning));
        _changed();
    }

    private void CommitInput(PropertyFieldModel field, string typed)
    {
        _input = typed;
        _changed();
    }

    private void CommitParameter(PropertyFieldModel field, string typed)
    {
        _parameter = typed;
        _changed();
    }

    private void CommitDelay(PropertyFieldModel field, string typed)
    {
        // Reverts rather than sticking, exactly as an unparseable position
        // does: a box left holding something the scene does not contain
        // disagrees with the scene until somebody notices. A negative delay is
        // the same case - the queue keys on a fire time, so a wire scheduled
        // into the past is not a shorter delay, it is a different bug.
        if (!PropertyFieldModel.TryParseNumber(typed, out float value) || value < 0f)
        {
            field.Revert();
            return;
        }

        _delay = value;
        _changed();
    }

    private void CommitTimes(PropertyFieldModel field, string typed)
    {
        if (!int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            field.Revert();
            return;
        }

        // Any negative reads as infinite, which is EntityConnection's own rule -
        // stated there so a count decremented past the end cannot wrap into a
        // finite one. Normalised here so the file gets the canonical -1 rather
        // than whatever was typed.
        _times = value < 0 ? EntityConnection.Infinite : value;
        _changed();
    }

    /// <summary>Seeds a brand new row, for the Add button.</summary>
    internal void Seed(IReadOnlyList<string> declared)
    {
        Refresh(
            new EntityConnectionInfo(
                new EntityConnection(
                    declared.Count > 0 ? declared[0] : string.Empty,
                    string.Empty, string.Empty, string.Empty, 0f, EntityConnection.Infinite),
                TargetResolves: false),
            declared);
    }
}

/// <summary>
/// The Outputs section: the wires leaving the selected entity, and the two
/// buttons that add and remove one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Present only for a single-node entity selection</b>, because that is all
/// the engine publishes: merging several entities' wiring has no honest answer
/// and is a named deferral. See <c>EntityPanelInfo</c>.
/// </para>
/// <para>
/// <b>The rows are the user's while an edit is in flight.</b> A snapshot
/// published between an add and the engine's echo still describes the list the
/// add replaced, and writing that back would make the new row appear and
/// vanish. The hold is the same bounded local opinion
/// <see cref="OptimisticValue{T}"/> documents: after
/// <see cref="HoldSnapshots"/> disagreeing snapshots the engine wins visibly,
/// because the value a user asks for is not always the value they get - a
/// wiring edit is refused outright while play mode owns the scene.
/// </para>
/// <para>UI thread only, like everything else on the panel.</para>
/// </remarks>
public sealed class EntityWiringModel : ObservableObject
{
    /// <summary>
    /// How many disagreeing snapshots to ignore before the engine wins.
    /// </summary>
    /// <remarks>
    /// The same six <see cref="OptimisticValue{T}"/> uses, and for the same
    /// reason: about a tenth of a second at the resting publish rate, and
    /// deliberately short, because a refusal the user cannot see is worse than
    /// a slow echo.
    /// </remarks>
    public const int HoldSnapshots = 6;

    private readonly Action<Guid, IReadOnlyList<EntityConnection>> _apply;
    private EntityConnection[]? _pending;
    private int _ticks;
    private Guid _nodeId;
    private bool _hasEntity;
    private bool _isKnown = true;
    private IReadOnlyList<string> _outputs = [];

    internal EntityWiringModel(Action<Guid, IReadOnlyList<EntityConnection>> apply) => _apply = apply;

    /// <summary>The wires, in AUTHORED ORDER, which is never re-sorted.</summary>
    public ObservableCollection<ConnectionRowModel> Rows { get; } = [];

    /// <summary>Whether the selection is one node carrying an entity.</summary>
    public bool HasEntity
    {
        get => _hasEntity;
        private set
        {
            if (Set(ref _hasEntity, value))
                Raise(nameof(IsEmpty));
        }
    }

    /// <summary>Whether there is an entity here and it has no wires yet.</summary>
    public bool IsEmpty => _hasEntity && Rows.Count == 0;

    /// <summary>
    /// Whether this session has a schema for the selected class, so the
    /// section can say why the output dropdowns are text boxes.
    /// </summary>
    public bool IsKnown
    {
        get => _isKnown;
        private set
        {
            if (Set(ref _isKnown, value))
                Raise(nameof(ShowsUnknownOutputs));
        }
    }

    /// <summary>Whether to explain that the outputs could not be listed.</summary>
    public bool ShowsUnknownOutputs => _hasEntity && !_isKnown;

    /// <summary>Takes one published snapshot's entity payload.</summary>
    public void Apply(EntityPanelInfo? info)
    {
        if (info is null)
        {
            // A selection that is not one entity drops everything, the pending
            // edit included: it was aimed at a node this panel is no longer
            // showing, and holding it would make the next entity selected open
            // with the previous one's wiring on screen.
            _pending = null;
            _ticks = 0;
            _nodeId = Guid.Empty;
            HasEntity = false;
            IsKnown = true;
            if (Rows.Count > 0)
            {
                Rows.Clear();
                Raise(nameof(IsEmpty));
            }

            return;
        }

        // A different node is a different subject, so an edit still in flight
        // for the previous one has nothing to reconcile against here.
        if (info.NodeId != _nodeId)
        {
            _nodeId = info.NodeId;
            _pending = null;
            _ticks = 0;
        }

        HasEntity = true;
        IsKnown = info.IsKnown;
        _outputs = info.Outputs;

        if (_pending is not null)
        {
            if (Matches(info.Connections, _pending))
            {
                _pending = null;
                _ticks = 0;
            }
            else if (++_ticks < HoldSnapshots)
            {
                // Still in flight. This snapshot describes a frame from before
                // the edit; writing it back would undo it on screen and then
                // redo it.
                return;
            }
            else
            {
                _pending = null;
                _ticks = 0;
            }
        }

        SyncRows(info.Connections);
    }

    /// <summary>Adds an empty wire and posts the new list.</summary>
    /// <remarks>
    /// <b>Posted immediately, rather than staged until it is filled in.</b> An
    /// unwired row is a legal connection - the target is resolved when the
    /// output fires and not before - so there is no half-built state to protect
    /// anybody from, and staging would mean a row that survives a snapshot only
    /// while the panel remembers it.
    /// </remarks>
    public void Add()
    {
        if (!_hasEntity)
            return;

        var row = new ConnectionRowModel(Post);
        row.Seed(_outputs);
        Rows.Add(row);
        Raise(nameof(IsEmpty));
        Post();
    }

    /// <summary>Removes one wire and posts the new list.</summary>
    public void Remove(ConnectionRowModel row)
    {
        if (row is null || !Rows.Remove(row))
            return;

        Raise(nameof(IsEmpty));
        Post();
    }

    // Gathers every row, in the order they are shown, which IS the order they
    // are stored in: connection order is authored data and round-trips through
    // map.json, so nothing here may sort or de-duplicate.
    private void Post()
    {
        if (!_hasEntity)
            return;

        var wires = new EntityConnection[Rows.Count];
        for (int i = 0; i < wires.Length; i++)
            wires[i] = Rows[i].ToConnection();

        _pending = wires;
        _ticks = 0;
        _apply(_nodeId, wires);
    }

    // Patched, never replaced. Assigning a fresh collection per publish would
    // reset scroll and destroy a half-typed value at the publish rate, which is
    // the same reason the property rows above are patched.
    private void SyncRows(IReadOnlyList<EntityConnectionInfo> wires)
    {
        bool countChanged = Rows.Count != wires.Count;

        while (Rows.Count > wires.Count)
            Rows.RemoveAt(Rows.Count - 1);

        while (Rows.Count < wires.Count)
            Rows.Add(new ConnectionRowModel(Post));

        for (int i = 0; i < wires.Count; i++)
            Rows[i].Refresh(wires[i], _outputs);

        if (countChanged)
            Raise(nameof(IsEmpty));
    }

    private static bool Matches(IReadOnlyList<EntityConnectionInfo> reported, EntityConnection[] wanted)
    {
        if (reported.Count != wanted.Length)
            return false;

        for (int i = 0; i < wanted.Length; i++)
        {
            if (reported[i].Wire != wanted[i])
                return false;
        }

        return true;
    }
}
