using System;
using System.Collections.Generic;

namespace SpectraEngine.Core.Inspection;

/// <summary>
/// The identity of a published row list: which properties they are and in what
/// order, with every value ignored.
/// </summary>
/// <remarks>
/// <para>
/// <b>A row's identity is the PAIR (<see cref="PropertyRow.Id"/>,
/// <see cref="PropertyRow.Key"/>), and comparing the id alone is a silent
/// corruption the moment entities reach the panel.</b> Every keyvalue row an
/// entity produces carries one id, <see cref="PropertyId.EntityKeyvalue"/>, and
/// is told apart only by its key - so two classes whose schemas happen to
/// declare the same NUMBER of properties compare "same shape" on ids alone, the
/// panel keeps the controls it already built, and the refresh then pours one
/// key's value into another key's editor box. Nothing throws and nothing logs:
/// the field simply shows a value belonging to a different property, and a
/// commit writes it there. That is the worst failure an inspector has, which is
/// why the comparison lives in one place with a name rather than as a loop
/// inside whichever panel needed it first.
/// </para>
/// <para>
/// <b>Captured from the PUBLISHED rows, never from the controls that were
/// built.</b> A panel does not build one control per published row - the name
/// and the id are its header rather than a section - so comparing the built
/// rows against the published ones reports a mismatch on every publish and
/// rebuilds the whole panel at the publish rate, resetting scroll and dropping
/// focus as it goes.
/// </para>
/// <para>
/// <b>Values are deliberately absent.</b> A shape that moved when a number
/// moved would rebuild the panel on every frame of a gizmo drag, which is the
/// same defect said the other way round.
/// </para>
/// </remarks>
public sealed class PropertyRowShape
{
    private readonly List<(PropertyId Id, string Key)> _entries = [];

    /// <summary>How many rows this shape was captured from.</summary>
    public int Count => _entries.Count;

    /// <summary>Forgets the captured shape.</summary>
    public void Clear() => _entries.Clear();

    /// <summary>
    /// Appends one row's identity, for a caller building the shape as it walks
    /// the rows it is laying out.
    /// </summary>
    /// <remarks>
    /// A null key is normalised to empty here rather than trusted. Every row
    /// the inspector builds carries one, but <see cref="PropertyRow"/> is a
    /// struct whose fields a caller can leave unset, and a null slipping into
    /// the list would make the comparison below throw from inside a refresh.
    /// </remarks>
    public void Add(in PropertyRow row) => _entries.Add((row.Id, row.Key ?? ""));

    /// <summary>Replaces the captured shape with <paramref name="rows"/>'.</summary>
    public void CaptureFrom(IReadOnlyList<PropertyRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        _entries.Clear();
        for (int i = 0; i < rows.Count; i++)
            Add(rows[i]);
    }

    /// <summary>
    /// Whether <paramref name="rows"/> are the same properties, in the same
    /// order, as the captured shape.
    /// </summary>
    public bool Matches(IReadOnlyList<PropertyRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (_entries.Count != rows.Count)
            return false;

        for (int i = 0; i < rows.Count; i++)
        {
            (PropertyId Id, string Key) entry = _entries[i];
            if (entry.Id != rows[i].Id)
                return false;

            // The half that is easy to leave out, and whose absence is silent:
            // see the remarks above for what an id-only comparison does to two
            // entity classes whose schemas are the same length.
            if (!string.Equals(entry.Key, rows[i].Key ?? "", StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}
