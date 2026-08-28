using Microsoft.Extensions.Logging;
using SpectraEngine.Core;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Audio;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.D3D11;
using SpectraEngine.Core.Graphics.D3D12;
using SpectraEngine.Core.Hosting;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Cameras;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Hosting;
using SpectraEngine.Physics.Box3D;
using SpectraShade.Compiler;
using System;

namespace SpectraEngine.Editor;

/// <summary>
/// One running engine inside the shell: the subsystems, the editor, and the
/// lifetime that ties them to a viewport surface.
/// </summary>
/// <remarks>
/// <b>The same wiring the standalone demo does, minus the window.</b> The shell
/// is a peer host rather than a layer above the demo: it builds the renderer,
/// the managers and the editor exactly as <c>Program.cs</c> does, then calls
/// <see cref="Engine.Start"/> with a surface it already has instead of
/// <c>Engine.Run</c>, which would create a second window and block the UI
/// thread.
/// <para>
/// <b>The editor is installed here, through the same factory the demo uses.</b>
/// That is what makes the shell's viewport a real editor viewport rather than a
/// preview: picking, gizmos, box select, undo and the editor camera are the
/// engine's, and the shell adds nothing to them.
/// </para>
/// <para>
/// <b>OpenGL is refused rather than attempted.</b> An embedded GL surface has to
/// create its own context against the child window and supply a proc-address
/// loader, which is real work that does not exist yet; offering the option and
/// failing inside the renderer would report it as a driver problem.
/// </para>
/// </remarks>
public sealed class EditorSession : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<EditorSession> _logger;
    private readonly Renderer _renderer;
    private readonly Engine _engine;

    /// <summary>Builds a session on the given backend, without starting it.</summary>
    /// <param name="loggerFactory">Owned by the caller; used for every subsystem.</param>
    /// <param name="backend">D3D11 or D3D12. See the remarks on OpenGL.</param>
    public EditorSession(ILoggerFactory loggerFactory, GraphicsBackend backend)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<EditorSession>();

        var shaderCompiler = new SpectraShadeCompiler();
        _renderer = backend switch
        {
            GraphicsBackend.D3D11 => new D3D11Renderer(loggerFactory.CreateLogger<D3D11Renderer>(), shaderCompiler),
            GraphicsBackend.D3D12 => new D3D12Renderer(loggerFactory.CreateLogger<D3D12Renderer>(), shaderCompiler),
            _ => throw new NotSupportedException(
                $"The editor viewport cannot host {backend} yet: an embedded OpenGL surface needs its own " +
                "WGL context and proc-address loader, which is not built. Use d3d11 or d3d12."),
        };

        var sceneManager = new SceneManager(loggerFactory.CreateLogger<SceneManager>());
        var assetManager = new AssetManager(loggerFactory.CreateLogger<AssetManager>());
        var audioManager = new AudioManager(loggerFactory.CreateLogger<AudioManager>());
        var inputManager = new InputManager(loggerFactory.CreateLogger<InputManager>());

        // The same seam the demo installs the editing layer through, invoked on
        // the render thread once the scene exists. No probe: the self-test is
        // the demo's instrumentation, and a person is sitting in front of this
        // one.
        sceneManager.EditorFactory = scene =>
            new SceneEditorHost(loggerFactory, scene, _renderer, inputManager);

        sceneManager.PhysicsFactory = _ =>
            new Box3DScenePhysics(loggerFactory.CreateLogger<Box3DScenePhysics>());

        SceneManager = sceneManager;
        _engine = new Engine(
            loggerFactory.CreateLogger<Engine>(),
            _renderer,
            sceneManager,
            assetManager,
            audioManager,
            inputManager);
    }

    /// <summary>The scene manager, for the panels that report on it.</summary>
    public SceneManager SceneManager { get; }

    /// <summary>The surface a UI thread drives this engine through.</summary>
    public EngineHost Host => _engine.Host;

    /// <summary>Whether the render thread is running.</summary>
    public bool IsRunning => _engine.IsRunning;

    /// <summary>Starts the engine against a viewport surface.</summary>
    public void Start(IRenderSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        _engine.Start(surface);
        _logger.LogInformation("Editor session started on {Backend}", _renderer.GetType().Name);
    }

    // --- Driving the editor from the UI thread -------------------------------
    //
    // Every one of these marshals onto the render thread through the host's
    // command queue, and every one reaches the editor through the SAME verbs a
    // key chord uses. The alternative — synthesising key presses at the engine
    // — is a second input path free to drift from the real one, and it would
    // not even work: the letter-row bindings deliberately stand down while a
    // camera is driving, so a toolbar built on them would go inert exactly
    // while somebody was navigating.
    //
    // The cast happens INSIDE the queued command, on the render thread, because
    // that is the only thread allowed to read SceneManager.Editor at all. It
    // also means there is no editor instance to capture and publish across a
    // thread boundary: the factory builds one later, on that thread, and this
    // simply asks for whatever is installed when the command runs.

    /// <summary>Runs one host verb: history, a structural edit, a mode toggle.</summary>
    public void Post(EditorHostCommand command) =>
        Host.EnqueueCommand(_ => Editor?.Apply(command));

    /// <summary>Runs one manipulator verb: pick a tool, flip a mode, drive snap.</summary>
    public void Post(GizmoCommand command) =>
        Host.EnqueueCommand(_ => Editor?.Apply(command));

    /// <summary>Runs one camera verb, such as framing the selection.</summary>
    public void Post(EditorCameraCommand command) =>
        Host.EnqueueCommand(_ => Editor?.Apply(command));

    /// <summary>
    /// Selects the node with this id. An id the scene no longer has is
    /// ordinary: a UI's view of the graph is a frame or two behind.
    /// </summary>
    public void Select(Guid nodeId, SelectionUpdate mode = SelectionUpdate.Replace) =>
        Host.EnqueueCommand(_ => Editor?.SelectById(nodeId, mode));

    // Render thread only. Null before the scene has loaded, and null for a host
    // that installed no editing layer at all.
    private SceneEditorHost? Editor => SceneManager.Editor as SceneEditorHost;

    /// <summary>
    /// Stops the engine and waits for the render thread. Safe to call twice.
    /// </summary>
    public void Stop()
    {
        if (!_engine.IsRunning)
            return;

        bool clean = _engine.Stop();
        if (!clean)
            _logger.LogError("The render thread ended on an exception; see the log above");
    }

    /// <inheritdoc/>
    public void Dispose() => Stop();
}
