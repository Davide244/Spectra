using System.Collections.Generic;
using System.Numerics;
using Microsoft.Extensions.Logging;
using Silk.NET.Input;

namespace SpectraEngine.Core.Input;

/// <summary>
/// Tracks keyboard and mouse state from Silk.NET input events and exposes a
/// pollable query surface. Events fire on the OS-event thread; queries are
/// expected from the render thread, so all shared state is mutated under a
/// single lock.
/// </summary>
public sealed class InputManager
{
    private readonly ILogger<InputManager> _logger;
    private readonly object _stateLock = new();
    private readonly HashSet<Key> _keysDown = [];
    private readonly HashSet<Key> _pendingPressed = [];
    private readonly HashSet<Key> _pressedThisFrame = [];
    private readonly HashSet<MouseButton> _mouseButtonsDown = [];
    private readonly HashSet<MouseButton> _pendingMousePressed = [];
    private readonly HashSet<MouseButton> _mousePressedThisFrame = [];

    private IInputContext? _inputContext;
    private Vector2 _accumulatedMouseDelta;
    private Vector2 _mouseDelta;
    private Vector2? _lastMousePosition;

    public InputManager(ILogger<InputManager> logger)
    {
        _logger = logger;
    }

    /// <summary>The mouse movement accumulated during the last <see cref="Update"/> interval.</summary>
    public Vector2 MouseDelta
    {
        // Locked like every other accessor: Vector2 is two floats, so an
        // unlocked read could observe a torn value mid-write.
        get { lock (_stateLock) return _mouseDelta; }
    }

    /// <summary>
    /// The cursor's latest position in window client coordinates: pixels,
    /// origin at the top-left, y growing downward — exactly the convention the
    /// camera's picking-ray API expects. Unlike <see cref="MouseDelta"/> this
    /// is not latched per frame: position is absolute, so the freshest value
    /// is always the right one. Zero until the OS reports a first position.
    /// </summary>
    public Vector2 MousePosition
    {
        // Locked for the same torn-read reason as MouseDelta.
        get { lock (_stateLock) return _lastMousePosition ?? Vector2.Zero; }
    }

    public void Initialize(IInputContext inputContext)
    {
        _inputContext = inputContext;

        for (int i = 0; i < _inputContext.Keyboards.Count; i++)
        {
            var keyboard = _inputContext.Keyboards[i];
            keyboard.KeyDown += OnKeyDown;
            keyboard.KeyUp += OnKeyUp;
        }

        for (int i = 0; i < _inputContext.Mice.Count; i++)
        {
            var mouse = _inputContext.Mice[i];
            mouse.MouseDown += OnMouseDown;
            mouse.MouseUp += OnMouseUp;
            mouse.MouseMove += OnMouseMove;
        }

        // Seed the pollable cursor position from the first mouse so a click
        // before any MouseMove event picks through the real cursor location
        // instead of (0,0). Initialize runs on the OS-event thread before the
        // render loop starts, so reading the device here is safe.
        if (_inputContext.Mice.Count > 0)
        {
            Vector2 position = _inputContext.Mice[0].Position;
            lock (_stateLock)
                _lastMousePosition ??= position;
        }

        _logger.LogInformation("Input manager initialized ({KeyboardCount} keyboards, {MouseCount} mice)",
            _inputContext.Keyboards.Count, _inputContext.Mice.Count);
    }

    /// <summary>Latches per-frame deltas; call once per game tick before querying.</summary>
    public void Update(double deltaTime)
    {
        lock (_stateLock)
        {
            _mouseDelta = _accumulatedMouseDelta;
            _accumulatedMouseDelta = Vector2.Zero;

            _pressedThisFrame.Clear();
            foreach (Key k in _pendingPressed)
                _pressedThisFrame.Add(k);
            _pendingPressed.Clear();

            _mousePressedThisFrame.Clear();
            foreach (MouseButton b in _pendingMousePressed)
                _mousePressedThisFrame.Add(b);
            _pendingMousePressed.Clear();
        }
    }

    public bool IsKeyDown(Key key)
    {
        lock (_stateLock)
            return _keysDown.Contains(key);
    }

    /// <summary>True for the single tick on which <paramref name="key"/> went from up to down.</summary>
    public bool WasKeyPressed(Key key)
    {
        lock (_stateLock)
            return _pressedThisFrame.Contains(key);
    }

    public bool IsMouseButtonDown(MouseButton button)
    {
        lock (_stateLock)
            return _mouseButtonsDown.Contains(button);
    }

    /// <summary>True for the single tick on which <paramref name="button"/> went from up to down.</summary>
    public bool WasMouseButtonPressed(MouseButton button)
    {
        lock (_stateLock)
            return _mousePressedThisFrame.Contains(button);
    }

    public void Shutdown()
    {
        if (_inputContext is not null)
        {
            // Mirror Initialize: detach our handlers so the devices hold no
            // references to this manager, then release the native context.
            for (int i = 0; i < _inputContext.Keyboards.Count; i++)
            {
                var keyboard = _inputContext.Keyboards[i];
                keyboard.KeyDown -= OnKeyDown;
                keyboard.KeyUp -= OnKeyUp;
            }

            for (int i = 0; i < _inputContext.Mice.Count; i++)
            {
                var mouse = _inputContext.Mice[i];
                mouse.MouseDown -= OnMouseDown;
                mouse.MouseUp -= OnMouseUp;
                mouse.MouseMove -= OnMouseMove;
            }

            _inputContext.Dispose();
            _inputContext = null;
        }

        _logger.LogInformation("Input manager shut down");
    }

    // ─── Event handlers (OS-event thread) ────────────────────

    private void OnKeyDown(IKeyboard keyboard, Key key, int keyCode)
    {
        lock (_stateLock)
        {
            // HashSet.Add returns false on auto-repeat — only true presses count.
            if (_keysDown.Add(key))
                _pendingPressed.Add(key);
        }
    }

    private void OnKeyUp(IKeyboard keyboard, Key key, int keyCode)
    {
        lock (_stateLock)
            _keysDown.Remove(key);
    }

    private void OnMouseDown(IMouse mouse, MouseButton button)
    {
        lock (_stateLock)
        {
            // Same edge rule as the keyboard: Add returns false while the
            // button is already down, so only true presses arm the edge.
            if (_mouseButtonsDown.Add(button))
                _pendingMousePressed.Add(button);
        }
    }

    private void OnMouseUp(IMouse mouse, MouseButton button)
    {
        lock (_stateLock)
            _mouseButtonsDown.Remove(button);
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        lock (_stateLock)
        {
            if (_lastMousePosition.HasValue)
                _accumulatedMouseDelta += position - _lastMousePosition.Value;
            _lastMousePosition = position;
        }
    }
}
