using SpectraEngine.Core.Graphics;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

// Argument parsing is the one part of this host that is pure — no window, no
// renderer, no GLFW — so the editing suite pins the self-test gate rather than
// trusting a comment about it. Nothing else in this assembly is exposed.
[assembly: InternalsVisibleTo("SpectraEngine.Editing.Tests")]

namespace SpectraEngine.Executable;

/// <summary>
/// Where the demo's self-test switch came from. Reported at startup so a log
/// says not just that the synthetic editing run is on but who asked for it.
/// </summary>
internal enum SelfTestSource
{
    /// <summary>Nobody asked; the self-test is off, which is the default.</summary>
    Default,

    /// <summary>A command-line switch decided it.</summary>
    CommandLine,

    /// <summary>The environment variable decided it.</summary>
    Environment,
}

/// <summary>
/// Everything the demo host takes from its command line: which graphics
/// backend to build, and whether to run the synthetic editing self-test.
/// </summary>
/// <remarks>
/// <b>The self-test defaults to OFF, and that is a correctness rule rather
/// than a preference.</b> <see cref="Editing.EditingSelfTest"/> is gate
/// instrumentation: it really drags a real brush node a whole world unit and
/// leaves it there for the frames the async recompile needs, so with it on the
/// demo scene visibly pops every few seconds with no human touching anything.
/// That is exactly right for a smoke gate and exactly wrong for somebody
/// looking at the editor, so it has to be asked for — <c>--selftest</c>, or
/// <see cref="SelfTestEnvironmentVariable"/> for a harness that would rather
/// not rewrite an argument list.
/// <para>
/// <b>Parsing lives here, not in <c>Program</c>, because it is testable.</b>
/// Handing in the environment value rather than reading it makes the whole
/// decision a pure function of two arguments, which is what lets a headless
/// test pin "off unless asked" for good.
/// </para>
/// </remarks>
internal sealed record DemoStartupOptions(
    GraphicsBackend Backend,
    bool SelfTestEnabled,
    SelfTestSource SelfTestSource,
    TimeSpan? FullscreenCycleInterval = null,
    bool StartInPlayMode = false,
    bool OffscreenProbe = false,
    string? Pipeline = null,
    bool Shadows = true,
    bool Profile = false)
{
    /// <summary>
    /// Environment variable read when no command-line switch names the
    /// self-test: any of the accepted truthy spellings turns it on.
    /// </summary>
    public const string SelfTestEnvironmentVariable = "SPECTRA_SELFTEST";

    /// <summary>One line of usage, appended to every argument error.</summary>
    private const string Usage =
        "Usage: SpectraEngine.Executable [opengl|d3d11|d3d12] [--selftest[=true|false]] " +
        "[--fullscreen-cycle[=seconds]] [--play[=true|false]] [--offscreen-probe[=true|false]] " +
        "[--pipeline=<name>] [--shadows[=true|false]] [--profile[=true|false]].";

    /// <summary>
    /// Resolves the command line (and the self-test environment variable) into
    /// the options the host runs with.
    /// </summary>
    /// <param name="args">The process arguments, in order; may be empty.</param>
    /// <param name="selfTestEnvironmentValue">
    /// The value of <see cref="SelfTestEnvironmentVariable"/>, or null/empty if
    /// it is not set. Consulted only when no command-line switch names the
    /// self-test, so an explicit <c>--selftest=false</c> always wins.
    /// </param>
    /// <returns>The parsed options; the backend defaults to OpenGL.</returns>
    /// <exception cref="ArgumentException">
    /// An argument is not a recognised backend or switch, or a switch carries a
    /// value that is not a boolean. Program turns this into a logged usage
    /// error rather than a stack trace.
    /// </exception>
    public static DemoStartupOptions Parse(IReadOnlyList<string> args, string? selfTestEnvironmentValue)
    {
        ArgumentNullException.ThrowIfNull(args);

        GraphicsBackend? backend = null;
        bool? selfTest = null;
        bool play = false;
        bool offscreenProbe = false;
        string? pipeline = null;
        bool shadows = true;
        bool profile = false;
        TimeSpan? fullscreenCycle = null;

        for (int i = 0; i < args.Count; i++)
        {
            string raw = args[i];
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            // Leading dashes and slashes are optional and interchangeable, and
            // `name=value` is accepted for every switch — the same shape the
            // pre-existing `backend=` spelling used, kept so an old command
            // line still runs.
            string token = raw.Trim();
            string body = token.TrimStart('-', '/');
            int equals = body.IndexOf('=');
            string name = (equals < 0 ? body : body[..equals]).ToLowerInvariant();
            string? value = equals < 0 ? null : body[(equals + 1)..];

            switch (name)
            {
                case "selftest" or "self-test":
                    selfTest = ParseBoolean(value, token);
                    continue;

                case "backend":
                    backend = ParseBackend(value ?? string.Empty, token);
                    continue;

                case "fullscreen-cycle" or "fullscreencycle":
                    fullscreenCycle = ParseInterval(value, token);
                    continue;

                // Enters play mode as the scene finishes loading rather than
                // waiting for F8. Off by default for the same reason the
                // self-test is: the resting state of this host is an editor, and
                // a build that seizes the cursor on its own is a surprise.
                case "play":
                    play = ParseBoolean(value, token);
                    continue;

                // Renders each of a handful of startup frames into an offscreen
                // target as well as the window, which is the only coverage the
                // D3D render-target paths get: those two backends have no
                // headless device fixture, and their failure modes are debug
                // layer messages rather than wrong pixels. Off by default
                // because it draws the scene twice while it runs.
                case "offscreen-probe" or "offscreenprobe":
                    offscreenProbe = ParseBoolean(value, token);
                    continue;

                // Which rendering strategy the run starts on, by name. The
                // rotation key is fine for a person and useless to an
                // unattended run, so a pipeline that is never selected is a
                // pipeline nothing ever gates. Names are not validated here:
                // the set is the renderer's, it differs per backend, and this
                // type deliberately knows nothing about either.
                case "pipeline":
                    pipeline = ParseName(value, token);
                    continue;

                // Shadows off, for measuring what they cost and for telling a
                // shadow bug from a lighting one in a single run.
                case "shadows":
                    shadows = ParseBoolean(value, token);
                    continue;

                // Per-phase frame timing in the periodic stats line. Off by
                // default because a profile nobody reads is still work, and the
                // scopes would otherwise be paid for in a shipped game.
                case "profile":
                    profile = ParseBoolean(value, token);
                    continue;
            }

            // Anything else is the positional backend — once. A second one is
            // a typo worth failing on rather than silently ignoring, which is
            // how a misspelled switch used to disappear.
            if (backend is not null)
                throw new ArgumentException($"Unexpected argument '{token}'. {Usage}");

            backend = ParseBackend(body, token);
        }

        if (selfTest is bool fromCommandLine)
            return new DemoStartupOptions(
                backend ?? GraphicsBackend.OpenGL, fromCommandLine, SelfTestSource.CommandLine,
                fullscreenCycle, play, offscreenProbe, pipeline, shadows, profile);

        if (!string.IsNullOrWhiteSpace(selfTestEnvironmentValue))
        {
            bool fromEnvironment = ParseBoolean(
                selfTestEnvironmentValue.Trim(), SelfTestEnvironmentVariable);
            return new DemoStartupOptions(
                backend ?? GraphicsBackend.OpenGL, fromEnvironment, SelfTestSource.Environment,
                fullscreenCycle, play, offscreenProbe, pipeline, shadows, profile);
        }

        return new DemoStartupOptions(
            backend ?? GraphicsBackend.OpenGL, false, SelfTestSource.Default,
            fullscreenCycle, play, offscreenProbe, pipeline, shadows, profile);
    }

    // A switch that takes a name needs one: a bare --pipeline says nothing
    // about which, and silently meaning "leave it alone" would make a typo in
    // the name look like a working run of the default pipeline.
    private static string ParseName(string? value, string origin)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"'{origin}' needs a value, e.g. --pipeline=deferred. {Usage}");

        return value.Trim();
    }

    // Aliases mirror the SpectraShade compiler CLI for consistency.
    private static GraphicsBackend ParseBackend(string value, string token) =>
        value.ToLowerInvariant() switch
        {
            "opengl" or "gl" => GraphicsBackend.OpenGL,
            "d3d11" or "dx11" or "directx11" or "hlsl11" => GraphicsBackend.D3D11,
            "d3d12" or "dx12" or "directx12" or "hlsl12" => GraphicsBackend.D3D12,
            "vulkan" or "vk" => GraphicsBackend.Vulkan,
            _ => throw new ArgumentException($"Unknown backend '{token}'. Try: opengl, d3d11, d3d12."),
        };

    // A bare --fullscreen-cycle means the harness's own default interval; a
    // value overrides it. Zero or negative would spin the window-mode latch as
    // fast as the pump runs, which measures nothing and cannot be watched, so
    // it is refused rather than clamped.
    private static TimeSpan ParseInterval(string? value, string origin)
    {
        if (value is null)
            return TimeSpan.FromSeconds(FullscreenCycleHarness.DefaultIntervalSeconds);

        if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            || double.IsNaN(seconds) || seconds <= 0.0)
        {
            throw new ArgumentException(string.Format(
                CultureInfo.InvariantCulture,
                "'{0}' expects a positive number of seconds, not '{1}'. {2}", origin, value, Usage));
        }

        return TimeSpan.FromSeconds(seconds);
    }

    // A bare switch means "on"; an explicit value is honoured so a harness can
    // pass --selftest=false to override an inherited environment variable.
    private static bool ParseBoolean(string? value, string origin)
    {
        if (value is null)
            return true;

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => throw new ArgumentException(string.Format(
                CultureInfo.InvariantCulture,
                "'{0}' expects a boolean (true/false, 1/0, yes/no, on/off), not '{1}'. {2}",
                origin, value, Usage)),
        };
    }
}
