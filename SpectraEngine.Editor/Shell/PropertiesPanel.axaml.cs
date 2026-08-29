using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;

namespace SpectraEngine.Editor.Shell;

/// <summary>
/// The inspector panel. Its three handlers are the whole commit policy: a
/// focused field stops taking refreshes, Enter and losing focus apply it, and
/// Escape puts the live value back.
/// </summary>
/// <remarks>
/// Escape has to exist precisely BECAUSE blur commits, or there would be no
/// way to abandon a half-typed value once a field is holding it. The panel
/// raises <see cref="EscapePressed"/> instead of focusing the viewport
/// itself, because which control deserves focus next is the window's answer —
/// the panel does not know a viewport exists.
/// </remarks>
public partial class PropertiesPanel : UserControl
{
    /// <summary>Escape ended an edit; the host should take focus back.</summary>
    public event Action? EscapePressed;

    public PropertiesPanel()
    {
        InitializeComponent();
    }

    private void OnPropertyFieldFocused(object? sender, FocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: PropertyFieldModel field })
            field.BeginEdit();
    }

    private void OnPropertyFieldBlurred(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: PropertyFieldModel field })
            field.Commit();
    }

    private void OnPropertyFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: PropertyFieldModel field })
            return;

        switch (e.Key)
        {
            case Key.Enter:
                field.Commit();
                // Focus stays put so a run of edits can be typed and confirmed
                // without reaching for the mouse, but the field is no longer
                // being edited, so the next snapshot may correct it.
                field.BeginEdit();
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
        }
    }
}
