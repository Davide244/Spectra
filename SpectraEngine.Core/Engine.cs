using Microsoft.Extensions.Logging;
using Silk.NET.Input;
using Silk.NET.Windowing;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Audio;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Core;

public sealed class Engine
{
    private readonly ILogger<Engine> _logger;
    private readonly Renderer _renderer;
    private readonly SceneManager _sceneManager;
    private readonly AssetManager _assetManager;
    private readonly AudioManager _audioManager;
    private readonly InputManager _inputManager;

    private IWindow? _window;

    public Engine(
        ILogger<Engine> logger,
        Renderer renderer,
        SceneManager sceneManager,
        AssetManager assetManager,
        AudioManager audioManager,
        InputManager inputManager)
    {
        _logger = logger;
        _renderer = renderer;
        _sceneManager = sceneManager;
        _assetManager = assetManager;
        _audioManager = audioManager;
        _inputManager = inputManager;
    }

    public void Run()
    {
        _logger.LogInformation("Spectra Engine {Version} starting", EngineInfo.VersionString);

        var options = WindowOptions.Default with
        {
            Title = "Spectra Engine",
            Size = new Silk.NET.Maths.Vector2D<int>(1280, 720),
        };

        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.Closing += OnClosing;

        _window.Run();
        _window.Dispose();

        _logger.LogInformation("Spectra Engine shut down");
    }

    private void OnLoad()
    {
        _assetManager.Initialize();
        _sceneManager.Initialize();
        _audioManager.Initialize();
        _renderer.Initialize(_window!);
        _inputManager.Initialize(_window!.CreateInput());

        _sceneManager.LoadDemoScene(_renderer);

        _logger.LogInformation("All subsystems initialized");
    }

    private void OnUpdate(double deltaTime)
    {
        _inputManager.Update(deltaTime);
        _sceneManager.Update(deltaTime);
    }

    private void OnRender(double deltaTime)
    {
        _renderer.Render(_sceneManager.ActiveScene, deltaTime);
    }

    private void OnClosing()
    {
        _inputManager.Shutdown();
        _audioManager.Shutdown();
        _renderer.Shutdown();
        _sceneManager.Shutdown();
        _assetManager.Shutdown();
    }
}
