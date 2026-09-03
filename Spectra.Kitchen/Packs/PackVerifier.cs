using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spectra.Kitchen.Cache;
using Spectra.Kitchen.Diagnostics;
using Spectra.Kitchen.Rules;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Assets.Images;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Assets.Sources;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.Shaders;
using System;
using System.Collections.Generic;
using System.IO;

namespace Spectra.Kitchen.Packs;

/// <summary>
/// What one <c>verify</c> run found.
/// </summary>
public sealed class PackVerifyResult
{
    /// <summary>The pack that was verified.</summary>
    public required string PackPath { get; init; }

    /// <summary>Everything the run has to say, in the order it found it.</summary>
    public required IReadOnlyList<CookDiagnostic> Diagnostics { get; init; }

    /// <summary>Entries whose payload was opened and measured.</summary>
    public int EntriesChecked { get; init; }

    /// <summary>Tombstones, which have no payload to check.</summary>
    public int TombstonesSkipped { get; init; }

    /// <summary>In-pack references resolved, across every format that expresses one.</summary>
    public int ReferencesChecked { get; init; }

    /// <summary>Uncompressed bytes decoded.</summary>
    public long PayloadBytes { get; init; }

    /// <summary>How many diagnostics are errors.</summary>
    public int ErrorCount { get; init; }

    /// <summary>How many diagnostics are warnings.</summary>
    public int WarningCount { get; init; }

    /// <summary>Whether the pack passed.</summary>
    public bool Succeeded => ErrorCount == 0;
}

/// <summary>
/// Proves a written <c>.spack</c> is one a shipped game can actually run on:
/// every payload decodes, every in-pack reference resolves, the table on disk is
/// searchable, and the digest agrees.
/// </summary>
/// <remarks>
/// <para><b>The failure this exists for is a pack that mounts cleanly and is
/// broken anyway.</b> Mounting proves the container; it says nothing about
/// whether a material's texture was cooked, and the runtime's answer to that
/// question is a magenta placeholder and a warning. So a cook can succeed, a
/// pack can mount, every log line can read healthy, and the shipped game shows
/// checkerboards.</para>
/// <para><b>Every reference is resolved through a STRICT
/// <see cref="ContentSourceStack"/> carrying the pack and NOTHING ELSE, and both
/// halves of that are the feature.</b> Alone, because the editor resolves loose
/// files at a higher priority than the pack, so a validation run mounted beside
/// them would silently answer out of the source tree and prove nothing about
/// what shipped. Strict, because a miss must THROW here where the same miss
/// degrades in a frame - and strictness is a property of the stack precisely so
/// that <see cref="AssetManager"/>'s degradation stays a pinned invariant rather
/// than becoming conditional on where content came from. The two behaviours are
/// deliberately different and neither is a bug in the other; see
/// <c>docs/formats-and-pipeline.md</c> 4.2.</para>
/// <para><b>The decode pass is not a slower restatement of the digest.</b> A
/// payload rewritten together with the digest over it hashes correctly and is
/// still not a deflate stream, so the two catch different files: the digest
/// catches a bit that rotted on disk, the decode catches bytes that were never
/// valid.</para>
/// <para><b>The table check is a claim about the FILE, not about the
/// writer.</b> <see cref="PackWriter"/> sorts and refuses collisions, which is a
/// statement about the code that wrote a pack; this is a statement about the
/// bytes in front of you, which is what a verify is for and is also the only one
/// of the two that survives a file being edited afterwards.</para>
/// <para><b>It uses <see cref="PackSource"/>, the reader a shipped game uses.</b>
/// Verifying through a tool-side reader would prove a tool can read the file.
/// </para>
/// <para><b>This verb and the COOK overlap on purpose, and
/// <see cref="CookGate"/> is authoritative for both.</b> A material naming a
/// texture nobody cooked is caught twice now - once by <c>MaterialRule</c>
/// against the project folder, once here against the written pack - and that is
/// not a redundancy to remove, because they are different claims: the cook says
/// the AUTHOR's content is consistent, this says the ARTIFACT is, which also
/// covers a pack somebody edited, a pack another build produced, and the case a
/// cook structurally cannot see, where two rules each succeed and the entry one
/// of them needed never reached the file. What must never differ is the VERDICT,
/// so neither site chooses a severity: both report a code and the gate decides,
/// which is why a texture missing in a project and the same texture missing in a
/// pack are one code at one loudness.</para>
/// </remarks>
public static class PackVerifier
{
    /// <summary>Verifies the pack at <paramref name="packPath"/>.</summary>
    /// <remarks>
    /// Never throws for anything about the pack's CONTENT: a refusal is a
    /// diagnostic, because the caller is a build gate that wants every problem in
    /// one run rather than the first one. A path the filesystem refuses does
    /// throw, because that is not a fact about the pack.
    /// </remarks>
    /// <param name="packPath">The <c>.spack</c> to verify.</param>
    /// <param name="logger">Where the mount reports its own trouble.</param>
    /// <param name="targets">
    /// The backends the pack was cooked for, when the caller knows. Null asks
    /// the pack itself - see <see cref="CheckShaders"/> for what that can and
    /// cannot prove.
    /// </param>
    /// <param name="strict">
    /// Whether this run is the gate: promotes the warn-by-default half of
    /// <see cref="CookGate"/> to errors, exactly as it does in a cook. Carried
    /// here rather than left to the cook alone because <c>scook verify</c> is the
    /// CI step, and a switch meaning one thing under one verb and nothing under
    /// the other is a switch nobody can rely on.
    /// </param>
    /// <exception cref="IOException">The file could not be opened.</exception>
    public static PackVerifyResult Verify(
        string packPath,
        ILogger? logger = null,
        IReadOnlyList<GraphicsBackend>? targets = null,
        bool strict = false)
    {
        ArgumentNullException.ThrowIfNull(packPath);

        string file = Path.GetFullPath(packPath);

        // The same collector the cook uses, so the gate is applied by the shape
        // rather than by every site here remembering to ask for it.
        var diagnostics = new CookDiagnosticLog(strict);

        PackContents contents;
        try
        {
            contents = PackContents.Read(file);
        }
        catch (PackMountException ex)
        {
            diagnostics.Add(CookDiagnostic.Error(CookDiagnosticCodes.PackNotMountable, ex.Message, file));
            return Finish(file, diagnostics, 0, 0, 0, 0);
        }

        CheckEntryOrder(contents, diagnostics);

        // After the table check and before anything else: the mount is what
        // validates the header, the regions, every entry's payload window and the
        // trailing digest, and a pack it refuses can answer no further question.
        // The order matters the other way too - the table check runs first so a
        // file with a swapped pair of records is reported by the entry NAMES it
        // put out of order rather than only as the mount's refusal.
        PackSource pack;
        try
        {
            pack = new PackSource(logger ?? NullLogger.Instance, file);
        }
        catch (PackMountException ex)
        {
            diagnostics.Add(CookDiagnostic.Error(CookDiagnosticCodes.PackNotMountable, ex.Message, file));
            return Finish(file, diagnostics, 0, 0, 0, 0);
        }

        int payloads = 0, tombstones = 0, references = 0;
        long payloadBytes = 0;

        // Collected during the walk and judged after it: the fallback for "which
        // backends should be here" is the union over the pack's own shaders, and
        // a union cannot be known until every shader has been seen. One code path
        // whether or not the caller named a target list, because two would drift
        // exactly where nobody is looking.
        var shaders = new List<ShaderEntry>();

        using (pack)
        {
            // The pack ALONE. See the class remarks: a stack that also carried the
            // loose tree would resolve a missing cooked texture out of the source
            // folder and report a pass for a pack that ships a hole.
            var strictStack = new ContentSourceStack(strict: true);
            strictStack.Mount(pack);

            for (int i = 0; i < contents.Entries.Count; i++)
            {
                PackEntry entry = contents.Entries[i];
                if (entry.IsTombstone)
                {
                    tombstones++;
                    continue;
                }

                string name = contents.NameOf(i);
                if (name.Length == 0)
                {
                    diagnostics.Add(CookDiagnostic.Warning(
                        CookDiagnosticCodes.PackEntryNotVerifiable,
                        $"Entry {i} (id {entry.AssetId:X32}) carries no name, so there is no path to ask the " +
                        "reader for and its payload was not decoded. Cook with the name table on.",
                        file));
                    continue;
                }

                // Opened through the pack rather than through the strict stack,
                // deliberately. A source answers false for both "not here" and
                // "here and unreadable", so the stack's strict throw cannot tell
                // them apart - and for an entry the table demonstrably holds, the
                // only remaining meaning of false is that the payload did not
                // decode. Strictness is for the reference pass below, where a miss
                // is exactly what it names.
                if (!pack.TryOpen(name, out ContentBlob? blob))
                {
                    diagnostics.Add(CookDiagnostic.Error(
                        CookDiagnosticCodes.PackEntryUnreadable,
                        $"'{name}' is in the entry table and its payload did not decode " +
                        $"({entry.StoredSize} stored bytes, codec {entry.EntryCodec}). The digest agrees, so " +
                        "these bytes were written this way rather than corrupted since.",
                        file));
                    continue;
                }

                using (blob)
                {
                    payloads++;
                    payloadBytes += blob.Length;

                    if ((ulong)blob.Length != entry.UncompressedSize)
                    {
                        diagnostics.Add(CookDiagnostic.Error(
                            CookDiagnosticCodes.PackEntryUnreadable,
                            $"'{name}' declares {entry.UncompressedSize} uncompressed bytes and decoded to " +
                            $"{blob.Length}.",
                            file));
                        continue;
                    }

                    references += CheckReferences(name, blob.Span, strictStack, diagnostics, file);
                    CheckImage(name, blob.Span, diagnostics, file);
                    CollectShader(name, blob.Span, shaders, diagnostics, file);
                }
            }
        }

        CheckShaders(shaders, targets, diagnostics, file);

        return Finish(file, diagnostics, payloads, tombstones, references, payloadBytes);
    }

    // One cooked shader and the backends its entry table declares.
    private readonly record struct ShaderEntry(string Name, GraphicsBackend[] Backends);

    /// <summary>
    /// Resolves whatever cross-asset references one entry's format expresses.
    /// </summary>
    /// <remarks>
    /// <b>This is the method that GROWS, and it grows one arm per cooked
    /// format.</b> Each arm parses the payload with the engine's own reader for
    /// that format and resolves what it names through the strict stack, and each
    /// reports in the band that names its subsystem, so the code a build log
    /// carries says which kind of asset was wrong before it says anything else:
    /// <list type="bullet">
    /// <item><description><c>.simage</c> - nothing to RESOLVE; an image is a
    /// leaf, so it is checked for readability by <see cref="CheckImage"/>
    /// instead. It joins this switch only if a cooked image ever names a
    /// companion (a streamed mip tail), and would report in the 2xxx
    /// band.</description></item>
    /// <item><description><c>.smodel</c> - its material references, in the 3xxx
    /// band. The authored <c>.obj</c>/<c>.mtl</c> pair already expresses one
    /// today and is deliberately not checked here: it is raw-copied rather than
    /// cooked, so the reference it carries is resolved by the importer at load
    /// time and not by anything in the pack.</description></item>
    /// <item><description><c>.scmap</c> - every material, model and script a
    /// level names, in the 7xxx band. The largest arm, and the one that turns
    /// this from a spot check into a whole-game one.</description></item>
    /// <item><description>a shader blob per requested backend, in the 6xxx band.
    /// It is <see cref="CheckShaders"/> rather than an arm here, because the
    /// claim is about the SET of shaders in the pack rather than about one
    /// entry's contents, and a set cannot be judged from inside a loop over
    /// it.</description></item>
    /// </list>
    /// </remarks>
    private static int CheckReferences(
        string name,
        ReadOnlySpan<byte> payload,
        ContentSourceStack strictStack,
        CookDiagnosticLog diagnostics,
        string packFile)
    {
        if (!name.EndsWith(MaterialParser.FileExtension, StringComparison.OrdinalIgnoreCase))
            return 0;

        MaterialDefinition material = MaterialParser.ParseUtf8(payload, name);

        // The parser's own complaints, carried rather than swallowed. It warns
        // instead of throwing so files stay forward-compatible, which means a
        // material with an unusable line is otherwise a silently weaker material
        // and nothing anywhere says so.
        foreach (string warning in material.Warnings)
            diagnostics.Add(CookDiagnostic.Warning(CookDiagnosticCodes.MaterialFileMalformed, warning, packFile));

        int resolved = 0;
        foreach (MaterialTextureSlot slot in material.Textures)
        {
            resolved++;

            try
            {
                // Through the SAME redirection the engine uses: a material names
                // the authored PNG and the pack holds its cooked .simage, so
                // asking for the literal path would report every texture in every
                // cooked pack as missing. ImageContentPath is the one expression
                // of that rule and both callers go through it, which is what
                // makes this check a claim about what the engine would actually
                // resolve rather than about what the table happens to spell.
                string imagePath = ImageContentPath.Resolve(strictStack, slot.TexturePath);
                if (strictStack.TryOpen(imagePath, out ContentBlob? texture))
                {
                    texture.Dispose();
                    continue;
                }
            }
            catch (FileNotFoundException)
            {
                // The strict stack's whole job, and the one line in this file
                // where the cooker's loudness and the runtime's softness are
                // visibly different behaviours over identical inputs.
            }

            // Same code and the same gate verdict as MaterialRule's cook-time
            // report, differing only in what "not there" was measured against:
            // "not in this pack" rather than "not in the content root". A build
            // log that said one thing when a cook caught it and another when a
            // verify did would be describing two failures where there is one.
            diagnostics.Add(CookDiagnostic.Error(
                CookDiagnosticCodes.MaterialTextureMissing,
                $"'{name}' binds sampler '{slot.Name}' to '{slot.TexturePath}', which is not in this pack. " +
                "The running engine would show the magenta placeholder and carry on; a shipped build would " +
                "ship that.",
                packFile));
        }

        return resolved;
    }

    /// <summary>
    /// Proves a cooked image is one this engine can actually upload.
    /// </summary>
    /// <remarks>
    /// <b>The digest cannot catch what this catches.</b> A <c>.simage</c> whose
    /// profile version does not match this build, or whose level index disagrees
    /// with its own format, hashes perfectly and is still a texture nothing can
    /// bind - so a pack would mount, every log line would read healthy, and the
    /// shipped game would show magenta. Reported through the reader's own
    /// message, which already names which rule the file broke; every one of them
    /// has the same answer, which is to recook.
    /// </remarks>
    private static void CheckImage(
        string name, ReadOnlySpan<byte> payload, CookDiagnosticLog diagnostics, string packFile)
    {
        if (!ImageContentPath.IsCooked(name)) return;

        try
        {
            _ = SimageReader.Read(payload, name);
        }
        catch (InvalidDataException ex)
        {
            diagnostics.Add(CookDiagnostic.Error(CookDiagnosticCodes.ImageFileUnreadable, ex.Message, packFile));
        }
    }

    /// <summary>
    /// Records which backends one cooked shader carries, refusing a payload the
    /// engine's own reader cannot parse.
    /// </summary>
    /// <remarks>
    /// Read through <see cref="ShaderFileReader.ReadBackends"/> - the same header
    /// and entry-table parse a load takes - so a verify cannot pass a file the
    /// engine would then refuse. Only the table is touched: the stage payloads
    /// are the bulk of a shader and nothing here has a question about them that
    /// the decode pass above has not already answered.
    /// </remarks>
    private static void CollectShader(
        string name,
        ReadOnlySpan<byte> payload,
        List<ShaderEntry> shaders,
        CookDiagnosticLog diagnostics,
        string packFile)
    {
        if (!name.EndsWith(ShaderRule.CookedExtension, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            shaders.Add(new ShaderEntry(name, ShaderFileReader.ReadBackends(payload)));
        }
        catch (InvalidDataException ex)
        {
            // The engine degrades here and compiles from source; the cooker does
            // not, because a build step whose job is to stop broken data shipping
            // must not share the runtime's soft landing.
            diagnostics.Add(CookDiagnostic.Error(
                CookDiagnosticCodes.ShaderFileUnreadable,
                $"'{name}' is a cooked shader this engine's reader refuses: {ex.Message}",
                packFile));
        }
    }

    /// <summary>
    /// Every cooked shader must carry a blob for every backend the pack was
    /// cooked for.
    /// </summary>
    /// <remarks>
    /// <para><b>The failure is silent everywhere else.</b> A shader missing one
    /// backend's blob mounts, loads and renders: the engine reports the miss and
    /// compiles that shader from source, so the picture is right and the shipped
    /// build pays for a compiler front end it was meant to have left behind. This
    /// is the only place that can say so.</para>
    /// <para><b>With no target list, the expectation is the UNION over the pack's
    /// own shaders, and that is a deliberately weaker claim.</b> A pack does not
    /// record what it was cooked for and inventing a header field to hold it
    /// would be a format change made for a verify. The union catches the failure
    /// that actually happens - one shader short of what every other shader in the
    /// same pack managed - and cannot catch a pack whose shaders are uniformly
    /// missing a backend, which is why <c>scook verify --target</c> exists and is
    /// authoritative when given.</para>
    /// </remarks>
    private static void CheckShaders(
        List<ShaderEntry> shaders,
        IReadOnlyList<GraphicsBackend>? targets,
        CookDiagnosticLog diagnostics,
        string packFile)
    {
        if (shaders.Count == 0) return;

        List<GraphicsBackend> expected = targets is { Count: > 0 }
            ? [.. targets]
            : UnionOfBackends(shaders);

        // In entry order, then in expected order, so a pack with several holes
        // reports them the same way every run.
        for (int i = 0; i < shaders.Count; i++)
        {
            ShaderEntry shader = shaders[i];
            for (int j = 0; j < expected.Count; j++)
            {
                if (Array.IndexOf(shader.Backends, expected[j]) >= 0) continue;

                diagnostics.Add(CookDiagnostic.Error(
                    CookDiagnosticCodes.ShaderBackendMissing,
                    $"'{shader.Name}' carries no blob for {CookSettingsDigest.ToWire(expected[j])}. " +
                    "The running engine would compile that shader from source and render correctly; a " +
                    "shipped build would ship the compiler's cost.",
                    packFile));
            }
        }
    }

    // First appearance order, never sorted: the expectation is then stated in the
    // order the pack's own first shader was cooked in, which is the order a
    // command line asked for, so a diagnostic list does not reorder itself
    // because an enum's numbering changed.
    private static List<GraphicsBackend> UnionOfBackends(List<ShaderEntry> shaders)
    {
        var union = new List<GraphicsBackend>(4);
        for (int i = 0; i < shaders.Count; i++)
        {
            GraphicsBackend[] backends = shaders[i].Backends;
            for (int j = 0; j < backends.Length; j++)
            {
                if (!union.Contains(backends[j])) union.Add(backends[j]);
            }
        }

        return union;
    }

    // The claim the writer cannot make on the file's behalf. Strictly ascending
    // covers both properties at once: an equal pair is a collision, whose harm is
    // that a binary search's answer depends on where it happened to land, and a
    // descending pair is an unsorted table, whose harm is that the search misses
    // entries entirely. Both present as content that is intermittently absent.
    private static void CheckEntryOrder(PackContents contents, CookDiagnosticLog diagnostics)
    {
        for (int i = 1; i < contents.Entries.Count; i++)
        {
            UInt128 previous = contents.Entries[i - 1].AssetId;
            UInt128 current = contents.Entries[i].AssetId;
            if (current > previous) continue;

            string what = current == previous
                ? $"share asset id {current:X32}"
                : $"are out of order: {current:X32} does not sit above {previous:X32}";

            diagnostics.Add(CookDiagnostic.Error(
                CookDiagnosticCodes.PackEntryTableUnsorted,
                $"Entries {i - 1} ('{Describe(contents, i - 1)}') and {i} ('{Describe(contents, i)}') {what}.",
                contents.Path));
        }
    }

    private static string Describe(PackContents contents, int index)
    {
        string name = contents.NameOf(index);
        return name.Length > 0 ? name : "unnamed";
    }

    private static PackVerifyResult Finish(
        string file,
        CookDiagnosticLog diagnostics,
        int entriesChecked,
        int tombstones,
        int references,
        long payloadBytes)
    {
        return new PackVerifyResult
        {
            PackPath = file,
            Diagnostics = diagnostics.Entries,
            EntriesChecked = entriesChecked,
            TombstonesSkipped = tombstones,
            ReferencesChecked = references,
            PayloadBytes = payloadBytes,
            ErrorCount = diagnostics.ErrorCount,
            WarningCount = diagnostics.WarningCount,
        };
    }
}
