using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Threading.Tasks;

namespace SpectraEngine.Editor.Shell;

/// <summary>What the user chose when told their work is unsaved.</summary>
public enum UnsavedChoice
{
    /// <summary>Go back; the gesture that asked is abandoned.</summary>
    Cancel,

    /// <summary>Write the level first, then carry on.</summary>
    Save,

    /// <summary>Carry on and lose the edits.</summary>
    Discard,
}

/// <summary>
/// The unsaved-work prompt: Save, Discard or Cancel.
/// </summary>
/// <remarks>
/// <para>
/// <b>A real top-level window, so it may sit over the viewport.</b> The airspace
/// rule constrains what Avalonia draws inside the main window; a separate
/// window is composited by the OS above the viewport's native child.
/// </para>
/// <para>
/// <b>Three answers, because the situation has three.</b> This replaces a
/// dialog that asked the user to type the word "discard": it offered no way to
/// keep the work, so the correct answer meant cancelling and starting again,
/// and any other text - "yes", an empty field - passed the name validation,
/// failed the equality check and closed the dialog having silently done
/// nothing. Reserve typed confirmation for what cannot be undone; unsaved work
/// has an obvious third answer and the dialog's job is to offer it.
/// </para>
/// </remarks>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
        Opened += (_, _) => DarkCaption.Apply(this);
    }

    /// <summary>
    /// Asks what to do about unsaved edits in <paramref name="document"/>.
    /// </summary>
    /// <param name="what">
    /// What the user was doing, phrased to follow "will discard them" - for
    /// example "opening another project".
    /// </param>
    public static Task<UnsavedChoice> AskAsync(Window owner, string document, string what)
    {
        var dialog = new ConfirmDialog();
        dialog.MessageText.Text = $"Save your changes to {document}?";
        dialog.DetailText.Text = $"If you do not, {what} will discard them.";
        return dialog.ShowDialog<UnsavedChoice>(owner);
    }

    private void OnSaveClicked(object? sender, RoutedEventArgs e) => Close(UnsavedChoice.Save);

    private void OnDiscardClicked(object? sender, RoutedEventArgs e) => Close(UnsavedChoice.Discard);

    // Also what the close button and Escape produce: ShowDialog returns
    // default(UnsavedChoice) when the window is closed without a result, and
    // Cancel is deliberately the zero value so the safe answer is the one a
    // dismissal gives.
    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(UnsavedChoice.Cancel);
}
