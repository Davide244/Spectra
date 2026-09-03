using Microsoft.Extensions.Logging;
using Serilog;
using SpectraEngine.Core;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Audio;
using SpectraEngine.Core.Entities;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.D3D11;
using SpectraEngine.Core.Graphics.D3D12;
using SpectraEngine.Core.Graphics.OpenGL;
using SpectraEngine.Core.Graphics.Shaders;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Projects;
using SpectraEngine.Core.Scene;
using SpectraEngine.Entities;
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

    // Loads the built-in entity assembly, whose generated module initializers are
    // what put logic_relay, logic_timer and math_counter in the catalogue. Nothing
    // in this host statically calls into it - a level names those classes as text -
    // so without the anchor a trimmed or AOT publish drops the assembly and every
    // map naming them loads placeholders that behave as nothing. Before anything
    // reads the catalogue, because the first read freezes it.
    BuiltinEntities.EnsureRegistered();

    // Writes what this build knows and EXITS, before a renderer, a window or a
    // thread exists. That is the whole shape of the switch: it is a measurement
    // of the process, not a session, so it must not compete with an engine run
    // for a GPU and must not leave a window open for somebody to close. It runs
    // here rather than later because everything below it is a session.
    //
    // Reading Schemas freezes the shared catalogue, which is correct: the export
    // has to describe every class, and one registered after it would be missing
    // from a file somebody ships.
    if (options.ExportEntitySchemaPath is { } exportPath)
    {
        string destination = Path.GetFullPath(exportPath);
        if (Path.GetDirectoryName(destination) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);

        IReadOnlyList<EntitySchema> exported = EntityCatalog.Shared.Schemas;
        byte[] sentDef = SentDef.Write(exported);
        File.WriteAllBytes(destination, sentDef);

        Log.Information(
            "Exported {Types} entity schema(s) to {Path} ({Bytes} bytes, .sentdef v{Version})",
            exported.Count, destination, sentDef.Length, SentDef.Version);
        return;
    }

    var shaderCompiler = new SpectraShadeCompiler();
    Renderer renderer = options.Backend switch
    {
        GraphicsBackend.D3D11 => new D3D11Renderer(loggerFactory.CreateLogger<D3D11Renderer>(), shaderCompiler),
        GraphicsBackend.D3D12 => new D3D12Renderer(loggerFactory.CreateLogger<D3D12Renderer>(), shaderCompiler),
        GraphicsBackend.OpenGL => new OpenGLRenderer(loggerFactory.CreateLogger<OpenGLRenderer>(), shaderCompiler),
        _ => throw new NotSupportedException($"Backend {options.Backend} is not yet implemented; pick opengl, d3d11, or d3d12."),
    };

    // Opt-in, unlike the editor shell where it is the default: the demo is the
    // measurement instrument, and a frame time under vsync measures the monitor.
    renderer.VSync = options.VSync;

    SceneManager.ScatterGridOverride = options.ScatterGrid;
    SceneManager.PropCountOverride = options.PropCount;
    SceneManager.LoadMapPathOverride = options.LoadMapPath;
    SceneManager.SaveMapPathOverride = options.SaveMapPath;
    SceneManager.SaveProjectPathOverride = options.SaveProjectPath;

    // A project supplies its own content root, so it has to be opened BEFORE the
    // asset manager is built rather than after the scene is. Everything else the
    // project decides (which map boots) is just a path handed to the map switch,
    // which is why there is no separate project-loading machinery below.
    ProjectLayout? project = null;
    if (options.ProjectPath is { } projectPath)
    {
        project = ProjectLayout.Open(projectPath);
        Log.Information(
            "Project '{Name}' opened from {Path}; content root {Assets}, {Maps} map(s) listed",
            project.Project.Name, project.ManifestPath, project.AssetsPath, project.Project.Maps.Count);

        // An explicit --map wins: naming both means "this project, that level",
        // which is what a level designer testing one map wants.
        if (options.LoadMapPath is null && project.Project.StartupMap is { } startup)
            SceneManager.LoadMapPathOverride = project.Resolve(startup);
        else if (options.LoadMapPath is null)
            Log.Warning("Project '{Name}' names no startup map; running the demo scene", project.Project.Name);
    }

    var sceneManager = new SceneManager(loggerFactory.CreateLogger<SceneManager>());
    var assetManager = project is null
        ? new AssetManager(loggerFactory.CreateLogger<AssetManager>())
        : new AssetManager(loggerFactory.CreateLogger<AssetManager>(), project.AssetsPath);
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

    // One frame, then out. The scene is written during the load, which finishes
    // before the first frame is presented, so the earliest snapshot already
    // describes a session whose export is on disk.
    //
    // Through the host's own shutdown latch rather than by closing the window,
    // because a window belongs to the main thread and this handler is raised on
    // the render one. It is the same latch a shell's Exit uses, which is why
    // there is nothing new to get wrong here.
    if (options.ExitAfterSave)
    {
        Log.Information("Exiting after the first frame: --exit-after-save was asked for.");
        engine.Host.FrameCompleted += _ => engine.Host.RequestShutdown();
    }

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
