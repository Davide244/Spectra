using Microsoft.Extensions.Logging;
using Silk.NET.Input;
using Silk.NET.Windowing;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Audio;
using SpectraEngine.Core.Diagnostics;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Hosting;
using SpectraEngine.Core.Inspection;
using SpectraEngine.Core.Physics;
using SpectraEngine.Core.Physics.Character;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Core.Windowing;
using System;
using System.Collections.Generic;
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
    private OffscreenProbe? _offscreenProbe;
    private ViewportCompareProbe? _viewportCompare;
    private SharedPacingProbe? _sharedPacing;
    private readonly FpsCounter _fpsCounter = new();

    /// <summary>
    /// Where each frame's time goes, phase by phase. Lives on the renderer,
    /// because that is where most of the phases are.
    /// </summary>
    public FrameProfiler Profiler => _renderer.Profiler;
    private readonly Renderer _renderer;
    private readonly SceneManager _sceneManager;
    private readonly AssetManager _assetManager;
    private readonly AudioManager _audioManager;
    private readonly InputManager _inputManager;
    private readonly WindowModeLatch _windowModeLatch;

    // Null in embedded mode, where a shell owns the window and hands the engine
    // nothing but a surface. Everything that reads it is window OWNERSHIP work
    // (title, cursor, event pump, fullscreen), which is exactly what an embedded
    // host keeps for itself.
    private IWindow? _window;

    private IRenderSurface? _surface;
    private Thread? _renderThread;
    private FlyCameraController? _cameraController;
    private FirstPersonController? _character;
    private DebugVisualization _debugFlags = DebugVisualization.None;

    // The capsule overlay is its own toggle rather than a DebugVisualization
    // bit: everything in that enum draws from the scene, and the character is
    // not in the scene graph. Off by default because in first person the capsule
    // is drawn around your own head, which is a face full of lines.
    private bool _drawCharacter;

    // The engine-owned per-frame draw list: rebuilt (in place, capacity
    // retained) once per frame by the render thread and handed to the
    // renderer, so backend pipelines iterate a flat, frustum-culled item list
    // instead of each walking the scene graph themselves.
    private readonly RenderView _renderView = new();

    // Turns the variable frame delta into whole fixed physics ticks plus the
    // leftover fraction render interpolation blends with. Engine-owned rather
    // than backend-owned, so that "one tick is one tick" is true across a
    // backend swap and remains true for the null backend.
    private readonly FixedTickAccumulator _physicsTicks = new();

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
        _windowModeLatch = new WindowModeLatch(logger);
        Host = new EngineHost(logger);
        Host.AttachInput(inputManager);
    }

    // Reused across publishes so a steady state with an unchanging selection
    // allocates nothing for it: the list is only rebuilt when the selection
    // actually differs from what the previous snapshot carried.
    private readonly List<Guid> _snapshotSelection = [];
    private Guid[] _publishedSelection = [];

    // What the last PUBLISHED snapshot carried, so a count that moved between
    // publishes forces the next one out rather than waiting for the clock.
    private int _publishedDebugLayerErrors;

    // The shared colour target as of the last publish, and the generation that
    // decided it. A composited host imports on the generation and nothing else.
    private Renderer.SharedTargetHandle? _publishedSharedTarget;
    private int _publishedSharedGeneration;

    /// <summary>
    /// Builds and publishes this frame's snapshot, if one is due. Render thread
    /// only, and the only place engine state is read for a UI.
    /// </summary>
    private void PublishHostFrame(TimeSpan elapsed)
    {
        // Read once, outside the builder, because the builder runs only when a
        // snapshot is actually going out and this is what decides whether one
        // is due at all.
        bool interacting = _sceneManager.Editor?.IsInteracting ?? false;

        // A debug-layer error is news in exactly the sense a command is: the
        // count moves only when the layer has just reported something wrong, and
        // a shell that hears about it up to a whole interval later cannot tie it
        // to whatever the user was doing when it happened. Compared BEFORE the
        // builder runs, because the builder runs only when a snapshot is already
        // going out, which is the case this exists to change.
        //
        // Through a FIELD rather than a local: a local read here and used inside
        // the builder is a captured variable, which turns the closure from one
        // delegate into a display class as well, per frame, on the render
        // thread.
        if (_renderer.DebugLayerErrorCount != _publishedDebugLayerErrors)
            Host.MarkDirty();

        // A composited host is sampling a resource that is about to be retired,
        // so a fresh generation is news in exactly the sense a command the user
        // just issued is: hearing about it a whole interval later is a third of
        // a second of viewport at the wrong size, and the retired generation is
        // pinned in the meantime because the acknowledgement cannot come until
        // the replacement has been imported.
        //
        // Through a FIELD rather than a local for the same reason the counter
        // above is: a local read here and used inside the builder is a captured
        // variable, which turns the closure into a display class as well, per
        // frame, on the render thread.
        _publishedSharedTarget = _renderer.TryGetSharedHandle(out Renderer.SharedTargetHandle shared)
            ? shared
            : null;

        if (_publishedSharedTarget?.Generation != _publishedSharedGeneration)
        {
            _publishedSharedGeneration = _publishedSharedTarget?.Generation ?? 0;
            Host.MarkDirty();
        }

        Host.PublishFrame(elapsed, builder =>
        {
            ISceneEditor? editor = _sceneManager.Editor;

            // The same value the check above read: this is the render thread,
            // which is the counter's only writer, so nothing moved it between.
            _publishedDebugLayerErrors = _renderer.DebugLayerErrorCount;

            // Drained rather than sampled, and drained HERE rather than beside
            // the dirty checks above, because the builder runs only when a
            // snapshot is going out: draining on a frame that publishes nothing
            // would throw the window's peak away and report the next window's
            // stall as smaller than it was.
            _renderer.DrainSharedAcquireWait(out float acquireWaitMs, out float acquirePeakMs);

            return new FrameSnapshot
            {
                FrameNumber = builder.FrameNumber,
                Changes = builder.Changes,
                ChangesOverflowed = builder.ChangesOverflowed,
                FrameTimeMs = _fpsCounter.FrameTimeMs,
                Fps = _fpsCounter.Fps,
                SelectedIds = CaptureSelection(),
                GizmoModeName = editor?.GizmoModeName,
                GizmoStyleName = editor?.GizmoStyleName,
                GizmoOrientationName = editor?.GizmoOrientationName,
                SnapEnabled = editor?.SnapEnabled ?? false,
                SnapIncrement = editor?.SnapIncrement ?? 0f,
                MoveSnapIncrement = editor?.MoveSnapIncrement ?? 0f,
                RotateSnapIncrement = editor?.RotateSnapIncrement ?? 0f,
                ResizeSnapIncrement = editor?.ResizeSnapIncrement ?? 0f,
                NavigationModeName = editor?.NavigationModeName,
                GridModeName = editor?.GridModeName,
                CameraPosition = _sceneManager.ActiveScene?.Camera.Position ?? default,
                UndoDepth = editor?.UndoDepth ?? 0,
                RedoDepth = editor?.RedoDepth ?? 0,
                StaticWorldCompileCount = _sceneManager.ActiveScene?.StaticWorldCompileCount ?? 0,
                StaticWorldDefect = _sceneManager.ActiveScene?.StaticWorldDefect,
                DebugLayerErrorCount = _publishedDebugLayerErrors,
                DebugLayerActive = _renderer.DebugLayerActive,
                SharedAcquireWaitMs = acquireWaitMs,
                SharedAcquirePeakMs = acquirePeakMs,
                SharedTarget = _publishedSharedTarget,
                IsPlaying = _character is { Active: true },
                CanPlay = _character is not null,
                DebugFlags = _debugFlags,
                PipelineName = _renderer.CurrentPipelineName,
                PipelineNames = _renderer.PipelineNames,
                SelectionProperties = CaptureProperties(),
                SelectionEntity = CaptureSelectionEntity(),
            };
        }, interacting);
    }

    // One last snapshot as the loop ends, so a shell sees the engine stop
    // rather than simply stop hearing from it.
    private void HostShutdownPublish(TimeSpan elapsed)
    {
        Host.SnapshotInterval = TimeSpan.Zero;
        PublishHostFrame(elapsed);
    }

    // Reused across publishes for the same reason the selection list is: a
    // panel showing a dozen rows must not allocate a dozen rows per publish on
    // the render thread.
    private readonly List<SceneNode> _snapshotNodes = [];
    private readonly List<PropertyRow> _snapshotProperties = [];
    private PropertyRow[] _publishedProperties = [];

    /// <summary>
    /// The selection's merged property rows, or an empty list when nothing is
    /// selected.
    /// </summary>
    /// <remarks>
    /// <b>Rebuilt every publish rather than diffed.</b> The values move
    /// continuously during a drag, so a change check would almost always say
    /// "changed" and would cost a full comparison to say it. The selection list
    /// above is the opposite case, which is why it is cached and this is not.
    /// </remarks>
    private IReadOnlyList<PropertyRow> CaptureProperties()
    {
        if (_sceneManager.ActiveScene is not { } scene || scene.Selection.Count == 0)
        {
            _publishedProperties = [];
            return _publishedProperties;
        }

        // Copied into a list of our own rather than handed the live one: the
        // inspector runs while the render thread owns the scene, but the set is
        // the selection's and may be rewritten by the next pick.
        _snapshotNodes.Clear();
        _snapshotNodes.AddRange(scene.Selection.Items);

        // The scene's schema catalogue, because an entity's rows are the ones
        // its CLASS declares and nothing else in this call knows the class. A
        // scene with no catalogue is the ordinary geometry case and still gets
        // rows for whatever an entity is carrying, as text.
        NodeInspector.Describe(_snapshotNodes, _snapshotProperties, scene.EntitySchemas);

        // Copied out, because the working list is reused next publish and a UI
        // reading it a frame later would see rows for a selection that has
        // moved on.
        if (_publishedProperties.Length != _snapshotProperties.Count)
            _publishedProperties = new PropertyRow[_snapshotProperties.Count];

        _snapshotProperties.CopyTo(_publishedProperties);
        return _publishedProperties;
    }

    // The target-name scan's buffer, reused across publishes. It is filled only
    // when the selection is one entity that actually carries wiring, which is a
    // rare interactive state; giving it a home here keeps that case off the
    // render thread's allocation budget entirely.
    private readonly List<string> _snapshotTargetNames = [];

    /// <summary>
    /// The selected entity's wiring, or null when the selection is not exactly
    /// one node carrying an entity.
    /// </summary>
    /// <remarks>
    /// <b>One node, deliberately.</b> Merging several entities' connection
    /// lists has no honest answer - see <see cref="EntityPanelInfo"/> - and the
    /// resolve check below reads the graph, which is the render thread's to
    /// read and nobody else's.
    /// </remarks>
    private EntityPanelInfo? CaptureSelectionEntity()
    {
        if (_sceneManager.ActiveScene is not { } scene)
            return null;

        IReadOnlyList<SceneNode> items = scene.Selection.Items;
        if (items.Count != 1)
            return null;

        return EntityPanelInfo.Capture(
            items[0], scene.EntitySchemas, scene, _snapshotTargetNames);
    }

    /// <summary>
    /// The selected ids as an immutable array, reusing the previous one when the
    /// selection has not changed.
    /// </summary>
    /// <remarks>
    /// A snapshot goes out about thirty times a second whether or not anything
    /// was selected, and the overwhelmingly common case is that the selection is
    /// identical to last time. Comparing before allocating keeps the steady
    /// state free, which matters because this runs on the render thread.
    /// </remarks>
    private IReadOnlyList<Guid> CaptureSelection()
    {
        if (_sceneManager.ActiveScene is not { } scene)
            return _publishedSelection = [];

        IReadOnlyList<SceneNode> items = scene.Selection.Items;

        _snapshotSelection.Clear();
        for (int i = 0; i < items.Count; i++)
            _snapshotSelection.Add(items[i].Id);

        if (_publishedSelection.Length == _snapshotSelection.Count)
        {
            bool same = true;
            for (int i = 0; i < _snapshotSelection.Count; i++)
            {
                if (_publishedSelection[i] != _snapshotSelection[i])
                {
                    same = false;
                    break;
                }
            }

            if (same)
                return _publishedSelection;
        }

        return _publishedSelection = [.. _snapshotSelection];
    }

    /// <summary>
    /// The surface a UI thread drives this engine through: queue work, ask it to
    /// stop, and hear about finished frames. See <see cref="EngineHost"/>.
    /// </summary>
    /// <remarks>
    /// Present whether or not anything is listening, because the standalone path
    /// costs nothing for it: commands nobody queues drain in a single failed
    /// dequeue, and a snapshot nobody subscribes to is still built at the
    /// publish interval so a shell attaching mid-run has something to bind to.
    /// </remarks>
    public EngineHost Host { get; }

    /// <summary>
    /// Windowed / borderless-fullscreen, as a request latch. Callable from any
    /// thread — the request is applied by the main thread in its event pump,
    /// exactly like the title and cursor-mode latches beside it.
    /// </summary>
    /// <remarks>
    /// The engine owns fullscreen on purpose. Left to itself, DXGI turns
    /// Alt+Enter into a <c>SetFullscreenState</c> transition inside the window
    /// procedure, on the main thread, while the render thread is presenting and
    /// resizing the same swap chain — which is what used to kill the render
    /// thread with <c>DXGI_ERROR_INVALID_CALL</c> out of <c>ResizeBuffers</c>.
    /// Both D3D backends now take Alt+Enter away from DXGI, and fullscreen
    /// becomes plain window-state work: no display mode switch, no device-lost
    /// transition, and the OpenGL backend gets it for free because there is no
    /// backend code involved at all — only the window, and the framebuffer-size
    /// latch that every backend already reconciles against.
    /// </remarks>
    public IWindowModeLatch WindowMode => _windowModeLatch;

    /// <summary>
    /// Whether play mode is entered as soon as the scene is loaded, rather than
    /// waiting for <see cref="PlayModeKey"/>.
    /// </summary>
    /// <remarks>
    /// Off by default, and for the same reason the editing self-test is: the
    /// resting state of this host is an editor, and a run that silently seizes
    /// the cursor and drops a camera to eye height is a surprising thing for a
    /// build to do on its own. A smoke run that wants a character walking asks
    /// for one.
    /// </remarks>
    public bool StartInPlayMode { get; set; }

    /// <summary>
    /// Whether to run the offscreen render-target probe once at startup.
    /// </summary>
    /// <remarks>
    /// Off by default: it renders the scene twice per probing frame. See
    /// <see cref="OffscreenProbe"/> for why it exists at all, which comes down
    /// to D3D11 and D3D12 having no headless device fixture to test render
    /// targets against.
    /// </remarks>
    public bool RunOffscreenProbe { get; set; }

    /// <summary>
    /// Whether to run the viewport comparison once at startup and then end the
    /// session.
    /// </summary>
    /// <remarks>
    /// <b>A measurement rather than a mode</b>, so it ends the run itself: the
    /// probe answers one question about one frame, and a session left open
    /// afterwards would go on paying for a composited surface nobody is
    /// importing. It needs a <see cref="RenderSurfaceKind.Composited"/> surface,
    /// since a window has no shared target to compare against; see
    /// <see cref="ViewportCompareProbe"/>.
    /// </remarks>
    public bool RunViewportCompare { get; set; }

    /// <summary>
    /// Null until <see cref="RunViewportCompare"/>'s probe has reported, then
    /// whether the two pictures agreed. A host's exit code.
    /// </summary>
    public bool? ViewportComparePassed { get; private set; }

    /// <summary>
    /// Whether to measure how the engine's frame rate follows the shared
    /// target's consumer, once at startup, and then end the session.
    /// </summary>
    /// <remarks>
    /// <b>A measurement rather than a mode</b>, the same shape
    /// <see cref="RunViewportCompare"/> takes, and it needs a
    /// <see cref="RenderSurfaceKind.Composited"/> surface for the same reason:
    /// there is no hand-over to pace without one. See
    /// <see cref="SharedPacingProbe"/> for what it drives and what is modelled.
    /// </remarks>
    public bool RunSharedPacingProbe { get; set; }

    /// <summary>
    /// Null until <see cref="RunSharedPacingProbe"/>'s probe has reported, then
    /// whether it produced a measurement at all. A host's exit code.
    /// </summary>
    public bool? SharedPacingProbePassed { get; private set; }

    /// <summary>
    /// Name of the rendering pipeline to start on, or null for the backend's
    /// default (the first one it registered).
    /// </summary>
    /// <remarks>
    /// A name the backend does not offer is a logged warning and the default,
    /// not a failure: the set differs per backend, and refusing to start over a
    /// pipeline choice would turn a diagnostic switch into a way to break a run.
    /// </remarks>
    public string? StartupPipeline { get; set; }

    /// <summary>Whether the frame's directional light casts a shadow. On by default.</summary>
    public bool ShadowsEnabled { get; set; } = true;

    /// <summary>Whether to measure and report where each frame's time goes.</summary>
    public bool ProfileFrames { get; set; }

    /// <summary>
    /// Overrides the renderer's graphics validation layer, or null to keep the
    /// build flavour's default. See <see cref="Renderer.EnableDebugLayer"/>.
    /// </summary>
    public bool? DebugLayer { get; set; }

    /// <summary>Substring of the graphics adapter to run on, or null for the system default.</summary>
    public string? PreferredAdapter { get; set; }

    /// <summary>Window size, or null for the default.</summary>
    public (int Width, int Height)? WindowSize { get; set; }

    /// <summary>The key that enters and leaves play mode.</summary>
    public const InputKey PlayModeKey = InputKey.F8;

    /// <summary>The key that toggles the character capsule overlay.</summary>
    public const InputKey CharacterOverlayKey = InputKey.F9;

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
            Size = WindowSize is { } requested
                ? new Silk.NET.Maths.Vector2D<int>(requested.Width, requested.Height)
                : new Silk.NET.Maths.Vector2D<int>(1280, 720),
            VSync = false,
            FramesPerSecond = 0,
            UpdatesPerSecond = 0,
            API = _renderer.WindowApi,
        };

        _window = Window.Create(options);
        _window.Initialize();

        // VSync goes through the window/context. This sets Silk's own flag; on
        // OpenGL it is NOT sufficient, because glfwSwapInterval acts on the
        // context current on the calling thread and the render thread takes the
        // context away immediately after. OpenGLRenderer.AcquireContext applies
        // it again over there, which is what actually turns vsync off; leaving
        // this out pins the frame time to the refresh interval with almost no
        // work in it and reads as a slow renderer rather than as a wait.
        _window.VSync = false;

        // Subsystems that touch no GPU state are set up on this (OS-event) thread.
        InitializeSubsystems();

        // The one subsystem that IS window-shaped: Silk's input devices belong
        // to the window this path created. An embedded engine has no devices at
        // all and is fed through EngineHost instead.
        _inputManager.Initialize(_window.CreateInput());

        // The renderer is handed a SURFACE, not a window: a handle, a context
        // and a size, with none of the ownership (title, cursor, event pump,
        // lifetime) that stays here. An embedded host supplies its own
        // implementation of the same three things and needs to touch no backend.
        AttachSurface(new WindowRenderSurface(_window));

        // Focus changes fire during DoEvents, i.e. on this thread — which is
        // the only thread allowed to touch the cursor. A focus loss has to
        // release a freelook capture immediately (and drop the held keys whose
        // key-up went to whoever stole the focus), so it is handled inside the
        // input manager rather than latched for the render thread to notice a
        // frame later.
        _window.FocusChanged += _inputManager.OnWindowFocusChanged;

        StartRenderThread();

        // The window-mode latch drives the window through this adapter — the
        // one object in the fullscreen path that names a windowing backend.
        var windowModeTarget = new SilkWindowModeTarget(_window);

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

            // Beside the mode, in the same slot and for the same reason: cursor
            // calls belong to the thread that owns the window, and the tools
            // that know what shape is wanted run on the render thread.
            _inputManager.ApplyPendingCursorShape();

            // The fourth main-thread latch, same slot, same reason: F11 is read
            // on the render thread but undecorating and moving the window is
            // window-thread work. A no-op on every frame but the one that
            // toggles.
            if (_windowModeLatch.ApplyPendingWindowMode(windowModeTarget) is { } newWindowMode)
            {
                // Re-seed the size latch from the window we just reshaped. GLFW
                // does fire FramebufferResize for this, but it is delivered
                // through whichever callback pass the backend chooses, and the
                // render thread must not present one frame at the old size into
                // buffers the OS already resized. Reading it here — on the only
                // thread allowed to — makes the hand-off immediate.
                var framebuffer = _window.FramebufferSize;
                _renderer.SetFramebufferSize(framebuffer);
                _logger.LogInformation(
                    "Window mode -> {Mode} ({Width}x{Height})", newWindowMode, framebuffer.X, framebuffer.Y);
            }

            Thread.Sleep(1);
        }

        // Whatever ended the pump (close request or render-thread death), the
        // render loop only watches this flag — raise it before joining.
        JoinRenderThread();
        ShutdownSubsystems();

        // Dispose already destroys the window; a preceding Reset would be redundant.
        _window.Dispose();
        _window = null;

        _logger.LogInformation("Spectra Engine shut down");
        return !_renderThreadFaulted;
    }

    // --- Embedded lifetime ---------------------------------------------------
    //
    // The same engine, minus the window: a shell already has one, already pumps
    // its events and already owns the title, the cursor and the fullscreen
    // state, so all it can hand over is a surface. What it gets back is the
    // render thread running against that surface, driven through EngineHost.
    //
    // Deliberately NOT the same call as Run. Run BLOCKS until the window closes
    // because that is what owning the process's main loop means; a shell calls
    // Start from its UI thread and must get it back immediately, and collapsing
    // the two would give one of the callers the wrong answer.

    /// <summary>
    /// Whether the render thread is running. False before <see cref="Start"/>
    /// and after <see cref="Stop"/>.
    /// </summary>
    public bool IsRunning => _renderThread is not null;

    /// <summary>
    /// Whether the render thread ended on an exception. Already logged; a shell
    /// reads this to decide whether the session died or merely finished.
    /// </summary>
    public bool Faulted => _renderThreadFaulted;

    /// <summary>
    /// Starts the engine against a surface the caller owns, creating no window
    /// and pumping no OS events. Returns as soon as the render thread is
    /// running. Call <see cref="Stop"/> to shut it down.
    /// </summary>
    /// <remarks>
    /// <b>Everything the standalone path does with its window stays with the
    /// caller.</b> The title, the cursor, the event pump, the close button and
    /// the fullscreen transition are all things a shell already owns and would
    /// have to be asked for back; the engine only ever needed a handle, a
    /// context and a size, which is what <see cref="IRenderSurface"/> is.
    /// <para>
    /// <b>No input devices are attached</b>, because a host-supplied surface has
    /// none the engine could enumerate: the shell's own windowing layer is
    /// already receiving the keyboard and the mouse. Input arrives through
    /// <see cref="EngineHost"/> instead, and until something submits any, the
    /// engine renders and simulates with every key up — which is a correct
    /// resting state, not a broken one.
    /// </para>
    /// <para>
    /// <b>Fullscreen is not silently dropped either.</b> <see cref="WindowMode"/>
    /// stays a request latch, and the request is now the shell's to apply to its
    /// own window: it polls <see cref="IWindowModeLatch.RequestedWindowMode"/>
    /// exactly as <see cref="Run"/>'s pump does. That was already the shape of
    /// the latch, which is why nothing about it changes here.
    /// </para>
    /// </remarks>
    public void Start(IRenderSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        if (_renderThread is not null)
            throw new InvalidOperationException("The engine is already running.");

        _logger.LogInformation(
            "Spectra Engine {Version} starting (embedded, {Kind} surface)",
            EngineInfo.VersionString, surface.Kind);

        InitializeSubsystems();
        AttachSurface(surface);
        StartRenderThread();
    }

    /// <summary>
    /// Stops the render thread and shuts the subsystems down. Returns
    /// <c>true</c> on a clean shutdown and <c>false</c> when the render thread
    /// had died on an exception (already logged). Idempotent.
    /// </summary>
    /// <remarks>
    /// Blocks until the render thread has finished, which is not optional: it
    /// owns the graphics context and every GPU resource in the process, and a
    /// shell that tore its window down while that thread was still presenting
    /// into it would be handing the driver a destroyed surface.
    /// </remarks>
    public bool Stop()
    {
        if (_renderThread is null)
            return !_renderThreadFaulted;

        JoinRenderThread();
        ShutdownSubsystems();

        _logger.LogInformation("Spectra Engine shut down");
        return !_renderThreadFaulted;
    }

    // --- Shared lifetime steps -----------------------------------------------

    private void InitializeSubsystems()
    {
        _assetManager.Initialize();
        _sceneManager.Initialize();
        _audioManager.Initialize();
    }

    private void AttachSurface(IRenderSurface surface)
    {
        _surface = surface;

        // Seed the renderer's framebuffer-size latch while we are still the
        // only thread, then keep it fresh from the resize event (which fires on
        // whichever thread owns the surface). No windowing backend promises
        // thread-safe size queries, so the render side only ever reads the
        // latch and never touches the surface's own size.
        _renderer.SetFramebufferSize(surface.PixelSize);
        surface.Resized += _renderer.SetFramebufferSize;

        // Release any thread-affine context (OpenGL) here so the render thread
        // can take ownership. Backends without one (D3D, Vulkan) no-op.
        _renderer.ReleaseContext(surface);
    }

    private void StartRenderThread()
    {
        // Cleared here rather than at the end of a run, so Stop's return value
        // and Faulted still describe the session that just ended. A restart is
        // a fresh session and must not inherit the previous one's exit flags,
        // or its render loop would see _closeRequested already raised and exit
        // before its first frame.
        _closeRequested = false;
        _renderThreadExited = false;
        _renderThreadFaulted = false;

        _renderThread = new Thread(RenderLoop)
        {
            Name = "Spectra Render",
        };
        _renderThread.Start();
    }

    private void JoinRenderThread()
    {
        if (_renderThread is not { } thread)
            return;

        _closeRequested = true;
        thread.Join();
        _renderThread = null;
    }

    private void ShutdownSubsystems()
    {
        // Detached before the managers go, so a resize arriving from a shell
        // that has not yet torn its own surface down cannot reach a renderer
        // that is already shutting down.
        if (_surface is { } surface)
        {
            surface.Resized -= _renderer.SetFramebufferSize;
            _surface = null;
        }

        _inputManager.Shutdown();
        _audioManager.Shutdown();
        _sceneManager.Shutdown();
        _assetManager.Shutdown();
    }

    // --- Play mode -----------------------------------------------------------
    // Entering is not just "start ticking the mover": it is a transfer of two
    // exclusive resources — the camera and the cursor — away from an editor that
    // may be holding both, mid-gesture. Doing that in one place is what keeps
    // the two of them from ever being owned twice.

    private void TogglePlayMode()
    {
        if (_character is not { } character)
            return;

        if (character.Active) ExitPlayMode();
        else EnterPlayMode();
    }

    private void EnterPlayMode()
    {
        if (_character is not { } character || character.Active)
            return;

        // Before the character asks for the cursor, never after. A suspended
        // editor has rolled back any half-finished drag and released its own
        // cursor lock, so the two requests cannot alternate.
        _sceneManager.Editor?.Suspend();
        character.Enter();

        // Last, once both exclusive resources have changed hands. Activation
        // runs every entity's spawn, and a spawn may already fire outputs and
        // schedule thinks; none of that has any business happening while a
        // gizmo drag is still open.
        //
        // A scene with no character never enters play mode at all today, so a
        // level's entities never tick there. That is a real limitation of the
        // gate rather than an oversight: this is the only moment the engine has
        // to hand the frame over.
        _sceneManager.StartEntityWorld();
    }

    private void ExitPlayMode()
    {
        if (_character is not { Active: true } character)
            return;

        // First, and before the camera and the cursor go back: an entity's
        // OnRemove runs here, and it must not run in a frame where the editor
        // has already taken the scene back.
        _sceneManager.StopEntityWorld();

        character.Exit();

        // The editor takes the frame back, and says so: while suspended it
        // refuses edits rather than trusting a shell whose view of play mode
        // is a publish interval behind.
        _sceneManager.Editor?.Resume();
    }

    // Runs on the dedicated render thread: owns the GL context, drives update
    // and render, and presents each frame. Exceptions must not escape: the
    // thread is non-background, so an unhandled throw would kill the process
    // without a fatal log entry or a log flush.
    private void RenderLoop()
    {
        var surface = _surface!;
        try
        {
            _renderer.AcquireContext(surface);

            if (DebugLayer is { } wanted)
                _renderer.EnableDebugLayer = wanted;
            _renderer.PreferredAdapter = PreferredAdapter;

            // Before Initialize, because that is where the backends build their
            // default and debug programs: set afterwards, a cooked pack would be
            // ignored for exactly the two shaders every frame binds, and nothing
            // would report it - the source fallback renders the same picture.
            _renderer.ShaderContent = _assetManager.Content;

            _renderer.Initialize(surface);

            _renderer.ShadowsEnabled = ShadowsEnabled;
            _renderer.Profiler.Enabled = ProfileFrames;

            // After Initialize, because that is where a backend registers its
            // pipelines and there is nothing to select before it.
            if (StartupPipeline is { Length: > 0 } pipelineName &&
                !_renderer.TrySelectPipeline(pipelineName))
            {
                _logger.LogWarning(
                    "No rendering pipeline named '{Requested}'; staying on {Pipeline}",
                    pipelineName, _renderer.CurrentPipelineName);
            }

            // GPU-side asset start-up belongs here, not in the main-thread
            // Initialize above: the placeholder texture is a GPU resource, so
            // it has to be created on the thread that owns the context.
            _assetManager.AttachRenderer(_renderer);

            _sceneManager.LoadStartupScene(_renderer, _assetManager);

            // After the scene, so the probe's frames draw real geometry through
            // real pipeline states rather than an empty clear. The PSO built for
            // an offscreen target's format is the D3D12 half of what this is
            // for, and an empty frame would never build one.
            if (RunOffscreenProbe)
                _offscreenProbe = new OffscreenProbe(_logger);

            // Beside the offscreen probe and for the same reason: it needs the
            // real scene, because a comparison of two clears would agree
            // whatever the colour route did to them.
            if (RunViewportCompare)
                _viewportCompare = new ViewportCompareProbe(_logger);

            // Beside the two above and for the same reason: it needs a real
            // scene, because a frame that draws nothing is not the frame whose
            // pacing is in question.
            if (RunSharedPacingProbe)
            {
                _sharedPacing = new SharedPacingProbe(_logger);

                // Subscribed rather than reading the renderer, because the
                // acquire-wait drain RESETS what it returns and the snapshot
                // builder below already calls it: a second reader would get
                // whatever landed in the gap, which is nothing. See
                // SharedPacingProbe.ObserveSnapshot.
                Host.FrameCompleted += _sharedPacing.ObserveSnapshot;
            }

            // The change log follows whichever scene is live, so a shell's tree
            // view hears about structure from the frame the scene loads rather
            // than from the first edit after it.
            Host.ObserveScene(_sceneManager.ActiveScene);

            if (_sceneManager.ActiveScene is { } activeScene)
            {
                _cameraController = new FlyCameraController(activeScene.Camera, _inputManager);

                // The character is built here rather than by the scene manager
                // because it needs live input, which the scene manager has no
                // access to. It is handed BACK to the scene manager only so the
                // periodic stats line can report it — see SceneManager.Character.
                _character = new FirstPersonController(_logger, activeScene, _inputManager)
                {
                    SpawnPosition = _sceneManager.PlayerSpawn,
                    SpawnYaw = _sceneManager.PlayerSpawnYaw,
                    FallOutHeight = _sceneManager.PlayerFallOutHeight,
                };
                _sceneManager.Character = _character;

                _logger.LogInformation(
                    "{Key} enters play mode (walk the world as a {Height:0.0} sunit character); " +
                    "{OverlayKey} toggles the capsule overlay",
                    PlayModeKey, _character.Tuning.StandHeight, CharacterOverlayKey);

                if (StartInPlayMode)
                    EnterPlayMode();
            }

            _logger.LogInformation("All subsystems initialized");

            var clock = Stopwatch.StartNew();
            double previous = clock.Elapsed.TotalSeconds;

            // A shell's RequestShutdown ends the render loop exactly as the
            // window's close button does; the main thread then leaves its pump
            // because the render thread has exited.
            while (!_closeRequested && !Host.ShutdownRequested)
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

                // Mode first, so the rest of the frame is unambiguous about who
                // owns the camera and the cursor. Escape leaves as well as the
                // toggle key: play mode captures the cursor, and the one key
                // every user tries when a window will not give the mouse back is
                // Escape.
                //
                // A host's request is taken at the same site the key is read, so
                // a Play button and F8 cannot disagree about what entering
                // means; Enter/Exit are already idempotent, so a request for
                // the current state is a no-op rather than a flicker.
                if (Host.TryTakePlayModeRequest(out bool enterPlay))
                {
                    if (enterPlay) EnterPlayMode();
                    else ExitPlayMode();
                }

                if (_inputManager.WasKeyPressed(PlayModeKey))
                    TogglePlayMode();
                else if (_character is { Active: true } && _inputManager.WasKeyPressed(InputKey.Escape))
                    ExitPlayMode();

                if (_inputManager.WasKeyPressed(CharacterOverlayKey))
                    _drawCharacter = !_drawCharacter;

                bool playing = _character is { Active: true };

                // ONE command per frame, replayed by every tick below. Sampling
                // per tick would multiply mouse look by the frame's tick count,
                // so the mouse would get faster as the machine got slower.
                if (playing)
                    _character!.BeginFrame(deltaTime);

                // The editor, when the host installed one, gets the frame
                // first: it owns selection, manipulation and — on the frames it
                // says so — navigation. It runs before the scene update so the
                // camera and any gizmo edit are final by the time the draw list
                // is built from them. A host without an editor (a shipped game)
                // simply keeps the fly camera.
                //
                // While the character is walking, neither it nor the fly camera
                // runs at all: three subsystems writing one camera would be a
                // fight, and the editor's own freelook would be asking the
                // window for a cursor mode on alternate frames. The editor was
                // suspended when play mode began, so nothing of its is left open.
                ISceneEditor? editor = _sceneManager.Editor;
                bool editorNavigated = false;
                if (!playing)
                {
                    editorNavigated = editor is not null && editor.Update(deltaTime);
                    if (!editorNavigated)
                        _cameraController?.Update(deltaTime);
                }

                // The demo update animates and logs; it gets last frame's
                // render view for its culling stats alongside time.
                using (Profiler.Measure(FramePhase.Update))
                    _sceneManager.Update(deltaTime, _renderView);

                // Drive the async static-world pipeline: harvest a finished
                // background compile (the swap and GPU mesh creation happen
                // here, on the render thread) and launch the next compile when
                // brush nodes have changed since the last one.
                // ─── FIXED TICK LOOP ───────────────────────────────────
                // Physics advances in whole fixed steps, never in frame
                // deltas: a step that varies with frame time makes the
                // simulation a function of how fast the machine is, which is
                // what determinism, replay and any future server
                // reconciliation all rest on NOT being true.
                //
                // Targets are pushed before the step (a door decides where it
                // is this tick before the tick resolves against it), and events
                // are drained INSIDE the loop, immediately after the step that
                // produced them — a backend's event buffers are overwritten by
                // the next step, so draining outside would silently discard
                // every tick's events but the last on a catch-up frame.
                //
                // Entity logic has taken the first slot in the loop; scripts and
                // the touch diff take theirs when they exist, and the ordering
                // above is already the one they need.
                IScenePhysics physics = _sceneManager.Physics;
                int ticks = _physicsTicks.Advance(deltaTime);
                for (int tick = 0; tick < ticks; tick++)
                {
                    // FIRST in the tick, and before the kinematic push: an
                    // entity that decides where a platform is this tick has to
                    // have decided before the step resolves against it, or the
                    // character rides last tick's pose. It is inside the loop
                    // and takes the FIXED step for the same reason physics
                    // does: a heap of fire times advanced by a frame delta
                    // makes when a door opens a function of how fast the
                    // machine is.
                    //
                    // A world exists only while play mode owns the scene, so
                    // this is a null read in an editing session.
                    _sceneManager.EntityWorld?.Tick(_physicsTicks.FixedDeltaTime);

                    physics.PushKinematicTargets(_physicsTicks.FixedDeltaTime);
                    using (Profiler.Measure(FramePhase.Physics))
                        physics.Step(_physicsTicks.FixedDeltaTime);
                    physics.DrainEvents();

                    // After the step, so a character resolves against the pose
                    // the kinematic bodies have this tick rather than last
                    // tick's — which is the difference between riding a moving
                    // platform and being left behind by it.
                    if (playing)
                        _character!.Tick(_physicsTicks.FixedDeltaTime);
                }
                // ─── end tick loop ─────────────────────────────────────

                // Host commands run HERE, immediately before the compile pump,
                // so an edit posted from a UI thread and the static-world
                // recompile it causes land in the same frame rather than a
                // frame apart. Any later and a shell's delete would be visible
                // in the tree one frame before it was visible in the viewport.
                Host.DrainCommands(_sceneManager.ActiveScene);

                using (Profiler.Measure(FramePhase.WorldSwap))
                    _sceneManager.ActiveScene?.ProcessStaticWorldCompilation(_renderer, _logger);

                // Static collision follows the compiled world in the SAME slot
                // the render meshes swap in, deliberately: geometry that is
                // visible and geometry that is solid must change in the same
                // instant, or a player walks into an invisible wall for a
                // frame. It stays out of the tick loop because running it up to
                // MaxTicksPerFrame times a frame would be that many shape-churn
                // batches of which at most one can do work.
                if (_sceneManager.ActiveScene is { } physicsScene)
                    physics.SyncStaticWorld(physicsScene);

                // The other half of the same story: part brushes are NOT in
                // that compile, so their meshes are built and collected here
                // instead. Proportional to the number of distinct part brushes,
                // never to the world — a part that merely moved is a cache hit.
                _sceneManager.ActiveScene?.ProcessPartBrushMeshes(_renderer);

                // Same shape, for content: background decodes hand their pixel
                // buffers over here and the GPU textures are created on this
                // thread. Costs nothing on a frame with nothing pending.
                using (Profiler.Measure(FramePhase.Assets))
                    _assetManager.PumpPendingUploads();

                // Audio rides the same slot and for the same reason: the render
                // thread owns the AL context (see AudioManager's threading
                // note), and this is the one place per frame guaranteed to run
                // on it. A frame that skipped it would not merely stop
                // reclaiming finished sources, it would let every streaming
                // queue drain, so it sits here unconditionally. The listener
                // follows the active camera first, or every positional sound
                // plays at the world origin and the feature reads as broken.
                if (_sceneManager.ActiveScene is { } listenerScene)
                {
                    Camera listener = listenerScene.Camera;
                    _audioManager.SetListener(listener.Position, listener.Forward, listener.Up);
                }

                _audioManager.Update();

                // Render poses last, once per frame: the blend between the two
                // most recent ticks. A render-only overlay — it must never
                // write back through a node's transform setters.
                physics.PublishRenderPoses(_physicsTicks.Alpha);

                // The eye follows the last tick, once, before the draw list is
                // built from the camera. Render-only: it never writes back into
                // the mover, so a replayed tick is unaffected by where the head
                // happens to be.
                if (playing)
                    _character!.UpdateView(deltaTime, _physicsTicks.Alpha);

                // F1–F5 toggle debug visualisations on/off. A host's requests
                // land at the same site, set before clear (see
                // TakeDebugVisualizationRequests for why that order).
                if (_inputManager.WasKeyPressed(InputKey.F1)) _debugFlags ^= DebugVisualization.Wireframe;
                if (_inputManager.WasKeyPressed(InputKey.F2)) _debugFlags ^= DebugVisualization.Vertices;
                if (_inputManager.WasKeyPressed(InputKey.F3)) _debugFlags ^= DebugVisualization.Aabbs;
                if (_inputManager.WasKeyPressed(InputKey.F4)) _debugFlags ^= DebugVisualization.Normals;
                if (_inputManager.WasKeyPressed(InputKey.F5)) _debugFlags ^= DebugVisualization.SceneGraph;

                Host.TakeDebugVisualizationRequests(
                    out DebugVisualization flagsToSet, out DebugVisualization flagsToClear);
                _debugFlags = (_debugFlags | flagsToSet) & ~flagsToClear;

                // F6 cycles render pipelines (Forward, Wireframe, ...); a host
                // selects one by name instead, with the same
                // warn-and-stay-put contract as the startup switch.
                if (_inputManager.WasKeyPressed(InputKey.F6))
                    _renderer.NextPipeline();

                if (Host.TakeRequestedPipeline() is { } requestedPipeline &&
                    !_renderer.TrySelectPipeline(requestedPipeline))
                {
                    _logger.LogWarning(
                        "No rendering pipeline named '{Requested}'; staying on {Pipeline}",
                        requestedPipeline, _renderer.CurrentPipelineName);
                }

                // A composited host letting go of a retired shared target. Here
                // rather than anywhere else because this is the render thread's
                // once-a-frame slot for everything a UI asked for, and freeing a
                // GPU resource belongs to it exclusively.
                if (Host.TryTakeSharedTargetRelease(out int releasedGeneration))
                    _renderer.NotifySharedTargetReleased(releasedGeneration);

                // F11 toggles borderless fullscreen. Only the REQUEST happens
                // here — the window itself is reshaped by the main thread in
                // Run's pump, because window calls belong to the thread that
                // created the window. Same key on all three backends: nothing
                // about this touches a graphics API.
                if (_inputManager.WasKeyPressed(InputKey.F11))
                    _windowModeLatch.ToggleFullscreen();

                _renderer.DebugDraw.Clear();
                _renderer.WorldLines.Clear();
                if (_sceneManager.ActiveScene is { } scene)
                {
                    // The world lane FIRST, and it is not the same buffer: it is
                    // flushed inside whichever pass owns the scene's depth, so a
                    // ground grid is occluded by the floor rather than drawn
                    // through it. See ISceneEditor.DrawWorld.
                    editor?.DrawWorld(_renderer.WorldLines);

                    // The selection outline used to be drawn HERE, from Core,
                    // for every host including a shipped game that can never
                    // have a selection. It moved to the editing assembly with
                    // the rest of the editor's viewport chrome (see
                    // SelectionOutline), which also let it become the shape of
                    // the thing rather than an axis-aligned box around it.

                    // The manipulator and the marquee ride the same depth-off
                    // line path as the highlight, which is what makes a gizmo
                    // grabbable on a handle buried inside the brush it moves:
                    // the handles are drawn on top of everything, so what you
                    // can see is exactly what you can pick.
                    editor?.Draw(_renderer.DebugDraw);

                    if (_drawCharacter)
                        _character?.Draw(_renderer.DebugDraw);

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
                    using (Profiler.Measure(FramePhase.ViewBuild))
                        viewScene.BuildRenderView(viewScene.Camera, _renderView);
                }
                else
                {
                    _renderView.Clear();
                }

                // Before Render, because the probe decides whether this frame
                // also goes into an offscreen target.
                _offscreenProbe?.Update(_renderer);
                if (_offscreenProbe is { Running: false })
                    _offscreenProbe = null;

                // In the same slot, because it decides what this frame writes.
                // When it finishes, the run is OVER rather than merely reported:
                // it has measured its one frame, and the frame it would open
                // next would take the shared key with nobody left to hand it
                // back, spending a timeout to render a picture nobody reads.
                if (_viewportCompare is { } compare)
                {
                    compare.Update(_renderer);
                    if (!compare.Running)
                    {
                        ViewportComparePassed = compare.Passed;
                        _viewportCompare = null;
                        Host.RequestShutdown();
                        break;
                    }
                }

                // In the same slot, and it ends the run for the same reason:
                // once it has its table the session is over, and a frame after
                // it would take the shared key with its modelled consumer torn
                // down and nobody left to hand the key back.
                if (_sharedPacing is { } pacing)
                {
                    pacing.Update(_renderer);
                    if (!pacing.Running)
                    {
                        SharedPacingProbePassed = pacing.Passed;
                        _sharedPacing = null;
                        Host.RequestShutdown();
                        break;
                    }
                }

                _renderer.Render(_sceneManager.ActiveScene, _renderView, deltaTime);

                if (_fpsCounter.Tick(rawDelta))
                {
                    _pendingTitle =
                        $"{WindowTitle}  —  {_fpsCounter.Fps:0} FPS  ({_fpsCounter.FrameTimeMs:0.00} ms)  —  {_renderer.CurrentPipelineName}";

                    // Published for the periodic stats line as well as the title:
                    // a window caption is invisible to an unattended run, and the
                    // frame cost is exactly the number a smoke run should carry.
                    _sceneManager.FrameTimeMs = _fpsCounter.FrameTimeMs;
                    _sceneManager.Fps = _fpsCounter.Fps;
                }

                using (Profiler.Measure(FramePhase.Present))
                    _renderer.Present(surface);

                // After Present, so a snapshot describes a frame that is
                // genuinely finished, and the handler's cost lands where it can
                // only delay the NEXT frame rather than this one's presentation.
                PublishHostFrame(clock.Elapsed);

                Profiler.EndFrame();
            }

            HostShutdownPublish(clock.Elapsed);

            // Asset-owned textures are destroyed through the renderer, so they
            // have to go before it shuts down — and on this thread. Part-brush
            // meshes are scene-owned and go the same way, for the same reason.
            _sceneManager.ActiveScene?.ReleasePartBrushMeshes(_renderer);
            _assetManager.ReleaseGraphicsResources();
            _renderer.Shutdown();
            _renderer.ReleaseContext(surface);
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
                _renderer.ReleaseContext(surface);
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
