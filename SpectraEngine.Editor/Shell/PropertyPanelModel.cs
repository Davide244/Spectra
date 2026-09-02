using Avalonia.Media;
using SpectraEngine.Core.Inspection;
using SpectraEngine.Editing.Commands;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;

namespace SpectraEngine.Editor.Shell;

/// <summary>One row of the property panel: a label, a unit, and its editor.</summary>
public sealed class PropertyRowModel : ObservableObject
{
    private bool _isPartial;
    private int _presentCount;
    private int _selectionCount;
    private bool _flag;
    private string _choice = string.Empty;
    private bool _applyingRefresh;
    private Vector3 _color;
    private string _hex = string.Empty;
    private IBrush _swatch = Brushes.Transparent;

    internal PropertyRowModel(PropertyRow row, Action<PropertyEdit> apply)
    {
        Id = row.Id;
        Group = row.Group;
        Name = row.Name;
        Kind = row.Kind;
        Unit = row.Unit ?? string.Empty;
        Choices = row.Choices;
        Apply = apply;

        Fields = Kind switch
        {
            // A colour is NOT three numbers to a person, so it does not get
            // three number cells - it gets ONE, holding a hex string, and the
            // swatch beside it is a readout. It has to be a real field rather
            // than a bound string, because the panel's commit contract lives in
            // PropertyFieldModel: a hex value parsed on every keystroke can
            // never be assembled, since "#8", "#80" and "#808" are all
            // unreadable and each one reverts the box to the last good colour.
            PropertyKind.Color =>
                [new PropertyFieldModel(Id, PropertyAxes.All, string.Empty, CommitField)],

            PropertyKind.Vector3 =>
            [
                new PropertyFieldModel(Id, PropertyAxes.X, "x", CommitField),
                new PropertyFieldModel(Id, PropertyAxes.Y, "y", CommitField),
                new PropertyFieldModel(Id, PropertyAxes.Z, "z", CommitField),
            ],
            PropertyKind.Number or PropertyKind.Text =>
                [new PropertyFieldModel(Id, PropertyAxes.All, string.Empty, CommitField)
                    { Unit = row.Unit ?? string.Empty }],
            _ => [],
        };
    }

    public PropertyId Id { get; }

    /// <summary>
    /// This row's index in the published list.
    /// </summary>
    /// <remarks>
    /// The panel does not render every published row - the name and the id
    /// become its header - so the model's rows and the snapshot's rows are no
    /// longer index-for-index. Carrying the source index refreshes in one pass
    /// and allocates nothing; the obvious alternative, filtering the published
    /// list into a fresh one, allocates at the publish rate for a panel that
    /// exists to avoid exactly that.
    /// </remarks>
    internal int SourceIndex { get; set; }

    public string Group { get; }
    public string Name { get; }
    public PropertyKind Kind { get; }
    public IReadOnlyList<string> Choices { get; }
    public IReadOnlyList<PropertyFieldModel> Fields { get; }

    /// <summary>The unit the value is measured in, or empty.</summary>
    public string Unit { get; }

    /// <summary>Whether there is a unit to print beside the label.</summary>
    public bool HasUnit => Unit.Length > 0;

    internal Action<PropertyEdit> Apply { get; }

    /// <summary>
    /// A three-number value, which the panel lays out over two lines.
    /// </summary>
    /// <remarks>
    /// <b>Two lines, because one does not fit.</b> The panel is 300px wide by
    /// default; a fixed label column plus three cells side by side left about
    /// 31px of interior per cell, which is five characters of monospace - so
    /// "-125.5" could not be displayed at all, in a panel whose entire job is
    /// displaying positions. Putting the label on its own line gives each cell
    /// about 86px and costs one row of height on three rows.
    /// </remarks>
    public bool IsVector => Kind == PropertyKind.Vector3;

    /// <summary>A colour, shown as a swatch and a hex value.</summary>
    public bool IsColor => Kind == PropertyKind.Color;

    /// <summary>A one-cell value that fits beside its label.</summary>
    public bool IsScalar => Kind is PropertyKind.Number or PropertyKind.Text;

    /// <summary>The starting value of each cell, captured when a drag begins.</summary>
    /// <remarks>
    /// <b>Per cell, because a drag on the row's LABEL is a uniform delta and
    /// not one shared number.</b> Seeding every axis from x's value and writing
    /// it back absolutely does not offset y and z, it overwrites them: a
    /// position of (10, 2, -5) becomes (10.02, 10.02, 10.02) on the first
    /// pointer move, and a brush of any shape becomes a cube. The commands are
    /// absolute by design, so the delta has to be reconstructed here.
    /// </remarks>
    internal float[] ScrubStarts { get; } = new float[3];

    public bool IsBoolean => Kind == PropertyKind.Boolean;
    public bool IsChoice => Kind == PropertyKind.Choice;
    public bool IsReadOnly => Kind == PropertyKind.ReadOnlyText;

    /// <summary>Whether this row's label is a drag handle for its value.</summary>
    public bool IsScrubbable => Kind is PropertyKind.Number or PropertyKind.Vector3;

    /// <summary>The value, for a read-only row.</summary>
    public string ReadOnlyText { get; private set; } = string.Empty;

    /// <summary>
    /// How much one pixel of horizontal drag is worth, in the value's own unit.
    /// </summary>
    /// <remarks>
    /// <b>Per property, because the units are not comparable.</b> A degree and
    /// a world unit are different sizes of thing, and one shared rate would
    /// make rotation unusably twitchy or position unusably slow. The numbers
    /// are chosen so a comfortable 200px drag covers a useful range: four world
    /// units of position, fifty degrees of rotation, one whole doubling of
    /// scale.
    /// </remarks>
    public float ScrubStep => Id switch
    {
        PropertyId.Rotation => 0.25f,
        PropertyId.Scale => 0.005f,
        PropertyId.LightIntensity => 0.05f,
        PropertyId.LightRange => 0.05f,
        _ => 0.02f,
    };

    /// <summary>How much one arrow-key press is worth.</summary>
    /// <remarks>
    /// Bigger than a pixel of drag, because a key press is a deliberate single
    /// step and a user pressing Up expects to see the object move.
    /// </remarks>
    public float KeyStep => Id switch
    {
        PropertyId.Rotation => 5f,
        PropertyId.Scale => 0.1f,
        _ => 1f,
    };

    /// <summary>The colour, as a brush for the swatch.</summary>
    public IBrush Swatch
    {
        get => _swatch;
        private set => Set(ref _swatch, value);
    }

    /// <summary>
    /// The colour as an sRGB hex string, for the swatch's tooltip.
    /// </summary>
    /// <remarks>
    /// <b>The stored value is LINEAR and the edited one is sRGB.</b> A light's
    /// colour is a quantity of light, and the panel used to show it as three
    /// linear floats under a label reading "Color (linear)" - "1, 0.9114,
    /// 0.7484" for a warm white, which is a correct description of the storage
    /// and tells a person nothing about the colour. The conversion happens
    /// here, at the edge, exactly as it does on the way into a texture.
    /// <para>
    /// Read-only: what the user edits is <c>Fields[0]</c>, on the same commit
    /// contract as every other cell.
    /// </para>
    /// </remarks>
    public string Hex
    {
        get => _hex;
        private set => Set(ref _hex, value);
    }

    /// <summary>Whether the selection disagrees about this row's value.</summary>
    public bool IsPartial
    {
        get => _isPartial;
        private set
        {
            if (Set(ref _isPartial, value))
                Raise(nameof(PartialLabel));
        }
    }

    /// <summary>
    /// "3 of 5" when the property is unique to part of the selection.
    /// </summary>
    /// <remarks>
    /// Shown rather than hidden, because editing such a row is still a bulk
    /// edit and the user is entitled to know how many objects it will reach.
    /// </remarks>
    public string PartialLabel => _isPartial
        ? string.Format(CultureInfo.InvariantCulture, "{0} of {1}", _presentCount, _selectionCount)
        : string.Empty;

    /// <summary>The value, for a checkbox row.</summary>
    public bool Flag
    {
        get => _flag;
        set
        {
            if (!Set(ref _flag, value) || _applyingRefresh)
                return;

            // A checkbox has no typing to finish, so the click IS the commit.
            Apply(new PropertyEdit { Id = Id, Flag = value });
        }
    }

    /// <summary>The value, for a choice row.</summary>
    public string Choice
    {
        get => _choice;
        set
        {
            if (!Set(ref _choice, value) || _applyingRefresh || string.IsNullOrEmpty(value))
                return;

            Apply(new PropertyEdit { Id = Id, Text = value });
        }
    }

    /// <summary>Takes a fresh value from a published row.</summary>
    internal void Refresh(PropertyRow row)
    {
        // The IsPartial setter raises PartialLabel when the FLAG flips; this
        // raise covers the label's other inputs, and only when they moved —
        // Refresh runs for every row on every pump, and an unconditional raise
        // here is binding churn for a panel that has not changed.
        bool partialCountsChanged =
            _presentCount != row.PresentCount || _selectionCount != row.SelectionCount;
        IsPartial = row.IsPartial;
        _presentCount = row.PresentCount;
        _selectionCount = row.SelectionCount;
        if (partialCountsChanged)
            Raise(nameof(PartialLabel));

        switch (Kind)
        {
            case PropertyKind.Vector3:
                Fields[0].Refresh(PropertyFieldModel.Format(row.Vector.X), row.MixedAxes.HasFlag(PropertyAxes.X));
                Fields[1].Refresh(PropertyFieldModel.Format(row.Vector.Y), row.MixedAxes.HasFlag(PropertyAxes.Y));
                Fields[2].Refresh(PropertyFieldModel.Format(row.Vector.Z), row.MixedAxes.HasFlag(PropertyAxes.Z));
                break;

            case PropertyKind.Color:
                RefreshColor(row.IsMixed ? new Vector3(float.NaN) : row.Vector);
                break;

            case PropertyKind.Number:
                Fields[0].Refresh(PropertyFieldModel.Format(row.Number), row.IsMixed);
                break;

            case PropertyKind.Text:
                Fields[0].Refresh(row.Text, row.IsMixed);
                break;

            case PropertyKind.Boolean:
                // Guarded, or assigning the refreshed value would look like a
                // click and apply itself straight back to the scene.
                _applyingRefresh = true;
                Flag = row.Flag;
                _applyingRefresh = false;
                break;

            case PropertyKind.Choice:
                _applyingRefresh = true;
                Choice = row.IsMixed ? string.Empty : row.Text;
                _applyingRefresh = false;
                break;

            default:
                string readOnly = row.IsMixed ? "(multiple)" : row.Text;
                if (!string.Equals(ReadOnlyText, readOnly, StringComparison.Ordinal))
                {
                    ReadOnlyText = readOnly;
                    Raise(nameof(ReadOnlyText));
                }
                break;
        }
    }

    private void RefreshColor(Vector3 linear)
    {
        bool mixed = float.IsNaN(linear.X);

        // Only the SWATCH is skipped for an unchanged colour: Set compares
        // brushes by REFERENCE, so an unguarded refresh allocates a fresh
        // SolidColorBrush and re-renders it on every pump for as long as a
        // light is selected. NaN never equals itself, which is why mixed is
        // compared as a state rather than through the vector.
        bool sameValue = _colorRefreshed &&
            (mixed ? float.IsNaN(_color.X) : !float.IsNaN(_color.X) && _color == linear);

        _colorRefreshed = true;
        _color = linear;
        Hex = mixed ? string.Empty : ToHex(linear);

        if (!sameValue)
        {
            Swatch = mixed
                ? Brushes.Transparent
                : new SolidColorBrush(Color.FromRgb(ToByte(linear.X), ToByte(linear.Y), ToByte(linear.Z)));
        }

        // The FIELD reconciles every pump like every other kind's cells do,
        // never under the value guard: per-pump reconciliation is what
        // visually reverts a refused or no-op commit. A hex typed while play
        // mode owns the scene is refused by the editor, and skipping this
        // would leave the box showing the refused value beside a swatch of
        // the real colour, indefinitely. The field's own equality guard makes
        // the unchanged case free, and a focused box is left alone exactly as
        // every other cell's is.
        Fields[0].Refresh(Hex, mixed);
    }

    // Whether RefreshColor has run at all: the skip must not swallow the FIRST
    // refresh, or a genuinely black light would never grow a swatch.
    private bool _colorRefreshed;

    /// <summary>
    /// Writes one absolute value while a drag is in flight.
    /// </summary>
    /// <remarks>
    /// <b>Absolute, like every command in this editor</b>, which is what lets a
    /// drag emit one of these per pointer move and still undo in one step: the
    /// host holds a transaction open around the gesture, and the coalescing
    /// commands inside it keep the value the drag STARTED from.
    /// </remarks>
    internal void ScrubTo(PropertyFieldModel field, float value)
    {
        field.SetScrubText(PropertyFieldModel.Format(value));

        if (Kind == PropertyKind.Number)
        {
            Apply(new PropertyEdit { Id = Id, Number = value });
            return;
        }

        Apply(new PropertyEdit
        {
            Id = Id,
            Axes = field.Axis,
            Vector = new Vector3(value, value, value),
        });
    }

    private void CommitField(PropertyFieldModel field, string typed)
    {
        switch (Kind)
        {
            case PropertyKind.Text:
                Apply(new PropertyEdit { Id = Id, Text = typed });
                break;

            case PropertyKind.Number:
                if (!PropertyFieldModel.TryParseNumber(typed, out float number)) { field.Revert(); return; }
                Apply(new PropertyEdit { Id = Id, Number = number });
                break;

            case PropertyKind.Color:
                if (!TryParseHex(typed, out Vector3 rgb)) { field.Revert(); return; }
                Apply(new PropertyEdit { Id = Id, Axes = PropertyAxes.All, Vector = rgb });
                break;

            case PropertyKind.Vector3:
                if (!PropertyFieldModel.TryParseNumber(typed, out float component)) { field.Revert(); return; }

                // One axis per cell, so typing into y is a bulk edit that leaves
                // every node's own x and z alone.
                Apply(new PropertyEdit
                {
                    Id = Id,
                    Axes = field.Axis,
                    Vector = new Vector3(component, component, component),
                });
                break;
        }
    }

    // ─── Colour space ────────────────────────────────────
    //
    // The engine stores linear light and a person writes sRGB. These are the
    // same two curves the texture path uses; they are written out rather than
    // shared with it because that path applies them in hardware, on a sampler,
    // and has no callable form.

    private static byte ToByte(float linear)
    {
        float v = float.IsFinite(linear) ? Math.Clamp(linear, 0f, 1f) : 0f;
        float s = v <= 0.0031308f ? v * 12.92f : (1.055f * MathF.Pow(v, 1f / 2.4f)) - 0.055f;
        return (byte)Math.Clamp(MathF.Round(s * 255f), 0f, 255f);
    }

    private static float FromByte(byte value)
    {
        float s = value / 255f;
        return s <= 0.04045f ? s / 12.92f : MathF.Pow((s + 0.055f) / 1.055f, 2.4f);
    }

    private static string ToHex(Vector3 linear) =>
        $"#{ToByte(linear.X):X2}{ToByte(linear.Y):X2}{ToByte(linear.Z):X2}";

    /// <summary>Reads "#RRGGBB" or "RRGGBB" into linear RGB.</summary>
    internal static bool TryParseHex(string? text, out Vector3 linear)
    {
        linear = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.Length > 0 && span[0] == '#')
            span = span[1..];

        if (span.Length != 6)
            return false;

        if (!byte.TryParse(span[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r)
            || !byte.TryParse(span.Slice(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g)
            || !byte.TryParse(span.Slice(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            return false;
        }

        linear = new Vector3(FromByte(r), FromByte(g), FromByte(b));
        return true;
    }
}

/// <summary>
/// The property panel: the selection's rows, grouped, patched from each
/// published snapshot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Patched, never replaced.</b> Assigning a fresh collection every snapshot
/// would reset scroll, drop focus and destroy whatever half-typed value was in
/// a box thirty times a second. Rows are matched by
/// <see cref="PropertyId"/> and reused; only a change in which properties exist
/// rebuilds anything.
/// </para>
/// <para>
/// <b>Sections come from a run of equal groups</b>, which is safe because the
/// inspector emits each group as one contiguous run and merges a multi-selection
/// in a deterministic order rather than in click order.
/// </para>
/// </remarks>
public sealed class PropertyPanelModel : ObservableObject
{
    private readonly Action<PropertyEdit> _apply;
    private readonly Action<string> _beginGesture;
    private readonly Action<bool> _endGesture;
    private readonly List<PropertyId> _shape = [];
    private int _selectionCount;
    private string _headerKind = string.Empty;
    private bool _isMultiple;

    public PropertyPanelModel(
        Action<PropertyEdit> apply, Action<string> beginGesture, Action<bool> endGesture)
    {
        _apply = apply;
        _beginGesture = beginGesture;
        _endGesture = endGesture;

        NameField = new PropertyFieldModel(
            PropertyId.NodeName, PropertyAxes.All, string.Empty,
            (field, typed) => _apply(new PropertyEdit { Id = PropertyId.NodeName, Text = typed }));
    }

    /// <summary>
    /// The node's name, edited in the panel's own header.
    /// </summary>
    /// <remarks>
    /// <b>The header IS the name field</b>, rather than the header naming the
    /// node and a "Node" section below it holding a second copy. That section
    /// used to carry exactly two rows: the name, and a GUID rendered as
    /// permanently truncated text - so the two most valuable rows in the panel
    /// were a duplicate and an identifier nobody can use. The id is still
    /// published and is still what every command addresses; it simply is not a
    /// thing to look at.
    /// </remarks>
    public PropertyFieldModel NameField { get; }

    /// <summary>The rows, in display order, with group headers folded in.</summary>
    public ObservableCollection<PropertyGroupModel> Groups { get; } = [];

    /// <summary>Whether anything is selected at all.</summary>
    public bool HasSelection => _selectionCount > 0;

    /// <summary>Whether the header has a kind to print.</summary>
    public bool HasKind => _headerKind.Length > 0;

    /// <summary>
    /// Whether more than one object is selected, so the header shows a count
    /// instead of an editable name.
    /// </summary>
    /// <remarks>
    /// <b>The panel names its subject.</b> A column of fields with "1 selected"
    /// over it says how many things are being edited and not WHICH, which for a
    /// scene of 253 similarly-named nodes is the only question that matters. A
    /// multi-selection is the one case where there is no single name to show,
    /// and it says so.
    /// </remarks>
    public bool IsMultiple
    {
        get => _isMultiple;
        private set
        {
            if (Set(ref _isMultiple, value))
                Raise(nameof(MultipleLabel));
        }
    }

    /// <summary>"3 objects", for a multi-selection header.</summary>
    public string MultipleLabel => $"{_selectionCount} objects";

    /// <summary>
    /// What kind of thing it is, derived from which sections exist.
    /// </summary>
    /// <remarks>
    /// Derived rather than published: a node carrying a brush grows a Brush
    /// section, and the kind and operation rows inside it already say which of
    /// the three brush kinds it is. Asking the engine for a fourth fact that is
    /// implied by three it already sent would be a second source to disagree
    /// with the first.
    /// </remarks>
    public string HeaderKind
    {
        get => _headerKind;
        private set
        {
            if (Set(ref _headerKind, value))
                Raise(nameof(HasKind));
        }
    }

    /// <summary>Opens one history entry to hold a drag across a field.</summary>
    internal void BeginGesture(string name) => _beginGesture(name);

    /// <summary>Closes it, keeping the result or rolling it back.</summary>
    internal void EndGesture(bool commit) => _endGesture(commit);

    /// <summary>Takes one published snapshot's rows.</summary>
    public void Apply(IReadOnlyList<PropertyRow> rows, int selectionCount)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (_selectionCount != selectionCount)
        {
            _selectionCount = selectionCount;
            Raise(nameof(HasSelection));
            // MultipleLabel prints this count; the IsMultiple setter only
            // covers the FLAG flipping, so the count's raise lives here, under
            // the count's own guard.
            Raise(nameof(MultipleLabel));
        }

        if (!SameShape(rows))
            Rebuild(rows);

        foreach (PropertyGroupModel group in Groups)
        {
            foreach (PropertyRowModel row in group.Rows)
                row.Refresh(rows[row.SourceIndex]);
        }

        RefreshHeader(rows, selectionCount);
    }

    private void RefreshHeader(IReadOnlyList<PropertyRow> rows, int selectionCount)
    {
        // Its setter raises MultipleLabel when the flag flips; the count's
        // half of that label is raised by Apply under the count guard.
        IsMultiple = selectionCount > 1;

        if (selectionCount == 0)
        {
            HeaderKind = string.Empty;
            return;
        }

        string name = string.Empty;
        bool nameMixed = false;
        string brushKind = string.Empty;
        string brushOperation = string.Empty;
        bool hasLight = false;
        bool hasMesh = false;
        bool hasBrush = false;

        foreach (PropertyRow row in rows)
        {
            switch (row.Id)
            {
                case PropertyId.NodeName:
                    name = row.Text;
                    nameMixed = row.IsMixed;
                    break;
                case PropertyId.BrushKind:
                    hasBrush = true;
                    brushKind = row.IsMixed ? string.Empty : row.Text;
                    break;
                case PropertyId.BrushOperation:
                    brushOperation = row.IsMixed ? string.Empty : row.Text;
                    break;
                case PropertyId.LightKind:
                    hasLight = true;
                    break;
                case PropertyId.MeshModel:
                    hasMesh = true;
                    break;
            }
        }

        NameField.Refresh(name, nameMixed);

        HeaderKind = DescribeKind(hasBrush, brushKind, brushOperation, hasLight, hasMesh);
    }

    private static string DescribeKind(
        bool hasBrush, string kind, string operation, bool hasLight, bool hasMesh)
    {
        if (hasBrush)
        {
            // Subtractive outranks the kind, exactly as it does in the tree: a
            // cut renders nothing at all, so it is the fact worth leading with.
            if (operation.Equals("Subtractive", StringComparison.OrdinalIgnoreCase))
                return "Cut";

            return kind switch
            {
                "World" => "Block",
                "Part" => "Part",
                _ => "Brush",
            };
        }

        if (hasLight) return "Light";
        if (hasMesh) return "Mesh";
        return string.Empty;
    }

    /// <summary>
    /// Whether the published rows are the same properties, in the same order,
    /// as the ones already built.
    /// </summary>
    /// <remarks>
    /// Compared against the PUBLISHED shape rather than against the built rows,
    /// because the panel does not build one row per published row: comparing
    /// the two directly would report a mismatch on every publish and rebuild
    /// the whole panel thirty times a second, resetting scroll and dropping
    /// focus as it went.
    /// </remarks>
    private bool SameShape(IReadOnlyList<PropertyRow> rows)
    {
        if (_shape.Count != rows.Count)
            return false;

        for (int i = 0; i < rows.Count; i++)
        {
            if (_shape[i] != rows[i].Id)
                return false;
        }

        return true;
    }

    private void Rebuild(IReadOnlyList<PropertyRow> rows)
    {
        Groups.Clear();
        _shape.Clear();

        PropertyGroupModel? current = null;
        for (int i = 0; i < rows.Count; i++)
        {
            PropertyRow row = rows[i];
            _shape.Add(row.Id);

            // The name and the id are the panel's HEADER, not a section. See
            // NameField for why.
            if (row.Id is PropertyId.NodeId or PropertyId.NodeName)
                continue;

            if (current is null || current.Name != row.Group)
            {
                current = new PropertyGroupModel(row.Group);
                Groups.Add(current);
            }

            current.Rows.Add(new PropertyRowModel(row, _apply) { SourceIndex = i });
        }
    }
}

/// <summary>One section of the panel, named after the payload it came from.</summary>
public sealed class PropertyGroupModel
{
    public PropertyGroupModel(string name) => Name = name;

    /// <summary>The section's heading, or empty for the leading unheaded run.</summary>
    public string Name { get; }

    /// <summary>Whether this section prints a heading at all.</summary>
    public bool HasName => Name.Length > 0;

    /// <summary>
    /// The heading as it is printed: upper case, because the style that sets it
    /// letter-spaces at 10px, and lower case at that size and tracking reads as
    /// damaged rather than as small.
    /// </summary>
    /// <remarks>
    /// <b>Retired, and the heading uses <see cref="Name"/> directly.</b> A
    /// 10px letter-spaced uppercase label is the marketing eyebrow, not a tool's
    /// section heading - every editor in this category sets them at body size
    /// and lets weight carry the emphasis. Uppercase is also measurably slower
    /// to read, because it destroys the word shape a reader recognises before
    /// they have read any letters.
    /// <para>
    /// Kept as a member rather than deleted because the panel's own tests name
    /// it, and it costs one expression.
    /// </para>
    /// </remarks>
    public string UpperName => Name.ToUpperInvariant();

    public ObservableCollection<PropertyRowModel> Rows { get; } = [];
}
