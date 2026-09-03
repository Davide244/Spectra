using System;

namespace SpectraEngine.Editor.Shell.Ribbon;

/// <summary>Where the active tab's body is showing, if anywhere.</summary>
public enum RibbonBodyHost
{
    /// <summary>Nowhere: the ribbon is collapsed to its tab strip.</summary>
    None,

    /// <summary>In the window, under the tab strip, taking layout space.</summary>
    Inline,

    /// <summary>
    /// In the flyout, over whatever is below. A real popup, which on Windows is
    /// a real OS window and therefore the ONE surface that may legally cross a
    /// native viewport - the same reason the menus, the context menus and the
    /// modal dialogs may.
    /// </summary>
    Flyout,
}

/// <summary>
/// The ribbon's two states and the transitions between them, as a value.
/// </summary>
/// <param name="Expanded">Pinned open. Persisted; see <see cref="EditorSettings"/>.</param>
/// <param name="ActiveTabId">Which page the strip is pointing at.</param>
/// <param name="FlyoutOpen">
/// A collapsed ribbon showing one page temporarily. Never true while
/// <paramref name="Expanded"/> is - the transitions below maintain that, and
/// <see cref="RibbonSurface.HostFor"/> would otherwise have two answers.
/// </param>
public readonly record struct RibbonSurfaceState(bool Expanded, string ActiveTabId, bool FlyoutOpen);

/// <summary>
/// The collapse state machine. Pure: no control, no window, no settings.
/// </summary>
/// <remarks>
/// <para>
/// <b>A state machine rather than three booleans on the window, because every
/// interesting case is a COMBINATION.</b> Clicking the active tab means "switch
/// page" while expanded and "put that page away" while collapsed; expanding
/// while a flyout is open must not leave the flyout behind it; and a command
/// invoked out of a flyout closes it, which is the behaviour that makes a
/// collapsed ribbon usable rather than sticky. Written as transitions over a
/// value, all four are testable without an Avalonia application.
/// </para>
/// <para>
/// <b>The active tab is deliberately not part of what gets persisted.</b> A
/// shell that reopened on View would start every session with Insert hidden,
/// which is precisely the failure that retired the previous tab strip. Only the
/// expanded flag survives a launch.
/// </para>
/// </remarks>
public static class RibbonSurface
{
    /// <summary>The state a session starts in, on the default tab.</summary>
    public static RibbonSurfaceState Create(bool expanded) =>
        new(expanded, RibbonLayout.DefaultTabId, FlyoutOpen: false);

    /// <summary>Where the active tab's body belongs right now.</summary>
    public static RibbonBodyHost HostFor(in RibbonSurfaceState state)
    {
        if (state.Expanded)
            return RibbonBodyHost.Inline;

        return state.FlyoutOpen ? RibbonBodyHost.Flyout : RibbonBodyHost.None;
    }

    /// <summary>
    /// A tab was clicked.
    /// </summary>
    /// <remarks>
    /// Expanded, this is navigation. Collapsed, it flies the page out - and
    /// clicking the tab that is already flown out puts it away again, which is
    /// the only way a keyboard-free user closes one without invoking something.
    /// An id no tab carries changes nothing, so a stale control cannot leave
    /// the strip pointing at a page that does not exist.
    /// </remarks>
    public static RibbonSurfaceState SelectTab(in RibbonSurfaceState state, string? tabId)
    {
        if (RibbonLayout.FindTab(tabId) is not { } tab)
            return state;

        if (state.Expanded)
            return state with { ActiveTabId = tab.Id, FlyoutOpen = false };

        bool sameTabAlreadyOut = state.FlyoutOpen
            && string.Equals(state.ActiveTabId, tab.Id, StringComparison.Ordinal);

        return state with { ActiveTabId = tab.Id, FlyoutOpen = !sameTabAlreadyOut };
    }

    /// <summary>
    /// Pins the ribbon open, or collapses it to the strip.
    /// </summary>
    /// <remarks>
    /// SET semantics rather than a toggle verb, the same rule every displayed
    /// state in this shell follows. Either way the flyout closes: an expanded
    /// ribbon with a popup of the same page over it is two copies of one thing.
    /// </remarks>
    public static RibbonSurfaceState SetExpanded(in RibbonSurfaceState state, bool expanded) =>
        state with { Expanded = expanded, FlyoutOpen = false };

    /// <summary>
    /// A ribbon control was invoked.
    /// </summary>
    /// <remarks>
    /// A command posted out of a flown-out page closes the page. Without it a
    /// collapsed ribbon behaves worse than an expanded one: the flyout stays
    /// over the viewport the edit just landed in, and the thing the user wanted
    /// to look at is the thing they cannot see.
    /// </remarks>
    public static RibbonSurfaceState Invoke(in RibbonSurfaceState state) =>
        state.FlyoutOpen ? state with { FlyoutOpen = false } : state;

    /// <summary>The flyout was dismissed from outside: a click away, a closing session.</summary>
    public static RibbonSurfaceState Dismiss(in RibbonSurfaceState state) =>
        state.FlyoutOpen ? state with { FlyoutOpen = false } : state;
}
