using System;
using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SpectraEngine.Core;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.OpenGL;
using SpectraShade.Compiler;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// Groups every test class that needs the one real GL context.
/// </summary>
/// <remarks>
/// A collection fixture, not a class fixture: GLFW registers a process-global
/// Win32 window class, so a second <see cref="GlRendererFixture"/> fails with
/// "class already exists". One instance shared by the whole collection is the
/// only arrangement that works, and it also serialises the classes so two of
/// them never drive the context at once.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class GlRendererCollection : ICollectionFixture<GlRendererFixture>
{
    /// <summary>Name to put on each participating class's <c>[Collection]</c>.</summary>
    public const string Name = "OpenGL renderer";
}

/// <summary>
/// Stands up an invisible OpenGL window and a fully initialized
/// <see cref="OpenGLRenderer"/> so tests can compile shaders against a real GL
/// context. Initialization itself exercises the SpectraShade → GLSL pipeline.
/// </summary>
public sealed class GlRendererFixture : IDisposable
{
    private readonly IWindow _window;

    public OpenGLRenderer Renderer { get; }

    /// <summary>
    /// The one real window this process may own. Exposed because the
    /// borderless-fullscreen test needs a real one too, and per the remarks on
    /// <see cref="GlRendererCollection"/> a second is not possible — so it
    /// borrows this one and puts its geometry back.
    /// </summary>
    public IWindow HostWindow => _window;

    /// <summary>
    /// A GL function table over the fixture's context, for tests that need to
    /// ask the driver what it actually did. Loading a second table is free and
    /// changes no state; the context itself stays the one the renderer owns.
    /// </summary>
    public GL Gl { get; }

    public GlRendererFixture()
    {
        // Same call the engine makes before its own Window.Create: the fixture
        // must not rely on Silk.NET's reflection-based backend discovery either,
        // so a trimmed/AOT test host behaves identically to this JIT one.
        SilkPlatform.EnsureRegistered();

        var options = WindowOptions.Default with
        {
            IsVisible = false,
            Size = new Vector2D<int>(64, 64),
            Title = "spectra-shader-tests",
            VSync = false,
        };

        _window = Window.Create(options);
        _window.Initialize();

        Renderer = new OpenGLRenderer(
            NullLogger<Renderer>.Instance,
            new SpectraShadeCompiler());
        Renderer.Initialize(_window);

        // After Initialize, so the context exists and is current on this thread.
        Gl = _window.CreateOpenGL();

        // The engine publishes this from the main thread before the render
        // thread starts, so a renderer that has never been told its framebuffer
        // size is not a state the engine can be in. Seeding it here matters:
        // anything that sizes an intermediate target to the window (the HDR
        // target, the deferred G-buffer) reads this latch and treats zero as
        // "minimised, nothing to draw".
        Renderer.SetFramebufferSize(options.Size);
    }

    public void Dispose()
    {
        Renderer.Shutdown();
        _window.Dispose();
    }
}
