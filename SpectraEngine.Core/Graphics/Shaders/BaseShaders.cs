using System;
using System.IO;
using System.Linq;

namespace SpectraEngine.Core.Graphics.Shaders;

/// <summary>
/// Accessor for the engine's built-in SpectraShade sources. The files are
/// embedded as assembly resources so they survive AOT publishing; when running
/// from a developer build the original source file on disk can also be located,
/// enabling hot-reload.
/// </summary>
public static class BaseShaders
{
    private const string LitFileName = "Lit.spectrashade";
    private const string DebugLineFileName = "DebugLine.spectrashade";
    private const string PostResolveFileName = "PostResolve.spectrashade";
    private const string GBufferFillFileName = "GBufferFill.spectrashade";
    private const string DeferredLightFileName = "DeferredLight.spectrashade";
    private const string ShadowDepthFileName = "ShadowDepth.spectrashade";

    /// <summary>The built-in lit shader — diffuse + ambient from one directional light, modulated by a diffuse texture.</summary>
    public static string Lit => ReadEmbedded(LitFileName);

    /// <summary>The unlit per-vertex-coloured shader used by the debug-draw renderer.</summary>
    public static string DebugLine => ReadEmbedded(DebugLineFileName);

    /// <summary>The tone-mapping resolve: the one place linear light becomes a display image.</summary>
    public static string PostResolve => ReadEmbedded(PostResolveFileName);

    /// <summary>The deferred geometry pass: writes surface properties, never light.</summary>
    public static string GBufferFill => ReadEmbedded(GBufferFillFileName);

    /// <summary>The deferred light pass: a Cook-Torrance BRDF over the G-buffer.</summary>
    public static string DeferredLight => ReadEmbedded(DeferredLightFileName);

    /// <summary>The shadow map's depth pass: writes depth from the light, and nothing else.</summary>
    public static string ShadowDepth => ReadEmbedded(ShadowDepthFileName);

    /// <summary>
    /// Resolves the absolute path of <paramref name="fileName"/> on disk if the
    /// engine is running from a developer build (the source tree is present),
    /// or returns null if only the embedded resource is available.
    /// </summary>
    public static string? TryResolveSourcePath(string fileName)
    {
        string? root = TryFindSourceRoot();
        if (root is null) return null;
        string candidate = Path.Combine(root, "SpectraEngine.Core", "Graphics", "BaseShaders", fileName);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>Source-file path for <see cref="Lit"/>, if locatable on disk.</summary>
    public static string? LitPath => TryResolveSourcePath(LitFileName);

    /// <summary>Source-file path for <see cref="DebugLine"/>, if locatable on disk.</summary>
    public static string? DebugLinePath => TryResolveSourcePath(DebugLineFileName);

    /// <summary>Source-file path for <see cref="PostResolve"/>, if locatable on disk.</summary>
    public static string? PostResolvePath => TryResolveSourcePath(PostResolveFileName);

    /// <summary>Source-file path for <see cref="GBufferFill"/>, if locatable on disk.</summary>
    public static string? GBufferFillPath => TryResolveSourcePath(GBufferFillFileName);

    /// <summary>Source-file path for <see cref="DeferredLight"/>, if locatable on disk.</summary>
    public static string? DeferredLightPath => TryResolveSourcePath(DeferredLightFileName);

    /// <summary>Source-file path for <see cref="ShadowDepth"/>, if locatable on disk.</summary>
    public static string? ShadowDepthPath => TryResolveSourcePath(ShadowDepthFileName);

    private static string ReadEmbedded(string fileName)
    {
        var assembly = typeof(BaseShaders).Assembly;
        string? resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.Ordinal));
        if (resourceName is null)
            throw new InvalidOperationException(
                $"Embedded shader resource '{fileName}' not found in {assembly.GetName().Name}.");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // Walks up from the executable directory looking for the solution file —
    // identifies a developer build (bin/Debug or bin/Release somewhere under
    // the source tree). Returns null for deployed builds where no parent
    // contains the .slnx (so hot-reload silently no-ops).
    private static string? TryFindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("*.slnx").Length > 0 || dir.GetFiles("*.sln").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
