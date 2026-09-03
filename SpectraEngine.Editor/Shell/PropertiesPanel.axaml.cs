using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System;

namespace SpectraEngine.Editor.Shell;

/// <summary>
/// The property panel: the selection's editable values, patched from every
/// published snapshot.
/// </summary>
/// <remarks>
/// <para>
/// <b>The commit contract lives here.</b> A focused field stops taking
/// refreshes, Enter and losing focus commit, Escape reverts, and text that will
/// not parse reverts rather than sticking.
/// </para>
/// <para>
/// <b>A number can also be DRAGGED, and that is what makes this an editor's
/// panel rather than a settings dialog.</b> Pressing a row's label - or one
/// axis letter of a vector - and moving sideways writes the value continuously;
/// the host holds one history entry open around the whole gesture, so it undoes
/// in one press. Every rule the gizmos follow applies: the value is recomputed
/// from the grab, a modifier scales the rate, releasing commits and losing the
/// pointer cancels.
/// </para>
/// </remarks>
public partial class PropertiesPanel : UserControl
{
    public PropertiesPanel() => InitializeComponent();

    /// <summary>Raised when Escape ends an edit, so the host can take focus back.</summary>
    public event Action? EscapePressed;

    // ─── The live drag ───────────────────────────────────
    //
    // One at a time, by construction: a pointer capture cannot be held by two
    // controls at once, and the fields the drag writes are addressed through
    // the captured control's own DataContext.

    private PropertyRowModel? _scrubRow;
    private PropertyFieldModel? _scrubField;
    private PropertyPanelModel? _scrubPanel;
    private Point _scrubOrigin;
    private double _scrubStart;
    private double _scrubValue;
    private double _scrubLastX;
    private bool _scrubMoved;

    private void OnPropertyFieldFocused(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: PropertyFieldModel field })
        {
            field.BeginEdit();
            return;
        }

        // The header's name box binds to the panel's own field rather than to a
        // row, because the name IS the header.
        if (sender is TextBox { DataContext: ShellModel { Properties: { } panel } })
            panel.NameField.BeginEdit();
    }

    private void OnPropertyFieldBlurred(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: PropertyFieldModel field })
        {
            field.Commit();
            return;
        }

        if (sender is TextBox { DataContext: ShellModel { Properties: { } panel } })
            panel.NameField.Commit();
    }

    private void OnPropertyFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
            return;

        PropertyFieldModel? field = box.DataContext as PropertyFieldModel
            ?? (box.DataContext as ShellModel)?.Properties?.NameField;

        if (field is null)
            return;

        // Enter commits and stops editing, which leaves the caret in a box the
        // refresh is free to overwrite again. That is correct for a field
        // nobody is touching and wrong the instant they touch it, so the next
        // key press re-arms the guard. Escape is the exception: it hands focus
        // away, so re-arming would leave a guard set on a box nobody is in.
        if (e.Key is not (Key.Enter or Key.Escape) && !field.IsEditing)
            field.BeginEdit();

        switch (e.Key)
        {
            case Key.Enter:
                // Commit and STOP editing. This used to call BeginEdit() again
                // straight afterwards, on the reasoning that focus stays in the
                // box so the edit is still open. The effect was that the field
                // never took another refresh for as long as it kept focus: type
                // a position, press Enter, then drag the object in the viewport,
                // and the box went on showing the number you typed while the
                // object was somewhere else. A field that lies about where an
                // object is is worse than one that loses focus.
                field.Commit();
                e.Handled = true;
                break;

            case Key.Escape:
                field.Revert();
                // Handing focus back is what makes Escape read as "I am done
                // here" rather than leaving the caret in a box that just
                // changed under it.
                EscapePressed?.Invoke();
                e.Handled = true;
                break;

            case Key.Up:
                e.Handled = Nudge(box, field, +1, e.KeyModifiers);
                break;

            case Key.Down:
                e.Handled = Nudge(box, field, -1, e.KeyModifiers);
                break;
        }
    }

    /// <summary>
    /// Steps a numeric field by one increment from the keyboard.
    /// </summary>
    /// <remarks>
    /// <b>Committed as an absolute value read out of the box</b>, not as a
    /// delta applied to the scene: the box is what the user is looking at, the
    /// commands are absolute anyway, and a delta would drift against a value
    /// the viewport is also moving.
    /// </remarks>
    private static bool Nudge(TextBox box, PropertyFieldModel field, int direction, KeyModifiers modifiers)
    {
        if (FindRow(box) is not { IsScrubbable: true } row)
            return false;

        if (!PropertyFieldModel.TryParseNumber(field.Text, out float current))
            return false;

        float step = row.KeyStep * Scale(modifiers);
        field.BeginEdit();
        field.Text = PropertyFieldModel.Format(current + (step * direction));
        field.Commit();
        field.BeginEdit();
        return true;
    }

    // ─── Outputs ─────────────────────────────────────────
    //
    // Add and remove post the WHOLE list, like every other wiring edit: the
    // command carries absolute arrays because a connection has no per-item
    // identity a delta could name. Both are ordinary clicks rather than
    // gestures, so each is one history entry on its own.

    private void OnAddConnection(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShellModel { Properties: { } panel })
            panel.Wiring.Add();
    }

    private void OnRemoveConnection(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ConnectionRowModel row }
            && DataContext is ShellModel { Properties: { } panel })
        {
            panel.Wiring.Remove(row);
        }
    }

    // ─── Drag to change a number ─────────────────────────

    private void OnAxisPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: PropertyFieldModel field } handle)
            return;

        BeginScrub(handle, FindRow(handle), field, e);
    }

    private void OnLabelPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: PropertyRowModel row } handle || !row.IsScrubbable)
            return;

        // A vector's own label drags all three cells together, which is the
        // gesture "make this twice the size" wants. One field is captured for
        // the readout; the edit carries every axis.
        BeginScrub(handle, row, row.Fields.Count > 0 ? row.Fields[0] : null, e);
    }

    private void BeginScrub(
        Control handle, PropertyRowModel? row, PropertyFieldModel? field, PointerPressedEventArgs e)
    {
        // Left button only. Avalonia raises PointerPressed for every button and
        // a capture is per POINTER rather than per button, so without this a
        // right-press on a label - on its way to a context menu - would open a
        // history transaction, capture the pointer, and write positions into the
        // scene for as long as the button was held.
        if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
            return;

        if (row is null || field is null || !row.IsScrubbable)
            return;

        // A mixed cell has no value to drag FROM, and an absolute write would
        // silently collapse the whole selection onto one number. Typing into it
        // is still allowed, because typing is an unambiguous instruction.
        if (!PropertyFieldModel.TryParseNumber(field.Text, out float start))
            return;

        if (DataContext is not ShellModel { Properties: { } panel })
            return;

        _scrubRow = row;
        _scrubField = field;
        _scrubPanel = panel;
        _scrubOrigin = e.GetPosition(this);
        _scrubLastX = _scrubOrigin.X;
        _scrubStart = start;
        _scrubValue = start;
        _scrubMoved = false;

        // Every cell's OWN starting value, because a drag on the row's label
        // moves all three by the same amount rather than to the same number.
        for (int i = 0; i < row.Fields.Count && i < row.ScrubStarts.Length; i++)
        {
            row.ScrubStarts[i] = PropertyFieldModel.TryParseNumber(row.Fields[i].Text, out float v)
                ? v
                : float.NaN;
        }

        foreach (PropertyFieldModel cell in row.Fields)
            cell.BeginScrub();

        panel.BeginGesture(row.Name);
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnScrubMoved(object? sender, PointerEventArgs e)
    {
        if (_scrubRow is not { } row || _scrubField is null || sender is not Control handle)
            return;

        if (!ReferenceEquals(e.Pointer.Captured, handle))
            return;

        double x = e.GetPosition(this).X;
        double dx = x - _scrubLastX;
        _scrubLastX = x;

        // Accumulated through the modifier rather than recomputed from the grab
        // point, so changing the modifier mid-drag changes the RATE from here on
        // instead of retroactively rescaling everything already travelled.
        _scrubValue += dx * row.ScrubStep * Scale(e.KeyModifiers);
        _scrubMoved = true;

        var value = (float)_scrubValue;

        // A vector dragged by its own label moves all three BY THE SAME AMOUNT;
        // dragged by one axis letter it moves that one. Writing `value` to all
        // three would not offset them, it would flatten them onto x's number -
        // the commands are absolute, so the delta is reconstructed from each
        // cell's own captured start.
        if (ReferenceEquals(handle.DataContext, row))
        {
            var delta = (float)(_scrubValue - _scrubStart);
            for (int i = 0; i < row.Fields.Count && i < row.ScrubStarts.Length; i++)
            {
                float from = row.ScrubStarts[i];
                if (float.IsNaN(from))
                    continue;

                row.ScrubTo(row.Fields[i], from + delta);
            }
        }
        else
        {
            row.ScrubTo(_scrubField, value);
        }

        e.Handled = true;
    }

    private void OnScrubReleased(object? sender, PointerReleasedEventArgs e)
    {
        // A press that never travelled is a click, not a drag, and must leave
        // nothing behind: the gesture is cancelled so the history stays clean
        // and the caret can land in the field the user was aiming at.
        EndScrub(commit: _scrubMoved);
        e.Handled = true;
    }

    private void OnScrubLost(object? sender, PointerCaptureLostEventArgs e) => EndScrub(commit: _scrubMoved);

    private void EndScrub(bool commit)
    {
        if (_scrubRow is { } row)
        {
            foreach (PropertyFieldModel cell in row.Fields)
                cell.EndScrub();
        }

        _scrubPanel?.EndGesture(commit);

        _scrubRow = null;
        _scrubField = null;
        _scrubPanel = null;
        _scrubMoved = false;
    }

    /// <summary>
    /// The rate multiplier a modifier asks for: Shift is coarse, Ctrl is fine.
    /// </summary>
    /// <remarks>
    /// The After Effects and Figma convention, which is the one this audience
    /// meets most often outside a game engine.
    /// </remarks>
    private static float Scale(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Shift) ? 10f
        : modifiers.HasFlag(KeyModifiers.Control) ? 0.1f
        : 1f;

    /// <summary>
    /// Walks up to the row a control belongs to.
    /// </summary>
    /// <remarks>
    /// The cells of a vector row are a nested ItemsControl, so a cell's own
    /// DataContext is the field and its row is only reachable through the tree.
    /// Bounded by the ItemsControl that produced the rows, so a control outside
    /// a row returns null instead of walking to the window.
    /// </remarks>
    private static PropertyRowModel? FindRow(Control? from)
    {
        for (Visual? v = from; v is not null; v = v.GetVisualParent())
        {
            if (v is Control { DataContext: PropertyRowModel row })
                return row;
        }

        return null;
    }

}
