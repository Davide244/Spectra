namespace SpectraEngine.Editor.Shell;

/// <summary>
/// Whether an asset dragged out of the content browser can become a node, and
/// what to say when it cannot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every refusal here is a SENTENCE, because the alternative is a gesture
/// that ends in nothing.</b> A drag has no keyboard equivalent to fall back on
/// and no menu item greying out beside it: if the drop does nothing and says
/// nothing, the honest reading is that the shell lost the gesture. That is
/// especially true of the viewport-mode refusal, which is a difference between
/// two panes that render an identical picture.
/// </para>
/// <para>
/// <b>Pure, and separate from both the viewport and the window</b>, because a
/// drag gesture cannot be driven headlessly at all: the only part of this
/// decision a test can reach is the part that is a function of its inputs, so
/// that part is written down as one.
/// </para>
/// </remarks>
public static class AssetDropPolicy
{
    /// <summary>
    /// What to tell the user instead of placing <paramref name="payload"/>, or
    /// null when the drop should go ahead.
    /// </summary>
    /// <remarks>
    /// <b>Order matters and follows what the user can act on.</b> No session is
    /// checked first because nothing else is true yet; the viewport mode next,
    /// because it is a property of the whole session rather than of this file;
    /// and the kind last, since "this is not a model" is only worth saying to
    /// somebody whose drop could otherwise have landed.
    /// </remarks>
    /// <param name="payload">What is being dragged.</param>
    /// <param name="hasSession">Whether a project is open with an engine in it.</param>
    /// <param name="viewportAcceptsDrops">
    /// Whether the running viewport is a drop target at all. A native child
    /// window is not: the OS delivers input to the HWND, its messages do not
    /// bubble into Avalonia, and OLE would need an <c>IDropTarget</c> registered
    /// on that window.
    /// </param>
    public static string? Refuse(ContentDragPayload payload, bool hasSession, bool viewportAcceptsDrops)
    {
        if (!hasSession)
            return "Open a project before dropping anything into the viewport.";

        if (!viewportAcceptsDrops)
        {
            return "This viewport is a native child window and cannot take a drop. " +
                "Relaunch with --viewport=composition to drag assets into the scene.";
        }

        if (!CanPlace(payload.Kind))
            return $"{payload.Name} is not a model; only models can be dropped into the scene yet.";

        return null;
    }

    /// <summary>
    /// Whether a kind has a placement at all.
    /// </summary>
    /// <remarks>
    /// Models only, for now. A material dropped on a FACE is the obvious next
    /// one and is a different gesture rather than a wider list here: it needs
    /// the face under the cursor, not the point, and a face is identified by
    /// plane index on a brush the ray happened to hit.
    /// </remarks>
    public static bool CanPlace(ContentKind kind) => kind == ContentKind.Model;
}
