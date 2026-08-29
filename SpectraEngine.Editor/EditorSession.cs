using Microsoft.Extensions.Logging;
using SpectraEngine.Core;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Audio;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.D3D11;
using SpectraEngine.Core.Graphics.D3D12;
using SpectraEngine.Core.Hosting;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Maps;
using SpectraEngine.Core.Scene;
using SpectraEngine.Core.Inspection;
using SpectraEngine.Editing.Cameras;
using SpectraEngine.Editing.Commands;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Hosting;
using SpectraEngine.Physics.Box3D;
using SpectraShade.Compiler;
using System;
using System.IO;

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
    /// <param name="contentRoot">
    /// The asset content root, or null for the engine's default. A project
    /// session passes the project's <c>Assets/</c> folder — and it is a
    /// constructor argument rather than a setter, because the asset manager
    /// resolves every path against the root it was built with and a root
    /// swapped mid-session would leave every cached texture keyed to a folder
    /// nothing looks in any more. Opening a different project is a new
    /// session, the way it is a new window in every IDE.
    /// </param>
    public EditorSession(ILoggerFactory loggerFactory, GraphicsBackend backend, string? contentRoot = null)
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

        var sceneManager = new SceneManager(loggerFactory.CreateLogger<SceneManager>())
        {
            // The shell edits projects; the authored demo belongs to the demo
            // executable. A session boots into the baseplate and whatever map
            // it should show is opened through OpenMap, where a bad bundle
            // reports instead of logging and falling back.
            Startup = StartupSceneKind.Baseplate,
        };
        var assetManager = contentRoot is null
            ? new AssetManager(loggerFactory.CreateLogger<AssetManager>())
            : new AssetManager(loggerFactory.CreateLogger<AssetManager>(), contentRoot);
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
    /// Sets one tool's snap increment — the payload-carrying sibling of the
    /// snap verbs, for the command surface's typed fields.
    /// </summary>
    public void SetSnapIncrement(GizmoMode tool, float increment) =>
        Host.EnqueueCommand(_ => Editor?.SetSnapIncrement(tool, increment));

    /// <summary>
    /// Selects the node with this id. An id the scene no longer has is
    /// ordinary: a UI's view of the graph is a frame or two behind.
    /// </summary>
    public void Select(Guid nodeId, SelectionUpdate mode = SelectionUpdate.Replace) =>
        Host.EnqueueCommand(_ => Editor?.SelectById(nodeId, mode));

    /// <summary>Applies one property-panel edit to the current selection.</summary>
    public void ApplyProperty(PropertyEdit edit) =>
        Host.EnqueueCommand(_ => Editor?.ApplyProperty(edit));

    // Render thread only. Null before the scene has loaded, and null for a host
    // that installed no editing layer at all.
    private SceneEditorHost? Editor => SceneManager.Editor as SceneEditorHost;

    // --- Documents -----------------------------------------------------------
    //
    // Both of these run on the RENDER thread, which is not a detail: the scene
    // graph, the static-world compile and every GPU resource belong to it, and
    // a UI thread that touched any of them would be racing the frame it is
    // watching. The completion callback is invoked there too, so a caller that
    // wants to touch its own UI has to marshal back deliberately rather than by
    // accident.

    /// <summary>
    /// Writes the live scene into a map bundle.
    /// </summary>
    /// <param name="bundlePath">The <c>.smap</c> directory to write.</param>
    /// <param name="done">
    /// Called on the render thread with the save report, or the failure. A
    /// report that is not complete is still a successful save: it means the
    /// scene held something the format cannot name, such as a mesh built in
    /// code.
    /// </param>
    public void SaveMap(string bundlePath, Action<MapSaveReport?, Exception?> done)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        ArgumentNullException.ThrowIfNull(done);

        Host.EnqueueCommand(scene =>
        {
            try
            {
                var report = new MapSaveReport();
                MapBundle.Save(bundlePath, MapSceneBinder.FromScene(scene, report));
                done(report, null);
            }
            catch (Exception ex) when (ex is MapFormatException or IOException or UnauthorizedAccessException)
            {
                done(null, ex);
            }
        });
    }

    /// <summary>
    /// Replaces the live scene's graph with a map bundle's.
    /// </summary>
    /// <remarks>
    /// <b>The editor is reset BEFORE the graph is replaced, and the world is
    /// recompiled after.</b> The reset is not housekeeping: an open gesture
    /// would be manipulating nodes that are about to leave the graph, the
    /// selection holds live node references that would outlive their scene, and
    /// the undo history addresses the old graph by id, where undo no-ops on a
    /// missing target rather than failing. The recompile uses the synchronous
    /// cache-free path, which is what a load is for: the incremental compiler
    /// carries caches from a previous world and a world just replaced wholesale
    /// has none worth carrying.
    /// </remarks>
    public void OpenMap(string bundlePath, Action<MapLoadReport?, Exception?> done)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        ArgumentNullException.ThrowIfNull(done);

        Host.EnqueueCommand(scene =>
        {
            try
            {
                MapDocument document = MapBundle.Load(bundlePath);

                Editor?.OnSceneReplaced();

                var report = new MapLoadReport();
                MapSceneBinder.ApplyTo(document, scene, report);
                scene.RebuildStaticWorld(_renderer);

                done(report, null);
            }
            catch (Exception ex) when (
                ex is MapFormatException or IOException or UnauthorizedAccessException)
            {
                done(null, ex);
            }
        });
    }

    /// <summary>
    /// Empties the scene, leaving a graph with nothing but the root in it.
    /// </summary>
    /// <remarks>
    /// The same reset as a load, because it is the same event from the editor's
    /// point of view: every node the selection, the history and any open
    /// gesture referred to is about to be gone.
    /// </remarks>
    public void NewMap(string name, Action<Exception?> done)
    {
        ArgumentNullException.ThrowIfNull(done);

        Host.EnqueueCommand(scene =>
        {
            try
            {
                Editor?.OnSceneReplaced();

                var empty = new MapDocument();
                empty.Scene.Name = string.IsNullOrWhiteSpace(name) ? "Scene" : name;
                MapSceneBinder.ApplyTo(empty, scene);

                // A new map is a baseplate, not a void: lit, with a floor to
                // stand things on — the same starter a fresh project boots
                // into, so "new" means one thing everywhere.
                SceneManager.PopulateBaseplate(scene);
                scene.RebuildStaticWorld(_renderer);

                done(null);
            }
            catch (Exception ex) when (ex is MapFormatException or IOException)
            {
                done(ex);
            }
        });
    }

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
