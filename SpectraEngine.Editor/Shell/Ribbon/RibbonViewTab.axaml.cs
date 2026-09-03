namespace SpectraEngine.Editor.Shell.Ribbon;

/// <summary>
/// The View page: framing, the ground grid, the debug overlays. Nothing on it
/// changes the level.
/// </summary>
public partial class RibbonViewTab : RibbonTabView
{
    public RibbonViewTab()
    {
        InitializeComponent();
        ValidateAgainstRoster();
    }

    /// <inheritdoc/>
    protected override string TabId => RibbonLayout.ViewTabId;
}
