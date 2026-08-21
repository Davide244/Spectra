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
using SpectraEngine.Executable.Editing;
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
    // CLI: first positional arg picks the graphics backend (default OpenGL).
    // Accepted aliases mirror the SpectraShade compiler CLI for consistency.
    GraphicsBackend backend = ParseBackend(args);

    var shaderCompiler = new SpectraShadeCompiler();
    Renderer renderer = backend switch
    {
        GraphicsBackend.D3D11 => new D3D11Renderer(loggerFactory.CreateLogger<D3D11Renderer>(), shaderCompiler),
        GraphicsBackend.D3D12 => new D3D12Renderer(loggerFactory.CreateLogger<D3D12Renderer>(), shaderCompiler),
        GraphicsBackend.OpenGL => new OpenGLRenderer(loggerFactory.CreateLogger<OpenGLRenderer>(), shaderCompiler),
        _ => throw new NotSupportedException($"Backend {backend} is not yet implemented; pick opengl, d3d11, or d3d12."),
    };

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
    sceneManager.EditorFactory = scene => new DemoEditorHost(
        loggerFactory, scene, renderer, inputManager, sceneManager.SelfTestNode);

    var engine = new Engine(
        loggerFactory.CreateLogger<Engine>(),
        renderer,
        sceneManager,
        assetManager,
        audioManager,
        inputManager);

    // No renderer disposal here: GPU teardown is thread-affine and happens in
    // Renderer.Shutdown on the render thread (Engine handles the crash path too).
    // A render-thread crash is caught and logged inside Engine, so Run returns
    // normally — its return value is the only signal the process gets.
    if (!engine.Run())
        Environment.ExitCode = 1;
}
catch (ArgumentException ex)
{
    // Usage error (unknown backend string from ParseBackend) — the message is
    // self-explanatory, so log without a stack trace.
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

static GraphicsBackend ParseBackend(string[] args)
{
    if (args.Length == 0) return GraphicsBackend.OpenGL;
    string raw = args[0].Trim().TrimStart('-', '/').ToLowerInvariant();
    if (raw.StartsWith("backend="))
        raw = raw["backend=".Length..];
    return raw switch
    {
        "opengl" or "gl" => GraphicsBackend.OpenGL,
        "d3d11" or "dx11" or "directx11" or "hlsl11" => GraphicsBackend.D3D11,
        "d3d12" or "dx12" or "directx12" or "hlsl12" => GraphicsBackend.D3D12,
        "vulkan" or "vk" => GraphicsBackend.Vulkan,
        _ => throw new ArgumentException(
            $"Unknown backend '{args[0]}'. Try: opengl, d3d11, d3d12."),
    };
}
