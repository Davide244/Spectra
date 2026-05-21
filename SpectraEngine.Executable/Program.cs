using Microsoft.Extensions.Logging;
using Serilog;
using SpectraEngine.Core;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Audio;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.D3D11;
using SpectraEngine.Core.Graphics.OpenGL;
using SpectraEngine.Core.Graphics.Shaders;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
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

// CLI: first positional arg picks the graphics backend (default OpenGL).
// Accepted aliases mirror the SpectraShade compiler CLI for consistency.
GraphicsBackend backend = ParseBackend(args);

var shaderCompiler = new SpectraShadeCompiler();
Renderer renderer = backend switch
{
    GraphicsBackend.D3D11 => new D3D11Renderer(loggerFactory.CreateLogger<D3D11Renderer>(), shaderCompiler),
    GraphicsBackend.OpenGL => new OpenGLRenderer(loggerFactory.CreateLogger<OpenGLRenderer>(), shaderCompiler),
    _ => throw new NotSupportedException($"Backend {backend} is not yet implemented; pick opengl or d3d11."),
};

var sceneManager = new SceneManager(loggerFactory.CreateLogger<SceneManager>());
var assetManager = new AssetManager(loggerFactory.CreateLogger<AssetManager>());
var audioManager = new AudioManager(loggerFactory.CreateLogger<AudioManager>());
var inputManager = new InputManager(loggerFactory.CreateLogger<InputManager>());

var engine = new Engine(
    loggerFactory.CreateLogger<Engine>(),
    renderer,
    sceneManager,
    assetManager,
    audioManager,
    inputManager);

try
{
    engine.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Engine terminated unexpectedly");
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
            $"Unknown backend '{args[0]}'. Try: opengl, d3d11."),
    };
}
