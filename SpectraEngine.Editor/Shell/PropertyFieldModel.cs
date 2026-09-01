using SpectraEngine.Core.Inspection;
using System;
using System.Globalization;

namespace SpectraEngine.Editor.Shell;

/// <summary>
/// One editable cell: a whole row for a scalar, or one axis of a vector.
/// </summary>
/// <remarks>
/// <para>
/// <b>A focused field belongs to the person typing in it, and nothing else may
/// write to it.</b> Rows refresh from a snapshot about thirty times a second,
/// and a gizmo drag republishes the position on every one of them. A field that
/// took each refresh would delete characters out from under somebody halfway
/// through typing a number, which reads as a broken keyboard rather than as a
/// panel doing what it was told.
/// </para>
/// <para>
/// <b>The commit points are Enter and losing focus.</b> Committing per
/// keystroke would push an undo entry per character and would apply "1" on the
/// way to typing "10". Escape reverts, and it has to exist precisely because
/// blur commits: without it there would be no way to abandon a half-typed value
/// once the field has it.
/// </para>
/// <para>
/// <b>Text that will not parse reverts rather than sticking.</b> The alternative
/// is a field left holding something the scene does not contain, which then
/// disagrees with the viewport until somebody notices.
/// </para>
/// </remarks>
public sealed class PropertyFieldModel : ObservableObject
{
    private readonly Action<PropertyFieldModel, string> _commit;
    private string _text = string.Empty;
    private string _live = string.Empty;
    private bool _isMixed;
    private bool _isEditing;
    private bool _isScrubbing;

    internal PropertyFieldModel(
        PropertyId id, PropertyAxes axis, string label, Action<PropertyFieldModel, string> commit)
    {
        Id = id;
        Axis = axis;
        Label = label;
        _commit = commit;
    }

    /// <summary>Which property this cell belongs to.</summary>
    public PropertyId Id { get; }

    /// <summary>Which axis this cell writes, or <see cref="PropertyAxes.All"/> for a scalar.</summary>
    public PropertyAxes Axis { get; }

    /// <summary>The per-axis label (x, y, z), or empty for a scalar.</summary>
    public string Label { get; }

    /// <summary>Whether this cell has an axis letter to show.</summary>
    public bool HasLabel => Label.Length > 0;

    // The three axis flags exist because a XAML class binding cannot compare
    // strings, and the axis letter has to wear the same colour its arrow wears
    // in the viewport.

    /// <summary>Whether this cell edits x.</summary>
    public bool IsX => Axis == PropertyAxes.X;

    /// <summary>Whether this cell edits y.</summary>
    public bool IsY => Axis == PropertyAxes.Y;

    /// <summary>Whether this cell edits z.</summary>
    public bool IsZ => Axis == PropertyAxes.Z;

    /// <summary>
    /// The unit to print inside a scalar cell, or empty.
    /// </summary>
    /// <remarks>
    /// Copied down from the row rather than read up from it: the cell's
    /// template binds against this model, and reaching back to a parent
    /// DataContext from inside a nested ItemsControl is the kind of binding
    /// that resolves to nothing and reports nothing when a template moves.
    /// </remarks>
    public string Unit { get; internal set; } = string.Empty;

    /// <summary>Whether there is a unit to print.</summary>
    public bool HasUnit => Unit.Length > 0;

    /// <summary>What the box shows.</summary>
    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }

    /// <summary>
    /// Whether the selection disagrees about this cell, so the box shows
    /// nothing rather than one node's value.
    /// </summary>
    /// <remarks>
    /// Blank rather than the first node's number, because a number sitting in a
    /// mixed field is a number somebody will read as the answer. The placeholder
    /// says what it is instead.
    /// </remarks>
    public bool IsMixed
    {
        get => _isMixed;
        private set
        {
            if (Set(ref _isMixed, value))
                Raise(nameof(Placeholder));
        }
    }

    /// <summary>Shown in an empty mixed box.</summary>
    public string Placeholder => _isMixed ? "mixed" : string.Empty;

    /// <summary>Whether somebody is typing here right now.</summary>
    public bool IsEditing => _isEditing;

    /// <summary>Takes a fresh value, unless this cell is being edited.</summary>
    public void Refresh(string live, bool mixed)
    {
        _live = live;
        IsMixed = mixed;

        // The guard, and the whole reason this class exists.
        //
        // TWO flags, not one. A drag ends by clearing the guard on every cell
        // of its row, because a vector drag writes all three - and if that were
        // the same flag typing uses, a cell the user had typed into and not yet
        // committed would be handed back to the refresh by a drag on its
        // NEIGHBOUR, and the next publish would silently replace what they
        // typed. The two states are genuinely independent: a pointer capture
        // does not move keyboard focus.
        if (_isEditing || _isScrubbing)
            return;

        Text = mixed ? string.Empty : live;
    }

    /// <summary>The box gained focus: refreshes stop landing here.</summary>
    public void BeginEdit() => _isEditing = true;

    /// <summary>
    /// A drag across this cell's handle has started: refreshes stop landing
    /// here until <see cref="EndScrub"/>.
    /// </summary>
    /// <remarks>
    /// <b>The same guard typing uses, for the same reason.</b> A drag writes
    /// absolute values several times faster than the engine publishes, so
    /// without it every refresh would put a value one or two publishes stale
    /// back into the box and the number under the cursor would jitter
    /// backwards while the object moved forwards.
    /// </remarks>
    public void BeginScrub() => _isScrubbing = true;

    /// <summary>Shows a value written by a drag, without committing anything.</summary>
    public void SetScrubText(string text)
    {
        _live = text;
        Text = text;
    }

    /// <summary>The drag ended: refreshes resume, unless somebody is typing.</summary>
    public void EndScrub() => _isScrubbing = false;

    /// <summary>
    /// Enter, or focus lost. Applies the value and hands the cell back to the
    /// refresh.
    /// </summary>
    public void Commit()
    {
        if (!_isEditing)
            return;

        _isEditing = false;
        string typed = Text.Trim();

        // Nothing typed into a mixed field means "leave them all alone", which
        // is what an empty box already showed.
        if (typed.Length == 0)
        {
            Revert();
            return;
        }

        if (string.Equals(typed, _live, StringComparison.Ordinal) && !_isMixed)
            return;

        _commit(this, typed);
    }

    /// <summary>Escape, or an unusable value: puts the live value back.</summary>
    public void Revert()
    {
        _isEditing = false;
        Text = _isMixed ? string.Empty : _live;
    }

    /// <summary>
    /// Parses a number the way the panel writes one.
    /// </summary>
    /// <remarks>
    /// Invariant culture, matching how the value was formatted into the box. A
    /// culture-sensitive parse would read the panel's own "1.5" as fifteen
    /// wherever a comma is the decimal separator.
    /// </remarks>
    public static bool TryParseNumber(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && float.IsFinite(value);

    /// <summary>Formats a number the way the panel reads one back.</summary>
    public static string Format(float value) =>
        MathF.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
}
