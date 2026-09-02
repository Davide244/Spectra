using SpectraEngine.Core.Entities;
using System.Globalization;
using System.Numerics;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The string form of every keyvalue type, and the invariant-culture rule that
/// decides whether a map written on one machine can be read on another.
/// </summary>
/// <remarks>
/// <b>Every failure in here is silent.</b> A comma-decimal machine writing
/// <c>"1,5"</c> produces a file that is refused everywhere else; a reader using
/// the ambient culture reads <c>"1.5"</c> as fifteen. Neither throws, neither
/// logs, and the level is merely wrong.
/// </remarks>
public sealed class KeyvalueWireTests
{
    // Built rather than named, so the pin holds on a machine with no ICU data:
    // asking for "de-DE" in globalization-invariant mode succeeds and returns a
    // culture with a DOT separator, which would make this test pass vacuously.
    private static CultureInfo CommaDecimal()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberDecimalSeparator = ",";
        culture.NumberFormat.NumberGroupSeparator = ".";
        return culture;
    }

    [Fact]
    public void A_bool_is_one_or_zero_and_nothing_else()
    {
        KeyvalueWire.Format(true).ShouldBe("1");
        KeyvalueWire.Format(false).ShouldBe("0");

        KeyvalueWire.TryParseBool("1", out bool on).ShouldBeTrue();
        on.ShouldBeTrue();
        KeyvalueWire.TryParseBool("0", out bool off).ShouldBeTrue();
        off.ShouldBeFalse();

        // One spelling, so a value written by any producer round-trips to the
        // same bytes. "true" is a second spelling and is refused.
        KeyvalueWire.TryParseBool("true", out _).ShouldBeFalse();
        KeyvalueWire.TryParseBool("", out _).ShouldBeFalse();
        KeyvalueWire.TryParseBool(null, out _).ShouldBeFalse();
    }

    [Fact]
    public void An_int_round_trips_through_its_wire_form()
    {
        foreach (int value in new[] { 0, 1, -1, int.MinValue, int.MaxValue })
        {
            KeyvalueWire.TryParseInt(KeyvalueWire.Format(value), out int read).ShouldBeTrue();
            read.ShouldBe(value);
        }
    }

    [Fact]
    public void A_flags_word_round_trips_as_a_non_negative_integer()
    {
        KeyvalueWire.Format(0u).ShouldBe("0");
        KeyvalueWire.TryParseFlags(KeyvalueWire.Format(0b1011u), out uint bits).ShouldBeTrue();
        bits.ShouldBe(0b1011u);

        KeyvalueWire.TryParseFlags("-1", out _).ShouldBeFalse();
    }

    [Fact]
    public void A_float_round_trips_exactly()
    {
        // Exactly, not approximately: the wire form is shortest-round-trippable,
        // so a value that survives a save and a load is bit-identical and the
        // editor's "is this still the default?" comparison stays a string test.
        foreach (float value in new[] { 0f, 1f, -1.5f, 0.1f, 1e-8f, 3.4028235e38f })
        {
            KeyvalueWire.TryParseFloat(KeyvalueWire.Format(value), out float read).ShouldBeTrue();
            read.ShouldBe(value);
        }
    }

    [Fact]
    public void The_vector_types_round_trip_component_wise()
    {
        var v2 = new Vector2(1.5f, -2.25f);
        KeyvalueWire.Format(v2).ShouldBe("1.5 -2.25");
        KeyvalueWire.TryParseVec2("1.5 -2.25", out Vector2 read2).ShouldBeTrue();
        read2.ShouldBe(v2);

        var v3 = new Vector3(1.5f, -2.25f, 0f);
        KeyvalueWire.Format(v3).ShouldBe("1.5 -2.25 0");
        KeyvalueWire.TryParseVec3("1.5 -2.25 0", out Vector3 read3).ShouldBeTrue();
        read3.ShouldBe(v3);

        var v4 = new Vector4(1f, 2f, 3f, 4f);
        KeyvalueWire.Format(v4).ShouldBe("1 2 3 4");
        KeyvalueWire.TryParseVec4("1 2 3 4", out Vector4 read4).ShouldBeTrue();
        read4.ShouldBe(v4);
    }

    [Fact]
    public void A_colour_is_three_linear_floats_and_angles_are_three_degrees()
    {
        // Both are a Vec3 on the wire; what differs is what the numbers MEAN,
        // which is the descriptor's job to declare. The colour is linear, the
        // same convention the map format already writes a light's colour in.
        var linear = new Vector3(1f, 0.9114f, 0.7484f);
        KeyvalueWire.FormatColor(linear).ShouldBe("1 0.9114 0.7484");
        KeyvalueWire.TryParseColor("1 0.9114 0.7484", out Vector3 readColor).ShouldBeTrue();
        readColor.ShouldBe(linear);

        var degrees = new Vector3(-45f, 90f, 0f);
        KeyvalueWire.FormatAngles(degrees).ShouldBe("-45 90 0");
        KeyvalueWire.TryParseAngles("-45 90 0", out Vector3 readAngles).ShouldBeTrue();
        readAngles.ShouldBe(degrees);
    }

    [Fact]
    public void A_node_reference_round_trips_as_a_guid()
    {
        var id = Guid.NewGuid();

        KeyvalueWire.TryParseNodeRef(KeyvalueWire.Format(id), out Guid read).ShouldBeTrue();
        read.ShouldBe(id);

        // The empty string is "no reference", which is a different fact from a
        // reference to a node that is not there, so it is not a guid.
        KeyvalueWire.TryParseNodeRef("", out _).ShouldBeFalse();
        KeyvalueWire.IsWellFormed(KeyvalueType.NodeRef, "").ShouldBeTrue();
    }

    [Fact]
    public void The_text_types_accept_whatever_they_are_given()
    {
        // Their wire form IS the value: there is no spelling of a string that is
        // malformed, so validation must not invent one.
        foreach (KeyvalueType type in new[]
                 {
                     KeyvalueType.String, KeyvalueType.TargetName, KeyvalueType.Choices,
                     KeyvalueType.AssetModel, KeyvalueType.AssetMaterial,
                     KeyvalueType.AssetTexture, KeyvalueType.AssetSound,
                 })
        {
            KeyvalueWire.IsWellFormed(type, "").ShouldBeTrue();
            KeyvalueWire.IsWellFormed(type, "Models/crate.obj").ShouldBeTrue();
            KeyvalueWire.IsWellFormed(type, null).ShouldBeFalse();
        }
    }

    [Fact]
    public void A_vector_with_the_wrong_component_count_is_refused()
    {
        // Too few is a truncated value and too many is a value of some other
        // type; accepting either lets a Vec2 arrive in a Vec3 field with a zero
        // somebody then has to explain.
        KeyvalueWire.TryParseVec3("1 2", out _).ShouldBeFalse();
        KeyvalueWire.TryParseVec3("1 2 3 4", out _).ShouldBeFalse();
        KeyvalueWire.TryParseVec3("1 2 three", out _).ShouldBeFalse();

        // Extra whitespace is not a component.
        KeyvalueWire.TryParseVec3("  1   2\t3 ", out Vector3 read).ShouldBeTrue();
        read.ShouldBe(new Vector3(1f, 2f, 3f));
    }

    [Fact]
    public void A_non_finite_float_has_no_wire_form_at_either_end()
    {
        // Format and TryParse agree on which values exist. Writing text that
        // cannot be read back is how a value silently changes across a save.
        Should.Throw<ArgumentOutOfRangeException>(() => KeyvalueWire.Format(float.NaN));
        Should.Throw<ArgumentOutOfRangeException>(() => KeyvalueWire.Format(float.PositiveInfinity));
        Should.Throw<ArgumentOutOfRangeException>(
            () => KeyvalueWire.Format(new Vector3(0f, float.NaN, 0f)));

        KeyvalueWire.TryParseFloat("NaN", out _).ShouldBeFalse();
        KeyvalueWire.TryParseFloat("Infinity", out _).ShouldBeFalse();
        KeyvalueWire.TryParseVec3("1 NaN 3", out _).ShouldBeFalse();
    }

    [Fact]
    public void Numbers_are_written_and_read_invariantly_under_a_comma_decimal_culture()
    {
        // The pin. Under this culture the ambient ToString would write "1,5" and
        // the ambient Parse would read "1.5" as fifteen, and a map saved on this
        // machine would be unreadable on any other.
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CommaDecimal();

            1.5f.ToString(CultureInfo.CurrentCulture).ShouldBe("1,5");

            KeyvalueWire.Format(1.5f).ShouldBe("1.5");
            KeyvalueWire.Format(new Vector3(1.5f, -0.25f, 1000f)).ShouldBe("1.5 -0.25 1000");

            KeyvalueWire.TryParseFloat("1.5", out float read).ShouldBeTrue();
            read.ShouldBe(1.5f);

            KeyvalueWire.TryParseVec3("1.5 -0.25 1000", out Vector3 readVector).ShouldBeTrue();
            readVector.ShouldBe(new Vector3(1.5f, -0.25f, 1000f));

            // And the culture's own spelling is refused rather than read as some
            // other number: the group separator is not in the accepted styles.
            KeyvalueWire.TryParseFloat("1,5", out _).ShouldBeFalse();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
