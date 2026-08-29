using Microsoft.Extensions.Logging;
using Serilog;
using SpectraEngine.Core;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Audio;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.D3D11;
using SpectraEngine.Core.Graphics.D3D12;
using SpectraEngine.Core.Graphics.OpenGL;
using SpectraEngine.Core.Graphics.Shaders;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Executable;
using SpectraEngine.Editing.Hosting;
using SpectraEngine.Executable.Editing;
using SpectraEngine.Physics.Box3D;
using SpectraShade.Compiler;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.Debug()
    .WriteTo.File("logs/spectra-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddSerilog(dispose: false);
});

// Everything — argument parsing included — runs inside the try so that a bad
// CLI argument or a constructor failure ends as a logged fatal with a flushed
// log file instead of an unhandled exception with a raw stack trace.
try
{
    // CLI: a positional arg picks the graphics backend (default OpenGL), and
    // --selftest opts into the synthetic editing run (default OFF). See
    // DemoStartupOptions for both, and for why the default is what it is.
    DemoStartupOptions options = DemoStartupOptions.Parse(
        args, Environment.GetEnvironmentVariable(DemoStartupOptions.SelfTestEnvironmentVariable));

    // Said once, loudly, before anything moves: with the self-test on, a brush
    // node in the demo scene really does jump a unit every few seconds with no
    // human touching it. A gate run should document itself; an interactive run
    // should never leave anyone wondering why the scene twitches.
    if (options.SelfTestEnabled)
    {
        Log.Information(
            "Editing self-test ENABLED (from {Source}): every {Interval:0.#} s the demo drives a synthetic " +
            "pick/grab/drag/commit/undo/redo on the self-test brush node and logs one 'Editing self-test: PASS' " +
            "line. That node visibly moves ~1 unit for a handful of frames per run while the static world " +
            "recompiles — that motion IS the test. Drop --selftest (and {EnvVar}) for a scene nothing synthetic touches.",
            options.SelfTestSource == SelfTestSource.Environment
                ? DemoStartupOptions.SelfTestEnvironmentVariable
                : "--selftest",
            EditingSelfTest.IntervalSeconds,
            DemoStartupOptions.SelfTestEnvironmentVariable);
    }

    var shaderCompiler = new SpectraShadeCompiler();
    Renderer renderer = options.Backend switch
    {
        GraphicsBackend.D3D11 => new D3D11Renderer(loggerFactory.CreateLogger<D3D11Renderer>(), shaderCompiler),
        GraphicsBackend.D3D12 => new D3D12Renderer(loggerFactory.CreateLogger<D3D12Renderer>(), shaderCompiler),
        GraphicsBackend.OpenGL => new OpenGLRenderer(loggerFactory.CreateLogger<OpenGLRenderer>(), shaderCompiler),
        _ => throw new NotSupportedException($"Backend {options.Backend} is not yet implemented; pick opengl, d3d11, or d3d12."),
    };

    SceneManager.ScatterGridOverride = options.ScatterGrid;
    SceneManager.PropCountOverride = options.PropCount;
    SceneManager.LoadMapPathOverride = options.LoadMapPath;
    SceneManager.SaveMapPathOverride = options.SaveMapPath;
    var sceneManager = new SceneManager(loggerFactory.CreateLogger<SceneManager>());
    var assetManager = new AssetManager(loggerFactory.CreateLogger<AssetManager>());
    var audioManager = new AudioManager(loggerFactory.CreateLogger<AudioManager>());
    var inputManager = new InputManager(loggerFactory.CreateLogger<InputManager>());

    // This host is the demo's editor, so it installs the editing layer. The
    // engine drives it through Core's ISceneEditor seam — Core cannot name
    // SpectraEngine.Editing, which is exactly what keeps gizmo/undo/tool code
    // out of a shipped game binary that simply never sets this.
    //
    // A factory rather than an instance because the scene does not exist until
    // the render thread has built it; SceneManager invokes this once, on that
    // thread, with the finished scene.
    //
    // The self-test node is handed over only when the switch asked for it: the
    // host skips the whole synthetic run on a null subject, so opting out here
    // is the entire gate.
    sceneManager.EditorFactory = scene => new SceneEditorHost(
        loggerFactory, scene, renderer, inputManager,
        options.SelfTestEnabled && sceneManager.SelfTestNode is { } subject
            ? new EditingSelfTest(loggerFactory.CreateLogger<EditingSelfTest>(), scene, subject)
            : null);

    // Physics, through the same seam shape and for a different reason: gizmos
    // must never ship in a game binary, whereas physics must — what this
    // factory buys is that Core never needs box3d.dll to resolve, so the
    // compiler tests, the BSP tests and a shader-only tool build stay clean.
    //
    // A host that sets nothing gets NullScenePhysics, which is a supported
    // configuration and exactly what Edit mode is. This one sets it, so the
    // demo compiles its authored brushes into real collision every time the
    // static world changes.
    sceneManager.PhysicsFactory = _ =>
        new Box3DScenePhysics(loggerFactory.CreateLogger<Box3DScenePhysics>());

    var engine = new Engine(
        loggerFactory.CreateLogger<Engine>(),
        renderer,
        sceneManager,
        assetManager,
        audioManager,
        inputManager)
    {
        // F8 does this interactively; the switch is for a smoke run that wants
        // the mover exercised without a human at the keyboard.
        StartInPlayMode = options.StartInPlayMode,
        RunOffscreenProbe = options.OffscreenProbe,
        StartupPipeline = options.Pipeline,
        ShadowsEnabled = options.Shadows,
        ProfileFrames = options.Profile,
        DebugLayer = options.DebugLayer,
        PreferredAdapter = options.Adapter,
        WindowSize = options.WindowSize,
    };

    // The fullscreen-cycle harness, when asked for: a driver thread that hits
    // the window-mode latch on a timer so an unattended run performs the exact
    // windowed <-> fullscreen transition a human does with F11. It only
    // requests; Engine.Run reshapes the window on the window thread while the
    // render thread presents and resizes the swap chain, which is the overlap
    // being gated. Disposed with the engine so the thread cannot outlive it.
    using var fullscreenCycle = options.FullscreenCycleInterval is { } cycleInterval
        ? new FullscreenCycleHarness(
            loggerFactory.CreateLogger<FullscreenCycleHarness>(), engine.WindowMode, cycleInterval)
        : null;

    if (options.FullscreenCycleInterval is { } describedInterval)
        Log.Information("{Message}", FullscreenCycleHarness.DescribeStartup(describedInterval));

    // No renderer disposal here: GPU teardown is thread-affine and happens in
    // Renderer.Shutdown on the render thread (Engine handles the crash path too).
    // A render-thread crash is caught and logged inside Engine, so Run returns
    // normally — its return value is the only signal the process gets.
    if (!engine.Run())
        Environment.ExitCode = 1;
}
catch (ArgumentException ex)
{
    // Usage error (an unknown backend or switch from DemoStartupOptions.Parse)
    // — the message is self-explanatory, so log without a stack trace.
    Log.Fatal("{Message}", ex.Message);
    Environment.ExitCode = 2;
}
catch (NotSupportedException ex)
{
    // Recognized but unimplemented backend (e.g. vulkan) — also a usage error.
    Log.Fatal("{Message}", ex.Message);
    Environment.ExitCode = 2;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Engine terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}
