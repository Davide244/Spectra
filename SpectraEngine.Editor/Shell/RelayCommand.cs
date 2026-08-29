using System;
using System.Windows.Input;

namespace SpectraEngine.Editor.Shell;

/// <summary>
/// The smallest possible <see cref="ICommand"/>: an action, always executable.
/// </summary>
/// <remarks>
/// Exists for the window's key bindings, which is the one place Avalonia
/// accepts nothing but an <see cref="ICommand"/>. Everything else in the shell
/// stays on plain Click handlers, deliberately: a command layer earning its
/// keep needs enabling state, and the shell's enabling state lives on the
/// bindings' <c>IsEnabled</c> already.
/// </remarks>
public sealed class RelayCommand(Action execute) : ICommand
{
    private readonly Action _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    /// <inheritdoc/>
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    /// <inheritdoc/>
    public bool CanExecute(object? parameter) => true;

    /// <inheritdoc/>
    public void Execute(object? parameter) => _execute();
}
