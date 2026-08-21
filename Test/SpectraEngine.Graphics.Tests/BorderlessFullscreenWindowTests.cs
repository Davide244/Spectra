using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.Windowing;
using SpectraEngine.Core.Windowing;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// The borderless-fullscreen transition against a <em>real</em> window, in the
/// suite whose whole purpose is meeting the real thing.
/// </summary>
/// <remarks>
/// <see cref="WindowModeLatchTests"/> proves the state machine over a fake; this
/// proves the half a fake cannot: that the windowing backend actually honours an
/// undecorate and a move-and-fill at runtime, and gives the geometry back
/// unchanged. That is worth a real window because the failure mode is not a
/// wrong pixel — a backend that does not implement the border setter throws, on
/// the main thread, the first time anyone presses F11.
/// <para>
/// It borrows the GL fixture's window rather than creating one: GLFW registers
/// a process-global window class, so there can only ever be one (see
/// <see cref="GlRendererCollection"/>), and re-running Silk's platform
/// registration after a window exists throws outright. Nothing here touches
/// GL, and the geometry is restored in a <c>finally</c>.
/// </para>
/// </remarks>
[Collection(GlRendererCollection.Name)]
public sealed class BorderlessFullscreenWindowTests
{
    private readonly GlRendererFixture _fixture;

    public BorderlessFullscreenWindowTests(GlRendererFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void A_real_window_undecorates_fills_the_display_and_comes_back_unchanged()
    {
        IWindow window = _fixture.HostWindow;
        var target = new SilkWindowModeTarget(window);
        var latch = new WindowModeLatch(NullLogger.Instance);

        target.TryGetDisplayBounds(out WindowRect display).ShouldBeTrue();
        display.IsPositive.ShouldBeTrue();

        WindowRect windowed = target.Bounds;
        bool decorated = target.Decorated;

        try
        {
            latch.RequestWindowMode(WindowMode.BorderlessFullscreen);
            latch.ApplyPendingWindowMode(target).ShouldBe(WindowMode.BorderlessFullscreen);
            PumpEvents(window);

            target.Decorated.ShouldBeFalse();
            target.Bounds.ShouldBe(display);

            // The framebuffer latch is what every backend reconciles its swap
            // chain against, so the transition is only real if the framebuffer
            // followed the window.
            window.FramebufferSize.X.ShouldBe(display.Width);
            window.FramebufferSize.Y.ShouldBe(display.Height);

            latch.RequestWindowMode(WindowMode.Windowed);
            latch.ApplyPendingWindowMode(target).ShouldBe(WindowMode.Windowed);
            PumpEvents(window);

            target.Decorated.ShouldBeTrue();
            target.Bounds.ShouldBe(windowed);
        }
        finally
        {
            // The window is shared with every other class in this collection;
            // an assertion failure above must not hand them a fullscreen one.
            target.Decorated = decorated;
            target.Bounds = windowed;
            PumpEvents(window);
        }
    }

    // GLFW applies attribute and geometry changes through its message queue, so
    // the assertions above have to be made after the queue has been drained —
    // which in the engine is simply the next pass of the main loop.
    private static void PumpEvents(IWindow window)
    {
        for (int i = 0; i < 10; i++)
            window.DoEvents();
    }
}
