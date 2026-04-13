using Microsoft.Extensions.Logging;
using Silk.NET.Input;

namespace SpectraEngine.Core.Input;

public sealed class InputManager
{
    private readonly ILogger<InputManager> _logger;
    private IInputContext? _inputContext;

    public InputManager(ILogger<InputManager> logger)
    {
        _logger = logger;
    }

    public void Initialize(IInputContext inputContext)
    {
        _inputContext = inputContext;

        for (int i = 0; i < _inputContext.Keyboards.Count; i++)
        {
            var keyboard = _inputContext.Keyboards[i];
            keyboard.KeyDown += OnKeyDown;
            keyboard.KeyUp += OnKeyUp;
            keyboard.KeyChar += OnKeyChar;
        }

        for (int i = 0; i < _inputContext.Mice.Count; i++)
        {
            var mouse = _inputContext.Mice[i];
            mouse.MouseDown += OnMouseDown;
            mouse.MouseUp += OnMouseUp;
            mouse.MouseMove += OnMouseMove;
            mouse.Scroll += OnScroll;
        }

        _logger.LogInformation("Input manager initialized ({KeyboardCount} keyboards, {MouseCount} mice)",
            _inputContext.Keyboards.Count, _inputContext.Mice.Count);
    }

    public void Update(double deltaTime)
    {
    }

    public void Shutdown()
    {
        _logger.LogInformation("Input manager shut down");
    }

    // Keyboard
    private void OnKeyDown(IKeyboard keyboard, Key key, int keyCode)
    {
        //_logger.LogDebug("Key down: {Key} (scancode {KeyCode})", key, keyCode);
    }

    private void OnKeyUp(IKeyboard keyboard, Key key, int keyCode)
    {
        //_logger.LogDebug("Key up: {Key} (scancode {KeyCode})", key, keyCode);
    }

    private void OnKeyChar(IKeyboard keyboard, char character)
    {
        //_logger.LogDebug("Key char: {Character}", character);
    }

    // Mouse
    private void OnMouseDown(IMouse mouse, MouseButton button)
    {
        //_logger.LogDebug("Mouse down: {Button}", button);
    }

    private void OnMouseUp(IMouse mouse, MouseButton button)
    {
        //_logger.LogDebug("Mouse up: {Button}", button);
    }

    private void OnMouseMove(IMouse mouse, System.Numerics.Vector2 position)
    {
        //_logger.LogTrace("Mouse move: {Position}", position);
    }

    private void OnScroll(IMouse mouse, ScrollWheel scroll)
    {
        //_logger.LogDebug("Scroll: X={X} Y={Y}", scroll.X, scroll.Y);
    }
}
