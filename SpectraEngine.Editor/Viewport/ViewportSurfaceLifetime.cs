namespace SpectraEngine.Editor.Viewport;

/// <summary>What an attach means for the surface the shell builds an engine on.</summary>
public enum ViewportAttach
{
    /// <summary>The first attach of a session: publish a surface and start an engine on it.</summary>
    Publish,

    /// <summary>
    /// A re-parent. Everything above the surface has been rebuilt and nothing
    /// below it was disturbed, so there is a picture to resume and no session to
    /// start.
    /// </summary>
    Resume,

    /// <summary>
    /// An attach after the shell has said the viewport is finished. Publishing
    /// here would start a second engine on a viewport that is going away.
    /// </summary>
    Ignore,
}

/// <summary>
/// Whether a viewport's surface is live, and which of the events that move it
/// a given attach or detach is.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two bits, and getting them wrong is how a dockable viewport eats a
/// level.</b> A control that is dragged into another dock or out into a float
/// window is detached and re-attached, and detachment is the only signal it
/// gets: answered as a teardown it raises the destroy event, the shell stops the
/// engine, the re-attach publishes a fresh surface and a SECOND session is built
/// on it, with a new scene and an empty undo history, and nothing anywhere
/// reports an error. So a detach is a RE-PARENT until
/// <see cref="Shutdown"/> has been called, the surface is published exactly once
/// per session, and the destroy event is raised exactly once for it.
/// </para>
/// <para>
/// <b>Pure and here rather than inside the control, because the control cannot
/// be built without a compositor.</b> A composited viewport needs a window, a
/// GPU and a driver that will import an NT handle before it will publish
/// anything at all, which puts every one of these transitions out of reach of a
/// test. The rule they encode is two booleans and is worth pinning on its own,
/// exactly as <see cref="ViewportInputRouter"/>'s cursor state was extracted
/// while both viewports were still native.
/// </para>
/// <para>
/// <b>Threading:</b> UI thread only. Every caller is a control lifecycle
/// callback or the shell's own teardown.
/// </para>
/// </remarks>
public sealed class ViewportSurfaceLifetime
{
    private bool _published;
    private bool _shuttingDown;

    /// <summary>
    /// Whether the shell currently holds a surface from this viewport with an
    /// engine on it.
    /// </summary>
    /// <remarks>
    /// Read synchronously by an attach that has to decide something before the
    /// compositor negotiation it starts can finish: whether to take the keyboard
    /// back (a re-parent lost it and a first attach never had it), and whether a
    /// compositor refusing the viewport is a launch that failed or a live
    /// session that stopped showing a picture.
    /// </remarks>
    public bool IsPublished => _published;

    /// <summary>Whether the shell has said this viewport is finished.</summary>
    public bool IsShuttingDown => _shuttingDown;

    /// <summary>
    /// What this attach means, once the compositor half behind it exists.
    /// </summary>
    /// <remarks>
    /// <b>Called where the surface would be published, not where the control is
    /// attached</b>, because the two are separated by an asynchronous
    /// negotiation with the compositor and a viewport detached again in that gap
    /// must publish nothing.
    /// </remarks>
    public ViewportAttach Attached()
    {
        if (_shuttingDown)
            return ViewportAttach.Ignore;

        if (_published)
            return ViewportAttach.Resume;

        _published = true;
        return ViewportAttach.Publish;
    }

    /// <summary>
    /// Whether this detach must raise the destroy event. True only after
    /// <see cref="Shutdown"/>, and only once.
    /// </summary>
    public bool Detached() => _shuttingDown && TakePublished();

    /// <summary>
    /// The shell is finished with this viewport. Returns whether the destroy
    /// event is owed now.
    /// </summary>
    /// <remarks>
    /// <b>Raised HERE rather than at the detach that follows, whenever there is
    /// a live surface.</b> The shell calls this before the control leaves the
    /// tree, which is what gives the engine its stop while the compositor half
    /// is still whole - the ordering the keyed-mutex hand-over needs, and the
    /// one a detach on its own cannot promise because the pane may be sitting in
    /// a float window that is about to be closed. A viewport that never
    /// published anything owes nothing.
    /// </remarks>
    public bool Shutdown()
    {
        _shuttingDown = true;
        return TakePublished();
    }

    private bool TakePublished()
    {
        if (!_published)
            return false;

        _published = false;
        return true;
    }
}
