using Microsoft.CodeAnalysis;

namespace SpectraEngine.Entities.Generator;

/// <summary>
/// The C# types a keyvalue can be stored in, as a closed set this generator
/// recognises.
/// </summary>
internal enum ClrKind
{
    /// <summary>Nothing this generator can bind.</summary>
    Unknown = 0,
    Bool,
    Int,
    UInt,
    Float,
    String,
    Vector2,
    Vector3,
    Vector4,
    Guid,
}

/// <summary>
/// One row of the binding table: a <c>KeyvalueType</c>, the C# type that carries
/// it, and the <c>KeyvalueWire</c> method that reads it.
/// </summary>
/// <param name="Name">The <c>KeyvalueType</c> member name, for the emitted schema.</param>
/// <param name="Value">Its frozen wire byte.</param>
/// <param name="Clr">Which C# type carries it.</param>
/// <param name="CSharpType">That type, spelled as the emitted code spells it.</param>
/// <param name="Reader">
/// The <c>KeyvalueWire</c> reader, or null when the wire form IS the value.
/// </param>
internal readonly record struct KeyvalueRow(
    string Name,
    byte Value,
    ClrKind Clr,
    string CSharpType,
    string? Reader);

/// <summary>
/// The one table joining a <c>KeyvalueType</c> to the C# type that carries it
/// and the <c>KeyvalueWire</c> method that reads it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hand-written, and it mirrors an enum this assembly cannot see.</b> The
/// generator matches Core's attributes by metadata name and never references
/// Core, so <c>KeyvalueType</c>'s numbering is transcribed here. That numbering
/// is frozen and append-only for the same reason it is frozen there (it is a
/// wire byte), so this table grows at the end and never renumbers.
/// </para>
/// <para>
/// <b>Inference is one-way and deliberately narrow.</b> Nine C# types map to a
/// primary <c>KeyvalueType</c> each; the kinds that SHARE a C# type -
/// <c>Color</c> and <c>Angles</c> both live in a <c>Vector3</c>, every asset
/// path and <c>TargetName</c> live in a <c>string</c> - have no inference at all
/// and must be stated. Guessing between them would silently give a property
/// panel a colour picker for a rotation, and there is nothing in the member's
/// type to guess from.
/// </para>
/// </remarks>
internal static class KeyvalueBinding
{
    /// <summary>The <c>KeyvalueType.NodeRef</c> value, whose empty form reads specially.</summary>
    public const byte NodeRefValue = 10;

    private static readonly KeyvalueRow[] Rows =
    [
        new KeyvalueRow("Bool", 0, ClrKind.Bool, "bool", "TryParseBool"),
        new KeyvalueRow("Int", 1, ClrKind.Int, "int", "TryParseInt"),
        new KeyvalueRow("Float", 2, ClrKind.Float, "float", "TryParseFloat"),
        new KeyvalueRow("String", 3, ClrKind.String, "string", null),
        new KeyvalueRow("Vec2", 4, ClrKind.Vector2, "global::System.Numerics.Vector2", "TryParseVec2"),
        new KeyvalueRow("Vec3", 5, ClrKind.Vector3, "global::System.Numerics.Vector3", "TryParseVec3"),
        new KeyvalueRow("Vec4", 6, ClrKind.Vector4, "global::System.Numerics.Vector4", "TryParseVec4"),
        new KeyvalueRow("Color", 7, ClrKind.Vector3, "global::System.Numerics.Vector3", "TryParseColor"),
        new KeyvalueRow("Angles", 8, ClrKind.Vector3, "global::System.Numerics.Vector3", "TryParseAngles"),
        new KeyvalueRow("TargetName", 9, ClrKind.String, "string", null),
        new KeyvalueRow("NodeRef", NodeRefValue, ClrKind.Guid, "global::System.Guid", "TryParseNodeRef"),
        new KeyvalueRow("AssetModel", 11, ClrKind.String, "string", null),
        new KeyvalueRow("AssetMaterial", 12, ClrKind.String, "string", null),
        new KeyvalueRow("AssetTexture", 13, ClrKind.String, "string", null),
        new KeyvalueRow("AssetSound", 14, ClrKind.String, "string", null),
        new KeyvalueRow("Choices", 15, ClrKind.String, "string", null),
        new KeyvalueRow("Flags", 16, ClrKind.UInt, "uint", "TryParseFlags"),
    ];

    /// <summary>The row for <paramref name="value"/>, or false when nothing names it.</summary>
    public static bool TryGet(byte value, out KeyvalueRow row)
    {
        for (int i = 0; i < Rows.Length; i++)
        {
            if (Rows[i].Value == value)
            {
                row = Rows[i];
                return true;
            }
        }

        row = default;
        return false;
    }

    /// <summary>
    /// The <c>KeyvalueType</c> a member of <paramref name="kind"/> gets when the
    /// author states none, or false when nothing is inferred from it.
    /// </summary>
    public static bool TryInfer(ClrKind kind, out KeyvalueRow row)
    {
        // The FIRST row for the kind, which is why the table is ordered with each
        // C# type's primary meaning ahead of its alternatives: Vec3 before Color
        // and Angles, String before TargetName and the asset paths.
        for (int i = 0; i < Rows.Length; i++)
        {
            if (Rows[i].Clr == kind)
            {
                row = Rows[i];
                return true;
            }
        }

        row = default;
        return false;
    }

    /// <summary>What C# type <paramref name="type"/> is, as far as a keyvalue is concerned.</summary>
    public static ClrKind Classify(ITypeSymbol type)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
                return ClrKind.Bool;
            case SpecialType.System_Int32:
                return ClrKind.Int;
            case SpecialType.System_UInt32:
                return ClrKind.UInt;
            case SpecialType.System_Single:
                return ClrKind.Float;
            case SpecialType.System_String:
                return ClrKind.String;
        }

        // Compared by full name rather than by symbol identity: the transform
        // holds no compilation to look the framework's own types up in, and a
        // name is exactly as precise for four types that only the framework
        // declares.
        switch (type.ToDisplayString())
        {
            case "System.Numerics.Vector2":
                return ClrKind.Vector2;
            case "System.Numerics.Vector3":
                return ClrKind.Vector3;
            case "System.Numerics.Vector4":
                return ClrKind.Vector4;
            case "System.Guid":
                return ClrKind.Guid;
            default:
                return ClrKind.Unknown;
        }
    }
}
