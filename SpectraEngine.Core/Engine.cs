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

    // Upper bound on a single simulation step. A long stall (CSG rebuild,
    // alt-tab, modal title-bar drag) would otherwise integrate into one huge
    // step and teleport the fly camera.
    private const double MaxDeltaTime = 0.1;

    private readonly ILogger<Engine> _logger;
    private readonly FpsCounter _fpsCounter = new();
    private readonly Renderer _renderer;
    private readonly SceneManager _sceneManager;
    private readonly AssetManager _assetManager;
    private readonly AudioManager _audioManager;
    private readonly InputManager _inputManager;

    private IWindow? _window;
    private FlyCameraController? _cameraController;
    private DebugVisualization _debugFlags = DebugVisualization.None;

    // The engine-owned per-frame draw list: rebuilt (in place, capacity
    // retained) once per frame by the render thread and handed to the
    // renderer, so backend pipelines iterate a flat, frustum-culled item list
    // instead of each walking the scene graph themselves.
    private readonly RenderView _renderView = new();

    // The render thread publishes its latest title here; the OS-event thread
    // applies it, because GLFW window calls must run on the main thread.
    private volatile string _pendingTitle = WindowTitle;

    // Mirror latch of the window's close request. GLFW window queries such as
    // IsClosing are main-thread-only, so the main loop latches the value here
    // and the render loop polls the flag instead of touching IWindow.
    private volatile bool _closeRequested;

    // Raised by the render thread on ANY exit — normal or crash — so the main
    // loop stops pumping events instead of spinning forever on a window that
    // nobody renders to anymore.
    private volatile bool _renderThreadExited;

    // Set by the render thread's catch handler so Run can report the failure
    // to its caller — otherwise a render-thread crash would look like a clean
    // shutdown and the process would exit 0.
    private volatile bool _renderThreadFaulted;

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

    /// <summary>
    /// Creates the window, runs the render thread, and pumps OS events until
    /// shutdown. Returns <c>true</c> on a clean shutdown; <c>false</c> when
    /// the render thread died on an exception (already logged), so the caller
    /// can report a nonzero process exit code.
    /// </summary>
    public bool Run()
    {
        _logger.LogInformation("Spectra Engine {Version} starting", EngineInfo.VersionString);

        // Must precede Window.Create and CreateInput below. Silk.NET otherwise
        // discovers its GLFW backends by reflection, which a NativeAOT publish
        // trims away — see SilkPlatform for the full story.
        SilkPlatform.EnsureRegistered();

        var options = WindowOptions.Default with
        {
            Title = WindowTitle,
            Size = new Silk.NET.Maths.Vector2D<int>(1280, 720),
            VSync = false,
            FramesPerSecond = 0,
            UpdatesPerSecond = 0,
            API = _renderer.WindowApi,
        };

        _window = Window.Create(options);
        _window.Initialize();

        // VSync goes through the window/context (glfwSwapInterval), which is a
        // main-thread call — set it here, not on the render thread. For OpenGL
        // the fresh context is still current on this thread at this point.
        _window.VSync = false;

        // Subsystems that touch no GPU state are set up on this (OS-event) thread.
        _assetManager.Initialize();
        _sceneManager.Initialize();
        _audioManager.Initialize();
        _inputManager.Initialize(_window.CreateInput());

        // Seed the renderer's framebuffer-size latch while we are still the
        // only thread, then keep it fresh from the resize event (which fires
        // during DoEvents on this thread). GLFW gives no thread-safety
        // guarantee for size queries, so the render side only ever reads the
        // latch and never touches IWindow.
        _renderer.SetFramebufferSize(_window.FramebufferSize);
        _window.FramebufferResize += _renderer.SetFramebufferSize;

        // Focus changes fire during DoEvents, i.e. on this thread — which is
        // the only thread allowed to touch the cursor. A focus loss has to
        // release a freelook capture immediately (and drop the held keys whose
        // key-up went to whoever stole the focus), so it is handled inside the
        // input manager rather than latched for the render thread to notice a
        // frame later.
        _window.FocusChanged += _inputManager.OnWindowFocusChanged;

        // Release any thread-affine context (OpenGL) here so the render thread
        // can take ownership. Backends without one (D3D, Vulkan) no-op.
        _renderer.ReleaseContext(_window);

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
        while (!_window.IsClosing && !_renderThreadExited)
        {
            _window.DoEvents();

            // Latch the close request for the render loop, which must not
            // query IsClosing itself (GLFW window reads are main-thread-only).
            if (_window.IsClosing)
                _closeRequested = true;

            string pending = _pendingTitle;
            if (!ReferenceEquals(pending, appliedTitle))
            {
                _window.Title = pending;
                appliedTitle = pending;
            }

            // The third main-thread latch, applied in the same slot and for the
            // same reason as the title above: the editor camera asks for a
            // locked cursor from the render thread, and GLFW only accepts
            // cursor calls here. A no-op on every frame but the two that begin
            // and end a freelook.
            _inputManager.ApplyPendingCursorMode();

            Thread.Sleep(1);
        }

        // Whatever ended the pump (close request or render-thread death), the
        // render loop only watches this flag — raise it before joining.
        _closeRequested = true;
        renderThread.Join();

        _inputManager.Shutdown();
        _audioManager.Shutdown();
        _sceneManager.Shutdown();
        _assetManager.Shutdown();

        // Dispose already destroys the window; a preceding Reset would be redundant.
        _window.Dispose();

        _logger.LogInformation("Spectra Engine shut down");
        return !_renderThreadFaulted;
    }

    // Runs on the dedicated render thread: owns the GL context, drives update
    // and render, and presents each frame. Exceptions must not escape: the
    // thread is non-background, so an unhandled throw would kill the process
    // without a fatal log entry or a log flush.
    private void RenderLoop()
    {
        var window = _window!;
        try
        {
            _renderer.AcquireContext(window);

            _renderer.Initialize(window);

            // GPU-side asset start-up belongs here, not in the main-thread
            // Initialize above: the placeholder texture is a GPU resource, so
            // it has to be created on the thread that owns the context.
            _assetManager.AttachRenderer(_renderer);

            _sceneManager.LoadDemoScene(_renderer, _assetManager);

            if (_sceneManager.ActiveScene is { } activeScene)
                _cameraController = new FlyCameraController(activeScene.Camera, _inputManager);

            _logger.LogInformation("All subsystems initialized");

            var clock = Stopwatch.StartNew();
            double previous = clock.Elapsed.TotalSeconds;

            while (!_closeRequested)
            {
                double now = clock.Elapsed.TotalSeconds;
                double rawDelta = now - previous;
                previous = now;

                // Clamp only the simulation step — a long stall must not
                // teleport the camera. The
                // counter gets the RAW delta
                // below: clamping it would report a 600 ms CSG-rebuild frame
                // as 100 ms, hiding exactly the stalls it exists to reveal.
                double deltaTime = Math.Min(rawDelta, MaxDeltaTime);

                _inputManager.Update(deltaTime);

                // The editor, when the host installed one, gets the frame
                // first: it owns selection, manipulation and — on the frames it
                // says so — navigation. It runs before the scene update so the
                // camera and any gizmo edit are final by the time the draw list
                // is built from them. A host without an editor (a shipped game)
                // simply keeps the fly camera.
                ISceneEditor? editor = _sceneManager.Editor;
                bool editorNavigated = editor is not null && editor.Update(deltaTime);
                if (!editorNavigated)
                    _cameraController?.Update(deltaTime);

                // The demo update animates and logs; it gets last frame's
                // render view for its culling stats alongside time.
                _sceneManager.Update(deltaTime, _renderView);

                // Drive the async static-world pipeline: harvest a finished
                // background compile (the swap and GPU mesh creation happen
                // here, on the render thread) and launch the next compile when
                // brush nodes have changed since the last one.
                _sceneManager.ActiveScene?.ProcessStaticWorldCompilation(_renderer, _logger);

                // Same shape, for content: background decodes hand their pixel
                // buffers over here and the GPU textures are created on this
                // thread. Costs nothing on a frame with nothing pending.
                _assetManager.PumpPendingUploads();

                // F1–F5 toggle debug visualisations on/off.
                if (_inputManager.WasKeyPressed(Key.F1)) _debugFlags ^= DebugVisualization.Wireframe;
                if (_inputManager.WasKeyPressed(Key.F2)) _debugFlags ^= DebugVisualization.Vertices;
                if (_inputManager.WasKeyPressed(Key.F3)) _debugFlags ^= DebugVisualization.Aabbs;
                if (_inputManager.WasKeyPressed(Key.F4)) _debugFlags ^= DebugVisualization.Normals;
                if (_inputManager.WasKeyPressed(Key.F5)) _debugFlags ^= DebugVisualization.SceneGraph;

                // F6 cycles render pipelines (Forward, Wireframe, ...).
                if (_inputManager.WasKeyPressed(Key.F6))
                    _renderer.NextPipeline();

                _renderer.DebugDraw.Clear();
                if (_sceneManager.ActiveScene is { } scene)
                {
                    // The selection highlight is editor UI, not a debug
                    // visualisation — it draws whenever something is selected,
                    // with no F-key opt-in (and costs nothing when nothing is).
                    DebugVisualizations.DrawSelectionHighlight(_renderer.DebugDraw, scene);

                    // The manipulator and the marquee ride the same depth-off
                    // line path as the highlight, which is what makes a gizmo
                    // grabbable on a handle buried inside the brush it moves:
                    // the handles are drawn on top of everything, so what you
                    // can see is exactly what you can pick.
                    editor?.Draw(_renderer.DebugDraw);

                    if (_debugFlags != DebugVisualization.None)
                        DebugVisualizations.Draw(_renderer.DebugDraw, scene, _debugFlags);
                }

                // Build this frame's draw list AFTER the scene update and the
                // static-world pump so it sees final transforms and the newest
                // compiled world. The camera's aspect ratio is seeded from the
                // framebuffer latch first so the culling frustum matches the
                // projection the pipeline will render with this frame (the
                // pipelines write the same value from the same latch).
                if (_sceneManager.ActiveScene is { } viewScene)
                {
                    var framebuffer = _renderer.FramebufferSize;
                    if (framebuffer.Y > 0)
                        viewScene.Camera.AspectRatio = framebuffer.X / (float)framebuffer.Y;
                    viewScene.BuildRenderView(viewScene.Camera, _renderView);
                }
                else
                {
                    _renderView.Clear();
                }

                _renderer.Render(_sceneManager.ActiveScene, _renderView, deltaTime);

                if (_fpsCounter.Tick(rawDelta))
                {
                    _pendingTitle =
                        $"{WindowTitle}  —  {_fpsCounter.Fps:0} FPS  ({_fpsCounter.FrameTimeMs:0.00} ms)  —  {_renderer.CurrentPipelineName}";
                }

                _renderer.Present(window);
            }

            // Asset-owned textures are destroyed through the renderer, so they
            // have to go before it shuts down — and on this thread.
            _assetManager.ReleaseGraphicsResources();
            _renderer.Shutdown();
            _renderer.ReleaseContext(window);
        }
        catch (Exception ex)
        {
            _renderThreadFaulted = true;
            _logger.LogCritical(ex, "Render thread crashed; shutting down");

            // Best-effort GPU teardown on the thread that owns the context. A
            // second failure here must not mask the original crash.
            try
            {
                _assetManager.ReleaseGraphicsResources();
                _renderer.Shutdown();
                _renderer.ReleaseContext(window);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogError(cleanupEx, "Renderer teardown after render-thread crash also failed");
            }
        }
        finally
        {
            // Normal exit or crash: either way the main loop must stop pumping
            // OS events and let the process shut down.
            _renderThreadExited = true;
        }
    }
}
