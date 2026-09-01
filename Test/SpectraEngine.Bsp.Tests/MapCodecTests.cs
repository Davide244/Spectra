using SpectraEngine.Core;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Maps;
using SpectraEngine.Core.Scene;
using SpectraEngine.Core.Serialization;
using System;
using System.Numerics;
using System.Text;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The authored map document survives a round trip byte for byte, and refuses
/// the edits that would change the world silently.
/// </summary>
/// <remarks>
/// <para>
/// <b>Byte identity is the feature, not tidiness.</b> A <c>.smap</c> bundle is
/// a folder of text a person opens in VS Code, edits, and commits: the promise
/// is that a two-line hand edit shows up in git as two lines, and that the
/// editor's next save does not stomp it. A writer that reorders members, or
/// emits CRLF on one platform and LF on another, or herds preserved members to
/// the end of the object, keeps every stated rule and breaks that promise on
/// the first pull request.
/// </para>
/// <para>
/// <b>These tests are about the DOCUMENT, never the scene.</b> The two round
/// trips are separate claims for a concrete reason: <c>Brush</c>'s constructor
/// re-normalises every plane it is given, so a scene cannot promise to
/// reproduce the bytes it was built from and the document can. Mixing them
/// would make a canonicalisation look like a codec bug.
/// </para>
/// </remarks>
public sealed class MapCodecTests
{
    /// <summary>
    /// A canonical document, hand-authored so this file pins the shape rather
    /// than describing whatever the writer currently produces. Every construct
    /// the codec knows appears at least once: both brush signs, both node
    /// kinds, an explicit texture axis, a light, nesting, and preserved members
    /// at four different levels.
    /// </summary>
    private const string Canonical = """
        {
          "spectramap": 1,
          "minimumReadableVersion": 1,
          "engine": "1.0.0",
          "scene": {
            "name": "Testmap",
            "spawn": {"p":[0,64,0],"r":[0,0,0,1]}
          },
          "editor": {"grid":1},
          "nodes": [
            {
              "id": "3f2a1c88-4b6d-4a19-9d0e-77c1f0a2b3e4",
              "name": "Wall",
              "transform": {"p":[4,0,-2]},
              "brush": {
                "planes": [
                  [1,0,0,-32],
                  [-1,0,0,-32],
                  [0,1,0,-8],
                  [0,-1,0,-8],
                  [0,0,1,-2],
                  [0,0,-1,-2]
                ],
                "faces": [
                  {},
                  {"material":"Materials/wall.spectramat"},
                  {"material":"Materials/wall.spectramat","u":[0,0,1],"v":[0,1,0],"uo":0.5,"vs":2},
                  {},
                  {},
                  {}
                ]
              },
              "children": []
            },
            {
              "id": "9c1e4d70-2a83-4f16-b5aa-0e6d3c8f21b7",
              "name": "Doorway",
              "kind": "part",
              "transform": {"p":[0,0,0],"r":[0,0.7071068,0,0.7071068],"s":[2,2,2]},
              "brush": {
                "operation": "subtractive",
                "planes": [
                  [1,0,0,-1],
                  [-1,0,0,-1],
                  [0,1,0,-1],
                  [0,-1,0,-1],
                  [0,0,1,-1],
                  [0,0,-1,-1]
                ],
                "faces": [
                  {},
                  {},
                  {},
                  {},
                  {},
                  {}
                ]
              },
              "entity": {"class":"func_door","keys":{"speed":"100"}},
              "children": [
                {
                  "id": "5b2f8a11-6c04-4e29-8d73-1af90b4e2c65",
                  "name": "Lamp",
                  "transform": {"p":[0,3,0]},
                  "light": {"kind":"point","color":[1,0.9,0.8],"intensity":40,"range":12},
                  "children": []
                }
              ]
            }
          ]
        }
        """;

    // -- the promise ---------------------------------------------------------

    [Fact]
    public void A_canonical_document_survives_a_read_and_a_write_byte_for_byte()
    {
        byte[] source = Utf8(Canonical);

        byte[] written = MapWriter.Write(MapReader.Read(source));

        Same(source, written);
    }

    [Fact]
    public void A_document_this_engine_wrote_reads_back_to_the_same_bytes()
    {
        // The weaker save/load/save form, which is what the format specification
        // actually pins. It is kept alongside the exact-bytes test because it
        // stays meaningful if the canonical shape is ever deliberately changed,
        // and it would catch a writer that is merely inconsistent with itself.
        byte[] once = MapWriter.Write(MapReader.Read(Utf8(Canonical)));

        byte[] twice = MapWriter.Write(MapReader.Read(once));

        Same(once, twice);
    }

    [Fact]
    public void The_writer_never_emits_a_carriage_return()
    {
        // Utf8JsonWriter.NewLine defaults to Environment.NewLine, so a writer
        // that left it alone would produce a different file on Windows than on
        // Linux and byte identity would hold only within one operating system.
        // Nothing about the output would look wrong on either.
        byte[] written = MapWriter.Write(MapReader.Read(Utf8(Canonical)));

        Array.IndexOf(written, (byte)'\r').ShouldBe(-1,
            "a map is diffed across a team, so the line ending cannot depend on who saved it");
    }

    [Fact]
    public void The_writer_emits_no_byte_order_mark()
    {
        byte[] written = MapWriter.Write(new MapDocument());

        written[0].ShouldBe((byte)'{', "a BOM would make every tool that reads the file guess");
    }

    // -- preservation --------------------------------------------------------

    [Fact]
    public void An_unrecognised_member_comes_back_exactly_where_it_was()
    {
        // 'entity' sits between 'brush' and 'children' in the fixture. Replaying
        // preserved members at the END of the object satisfies every rule the
        // specification states and still produces different bytes from the file
        // that was read - which is precisely the case preservation exists for,
        // since a newer engine writes its own members interleaved among ours.
        byte[] written = MapWriter.Write(MapReader.Read(Utf8(Canonical)));

        string text = Encoding.UTF8.GetString(written);
        text.IndexOf("\"entity\"", StringComparison.Ordinal)
            .ShouldBeGreaterThan(text.IndexOf("\"Doorway\"", StringComparison.Ordinal));
        text.IndexOf("\"entity\"", StringComparison.Ordinal)
            .ShouldBeLessThan(text.IndexOf("\"Lamp\"", StringComparison.Ordinal),
                "'entity' was written before 'children', and must come back before it");
    }

    [Fact]
    public void An_unbuilt_payload_is_carried_rather_than_decoded()
    {
        MapDocument document = MapReader.Read(Utf8(Canonical));

        MapNode doorway = document.Nodes[1];
        doorway.Unknown.Count.ShouldBe(1);
        doorway.Unknown[0].Name.ShouldBe("entity");
        Encoding.UTF8.GetString(doorway.Unknown[0].Raw)
            .ShouldBe("""{"class":"func_door","keys":{"speed":"100"}}""",
                "an entity payload has no engine concept behind it yet, so it must ride through untouched");
    }

    [Fact]
    public void A_reserved_key_is_carried_by_name_rather_than_as_an_unknown()
    {
        MapDocument document = MapReader.Read(Utf8(Canonical));

        // 'editor' is reserved so the cook can drop it wholesale by name without
        // inspecting it, which it could not do if the key were swept into the
        // same bag as members a future engine needs.
        document.Editor.ShouldNotBeNull();
        Encoding.UTF8.GetString(document.Editor!.Raw).ShouldBe("""{"grid":1}""");
        document.Unknown.ShouldBeEmpty();
    }

    [Fact]
    public void A_scene_member_with_no_engine_concept_still_round_trips()
    {
        MapDocument document = MapReader.Read(Utf8(Canonical));

        // Nothing on Scene carries a gameplay spawn, so decoding it would
        // produce a value that means nothing. Preserving it keeps the map
        // truthful through an engine that cannot use it.
        document.Scene.Unknown.Count.ShouldBe(1);
        document.Scene.Unknown[0].Name.ShouldBe("spawn");
    }

    // -- what it decodes -----------------------------------------------------

    [Fact]
    public void The_document_decodes_to_the_values_it_states()
    {
        MapDocument document = MapReader.Read(Utf8(Canonical));

        // The value the FIXTURE states, not this engine's current constant. The
        // two happened to be equal while the format had only ever had one
        // version, which made this assertion a constant compared against itself;
        // reading a document is supposed to report what the document says.
        document.FormatVersion.ShouldBe(1);
        document.Scene.Name.ShouldBe("Testmap");
        document.Nodes.Count.ShouldBe(2);

        MapNode wall = document.Nodes[0];
        wall.Id.ShouldBe(Guid.Parse("3f2a1c88-4b6d-4a19-9d0e-77c1f0a2b3e4"));
        wall.Name.ShouldBe("Wall");
        wall.Kind.ShouldBeNull("an omitted kind is not a declared World, it is no declaration at all");
        wall.Transform.Position.ShouldBe(new Vector3(4f, 0f, -2f));
        wall.Transform.Scale.ShouldBe(Vector3.One, "an omitted scale is identity, never default(Transform)'s zero");
        wall.Brush.ShouldNotBeNull();
        wall.Brush!.Operation.ShouldBe(BrushOperation.Additive);
        wall.Brush.Planes.Count.ShouldBe(6);
        wall.Brush.Faces.Count.ShouldBe(6);
        wall.Brush.Faces[2].UAxis.ShouldBe(new Vector3(0f, 0f, 1f));
        wall.Brush.Faces[2].UOffset.ShouldBe(0.5f);
        wall.Brush.Faces[2].VScale.ShouldBe(2f);
        wall.Brush.Faces[0].UScale.ShouldBe(1f, "a fully default face means scale 1, not the struct default's 0");

        MapNode doorway = document.Nodes[1];
        doorway.Kind.ShouldBe(BrushKind.Part);
        doorway.Brush!.Operation.ShouldBe(BrushOperation.Subtractive);
        doorway.Children.Count.ShouldBe(1);

        MapLight lamp = doorway.Children[0].Light.ShouldNotBeNull();
        lamp.Kind.ShouldBe(LightKind.Point);
        lamp.Intensity.ShouldBe(40f);
        lamp.Range.ShouldBe(12f);
        lamp.Enabled.ShouldBeTrue("an omitted 'enabled' means on");
    }

    [Fact]
    public void An_omitted_light_range_is_ten_rather_than_zero()
    {
        // Light.Range refuses anything not strictly positive, so defaulting a
        // missing number to 0f throws out of the property setter halfway
        // through a load - with a message about a value, naming no node.
        MapDocument document = MapReader.Read(Utf8("""
            {
              "spectramap": 1,
              "minimumReadableVersion": 1,
              "engine": "1.0.0",
              "scene": {
                "name": "S"
              },
              "nodes": [
                {
                  "id": "3f2a1c88-4b6d-4a19-9d0e-77c1f0a2b3e4",
                  "name": "Sun",
                  "transform": {"p":[0,0,0]},
                  "light": {"intensity":3},
                  "children": []
                }
              ]
            }
            """));

        document.Nodes[0].Light!.Range.ShouldBe(10f);
    }

    // -- refusals ------------------------------------------------------------

    [Theory]
    // A mistyped kind falling through to World re-admits a simulated brush to
    // the carve: a world topology change on load, not a lost setting.
    [InlineData("\"kind\": \"prt\"", "prt")]
    // A mistyped operation falling through to Additive turns a doorway into a
    // wall.
    [InlineData("\"kind\": \"part\"", null)]
    public void A_value_outside_a_closed_vocabulary_is_refused_by_name(string member, string? bad)
    {
        string text = $$"""
            {
              "spectramap": 1,
              "minimumReadableVersion": 1,
              "engine": "1.0.0",
              "scene": {
                "name": "S"
              },
              "nodes": [
                {
                  "id": "3f2a1c88-4b6d-4a19-9d0e-77c1f0a2b3e4",
                  "name": "Suspect",
                  {{member}},
                  "transform": {"p":[0,0,0]},
                  "children": []
                }
              ]
            }
            """;

        if (bad is null)
        {
            Should.NotThrow(() => MapReader.Read(Utf8(text)));
            return;
        }

        var thrown = Should.Throw<MapFormatException>(() => MapReader.Read(Utf8(text)));
        thrown.NodeName.ShouldBe("Suspect", "the first bad map is undebuggable without the node name");
        thrown.ByteOffset.ShouldBeGreaterThan(0);
        thrown.Message.ShouldContain(bad);
    }

    [Fact]
    public void A_face_list_that_does_not_match_the_planes_is_refused()
    {
        // 'faces' is indexed BY PLANE INDEX, which is the whole convention. A
        // reader that padded the list out would texture the wrong faces.
        var thrown = Should.Throw<MapFormatException>(() => MapReader.Read(Utf8("""
            {
              "spectramap": 1,
              "minimumReadableVersion": 1,
              "engine": "1.0.0",
              "scene": {
                "name": "S"
              },
              "nodes": [
                {
                  "id": "3f2a1c88-4b6d-4a19-9d0e-77c1f0a2b3e4",
                  "name": "Lopsided",
                  "transform": {"p":[0,0,0]},
                  "brush": {
                    "planes": [[1,0,0,-1],[-1,0,0,-1]],
                    "faces": [{}]
                  },
                  "children": []
                }
              ]
            }
            """)));

        thrown.NodeName.ShouldBe("Lopsided");
        thrown.Message.ShouldContain("2");
    }

    [Fact]
    public void An_unrecognised_brush_member_is_refused_rather_than_preserved()
    {
        // Unlike a face record. Every member of a brush is load-bearing
        // geometry, so one quietly carried and ignored is a wall where a
        // doorway was.
        var thrown = Should.Throw<MapFormatException>(() => MapReader.Read(Utf8("""
            {
              "spectramap": 1,
              "minimumReadableVersion": 1,
              "engine": "1.0.0",
              "scene": {
                "name": "S"
              },
              "nodes": [
                {
                  "id": "3f2a1c88-4b6d-4a19-9d0e-77c1f0a2b3e4",
                  "name": "Odd",
                  "transform": {"p":[0,0,0]},
                  "brush": {
                    "planes": [[1,0,0,-1]],
                    "faces": [{}],
                    "bevel": true
                  },
                  "children": []
                }
              ]
            }
            """)));

        thrown.NodeName.ShouldBe("Odd");
        thrown.Message.ShouldContain("bevel");
    }

    [Fact]
    public void A_document_that_needs_a_newer_reader_is_refused_loudly()
    {
        var thrown = Should.Throw<MapFormatException>(() => MapReader.Read(Utf8("""
            {
              "spectramap": 7,
              "minimumReadableVersion": 7,
              "engine": "9.9.9",
              "scene": {
                "name": "S"
              },
              "nodes": []
            }
            """)));

        thrown.Message.ShouldContain("7");
    }

    [Fact]
    public void A_newer_document_this_reader_can_still_handle_is_accepted()
    {
        // The asymmetry is the point: unknown members are carried, so "newer"
        // is survivable; minimumReadableVersion is the author's own statement
        // that it is not.
        MapDocument document = MapReader.Read(Utf8("""
            {
              "spectramap": 9,
              "minimumReadableVersion": 1,
              "engine": "9.9.9",
              "scene": {
                "name": "S"
              },
              "nodes": []
            }
            """));

        document.FormatVersion.ShouldBe(9);
    }

    // -- helpers -------------------------------------------------------------

    private static byte[] Utf8(string text) =>
        Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\n") + "\n");

    private static void Same(byte[] expected, byte[] actual)
    {
        if (expected.AsSpan().SequenceEqual(actual)) return;

        string want = Encoding.UTF8.GetString(expected);
        string got = Encoding.UTF8.GetString(actual);
        int at = 0;
        while (at < want.Length && at < got.Length && want[at] == got[at]) at++;

        throw new Xunit.Sdk.XunitException(
            $"The document changed on the way through, first at character {at}.\n"
            + $"--- expected ---\n{want}\n--- actual ---\n{got}");
    }
}
