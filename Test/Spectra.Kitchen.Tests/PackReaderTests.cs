using Microsoft.Extensions.Logging.Abstractions;
using Spectra.Kitchen.Packs;
using SpectraEngine.Core;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Assets.Sources;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// The reader half of the container: what a mount refuses, what a lookup answers,
/// and the lifetime rule that stops a span into a mapped view outliving the view.
/// </summary>
/// <remarks>
/// The packs here are written by <see cref="PackWriter"/> rather than hand-built,
/// which is the right direction for these tests and the wrong one for
/// <see cref="PackWriterTests"/>: the writer is already pinned against a
/// hand-written parse of the spec, so a reader checked against it is checked
/// against the spec transitively. The corruption cases edit those bytes
/// afterwards, where the second opinion is the digest rather than a second parser.
/// </remarks>
public class PackReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"spectra_packs_{Guid.NewGuid():N}");

    private readonly List<IDisposable> _open = [];

    public PackReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // Every source first: a mapped view keeps its file open, so a directory
        // holding one cannot be deleted on Windows.
        for (int i = _open.Count - 1; i >= 0; i--) _open[i].Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leaked mapping would land here, and failing the test on the
            // cleanup rather than on the assertion helps nobody.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Two_identical_mount_lists_produce_byte_identical_resolution()
    {
        // The determinism oracle, in the same discipline the CSG ones follow: two
        // installs assembled the same way must not merely serve the same bytes,
        // they must agree about WHICH pack served them, or a patch that appears
        // not to apply is indistinguishable from one that did.
        WriteBasePack();
        WritePatchPack();
        WriteModPack();

        PackMountStack first = BuildStack();
        PackMountStack second = BuildStack();

        var firstPaths = new List<string>();
        var secondPaths = new List<string>();
        first.TryEnumerate(string.Empty, string.Empty, firstPaths);
        second.TryEnumerate(string.Empty, string.Empty, secondPaths);

        firstPaths.ShouldBe(secondPaths);
        firstPaths.ShouldNotBeEmpty();

        foreach (string path in firstPaths)
        {
            first.TryOpen(path, out ContentBlob? a).ShouldBeTrue(path);
            second.TryOpen(path, out ContentBlob? b).ShouldBeTrue(path);

            using (a)
            using (b)
            {
                a!.Span.ToArray().ShouldBe(b!.Span.ToArray(), path);
            }
        }

        first.Shadowings.ShouldBe(second.Shadowings);
        first.Shadowings.ShouldNotBeEmpty("the fixture overlaps on purpose, so a stack with no decisions in it is not testing this");
    }

    [Fact]
    public void A_blob_stays_valid_while_its_handle_is_held_and_the_unmap_waits_for_the_last_reference()
    {
        // Deterministic rather than timing based: the unmount is REQUESTED, and
        // the two observations either side of the release are what prove it was
        // deferred rather than merely slow.
        byte[] payload = Bytes(64, seed: 5);
        string path = WritePack("hold.spack", writer => writer.Add("Textures/held.png", PackEntryKind.Image, payload));

        var source = new PackSource(NullLogger.Instance, path);
        PackHandle handle = source.Handle;

        handle.ReferenceCount.ShouldBe(1, "the mount holds one reference of its own");

        source.TryOpen("Textures/held.png", out ContentBlob? blob).ShouldBeTrue();
        handle.ReferenceCount.ShouldBe(2, "the blob took one, and it travels with the blob");

        source.Dispose();

        handle.UnmountRequested.ShouldBeTrue();
        handle.IsReleased.ShouldBeFalse("a blob still holds a reference");
        handle.IsMapped.ShouldBeTrue("unmapping under a live span is an access violation, not an exception");
        blob!.Span.ToArray().ShouldBe(payload, "the bytes are still the file's own");

        handle.TryAddRef().ShouldBeFalse("an unmounting pack may not hand out new references, or the unmount never finishes");

        blob.Dispose();

        handle.IsReleased.ShouldBeTrue();
        handle.IsMapped.ShouldBeFalse();
        Should.Throw<ObjectDisposedException>(() => blob.Span.Length);
    }

    [Fact]
    public void An_uncompressed_entry_is_a_window_into_the_view_rather_than_a_copy()
    {
        byte[] payload = Bytes(512, seed: 9);
        string path = WritePack("window.spack", writer => writer.Add("Models/crate.smodel", PackEntryKind.Model, payload));

        var mapped = Track(new PackSource(NullLogger.Instance, path));

        mapped.TryOpen("Models/crate.smodel", out ContentBlob? first).ShouldBeTrue();
        mapped.TryOpen("Models/crate.smodel", out ContentBlob? second).ShouldBeTrue();
        using (first)
        using (second)
        {
            first!.Length.ShouldBe(payload.Length);
            first.Span.ToArray().ShouldBe(payload);

            // Two opens of one entry land on the same ADDRESS, which no copying
            // reader can do: two pooled rents are two different buffers. That is
            // the zero-copy claim stated as something a test can see.
            SameAddress(first.Span, second!.Span).ShouldBeTrue();
        }

        // And the contrast, so the assertion above is not vacuously true of any
        // reader: the fallback copies, so its two blobs are two buffers.
        var streamed = Track(new StreamPackSource(NullLogger.Instance, path));
        streamed.TryOpen("Models/crate.smodel", out ContentBlob? copyA).ShouldBeTrue();
        streamed.TryOpen("Models/crate.smodel", out ContentBlob? copyB).ShouldBeTrue();
        using (copyA)
        using (copyB)
        {
            copyA!.Span.ToArray().ShouldBe(payload);
            SameAddress(copyA.Span, copyB!.Span).ShouldBeFalse();
        }
    }

    [Fact]
    public void A_deflate_entry_inflates_into_a_buffer_the_blob_returns_on_dispose()
    {
        byte[] payload = new byte[4096];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 7);

        string path = WritePack(
            "deflate.spack",
            writer => writer.Add("Maps/lobby.scmap", PackEntryKind.Map, payload, PackCodec.Deflate));

        var source = Track(new PackSource(NullLogger.Instance, path));

        source.TryOpen("Maps/lobby.scmap", out ContentBlob? blob).ShouldBeTrue();
        using (blob)
        {
            blob!.Span.ToArray().ShouldBe(payload);
            source.Handle.ReferenceCount.ShouldBe(2, "a decompressed blob holds a reference too, so the rule has no exception");
        }

        source.Handle.ReferenceCount.ShouldBe(1);
    }

    [Fact]
    public void A_path_the_pack_does_not_hold_is_a_miss_rather_than_a_throw()
    {
        string path = WritePack("small.spack", writer => writer.Add("Textures/a.png", PackEntryKind.Image, Bytes(8)));
        var source = Track(new PackSource(NullLogger.Instance, path));

        source.Exists("Textures/absent.png").ShouldBeFalse();
        source.TryOpen("Textures/absent.png", out ContentBlob? nothing).ShouldBeFalse();
        nothing.ShouldBeNull();

        // A path no caller could ever have meant is a miss too, not an argument
        // exception thrown into the middle of a frame.
        source.TryOpen("../outside.png", out ContentBlob? escaped).ShouldBeFalse();
        escaped.ShouldBeNull();
    }

    [Fact]
    public void Spellings_that_differ_only_in_case_or_separator_resolve_to_one_entry()
    {
        byte[] payload = Bytes(24, seed: 3);
        string path = WritePack("case.spack", writer => writer.Add("Textures/Wall_Brick.png", PackEntryKind.Image, payload));
        var source = Track(new PackSource(NullLogger.Instance, path));

        source.TryOpen("textures/wall_brick.png", out ContentBlob? folded).ShouldBeTrue();
        using (folded) folded!.Span.ToArray().ShouldBe(payload);

        source.TryOpen("Textures\\Wall_Brick.png", out ContentBlob? separated).ShouldBeTrue();
        using (separated) separated!.Span.ToArray().ShouldBe(payload);
    }

    [Fact]
    public void A_truncated_pack_is_refused_at_mount()
    {
        string path = WritePack("truncated.spack", writer => writer.Add("Textures/a.png", PackEntryKind.Image, Bytes(64)));

        byte[] whole = File.ReadAllBytes(path);
        File.WriteAllBytes(path, whole[..^10]);

        var mapped = Should.Throw<PackMountException>(() => new PackSource(NullLogger.Instance, path));
        mapped.Message.ShouldContain("truncated");
        mapped.Message.ShouldContain(whole.Length.ToString());

        // The header carries the size so truncation is caught from the file's own
        // bytes, which is why the fallback catches it identically with no stat.
        Should.Throw<PackMountException>(() => new StreamPackSource(NullLogger.Instance, path))
            .Message.ShouldContain("truncated");
    }

    [Fact]
    public void A_file_too_short_to_hold_a_header_is_refused_at_mount()
    {
        string path = Path.Combine(_root, "stub.spack");
        File.WriteAllBytes(path, new byte[32]);

        Should.Throw<PackMountException>(() => new PackSource(NullLogger.Instance, path))
            .Message.ShouldContain("too short");
        Should.Throw<PackMountException>(() => new StreamPackSource(NullLogger.Instance, path))
            .Message.ShouldContain("too short");
    }

    [Fact]
    public void A_pack_with_one_flipped_payload_byte_is_refused_at_mount()
    {
        string path = WritePack("flipped.spack", writer => writer.Add("Textures/a.png", PackEntryKind.Image, Bytes(64, seed: 2)));

        byte[] whole = File.ReadAllBytes(path);
        int at = (int)HandParsedPack.ReadHeader(whole).DataSectionOffset;
        whole[at] ^= 0xFF;
        File.WriteAllBytes(path, whole);

        // Caught at mount, not at the read that happens to want that entry: a pack
        // whose bytes cannot be trusted must not become a source at all.
        Should.Throw<PackMountException>(() => new PackSource(NullLogger.Instance, path))
            .Message.ShouldContain("digest");
        Should.Throw<PackMountException>(() => new StreamPackSource(NullLogger.Instance, path))
            .Message.ShouldContain("digest");
    }

    [Fact]
    public void A_pack_with_the_wrong_magic_is_refused_at_mount()
    {
        string path = WritePack("magic.spack", writer => writer.Add("Textures/a.png", PackEntryKind.Image, Bytes(16)));

        byte[] whole = File.ReadAllBytes(path);
        whole[2] = (byte)'C';
        File.WriteAllBytes(path, whole);

        Should.Throw<PackMountException>(() => new PackSource(NullLogger.Instance, path))
            .Message.ShouldContain("not a .spack file");
    }

    [Fact]
    public void A_pack_demanding_a_newer_reader_is_refused_naming_both_numbers()
    {
        string path = WritePack("future.spack", writer => writer.Add("Textures/a.png", PackEntryKind.Image, Bytes(16)));

        byte[] whole = File.ReadAllBytes(path);
        const ushort Demanded = 99;
        whole[0x04] = (byte)Demanded;           // FormatVersion, so the pack stays self-consistent
        whole[0x05] = 0;
        whole[0x06] = (byte)Demanded;           // MinReaderVersion
        whole[0x07] = 0;
        File.WriteAllBytes(path, whole);

        var thrown = Should.Throw<PackMountException>(() => new PackSource(NullLogger.Instance, path));

        thrown.Message.ShouldContain(Demanded.ToString());
        thrown.Message.ShouldContain(EngineInfo.PackFormatVersion.ToString());
    }

    [Fact]
    public void A_pack_that_does_not_claim_a_sorted_table_is_refused_at_mount()
    {
        string path = WritePack("unsorted.spack", writer => writer.Add("Textures/a.png", PackEntryKind.Image, Bytes(16)));

        byte[] whole = File.ReadAllBytes(path);
        whole[0x08] &= unchecked((byte)~(uint)PackFlags.EntriesSortedByAssetId);
        File.WriteAllBytes(path, whole);

        // Refused rather than searched: a binary search over an unsorted table
        // misses entries silently, which presents as content that is
        // intermittently absent rather than as a corrupt file.
        Should.Throw<PackMountException>(() => new PackSource(NullLogger.Instance, path))
            .Message.ShouldContain(nameof(PackFlags.EntriesSortedByAssetId));
    }

    [Fact]
    public void A_tombstone_hides_the_asset_beneath_it()
    {
        WriteBasePack();
        WritePack(
            "mod.spack",
            writer =>
            {
                writer.AddTombstone("Textures/wall_brick.png");
                writer.Add("Textures/mod_only.png", PackEntryKind.Image, Bytes(12, seed: 40));
            },
            band: PackFlags.IsModPack);

        var stack = Track(new PackMountStack(NullLogger.Instance));
        stack.Mount(Track(new PackSource(NullLogger.Instance, Path.Combine(_root, "base.spack"), PackMountBand.Base)));
        stack.Mount(Track(new PackSource(NullLogger.Instance, Path.Combine(_root, "mod.spack"), PackMountBand.Mod)));

        stack.Exists("Textures/wall_brick.png").ShouldBeFalse("the mod's tombstone deletes it");
        stack.TryOpen("Textures/wall_brick.png", out ContentBlob? hidden).ShouldBeFalse();
        hidden.ShouldBeNull();

        // The base pack still has it: a tombstone is a decision the stack makes,
        // not an edit to the pack underneath.
        var beneath = Track(new PackSource(NullLogger.Instance, Path.Combine(_root, "base.spack")));
        beneath.Exists("Textures/wall_brick.png").ShouldBeTrue();

        // Nothing else is touched, and the decision is on the record.
        stack.Exists("Textures/floor_tile.png").ShouldBeTrue();
        stack.Exists("Textures/mod_only.png").ShouldBeTrue();

        var paths = new List<string>();
        stack.TryEnumerate(string.Empty, string.Empty, paths);
        paths.ShouldNotContain("Textures/wall_brick.png");

        stack.Shadowings.ShouldContain(s => s.Path == "Textures/wall_brick.png" && s.HiddenByTombstone);
    }

    [Fact]
    public void A_higher_band_shadows_a_lower_one_and_the_decision_is_recorded()
    {
        WriteBasePack();
        WritePatchPack();

        var stack = Track(new PackMountStack(NullLogger.Instance));
        stack.Mount(Track(new PackSource(NullLogger.Instance, Path.Combine(_root, "base.spack"), PackMountBand.Base)));
        stack.Mount(Track(new PackSource(NullLogger.Instance, Path.Combine(_root, "patch.spack"), PackMountBand.Patch)));

        stack.TryOpen("Textures/wall_brick.png", out ContentBlob? blob).ShouldBeTrue();
        using (blob) blob!.Span.ToArray().ShouldBe(PatchedWallBytes, "the patch band wins");

        MountShadowing decision = stack.Shadowings.ShouldHaveSingleItem();
        decision.Path.ShouldBe("Textures/wall_brick.png");
        decision.WinnerPriority.ShouldBe(PackMountBand.Patch);
        decision.ShadowedPriority.ShouldBe(PackMountBand.Base);
        decision.HiddenByTombstone.ShouldBeFalse();
    }

    [Fact]
    public void Mount_order_breaks_a_tie_within_one_band()
    {
        WriteBasePack();
        WritePack("second.spack", writer => writer.Add("Textures/wall_brick.png", PackEntryKind.Image, Bytes(7, seed: 77)));

        var stack = Track(new PackMountStack(NullLogger.Instance));
        stack.Mount(Track(new PackSource(NullLogger.Instance, Path.Combine(_root, "base.spack"), PackMountBand.Base)));
        stack.Mount(Track(new PackSource(NullLogger.Instance, Path.Combine(_root, "second.spack"), PackMountBand.Base)));

        stack.TryOpen("Textures/wall_brick.png", out ContentBlob? blob).ShouldBeTrue();
        using (blob) blob!.Span.ToArray().ShouldBe(Bytes(7, seed: 77), "the later mount wins the tie");
    }

    [Fact]
    public void The_stream_fallback_answers_identically_to_the_mapped_source()
    {
        byte[] compressible = new byte[2048];
        for (int i = 0; i < compressible.Length; i++) compressible[i] = (byte)(i % 5);

        string path = WritePack(
            "both.spack",
            writer =>
            {
                writer.Add("Textures/wall_brick.png", PackEntryKind.Image, Bytes(300, seed: 1));
                writer.Add("Models/crate.smodel", PackEntryKind.Model, Bytes(1, seed: 2));
                writer.Add("Maps/lobby.scmap", PackEntryKind.Map, compressible, PackCodec.Deflate);
                writer.Add("Materials/brick.smaterial", PackEntryKind.Material, Bytes(0, seed: 3));
                writer.AddTombstone("Textures/gone.png");
            });

        var mapped = Track(new PackSource(NullLogger.Instance, path));
        var streamed = Track(new StreamPackSource(NullLogger.Instance, path));

        streamed.EntryCount.ShouldBe(mapped.EntryCount);
        streamed.TombstoneCount.ShouldBe(mapped.TombstoneCount);
        streamed.Header.TotalFileSize.ShouldBe(mapped.Header.TotalFileSize);

        var mappedPaths = new List<string>();
        var streamedPaths = new List<string>();
        mapped.TryEnumerate(string.Empty, string.Empty, mappedPaths);
        streamed.TryEnumerate(string.Empty, string.Empty, streamedPaths);

        streamedPaths.ShouldBe(mappedPaths);
        mappedPaths.Count.ShouldBe(4, "the tombstone is a deletion, so it is not something either source serves");

        foreach (string entry in mappedPaths)
        {
            mapped.TryOpen(entry, out ContentBlob? a).ShouldBeTrue(entry);
            streamed.TryOpen(entry, out ContentBlob? b).ShouldBeTrue(entry);

            using (a)
            using (b)
            {
                b!.Span.ToArray().ShouldBe(a!.Span.ToArray(), entry);
            }
        }

        streamed.IsTombstone("Textures/gone.png").ShouldBe(mapped.IsTombstone("Textures/gone.png"));
        streamed.Exists("Textures/gone.png").ShouldBeFalse();
        streamed.Exists("Textures/absent.png").ShouldBe(mapped.Exists("Textures/absent.png"));
    }

    [Fact]
    public void The_stream_fallback_defers_its_unmount_the_same_way()
    {
        byte[] payload = Bytes(48, seed: 11);
        string path = WritePack("streamhold.spack", writer => writer.Add("Textures/a.png", PackEntryKind.Image, payload));

        var source = new StreamPackSource(NullLogger.Instance, path);
        PackHandle handle = source.Handle;

        source.TryOpen("Textures/a.png", out ContentBlob? blob).ShouldBeTrue();
        handle.ReferenceCount.ShouldBe(2, "one rule, not one rule per source");

        source.Dispose();
        handle.IsReleased.ShouldBeFalse();
        blob!.Span.ToArray().ShouldBe(payload);

        blob.Dispose();
        handle.IsReleased.ShouldBeTrue();
    }

    [Fact]
    public void A_pack_with_no_name_table_still_resolves_by_id_and_enumerates_nothing()
    {
        byte[] payload = Bytes(16, seed: 6);
        string path = WritePack(
            "nameless.spack",
            writer => writer.Add("Textures/a.png", PackEntryKind.Image, payload),
            includeNameTable: false);

        var source = Track(new PackSource(NullLogger.Instance, path));

        source.TryOpen("Textures/a.png", out ContentBlob? blob).ShouldBeTrue("identity is the hashed path, not the name table");
        using (blob) blob!.Span.ToArray().ShouldBe(payload);

        var paths = new List<string>();
        source.TryEnumerate(string.Empty, string.Empty, paths);
        paths.ShouldBeEmpty("there is no name to report, which is the cost of dropping the table");
    }

    [Fact]
    public void An_empty_pack_mounts_and_answers_every_lookup_with_a_miss()
    {
        string path = WritePack("empty.spack", static _ => { });
        var source = Track(new PackSource(NullLogger.Instance, path));

        source.EntryCount.ShouldBe(0);
        source.Exists("Textures/a.png").ShouldBeFalse();
    }

    [Fact]
    public void Enumeration_filters_by_prefix_and_extension()
    {
        string path = WritePack(
            "filtered.spack",
            writer =>
            {
                writer.Add("Textures/a.png", PackEntryKind.Image, Bytes(4, seed: 1));
                writer.Add("Textures/nested/b.png", PackEntryKind.Image, Bytes(4, seed: 2));
                writer.Add("Textures/c.ktx2", PackEntryKind.Image, Bytes(4, seed: 3));
                writer.Add("Models/d.png", PackEntryKind.Model, Bytes(4, seed: 4));
            });

        var source = Track(new PackSource(NullLogger.Instance, path));

        var textures = new List<string>();
        source.TryEnumerate("Textures", ".png", textures);
        textures.Sort(StringComparer.Ordinal);
        textures.ShouldBe(["Textures/a.png", "Textures/nested/b.png"]);
    }

    private const int WallSeed = 21;
    private static byte[] PatchedWallBytes => Bytes(96, seed: 31);

    private PackMountStack BuildStack()
    {
        var stack = Track(new PackMountStack(NullLogger.Instance));
        stack.Mount(Track(new PackSource(NullLogger.Instance, Path.Combine(_root, "base.spack"), PackMountBand.Base)));
        stack.Mount(Track(new PackSource(NullLogger.Instance, Path.Combine(_root, "patch.spack"), PackMountBand.Patch)));
        stack.Mount(Track(new PackSource(NullLogger.Instance, Path.Combine(_root, "mod.spack"), PackMountBand.Mod)));
        return stack;
    }

    private void WriteBasePack() => WritePack(
        "base.spack",
        writer =>
        {
            writer.Add("Textures/wall_brick.png", PackEntryKind.Image, Bytes(64, seed: WallSeed));
            writer.Add("Textures/floor_tile.png", PackEntryKind.Image, Bytes(48, seed: 22));
            writer.Add("Models/crate.smodel", PackEntryKind.Model, Bytes(200, seed: 23));
            writer.Add("Materials/brick.smaterial", PackEntryKind.Material, Bytes(12, seed: 24));
        });

    private void WritePatchPack() => WritePack(
        "patch.spack",
        writer => writer.Add("Textures/wall_brick.png", PackEntryKind.Image, PatchedWallBytes),
        band: PackFlags.IsPatchPack,
        sequence: 1);

    private void WriteModPack() => WritePack(
        "mod.spack",
        writer =>
        {
            writer.Add("Models/crate.smodel", PackEntryKind.Model, Bytes(120, seed: 41));
            writer.Add("Textures/mod_only.png", PackEntryKind.Image, Bytes(30, seed: 42));
            writer.AddTombstone("Materials/brick.smaterial");
        },
        band: PackFlags.IsModPack);

    private string WritePack(
        string fileName,
        Action<PackWriter> build,
        PackFlags band = PackFlags.None,
        uint sequence = 0,
        bool includeNameTable = true)
    {
        var writer = new PackWriter(sequence, includeNameTable, band);
        build(writer);

        string path = Path.Combine(_root, fileName);
        writer.WriteToFile(path);
        return path;
    }

    private static bool SameAddress(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second) =>
        Unsafe.AreSame(ref MemoryMarshal.GetReference(first), ref MemoryMarshal.GetReference(second));

    private T Track<T>(T disposable) where T : IDisposable
    {
        _open.Add(disposable);
        return disposable;
    }

    // Deterministic content, so a byte comparison measures the reader rather than
    // the payloads.
    private static byte[] Bytes(int length, int seed = 0)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)((i * 31) + seed + 1);
        return bytes;
    }
}
