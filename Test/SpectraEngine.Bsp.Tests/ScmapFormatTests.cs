using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SpectraEngine.Core;
using SpectraEngine.Core.Assets.Models;
using SpectraEngine.Core.Maps.Compiled;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// Raw file bytes are cast into every one of these structs, so their size and
/// field order ARE the compiled-map format. A field reordered or retyped by an
/// edit compiles cleanly and produces a file that parses into the wrong numbers
/// with nothing reporting it, which is what these pins exist to catch.
/// </summary>
public class ScmapFormatTests
{
    [Fact]
    public void Every_record_is_the_documented_size()
    {
        Unsafe.SizeOf<ScmapHeader>().ShouldBe(64);
        Unsafe.SizeOf<ScmapHeader>().ShouldBe(ScmapFormat.HeaderSize);

        Unsafe.SizeOf<ScmapSection>().ShouldBe(32);
        Unsafe.SizeOf<ScmapSection>().ShouldBe(ScmapFormat.SectionSize);

        Unsafe.SizeOf<ScmapAssetEntry>().ShouldBe(16);
        Unsafe.SizeOf<ScmapAssetEntry>().ShouldBe(ScmapFormat.AssetEntrySize);

        Unsafe.SizeOf<ScmapNodeRecord>().ShouldBe(80);
        Unsafe.SizeOf<ScmapNodeRecord>().ShouldBe(ScmapFormat.NodeRecordSize);

        Unsafe.SizeOf<ScmapChunkRecord>().ShouldBe(64);
        Unsafe.SizeOf<ScmapChunkRecord>().ShouldBe(ScmapFormat.ChunkRecordSize);

        Unsafe.SizeOf<ScmapSpawn>().ShouldBe(32);
        Unsafe.SizeOf<ScmapSpawn>().ShouldBe(ScmapFormat.SpawnRecordSize);

        Unsafe.SizeOf<ScmapMeta>().ShouldBe(48);
        Unsafe.SizeOf<ScmapMeta>().ShouldBe(ScmapFormat.MetaPreambleSize);

        // The three framework types the records embed. None of them documents its
        // field layout as a contract, and this format casts raw bytes into all of
        // them.
        Unsafe.SizeOf<Vector3>().ShouldBe(12);
        Unsafe.SizeOf<Quaternion>().ShouldBe(16);
        Unsafe.SizeOf<UInt128>().ShouldBe(16);
    }

    [Fact]
    public void Every_record_array_starts_and_strides_sixteen_byte_aligned()
    {
        // A section starts 16-byte aligned, so an array inside it is only castable
        // in place when both its preamble and its stride are multiples of 16.
        (ScmapFormat.HeaderSize % ScmapFormat.PayloadAlignment).ShouldBe(0);
        (ScmapFormat.SectionSize % ScmapFormat.PayloadAlignment).ShouldBe(0);
        (ScmapFormat.NodePreambleSize % ScmapFormat.PayloadAlignment).ShouldBe(0);
        (ScmapFormat.NodeRecordSize % ScmapFormat.PayloadAlignment).ShouldBe(0);
        (ScmapFormat.ChunkPreambleSize % ScmapFormat.PayloadAlignment).ShouldBe(0);
        (ScmapFormat.ChunkRecordSize % ScmapFormat.PayloadAlignment).ShouldBe(0);
        (ScmapFormat.MetaPreambleSize % ScmapFormat.PayloadAlignment).ShouldBe(0);
        (ScmapFormat.SpawnRecordSize % ScmapFormat.PayloadAlignment).ShouldBe(0);
    }

    [Fact]
    public void Header_fields_sit_at_the_documented_offsets()
    {
        Offset<ScmapHeader>(nameof(ScmapHeader.Magic)).ShouldBe(0x00);
        Offset<ScmapHeader>(nameof(ScmapHeader.FormatVersion)).ShouldBe(0x04);
        Offset<ScmapHeader>(nameof(ScmapHeader.HeaderSize)).ShouldBe(0x06);
        Offset<ScmapHeader>(nameof(ScmapHeader.Flags)).ShouldBe(0x08);
        Offset<ScmapHeader>(nameof(ScmapHeader.SectionCount)).ShouldBe(0x0C);
        Offset<ScmapHeader>(nameof(ScmapHeader.SourceMapDigest)).ShouldBe(0x10);
        Offset<ScmapHeader>(nameof(ScmapHeader.GeometryFormatVersion)).ShouldBe(0x20);
        Offset<ScmapHeader>(nameof(ScmapHeader.MapFormatVersion)).ShouldBe(0x24);
        Offset<ScmapHeader>(nameof(ScmapHeader.VertexLayoutId)).ShouldBe(0x28);
        Offset<ScmapHeader>(nameof(ScmapHeader.EngineVersion)).ShouldBe(0x2C);
        Offset<ScmapHeader>(nameof(ScmapHeader.TotalSize)).ShouldBe(0x30);
        Offset<ScmapHeader>(nameof(ScmapHeader.Reserved)).ShouldBe(0x38);
    }

    [Fact]
    public void Section_table_fields_sit_at_the_documented_offsets()
    {
        Offset<ScmapSection>(nameof(ScmapSection.Kind)).ShouldBe(0x00);
        Offset<ScmapSection>(nameof(ScmapSection.Version)).ShouldBe(0x04);
        Offset<ScmapSection>(nameof(ScmapSection.Flags)).ShouldBe(0x06);
        Offset<ScmapSection>(nameof(ScmapSection.Offset)).ShouldBe(0x08);
        Offset<ScmapSection>(nameof(ScmapSection.Size)).ShouldBe(0x10);
        Offset<ScmapSection>(nameof(ScmapSection.UncompressedSize)).ShouldBe(0x18);
    }

    [Fact]
    public void Node_record_fields_sit_at_the_documented_offsets()
    {
        Offset<ScmapNodeRecord>(nameof(ScmapNodeRecord.Id)).ShouldBe(0x00);
        Offset<ScmapNodeRecord>(nameof(ScmapNodeRecord.NameString)).ShouldBe(0x10);
        Offset<ScmapNodeRecord>(nameof(ScmapNodeRecord.ParentIndex)).ShouldBe(0x14);
        Offset<ScmapNodeRecord>(nameof(ScmapNodeRecord.LocalPosition)).ShouldBe(0x18);
        Offset<ScmapNodeRecord>(nameof(ScmapNodeRecord.LocalRotation)).ShouldBe(0x24);
        Offset<ScmapNodeRecord>(nameof(ScmapNodeRecord.LocalScale)).ShouldBe(0x34);
        Offset<ScmapNodeRecord>(nameof(ScmapNodeRecord.PayloadKindRaw)).ShouldBe(0x40);
        Offset<ScmapNodeRecord>(nameof(ScmapNodeRecord.PayloadFlagsRaw)).ShouldBe(0x42);
        Offset<ScmapNodeRecord>(nameof(ScmapNodeRecord.PayloadIndex)).ShouldBe(0x44);
        Offset<ScmapNodeRecord>(nameof(ScmapNodeRecord.Reserved)).ShouldBe(0x48);
    }

    [Fact]
    public void Chunk_record_fields_sit_at_the_documented_offsets()
    {
        Offset<ScmapChunkRecord>(nameof(ScmapChunkRecord.X)).ShouldBe(0x00);
        Offset<ScmapChunkRecord>(nameof(ScmapChunkRecord.Y)).ShouldBe(0x04);
        Offset<ScmapChunkRecord>(nameof(ScmapChunkRecord.Z)).ShouldBe(0x08);
        Offset<ScmapChunkRecord>(nameof(ScmapChunkRecord.BoundsMin)).ShouldBe(0x0C);
        Offset<ScmapChunkRecord>(nameof(ScmapChunkRecord.BoundsMax)).ShouldBe(0x18);
        Offset<ScmapChunkRecord>(nameof(ScmapChunkRecord.MeshOffset)).ShouldBe(0x24);
        Offset<ScmapChunkRecord>(nameof(ScmapChunkRecord.MeshSize)).ShouldBe(0x28);
        Offset<ScmapChunkRecord>(nameof(ScmapChunkRecord.BspOffset)).ShouldBe(0x2C);
        Offset<ScmapChunkRecord>(nameof(ScmapChunkRecord.BspSize)).ShouldBe(0x30);
        Offset<ScmapChunkRecord>(nameof(ScmapChunkRecord.RegionIndex)).ShouldBe(0x34);
        Offset<ScmapChunkRecord>(nameof(ScmapChunkRecord.Flags)).ShouldBe(0x38);
        Offset<ScmapChunkRecord>(nameof(ScmapChunkRecord.Reserved)).ShouldBe(0x3C);
    }

    [Fact]
    public void Meta_and_asset_fields_sit_at_the_documented_offsets()
    {
        Offset<ScmapMeta>(nameof(ScmapMeta.SceneNameString)).ShouldBe(0x00);
        Offset<ScmapMeta>(nameof(ScmapMeta.SpawnCount)).ShouldBe(0x04);
        Offset<ScmapMeta>(nameof(ScmapMeta.CellSize)).ShouldBe(0x08);
        Offset<ScmapMeta>(nameof(ScmapMeta.WeldBand)).ShouldBe(0x0C);
        Offset<ScmapMeta>(nameof(ScmapMeta.SnapGrid)).ShouldBe(0x10);
        Offset<ScmapMeta>(nameof(ScmapMeta.RegionSize)).ShouldBe(0x14);
        Offset<ScmapMeta>(nameof(ScmapMeta.BytecodeDebugLevel)).ShouldBe(0x18);
        Offset<ScmapMeta>(nameof(ScmapMeta.CookFlags)).ShouldBe(0x1C);

        Offset<ScmapAssetEntry>(nameof(ScmapAssetEntry.Kind)).ShouldBe(0x00);
        Offset<ScmapAssetEntry>(nameof(ScmapAssetEntry.PathString)).ShouldBe(0x04);
        Offset<ScmapAssetEntry>(nameof(ScmapAssetEntry.ContentHash)).ShouldBe(0x08);

        Offset<ScmapSpawn>(nameof(ScmapSpawn.Position)).ShouldBe(0x00);
        Offset<ScmapSpawn>(nameof(ScmapSpawn.Rotation)).ShouldBe(0x0C);
        Offset<ScmapSpawn>(nameof(ScmapSpawn.Reserved)).ShouldBe(0x1C);
    }

    [Fact]
    public void The_magic_reads_SCMP_in_a_hex_dump()
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, ScmapFormat.Magic);

        Encoding.ASCII.GetString(bytes).ShouldBe("SCMP");
        ScmapFormat.DescribeFourCc(ScmapFormat.Magic).ShouldBe("SCMP");
        ScmapFormat.FileExtension.ShouldBe(".scmap");
    }

    [Fact]
    public void A_node_id_is_stored_in_RFC_4122_byte_order()
    {
        // The authored map spells an id as hex in RFC order, so a hex dump of the
        // compiled map must carry the same characters. System.Guid's in-memory
        // layout byte-swaps its first three components on a little-endian machine,
        // which is exactly what the encode exists to undo.
        var id = Guid.Parse("3f2a1c88-4b6d-4a19-9d0e-77c1f0a2b3e4");

        var record = new ScmapNodeRecord(
            id, 0, -1, Vector3.Zero, Quaternion.Identity, Vector3.One,
            ScmapPayloadKind.None, ScmapPayloadFlags.None);

        Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<ScmapNodeRecord>()];
        MemoryMarshal.Write(bytes, in record);

        bytes[..16].ToArray().ShouldBe(id.ToByteArray(bigEndian: true));
        record.NodeId.ShouldBe(id);
    }

    [Fact]
    public void Payload_flags_keep_the_realm_and_state_fields_out_of_the_flag_bits()
    {
        var record = new ScmapNodeRecord(
            Guid.Empty, 0, -1, Vector3.Zero, Quaternion.Identity, Vector3.One,
            ScmapPayloadKind.PartBrush,
            ScmapPayloadFlags.IsEntityOwned | ScmapPayloadFlags.SubtractiveBrush,
            ScmapNodeRealm.Server,
            ScmapNodeState.Dormant);

        record.PayloadFlags.ShouldBe(ScmapPayloadFlags.IsEntityOwned | ScmapPayloadFlags.SubtractiveBrush);
        record.DeclaredRealm.ShouldBe(ScmapNodeRealm.Server);
        record.DeclaredState.ShouldBe(ScmapNodeState.Dormant);
        record.IsSubtractiveBrush.ShouldBeTrue();

        // Bit 7 is meaningful only on a brush. A future payload kind is free to
        // leave it zero, so a reader must ignore it rather than error, and the
        // accessor is where that rule lives.
        var mesh = new ScmapNodeRecord(
            Guid.Empty, 0, -1, Vector3.Zero, Quaternion.Identity, Vector3.One,
            ScmapPayloadKind.MeshInstance,
            ScmapPayloadFlags.SubtractiveBrush);

        mesh.IsSubtractiveBrush.ShouldBeFalse();
    }

    [Fact]
    public void A_flag_inside_the_realm_or_state_field_is_refused_rather_than_folded()
    {
        // A two-bit field spelled as flags is how a realm of Server gets written
        // into bit 1 and read back as something else entirely.
        Should.Throw<ArgumentException>(() => ScmapNodeRecord.ComposeFlags(
            (ScmapPayloadFlags)(1 << ScmapNodeRecord.RealmShift),
            ScmapNodeRealm.Inherit,
            ScmapNodeState.Inherit));
    }

    [Fact]
    public void The_vertex_layout_id_is_the_models_own_hash_of_the_same_attributes()
    {
        // One implementation, borrowed rather than copied. Two cooked formats
        // naming one geometry shape must hash it the same way, or one of the two
        // gates is reporting nonsense.
        ScmapFormat.StandardVertexLayoutId.ShouldBe(
            SmodelFormat.ComputeVertexLayoutId(ScmapFormat.StandardVertexLayout));

        uint floats = 0;
        foreach (SmodelVertexAttribute attribute in ScmapFormat.StandardVertexLayout)
            floats += attribute.ComponentCount;

        floats.ShouldBe(ScmapFormat.StandardVertexStrideFloats);
    }

    [Fact]
    public void The_compile_constants_are_read_from_the_engine_rather_than_restated()
    {
        var meta = new ScmapMeta(sceneNameString: 0, spawnCount: 0);

        meta.CellSize.ShouldBe(SpectraEngine.Core.Bsp.ChunkCoord.CellSize);
        meta.SnapGrid.ShouldBe(SpectraEngine.Core.Bsp.VertexSnapper.GridSize);
        meta.WeldBand.ShouldBe(SpectraEngine.Core.Bsp.ChunkGrid.WeldBand);
    }

    [Fact]
    public void The_compiled_map_version_is_declared_and_gated_exactly()
    {
        // Not a floor. The refusal message says recook, which is only honest
        // because a compiled map is a build output.
        EngineInfo.CompiledMapFormatVersion.ShouldBe((ushort)1);
    }

    [Fact]
    public void A_string_table_reads_back_what_was_written_into_it()
    {
        byte[] section = BuildStringSection(string.Empty, "Wall", "Materials/wall.spectramat", "Ünïcode");
        var table = new ScmapStringTable(section, "test.scmap");

        table.Count.ShouldBe(4);
        table.GetString(0).ShouldBe(string.Empty);
        table.GetString(1).ShouldBe("Wall");
        table.GetString(2).ShouldBe("Materials/wall.spectramat");
        table.GetString(3).ShouldBe("Ünïcode");
        table.GetStringOrEmpty(9).ShouldBe(string.Empty);
    }

    [Fact]
    public void A_string_table_whose_offsets_run_backwards_is_refused()
    {
        byte[] section = BuildStringSection(string.Empty, "one", "two");

        // Offsets live after the u32 count; index 2 is the second entry.
        int offsetOfSecond = ScmapFormat.StringCountSize + sizeof(uint);
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(offsetOfSecond), 99u);

        Should.Throw<ScmapFormatException>(() => new ScmapStringTable(section, "test.scmap"));
    }

    [Fact]
    public void A_string_table_whose_last_offset_misses_the_blob_end_is_refused()
    {
        byte[] section = BuildStringSection(string.Empty, "one", "two");

        // The last offset IS the blob length, which is what makes every string's
        // extent a subtraction with no special case for the final one.
        int offsetOfTerminator = ScmapFormat.StringCountSize + (3 * sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(offsetOfTerminator), 5u);

        Should.Throw<ScmapFormatException>(() => new ScmapStringTable(section, "test.scmap"));
    }

    [Fact]
    public void A_truncated_string_table_is_refused_rather_than_read_past()
    {
        byte[] section = BuildStringSection(string.Empty, "one", "two");

        Should.Throw<ScmapFormatException>(
            () => new ScmapStringTable(section.AsSpan(0, section.Length - 3), "test.scmap"));

        Should.Throw<ScmapFormatException>(() => new ScmapStringTable([0, 0, 0, 0], "test.scmap"));
    }

    private static int Offset<T>(string field) => (int)Marshal.OffsetOf<T>(field);

    // The section body, laid out by hand so the reader is tested against bytes
    // rather than against the writer that produces them.
    private static byte[] BuildStringSection(params string[] strings)
    {
        var blob = new System.IO.MemoryStream();
        var offsets = new uint[strings.Length + 1];

        for (int i = 0; i < strings.Length; i++)
        {
            offsets[i] = (uint)blob.Length;
            byte[] utf8 = Encoding.UTF8.GetBytes(strings[i]);
            blob.Write(utf8, 0, utf8.Length);
        }

        offsets[strings.Length] = (uint)blob.Length;

        var body = new byte[ScmapFormat.StringCountSize + (offsets.Length * sizeof(uint)) + sizeof(uint) + blob.Length];
        Span<byte> span = body;

        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)strings.Length);
        int cursor = ScmapFormat.StringCountSize;

        foreach (uint offset in offsets)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[cursor..], offset);
            cursor += sizeof(uint);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(span[cursor..], (uint)blob.Length);
        cursor += sizeof(uint);

        blob.ToArray().CopyTo(span[cursor..]);
        return body;
    }
}
