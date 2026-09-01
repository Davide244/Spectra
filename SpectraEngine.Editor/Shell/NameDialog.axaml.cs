using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System.IO;
using System.Linq;

namespace SpectraEngine.Editor.Shell;

/// <summary>
/// Asks for one name: a new map, a new project, a "save as".
/// </summary>
/// <remarks>
/// <para>
/// <b>Hand-built rather than a save-file picker, because a map is a
/// FOLDER.</b> The platform save dialogs name files, and pointing one at a
/// directory bundle means either lying about what is being created or relying
/// on whether a given backend happens to touch the path it returns. Choosing
/// the parent folder and typing a name is two plain steps that mean exactly
/// what they say.
/// </para>
/// <para>
/// <b>The name is validated here rather than at the filesystem</b>, because the
/// failure it prevents is not an exception: a name carrying a separator would
/// quietly create the bundle somewhere else entirely.
/// </para>
/// </remarks>
public partial class NameDialog : Window
{
    public NameDialog()
    {
        InitializeComponent();
        Opened += (_, _) => Input.Focus();
        Opened += (_, _) => DarkCaption.Apply(this);
    }

    /// <summary>
    /// Shows the dialog and returns the trimmed name, or null when cancelled.
    /// </summary>
    public static System.Threading.Tasks.Task<string?> AskAsync(
        Window owner, string title, string prompt, string suggested)
    {
        var dialog = new NameDialog { Title = title };
        dialog.PromptText.Text = prompt;
        dialog.Input.Text = suggested;
        dialog.Input.SelectAll();
        return dialog.ShowDialog<string?>(owner);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter accepts and Escape cancels, because a one-field dialog that
        // makes you reach for the mouse is a dialog people resent.
        if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(null); e.Handled = true; }
    }

    private void OnAccept(object? sender, RoutedEventArgs e) => Accept();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void Accept()
    {
        string name = (Input.Text ?? string.Empty).Trim();

        if (name.Length == 0)
        {
            Reject("A name is needed.");
            return;
        }

        // Separators first and by name: a name containing one would create the
        // bundle in a directory the user never chose, which is the one failure
        // here that is silent rather than loud.
        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
        {
            Reject("A name cannot contain a path separator.");
            return;
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        if (name.Any(invalid.Contains))
        {
            Reject("That name contains characters a file name cannot carry.");
            return;
        }

        Close(name);
    }

    private void Reject(string reason)
    {
        ErrorText.Text = reason;
        ErrorText.IsVisible = true;
        Input.Focus();
    }
}
