using SpectraEngine.Core.Entities;
using System;
using System.Buffers.Binary;
using System.Reflection;
using System.Text;

namespace SpectraEngine.Entities.Tests;

/// <summary>
/// The <c>.sentdef</c> container: that two writes agree, that a read gives back
/// what was written, and that the forward-compatibility mechanism actually
/// works rather than merely existing.
/// </summary>
/// <remarks>
/// <b>Most of these assert on BYTES rather than on behaviour, which is unusual
/// here and deliberate.</b> This file is the one artifact two independent
/// producers of an entity class have to agree on down to the byte, and every
/// failure mode it has renders as "the editor showed the wrong property" three
/// layers away from the cause. A round trip alone would pass with the layout
/// entirely rearranged, as long as both halves rearranged together.
/// </remarks>
public sealed class SentDefTests
{
    // One class carrying every shape the format has: both bounds, one unbounded
    // pair, a widget, a choice list with an empty display, a flag set, empty
    // display and tooltip strings, inputs and outputs.
    private static EntitySchema Door(EntityOrigin origin = EntityOrigin.EngineCSharp) => new(
        "func_door",
        displayName: "Door",
        group: "Brush",
        placement: EntityPlacement.Brush,
        origin: origin,
        keyvalues:
        [
            new KeyvalueDescriptor(
                "speed", "Speed", "Units a second.", "100",
                KeyvalueType.Float, KeyvalueWidget.Slider, 1f, 1000f,
                KeyvalueFlags.RequiresRestart, KeyvalueDescriptor.NoChoices),
            new KeyvalueDescriptor(
                "movedir", "", "", "up",
                KeyvalueType.Choices, KeyvalueWidget.Auto, float.NaN, float.NaN, 0u,
                [("up", "Up"), ("down", "Down"), ("north", "")]),
            new KeyvalueDescriptor(
                "locked", "Locked", "", "0",
                KeyvalueType.Bool, KeyvalueWidget.Auto, float.NaN, 12.5f,
                KeyvalueFlags.ReadOnly | KeyvalueFlags.HideInEditor, KeyvalueDescriptor.NoChoices),
        ],
        inputs: ["Open", "Close", "Toggle"],
        outputs: ["OnOpened", "OnClosed"]);

    // The other extreme: a name and nothing else.
    private static EntitySchema Bare(string className) => new(className);

    [Fact]
    public void The_magic_reads_SENT_so_a_hex_dump_says_what_the_file_is()
    {
        byte[] image = SentDef.Write([Door()]);

        Encoding.ASCII.GetString(image.AsSpan(0, 4)).ShouldBe("SENT");
        BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(SentDef.HeaderVersionOffset)).ShouldBe(SentDef.Version);
        BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(SentDef.HeaderSizeOffset)).ShouldBe(SentDef.HeaderSize);
    }

    [Fact]
    public void Two_writes_of_one_schema_set_are_byte_identical()
    {
        EntitySchema[] schemas = [Door(), Bare("logic_auto"), Bare("info_player_start")];

        SentDef.Write(schemas).ShouldBe(SentDef.Write(schemas));
    }

    [Fact]
    public void The_order_the_classes_arrive_in_never_reaches_the_bytes()
    {
        EntitySchema door = Door();
        EntitySchema auto = Bare("logic_auto");
        EntitySchema spawn = Bare("info_player_start");

        SentDef.Write([door, auto, spawn]).ShouldBe(SentDef.Write([spawn, door, auto]));
    }

    [Fact]
    public void A_catalogues_registration_order_never_reaches_the_bytes()
    {
        // The real hazard this guards, and the reason the sort exists at all: the
        // producer is a [ModuleInitializer] per class, and the order the loader
        // runs those in is stable enough to look deterministic in a debug run and
        // is not a guarantee. Two catalogues, opposite registration orders, one
        // file - which is as close to a cross-PROCESS check as one process can
        // get, the rest being carried by the sort being ordinal and by nothing in
        // the layout reading a clock, a path or a hash-set's iteration order.
        var forwards = new EntityCatalog();
        forwards.Add(Door(), static () => new PlaceholderEntity());
        forwards.Add(Bare("logic_auto"), static () => new PlaceholderEntity());
        forwards.Add(Bare("info_player_start"), static () => new PlaceholderEntity());

        var backwards = new EntityCatalog();
        backwards.Add(Bare("info_player_start"), static () => new PlaceholderEntity());
        backwards.Add(Bare("logic_auto"), static () => new PlaceholderEntity());
        backwards.Add(Door(), static () => new PlaceholderEntity());

        SentDef.Write(forwards.Schemas).ShouldBe(SentDef.Write(backwards.Schemas));
    }

    [Fact]
    public void Classes_are_sorted_ordinally_which_is_not_the_order_a_culture_would_pick()
    {
        // 'Z' is 0x5A and 'a' is 0x61, so ordinal puts the capital first; every
        // culture-aware comparison this engine could pick up instead puts "apple"
        // first. The whole point of stating the comparison is that a machine's
        // locale must not decide the bytes.
        EntitySchema[] written = SentDef.Read(SentDef.Write([Bare("apple"), Bare("Zebra")]));

        written.Select(schema => schema.ClassName).ShouldBe(["Zebra", "apple"]);
    }

    [Fact]
    public void Every_declared_field_survives_the_round_trip()
    {
        EntitySchema original = Door(EntityOrigin.SdkCSharp);

        EntitySchema[] read = SentDef.Read(SentDef.Write([original]));

        read.Length.ShouldBe(1);
        EntitySchema copy = read[0];
        copy.ClassName.ShouldBe("func_door");
        copy.DisplayName.ShouldBe("Door");
        copy.Group.ShouldBe("Brush");
        copy.Placement.ShouldBe(EntityPlacement.Brush);
        copy.Origin.ShouldBe(EntityOrigin.SdkCSharp);
        copy.Inputs.ShouldBe(["Open", "Close", "Toggle"]);
        copy.Outputs.ShouldBe(["OnOpened", "OnClosed"]);

        copy.Keyvalues.Count.ShouldBe(3);

        KeyvalueDescriptor speed = copy.Keyvalues[0];
        speed.Name.ShouldBe("speed");
        speed.Display.ShouldBe("Speed");
        speed.Tooltip.ShouldBe("Units a second.");
        speed.Default.ShouldBe("100");
        speed.Type.ShouldBe(KeyvalueType.Float);
        speed.Widget.ShouldBe(KeyvalueWidget.Slider);
        speed.Min.ShouldBe(1f);
        speed.Max.ShouldBe(1000f);
        speed.Flags.ShouldBe(KeyvalueFlags.RequiresRestart);
        speed.RequiresRestart.ShouldBeTrue();
        speed.Choices.Count.ShouldBe(0);

        KeyvalueDescriptor locked = copy.Keyvalues[2];
        locked.IsReadOnly.ShouldBeTrue();
        locked.IsHiddenInEditor.ShouldBeTrue();
        locked.Max.ShouldBe(12.5f);

        // Empty strings are a value, not an absence: they ride the one shared
        // reference of 0 and must come back as "" rather than as null.
        KeyvalueDescriptor movedir = copy.Keyvalues[1];
        movedir.Display.ShouldBe("");
        movedir.Tooltip.ShouldBe("");
    }

    [Fact]
    public void A_choice_list_survives_the_round_trip_including_an_empty_display()
    {
        EntitySchema copy = SentDef.Read(SentDef.Write([Door()]))[0];

        copy.Keyvalues[1].Choices.ShouldBe([("up", "Up"), ("down", "Down"), ("north", "")]);
    }

    [Fact]
    public void An_unbounded_bound_comes_back_as_NaN_and_is_detected_with_IsNaN()
    {
        KeyvalueDescriptor movedir = SentDef.Read(SentDef.Write([Door()]))[0].Keyvalues[1];

        // float.IsNaN, never ==: NaN is unequal to itself, so an equality test
        // reports every bound as present and then clamps against a NaN, which
        // yields NaN. Asked here the way the engine asks it.
        float.IsNaN(movedir.Min).ShouldBeTrue();
        float.IsNaN(movedir.Max).ShouldBeTrue();
        movedir.HasMin.ShouldBeFalse();
        movedir.HasMax.ShouldBeFalse();

        // And the other direction on the same descriptor, so "everything came
        // back NaN" cannot pass this.
        KeyvalueDescriptor locked = SentDef.Read(SentDef.Write([Door()]))[0].Keyvalues[2];
        float.IsNaN(locked.Min).ShouldBeTrue();
        locked.HasMax.ShouldBeTrue();
    }

    [Fact]
    public void A_class_with_nothing_declared_survives_the_round_trip()
    {
        EntitySchema copy = SentDef.Read(SentDef.Write([Bare("logic_auto")]))[0];

        copy.ClassName.ShouldBe("logic_auto");
        copy.DisplayName.ShouldBe("");
        copy.Group.ShouldBe("");
        copy.Placement.ShouldBe(EntityPlacement.Point);
        copy.Origin.ShouldBe(EntityOrigin.EngineCSharp);
        copy.Keyvalues.ShouldBeEmpty();
        copy.Inputs.ShouldBeEmpty();
        copy.Outputs.ShouldBeEmpty();
    }

    [Fact]
    public void An_empty_schema_set_writes_a_readable_file()
    {
        // A game with no entity classes is a legal game, and a reader that
        // needed at least one record would refuse its definition table.
        byte[] image = SentDef.Write([]);

        image.Length.ShouldBe(SentDef.HeaderSize + 2);
        SentDef.Read(image).ShouldBeEmpty();
    }

    [Fact]
    public void The_string_table_holds_a_reused_string_once()
    {
        // Four classes filed under one group, each with a keyvalue of one name:
        // eight references to two strings.
        EntitySchema[] schemas =
        [
            Pickup("game_pickup_a"), Pickup("game_pickup_b"), Pickup("game_pickup_c"), Pickup("game_pickup_d"),
        ];

        byte[] image = SentDef.Write(schemas);

        CountRecords(StringTable(image), "Gameplay").ShouldBe(1);
        CountRecords(StringTable(image), "target").ShouldBe(1);

        static EntitySchema Pickup(string className) => new(
            className,
            group: "Gameplay",
            keyvalues:
            [
                new KeyvalueDescriptor(
                    "target", "", "", "", KeyvalueType.TargetName, KeyvalueWidget.Auto,
                    float.NaN, float.NaN, 0u, KeyvalueDescriptor.NoChoices),
            ]);
    }

    [Fact]
    public void The_empty_string_is_the_table_entry_every_unset_field_shares()
    {
        byte[] image = SentDef.Write([Bare("logic_auto")]);

        // Offset zero is the empty string by definition, so the table always
        // opens with a zero-length record and every unset display, tooltip and
        // default points at it.
        BinaryPrimitives.ReadUInt16LittleEndian(StringTable(image)).ShouldBe((ushort)0);

        ReadOnlySpan<byte> record = image.AsSpan(SentDef.HeaderSize);
        BinaryPrimitives.ReadUInt32LittleEndian(record[SentDef.TypeRecordDisplayNameOffset..]).ShouldBe(0u);
        BinaryPrimitives.ReadUInt32LittleEndian(record[SentDef.TypeRecordGroupOffset..]).ShouldBe(0u);
        BinaryPrimitives.ReadUInt32LittleEndian(record[SentDef.TypeRecordClassNameOffset..]).ShouldNotBe(0u);
    }

    [Fact]
    public void A_reader_skips_trailing_bytes_a_newer_writer_appended_and_lands_on_the_next_record()
    {
        // The whole forward-compatibility mechanism, exercised rather than
        // described. A newer writer appends a field to a type record and bumps
        // its RecordSize; this build knows nothing about that field, and the
        // record AFTER it must still parse - which it can only do by advancing
        // by the declared size rather than by what it managed to read.
        byte[] original = SentDef.Write([Door(), Bare("zz_last")]);
        byte[] grown = AppendToFirstRecord(original, extra: 12);

        EntitySchema[] read = SentDef.Read(grown);

        read.Length.ShouldBe(2);

        // The first record is intact up to the fields this build knows...
        read[0].ClassName.ShouldBe("func_door");
        read[0].Keyvalues.Count.ShouldBe(3);
        read[0].Outputs.ShouldBe(["OnOpened", "OnClosed"]);

        // ...and, the assertion that matters, so is the SECOND. Without the
        // skip it would be parsed twelve bytes early, out of the middle of the
        // first record's trailing field.
        read[1].ClassName.ShouldBe("zz_last");
        read[1].Keyvalues.ShouldBeEmpty();
    }

    [Fact]
    public void One_class_written_with_two_origins_differs_in_exactly_the_origin_byte()
    {
        // Held in trust for D15: when a Luau definition of a class exists, this
        // is the oracle that says the two producers agree. Written now against
        // hand-built schemas so it is waiting rather than being invented
        // alongside the thing it is supposed to check.
        byte[] fromCSharp = SentDef.Write([Door(EntityOrigin.EngineCSharp)]);
        byte[] fromLuau = SentDef.Write([Door(EntityOrigin.Luau)]);

        fromCSharp.Length.ShouldBe(fromLuau.Length);

        var differences = new List<int>();
        for (int i = 0; i < fromCSharp.Length; i++)
        {
            if (fromCSharp[i] != fromLuau[i])
                differences.Add(i);
        }

        differences.ShouldBe([SentDef.HeaderSize + SentDef.TypeRecordOriginOffset]);
        fromCSharp[differences[0]].ShouldBe((byte)EntityOrigin.EngineCSharp);
        fromLuau[differences[0]].ShouldBe((byte)EntityOrigin.Luau);
    }

    [Fact]
    public void The_built_in_classes_round_trip_through_the_file_the_export_switch_writes()
    {
        // The same call --export-entity-schema makes, over real generated
        // schemas rather than hand-built ones.
        byte[] image = SentDef.Write(BuiltinEntities.Schemas);
        EntitySchemaCatalog catalog = EntitySchemaCatalog.LoadFromSentDef(image);

        catalog.Count.ShouldBe(BuiltinEntities.ClassCount);
        catalog.TryGetSchema("logic_relay", out EntitySchema? relay).ShouldBeTrue();
        relay!.Placement.ShouldBe(EntityPlacement.Abstract);
        relay.Inputs.ShouldBe(["Trigger", "Enable", "Disable", "Toggle"]);

        catalog.TryGetSchema("logic_timer", out EntitySchema? timer).ShouldBeTrue();
        KeyvalueDescriptor refire = timer!.Keyvalues.Single(keyvalue => keyvalue.Name == "refiretime");
        refire.HasMin.ShouldBeTrue();
        refire.HasMax.ShouldBeFalse();
    }

    // --- the catalogue -----------------------------------------------------

    [Fact]
    public void The_catalogue_resolves_a_class_by_name_and_misses_on_one_it_never_saw()
    {
        EntitySchemaCatalog catalog =
            EntitySchemaCatalog.LoadFromSentDef(SentDef.Write([Door(), Bare("logic_auto")]));

        catalog.Count.ShouldBe(2);
        catalog.Schemas.Select(schema => schema.ClassName).ShouldBe(["func_door", "logic_auto"]);

        catalog.TryGetSchema("func_door", out EntitySchema? door).ShouldBeTrue();
        door!.DisplayName.ShouldBe("Door");

        // A miss is an ordinary answer: a level may name a class from a game
        // whose definitions are not mounted, and that level still loads.
        catalog.TryGetSchema("game_pickup", out EntitySchema? missing).ShouldBeFalse();
        missing.ShouldBeNull();
        catalog.TryGetSchema(null, out _).ShouldBeFalse();
    }

    [Fact]
    public void The_catalogue_has_no_public_constructor_so_bytes_are_the_only_way_in()
    {
        // Structural, and this is what makes it structural rather than a habit:
        // "the editor reads .sentdef and nothing else" only holds if there is no
        // second door for an in-process host to walk through, which would let the
        // two consumers drift with nothing failing.
        typeof(EntitySchemaCatalog)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .ShouldBeEmpty();
    }

    // --- what the writer refuses -------------------------------------------

    [Fact]
    public void A_reserved_flag_bit_is_refused_at_the_write()
    {
        // Bits 3 to 7 are claimed by designs that are not built. Refusing rather
        // than masking is what makes the first real producer of them notice: a
        // writer that dropped the bit silently would ship a file missing it while
        // every parity test still passed.
        var schema = new EntitySchema(
            "func_door",
            keyvalues:
            [
                new KeyvalueDescriptor(
                    "speed", "", "", "", KeyvalueType.Float, KeyvalueWidget.Auto,
                    float.NaN, float.NaN, 1u << 3, KeyvalueDescriptor.NoChoices),
            ]);

        ArgumentException thrown = Should.Throw<ArgumentException>(() => SentDef.Write([schema]));
        thrown.Message.ShouldContain("reserved flag bits");
        thrown.Message.ShouldContain("DefinedMask");
    }

    [Fact]
    public void Two_schemas_claiming_one_class_name_are_refused()
    {
        Should.Throw<ArgumentException>(() => SentDef.Write([Bare("func_door"), Door()]))
            .Message.ShouldContain("func_door");
    }

    [Fact]
    public void A_placement_outside_the_enum_is_refused()
    {
        var schema = new EntitySchema("func_door", placement: (EntityPlacement)9);

        Should.Throw<ArgumentException>(() => SentDef.Write([schema]))
            .Message.ShouldContain("EntityPlacement");
    }

    // --- what the reader refuses -------------------------------------------

    [Fact]
    public void An_image_shorter_than_a_header_is_refused()
    {
        Should.Throw<SentDefFormatException>(() => SentDef.Read([]))
            .Message.ShouldContain("Truncated");

        Should.Throw<SentDefFormatException>(() => SentDef.Read(new byte[SentDef.HeaderSize - 1]));
    }

    [Fact]
    public void A_wrong_magic_is_refused_naming_the_one_it_wanted()
    {
        byte[] image = Mutate(SentDef.Write([Door()]), bytes => bytes[0] = (byte)'X');

        Should.Throw<SentDefFormatException>(() => SentDef.Read(image))
            .Message.ShouldContain("SENT");
    }

    [Fact]
    public void A_version_this_build_does_not_implement_is_refused()
    {
        byte[] image = Mutate(
            SentDef.Write([Door()]),
            bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(SentDef.HeaderVersionOffset), 7));

        Should.Throw<SentDefFormatException>(() => SentDef.Read(image))
            .Message.ShouldContain("version 7");
    }

    [Fact]
    public void A_header_size_below_the_fields_it_must_carry_is_refused()
    {
        // The value the spec table used to state. Sixteen would put the first
        // type record four bytes inside the header, which is exactly what this
        // field exists to prevent.
        byte[] image = Mutate(
            SentDef.Write([Door()]),
            bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(SentDef.HeaderSizeOffset), 16));

        Should.Throw<SentDefFormatException>(() => SentDef.Read(image))
            .Message.ShouldContain("HeaderSize");
    }

    [Fact]
    public void A_string_table_outside_the_image_is_refused()
    {
        byte[] image = Mutate(
            SentDef.Write([Door()]),
            bytes => BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(SentDef.HeaderStringTableSizeOffset), uint.MaxValue));

        Should.Throw<SentDefFormatException>(() => SentDef.Read(image))
            .Message.ShouldContain("string table");
    }

    [Fact]
    public void A_string_table_that_does_not_open_with_the_empty_string_is_refused()
    {
        byte[] image = SentDef.Write([Door()]);
        int tableOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(
            image.AsSpan(SentDef.HeaderStringTableOffsetOffset));

        byte[] broken = Mutate(
            image, bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(tableOffset), 1));

        Should.Throw<SentDefFormatException>(() => SentDef.Read(broken))
            .Message.ShouldContain("zero-length record");
    }

    [Fact]
    public void A_string_reference_past_the_end_of_the_table_is_refused()
    {
        byte[] image = Mutate(
            SentDef.Write([Door()]),
            bytes => BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(SentDef.HeaderSize + SentDef.TypeRecordClassNameOffset), 100_000u));

        Should.Throw<SentDefFormatException>(() => SentDef.Read(image))
            .Message.ShouldContain("outside the");
    }

    [Fact]
    public void A_record_size_below_the_fixed_part_is_refused()
    {
        byte[] image = Mutate(
            SentDef.Write([Door()]),
            bytes => BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(SentDef.HeaderSize + SentDef.TypeRecordSizeOffset), 4u));

        Should.Throw<SentDefFormatException>(() => SentDef.Read(image))
            .Message.ShouldContain("declares a size of 4");
    }

    [Fact]
    public void A_record_size_past_the_end_of_the_image_is_refused()
    {
        byte[] image = Mutate(
            SentDef.Write([Door()]),
            bytes => BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(SentDef.HeaderSize + SentDef.TypeRecordSizeOffset), 1_000_000u));

        Should.Throw<SentDefFormatException>(() => SentDef.Read(image))
            .Message.ShouldContain("Truncated");
    }

    [Fact]
    public void A_declared_count_larger_than_its_own_record_is_refused()
    {
        // The mirror of the skip oracle. Trailing bytes a reader does not
        // understand are skipped; content a record does not have room for is a
        // refusal, because the alternative is reading the next record's bytes as
        // this one's keyvalue.
        byte[] image = Mutate(
            SentDef.Write([Bare("logic_auto")]),
            bytes => BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(SentDef.HeaderSize + SentDef.TypeRecordKeyvalueCountOffset), 1));

        Should.Throw<SentDefFormatException>(() => SentDef.Read(image))
            .Message.ShouldContain("more content than its record holds");
    }

    [Fact]
    public void A_type_count_larger_than_the_image_is_refused_before_anything_is_allocated()
    {
        byte[] image = Mutate(
            SentDef.Write([Door()]),
            bytes => BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(SentDef.HeaderTypeCountOffset), 1_000_000u));

        Should.Throw<SentDefFormatException>(() => SentDef.Read(image))
            .Message.ShouldContain("cannot fit");
    }

    [Fact]
    public void Two_records_claiming_one_class_name_are_refused()
    {
        // Which is also the out-of-order refusal: the walk requires each class
        // name to be strictly greater than the last, so a duplicate and a
        // shuffled file fail the same check.
        byte[] image = SentDef.Write([Door(), Bare("zz_last")]);
        int firstSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(
            image.AsSpan(SentDef.HeaderSize + SentDef.TypeRecordSizeOffset));
        uint firstName = BinaryPrimitives.ReadUInt32LittleEndian(
            image.AsSpan(SentDef.HeaderSize + SentDef.TypeRecordClassNameOffset));

        byte[] duplicated = Mutate(
            image,
            bytes => BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(SentDef.HeaderSize + firstSize + SentDef.TypeRecordClassNameOffset), firstName));

        Should.Throw<SentDefFormatException>(() => SentDef.Read(duplicated))
            .Message.ShouldContain("sorted by class name");
    }

    [Fact]
    public void A_keyvalue_type_this_build_does_not_name_is_refused()
    {
        byte[] image = Mutate(
            SentDef.Write([Door()]),
            bytes => bytes[SentDef.HeaderSize + SentDef.TypeRecordFixedSize + 0x10] = 200);

        Should.Throw<SentDefFormatException>(() => SentDef.Read(image))
            .Message.ShouldContain("KeyvalueType");
    }

    // --- what the reader tolerates -----------------------------------------

    [Fact]
    public void A_flag_bit_this_build_cannot_honour_is_masked_off_on_read()
    {
        // The other half of the writer's refusal: a file some other tool wrote
        // may carry a bit this build has no meaning for, and dropping it is the
        // only safe answer - you cannot honour what you cannot understand.
        byte[] image = Mutate(
            SentDef.Write([Door()]),
            bytes => BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(SentDef.HeaderSize + SentDef.TypeRecordFixedSize + 0x1C),
                KeyvalueFlags.RequiresRestart | (1u << 5)));

        KeyvalueDescriptor speed = SentDef.Read(image)[0].Keyvalues[0];

        speed.Flags.ShouldBe(KeyvalueFlags.RequiresRestart);
        speed.RequiresRestart.ShouldBeTrue();
    }

    [Fact]
    public void An_unrecognised_widget_degrades_to_auto_rather_than_failing()
    {
        // KeyvalueWidget's own rule, honoured here: a property that cannot be
        // shown the way it asked for is worse shown not at all than shown
        // plainly, so an unknown widget is a fallback rather than a refusal.
        byte[] image = Mutate(
            SentDef.Write([Door()]),
            bytes => bytes[SentDef.HeaderSize + SentDef.TypeRecordFixedSize + 0x11] = 200);

        SentDef.Read(image)[0].Keyvalues[0].Widget.ShouldBe(KeyvalueWidget.Auto);
    }

    // --- helpers -----------------------------------------------------------

    private static byte[] Mutate(byte[] image, Action<byte[]> edit)
    {
        var copy = (byte[])image.Clone();
        edit(copy);
        return copy;
    }

    private static ReadOnlySpan<byte> StringTable(byte[] image)
    {
        int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(SentDef.HeaderStringTableOffsetOffset));
        int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(SentDef.HeaderStringTableSizeOffset));
        return image.AsSpan(offset, size);
    }

    // Counts whole length-prefixed records rather than raw text, so a substring
    // of some other string cannot be mistaken for a second copy.
    private static int CountRecords(ReadOnlySpan<byte> table, string text)
    {
        int byteCount = Encoding.UTF8.GetByteCount(text);
        var needle = new byte[sizeof(ushort) + byteCount];
        BinaryPrimitives.WriteUInt16LittleEndian(needle, (ushort)byteCount);
        Encoding.UTF8.GetBytes(text, needle.AsSpan(sizeof(ushort)));

        int found = 0;
        for (int i = 0; i + needle.Length <= table.Length; i++)
        {
            if (table.Slice(i, needle.Length).SequenceEqual(needle))
                found++;
        }

        return found;
    }

    // Plays a newer writer: splices bytes onto the end of the first type record
    // and bumps its RecordSize by as much. The string table offset moves too,
    // because everything after the record shifted - which is the header field
    // that exists so it can.
    private static byte[] AppendToFirstRecord(byte[] image, int extra)
    {
        int recordSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(
            image.AsSpan(SentDef.HeaderSize + SentDef.TypeRecordSizeOffset));
        int tableOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(
            image.AsSpan(SentDef.HeaderStringTableOffsetOffset));
        int insertAt = SentDef.HeaderSize + recordSize;

        var grown = new byte[image.Length + extra];
        image.AsSpan(0, insertAt).CopyTo(grown);

        // Deliberately not zeros: a reader that parsed the appended field rather
        // than skipping it would come back with 0xABAB rather than something
        // that could pass for a default.
        grown.AsSpan(insertAt, extra).Fill(0xAB);
        image.AsSpan(insertAt).CopyTo(grown.AsSpan(insertAt + extra));

        BinaryPrimitives.WriteUInt32LittleEndian(
            grown.AsSpan(SentDef.HeaderSize + SentDef.TypeRecordSizeOffset), (uint)(recordSize + extra));
        BinaryPrimitives.WriteUInt32LittleEndian(
            grown.AsSpan(SentDef.HeaderStringTableOffsetOffset), (uint)(tableOffset + extra));
        return grown;
    }
}
