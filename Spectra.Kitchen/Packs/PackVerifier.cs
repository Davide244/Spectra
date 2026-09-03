using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spectra.Kitchen.Diagnostics;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Assets.Sources;
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
    /// <exception cref="IOException">The file could not be opened.</exception>
    public static PackVerifyResult Verify(string packPath, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(packPath);

        string file = Path.GetFullPath(packPath);
        var diagnostics = new List<CookDiagnostic>();

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

        using (pack)
        {
            // The pack ALONE. See the class remarks: a stack that also carried the
            // loose tree would resolve a missing cooked texture out of the source
            // folder and report a pass for a pack that ships a hole.
            var strict = new ContentSourceStack(strict: true);
            strict.Mount(pack);

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

                    references += CheckReferences(name, blob.Span, strict, diagnostics, file);
                }
            }
        }

        return Finish(file, diagnostics, payloads, tombstones, references, payloadBytes);
    }

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
    /// <item><description><c>.simage</c> - nothing to resolve; an image is a
    /// leaf. It joins this switch only if a cooked image ever names a companion
    /// (a streamed mip tail), and would report in the 2xxx band.</description></item>
    /// <item><description><c>.smodel</c> - its material references, in the 3xxx
    /// band. The authored <c>.obj</c>/<c>.mtl</c> pair already expresses one
    /// today and is deliberately not checked here: it is raw-copied rather than
    /// cooked, so the reference it carries is resolved by the importer at load
    /// time and not by anything in the pack.</description></item>
    /// <item><description><c>.scmap</c> - every material, model and script a
    /// level names, in the 7xxx band. The largest arm, and the one that turns
    /// this from a spot check into a whole-game one.</description></item>
    /// <item><description>a shader blob per requested backend, in the 6xxx band.
    /// Not here yet because there is no shader cook rule, so every material in
    /// every project names the built-in <c>lit</c> and there is nothing in a
    /// pack for a shader reference to point at.</description></item>
    /// </list>
    /// </remarks>
    private static int CheckReferences(
        string name,
        ReadOnlySpan<byte> payload,
        ContentSourceStack strict,
        List<CookDiagnostic> diagnostics,
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
                if (strict.TryOpen(slot.TexturePath, out ContentBlob? texture))
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

            diagnostics.Add(CookDiagnostic.Error(
                CookDiagnosticCodes.MaterialTextureMissing,
                $"'{name}' binds sampler '{slot.Name}' to '{slot.TexturePath}', which is not in this pack. " +
                "The running engine would show the magenta placeholder and carry on; a shipped build would " +
                "ship that.",
                packFile));
        }

        return resolved;
    }

    // The claim the writer cannot make on the file's behalf. Strictly ascending
    // covers both properties at once: an equal pair is a collision, whose harm is
    // that a binary search's answer depends on where it happened to land, and a
    // descending pair is an unsorted table, whose harm is that the search misses
    // entries entirely. Both present as content that is intermittently absent.
    private static void CheckEntryOrder(PackContents contents, List<CookDiagnostic> diagnostics)
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
        List<CookDiagnostic> diagnostics,
        int entriesChecked,
        int tombstones,
        int references,
        long payloadBytes)
    {
        int errors = 0, warnings = 0;
        for (int i = 0; i < diagnostics.Count; i++)
        {
            if (diagnostics[i].IsError) errors++;
            else if (diagnostics[i].Severity == CookDiagnosticSeverity.Warning) warnings++;
        }

        return new PackVerifyResult
        {
            PackPath = file,
            Diagnostics = diagnostics,
            EntriesChecked = entriesChecked,
            TombstonesSkipped = tombstones,
            ReferencesChecked = references,
            PayloadBytes = payloadBytes,
            ErrorCount = errors,
            WarningCount = warnings,
        };
    }
}
