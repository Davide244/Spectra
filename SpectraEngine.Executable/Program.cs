using Microsoft.Extensions.Logging;
using Serilog;
using SpectraEngine.Core;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Audio;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.OpenGL;
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

// Create subsystems
var shaderCompiler = new SpectraShadeCompiler();
var renderer = new OpenGLRenderer(loggerFactory.CreateLogger<OpenGLRenderer>(), shaderCompiler);
var sceneManager = new SceneManager(loggerFactory.CreateLogger<SceneManager>());
var assetManager = new AssetManager(loggerFactory.CreateLogger<AssetManager>());
var audioManager = new AudioManager(loggerFactory.CreateLogger<AudioManager>());
var inputManager = new InputManager(loggerFactory.CreateLogger<InputManager>());

// Create and run engine
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
