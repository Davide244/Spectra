using Avalonia.Controls;

namespace SpectraEngine.Editor.Shell.Ribbon;

/// <summary>
/// The Build page: insert, transform, snap, arrange. Everything on it changes
/// the level.
/// </summary>
/// <remarks>
/// No hand-written <c>InitializeComponent</c> here, deliberately. A
/// parameterless one declared in the same partial class SHADOWS the generated
/// overload at the call site, the XAML loads and every named field stays null -
/// which is how <c>NameDialog</c> threw on its first line for its whole life.
/// </remarks>
public partial class RibbonBuildTab : RibbonTabView
{
    public RibbonBuildTab()
    {
        InitializeComponent();
        ValidateAgainstRoster();
    }

    /// <inheritdoc/>
    protected override string TabId => RibbonLayout.DefaultTabId;

    /// <summary>
    /// The snap increment field, so the window can keep owning the commit rule
    /// it already owns for it: parse, refuse zero and negatives rather than
    /// clamping, revert on anything unparseable, and stop taking refreshes
    /// while it has focus.
    /// </summary>
    public TextBox SnapField => SnapBox;
}
