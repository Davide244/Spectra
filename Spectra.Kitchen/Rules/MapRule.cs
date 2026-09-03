using System;
using System.Collections.Generic;
using System.IO;
using Spectra.Kitchen.Cache;
using Spectra.Kitchen.Diagnostics;
using Spectra.Kitchen.Maps;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Maps;
using SpectraEngine.Core.Maps.Compiled;

namespace Spectra.Kitchen.Rules;

/// <summary>
/// Bakes a <c>.smap</c> bundle into a <c>.scmap</c>: a shipped game runs zero CSG
/// at load.
/// </summary>
/// <remarks>
/// <para><b>The source is a FOLDER, which is why this rule lists before it
/// reads.</b> Every other rule cooks one file; a map bundle is a directory holding
/// a document and, later, its scripts, so the rule asks
/// <see cref="IRuleContext.ListFiles"/> what is in it and then reads each file
/// through the context. That keeps the artifact a function of RECORDED inputs -
/// including the source digest, which covers every byte of the bundle rather than
/// just the document - and leaves exactly one gap, which
/// <see cref="IRuleContext.ListFiles"/> states: a file newly added to a bundle does
/// not invalidate a cached bake, because a directory listing is not something the
/// cache can restate.</para>
/// <para><b>It declares <c>KeepBrushSource</c> and nothing else.</b> That switch is
/// the only setting that changes these bytes, so a <c>--profile fast</c> run does
/// not re-bake a project's levels while a changed <c>--keep-brush-source</c> does.
/// Declaring one too few is a stale artifact and one too many is a rebuild, and the
/// declaration is what the cache key is built from.</para>
/// <para><b>The output path REDIRECTS, exactly as a texture's does.</b> The
/// authored bundle is <c>Maps/Lobby.smap</c> and the cooked entry is
/// <c>Maps/Lobby.scmap</c>: identity is the source path with the cooked extension,
/// which is the same rule <c>ImageContentPath</c> already expresses for
/// <c>.png</c> to <c>.simage</c>, so a boot that looks for one spelling and a cook
/// that writes another cannot happen.</para>
/// <para><b>A map that cannot be compiled writes NO output.</b> The runtime
/// degrades and the cooker does not: a level with a non-rigid brush node stops
/// recompiling in the editor and shows the last good world, which is exactly the
/// thing that must not ship, so the bake reports and emits nothing rather than
/// baking whatever the compile managed.</para>
/// </remarks>
public sealed class MapRule : IRule
{
    /// <inheritdoc/>
    public RuleKind Kind => RuleKind.Map;

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public CookSettingKeys SettingsRead => CookSettingKeys.KeepBrushSource;

    /// <inheritdoc/>
    public void Cook(IRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string bundle = context.SourcePath;
        string documentPath = bundle + '/' + MapFormat.DocumentFileName;

        // Every file in the bundle, read through the context so every one is a
        // recorded dependency and the source digest below covers bytes this rule
        // provably saw. The document is read from that same list rather than
        // separately, or a bundle would be read twice and the two reads could
        // disagree about what it holds.
        var files = new List<(string Path, byte[] Bytes)>();
        byte[]? documentBytes = null;

        foreach (string file in context.ListFiles(bundle))
        {
            string relative = file[(bundle.Length + 1)..];

            // Per-user editor state is skipped rather than read, and the digest
            // asks the same question: hashed in, it would put a different number in
            // every developer's compiled map for one level, and read as a
            // dependency it would miss the cook cache every time somebody moved a
            // viewport camera.
            if (!MapBundleDigest.IsSourceFile(relative)) continue;

            byte[] bytes = context.Read(file);
            files.Add((relative, bytes));

            if (file.Equals(documentPath, StringComparison.OrdinalIgnoreCase)) documentBytes = bytes;
        }

        if (documentBytes is null)
        {
            // Read for its own sake, so the miss is RECORDED: without it, the next
            // cook after somebody restores the document serves the stale bake and
            // reports success.
            context.Read(documentPath);
            return;
        }

        MapDocument document;
        try
        {
            document = MapReader.Read(documentBytes);
        }
        catch (MapFormatException ex)
        {
            context.Report(CookDiagnostic.Error(
                CookDiagnosticCodes.MapDocumentMalformed,
                $"'{documentPath}' is not a readable map: {ex.Message}",
                documentPath));

            return;
        }

        byte[]? compiled;
        try
        {
            compiled = ScmapBake.Bake(
                document,
                MapBundleDigest.Compute(files),
                context.KeepBrushSource,
                context.Report,
                bundle);
        }
        catch (MapFormatException ex)
        {
            // The binder's own refusal: a plane set Brush's constructor rejects. A
            // hole in the world rather than a missing decoration, so it is fatal
            // here where a missing model would only warn.
            context.Report(CookDiagnostic.Error(
                CookDiagnosticCodes.MapBrushRefused,
                $"'{bundle}' has a brush this engine cannot build: {ex.Message}",
                documentPath));

            return;
        }

        if (compiled is null) return;

        context.Emit(CookedPath(bundle), compiled, PackEntryKind.Map);
    }

    /// <summary>
    /// The content path a bundle's compiled map is emitted and resolved under.
    /// </summary>
    /// <remarks>
    /// One expression of the redirect, called by the rule that writes it and by
    /// anything that has to find it. A cook that spelled it one way and a boot that
    /// spelled it another produces no error anywhere: the runtime simply finds no
    /// compiled map while every log line reads healthy.
    /// </remarks>
    public static string CookedPath(string bundlePath) => CompiledMapPath.For(bundlePath);
}
