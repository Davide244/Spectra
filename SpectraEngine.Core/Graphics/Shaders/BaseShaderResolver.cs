using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Assets.Sources;
using System;
using System.IO;
using System.Text;

namespace SpectraEngine.Core.Graphics.Shaders;

/// <summary>
/// Where one shader came from: a cooked blob, or source text to compile.
/// </summary>
/// <param name="Cooked">
/// The compiled pipeline for the asking backend, when the content stack held
/// one. Non-null means nothing has to be compiled at all.
/// </param>
/// <param name="Source">
/// SpectraShade source to compile, when there was no cooked blob. Exactly one of
/// this and <paramref name="Cooked"/> is non-null.
/// </param>
/// <param name="WatchPath">
/// Absolute path to watch for hot-reload, or null when there is nothing on disk
/// behind this shader - a packed source, and every cooked one.
/// </param>
public readonly record struct ResolvedShader(PipelineBlob? Cooked, string? Source, string? WatchPath);

/// <summary>
/// Resolves a built-in shader through the content stack, exactly as a texture or a
/// material resolves.
/// </summary>
/// <remarks>
/// <para><b>Cooked first, then source, then the embedded copy, and the ORDER is
/// the feature.</b> A shipped game must not carry a shader compiler's work in
/// its frame budget, so a pack that holds <c>Shaders/Lit.specshadecomp</c> is
/// what the engine binds; a project that authored <c>Shaders/Lit.spectrashade</c>
/// and has not cooked it still runs, from source; and a build that has neither
/// falls back to the copy embedded in this assembly, which is what every build
/// did before packs existed and is why nothing here can leave a renderer with no
/// program.</para>
/// <para><b>There is no existence probe before the open, deliberately.</b> The
/// documented failure in this engine's content layer is an <c>Exists</c> that
/// disagrees with the <c>TryOpen</c> beside it - the probe answers out of one
/// source and the read out of another, or the entry is present and undecodable -
/// and the symptom is content that silently never resolves while every log line
/// reads healthy. One call decides, and its answer is the one used.</para>
/// <para><b>A cooked file with no blob for THIS backend is reported and then
/// falls through.</b> That is a pack cooked for a target list this run is not
/// in, which is a real mistake worth a line naming both the shader and the
/// backend; refusing to render over it would turn a mis-targeted pack into a
/// black window, and the source fallback below still produces the right picture.
/// The cooker is where that is fatal - see <c>PackVerifier</c>'s shader arm.
/// </para>
/// </remarks>
public static class BaseShaderResolver
{
    /// <summary>
    /// Resolves the built-in shader <paramref name="fileName"/> (a bare file
    /// name, e.g. <c>Lit.spectrashade</c>) for <paramref name="backend"/>.
    /// </summary>
    /// <param name="content">
    /// The mounted content stack, or null for a renderer nobody handed one -
    /// every test fixture, and any host that runs the engine without assets. A
    /// null stack resolves to the embedded copy, which is the behaviour every
    /// build had before this existed.
    /// </param>
    public static ResolvedShader ResolveBuiltIn(
        IContentSource? content, string fileName, GraphicsBackend backend, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(logger);

        if (content is not null)
        {
            string cookedPath = BaseShaders.CookedContentPath(fileName);
            if (TryReadCooked(content, cookedPath, backend, logger) is { } cooked)
                return new ResolvedShader(cooked, Source: null, WatchPath: null);

            string sourcePath = BaseShaders.ContentPath(fileName);
            if (content.TryOpen(sourcePath, out ContentBlob? blob))
            {
                using (blob)
                {
                    string text = DecodeUtf8(blob.Span);

                    // Asked only for content that was actually served: a source
                    // with no watch path is simply not watched, which is the
                    // correct answer for a packed one.
                    content.TryGetWatchPath(sourcePath, out string? watch);
                    return new ResolvedShader(Cooked: null, text, watch);
                }
            }
        }

        // The developer tree's own file when there is one, and only then the
        // embedded copy. Reading the FILE rather than the resource is what every
        // backend did before this resolver existed, and it is load-bearing in the
        // inner loop: the resource is a snapshot taken at build time, so serving
        // it here would make an edit invisible until a rebuild while the watcher
        // registered beside it went on claiming hot-reload was live.
        string? diskPath = BaseShaders.TryResolveSourcePath(fileName);
        if (diskPath is not null)
        {
            try
            {
                return new ResolvedShader(Cooked: null, File.ReadAllText(diskPath), diskPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The existence check inside TryResolveSourcePath and this read
                // are two looks at one filesystem, so this is a file that moved
                // between them rather than a design problem - and the embedded
                // copy below cannot fail, which is why it is the floor.
                logger.LogWarning(
                    "Shader source '{Path}' could not be read; using the embedded copy: {Reason}",
                    diskPath, ex.Message);
            }
        }

        return new ResolvedShader(Cooked: null, BaseShaders.ReadEmbeddedSource(fileName), WatchPath: null);
    }

    private static PipelineBlob? TryReadCooked(
        IContentSource content, string cookedPath, GraphicsBackend backend, ILogger logger)
    {
        if (!content.TryOpen(cookedPath, out ContentBlob? blob)) return null;

        using (blob)
        {
            PipelineBlob? pipeline;
            try
            {
                // Straight off the blob's span: on a mounted pack that is a
                // window into the mapped view, so the container's whole
                // no-copy-to-the-GPU argument survives the shader lane too.
                pipeline = ShaderFileReader.ReadPipeline(blob.Span, backend);
            }
            catch (InvalidDataException ex)
            {
                // Degraded rather than thrown, like every other content failure
                // in a frame: the caller falls through to source and the picture
                // is right. A cook that shipped this is caught by the verify.
                logger.LogError(
                    "Compiled shader '{Path}' could not be read and was ignored: {Reason}",
                    cookedPath, ex.Message);

                return null;
            }

            if (pipeline is not null) return pipeline;

            logger.LogError(
                "Compiled shader '{Path}' carries no blob for {Backend}, so it was compiled from source " +
                "instead. The pack was cooked for a different target list than this run is using.",
                cookedPath, backend);

            return null;
        }
    }

    // A content source hands out bytes, and a SpectraShade file is text: an
    // editor that saved one with a BOM would otherwise put U+FEFF in front of
    // the first token, which the lexer reports as a syntax error on line one of
    // a file that looks perfectly ordinary.
    private static string DecodeUtf8(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> bom = [0xEF, 0xBB, 0xBF];
        if (bytes.Length >= 3 && bytes[..3].SequenceEqual(bom)) bytes = bytes[3..];

        return Encoding.UTF8.GetString(bytes);
    }
}
