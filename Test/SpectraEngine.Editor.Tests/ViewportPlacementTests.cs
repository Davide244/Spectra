using SpectraEngine.Core.Graphics;
using SpectraEngine.Editor.Viewport;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SpectraEngine.Editor.Tests;

/// <summary>
/// Where the viewport pane lives, and the promise that it follows the viewport
/// the session actually got.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this suite exists to make unreachable is a native child in a
/// dock tool.</b> A <c>NativeControlHost</c>'s HWND is destroyed by any
/// re-parent and the engine session goes with it, so a layout that decided
/// "docked" from anything other than the decision that built the viewport - a
/// settings read, a stale field, a bool passed at one call site and forgotten
/// at another - is a level that vanishes the first time somebody drags a tab.
/// The mapping is therefore a pure function over the decision itself, and this
/// pins it.
/// </para>
/// <para>
/// The rest is the enumeration, exactly as <see cref="ViewportModePolicyTests"/>
/// does it: every placement owes rules and a sentence, and a new one with
/// neither fails the build's test run rather than defaulting into whichever
/// answer the switch happened to fall through to.
/// </para>
/// </remarks>
public sealed class ViewportPlacementTests
{
    private static ViewportDecision Composited => new(
        UseComposition: true,
        ViewportChoiceReason.ExplicitComposition,
        ViewportModePolicy.Describe(ViewportChoiceReason.ExplicitComposition));

    private static ViewportDecision Native => new(
        UseComposition: false,
        ViewportChoiceReason.ExplicitNative,
        ViewportModePolicy.Describe(ViewportChoiceReason.ExplicitNative));

    // --- The enumeration -----------------------------------------------------

    [Fact]
    public void Every_placement_owes_rules_and_a_sentence()
    {
        foreach (ViewportPlacement placement in Enum.GetValues<ViewportPlacement>())
        {
            // Both throw rather than defaulting, because the permissive default
            // admits a native child to a dock and the restrictive one silently
            // takes docking away from a composited session.
            _ = ViewportLayout.RulesFor(placement);

            ViewportLayout.Describe(placement).ShouldNotBeNullOrWhiteSpace(
                $"{placement} has no sentence, so a session laid out that way would be laid out silently.");
        }
    }

    [Fact]
    public void A_placement_that_is_not_a_placement_is_refused_rather_than_defaulted()
    {
        const ViewportPlacement Invented = (ViewportPlacement)99;

        Should.Throw<ArgumentOutOfRangeException>(() => ViewportLayout.RulesFor(Invented));
        Should.Throw<ArgumentOutOfRangeException>(() => ViewportLayout.Describe(Invented));
    }

    /// <summary>
    /// Both placements are reachable, so neither is dead code nobody has run.
    /// </summary>
    [Fact]
    public void Every_placement_is_reached_by_some_decision()
    {
        ViewportDecision[] decisions = [Native, Composited];

        HashSet<ViewportPlacement> reached = [.. decisions.Select(d => ViewportLayout.For(d))];

        reached.ShouldBe(Enum.GetValues<ViewportPlacement>().ToHashSet(), ignoreOrder: true);
    }

    // --- The mapping ---------------------------------------------------------

    [Fact]
    public void A_native_session_pins_the_viewport()
    {
        // The HWND reason, and it has not weakened: any dock arrangement that
        // re-parents a NativeControlHost destroys its child window and the
        // engine session behind it.
        ViewportLayout.For(Native).ShouldBe(ViewportPlacement.PinnedCell);
    }

    [Fact]
    public void A_composited_session_docks_the_viewport()
    {
        // There is no window to destroy: the pane is a control over a
        // CompositionSurfaceVisual, so a re-parent costs it a compositor
        // rebuild and nothing else.
        ViewportLayout.For(Composited).ShouldBe(ViewportPlacement.DockedTool);
    }

    /// <summary>
    /// The mapping reads the decision and nothing else, whatever reason it
    /// carries.
    /// </summary>
    /// <remarks>
    /// <b>A fallback is still a native session.</b> Every reason but the two
    /// explicit ones can arrive on either side of the choice over time, and a
    /// layout that keyed off the reason rather than off the answer would dock a
    /// native child the first time a new fallback reason was added without
    /// anybody remembering this table.
    /// </remarks>
    [Fact]
    public void The_reason_never_changes_the_placement_only_the_answer_does()
    {
        foreach (ViewportChoiceReason reason in Enum.GetValues<ViewportChoiceReason>())
        {
            var native = new ViewportDecision(false, reason, ViewportModePolicy.Describe(reason));
            var composited = new ViewportDecision(true, reason, ViewportModePolicy.Describe(reason));

            ViewportLayout.For(native).ShouldBe(ViewportPlacement.PinnedCell);
            ViewportLayout.For(composited).ShouldBe(ViewportPlacement.DockedTool);
        }
    }

    // --- What each placement permits -----------------------------------------

    [Fact]
    public void A_pinned_viewport_may_not_float_or_pin_and_owns_its_airspace()
    {
        ViewportPlacementRules rules = ViewportLayout.RulesFor(ViewportPlacement.PinnedCell);

        rules.Docked.ShouldBeFalse();
        rules.CanFloat.ShouldBeFalse();

        // Dock draws a pinned flyout in the main window's own Avalonia layer,
        // which a native child composites over: the pin glyph would be an
        // invitation to make a panel invisible.
        rules.CanPin.ShouldBeFalse();

        rules.ToolsMayShareTheWindow.ShouldBeFalse();
    }

    [Fact]
    public void A_docked_viewport_floats_pins_and_gives_the_window_back()
    {
        ViewportPlacementRules rules = ViewportLayout.RulesFor(ViewportPlacement.DockedTool);

        rules.Docked.ShouldBeTrue();
        rules.CanFloat.ShouldBeTrue();
        rules.CanPin.ShouldBeTrue();

        // The airspace rule is void here, which is the whole point of the
        // composited path: an overlay, an adorner or a managed float may cross
        // the viewport's rectangle because there is no child window over it.
        rules.ToolsMayShareTheWindow.ShouldBeTrue();
    }

    [Fact]
    public void The_viewport_never_closes_in_either_placement()
    {
        // There is no Window menu and no reopen verb, so a closed viewport is a
        // session with a running engine and no way to see it. The honest answer
        // to "I do not want this pane" is closing the session.
        ViewportLayout.ViewportCanClose.ShouldBeFalse();
    }

    /// <summary>
    /// Pinning and airspace are the same fact said twice, so they move
    /// together.
    /// </summary>
    /// <remarks>
    /// <b>The failure this catches is a half-lifted restriction.</b> Dock's
    /// pinned flyout is drawn in the same managed layer as every overlay the
    /// airspace rule bans, so a placement that allowed pinning while still
    /// claiming the window is a placement where the pin glyph opens a flyout
    /// nobody can see.
    /// </remarks>
    [Fact]
    public void Pinning_is_permitted_exactly_where_the_managed_layer_is_usable()
    {
        foreach (ViewportPlacement placement in Enum.GetValues<ViewportPlacement>())
        {
            ViewportPlacementRules rules = ViewportLayout.RulesFor(placement);
            rules.CanPin.ShouldBe(rules.ToolsMayShareTheWindow);
        }
    }
}
