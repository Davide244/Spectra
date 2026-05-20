using System;
using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.OpenGL;
using SpectraShade.Compiler;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// Stands up an invisible OpenGL window and a fully initialized
/// <see cref="OpenGLRenderer"/> so tests can compile shaders against a real GL
/// context. Initialization itself exercises the SpectraShade → GLSL pipeline.
/// </summary>
public sealed class GlRendererFixture : IDisposable
{
    private readonly IWindow _window;

    public OpenGLRenderer Renderer { get; }

    public GlRendererFixture()
    {
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
    }

    public void Dispose()
    {
        Renderer.Shutdown();
        _window.Dispose();
    }
}
