using SpectraEngine.Core.Assets.Packs;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// Raw file bytes are cast into <see cref="PackHeader"/> and
/// <see cref="PackEntry"/>, so their size and field order ARE the format. A field
/// reordered or retyped by an edit compiles cleanly and produces a file that
/// parses into the wrong numbers with nothing reporting it, which is what these
/// pins exist to catch.
/// </summary>
public class PackFormatLayoutTests
{
    [Fact]
    public void The_header_is_sixty_four_bytes()
    {
        Unsafe.SizeOf<PackHeader>().ShouldBe(64);
        Unsafe.SizeOf<PackHeader>().ShouldBe(PackFormat.HeaderSize);
    }

    [Fact]
    public void The_entry_is_forty_eight_bytes()
    {
        Unsafe.SizeOf<PackEntry>().ShouldBe(48);
        Unsafe.SizeOf<PackEntry>().ShouldBe(PackFormat.EntrySize);
    }

    [Fact]
    public void The_entry_stride_keeps_every_asset_id_sixteen_byte_aligned()
    {
        // The table starts at the header's end and every entry follows the last,
        // so an id is 16-byte aligned only if both numbers are multiples of 16.
        // That is what makes reinterpreting the table in place legal.
        (PackFormat.HeaderSize % PackFormat.PayloadAlignment).ShouldBe(0);
        (PackFormat.EntrySize % PackFormat.PayloadAlignment).ShouldBe(0);
    }

    [Fact]
    public void Header_fields_sit_at_the_documented_offsets()
    {
        var header = new PackHeader(
            magic: 0x03020100u,
            formatVersion: 0x0504,
            minReaderVersion: 0x0706,
            flags: (PackFlags)0x0B0A0908u,
            entryCount: 0x0F0E0D0Cu,
            entryTableOffset: 0x1716151413121110ul,
            nameTableOffset: 0x1F1E1D1C1B1A1918ul,
            nameTableLength: 0x2726252423222120ul,
            packSequence: 0x2B2A2928u,
            engineVersion: 0x2F2E2D2Cu,
            dataSectionOffset: 0x3736353433323130ul,
            totalFileSize: 0x3F3E3D3C3B3A3938ul);

        Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<PackHeader>()];
        MemoryMarshal.Write(bytes, in header);

        // Each field was given the little-endian value of its own byte offsets, so
        // a correct layout produces the identity sequence 00 01 02 ... 3F. Any
        // reorder, retype or inserted pad breaks the run at the field that moved.
        for (int i = 0; i < bytes.Length; i++)
            bytes[i].ShouldBe((byte)i, $"byte {i} of the header");
    }

    [Fact]
    public void Entry_fields_sit_at_the_documented_offsets()
    {
        var entry = new PackEntry(
            assetId: new UInt128(0x0F0E0D0C0B0A0908ul, 0x0706050403020100ul),
            payloadOffset: 0x1716151413121110ul,
            storedSize: 0x1F1E1D1C1B1A1918ul,
            uncompressedSize: 0x2726252423222120ul,
            nameOffset: 0x2B2A2928u,
            nameLength: 0x2D2C,
            kind: (PackEntryKind)0x2E,
            codec: (PackCodec)0x2F);

        Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<PackEntry>()];
        MemoryMarshal.Write(bytes, in entry);

        for (int i = 0; i < bytes.Length; i++)
            bytes[i].ShouldBe((byte)i, $"byte {i} of the entry");
    }

    [Fact]
    public void An_entry_table_casts_back_out_of_raw_bytes_in_place()
    {
        var first = new PackEntry(1, 4096, 10, 10, 0, 5, PackEntryKind.Image, PackCodec.None);
        var second = new PackEntry(UInt128.MaxValue, 4112, 3, 7, 7, 4, PackEntryKind.Model, PackCodec.Deflate);

        Span<byte> table = stackalloc byte[PackFormat.EntrySize * 2];
        MemoryMarshal.Write(table, in first);
        MemoryMarshal.Write(table[PackFormat.EntrySize..], in second);

        ReadOnlySpan<PackEntry> entries = MemoryMarshal.Cast<byte, PackEntry>(table);

        entries.Length.ShouldBe(2);
        entries[0].AssetId.ShouldBe(first.AssetId);
        entries[1].AssetId.ShouldBe(UInt128.MaxValue);
        entries[1].PayloadOffset.ShouldBe(4112ul);
        entries[1].EntryKind.ShouldBe(PackEntryKind.Model);
        entries[1].EntryCodec.ShouldBe(PackCodec.Deflate);
    }

    [Fact]
    public void The_magic_reads_SPAK_in_a_hex_dump()
    {
        Span<byte> bytes = stackalloc byte[4];
        MemoryMarshal.Write(bytes, in Unsafe.AsRef(in MagicValue));

        bytes[0].ShouldBe((byte)'S');
        bytes[1].ShouldBe((byte)'P');
        bytes[2].ShouldBe((byte)'A');
        bytes[3].ShouldBe((byte)'K');
    }

    [Fact]
    public void The_tombstone_kind_is_the_top_of_the_byte()
    {
        // Every other kind is append-only from zero, so a new one can never
        // collide with the deletion marker.
        ((byte)PackEntryKind.Tombstone).ShouldBe((byte)0xFF);
    }

    [Fact]
    public void Align_up_rounds_to_the_next_boundary_and_leaves_an_aligned_value_alone()
    {
        PackFormat.AlignUp(0, 16).ShouldBe(0);
        PackFormat.AlignUp(1, 16).ShouldBe(16);
        PackFormat.AlignUp(16, 16).ShouldBe(16);
        PackFormat.AlignUp(17, 16).ShouldBe(32);
        PackFormat.AlignUp(PackFormat.HeaderSize, PackFormat.DataSectionAlignment).ShouldBe(4096);
        PackFormat.AlignUp(4096, PackFormat.DataSectionAlignment).ShouldBe(4096);
        PackFormat.AlignUp(4097, PackFormat.DataSectionAlignment).ShouldBe(8192);
    }

    [Fact]
    public void Align_up_refuses_an_alignment_that_is_not_a_power_of_two()
    {
        Should.Throw<ArgumentException>(() => PackFormat.AlignUp(10, 24));
    }

    private static readonly uint MagicValue = PackFormat.Magic;
}
