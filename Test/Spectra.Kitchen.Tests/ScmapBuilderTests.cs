using System;
using System.Buffers.Binary;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Spectra.Kitchen.Maps;
using SpectraEngine.Core;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Maps.Compiled;
using SpectraEngine.Core.Scene;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// The five tables of a compiled map, written and read back.
/// </summary>
/// <remarks>
/// Every refusal here is reproduced by editing the BYTES of a valid file rather
/// than by asking the builder to produce an invalid one, wherever the claim is
/// about a reader. The two are different statements: the builder refusing is a
/// fact about this cook, and the reader refusing is a fact about the file, which
/// is the only one of the two that survives the file being written by something
/// else or edited afterwards.
/// </remarks>
public class ScmapBuilderTests
{
    [Fact]
    public void The_five_tables_read_back_as_they_were_written()
    {
        ScmapProbe probe = ScmapProbe.Read(ScmapFixture.Build());

        probe.Header.FormatVersion.ShouldBe(EngineInfo.CompiledMapFormatVersion);
        probe.Header.SourceMapDigest.ShouldBe(ScmapFixture.Digest);
        probe.Header.MapFormatVersion.ShouldBe((uint)EngineInfo.MapFormatVersion);
        probe.SceneName.ShouldBe(ScmapFixture.SceneName);

        probe.Assets.Select(a => a.Path).ShouldBe(ScmapFixture.AssetPaths);
        probe.Assets[0].Kind.ShouldBe(PackEntryKind.Material);
        probe.Assets[2].Kind.ShouldBe(PackEntryKind.Image);
        probe.Assets[3].ContentHash.ShouldBe(0xDDDD_EEEE_FFFF_0000ul);

        probe.NodeNames.ShouldBe(ScmapFixture.NodeNames);
        probe.Nodes[0].ParentIndex.ShouldBe(-1);
        probe.Nodes[2].ParentIndex.ShouldBe(1);
        probe.Nodes[3].IsSubtractiveBrush.ShouldBeTrue();
        probe.Nodes[6].PayloadKind.ShouldBe(ScmapPayloadKind.MeshInstance);
        probe.Nodes[6].PayloadIndex.ShouldBe(3u);
        probe.Nodes[6].PayloadFlags.ShouldBe(ScmapPayloadFlags.IsEntityOwned);

        for (int i = 0; i < probe.Nodes.Count; i++)
            probe.Nodes[i].NodeId.ShouldBe(ScmapFixture.NodeId(i));

        probe.Spawns.Count.ShouldBe(1);
        probe.Meta.SpawnCount.ShouldBe(1u);
        probe.InvalidDeclaredStates.ShouldBe(0);
    }

    [Fact]
    public void Every_authored_transform_round_trips_bit_for_bit()
    {
        // Not approximately. A compiled map stores the authored ten floats rather
        // than a composed world matrix precisely so replaying the composition
        // reproduces bit-identical matrices, which is what the compile cache's
        // exact-equality keying and the bake oracle both depend on. A tolerance
        // here would pass while quietly giving that away.
        ScmapProbe probe = ScmapProbe.Read(ScmapFixture.Build());

        // The AUTHORED values, not values read back out of the same file: an
        // expectation taken from the file compares it to itself and passes however
        // the floats were mangled on the way in.
        Transform[] expected = ScmapFixture.Transforms;
        probe.Nodes.Count.ShouldBe(expected.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            ScmapNodeRecord record = probe.Nodes[i];

            Bits(record.LocalPosition.X).ShouldBe(Bits(expected[i].Position.X), $"node {i} position x");
            Bits(record.LocalPosition.Y).ShouldBe(Bits(expected[i].Position.Y), $"node {i} position y");
            Bits(record.LocalPosition.Z).ShouldBe(Bits(expected[i].Position.Z), $"node {i} position z");

            Bits(record.LocalRotation.X).ShouldBe(Bits(expected[i].Rotation.X), $"node {i} rotation x");
            Bits(record.LocalRotation.Y).ShouldBe(Bits(expected[i].Rotation.Y), $"node {i} rotation y");
            Bits(record.LocalRotation.Z).ShouldBe(Bits(expected[i].Rotation.Z), $"node {i} rotation z");
            Bits(record.LocalRotation.W).ShouldBe(Bits(expected[i].Rotation.W), $"node {i} rotation w");

            Bits(record.LocalScale.X).ShouldBe(Bits(expected[i].Scale.X), $"node {i} scale x");
            Bits(record.LocalScale.Y).ShouldBe(Bits(expected[i].Scale.Y), $"node {i} scale y");
            Bits(record.LocalScale.Z).ShouldBe(Bits(expected[i].Scale.Z), $"node {i} scale z");
        }
    }

    [Fact]
    public void Strings_are_emitted_in_first_reference_order_during_the_canonical_walk()
    {
        // The empty string, the scene name, the asset table in its own order, then
        // node names in pre-order. Interning as each Add arrives would satisfy this
        // only while the cook happened to call in that order.
        ScmapProbe probe = ScmapProbe.Read(ScmapFixture.Build());
        probe.Strings.ShouldBe(ScmapFixture.ExpectedStrings());
    }

    [Fact]
    public void The_chunk_directory_is_sorted_however_the_cells_arrived()
    {
        ScmapProbe probe = ScmapProbe.Read(ScmapFixture.Build());

        probe.Chunks.Count.ShouldBe(ScmapFixture.SortedCells.Length);
        for (int i = 0; i < probe.Chunks.Count; i++)
        {
            ChunkCoord expected = ScmapFixture.SortedCells[i];
            probe.Chunks[i].X.ShouldBe(expected.X);
            probe.Chunks[i].Y.ShouldBe(expected.Y);
            probe.Chunks[i].Z.ShouldBe(expected.Z);

            // A cell with no owned render geometry is legal and common: the compile
            // produces no artifact for a resident-only cell.
            probe.Chunks[i].MeshSize.ShouldBe(0u);
            probe.Chunks[i].BspSize.ShouldBe(0u);
        }

        // The true render bounds, never the cell cube.
        probe.Chunks[2].BoundsMin.ShouldBe(new Vector3(-0.5f));
    }

    [Fact]
    public void An_unsorted_chunk_directory_in_a_FILE_is_refused()
    {
        // A claim about the bytes rather than about the writer, which is the only
        // one of the two that survives the file being edited: a binary search over
        // an unsorted directory answers "no such cell" for a cell that is right
        // there, which reads as a player falling through a floor they can see.
        byte[] file = ScmapFixture.Build();
        (int offset, _) = FindSection(file, ScmapFormat.ChunkDirectorySection);

        int first = offset + ScmapFormat.ChunkPreambleSize;
        int second = first + ScmapFormat.ChunkRecordSize;

        byte[] swap = file.AsSpan(first, ScmapFormat.ChunkRecordSize).ToArray();
        file.AsSpan(second, ScmapFormat.ChunkRecordSize).CopyTo(file.AsSpan(first));
        swap.CopyTo(file.AsSpan(second));

        Should.Throw<ScmapFormatException>(() => ScmapProbe.Read(file))
            .Message.ShouldContain("ascending cell order");
    }

    [Fact]
    public void The_reserved_section_codes_are_claimed_and_empty()
    {
        byte[] file = ScmapFixture.Build();

        foreach (uint kind in new[]
        {
            ScmapFormat.EntitySection,
            ScmapFormat.EntityConnectionSection,
            ScmapFormat.ScriptSection,
            ScmapFormat.ScriptBytecodeSection,
            ScmapFormat.ScriptSourceSection,
            ScmapFormat.ChunkMeshSection,
            ScmapFormat.ChunkBspSection,
        })
        {
            (int offset, long size) = FindSection(file, kind);
            offset.ShouldBeGreaterThan(0, ScmapFormat.DescribeFourCc(kind));
            size.ShouldBe(0, ScmapFormat.DescribeFourCc(kind));
        }

        // The two with no producer are never written at all.
        Should.Throw<InvalidOperationException>(() => FindSection(file, ScmapFormat.RegionIndexSection));
        Should.Throw<InvalidOperationException>(() => FindSection(file, ScmapFormat.BrushModelSection));

        ScmapProbe probe = ScmapProbe.Read(file);
        probe.SkippedSections.ShouldBe(ScmapFixture.ReservedEmptySections);
    }

    [Fact]
    public void A_section_code_this_build_has_no_meaning_for_is_skipped_rather_than_refused()
    {
        // The forward-compatibility mechanism: a lightmap or a navmesh section
        // written by a later cooker must not make a map unreadable today.
        byte[] file = ScmapFixture.Build();
        (int offset, _) = FindSection(file, ScmapFormat.EntitySection);
        int record = TableRecordOffset(file, ScmapFormat.EntitySection);

        BinaryPrimitives.WriteUInt32LittleEndian(
            file.AsSpan(record), 'Z' | ('Z' << 8) | ('Z' << 16) | ((uint)'Z' << 24));

        offset.ShouldBeGreaterThan(0);

        ScmapProbe probe = ScmapProbe.Read(file);
        probe.SkippedSections.ShouldBe(ScmapFixture.ReservedEmptySections);
        probe.NodeNames.ShouldBe(ScmapFixture.NodeNames);
    }

    [Fact]
    public void A_retired_payload_kind_is_refused_by_the_cook_naming_the_node()
    {
        ScmapBuilder builder = ScmapFixture.CreateBuilder();

        Should.Throw<InvalidOperationException>(() => builder.AddNode(new ScmapNodeSource(
            Guid.NewGuid(), "DoorLeaf", 0, default, ScmapPayloadKind.RetiredBrushModel)))
            .Message.ShouldContain("DoorLeaf");
    }

    [Fact]
    public void A_retired_payload_kind_in_a_FILE_is_refused_naming_the_node()
    {
        // The value was burned rather than reused although no compiled map has ever
        // shipped, because an enum value in a shipped format must never mean two
        // things. Guessing that it meant a part brush is how a door becomes a wall.
        byte[] file = ScmapFixture.Build();
        (int offset, _) = FindSection(file, ScmapFormat.NodeSection);

        const int NodeIndex = 2;
        int kindOffset = offset + ScmapFormat.NodePreambleSize + (NodeIndex * ScmapFormat.NodeRecordSize) + 0x40;
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(kindOffset), 3);

        ScmapFormatException failure = Should.Throw<ScmapFormatException>(() => ScmapProbe.Read(file));
        failure.Message.ShouldContain(ScmapFixture.NodeNames[NodeIndex]);
        failure.Message.ShouldContain("Recook");
    }

    [Fact]
    public void A_material_reaches_the_asset_table_as_a_PATH_and_never_as_its_interned_id()
    {
        // An id is per-process interning order and means nothing in a file. A cook
        // that wrote one produces a map that loads perfectly in the test that wrote
        // it and mis-textures the whole world the moment a second map interns
        // first, and the wrong version is SHORTER CODE.
        //
        // The unrelated intern is load-bearing: without it the ids and the table
        // indices can agree by coincidence and the bug hides completely.
        MaterialRegistry.Intern("Materials/scmap_unrelated_first.spectramat");

        MaterialRef wall = MaterialRegistry.Intern("Materials/scmap_wall.spectramat");
        MaterialRef trim = MaterialRegistry.Intern("Materials/scmap_trim.spectramat");

        var builder = new ScmapBuilder("MaterialOrder");
        uint wallIndex = builder.AddMaterial(wall, 0x11ul);
        uint trimIndex = builder.AddMaterial(trim, 0x22ul);

        wallIndex.ShouldBe(0u);
        trimIndex.ShouldBe(1u);
        ((int)wallIndex).ShouldNotBe(wall.Id);
        ((int)trimIndex).ShouldNotBe(trim.Id);

        ScmapProbe probe = ScmapProbe.Read(builder.Build(UInt128.Zero, EngineInfo.MapFormatVersion));

        probe.Assets.Count.ShouldBe(2);
        probe.Assets[0].Path.ShouldBe("Materials/scmap_wall.spectramat");
        probe.Assets[1].Path.ShouldBe("Materials/scmap_trim.spectramat");
        probe.Assets[0].Kind.ShouldBe(PackEntryKind.Material);

        // The row is a kind, a STRING INDEX and a content hash, and the string
        // index is the table's own first-reference position: index 0 is the empty
        // string, 1 is the scene name, and the asset paths follow.
        probe.Strings[0].ShouldBe(string.Empty);
        probe.Strings[1].ShouldBe("MaterialOrder");
        probe.Strings[2].ShouldBe("Materials/scmap_wall.spectramat");
        probe.Strings[3].ShouldBe("Materials/scmap_trim.spectramat");
    }

    [Fact]
    public void The_default_material_has_no_asset_row_and_says_so()
    {
        var builder = new ScmapBuilder("Defaults");

        Should.Throw<InvalidOperationException>(() => builder.AddMaterial(MaterialRef.Default))
            .Message.ShouldContain("names no path");
    }

    [Fact]
    public void One_path_added_twice_is_one_row()
    {
        var builder = new ScmapBuilder("Dedupe");
        uint first = builder.AddAsset(new ScmapAssetSource(PackEntryKind.Image, "Textures/a.png"));
        uint again = builder.AddAsset(new ScmapAssetSource(PackEntryKind.Image, "Textures\\a.png"));

        again.ShouldBe(first);
        builder.AssetCount.ShouldBe(1);

        Should.Throw<InvalidOperationException>(
            () => builder.AddAsset(new ScmapAssetSource(PackEntryKind.Model, "Textures/a.png")));
    }

    [Fact]
    public void A_parent_index_that_does_not_precede_its_child_is_refused()
    {
        var builder = new ScmapBuilder("Order");

        Should.Throw<InvalidOperationException>(() => builder.AddNode(
            new ScmapNodeSource(Guid.NewGuid(), "Orphan", 0, default, ScmapPayloadKind.None)));

        builder.AddNode(new ScmapNodeSource(Guid.NewGuid(), "Root", -1, default, ScmapPayloadKind.None));

        Should.Throw<InvalidOperationException>(() => builder.AddNode(
            new ScmapNodeSource(Guid.NewGuid(), "Forward", 1, default, ScmapPayloadKind.None)));
    }

    [Fact]
    public void A_directory_entry_pointing_at_a_chunk_blob_this_stage_does_not_write_is_refused()
    {
        var builder = new ScmapBuilder("Blobs");

        Should.Throw<InvalidOperationException>(() => builder.AddChunk(new ScmapChunkSource(
            new ChunkCoord(0, 0, 0),
            new Aabb(Vector3.Zero, Vector3.One),
            MeshOffset: 0,
            MeshSize: 512)))
            .Message.ShouldContain("CMSH");
    }

    [Fact]
    public void A_compile_constant_that_does_not_match_this_engine_is_refused_naming_both_numbers()
    {
        // A map baked on another lattice is not an error anywhere at load: it is
        // sporadic collision bugs, or seams exactly where two cells meet.
        byte[] file = ScmapFixture.Build();
        (int offset, _) = FindSection(file, ScmapFormat.MetaSection);

        BinaryPrimitives.WriteSingleLittleEndian(file.AsSpan(offset + 0x08), 64f);

        ScmapFormatException failure = Should.Throw<ScmapFormatException>(() => ScmapProbe.Read(file));
        failure.Message.ShouldContain("cell size");
        failure.Message.ShouldContain("64");
        failure.Message.ShouldContain(ChunkCoord.CellSize.ToString());
        failure.Message.ShouldContain("Recook");
    }

    [Fact]
    public void A_section_that_does_not_start_on_a_sixteen_byte_boundary_is_refused()
    {
        // The last line of defence behind the writer's own assertions, and the one
        // that survives a file written by something else: every payload in this
        // format is reinterpreted in place, so an unaligned section start is a
        // plane straddling a boundary rather than an exception.
        byte[] file = ScmapFixture.Build();
        int record = TableRecordOffset(file, ScmapFormat.NodeSection);

        ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(record + 0x08));
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(record + 0x08), offset + 1);

        Should.Throw<ScmapFormatException>(() => ScmapProbe.Read(file))
            .Message.ShouldContain("NODE");
    }

    [Fact]
    public void A_format_version_this_engine_does_not_read_is_refused_rather_than_carried()
    {
        // Exact, never a floor: a compiled map is a build output that can always be
        // regenerated, so there is nothing to degrade to.
        byte[] file = ScmapFixture.Build();
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x04), 99);

        Should.Throw<ScmapFormatException>(() => ScmapProbe.Read(file))
            .Message.ShouldContain("Recook");
    }

    [Fact]
    public void A_file_that_is_not_a_compiled_map_is_refused_by_its_own_first_four_bytes()
    {
        byte[] file = ScmapFixture.Build();
        file[0] = (byte)'X';

        Should.Throw<ScmapFormatException>(() => ScmapProbe.Read(file))
            .Message.ShouldContain("SCMP");
    }

    [Fact]
    public void Two_builds_of_one_fixture_are_byte_identical_in_this_process()
    {
        // The weak half of the determinism claim. The strong half needs two
        // PROCESSES, because .NET randomises the string hash seed per process and
        // an in-process comparison structurally cannot see an order that leaked
        // from one; see ScmapDeterminismTests.
        ScmapFixture.Build().ShouldBe(ScmapFixture.Build());
    }

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    private static int TableRecordOffset(byte[] file, uint kind)
    {
        ScmapHeader header = MemoryMarshal.Read<ScmapHeader>(file);
        for (int i = 0; i < header.SectionCount; i++)
        {
            int record = ScmapFormat.SectionTableOffset + (i * ScmapFormat.SectionSize);
            if (BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(record)) == kind) return record;
        }

        throw new InvalidOperationException($"No '{ScmapFormat.DescribeFourCc(kind)}' section in the file.");
    }

    private static (int Offset, long Size) FindSection(byte[] file, uint kind)
    {
        int record = TableRecordOffset(file, kind);
        ScmapSection section = MemoryMarshal.Read<ScmapSection>(file.AsSpan(record));
        return ((int)section.Offset, (long)section.Size);
    }
}
