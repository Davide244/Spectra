using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SpectraEngine.Core.Entities;

/// <summary>
/// The <c>.sentdef</c> container: the bytes that carry every
/// <see cref="EntitySchema"/> this build knows to something that does not
/// reference the assembly they were declared in.
/// </summary>
/// <remarks>
/// <para>
/// <b>The writer, the reader and the layout constants are in ONE file on
/// purpose.</b> Two halves of a byte layout in two files drift the first time
/// somebody adds a field to one of them, and the failure is not a build error:
/// it is a file that parses into different numbers than it was written from, so
/// a float bound becomes a colour and a class silently gains a property nobody
/// declared. Everything about where a byte sits is decided here, once, and both
/// directions read the same constants.
/// </para>
/// <para>
/// <b>The layout is <c>docs/formats-and-pipeline.md</c> section 3.2, with two
/// things that document leaves open settled here.</b> The choice list's
/// encoding (the document says a keyvalue record is "32 bytes + a variable
/// choice list" and never says what a choice is), and the input and output
/// records (named, never laid out). Both are pinned below. The document's
/// header table is also arithmetically one field wider than its own
/// <c>HeaderSize = 16</c>: the listed fields run to 0x14, so
/// <see cref="HeaderSize"/> is 20 here. Sixteen would put the first type record
/// four bytes inside the header, which is exactly the thing that field exists
/// to prevent.
/// </para>
/// <para>
/// <b><see cref="TypeRecordSizeOffset">RecordSize</see> includes itself, and it
/// is the whole forward-compatibility mechanism.</b> A reader that does not
/// understand a field a newer writer appended does not have to: it reads the
/// fields it knows, then advances by <c>RecordSize</c> from the record's start
/// and lands exactly on the next record. That only works if a reader never
/// derives the next record's position from what it managed to parse, so the
/// walk below advances by the declared size and by nothing else.
/// </para>
/// <para>
/// <b>Explicitly little-endian, unlike <c>.spack</c>.</b> A pack casts a struct
/// straight out of a mapped view and asserts the machine's endianness to make
/// that legal; this format is written and read a field at a time through
/// <see cref="BinaryPrimitives"/>, so the byte order is stated rather than
/// inherited and a big-endian host would produce the same file.
/// </para>
/// <para>
/// <b>Determinism is the property this class exists to have</b>, because the
/// artifact is compared byte for byte: two producers of one entity class must
/// write identical records apart from the <see cref="EntityOrigin"/> badge, and
/// an incremental cook must be able to tell "unchanged" from "rewritten".
/// Three things deliver it. Types are sorted by class name with
/// <see cref="string.CompareOrdinal"/> - the same order
/// <see cref="EntityCatalog"/> enumerates in - so the loader-dependent order
/// module initializers ran in is never observable. Strings are interned in one
/// fixed traversal order rather than by hash-set iteration. And nothing in the
/// layout depends on a clock, a path, or a machine's culture.
/// </para>
/// </remarks>
public static class SentDef
{
    /// <summary>
    /// The four bytes at offset 0, reading <c>SENT</c> in ASCII when written
    /// little-endian.
    /// </summary>
    public const uint Magic = 0x544E4553;

    /// <summary>The version this build writes, and the only one it reads.</summary>
    /// <remarks>
    /// <b>Exact match, refuse loudly</b> - the rule every cooked format in this
    /// engine follows. The escape hatch for an additive change is
    /// <see cref="TypeRecordSizeOffset">RecordSize</see>, which is precisely why
    /// appending a trailing field is not a version bump; a version bump is for a
    /// change that moves a byte somebody already reads.
    /// </remarks>
    public const ushort Version = 1;

    /// <summary>
    /// Bytes of header, which is also the offset of the first type record.
    /// </summary>
    /// <remarks>
    /// Written explicitly although it is always 20 today, so the header can grow
    /// without spending a version number: a reader starts its walk here rather
    /// than at a constant of its own.
    /// </remarks>
    public const ushort HeaderSize = 20;

    /// <summary>Offset of <see cref="Magic"/>.</summary>
    public const int HeaderMagicOffset = 0x00;

    /// <summary>Offset of the <c>u16</c> format version.</summary>
    public const int HeaderVersionOffset = 0x04;

    /// <summary>Offset of the <c>u16</c> header size.</summary>
    public const int HeaderSizeOffset = 0x06;

    /// <summary>Offset of the <c>u32</c> type count.</summary>
    public const int HeaderTypeCountOffset = 0x08;

    /// <summary>Offset of the <c>u32</c> absolute string-table offset.</summary>
    public const int HeaderStringTableOffsetOffset = 0x0C;

    /// <summary>Offset of the <c>u32</c> string-table size in bytes.</summary>
    public const int HeaderStringTableSizeOffset = 0x10;

    /// <summary>Bytes of type record before the first keyvalue record.</summary>
    public const int TypeRecordFixedSize = 0x18;

    /// <summary>Offset, within a type record, of its self-including size.</summary>
    public const int TypeRecordSizeOffset = 0x00;

    /// <summary>Offset, within a type record, of the class-name string reference.</summary>
    public const int TypeRecordClassNameOffset = 0x04;

    /// <summary>Offset, within a type record, of the display-name string reference.</summary>
    public const int TypeRecordDisplayNameOffset = 0x08;

    /// <summary>Offset, within a type record, of the group string reference.</summary>
    public const int TypeRecordGroupOffset = 0x0C;

    /// <summary>Offset, within a type record, of the <see cref="EntityPlacement"/> byte.</summary>
    public const int TypeRecordPlacementOffset = 0x10;

    /// <summary>Offset, within a type record, of the <see cref="EntityOrigin"/> byte.</summary>
    /// <remarks>
    /// The one byte <c>D15</c>'s parity pin allows two producers of the same
    /// class to differ in. Named here so a test can assert on the position
    /// rather than counting the table by hand.
    /// </remarks>
    public const int TypeRecordOriginOffset = 0x11;

    /// <summary>Offset, within a type record, of the <c>u16</c> keyvalue count.</summary>
    public const int TypeRecordKeyvalueCountOffset = 0x12;

    /// <summary>Offset, within a type record, of the <c>u16</c> input count.</summary>
    public const int TypeRecordInputCountOffset = 0x14;

    /// <summary>Offset, within a type record, of the <c>u16</c> output count.</summary>
    public const int TypeRecordOutputCountOffset = 0x16;

    /// <summary>
    /// Bytes of keyvalue record, before its choice list.
    /// </summary>
    /// <remarks>
    /// <b>Fixed at 32 and it may not grow.</b> Both the replication design and
    /// the realm design claim bits of the record's existing
    /// <see cref="KeyvalueDescriptor.Flags"/> word specifically so that neither
    /// has to add a member here; a new field would break every
    /// <c>.sentdef</c> already written.
    /// </remarks>
    public const int KeyvalueRecordSize = 0x20;

    /// <summary>Bytes of one choice record: two string references.</summary>
    public const int ChoiceRecordSize = 8;

    /// <summary>Bytes of one string reference, and of one input or output record.</summary>
    public const int StringRefSize = 4;

    // Throwing rather than replacing, in both directions. Encoding a lone
    // surrogate silently writes U+FFFD, and decoding an invalid sequence
    // silently produces one: either way a class name comes back spelled
    // differently from how it went in, resolves against nothing, and the map
    // that names it loads a placeholder with no error anywhere.
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Writes <paramref name="schemas"/> as a <c>.sentdef</c> image.
    /// </summary>
    /// <remarks>
    /// <b>Sorts a COPY.</b> Sorting the caller's list would reorder a
    /// catalogue's own array as a side effect of exporting it, and the order a
    /// schema list is in is the order a property panel lays a class out.
    /// </remarks>
    /// <param name="schemas">The classes to write, in any order.</param>
    /// <returns>The complete file image.</returns>
    /// <exception cref="ArgumentException">
    /// Two schemas claim one class name, a count or a string exceeds what the
    /// layout can express, a placement or origin is outside its enum, or a
    /// keyvalue carries a flag bit this build has not assigned meaning to.
    /// </exception>
    public static byte[] Write(IReadOnlyList<EntitySchema> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);

        EntitySchema[] ordered = SortedByClassName(schemas);

        // Pass one measures and interns; pass two writes into an array sized
        // exactly. Two passes rather than a growing buffer because the total is
        // then an assertion as well as an allocation: the write ends at the last
        // byte or the two passes disagree, which is the failure a layout change
        // touching one of them produces.
        var strings = new StringTableBuilder();
        var recordSizes = new int[ordered.Length];
        long recordBytes = 0;
        for (int i = 0; i < ordered.Length; i++)
        {
            recordSizes[i] = MeasureAndIntern(ordered[i], strings);
            recordBytes += recordSizes[i];
        }

        long stringTableOffset = HeaderSize + recordBytes;
        long total = stringTableOffset + strings.Length;
        if (total > int.MaxValue)
        {
            throw new ArgumentException(
                $"A .sentdef image would be {total} bytes, which its u32 offsets cannot address.",
                nameof(schemas));
        }

        var bytes = new byte[(int)total];
        Span<byte> image = bytes;

        BinaryPrimitives.WriteUInt32LittleEndian(image[HeaderMagicOffset..], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(image[HeaderVersionOffset..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(image[HeaderSizeOffset..], HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(image[HeaderTypeCountOffset..], (uint)ordered.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image[HeaderStringTableOffsetOffset..], (uint)stringTableOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(image[HeaderStringTableSizeOffset..], (uint)strings.Length);

        int position = HeaderSize;
        for (int i = 0; i < ordered.Length; i++)
        {
            WriteType(image.Slice(position, recordSizes[i]), ordered[i], strings);
            position += recordSizes[i];
        }

        // The table was sized before any record was written, so a string the
        // write pass interns and the measure pass did not would silently push
        // the file past its own end. Caught here rather than as a CopyTo that
        // happens to throw.
        if (strings.Length != (int)(total - stringTableOffset))
        {
            throw new InvalidOperationException(
                "The .sentdef write pass interned a string the measure pass did not; the two passes disagree " +
                "about the layout.");
        }

        strings.CopyTo(image[position..]);
        return bytes;
    }

    /// <summary>
    /// Reads a <c>.sentdef</c> image back into schemas, in the file's order.
    /// </summary>
    /// <remarks>
    /// <b>Takes a span, because a mounted pack hands out spans into a mapped
    /// view</b> and copying a game's whole definition table to parse it would
    /// throw away the one property the container was built for. Nothing here
    /// keeps a reference to <paramref name="image"/>: every string is decoded
    /// into managed memory before the method returns, so the view may be
    /// unmapped the moment it does.
    /// </remarks>
    /// <param name="image">The complete file.</param>
    /// <returns>The schemas, ordered by class name as the file stores them.</returns>
    /// <exception cref="SentDefFormatException">The image is not one this build can read.</exception>
    public static EntitySchema[] Read(ReadOnlySpan<byte> image)
    {
        if (image.Length < HeaderSize)
        {
            throw new SentDefFormatException(
                $"Truncated: a .sentdef header is {HeaderSize} bytes and this image is {image.Length}.", 0);
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(image[HeaderMagicOffset..]);
        if (magic != Magic)
        {
            throw new SentDefFormatException(
                $"Not a .sentdef: expected the magic 'SENT' (0x{Magic:X8}) and found 0x{magic:X8}.",
                HeaderMagicOffset);
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(image[HeaderVersionOffset..]);
        if (version != Version)
        {
            // Exact match, and the message says which version this build is so a
            // report carries the number rather than "it did not load".
            throw new SentDefFormatException(
                $"This build reads .sentdef version {Version} and the image declares version {version}. " +
                "Re-export the entity schemas with a matching engine.",
                HeaderVersionOffset);
        }

        ushort headerSize = BinaryPrimitives.ReadUInt16LittleEndian(image[HeaderSizeOffset..]);
        if (headerSize < HeaderSize || headerSize > image.Length)
        {
            throw new SentDefFormatException(
                $"HeaderSize is {headerSize}, which is outside the {HeaderSize}..{image.Length} a readable image allows.",
                HeaderSizeOffset);
        }

        uint typeCount = BinaryPrimitives.ReadUInt32LittleEndian(image[HeaderTypeCountOffset..]);
        uint stringTableOffset = BinaryPrimitives.ReadUInt32LittleEndian(image[HeaderStringTableOffsetOffset..]);
        uint stringTableSize = BinaryPrimitives.ReadUInt32LittleEndian(image[HeaderStringTableSizeOffset..]);

        if (stringTableOffset < headerSize ||
            stringTableOffset > (uint)image.Length ||
            stringTableSize > (uint)image.Length - stringTableOffset)
        {
            throw new SentDefFormatException(
                $"The string table claims {stringTableSize} bytes at offset {stringTableOffset}, " +
                $"which does not fit inside a {image.Length}-byte image.",
                HeaderStringTableOffsetOffset);
        }

        ReadOnlySpan<byte> table = image.Slice((int)stringTableOffset, (int)stringTableSize);

        // Index 0 is the empty string by definition, so a reference of 0 can be
        // answered without touching the table at all. Checked rather than
        // assumed, because every empty display name, tooltip and default in the
        // file points here and a table that does not start with a zero-length
        // record would hand all of them somebody else's text.
        if (table.Length < sizeof(ushort) || BinaryPrimitives.ReadUInt16LittleEndian(table) != 0)
        {
            throw new SentDefFormatException(
                "The string table must open with a zero-length record, which is the empty string every " +
                "reference of 0 resolves to.",
                (long)stringTableOffset);
        }

        // Refused before the array is allocated: a corrupt count of four billion
        // would otherwise be an OutOfMemoryException naming nothing, rather than
        // a refusal naming the file.
        if ((long)typeCount * TypeRecordFixedSize > image.Length - headerSize)
        {
            throw new SentDefFormatException(
                $"The header claims {typeCount} type(s), which cannot fit in the " +
                $"{image.Length - headerSize} bytes after the header.",
                HeaderTypeCountOffset);
        }

        var schemas = new EntitySchema[typeCount];
        var decoded = new Dictionary<uint, string>();
        int cursor = headerSize;
        string previousClassName = "";

        for (int i = 0; i < schemas.Length; i++)
        {
            if (image.Length - cursor < TypeRecordFixedSize)
            {
                throw new SentDefFormatException(
                    $"Truncated: type record {i} needs {TypeRecordFixedSize} bytes and " +
                    $"{image.Length - cursor} remain.",
                    cursor);
            }

            uint recordSize = BinaryPrimitives.ReadUInt32LittleEndian(image[(cursor + TypeRecordSizeOffset)..]);
            if (recordSize < TypeRecordFixedSize)
            {
                throw new SentDefFormatException(
                    $"Type record {i} declares a size of {recordSize}, below the {TypeRecordFixedSize} " +
                    "bytes every record starts with.",
                    cursor);
            }

            if (recordSize > (uint)(image.Length - cursor))
            {
                throw new SentDefFormatException(
                    $"Truncated: type record {i} declares {recordSize} bytes and " +
                    $"{image.Length - cursor} remain.",
                    cursor);
            }

            EntitySchema schema = ReadType(image.Slice(cursor, (int)recordSize), table, decoded, i, cursor);

            // The file's order is a format invariant, not an accident of the
            // writer: it is what makes two exports of one catalogue comparable
            // byte for byte, and it is what a lookup will binary-search over.
            // Strictly increasing, so this refuses a duplicate class name too -
            // two classes claiming one name is a file that means different
            // things depending on which entry a reader happened to keep.
            if (i > 0 && string.CompareOrdinal(previousClassName, schema.ClassName) >= 0)
            {
                throw new SentDefFormatException(
                    $"Type records must be sorted by class name (ordinal) and strictly unique: " +
                    $"'{schema.ClassName}' follows '{previousClassName}'.",
                    cursor + TypeRecordClassNameOffset);
            }

            previousClassName = schema.ClassName;
            schemas[i] = schema;

            // Advance by the DECLARED size, never by what was parsed. That one
            // line is the forward-compatibility mechanism: a newer writer's
            // trailing fields are skipped and the next record still lands.
            cursor += (int)recordSize;
        }

        return schemas;
    }

    // Ordinal, and never the current culture: a catalogue sorted by a machine's
    // locale would export a different file on a different machine from the same
    // source, which is exactly the difference D15's parity pin measures.
    private static EntitySchema[] SortedByClassName(IReadOnlyList<EntitySchema> schemas)
    {
        var ordered = new EntitySchema[schemas.Count];
        for (int i = 0; i < ordered.Length; i++)
        {
            ordered[i] = schemas[i] ?? throw new ArgumentException(
                $"Schema {i} is null; a .sentdef entry has to describe a class.", nameof(schemas));
        }

        Array.Sort(ordered, static (a, b) => string.CompareOrdinal(a.ClassName, b.ClassName));

        for (int i = 1; i < ordered.Length; i++)
        {
            if (string.Equals(ordered[i - 1].ClassName, ordered[i].ClassName, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Two schemas claim the class name '{ordered[i].ClassName}'. One name is one class, " +
                    "or a map means different things depending on which record a reader kept.",
                    nameof(schemas));
            }
        }

        return ordered;
    }

    private static int MeasureAndIntern(EntitySchema schema, StringTableBuilder strings)
    {
        if (!Enum.IsDefined(schema.Placement))
        {
            throw new ArgumentException(
                $"Class '{schema.ClassName}' declares placement {(byte)schema.Placement}, which is not an " +
                $"{nameof(EntityPlacement)} this build names.", nameof(schema));
        }

        if (!Enum.IsDefined(schema.Origin))
        {
            throw new ArgumentException(
                $"Class '{schema.ClassName}' declares origin {(byte)schema.Origin}, which is not an " +
                $"{nameof(EntityOrigin)} this build names.", nameof(schema));
        }

        // The interning order below IS the string table's layout, so it has to
        // match the field order of the record that is written afterwards. It is
        // the only reason two writes of one catalogue produce one file.
        strings.Intern(schema.ClassName);
        strings.Intern(schema.DisplayName);
        strings.Intern(schema.Group);

        RequireUInt16(schema.Keyvalues.Count, "keyvalues", schema.ClassName);
        RequireUInt16(schema.Inputs.Count, "inputs", schema.ClassName);
        RequireUInt16(schema.Outputs.Count, "outputs", schema.ClassName);

        int size = TypeRecordFixedSize;
        for (int i = 0; i < schema.Keyvalues.Count; i++)
        {
            KeyvalueDescriptor keyvalue = schema.Keyvalues[i];

            // Refused rather than masked. Bits 3 to 7 are claimed by designs
            // that are not built (replication, per-property realm), so nothing
            // in this build can legitimately set one - and a writer that quietly
            // dropped it would let the first real producer of those bits ship a
            // file missing them while every parity test still passed. Widening
            // KeyvalueFlags.DefinedMask is the change that admits them.
            if (!Enum.IsDefined(keyvalue.Type))
            {
                throw new ArgumentException(
                    $"Keyvalue '{keyvalue.Name}' on class '{schema.ClassName}' declares type " +
                    $"{(byte)keyvalue.Type}, which is not a {nameof(KeyvalueType)} this build names.",
                    nameof(schema));
            }

            uint reserved = keyvalue.Flags & ~KeyvalueFlags.DefinedMask;
            if (reserved != 0)
            {
                throw new ArgumentException(
                    $"Keyvalue '{keyvalue.Name}' on class '{schema.ClassName}' sets reserved flag bits " +
                    $"0x{reserved:X8}. Those bits are claimed and unassigned; widen " +
                    $"{nameof(KeyvalueFlags)}.{nameof(KeyvalueFlags.DefinedMask)} in the change that gives " +
                    "them meaning.", nameof(schema));
            }

            strings.Intern(keyvalue.Name);
            strings.Intern(keyvalue.Display);
            strings.Intern(keyvalue.Tooltip);
            strings.Intern(keyvalue.Default);

            IReadOnlyList<(string Value, string Display)> choices = keyvalue.Choices ?? KeyvalueDescriptor.NoChoices;
            RequireUInt16(choices.Count, $"choices on keyvalue '{keyvalue.Name}'", schema.ClassName);
            for (int c = 0; c < choices.Count; c++)
            {
                strings.Intern(choices[c].Value);
                strings.Intern(choices[c].Display);
            }

            size += KeyvalueRecordSize + (choices.Count * ChoiceRecordSize);
        }

        for (int i = 0; i < schema.Inputs.Count; i++)
            strings.Intern(schema.Inputs[i]);
        for (int i = 0; i < schema.Outputs.Count; i++)
            strings.Intern(schema.Outputs[i]);

        return size + ((schema.Inputs.Count + schema.Outputs.Count) * StringRefSize);
    }

    private static void WriteType(Span<byte> record, EntitySchema schema, StringTableBuilder strings)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(record[TypeRecordSizeOffset..], (uint)record.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(record[TypeRecordClassNameOffset..], strings.Intern(schema.ClassName));
        BinaryPrimitives.WriteUInt32LittleEndian(record[TypeRecordDisplayNameOffset..], strings.Intern(schema.DisplayName));
        BinaryPrimitives.WriteUInt32LittleEndian(record[TypeRecordGroupOffset..], strings.Intern(schema.Group));
        record[TypeRecordPlacementOffset] = (byte)schema.Placement;
        record[TypeRecordOriginOffset] = (byte)schema.Origin;
        BinaryPrimitives.WriteUInt16LittleEndian(record[TypeRecordKeyvalueCountOffset..], (ushort)schema.Keyvalues.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(record[TypeRecordInputCountOffset..], (ushort)schema.Inputs.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(record[TypeRecordOutputCountOffset..], (ushort)schema.Outputs.Count);

        int position = TypeRecordFixedSize;
        for (int i = 0; i < schema.Keyvalues.Count; i++)
        {
            KeyvalueDescriptor keyvalue = schema.Keyvalues[i];
            IReadOnlyList<(string Value, string Display)> choices = keyvalue.Choices ?? KeyvalueDescriptor.NoChoices;
            Span<byte> field = record[position..];

            BinaryPrimitives.WriteUInt32LittleEndian(field[0x00..], strings.Intern(keyvalue.Name));
            BinaryPrimitives.WriteUInt32LittleEndian(field[0x04..], strings.Intern(keyvalue.Display));
            BinaryPrimitives.WriteUInt32LittleEndian(field[0x08..], strings.Intern(keyvalue.Tooltip));
            BinaryPrimitives.WriteUInt32LittleEndian(field[0x0C..], strings.Intern(keyvalue.Default));
            field[0x10] = (byte)keyvalue.Type;

            // The widget byte is written VERBATIM, not validated. Its documented
            // policy is that an unrecognised widget degrades to Auto rather than
            // failing, and preserving the byte is what lets a newer editor that
            // does know it still get it - the same forward-compat trade
            // RecordSize makes one level up.
            field[0x11] = keyvalue.Widget;
            BinaryPrimitives.WriteUInt16LittleEndian(field[0x12..], (ushort)choices.Count);

            // The BITS, not a comparison: NaN means unbounded here and is unequal
            // to itself, so anything that tried to canonicalise it by testing
            // equality would rewrite every bound in the file.
            BinaryPrimitives.WriteSingleLittleEndian(field[0x14..], keyvalue.Min);
            BinaryPrimitives.WriteSingleLittleEndian(field[0x18..], keyvalue.Max);
            BinaryPrimitives.WriteUInt32LittleEndian(field[0x1C..], keyvalue.Flags);

            position += KeyvalueRecordSize;
            for (int c = 0; c < choices.Count; c++)
            {
                Span<byte> choice = record[position..];
                BinaryPrimitives.WriteUInt32LittleEndian(choice[0..], strings.Intern(choices[c].Value));
                BinaryPrimitives.WriteUInt32LittleEndian(choice[4..], strings.Intern(choices[c].Display));
                position += ChoiceRecordSize;
            }
        }

        for (int i = 0; i < schema.Inputs.Count; i++, position += StringRefSize)
            BinaryPrimitives.WriteUInt32LittleEndian(record[position..], strings.Intern(schema.Inputs[i]));

        for (int i = 0; i < schema.Outputs.Count; i++, position += StringRefSize)
            BinaryPrimitives.WriteUInt32LittleEndian(record[position..], strings.Intern(schema.Outputs[i]));

        if (position != record.Length)
        {
            // Unreachable unless MeasureAndIntern and WriteType disagree about
            // the layout, which is the one failure two passes exist to catch and
            // the reason they are in one file.
            throw new InvalidOperationException(
                $"Wrote {position} bytes into a {record.Length}-byte record for '{schema.ClassName}'.");
        }
    }

    private static EntitySchema ReadType(
        ReadOnlySpan<byte> record,
        ReadOnlySpan<byte> table,
        Dictionary<uint, string> decoded,
        int index,
        long recordOffset)
    {
        string className = ReadString(record, table, decoded, TypeRecordClassNameOffset, recordOffset, index);
        if (className.Length == 0)
        {
            throw new SentDefFormatException(
                $"Type record {index} has an empty class name; a class with no name cannot be looked up.",
                recordOffset + TypeRecordClassNameOffset);
        }

        string displayName = ReadString(record, table, decoded, TypeRecordDisplayNameOffset, recordOffset, index);
        string group = ReadString(record, table, decoded, TypeRecordGroupOffset, recordOffset, index);

        byte placement = record[TypeRecordPlacementOffset];
        if (!Enum.IsDefined((EntityPlacement)placement))
        {
            // Refused rather than degraded. Placement decides whether a node
            // carries brush geometry at all, so there is no safe value to fall
            // back to, and both enums are frozen and append-only: a byte outside
            // the set is either corruption or a file a newer Version field
            // should have announced.
            throw new SentDefFormatException(
                $"Class '{className}' declares placement {placement}, which is not an " +
                $"{nameof(EntityPlacement)} this build names.",
                recordOffset + TypeRecordPlacementOffset);
        }

        byte origin = record[TypeRecordOriginOffset];
        if (!Enum.IsDefined((EntityOrigin)origin))
        {
            throw new SentDefFormatException(
                $"Class '{className}' declares origin {origin}, which is not an " +
                $"{nameof(EntityOrigin)} this build names.",
                recordOffset + TypeRecordOriginOffset);
        }

        int keyvalueCount = BinaryPrimitives.ReadUInt16LittleEndian(record[TypeRecordKeyvalueCountOffset..]);
        int inputCount = BinaryPrimitives.ReadUInt16LittleEndian(record[TypeRecordInputCountOffset..]);
        int outputCount = BinaryPrimitives.ReadUInt16LittleEndian(record[TypeRecordOutputCountOffset..]);

        var keyvalues = new KeyvalueDescriptor[keyvalueCount];
        int position = TypeRecordFixedSize;

        for (int i = 0; i < keyvalueCount; i++)
        {
            // Every advance is bounds-checked against the record's own declared
            // size. A count that overruns it is a refusal naming the class, not
            // an IndexOutOfRangeException from inside a slice.
            RequireRoom(record.Length, position, KeyvalueRecordSize, className, "keyvalue record", recordOffset);
            ReadOnlySpan<byte> field = record.Slice(position, KeyvalueRecordSize);

            string name = ReadString(field, table, decoded, 0x00, recordOffset + position, index);
            string display = ReadString(field, table, decoded, 0x04, recordOffset + position, index);
            string tooltip = ReadString(field, table, decoded, 0x08, recordOffset + position, index);
            string @default = ReadString(field, table, decoded, 0x0C, recordOffset + position, index);

            byte type = field[0x10];
            if (!Enum.IsDefined((KeyvalueType)type))
            {
                throw new SentDefFormatException(
                    $"Keyvalue '{name}' on class '{className}' declares type {type}, which is not a " +
                    $"{nameof(KeyvalueType)} this build names.",
                    recordOffset + position + 0x10);
            }

            // Degraded, not refused: KeyvalueWidget's own rule is that a widget
            // nobody recognises falls back to Auto, because a property that
            // cannot be shown is worse than one shown plainly.
            byte widget = field[0x11];
            if (!KeyvalueWidget.IsDefined(widget))
                widget = KeyvalueWidget.Auto;

            int choiceCount = BinaryPrimitives.ReadUInt16LittleEndian(field[0x12..]);
            float min = BinaryPrimitives.ReadSingleLittleEndian(field[0x14..]);
            float max = BinaryPrimitives.ReadSingleLittleEndian(field[0x18..]);

            // Masked, because a bit this build cannot honour must not be acted
            // on by accident. The writer refuses to set one; this is the other
            // half of that guarantee, for a file some other tool wrote.
            uint flags = KeyvalueFlags.Mask(BinaryPrimitives.ReadUInt32LittleEndian(field[0x1C..]));

            position += KeyvalueRecordSize;

            IReadOnlyList<(string Value, string Display)> choices;
            if (choiceCount == 0)
            {
                choices = KeyvalueDescriptor.NoChoices;
            }
            else
            {
                var list = new (string Value, string Display)[choiceCount];
                for (int c = 0; c < choiceCount; c++)
                {
                    RequireRoom(record.Length, position, ChoiceRecordSize, className, "choice record", recordOffset);
                    ReadOnlySpan<byte> choice = record.Slice(position, ChoiceRecordSize);
                    list[c] = (
                        ReadString(choice, table, decoded, 0, recordOffset + position, index),
                        ReadString(choice, table, decoded, 4, recordOffset + position, index));
                    position += ChoiceRecordSize;
                }

                choices = list;
            }

            keyvalues[i] = new KeyvalueDescriptor(
                name, display, tooltip, @default, (KeyvalueType)type, widget, min, max, flags, choices);
        }

        var inputs = new string[inputCount];
        for (int i = 0; i < inputCount; i++, position += StringRefSize)
        {
            RequireRoom(record.Length, position, StringRefSize, className, "input record", recordOffset);
            inputs[i] = ReadString(record, table, decoded, position, recordOffset, index);
        }

        var outputs = new string[outputCount];
        for (int i = 0; i < outputCount; i++, position += StringRefSize)
        {
            RequireRoom(record.Length, position, StringRefSize, className, "output record", recordOffset);
            outputs[i] = ReadString(record, table, decoded, position, recordOffset, index);
        }

        // Whatever is left is a newer writer's trailing fields and is skipped by
        // the caller's advance-by-RecordSize. Nothing is read from it and
        // nothing complains about it: that is what forward compatible means.
        return new EntitySchema(
            className, displayName, group, (EntityPlacement)placement, (EntityOrigin)origin,
            keyvalues, inputs, outputs);
    }

    private static void RequireRoom(
        int recordLength, int position, int needed, string className, string what, long recordOffset)
    {
        if (recordLength - position >= needed)
            return;

        throw new SentDefFormatException(
            $"Class '{className}' declares more content than its record holds: a {what} needs {needed} " +
            $"bytes at offset {position} of a {recordLength}-byte record.",
            recordOffset + position);
    }

    // Decoded strings are cached by table offset, so the deduplication the
    // writer did in the FILE is also deduplication in memory. Without this an
    // editor holding a game's definitions keeps one string object per reference
    // rather than one per distinct value, which for a group name repeated across
    // a hundred classes is a hundred copies of one word.
    private static string ReadString(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> table,
        Dictionary<uint, string> decoded,
        int fieldOffset,
        long absoluteFieldOffset,
        int typeIndex)
    {
        uint reference = BinaryPrimitives.ReadUInt32LittleEndian(source[fieldOffset..]);
        if (reference == 0)
            return string.Empty;

        if (decoded.TryGetValue(reference, out string? cached))
            return cached;

        if (reference > (uint)table.Length - sizeof(ushort))
        {
            throw new SentDefFormatException(
                $"Type record {typeIndex} references string offset {reference}, which is outside the " +
                $"{table.Length}-byte string table.",
                absoluteFieldOffset + fieldOffset);
        }

        ReadOnlySpan<byte> at = table[(int)reference..];
        int length = BinaryPrimitives.ReadUInt16LittleEndian(at);
        if (length > at.Length - sizeof(ushort))
        {
            throw new SentDefFormatException(
                $"The string at offset {reference} claims {length} bytes and the table has " +
                $"{at.Length - sizeof(ushort)} left.",
                absoluteFieldOffset + fieldOffset);
        }

        string text;
        try
        {
            text = Utf8.GetString(at.Slice(sizeof(ushort), length));
        }
        catch (DecoderFallbackException ex)
        {
            throw new SentDefFormatException(
                $"The string at offset {reference} is not valid UTF-8.", absoluteFieldOffset + fieldOffset, ex);
        }

        decoded.Add(reference, text);
        return text;
    }

    private static void RequireUInt16(int count, string what, string className)
    {
        if (count is >= 0 and <= ushort.MaxValue)
            return;

        throw new ArgumentException(
            $"Class '{className}' declares {count} {what}; the record stores that count as a u16.");
    }

    /// <summary>
    /// The deduplicated string table, built in one fixed traversal order.
    /// </summary>
    /// <remarks>
    /// <b>Ordinal comparison, and the empty string is seeded at offset 0.</b> A
    /// culture-aware comparer would fold two distinct names together on one
    /// machine and not on another, which is a class silently losing its display
    /// name in a file that still parses; and seeding the empty string is what
    /// lets every unset display, tooltip and default share one reference that a
    /// reader answers without touching the table.
    /// </remarks>
    private sealed class StringTableBuilder
    {
        private readonly Dictionary<string, uint> _offsets = new(StringComparer.Ordinal);
        private readonly List<byte> _bytes = [];

        public StringTableBuilder() => Intern(string.Empty);

        public int Length => _bytes.Count;

        public uint Intern(string? value)
        {
            string text = value ?? string.Empty;
            if (_offsets.TryGetValue(text, out uint existing))
                return existing;

            int byteCount = Utf8.GetByteCount(text);
            if (byteCount > ushort.MaxValue)
            {
                throw new ArgumentException(
                    $"A .sentdef string is length-prefixed with a u16 and this one is {byteCount} bytes: " +
                    $"'{text[..Math.Min(text.Length, 40)]}...'.");
            }

            uint offset = (uint)_bytes.Count;
            Span<byte> prefix = stackalloc byte[sizeof(ushort)];
            BinaryPrimitives.WriteUInt16LittleEndian(prefix, (ushort)byteCount);
            _bytes.Add(prefix[0]);
            _bytes.Add(prefix[1]);
            if (byteCount > 0)
                _bytes.AddRange(Utf8.GetBytes(text));

            _offsets.Add(text, offset);
            return offset;
        }

        public void CopyTo(Span<byte> destination) => CollectionsMarshal.AsSpan(_bytes).CopyTo(destination);
    }
}
