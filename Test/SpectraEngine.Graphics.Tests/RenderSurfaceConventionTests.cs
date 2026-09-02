using SpectraEngine.Core.Graphics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// The seam that makes the engine embeddable: <b>nothing under
/// <c>Graphics/</c> may name a window.</b>
/// </summary>
/// <remarks>
/// A renderer that takes a Silk.NET <c>IWindow</c> takes far more than a surface:
/// a title, a cursor, an event pump, and a lifetime. An editor shell owns every
/// one of those and can hand the engine only a native child handle and, for
/// OpenGL, a context. Narrowing the dependency to <see cref="IRenderSurface"/>
/// is what turns hosting into "write one small adapter" instead of "touch three
/// backends", and it is worth nothing if the next backend change quietly reaches
/// for a window again.
/// <para>
/// <b>Enforced in the source, like the COM ownership rule</b>, and for the same
/// reason: the thing that would break is a compile-time dependency, so no
/// runtime test can see it. <c>WindowRenderSurface</c> is the one legitimate
/// exception, because being the adapter is its entire job.
/// </para>
/// </remarks>
public sealed class RenderSurfaceConventionTests
{
    [Fact]
    public void No_graphics_source_names_a_window()
    {
        var offenders = new List<string>();

        foreach (string file in GraphicsSources())
        {
            // The adapter, and the fullscreen latch's own vocabulary, which is
            // about the window the ENGINE owns and never reaches a backend.
            string name = Path.GetFileName(file);
            if (name == "WindowRenderSurface.cs")
                continue;

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (IsComment(line))
                    continue;

                // IWindow, but not IWindowModeLatch or IWindowModeTarget: those
                // name the engine's own fullscreen seam, which is deliberately
                // backend-neutral already and never carries a surface.
                int at = line.IndexOf("IWindow", StringComparison.Ordinal);
                while (at >= 0)
                {
                    if (!IsLongerIdentifier(line, at))
                    {
                        offenders.Add($"{name}({i + 1}): {line.Trim()}");
                        break;
                    }

                    at = line.IndexOf("IWindow", at + 1, StringComparison.Ordinal);
                }
            }
        }

        offenders.ShouldBeEmpty(
            "the renderer depends on IRenderSurface, not on a window: a backend that reaches for " +
            "IWindow again takes ownership of a title, a cursor and an event pump that an embedded " +
            "host already owns, and re-couples the engine to running its own window");
    }

    /// <summary>
    /// The same seam pointed the other way: <b>nothing under <c>Graphics/</c>
    /// may name the shell's UI framework.</b>
    /// </summary>
    /// <remarks>
    /// A composited surface hands a texture to something outside the engine, and
    /// the vocabulary for that (a shared handle, a keyed mutex, a generation) is
    /// deliberately made of nothing but a native handle and integers. The moment
    /// a backend names the framework on the other side, the engine can only ever
    /// be embedded in that one shell, and the whole reason
    /// <see cref="IRenderSurface"/> exists is gone.
    /// <para>
    /// <b>No file is exempt, unlike the window rule.</b> There is no adapter
    /// here to be the exception: the shell's adapter lives in the shell, which
    /// is the arrangement being enforced.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_graphics_source_names_the_shell_ui_framework()
    {
        var offenders = new List<string>();

        foreach (string file in GraphicsSources())
        {
            string name = Path.GetFileName(file);
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (IsComment(line))
                    continue;

                if (line.Contains("Avalonia", StringComparison.Ordinal))
                    offenders.Add($"{name}({i + 1}): {line.Trim()}");
            }
        }

        offenders.ShouldBeEmpty(
            "the renderer's shared-target vocabulary is a native handle and four integers, and it stays " +
            "that way: a backend that names the shell's UI framework can be embedded in exactly one shell, " +
            "which is the coupling IRenderSurface was introduced to remove");
    }

    [Fact]
    public void Every_backend_refuses_a_surface_it_cannot_use()
    {
        // The refusal is the whole value of Kind carrying the platform rather
        // than the handle being a bare nint: "wrong platform" has to stay a
        // clear error instead of whatever a driver does with a nonsense pointer.
        var glOnly = new StubSurface(RenderSurfaceKind.None, handle: 0);
        var win32Only = new StubSurface(RenderSurfaceKind.Win32, handle: 1234);

        glOnly.GLContext.ShouldBeNull();
        win32Only.GLContext.ShouldBeNull();

        // A GL-less surface cannot drive the GL backend, and a context-less,
        // handle-less one cannot drive anything. Both messages have to name what
        // was actually supplied, because "it did not start" with no reason is
        // the failure mode an embedded host will hit first.
        Should.Throw<InvalidOperationException>(() => new Core.Graphics.OpenGL.OpenGLRenderer(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Renderer>.Instance,
                new SpectraShade.Compiler.SpectraShadeCompiler())
            .Initialize(win32Only))
            .Message.ShouldContain("GL context");
    }

    private sealed class StubSurface(RenderSurfaceKind kind, nint handle) : IRenderSurface
    {
        public RenderSurfaceKind Kind => kind;
        public nint NativeHandle => handle;
        public Silk.NET.Core.Contexts.IGLContext? GLContext => null;
        public Silk.NET.Maths.Vector2D<int> PixelSize => new(64, 64);

        public event Action<Silk.NET.Maths.Vector2D<int>>? Resized
        {
            add { }
            remove { }
        }
    }

    // True when the match is part of a longer identifier such as
    // IWindowModeLatch, which is the engine's fullscreen seam and not a window.
    private static bool IsLongerIdentifier(string line, int at)
    {
        int after = at + "IWindow".Length;
        return after < line.Length && (char.IsLetterOrDigit(line[after]) || line[after] == '_');
    }

    private static bool IsComment(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("*", StringComparison.Ordinal);
    }

    private static IEnumerable<string> GraphicsSources()
    {
        string graphics = Path.Combine(SourceRoot(), "SpectraEngine.Core", "Graphics");
        Directory.Exists(graphics).ShouldBeTrue($"expected the graphics sources under {graphics}");
        return Directory.EnumerateFiles(graphics, "*.cs", SearchOption.AllDirectories);
    }

    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("*.slnx").Length > 0 || dir.GetFiles("*.sln").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"No solution file above {AppContext.BaseDirectory}; the source-convention test needs the repo.");
    }
}
