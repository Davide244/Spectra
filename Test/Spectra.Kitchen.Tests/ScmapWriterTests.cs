using System;
using System.IO;
using System.Runtime.InteropServices;
using Spectra.Kitchen.Maps;
using SpectraEngine.Core;
using SpectraEngine.Core.Maps.Compiled;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// The container half of a compiled map: the header, the section table, the
/// padding between sections, and the assertion that a section landed where the
/// layout put it.
/// </summary>
/// <remarks>
/// <b>The offset assertion is the reason this file exists.</b> The named hazard of
/// this format is a section landing at a non-16-aligned offset because the layout
/// pass and the write pass disagreed about a size including its padding, and its
/// symptom is arbitrary: the section table is then full of plausible offsets, so
/// the file parses and a chunk mesh comes out of the middle of the string blob. A
/// disagreement has to fail at the section that disagreed, by name, at cook time.
/// </remarks>
public class ScmapWriterTests
{
    [Fact]
    public void A_section_that_writes_fewer_bytes_than_it_declared_is_refused_by_name()
    {
        var writer = new ScmapWriter(ScmapFlags.None, 0, EngineInfo.MapFormatVersion);
        writer.AddSection(ScmapFormat.StringSection, new byte[8]);
        writer.AddSection(ScmapFormat.MetaSection, bodySize: 48, stream => stream.Write(new byte[32]));
        writer.AddSection(ScmapFormat.NodeSection, new byte[16]);

        using var buffer = new MemoryStream();
        InvalidOperationException failure = Should.Throw<InvalidOperationException>(() => writer.Write(buffer));

        failure.Message.ShouldContain("META");
        failure.Message.ShouldContain("48");
        failure.Message.ShouldContain("32");
    }

    [Fact]
    public void A_section_that_writes_more_bytes_than_it_declared_is_refused_by_name()
    {
        // The mirror case, and the more dangerous one: by the time anything
        // notices, the overrun has already been written over the bytes the next
        // section was going to occupy.
        var writer = new ScmapWriter(ScmapFlags.None, 0, EngineInfo.MapFormatVersion);
        writer.AddSection(ScmapFormat.StringSection, bodySize: 16, stream => stream.Write(new byte[17]));
        writer.AddSection(ScmapFormat.NodeSection, new byte[16]);

        using var buffer = new MemoryStream();
        InvalidOperationException failure = Should.Throw<InvalidOperationException>(() => writer.Write(buffer));

        failure.Message.ShouldContain("STRT");
    }

    [Fact]
    public void A_layout_and_a_write_that_agree_produce_sections_at_the_declared_offsets()
    {
        var writer = new ScmapWriter(ScmapFlags.None, 0, EngineInfo.MapFormatVersion);
        writer.AddSection(ScmapFormat.StringSection, new byte[7]);
        writer.AddSection(ScmapFormat.AssetSection, []);
        writer.AddSection(ScmapFormat.MetaSection, new byte[48]);
        writer.AddSection(ScmapFormat.NodeSection, new byte[96]);
        writer.AddSection(ScmapFormat.ChunkDirectorySection, new byte[1]);

        using var buffer = new MemoryStream();
        writer.Write(buffer);
        byte[] file = buffer.ToArray();

        ScmapHeader header = MemoryMarshal.Read<ScmapHeader>(file);
        header.SectionCount.ShouldBe(5u);
        header.TotalSize.ShouldBe((ulong)file.Length);
        header.HeaderSize.ShouldBe((ushort)ScmapFormat.HeaderSize);
        header.GeometryFormatVersion.ShouldBe(EngineInfo.GeometryFormatVersion);
        header.VertexLayoutId.ShouldBe(ScmapFormat.StandardVertexLayoutId);

        long cursor = ScmapFormat.SectionTableOffset + (5 * ScmapFormat.SectionSize);
        for (int i = 0; i < 5; i++)
        {
            ScmapSection section = MemoryMarshal.Read<ScmapSection>(
                file.AsSpan(ScmapFormat.SectionTableOffset + (i * ScmapFormat.SectionSize)));

            ((long)section.Offset % ScmapFormat.PayloadAlignment).ShouldBe(0);
            ((long)section.Offset).ShouldBe(cursor);
            section.UncompressedSize.ShouldBe(section.Size);

            cursor += ScmapLayout.PaddedSectionSize((long)section.Size);
        }

        cursor.ShouldBe(file.Length);
    }

    [Fact]
    public void The_padding_between_sections_is_written_rather_than_seeked_over()
    {
        // Against a destination pre-filled with a byte that is not zero, so a gap
        // the writer skipped rather than wrote is visible. A seek past the end
        // leaves the gap holding whatever the filesystem gives back, which on most
        // filesystems is zeros and on none of them is a promise, and byte identity
        // between two cooks is what that would quietly break.
        var backing = new byte[512];
        backing.AsSpan().Fill(0xCD);

        using var buffer = new MemoryStream(backing, 0, backing.Length, writable: true, publiclyVisible: true);

        var writer = new ScmapWriter(ScmapFlags.None, 0, EngineInfo.MapFormatVersion);
        writer.AddSection(ScmapFormat.StringSection, new byte[] { 0xAB });
        writer.Write(buffer);

        int expected = ScmapFormat.HeaderSize + ScmapFormat.SectionSize + ScmapFormat.PayloadAlignment;
        buffer.Position.ShouldBe(expected);

        // The one declared byte, then fifteen bytes of padding the writer put down.
        backing[expected - 16].ShouldBe((byte)0xAB);
        for (int i = expected - 15; i < expected; i++) backing[i].ShouldBe((byte)0, $"padding byte {i}");

        // And nothing past the file's end was touched.
        backing[expected].ShouldBe((byte)0xCD);
    }

    [Fact]
    public void Two_writes_of_one_writer_produce_the_same_bytes()
    {
        var writer = new ScmapWriter(ScmapFlags.HasDebugInfo, new UInt128(7, 9), EngineInfo.MapFormatVersion);
        writer.AddSection(ScmapFormat.StringSection, new byte[] { 1, 2, 3 });
        writer.AddSection(ScmapFormat.NodeSection, new byte[80]);

        using var first = new MemoryStream();
        using var second = new MemoryStream();
        writer.Write(first);
        writer.Write(second);

        second.ToArray().ShouldBe(first.ToArray());
    }

    [Fact]
    public void A_reserved_section_code_is_refused_by_name()
    {
        var writer = new ScmapWriter(ScmapFlags.None, 0, EngineInfo.MapFormatVersion);

        Should.Throw<ArgumentException>(() => writer.AddSection(ScmapFormat.RegionIndexSection, new byte[4]))
            .Message.ShouldContain("RGNI");

        Should.Throw<ArgumentException>(() => writer.AddSection(ScmapFormat.BrushModelSection, new byte[4]))
            .Message.ShouldContain("BMDL");
    }

    [Fact]
    public void A_section_added_twice_is_refused()
    {
        var writer = new ScmapWriter(ScmapFlags.None, 0, EngineInfo.MapFormatVersion);
        writer.AddSection(ScmapFormat.StringSection, new byte[4]);

        Should.Throw<ArgumentException>(() => writer.AddSection(ScmapFormat.StringSection, new byte[4]))
            .Message.ShouldContain("STRT");
    }

    [Fact]
    public void The_layouts_padded_size_is_the_only_expression_of_what_a_section_costs()
    {
        ScmapLayout.PaddedSectionSize(0).ShouldBe(0);
        ScmapLayout.PaddedSectionSize(1).ShouldBe(16);
        ScmapLayout.PaddedSectionSize(16).ShouldBe(16);
        ScmapLayout.PaddedSectionSize(17).ShouldBe(32);

        ScmapLayout layout = ScmapLayout.Compute([
            new ScmapSectionSize(ScmapFormat.StringSection, 17),
            new ScmapSectionSize(ScmapFormat.NodeSection, 3),
        ]);

        long tableEnd = ScmapFormat.SectionTableOffset + (2 * ScmapFormat.SectionSize);
        layout.OffsetAt(0).ShouldBe(tableEnd);
        layout.OffsetAt(1).ShouldBe(tableEnd + 32);
        layout.TotalSize.ShouldBe(tableEnd + 32 + 16);
    }
}
