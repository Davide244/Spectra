using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SpectraEngine.Editor.Shell;

/// <summary>
/// The smallest useful <see cref="INotifyPropertyChanged"/> base: set a field,
/// notify only when the value actually changed.
/// </summary>
/// <remarks>
/// <b>Hand-rolled rather than an MVVM package.</b> The shell needs change
/// notification and nothing else from that stack: no commands (toolbar clicks
/// go straight to a method), no messenger, no DI, no navigation. A framework
/// here would be twenty times this file to obtain one interface.
/// <para>
/// <b>The equality guard is load-bearing, not tidiness.</b> Selection is
/// reapplied from a snapshot about thirty times a second across every node the
/// tree holds; without the guard that is a notification storm proportional to
/// the whole scene rather than to what changed. It is the second line of
/// defence behind the caller only touching what moved.
/// </para>
/// </remarks>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Assigns <paramref name="value"/> and raises a change for the calling
    /// property, unless the value is already equal. Returns whether it changed.
    /// </summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    /// <summary>Raises a change for a property whose value is computed.</summary>
    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
