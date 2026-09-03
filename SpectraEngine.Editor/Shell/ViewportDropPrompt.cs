namespace SpectraEngine.Editor.Shell;

/// <summary>
/// What the viewport draws over the picture while an asset drag is over it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The overlay is the first thing this shell has ever drawn over the render,
/// and its own visibility rule is the airspace rule stated as data.</b> A
/// composited pane is an ordinary control with the frame arriving underneath it
/// as a <c>CompositionSurfaceVisual</c>, so an overlay lands on top of it like
/// an overlay on any other control. A native child is a window the OS
/// composites ABOVE everything Avalonia draws into this window, so the identical
/// markup over one is painted and never seen. That is why
/// <paramref name="viewportAcceptsDrops"/> hides this outright rather than
/// showing the refusal it would otherwise carry: an overlay nobody can see is
/// worse than none, because the code claims to have reported something. The
/// native session's refusal reaches the user through the status bar and the
/// output log, exactly as it did before this type existed.
/// </para>
/// <para>
/// <b>It decides nothing, it only says what has already been decided.</b> The
/// verdict is <see cref="AssetDropPolicy.Refuse"/>'s and is asked for here
/// rather than restated, because a viewport that painted an accepting frame
/// over a drop the policy then refused would be a lie delivered at exactly the
/// moment somebody let go of the mouse.
/// </para>
/// <para>
/// <b>A value, and comparable, because <c>DragOver</c> fires at POINTER RATE.</b>
/// A drag crossing a 900,000 pixel pane raises this several hundred times, and
/// every one of them carries the same answer: the payload does not change while
/// a gesture is in flight. Held as a record struct, an unchanged prompt is one
/// comparison and no property-changed notification at all, which is the
/// difference between a static frame and five bindings re-evaluating per
/// pointer move.
/// </para>
/// </remarks>
/// <param name="IsVisible">Whether the overlay is drawn at all.</param>
/// <param name="Accepts">
/// Whether letting go here would place something. False is the refusing arm,
/// which is still drawn: a gesture that ends in nothing is indistinguishable
/// from a gesture the shell never received.
/// </param>
/// <param name="Headline">The verdict, in two or three words.</param>
/// <param name="Subject">
/// What would be placed, as the content-relative path the engine names it by.
/// Empty on the refusing arm, where <paramref name="Reason"/> already names the
/// file and a second mention would read as two different things.
/// </param>
/// <param name="Reason">
/// Why not, verbatim from <see cref="AssetDropPolicy"/>. Empty when accepting.
/// </param>
public readonly record struct ViewportDropPrompt(
    bool IsVisible,
    bool Accepts,
    string Headline,
    string Subject,
    string Reason)
{
    /// <summary>No drag over the viewport, and nothing drawn.</summary>
    /// <remarks>
    /// Every string is empty rather than null, because these bind straight to
    /// <c>TextBlock.Text</c> and a null there is a binding that silently leaves
    /// the previous value on screen.
    /// </remarks>
    public static ViewportDropPrompt None { get; } =
        new(false, false, string.Empty, string.Empty, string.Empty);

    /// <summary>
    /// What to draw for <paramref name="payload"/> hovering over the viewport,
    /// or <see cref="None"/> when nothing should be.
    /// </summary>
    /// <param name="payload">The asset under the pointer, or null for no drag.</param>
    /// <param name="hasSession">Whether a project is open with an engine in it.</param>
    /// <param name="viewportAcceptsDrops">
    /// <see cref="Viewport.IEngineViewport.AcceptsAssetDrops"/>, which today is
    /// also the answer to "can this session be drawn over at all" - see the
    /// remarks on this type for why those are one question and not two.
    /// </param>
    public static ViewportDropPrompt For(
        ContentDragPayload? payload, bool hasSession, bool viewportAcceptsDrops)
    {
        if (payload is null || !hasSession || !viewportAcceptsDrops)
            return None;

        return AssetDropPolicy.Refuse(payload, hasSession, viewportAcceptsDrops) is { } refusal
            ? new ViewportDropPrompt(true, false, "Cannot place", string.Empty, refusal)
            : new ViewportDropPrompt(true, true, "Drop to place", payload.ContentPath, string.Empty);
    }
}
