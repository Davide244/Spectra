using Microsoft.Extensions.Logging;
using Silk.NET.Input;
using Silk.NET.Windowing;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Audio;
using SpectraEngine.Core.Diagnostics;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using System.Diagnostics;
using System.Threading;

namespace SpectraEngine.Core;

public sealed class Engine
{
    private const string WindowTitle = "Spectra Engine";

    private readonly ILogger<Engine> _logger;
    private readonly FpsCounter _fpsCounter = new();
    private readonly Renderer _renderer;
    private readonly SceneManager _sceneManager;
    private readonly AssetManager _assetManager;
    private readonly AudioManager _audioManager;
    private readonly InputManager _inputManager;

    private IWindow? _window;
    private FlyCameraController? _cameraController;

    // The render thread publishes its latest title here; the OS-event thread
    // applies it, because GLFW window calls must run on the main thread.
    private volatile string _pendingTitle = WindowTitle;

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
            Title = WindowTitle,
            Size = new Silk.NET.Maths.Vector2D<int>(1280, 720),
            VSync = false,
            FramesPerSecond = 0,
            UpdatesPerSecond = 0,
        };

        _window = Window.Create(options);
        _window.Initialize();

        // Subsystems that touch no GPU state are set up on this (OS-event) thread.
        _assetManager.Initialize();
        _sceneManager.Initialize();
        _audioManager.Initialize();
        _inputManager.Initialize(_window.CreateInput());

        // Release the GL context here so the render thread can take ownership.
        _window.GLContext?.Clear();

        var renderThread = new Thread(RenderLoop)
        {
            Name = "Spectra Render",
        };
        renderThread.Start();

        // Window events must be pumped on the thread that created the window.
        // While Windows runs its modal move/size loop during a title-bar drag,
        // this thread blocks inside DoEvents — but the render thread keeps
        // running, so the game no longer freezes.
        string appliedTitle = WindowTitle;
        while (!_window.IsClosing)
        {
            _window.DoEvents();

            string pending = _pendingTitle;
            if (!ReferenceEquals(pending, appliedTitle))
            {
                _window.Title = pending;
                appliedTitle = pending;
            }

            Thread.Sleep(1);
        }

        renderThread.Join();

        _inputManager.Shutdown();
        _audioManager.Shutdown();
        _sceneManager.Shutdown();
        _assetManager.Shutdown();

        _window.Reset();
        _window.Dispose();

        _logger.LogInformation("Spectra Engine shut down");
    }

    // Runs on the dedicated render thread: owns the GL context, drives update
    // and render, and presents each frame.
    private void RenderLoop()
    {
        var window = _window!;
        window.GLContext?.MakeCurrent();
        window.VSync = false;

        _renderer.Initialize(window);
        _sceneManager.LoadDemoScene(_renderer);

        if (_sceneManager.ActiveScene is { } activeScene)
            _cameraController = new FlyCameraController(activeScene.Camera, _inputManager);

        _logger.LogInformation("All subsystems initialized");

        var clock = Stopwatch.StartNew();
        double previous = clock.Elapsed.TotalSeconds;

        while (!window.IsClosing)
        {
            double now = clock.Elapsed.TotalSeconds;
            double deltaTime = now - previous;
            previous = now;

            _inputManager.Update(deltaTime);
            _cameraController?.Update(deltaTime);
            _sceneManager.Update(deltaTime);
            _renderer.Render(_sceneManager.ActiveScene, deltaTime);

            if (_fpsCounter.Tick(deltaTime))
            {
                _pendingTitle =
                    $"{WindowTitle}  —  {_fpsCounter.Fps:0} FPS  ({_fpsCounter.FrameTimeMs:0.00} ms)";
            }

            window.GLContext?.SwapBuffers();
        }

        _renderer.Shutdown();
        window.GLContext?.Clear();
    }
}
