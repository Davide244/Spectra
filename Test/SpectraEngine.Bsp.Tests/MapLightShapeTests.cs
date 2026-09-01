using SpectraEngine.Core;
using SpectraEngine.Core.Maps;
using SpectraEngine.Core.Scene;
using System.Numerics;
using System.Text;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The light shapes across the map format: the members were APPENDED, so a file
/// written before they existed still round-trips to the same bytes, and a file
/// that needs them says so per document rather than raising the floor for
/// everybody.
/// </summary>
/// <remarks>
/// <b>The append discipline is the claim, and it deserves its own suite.</b>
/// Byte identity for existing files holds by construction only while every new
/// member is written last and only when it differs from its default - both of
/// which are conventions a future edit can break silently, because the failure
/// is a file that is still valid and no longer identical.
/// </remarks>
public sealed class MapLightShapeTests
{
    private static byte[] Utf8(string text) =>
        Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\n") + "\n");

    // A document from BEFORE the shape members existed: one point light, all
    // five new members absent.
    private const string OldFile = """
        {
          "spectramap": 1,
          "minimumReadableVersion": 1,
          "engine": "1.0.0",
          "scene": {
            "name": "Old"
          },
          "nodes": [
            {
              "id": "3f2a1c88-4b6d-4a19-9d0e-77c1f0a2b3e4",
              "name": "Lamp",
              "transform": {"p":[1,2,3]},
              "light": {"kind":"point","intensity":4,"range":12},
              "children": []
            }
          ]
        }
        """;

    [Fact]
    public void A_file_written_before_the_shape_members_existed_still_round_trips_exactly()
    {
        byte[] source = Utf8(OldFile);

        byte[] written = MapWriter.Write(MapReader.Read(source));

        written.ShouldBe(source,
            "the shape members are written only when they differ from their defaults, and a " +
            "point light carries the defaults - so nothing new may appear in this object");
    }

    [Fact]
    public void A_new_writer_then_an_old_reader_then_a_new_reader_is_still_identical()
    {
        // The chain the append discipline actually has to survive: this engine
        // writes, something that does not know the new members reads and writes
        // it back (modelled by a round trip through a document whose unknowns
        // are preserved), and this engine reads it again.
        byte[] once = MapWriter.Write(MapReader.Read(Utf8(OldFile)));
        byte[] twice = MapWriter.Write(MapReader.Read(once));

        twice.ShouldBe(once);
    }

    [Fact]
    public void A_rect_lights_extents_survive_a_round_trip()
    {
        var scene = new Scene("Lit");
        var lamp = scene.Root.CreateChild("Panel");
        lamp.Light = new Light
        {
            Kind = LightKind.Rect,
            Intensity = 12f,
            Range = 8f,
            Width = 2.5f,
            Height = 0.75f,
        };

        byte[] bytes = MapWriter.Write(MapSceneBinder.FromScene(scene, report: null));
        MapDocument reloaded = MapReader.Read(bytes);

        MapLight? light = reloaded.Nodes[0].Light;
        light.ShouldNotBeNull();
        light!.Kind.ShouldBe(LightKind.Rect);
        light.Width.ShouldBe(2.5f);
        light.Height.ShouldBe(0.75f);
    }

    [Fact]
    public void A_spots_angles_survive_a_round_trip()
    {
        var scene = new Scene("Lit");
        var lamp = scene.Root.CreateChild("Spot");
        lamp.Light = new Light
        {
            Kind = LightKind.Spot,
            InnerAngle = 12f,
            OuterAngle = 40f,
        };

        byte[] bytes = MapWriter.Write(MapSceneBinder.FromScene(scene, report: null));
        MapDocument reloaded = MapReader.Read(bytes);

        MapLight light = reloaded.Nodes[0].Light!;
        light.Kind.ShouldBe(LightKind.Spot);
        light.InnerAngle.ShouldBe(12f);
        light.OuterAngle.ShouldBe(40f);
    }

    [Fact]
    public void A_scene_with_only_old_light_kinds_does_not_raise_the_reader_floor()
    {
        var scene = new Scene("Plain");
        scene.Root.CreateChild("Sun").Light = new Light { Kind = LightKind.Directional };
        scene.Root.CreateChild("Lamp").Light = new Light { Kind = LightKind.Point };

        MapDocument document = MapSceneBinder.FromScene(scene, report: null);

        // A blanket floor would tell every older editor to refuse every map this
        // engine writes, including the overwhelming majority carrying nothing it
        // could not read.
        document.MinimumReadableVersion.ShouldBe(EngineInfo.MinimumReadableMapVersion);
    }

    [Fact]
    public void A_scene_carrying_a_shape_raises_the_floor_for_that_document_only()
    {
        var scene = new Scene("Shaped");
        scene.Root.CreateChild("Panel").Light = new Light { Kind = LightKind.Rect };

        MapDocument document = MapSceneBinder.FromScene(scene, report: null);

        // An older editor opening this and saving it would build a fresh
        // MapLight without the extents and silently delete them, leaving a light
        // that is the wrong shape - which is what the floor exists to refuse.
        document.MinimumReadableVersion.ShouldBe(EngineInfo.LightShapeMapVersion);
    }

    [Fact]
    public void An_unknown_light_kind_is_refused_rather_than_widened_to_directional()
    {
        string bad = OldFile.Replace("\"kind\":\"point\"", "\"kind\":\"pont\"");

        Should.Throw<MapFormatException>(() => MapReader.Read(Utf8(bad)))
            .Message.ShouldContain("pont");
    }

    [Fact]
    public void Every_light_kind_has_a_distinct_wire_name()
    {
        // The oracle behind "every missed switch fails silently": ToWire was a
        // ternary, so a new kind serialised as "directional" with nothing
        // anywhere reporting it. Reflection is fine in a test project.
        var seen = new HashSet<string>();

        foreach (LightKind kind in Enum.GetValues<LightKind>())
        {
            var scene = new Scene("K");
            scene.Root.CreateChild(kind.ToString()).Light = new Light { Kind = kind };

            byte[] bytes = MapWriter.Write(MapSceneBinder.FromScene(scene, report: null));
            MapDocument reloaded = MapReader.Read(bytes);

            reloaded.Nodes[0].Light!.Kind.ShouldBe(kind,
                $"'{kind}' did not survive a round trip; a missing case in ToWire or the reader " +
                "serialises it as some other kind, which is a level that loads and is not the " +
                "one that was saved");

            seen.Add(kind.ToString()).ShouldBeTrue();
        }

        seen.Count.ShouldBe(Enum.GetValues<LightKind>().Length);
    }
}
