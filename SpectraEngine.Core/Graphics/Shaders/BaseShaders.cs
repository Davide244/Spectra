using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace SpectraEngine.Core.Graphics.Shaders;

/// <summary>
/// Accessor for the engine's built-in SpectraShade sources. The files are
/// embedded as assembly resources so they survive AOT publishing; when running
/// from a developer build the original source file on disk can also be located,
/// enabling hot-reload.
/// </summary>
public static class BaseShaders
{
    /// <summary>The extension a SpectraShade source file is written with.</summary>
    public const string SourceExtension = ".spectrashade";

    /// <summary>
    /// The content folder a shader resolves under, so a built-in has one
    /// identity whether it came from a folder, a pack or the embedded copy.
    /// </summary>
    /// <remarks>
    /// A built-in is an ASSET PATH before it is an embedded resource. Without
    /// that, a cooked pack has nowhere to put a compiled shader that the engine
    /// would then look for, and a project could never override one; with it,
    /// <c>Shaders/Lit.specshadecomp</c> in a pack is what a shipped game binds
    /// and the embedded copy is the fallback for a build that cooked none.
    /// </remarks>
    public const string ContentFolder = "Shaders";

    /// <summary>File name of the built-in lit shader.</summary>
    public const string LitFileName = "Lit.spectrashade";

    /// <summary>File name of the debug-line shader.</summary>
    public const string DebugLineFileName = "DebugLine.spectrashade";

    /// <summary>File name of the tone-mapping resolve shader.</summary>
    public const string PostResolveFileName = "PostResolve.spectrashade";

    /// <summary>File name of the deferred geometry pass.</summary>
    public const string GBufferFillFileName = "GBufferFill.spectrashade";

    /// <summary>File name of the deferred light pass.</summary>
    public const string DeferredLightFileName = "DeferredLight.spectrashade";

    /// <summary>File name of the shadow depth pass.</summary>
    public const string ShadowDepthFileName = "ShadowDepth.spectrashade";

    /// <summary>File name of the depth-tested world line shader.</summary>
    public const string WorldLineFileName = "WorldLine.spectrashade";

    /// <summary>File name of the world line's deferred, blended half.</summary>
    public const string WorldLineBlendFileName = "WorldLineBlend.spectrashade";

    // The resource name MSBuild computes for Graphics\BaseShaders\<file>: root
    // namespace, then the folder path with separators as dots. Naming it as a
    // constant is the point of this class's lookup - a suffix match over
    // GetManifestResourceNames answers "some resource ends this way", which a
    // rename of an unrelated file can satisfy, and the wrong shader then
    // compiles and renders rather than failing.
    private const string ResourcePrefix = "SpectraEngine.Core.Graphics.BaseShaders.";

    // Every file this class exposes, in declaration order. Surfaced through
    // FileNames so a test can assert the constants and the embedded set have
    // not drifted apart: dropping a file from the EmbeddedResource glob is a
    // build-configuration failure the compiler cannot see.
    private static readonly string[] AllFileNames =
    [
        LitFileName,
        DebugLineFileName,
        PostResolveFileName,
        GBufferFillFileName,
        DeferredLightFileName,
        ShadowDepthFileName,
        WorldLineFileName,
        WorldLineBlendFileName,
    ];

    // One-shot latch for the hot-reload verdict below.
    private static int _hotReloadStateLogged;

    /// <summary>File names of every built-in shader. Any thread.</summary>
    public static IReadOnlyList<string> FileNames => AllFileNames;

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

    /// <summary>The depth-tested world line, single target.</summary>
    public static string WorldLine => ReadEmbedded(WorldLineFileName);

    /// <summary>The world line's deferred half: post-light, alpha-blended, depth tested in the shader.</summary>
    public static string WorldLineBlend => ReadEmbedded(WorldLineBlendFileName);


    /// <summary>
    /// The content-relative path <paramref name="fileName"/> resolves under -
    /// <c>Shaders/Lit.spectrashade</c> for the built-in lit shader. Any thread.
    /// </summary>
    public static string ContentPath(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return $"{ContentFolder}/{fileName}";
    }

    /// <summary>
    /// The content-relative path the COOKED form of <paramref name="fileName"/>
    /// resolves under - <c>Shaders/Lit.specshadecomp</c>. Any thread.
    /// </summary>
    /// <remarks>
    /// The same string the shader cook rule emits, derived the same way from the
    /// source path, so the pack's entry and the engine's lookup cannot be spelled
    /// differently. A mismatch here is not an error anywhere: the lookup misses,
    /// the engine falls back to compiling source, and a shipped build silently
    /// pays for a compiler it was meant to have left behind.
    /// </remarks>
    public static string CookedContentPath(string fileName) =>
        ContentPath(Path.ChangeExtension(fileName, CompiledShaderFile.FileExtension));

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

    /// <summary>Source-file path for <see cref="WorldLine"/>, if locatable on disk.</summary>
    public static string? WorldLinePath => TryResolveSourcePath(WorldLineFileName);

    /// <summary>Source-file path for <see cref="WorldLineBlend"/>, if locatable on disk.</summary>
    public static string? WorldLineBlendPath => TryResolveSourcePath(WorldLineBlendFileName);

    /// <summary>
    /// Opens the embedded source for <paramref name="fileName"/> (a bare file
    /// name, e.g. <c>Lit.spectrashade</c>). The caller owns the stream. Any thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No resource is embedded under that name.
    /// </exception>
    public static Stream OpenEmbedded(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        var assembly = typeof(BaseShaders).Assembly;
        string resourceName = ResourcePrefix + fileName;
        return assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded shader resource '{fileName}' (looked up as '{resourceName}') " +
                $"not found in {assembly.GetName().Name}.");
    }

    /// <summary>
    /// The embedded source text for <paramref name="fileName"/> (a bare file
    /// name). The floor every other shader lookup falls back to. Any thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No resource is embedded under that name.
    /// </exception>
    public static string ReadEmbeddedSource(string fileName) => ReadEmbedded(fileName);

    private static string ReadEmbedded(string fileName)
    {
        using Stream stream = OpenEmbedded(fileName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// States once, at startup, whether shader hot-reload is live and why not
    /// when it is not. Repeated calls do nothing. Any thread.
    /// </summary>
    /// <remarks>
    /// Hot-reload needs the SpectraShade sources on disk, which
    /// <see cref="TryFindSourceRoot"/> can only find by walking up to a solution
    /// file. A NativeAOT-published developer build has no such tree above it, so
    /// the engine silently falls back to the embedded copies and every save is
    /// ignored for the rest of the session. Losing it is a legitimate state, not
    /// an error - so the engine keeps running and says so at Warning, rather
    /// than leaving somebody to work out from an unchanging picture that their
    /// edits stopped arriving.
    /// </remarks>
    public static void LogHotReloadState(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (Interlocked.Exchange(ref _hotReloadStateLogged, 1) != 0) return;

        string? root = TryFindSourceRoot();
        if (root is null)
        {
            logger.LogWarning(
                "Shader hot-reload off: no .slnx or .sln above the base directory {BaseDirectory}, " +
                "so the SpectraShade sources cannot be located on disk. Shaders come from the " +
                "embedded copies and edits to them will not be picked up (expected in a deployed " +
                "or NativeAOT-published build).",
                AppContext.BaseDirectory);
            return;
        }

        string directory = Path.Combine(root, "SpectraEngine.Core", "Graphics", "BaseShaders");
        if (!Directory.Exists(directory))
        {
            logger.LogWarning(
                "Shader hot-reload off: the source tree at {Root} has no {Directory}, " +
                "so shaders come from the embedded copies and edits will not be picked up.",
                root, directory);
            return;
        }

        logger.LogInformation("Shader hot-reload on; sources under {Directory}", directory);
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
