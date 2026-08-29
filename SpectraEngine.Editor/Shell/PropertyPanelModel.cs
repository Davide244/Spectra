using SpectraEngine.Core.Inspection;
using SpectraEngine.Editing.Commands;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;

namespace SpectraEngine.Editor.Shell;

/// <summary>One row of the property panel: a label, and one to three cells.</summary>
public sealed class PropertyRowModel : ObservableObject
{
    private bool _isPartial;
    private int _presentCount;
    private int _selectionCount;
    private bool _flag;
    private string _choice = string.Empty;
    private bool _applyingRefresh;

    internal PropertyRowModel(PropertyRow row, Action<PropertyEdit> apply)
    {
        Id = row.Id;
        Group = row.Group;
        Name = row.Name;
        Kind = row.Kind;
        Choices = row.Choices;
        Apply = apply;

        Fields = Kind switch
        {
            PropertyKind.Vector3 or PropertyKind.Color =>
            [
                new PropertyFieldModel(Id, PropertyAxes.X, "x", CommitField),
                new PropertyFieldModel(Id, PropertyAxes.Y, "y", CommitField),
                new PropertyFieldModel(Id, PropertyAxes.Z, "z", CommitField),
            ],
            PropertyKind.Number or PropertyKind.Text =>
                [new PropertyFieldModel(Id, PropertyAxes.All, string.Empty, CommitField)],
            _ => [],
        };
    }

    public PropertyId Id { get; }
    public string Group { get; }
    public string Name { get; }
    public PropertyKind Kind { get; }
    public IReadOnlyList<string> Choices { get; }
    public IReadOnlyList<PropertyFieldModel> Fields { get; }

    internal Action<PropertyEdit> Apply { get; }

    public bool HasFields => Fields.Count > 0;
    public bool IsBoolean => Kind == PropertyKind.Boolean;
    public bool IsChoice => Kind == PropertyKind.Choice;
    public bool IsReadOnly => Kind == PropertyKind.ReadOnlyText;

    /// <summary>The value, for a read-only row.</summary>
    public string ReadOnlyText { get; private set; } = string.Empty;

    /// <summary>Whether only part of the selection carries this property.</summary>
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
        IsPartial = row.IsPartial;
        _presentCount = row.PresentCount;
        _selectionCount = row.SelectionCount;
        Raise(nameof(PartialLabel));

        switch (Kind)
        {
            case PropertyKind.Vector3 or PropertyKind.Color:
                Fields[0].Refresh(PropertyFieldModel.Format(row.Vector.X), row.MixedAxes.HasFlag(PropertyAxes.X));
                Fields[1].Refresh(PropertyFieldModel.Format(row.Vector.Y), row.MixedAxes.HasFlag(PropertyAxes.Y));
                Fields[2].Refresh(PropertyFieldModel.Format(row.Vector.Z), row.MixedAxes.HasFlag(PropertyAxes.Z));
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
                ReadOnlyText = row.IsMixed ? "(multiple)" : row.Text;
                Raise(nameof(ReadOnlyText));
                break;
        }
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

            case PropertyKind.Vector3 or PropertyKind.Color:
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
    private int _selectionCount;

    public PropertyPanelModel(Action<PropertyEdit> apply) => _apply = apply;

    /// <summary>The rows, in display order, with group headers folded in.</summary>
    public ObservableCollection<PropertyGroupModel> Groups { get; } = [];

    /// <summary>Whether anything is selected at all.</summary>
    public bool HasSelection => _selectionCount > 0;

    /// <summary>What to say when there is nothing to show.</summary>
    public string EmptyLabel => "Nothing selected";

    /// <summary>Takes one published snapshot's rows.</summary>
    public void Apply(IReadOnlyList<PropertyRow> rows, int selectionCount)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (_selectionCount != selectionCount)
        {
            _selectionCount = selectionCount;
            Raise(nameof(HasSelection));
        }

        if (!SameShape(rows))
            Rebuild(rows);

        int index = 0;
        foreach (PropertyGroupModel group in Groups)
        {
            foreach (PropertyRowModel row in group.Rows)
                row.Refresh(rows[index++]);
        }
    }

    private bool SameShape(IReadOnlyList<PropertyRow> rows)
    {
        int index = 0;
        foreach (PropertyGroupModel group in Groups)
        {
            foreach (PropertyRowModel row in group.Rows)
            {
                if (index >= rows.Count || rows[index].Id != row.Id)
                    return false;
                index++;
            }
        }
        return index == rows.Count;
    }

    private void Rebuild(IReadOnlyList<PropertyRow> rows)
    {
        Groups.Clear();

        PropertyGroupModel? current = null;
        foreach (PropertyRow row in rows)
        {
            if (current is null || current.Name != row.Group)
            {
                current = new PropertyGroupModel(row.Group);
                Groups.Add(current);
            }

            current.Rows.Add(new PropertyRowModel(row, _apply));
        }
    }
}

/// <summary>One section of the panel, named after the payload it came from.</summary>
public sealed class PropertyGroupModel
{
    public PropertyGroupModel(string name) => Name = name;

    public string Name { get; }

    public ObservableCollection<PropertyRowModel> Rows { get; } = [];
}
