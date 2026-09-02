using SpectraEngine.Core;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Entities;
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
    /// kinds, an explicit texture axis, a light, an entity, nesting, and
    /// preserved members at four different levels.
    /// </summary>
    /// <remarks>
    /// <b>The <c>entity</c> member moved from the preserved side to the bound
    /// side, and this fixture changed with it deliberately.</b> It used to sit
    /// here on one line as an opaque unknown, which is what an unbound payload
    /// looks like; it is now the writer's own indented record. <c>script</c>
    /// takes over as the member that is carried and not decoded, because nothing
    /// in Core executes Luau.
    /// </remarks>
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
              "entity": {
                "class": "func_door",
                "keys": {"speed":"100"},
                "outputs": [
                  {"output":"OnFullyOpen","target":"Lamp","input":"TurnOn"},
                  {"output":"OnClose","target":"Lamp","input":"TurnOff","param":"1","delay":0.25,"times":2}
                ]
              },
              "script": {"module":false,"source":["local part = script.Parent"]},
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
        // 'script' sits between 'entity' and 'children' in the fixture.
        // Replaying preserved members at the END of the object satisfies every
        // rule the specification states and still produces different bytes from
        // the file that was read - which is precisely the case preservation
        // exists for, since a newer engine writes its own members interleaved
        // among ours.
        byte[] written = MapWriter.Write(MapReader.Read(Utf8(Canonical)));

        // The marker for "inside children" is the lamp's own 'light' record
        // rather than its name, because the doorway's wires TARGET the lamp by
        // name and that mention comes earlier in the file.
        string text = Encoding.UTF8.GetString(written);
        text.IndexOf("\"script\"", StringComparison.Ordinal)
            .ShouldBeGreaterThan(text.IndexOf("\"Doorway\"", StringComparison.Ordinal));
        text.IndexOf("\"script\"", StringComparison.Ordinal)
            .ShouldBeLessThan(text.IndexOf("\"light\"", StringComparison.Ordinal),
                "'script' was written before 'children', and must come back before it");
    }

    [Fact]
    public void An_unbuilt_payload_is_carried_rather_than_decoded()
    {
        MapDocument document = MapReader.Read(Utf8(Canonical));

        // 'script' is the payload with no engine concept behind it now: nothing
        // in Core executes Luau. 'entity' used to be here and is bound.
        MapNode doorway = document.Nodes[1];
        doorway.Unknown.Count.ShouldBe(1);
        doorway.Unknown[0].Name.ShouldBe("script");
        Encoding.UTF8.GetString(doorway.Unknown[0].Raw)
            .ShouldBe("""{"module":false,"source":["local part = script.Parent"]}""",
                "a script payload has no engine concept behind it yet, so it must ride through untouched");
    }

    [Fact]
    public void The_entity_payload_is_decoded_rather_than_carried()
    {
        // The deliberate oracle change. While 'entity' was preserved it survived
        // a document round trip and was deleted by any save that went through a
        // scene, because MapSceneBinder builds a fresh MapNode and nothing there
        // had ever seen it.
        MapDocument document = MapReader.Read(Utf8(Canonical));

        MapEntity entity = document.Nodes[1].Entity.ShouldNotBeNull();
        entity.Class.ShouldBe("func_door");
        entity.Keys.Count.ShouldBe(1);
        entity.Keys[0].Key.ShouldBe("speed");
        entity.Keys[0].Value.ShouldBe("100", "keyvalues are string-typed on the wire");
        entity.Unknown.ShouldBeEmpty();

        entity.Outputs.Count.ShouldBe(2);
        entity.Outputs[0].Output.ShouldBe("OnFullyOpen");
        entity.Outputs[0].Target.ShouldBe("Lamp");
        entity.Outputs[0].Input.ShouldBe("TurnOn");
        entity.Outputs[0].Param.ShouldBe("", "an omitted param is empty, not null");
        entity.Outputs[0].Delay.ShouldBe(0f);
        entity.Outputs[0].Times.ShouldBe(EntityConnection.Infinite,
            "an omitted 'times' is infinite, which is what almost every wire is");

        entity.Outputs[1].Param.ShouldBe("1");
        entity.Outputs[1].Delay.ShouldBe(0.25f);
        entity.Outputs[1].Times.ShouldBe(2);
    }

    [Fact]
    public void An_entity_record_with_no_class_is_refused()
    {
        // A record naming no class names no entity, the same refusal a 'mesh'
        // with no 'model' gets. The EMPTY string is a different fact and is
        // accepted, because EntityData models an entity carrying no class yet.
        var thrown = Should.Throw<MapFormatException>(() => MapReader.Read(Utf8("""
            {
              "spectramap": 3,
              "minimumReadableVersion": 3,
              "engine": "1.0.0",
              "scene": {
                "name": "S"
              },
              "nodes": [
                {
                  "id": "3f2a1c88-4b6d-4a19-9d0e-77c1f0a2b3e4",
                  "name": "Classless",
                  "transform": {"p":[0,0,0]},
                  "entity": {"keys":{"speed":"100"}},
                  "children": []
                }
              ]
            }
            """)));

        thrown.NodeName.ShouldBe("Classless");
        thrown.Message.ShouldContain("class");
    }

    [Fact]
    public void A_keyvalue_that_is_not_a_string_is_refused()
    {
        // Keyvalues are string-typed on the wire: a schema is what says whether
        // "100" is a speed or a count. A reader that accepted a bare number would
        // have to invent a spelling to write it back with.
        var thrown = Should.Throw<MapFormatException>(() => MapReader.Read(Utf8("""
            {
              "spectramap": 3,
              "minimumReadableVersion": 3,
              "engine": "1.0.0",
              "scene": {
                "name": "S"
              },
              "nodes": [
                {
                  "id": "3f2a1c88-4b6d-4a19-9d0e-77c1f0a2b3e4",
                  "name": "Numeric",
                  "transform": {"p":[0,0,0]},
                  "entity": {"class":"func_door","keys":{"speed":100}},
                  "children": []
                }
              ]
            }
            """)));

        thrown.NodeName.ShouldBe("Numeric");
        thrown.Message.ShouldContain("speed");
    }

    [Fact]
    public void An_entity_carrying_keys_outputs_and_an_unknown_member_round_trips_byte_for_byte()
    {
        // The entity record is OPEN, like a face and unlike a brush: a payload is
        // exactly where this format grows, and none of it changes what the solid
        // is. The unknown sits AFTER 'outputs', so it also pins that the anchor
        // survives a member the writer emits as a record array.
        byte[] source = Utf8("""
            {
              "spectramap": 3,
              "minimumReadableVersion": 3,
              "engine": "1.0.0",
              "scene": {
                "name": "Wired"
              },
              "nodes": [
                {
                  "id": "3f2a1c88-4b6d-4a19-9d0e-77c1f0a2b3e4",
                  "name": "Button",
                  "transform": {"p":[0,0,0]},
                  "entity": {
                    "class": "func_button",
                    "keys": {"wait":"3","spawnflags":"1024"},
                    "outputs": [
                      {"output":"OnPressed","target":"door_*","input":"Open"},
                      {"output":"OnPressed","target":"!activator","input":"Speak","param":"hello","delay":2,"times":1}
                    ],
                    "lightmapScale": 8
                  },
                  "children": []
                }
              ]
            }
            """);

        Same(source, MapWriter.Write(MapReader.Read(source)));
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
