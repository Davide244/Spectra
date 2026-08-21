using Microsoft.Extensions.Logging.Abstractions;
using SpectraEngine.Core.Windowing;
using System.Collections.Generic;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// The borderless-fullscreen latch, headless. A real fullscreen transition
/// needs a compositor and a GPU, but everything that made the old Alt+Enter
/// path a crash is decision logic: who may touch the window, when the request
/// is applied, and what geometry the way back restores.
/// </summary>
public sealed class WindowModeLatchTests
{
    /// <summary>
    /// A window that records what was done to it and in which order — the
    /// order is load-bearing (un-maximize, then undecorate, then move), so the
    /// tests assert on it rather than only on the end state.
    /// </summary>
    private sealed class FakeWindow : IWindowModeTarget
    {
        private WindowRect _bounds = new(120, 80, 1280, 720);
        private bool _decorated = true;
        private bool _maximized;

        internal List<string> Log { get; } = [];
        internal WindowRect? DisplayBounds { get; set; } = new(0, 0, 2560, 1440);

        public WindowRect Bounds
        {
            get => _bounds;
            set
            {
                _bounds = value;
                Log.Add($"bounds={value.X},{value.Y},{value.Width}x{value.Height}");
            }
        }

        public bool Decorated
        {
            get => _decorated;
            set
            {
                _decorated = value;
                Log.Add($"decorated={value}");
            }
        }

        public bool IsMaximized
        {
            get => _maximized;
            set
            {
                _maximized = value;
                Log.Add($"maximized={value}");
            }
        }

        public bool TryGetDisplayBounds(out WindowRect bounds)
        {
            if (DisplayBounds is { } display)
            {
                bounds = display;
                return true;
            }
            bounds = default;
            return false;
        }
    }

    private static WindowModeLatch NewLatch() => new(NullLogger.Instance);

    [Fact]
    public void A_fresh_latch_is_windowed_and_has_nothing_pending()
    {
        var latch = NewLatch();
        var window = new FakeWindow();

        latch.WindowMode.ShouldBe(WindowMode.Windowed);
        latch.RequestedWindowMode.ShouldBe(WindowMode.Windowed);
        latch.ApplyPendingWindowMode(window).ShouldBeNull();
        window.Log.ShouldBeEmpty();
    }

    [Fact]
    public void Requesting_fullscreen_changes_nothing_until_the_window_thread_applies_it()
    {
        var latch = NewLatch();
        var window = new FakeWindow();

        latch.RequestWindowMode(WindowMode.BorderlessFullscreen);

        // The request is visible; the window is untouched. This IS the fix:
        // the requester runs on the render thread and may not reshape a window.
        latch.RequestedWindowMode.ShouldBe(WindowMode.BorderlessFullscreen);
        latch.WindowMode.ShouldBe(WindowMode.Windowed);
        window.Log.ShouldBeEmpty();
        window.Decorated.ShouldBeTrue();

        latch.ApplyPendingWindowMode(window).ShouldBe(WindowMode.BorderlessFullscreen);
        latch.WindowMode.ShouldBe(WindowMode.BorderlessFullscreen);
    }

    [Fact]
    public void Going_fullscreen_undecorates_and_fills_the_display()
    {
        var latch = NewLatch();
        var window = new FakeWindow { DisplayBounds = new WindowRect(-1920, 0, 1920, 1080) };

        latch.RequestWindowMode(WindowMode.BorderlessFullscreen);
        latch.ApplyPendingWindowMode(window);

        window.Decorated.ShouldBeFalse();
        window.Bounds.ShouldBe(new WindowRect(-1920, 0, 1920, 1080));

        // Border before geometry: dropping the frame changes the client area,
        // so sizing first would leave the window off by the frame thickness.
        window.Log.ShouldBe(["decorated=False", "bounds=-1920,0,1920x1080"]);
    }

    [Fact]
    public void Coming_back_restores_the_exact_windowed_geometry()
    {
        var latch = NewLatch();
        var window = new FakeWindow { Bounds = new WindowRect(300, 200, 1600, 900) };
        window.Log.Clear();

        latch.ToggleFullscreen();
        latch.ApplyPendingWindowMode(window).ShouldBe(WindowMode.BorderlessFullscreen);

        latch.ToggleFullscreen();
        latch.ApplyPendingWindowMode(window).ShouldBe(WindowMode.Windowed);

        window.Decorated.ShouldBeTrue();
        window.Bounds.ShouldBe(new WindowRect(300, 200, 1600, 900));
    }

    [Fact]
    public void A_maximized_window_is_unmaximized_on_the_way_in_and_remaximized_on_the_way_out()
    {
        var latch = NewLatch();
        var window = new FakeWindow { IsMaximized = true };
        window.Log.Clear();

        latch.RequestWindowMode(WindowMode.BorderlessFullscreen);
        latch.ApplyPendingWindowMode(window);

        // Un-maximize FIRST: a maximized window ignores an explicit
        // position/size, so the move would silently not happen.
        window.Log[0].ShouldBe("maximized=False");
        window.IsMaximized.ShouldBeFalse();
        window.Decorated.ShouldBeFalse();

        latch.RequestWindowMode(WindowMode.Windowed);
        latch.ApplyPendingWindowMode(window);

        window.IsMaximized.ShouldBeTrue();
        window.Decorated.ShouldBeTrue();
    }

    [Fact]
    public void Applying_twice_without_a_new_request_is_a_no_op()
    {
        var latch = NewLatch();
        var window = new FakeWindow();

        latch.ToggleFullscreen();
        latch.ApplyPendingWindowMode(window).ShouldBe(WindowMode.BorderlessFullscreen);

        int touchesAfterTransition = window.Log.Count;
        latch.ApplyPendingWindowMode(window).ShouldBeNull();
        latch.ApplyPendingWindowMode(window).ShouldBeNull();
        window.Log.Count.ShouldBe(touchesAfterTransition);
    }

    [Fact]
    public void Two_toggles_between_two_applies_cancel_out()
    {
        var latch = NewLatch();
        var window = new FakeWindow();

        latch.ToggleFullscreen();
        latch.ToggleFullscreen();

        latch.RequestedWindowMode.ShouldBe(WindowMode.Windowed);
        latch.ApplyPendingWindowMode(window).ShouldBeNull();
        window.Log.ShouldBeEmpty();
    }

    [Fact]
    public void A_fullscreen_request_with_no_usable_display_is_refused_and_not_retried()
    {
        var latch = NewLatch();
        var window = new FakeWindow { DisplayBounds = null };

        latch.RequestWindowMode(WindowMode.BorderlessFullscreen);
        latch.ApplyPendingWindowMode(window).ShouldBeNull();

        // Refused, and the request is reset — otherwise every pass of the event
        // pump would retry it forever.
        latch.WindowMode.ShouldBe(WindowMode.Windowed);
        latch.RequestedWindowMode.ShouldBe(WindowMode.Windowed);
        window.Log.ShouldBeEmpty();
        window.Decorated.ShouldBeTrue();
    }

    [Fact]
    public void A_degenerate_display_rectangle_is_refused_too()
    {
        var latch = NewLatch();
        var window = new FakeWindow { DisplayBounds = new WindowRect(0, 0, 0, 0) };

        latch.RequestWindowMode(WindowMode.BorderlessFullscreen);
        latch.ApplyPendingWindowMode(window).ShouldBeNull();

        latch.WindowMode.ShouldBe(WindowMode.Windowed);
        window.Log.ShouldBeEmpty();
    }
}
