using Avalonia.Controls;
using Avalonia.Interactivity;

using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;
using Silk.NET.Maths;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Hosting;
using SpectraEngine.Editor.Viewport;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SpectraEngine.Editor;

/// <summary>
/// The shell window: a scene tree, a viewport with the engine live inside it,
/// and a status line.
/// </summary>
/// <remarks>
/// <b>Everything crossing between the two threads crosses here, and only in two
/// directions.</b> The engine publishes immutable snapshots, which the UI thread
/// reads; the UI thread posts commands, which the render thread runs. Nothing
/// in this window ever touches a <c>Scene</c>, a <c>SceneNode</c> or the
/// renderer.
/// <para>
/// <b>The pump is not decoration.</b> A cursor lock is window-thread work that
/// the engine asks for from the render thread, exactly as it is in the
/// standalone path, so the shell needs a slot of its own to apply it in.
/// <see cref="DispatcherTimer"/> at roughly display rate is that slot.
/// </para>
/// </remarks>
public partial class MainWindow : Window
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MainWindow> _logger;
    private readonly DispatcherTimer _pump;

    // Every published snapshot, not just the newest. The engine's contract is
    // that a structural change rides the NEXT snapshot out and is then gone, so
    // a shell that keeps only the latest silently drops graph edits: the very
    // first snapshot carries the "you have nothing, rebuild" flag, and losing
    // exactly that one leaves a tree view permanently showing four nodes of a
    // 257-node scene with nothing anywhere reporting a problem. Measured, not
    // hypothesised.
    private readonly ConcurrentQueue<FrameSnapshot> _published = new();

    // Bounded like the engine's own change log, and for the same reason: a
    // stalled UI thread must not turn into unbounded growth on the render
    // thread's publish path. Eight seconds at the publish rate.
    private const int MaxQueuedSnapshots = 240;
    private int _queuedSnapshots;
    private volatile bool _droppedSnapshots;

    private EditorSession? _session;
    private SceneTreeModel? _tree;
    private FrameSnapshot _latest = FrameSnapshot.Empty;
    private bool _stopping;

    // The viewport's own pixel size, which is NOT the window's: the engine
    // renders into a pane, and every per-pixel cost in the frame is measured
    // against this rather than against whatever the shell is.
    private Vector2D<int> _viewportSize;

    /// <summary>Creates the window and wires the viewport's lifetime to the engine's.</summary>
    public MainWindow()
    {
        InitializeComponent();

        _loggerFactory = new SerilogLoggerFactory(Serilog.Log.Logger, dispose: false);
        _logger = _loggerFactory.CreateLogger<MainWindow>();

        Viewport.SurfaceCreated += OnSurfaceCreated;
        Viewport.SurfaceDestroying += OnSurfaceDestroying;

        _pump = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16), DispatcherPriority.Normal, OnPump);

        if (!EngineViewport.IsSupported)
        {
            StatusText.Text =
                "This platform cannot host the viewport yet: the embedded surface is Windows-only in v1.";
        }
    }

    // --- Engine lifetime -----------------------------------------------------

    private void OnSurfaceCreated(IRenderSurface surface)
    {
        try
        {
            var session = new EditorSession(_loggerFactory, ResolveBackend());

            // Input is armed before the engine starts: the host exists from
            // construction, so a click during the first frames reaches a real
            // state machine rather than being dropped.
            Viewport.Host = session.Host;
            session.Host.FrameCompleted += OnFrameCompleted;

            _tree = new SceneTreeModel(session.Host, _loggerFactory.CreateLogger<SceneTreeModel>());
            SceneTree.ItemsSource = _tree.Roots;

            _viewportSize = surface.PixelSize;
            surface.Resized += OnViewportResized;

            session.Start(surface);
            _session = session;
            _pump.Start();
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "The editor session could not start");
            StatusText.Text = $"The engine could not start: {ex.Message}";
        }
    }

    private void OnSurfaceDestroying()
    {
        // Before the window goes, never after: the render thread owns the swap
        // chain presenting into it.
        StopSession();
    }

    /// <inheritdoc/>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        StopSession();
        base.OnClosing(e);
    }

    private void StopSession()
    {
        if (_stopping || _session is null)
            return;

        _stopping = true;
        _pump.Stop();
        Viewport.Host = null;

        _session.Host.FrameCompleted -= OnFrameCompleted;
        _session.Stop();
        _session.Dispose();
        _session = null;
        _stopping = false;
    }

    // --- The two crossings ---------------------------------------------------

    // Raised ON the render thread, inside the engine's frame. Everything here
    // must be cheap and must not touch a control: the snapshot is queued and the
    // UI reads it from its own pump, which is the shape the host documents.
    private void OnFrameCompleted(FrameSnapshot snapshot)
    {
        // The newest one always wins for the status line, which wants current
        // values rather than a history.
        _latest = snapshot;

        if (Interlocked.Increment(ref _queuedSnapshots) > MaxQueuedSnapshots)
        {
            // Reported rather than silently discarded: the tree rebuilds from
            // the live graph instead of continuing to look correct.
            Interlocked.Decrement(ref _queuedSnapshots);
            _droppedSnapshots = true;
            return;
        }

        _published.Enqueue(snapshot);
    }

    private void OnPump(object? sender, EventArgs e)
    {
        // The cursor first: a freelook that started this frame should capture
        // before anything else looks at the pointer.
        Viewport.PumpCursorMode();

        // Drained, never sampled: each snapshot's change list is a batch that
        // exists once.
        while (_published.TryDequeue(out FrameSnapshot? queued))
        {
            Interlocked.Decrement(ref _queuedSnapshots);
            _tree?.ApplyChanges(queued);
        }

        if (_droppedSnapshots)
        {
            _droppedSnapshots = false;
            _logger.LogWarning("The shell fell behind the engine's snapshots; rebuilding the scene tree");
            _tree?.MarkStale();
        }

        FrameSnapshot snapshot = _latest;
        if (ReferenceEquals(snapshot, FrameSnapshot.Empty))
            return;

        // Selection is a state rather than a history, so it is applied once
        // from the newest snapshot instead of once per drained one: the walk is
        // over every node in the tree.
        _tree?.ApplySelection(snapshot.SelectedIds);
        UpdateStatus(snapshot);
    }

    // Raised on the UI thread by the viewport's own window procedure, beside
    // the renderer's size latch rather than instead of it.
    private void OnViewportResized(Vector2D<int> size)
    {
        _viewportSize = size;
        _logger.LogDebug("Viewport resized to {Width}x{Height}", size.X, size.Y);
    }

    private void UpdateStatus(FrameSnapshot snapshot)
    {
        StatusText.Text =
            $"{snapshot.Fps,5:0} fps  {snapshot.FrameTimeMs,6:0.00} ms   |   " +
            $"{_viewportSize.X}x{_viewportSize.Y}   |   " +
            $"{snapshot.SelectedIds.Count} selected   |   " +
            $"{snapshot.GizmoModeName ?? "no"} gizmo, {snapshot.NavigationModeName ?? "no"} navigation   |   " +
            $"undo {snapshot.UndoDepth} / redo {snapshot.RedoDepth}   |   " +
            $"{snapshot.StaticWorldCompileCount} world compiles";

        if (_tree is { } tree)
            TreeStatus.Text = $"{tree.Count} node(s), frame {snapshot.FrameNumber}";
    }

    // --- Menu ----------------------------------------------------------------

    private void OnExitClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnFocusViewportClicked(object? sender, RoutedEventArgs e) => Viewport.Focus();

    // --- Startup -------------------------------------------------------------

    private GraphicsBackend ResolveBackend()
    {
        foreach (string arg in Program.StartupArgs)
        {
            switch (arg.ToLowerInvariant())
            {
                case "d3d11": return GraphicsBackend.D3D11;
                case "d3d12": return GraphicsBackend.D3D12;
                case "opengl":
                    // Named explicitly and refused explicitly: an embedded GL
                    // surface needs its own context, and letting the renderer
                    // discover that would report it as a driver failure.
                    throw new NotSupportedException(
                        "The editor viewport cannot host OpenGL yet; use d3d11 or d3d12.");
            }
        }

        return GraphicsBackend.D3D11;
    }
}
