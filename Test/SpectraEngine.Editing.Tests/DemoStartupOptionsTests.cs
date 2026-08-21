using SpectraEngine.Core.Graphics;
using SpectraEngine.Executable;
using System;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// The demo host's startup switch, and above all its default. The synthetic
/// editing self-test drags a real brush node a real world unit and leaves it
/// displaced for the frames the async recompile needs, so an accidentally
/// always-on self-test is indistinguishable — to somebody using the editor —
/// from a brush that jitters every few seconds. It was exactly that once.
/// These tests pin the gate so it cannot come back on by accident: OFF unless
/// a command-line switch or the environment variable asks for it, and ON, with
/// an attributable source, when one does.
/// </summary>
/// <remarks>
/// Headless by construction: <see cref="DemoStartupOptions.Parse"/> takes the
/// environment value as an argument instead of reading it, so nothing here
/// needs a window, a renderer, or process-wide state.
/// </remarks>
public sealed class DemoStartupOptionsTests
{
    [Fact]
    public void The_self_test_is_off_when_nothing_asks_for_it()
    {
        DemoStartupOptions options = DemoStartupOptions.Parse([], selfTestEnvironmentValue: null);

        options.SelfTestEnabled.ShouldBeFalse();
        options.SelfTestSource.ShouldBe(SelfTestSource.Default);
    }

    [Theory]
    [InlineData("opengl")]
    [InlineData("d3d11")]
    [InlineData("d3d12")]
    public void The_self_test_is_off_for_a_plain_backend_run(string backend)
    {
        DemoStartupOptions options = DemoStartupOptions.Parse([backend], selfTestEnvironmentValue: null);

        options.SelfTestEnabled.ShouldBeFalse();
    }

    [Theory]
    [InlineData("--selftest")]
    [InlineData("-selftest")]
    [InlineData("/selftest")]
    [InlineData("--self-test")]
    [InlineData("--selftest=true")]
    [InlineData("--selftest=1")]
    [InlineData("--selftest=on")]
    public void The_switch_turns_the_self_test_on(string argument)
    {
        DemoStartupOptions options = DemoStartupOptions.Parse([argument], selfTestEnvironmentValue: null);

        options.SelfTestEnabled.ShouldBeTrue();
        options.SelfTestSource.ShouldBe(SelfTestSource.CommandLine);
    }

    [Fact]
    public void The_switch_composes_with_a_backend_in_either_order()
    {
        // The gate runs this exact shape on all three backends, so both orders
        // have to work — the backend used to be read positionally as args[0].
        DemoStartupOptions backendFirst = DemoStartupOptions.Parse(["d3d11", "--selftest"], null);
        DemoStartupOptions switchFirst = DemoStartupOptions.Parse(["--selftest", "d3d11"], null);

        backendFirst.ShouldBe(switchFirst);
        backendFirst.Backend.ShouldBe(GraphicsBackend.D3D11);
        backendFirst.SelfTestEnabled.ShouldBeTrue();
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("ON")]
    public void The_environment_variable_turns_the_self_test_on(string value)
    {
        DemoStartupOptions options = DemoStartupOptions.Parse([], value);

        options.SelfTestEnabled.ShouldBeTrue();
        options.SelfTestSource.ShouldBe(SelfTestSource.Environment);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("off")]
    public void The_environment_variable_leaves_the_self_test_off(string? value)
    {
        DemoStartupOptions.Parse([], value).SelfTestEnabled.ShouldBeFalse();
    }

    [Fact]
    public void An_explicit_command_line_no_overrides_an_inherited_environment_yes()
    {
        // A harness that exported the variable once must still be able to run a
        // quiet session without unsetting it.
        DemoStartupOptions options = DemoStartupOptions.Parse(["--selftest=false"], "true");

        options.SelfTestEnabled.ShouldBeFalse();
        options.SelfTestSource.ShouldBe(SelfTestSource.CommandLine);
    }

    [Fact]
    public void An_unparsable_environment_value_is_a_usage_error()
    {
        // Silently ignoring it would mean a gate that believes it enabled the
        // self-test and a log that never says otherwise.
        Should.Throw<ArgumentException>(() => DemoStartupOptions.Parse([], "maybe"));
    }

    [Theory]
    [InlineData("--selftst")]
    [InlineData("--selftest=maybe")]
    [InlineData("nonsense")]
    public void A_misspelled_argument_is_a_usage_error(string argument)
    {
        Should.Throw<ArgumentException>(() => DemoStartupOptions.Parse([argument], null));
    }

    [Theory]
    [InlineData("opengl", GraphicsBackend.OpenGL)]
    [InlineData("gl", GraphicsBackend.OpenGL)]
    [InlineData("--d3d11", GraphicsBackend.D3D11)]
    [InlineData("dx11", GraphicsBackend.D3D11)]
    [InlineData("d3d12", GraphicsBackend.D3D12)]
    [InlineData("backend=d3d12", GraphicsBackend.D3D12)]
    [InlineData("--backend=vulkan", GraphicsBackend.Vulkan)]
    public void Backend_spellings_still_parse_as_they_did(string argument, GraphicsBackend expected)
    {
        DemoStartupOptions.Parse([argument], null).Backend.ShouldBe(expected);
    }

    [Fact]
    public void The_backend_defaults_to_opengl()
    {
        DemoStartupOptions.Parse([], null).Backend.ShouldBe(GraphicsBackend.OpenGL);
    }

    [Fact]
    public void The_fullscreen_cycle_is_off_when_nothing_asks_for_it()
    {
        // Same gate, same reason as the self-test above: a window that resizes
        // itself every couple of seconds is right for an automated run and
        // wrong for anybody trying to use the editor.
        DemoStartupOptions.Parse(["d3d12"], null).FullscreenCycleInterval.ShouldBeNull();
    }

    [Theory]
    [InlineData("--fullscreen-cycle")]
    [InlineData("fullscreen-cycle")]
    [InlineData("--fullscreencycle")]
    public void A_bare_fullscreen_cycle_switch_uses_the_harness_default(string argument)
    {
        DemoStartupOptions.Parse([argument], null).FullscreenCycleInterval
            .ShouldBe(TimeSpan.FromSeconds(FullscreenCycleHarness.DefaultIntervalSeconds));
    }

    [Fact]
    public void The_fullscreen_cycle_interval_is_read_invariantly()
    {
        // Parsed with the invariant culture on purpose: this switch is typed by
        // gate scripts, and on a comma-decimal machine a current-culture parse
        // would reject the seconds value every script writes.
        DemoStartupOptions.Parse(["--fullscreen-cycle=0.5"], null).FullscreenCycleInterval
            .ShouldBe(TimeSpan.FromSeconds(0.5));
    }

    [Theory]
    [InlineData("--fullscreen-cycle=0")]
    [InlineData("--fullscreen-cycle=-2")]
    [InlineData("--fullscreen-cycle=soon")]
    public void A_non_positive_or_unparsable_interval_is_a_usage_error(string argument)
    {
        // Clamping a zero would spin the window-mode latch as fast as the event
        // pump runs, which measures nothing and cannot be watched.
        Should.Throw<ArgumentException>(() => DemoStartupOptions.Parse([argument], null));
    }

    [Fact]
    public void The_fullscreen_cycle_survives_the_self_test_environment_path()
    {
        // The three Parse exits each construct the record separately, so the
        // cycle interval has to be threaded through all of them — this pins the
        // one an environment-driven gate run takes.
        DemoStartupOptions options = DemoStartupOptions.Parse(["d3d11", "--fullscreen-cycle=1"], "true");

        options.SelfTestEnabled.ShouldBeTrue();
        options.SelfTestSource.ShouldBe(SelfTestSource.Environment);
        options.Backend.ShouldBe(GraphicsBackend.D3D11);
        options.FullscreenCycleInterval.ShouldBe(TimeSpan.FromSeconds(1));
    }
}
