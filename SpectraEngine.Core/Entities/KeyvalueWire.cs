using System;
using System.Globalization;
using System.Numerics;

namespace SpectraEngine.Core.Entities;

/// <summary>
/// The one home for the string form of every <see cref="KeyvalueType"/>: how a
/// value is written, and how a written value is read back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keyvalues are string-typed on the wire</b>, the way an FGD and a VMF are:
/// a map carries text, a schema declares what that text MEANS, and the
/// conversion happens exactly once, when a value is bound to a live entity. That
/// is what lets a map naming a class this build has never heard of round-trip
/// byte for byte, and it is why a schema default is a string too.
/// </para>
/// <para>
/// <b>One home, because two would drift.</b> A map reader, a console command, a
/// property panel, an entity binder and a schema exporter all convert between
/// these two forms; the moment two of them spell a vector differently, a value
/// saved by one is refused by another, and the file looks corrupt.
/// </para>
/// <para>
/// <b>Everything is invariant, always.</b> A comma-decimal machine writing
/// <c>"1,5"</c> produces a file no other machine can read, and one reading
/// <c>"1.5"</c> with its own culture parses it as fifteen. Neither reports
/// anything. Every conversion here states
/// <see cref="CultureInfo.InvariantCulture"/> and the number styles exclude
/// group separators, so <c>"1,5"</c> is refused rather than silently accepted as
/// some other number.
/// </para>
/// <para>
/// <b>Format and TryParse agree on which values EXIST.</b> A non-finite float has
/// no wire form (the parse side refuses <c>NaN</c> and infinities, because a
/// keyvalue carrying one poisons every arithmetic it reaches), so formatting one
/// throws instead of writing text that cannot be read back.
/// </para>
/// <para>
/// <b>An overload per value type, not one entry point taking a
/// <see cref="KeyvalueType"/>.</b> Each type carries a different payload, so a
/// single method keyed on the enum would have to take <c>object</c> and box
/// every number the engine writes. The enum appears where a value's declared
/// type is all there is to go on: <see cref="IsWellFormed"/>, which is what a
/// reader and a property panel ask. The text types have no <c>Format</c> at all,
/// because their wire form IS the value.
/// </para>
/// </remarks>
public static class KeyvalueWire
{
    /// <summary>The wire form of a <see cref="KeyvalueType.Bool"/>.</summary>
    /// <remarks>
    /// <c>"1"</c> and <c>"0"</c>, never <c>"true"</c>: one spelling, so a value
    /// written by any producer round-trips to the same bytes.
    /// </remarks>
    public static string Format(bool value) => value ? "1" : "0";

    /// <summary>The wire form of a <see cref="KeyvalueType.Int"/>.</summary>
    public static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The wire form of a <see cref="KeyvalueType.Flags"/> bit set.</summary>
    public static string Format(uint value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The wire form of a <see cref="KeyvalueType.Float"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not finite.</exception>
    public static string Format(float value) => Finite(value).ToString(CultureInfo.InvariantCulture);

    /// <summary>The wire form of a <see cref="KeyvalueType.Vec2"/>: <c>"x y"</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A component is not finite.</exception>
    public static string Format(Vector2 value) =>
        string.Create(CultureInfo.InvariantCulture, $"{Finite(value.X)} {Finite(value.Y)}");

    /// <summary>The wire form of a <see cref="KeyvalueType.Vec3"/>: <c>"x y z"</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A component is not finite.</exception>
    public static string Format(Vector3 value) =>
        string.Create(CultureInfo.InvariantCulture, $"{Finite(value.X)} {Finite(value.Y)} {Finite(value.Z)}");

    /// <summary>The wire form of a <see cref="KeyvalueType.Vec4"/>: <c>"x y z w"</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A component is not finite.</exception>
    public static string Format(Vector4 value) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Finite(value.X)} {Finite(value.Y)} {Finite(value.Z)} {Finite(value.W)}");

    /// <summary>
    /// The wire form of a <see cref="KeyvalueType.Color"/>: three LINEAR floats,
    /// <c>"r g b"</c>.
    /// </summary>
    /// <remarks>
    /// <b>Linear, which is the same convention the map format already writes a
    /// light's colour in</b> (<c>MapLight.Color</c> is a linear triple, not a
    /// display colour). Everything the engine shades with is linear light, so a
    /// colour that arrives from a picker is converted once, at the edge, by
    /// <c>ColorSpace.SrgbToLinear</c>; storing the display value instead would
    /// make two files disagree about what the same three numbers mean.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">A component is not finite.</exception>
    public static string FormatColor(Vector3 linearColor) => Format(linearColor);

    /// <summary>
    /// The wire form of a <see cref="KeyvalueType.Angles"/>: three floats in
    /// DEGREES, <c>"pitch yaw roll"</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A component is not finite.</exception>
    public static string FormatAngles(Vector3 degrees) => Format(degrees);

    /// <summary>
    /// The wire form of a <see cref="KeyvalueType.NodeRef"/>: a
    /// <c>SceneNode.Id</c> in the hyphenated form.
    /// </summary>
    /// <remarks>
    /// Every value formats, <see cref="Guid.Empty"/> included, so the round trip
    /// has no exception in it. "No reference" is the EMPTY STRING, which nothing
    /// formats and which <see cref="IsWellFormed"/> accepts on its own terms: an
    /// unset reference and a reference to a node that is not there are different
    /// facts and must not share a spelling.
    /// </remarks>
    public static string Format(Guid nodeId) => nodeId.ToString("D");

    /// <summary>Reads a <see cref="KeyvalueType.Bool"/>.</summary>
    public static bool TryParseBool(string? text, out bool value)
    {
        ReadOnlySpan<char> token = Trim(text);
        if (token.Length == 1 && token[0] == '1')
        {
            value = true;
            return true;
        }

        value = false;
        return token.Length == 1 && token[0] == '0';
    }

    /// <summary>Reads a <see cref="KeyvalueType.Int"/>.</summary>
    public static bool TryParseInt(string? text, out int value) =>
        int.TryParse(Trim(text), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>Reads a <see cref="KeyvalueType.Flags"/> bit set.</summary>
    public static bool TryParseFlags(string? text, out uint value) =>
        uint.TryParse(Trim(text), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>Reads a finite <see cref="KeyvalueType.Float"/>.</summary>
    public static bool TryParseFloat(string? text, out float value) =>
        TryParseComponent(Trim(text), out value);

    /// <summary>Reads a <see cref="KeyvalueType.Vec2"/>.</summary>
    public static bool TryParseVec2(string? text, out Vector2 value)
    {
        Span<float> parts = stackalloc float[2];
        if (!TryReadFloats(text, parts))
        {
            value = default;
            return false;
        }

        value = new Vector2(parts[0], parts[1]);
        return true;
    }

    /// <summary>Reads a <see cref="KeyvalueType.Vec3"/>.</summary>
    public static bool TryParseVec3(string? text, out Vector3 value)
    {
        Span<float> parts = stackalloc float[3];
        if (!TryReadFloats(text, parts))
        {
            value = default;
            return false;
        }

        value = new Vector3(parts[0], parts[1], parts[2]);
        return true;
    }

    /// <summary>Reads a <see cref="KeyvalueType.Vec4"/>.</summary>
    public static bool TryParseVec4(string? text, out Vector4 value)
    {
        Span<float> parts = stackalloc float[4];
        if (!TryReadFloats(text, parts))
        {
            value = default;
            return false;
        }

        value = new Vector4(parts[0], parts[1], parts[2], parts[3]);
        return true;
    }

    /// <summary>Reads a <see cref="KeyvalueType.Color"/> as LINEAR RGB.</summary>
    public static bool TryParseColor(string? text, out Vector3 linearColor) =>
        TryParseVec3(text, out linearColor);

    /// <summary>Reads a <see cref="KeyvalueType.Angles"/> triple, in degrees.</summary>
    public static bool TryParseAngles(string? text, out Vector3 degrees) =>
        TryParseVec3(text, out degrees);

    /// <summary>
    /// Reads a <see cref="KeyvalueType.NodeRef"/>. The empty string means "no
    /// reference" and is refused here rather than yielding
    /// <see cref="Guid.Empty"/>, so a caller cannot confuse an unset reference
    /// with one pointing at a node that does not exist.
    /// </summary>
    public static bool TryParseNodeRef(string? text, out Guid nodeId) =>
        Guid.TryParseExact(Trim(text), "D", out nodeId);

    /// <summary>
    /// Whether <paramref name="text"/> is a value the declared
    /// <paramref name="type"/> can carry.
    /// </summary>
    /// <remarks>
    /// <b>The text types accept anything, including the empty string</b>, because
    /// their wire form IS the value: there is no spelling of a string that is
    /// malformed. A node reference is the one type where empty is legal without
    /// being parseable, since that is how "no reference" is written.
    /// </remarks>
    public static bool IsWellFormed(KeyvalueType type, string? text)
    {
        if (text is null)
            return false;

        switch (type)
        {
            case KeyvalueType.Bool:
                return TryParseBool(text, out _);
            case KeyvalueType.Int:
                return TryParseInt(text, out _);
            case KeyvalueType.Float:
                return TryParseFloat(text, out _);
            case KeyvalueType.Vec2:
                return TryParseVec2(text, out _);
            case KeyvalueType.Vec3:
            case KeyvalueType.Color:
            case KeyvalueType.Angles:
                return TryParseVec3(text, out _);
            case KeyvalueType.Vec4:
                return TryParseVec4(text, out _);
            case KeyvalueType.NodeRef:
                return text.Length == 0 || TryParseNodeRef(text, out _);
            case KeyvalueType.Flags:
                return TryParseFlags(text, out _);
            case KeyvalueType.String:
            case KeyvalueType.TargetName:
            case KeyvalueType.AssetModel:
            case KeyvalueType.AssetMaterial:
            case KeyvalueType.AssetTexture:
            case KeyvalueType.AssetSound:
            case KeyvalueType.Choices:
                return true;
            default:
                // A type this build does not know is not a value it can validate.
                // Refusing beats reporting a guess as a fact.
                return false;
        }
    }

    // Whitespace-separated components, read without allocating: a level's worth
    // of keyvalues is parsed on load and again on every property commit, and a
    // split array per vector is garbage the render thread would pay for.
    //
    // Strict in both directions. Too few components is a truncated value and too
    // many is a value of some other type, and accepting either would let a Vec2
    // written into a Vec3 field arrive with a zero somebody has to explain.
    private static bool TryReadFloats(string? text, Span<float> values)
    {
        if (text is null)
            return false;

        ReadOnlySpan<char> span = text;
        int written = 0;
        int i = 0;
        while (true)
        {
            while (i < span.Length && char.IsWhiteSpace(span[i]))
                i++;
            if (i >= span.Length)
                break;

            int start = i;
            while (i < span.Length && !char.IsWhiteSpace(span[i]))
                i++;

            if (written == values.Length)
                return false;
            if (!TryParseComponent(span[start..i], out float parsed))
                return false;

            values[written++] = parsed;
        }

        return written == values.Length;
    }

    // NumberStyles.Float deliberately excludes AllowThousands, so "1,5" is
    // refused rather than read as fifteen on a machine whose culture would have
    // written it that way.
    private static bool TryParseComponent(ReadOnlySpan<char> token, out float value)
    {
        if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return false;

        if (float.IsFinite(value))
            return true;

        value = 0f;
        return false;
    }

    private static ReadOnlySpan<char> Trim(string? text) => text is null ? default : text.AsSpan().Trim();

    private static float Finite(float value) => float.IsFinite(value)
        ? value
        : throw new ArgumentOutOfRangeException(
            nameof(value), value, "A keyvalue cannot carry a value that cannot be read back.");
}
