using System;

namespace SpectraEngine.Editor.Viewport;

/// <summary>Where the viewport pane lives in the shell's layout.</summary>
/// <remarks>
/// <b>Two values, because there are two viewports and the layout is a
/// consequence of which one is running.</b> It is not a preference and there is
/// no UI for it: a native child window cannot be re-parented without destroying
/// the HWND and the engine session behind it, and a composited pane is an
/// ordinary control with nothing to destroy. Naming the two rather than testing
/// a bool at each site is what makes the mapping a function with a test on it,
/// and what stops a third viewport kind landing in a dock by defaulting.
/// </remarks>
public enum ViewportPlacement
{
    /// <summary>
    /// A plain grid cell nothing may re-parent. The native child's placement,
    /// and the one the shell has always had.
    /// </summary>
    PinnedCell,

    /// <summary>
    /// A dock tool like every other panel: re-stackable, tabbable, floatable.
    /// Only ever a composited pane.
    /// </summary>
    DockedTool,
}

/// <summary>
/// What a placement permits, so a dock tool cannot be handed a capability its
/// hosting model does not survive.
/// </summary>
/// <param name="Docked">Whether the pane is a dock tool at all.</param>
/// <param name="CanFloat">Whether it may be dragged out into its own window.</param>
/// <param name="CanPin">
/// Whether it may be collapsed to a pinned flyout. <b>This is the airspace rule
/// wearing a different name</b>: Dock draws a pinned flyout in the main window's
/// own Avalonia layer, which a native child composites over, so unpinning a pane
/// beside a native viewport would make it invisible exactly where it was asked
/// for.
/// </param>
/// <param name="ToolsMayShareTheWindow">
/// Whether anything Avalonia draws in this window may cross the viewport's
/// rectangle. False is the airspace rule; true is what a composited pane buys.
/// </param>
public readonly record struct ViewportPlacementRules(
    bool Docked,
    bool CanFloat,
    bool CanPin,
    bool ToolsMayShareTheWindow);

/// <summary>
/// The layout half of the viewport decision: pinned cell or dock tool, and what
/// each one permits.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure, and derived from the SAME decision that built the viewport.</b> The
/// shell knows whether a session composited exactly once, in
/// <see cref="ViewportModePolicy.Decide"/>'s answer; a layout that asked the
/// question a second way - a settings read, a type test on the viewport, a flag
/// set at the call site - would be free to disagree with it, and the way that
/// disagreement presents is a native child docked into a tool, which destroys
/// the HWND and takes the engine session with it the first time anybody drags a
/// tab.
/// </para>
/// <para>
/// <b>The rules are DATA rather than three properties on a strategy object</b>,
/// exactly as <c>GizmoStyle</c>'s roster is: everything that varies between the
/// two placements turned out to be a bool, so there is one table and the shell
/// reads it rather than each site re-deciding what "docked" implies.
/// </para>
/// </remarks>
public static class ViewportLayout
{
    /// <summary>Where a session's pane goes, given the viewport it chose.</summary>
    /// <remarks>
    /// <b>Takes the decision rather than a bool</b>, so the coupling is
    /// structural: there is no way to call this with an answer the policy did
    /// not give.
    /// </remarks>
    public static ViewportPlacement For(in ViewportDecision decision) =>
        decision.UseComposition ? ViewportPlacement.DockedTool : ViewportPlacement.PinnedCell;

    /// <summary>What a placement permits.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A placement with no rules. Thrown rather than defaulted, because the
    /// permissive default would admit a native child to a dock and the
    /// restrictive one would silently take docking away from a composited
    /// session with nothing reporting it.
    /// </exception>
    public static ViewportPlacementRules RulesFor(ViewportPlacement placement) => placement switch
    {
        ViewportPlacement.PinnedCell => new ViewportPlacementRules(
            Docked: false, CanFloat: false, CanPin: false, ToolsMayShareTheWindow: false),

        ViewportPlacement.DockedTool => new ViewportPlacementRules(
            Docked: true, CanFloat: true, CanPin: true, ToolsMayShareTheWindow: true),

        _ => throw new ArgumentOutOfRangeException(nameof(placement), placement, "No rules for this placement."),
    };

    /// <summary>
    /// One sentence per placement, for the log line every session writes.
    /// </summary>
    /// <remarks>
    /// <b>For the same reason <see cref="ViewportModePolicy.Describe"/>
    /// exists.</b> A pinned pane and a docked one render the same picture, so a
    /// session that silently got the restricted layout would show up weeks
    /// later as a tab that refuses to be dragged, on one machine, with nothing
    /// anywhere saying why.
    /// </remarks>
    public static string Describe(ViewportPlacement placement) => placement switch
    {
        ViewportPlacement.PinnedCell =>
            "the viewport is pinned in its own cell: a native child window cannot be re-parented without " +
            "destroying the HWND and the engine session behind it, and nothing Avalonia draws in this " +
            "window may cross it.",

        ViewportPlacement.DockedTool =>
            "the viewport is a dock tool: a composited pane has no window to destroy, so it docks, tabs " +
            "and floats like every other panel and the airspace rule no longer binds this window.",

        _ => throw new ArgumentOutOfRangeException(nameof(placement), placement, "No sentence for this placement."),
    };

    /// <summary>The viewport tool never closes, in either placement.</summary>
    /// <remarks>
    /// <b>Not a rule that varies, which is why it is a constant rather than a
    /// field on <see cref="ViewportPlacementRules"/>.</b> There is no Window
    /// menu and no reopen verb, so a closed viewport is a session with a running
    /// engine and no way to see it; the honest answer to "I do not want this
    /// pane" is closing the session.
    /// </remarks>
    public const bool ViewportCanClose = false;
}
