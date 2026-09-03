using SpectraEngine.Editor.Viewport;

namespace SpectraEngine.Editor.Tests;

/// <summary>
/// The rule that decides whether a viewport leaving the visual tree ends a
/// session or is merely a dock drag passing through.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every case here is silent in production, and the worst one destroys a
/// level.</b> A dock drag detaches and re-attaches the pane; answered as a
/// teardown it stops the engine, the re-attach publishes a fresh surface, and
/// the shell builds a SECOND session on it with a new scene and an empty undo
/// history. Nothing throws, nothing logs an error, and the editor looks like it
/// simply reset itself.
/// </para>
/// <para>
/// <b>None of it is reachable through the control it lives in.</b> A composited
/// viewport publishes nothing without a window, a compositor, a GPU and a driver
/// that will import an NT handle, so the transitions were extracted into two
/// booleans that can be stepped through with none of those - the same move
/// <see cref="ViewportInputRouter"/> made for the cursor lock while both
/// viewports were still native.
/// </para>
/// </remarks>
public sealed class ViewportSurfaceLifetimeTests
{
    [Fact]
    public void A_fresh_viewport_publishes_on_its_first_attach()
    {
        var lifetime = new ViewportSurfaceLifetime();

        lifetime.IsPublished.ShouldBeFalse();
        lifetime.Attached().ShouldBe(ViewportAttach.Publish);
        lifetime.IsPublished.ShouldBeTrue();
    }

    [Fact]
    public void A_detach_before_shutdown_ends_nothing()
    {
        // THE case. A tool dragged into another dock is detached and
        // re-attached, and detachment is the only signal a control gets: raising
        // the destroy event here stops the engine and the re-attach builds a
        // second session on a second surface, with the level gone and nothing
        // reporting an error.
        var lifetime = new ViewportSurfaceLifetime();
        lifetime.Attached();

        lifetime.Detached().ShouldBeFalse();
        lifetime.IsPublished.ShouldBeTrue();
    }

    [Fact]
    public void A_re_attach_resumes_rather_than_publishing_a_second_surface()
    {
        var lifetime = new ViewportSurfaceLifetime();
        lifetime.Attached();
        lifetime.Detached();

        lifetime.Attached().ShouldBe(ViewportAttach.Resume);
        lifetime.IsPublished.ShouldBeTrue();
    }

    [Fact]
    public void Any_number_of_re_parents_publishes_exactly_once()
    {
        // A pane can be dragged between docks all afternoon. Each round trip is
        // a compositor rebuild above the surface and nothing at all below it.
        var lifetime = new ViewportSurfaceLifetime();

        lifetime.Attached().ShouldBe(ViewportAttach.Publish);

        for (int i = 0; i < 8; i++)
        {
            lifetime.Detached().ShouldBeFalse();
            lifetime.Attached().ShouldBe(ViewportAttach.Resume);
        }

        lifetime.Shutdown().ShouldBeTrue();
    }

    // --- Shutdown ------------------------------------------------------------

    [Fact]
    public void Shutdown_owes_the_destroy_event_while_a_surface_is_live()
    {
        // Raised at the shutdown rather than at the detach that follows it,
        // because the shell calls this while the compositor half is still whole
        // and while the pane may be sitting in a float window that is about to
        // be closed.
        var lifetime = new ViewportSurfaceLifetime();
        lifetime.Attached();

        lifetime.Shutdown().ShouldBeTrue();
        lifetime.IsShuttingDown.ShouldBeTrue();
        lifetime.IsPublished.ShouldBeFalse();
    }

    [Fact]
    public void The_detach_after_a_shutdown_does_not_end_the_session_twice()
    {
        // The shell removes the control right after saying it is finished, so
        // both paths run. A second destroy event stops an engine that is already
        // stopped and, on the native child's shape of this, would be a second
        // teardown of a surface that is gone.
        var lifetime = new ViewportSurfaceLifetime();
        lifetime.Attached();
        lifetime.Shutdown().ShouldBeTrue();

        lifetime.Detached().ShouldBeFalse();
    }

    [Fact]
    public void A_viewport_that_never_published_owes_nothing_at_shutdown()
    {
        // A launch that closed before the compositor answered, or a machine
        // that refused the import: there is no engine to stop.
        var lifetime = new ViewportSurfaceLifetime();

        lifetime.Shutdown().ShouldBeFalse();
        lifetime.IsShuttingDown.ShouldBeTrue();
    }

    [Fact]
    public void An_attach_after_shutdown_publishes_nothing()
    {
        // The compositor negotiation is asynchronous, so an attach can still be
        // in flight when the shell decides the session is over. Publishing then
        // starts an engine behind a pane that is on its way out of the tree.
        var lifetime = new ViewportSurfaceLifetime();
        lifetime.Shutdown();

        lifetime.Attached().ShouldBe(ViewportAttach.Ignore);
        lifetime.IsPublished.ShouldBeFalse();
    }

    [Fact]
    public void Shutdown_is_idempotent()
    {
        // CloseSessionView and the window's own OnClosing both call it, and a
        // dirty close is cancelled, confirmed and re-issued.
        var lifetime = new ViewportSurfaceLifetime();
        lifetime.Attached();

        lifetime.Shutdown().ShouldBeTrue();
        lifetime.Shutdown().ShouldBeFalse();
        lifetime.Shutdown().ShouldBeFalse();
    }
}
