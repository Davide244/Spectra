using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Maps;
using SpectraEngine.Core.Scene;
using System;
using System.IO;
using System.Numerics;
using System.Text;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// A scene saved to a map and loaded back is the same scene, and a bundle on
/// disk behaves like a folder someone else also edits.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the lossy half of the round trip and it is tested separately from
/// the codec on purpose.</b> <c>Brush</c>'s constructor re-normalises every
/// plane, so a scene cannot promise to reproduce the bytes it was built from;
/// mixing the two claims into one test would make a canonicalisation look like
/// a codec bug and would hide whichever half actually broke.
/// </para>
/// </remarks>
public sealed class MapSceneRoundTripTests
{
    private static Scene NewScene()
    {
        var scene = new Scene("Testmap");
        return scene;
    }

    private static Brush Box(float half = 1f, MaterialRef material = default) =>
        Brush.CreateBox(new Vector3(-half), new Vector3(half), material);

    // -- the graph -----------------------------------------------------------

    [Fact]
    public void A_saved_scene_comes_back_with_the_same_identities_and_order()
    {
        Scene source = NewScene();
        for (int i = 0; i < 4; i++)
        {
            SceneNode node = source.Root.CreateChild($"Brush{i}");
            node.LocalPosition = new Vector3(i * 3f, 0f, 0f);
            node.Brush = Box();
        }
        SceneNode nested = source.Root.Children[1].CreateChild("Child");
        nested.LocalPosition = new Vector3(0f, 5f, 0f);

        Guid[] expectedIds = new Guid[4];
        for (int i = 0; i < 4; i++) expectedIds[i] = source.Root.Children[i].Id;
        Guid nestedId = nested.Id;

        Scene loaded = NewScene();
        MapSceneBinder.ApplyTo(MapReader.Read(MapWriter.Write(MapSceneBinder.FromScene(source))), loaded);

        loaded.Name.ShouldBe("Testmap");
        loaded.Root.Children.Count.ShouldBe(4);
        for (int i = 0; i < 4; i++)
        {
            // Ids, not just names: every editor command addresses nodes by id,
            // and an undo of a delete recreates the node under the same one.
            loaded.Root.Children[i].Id.ShouldBe(expectedIds[i]);
            loaded.Root.Children[i].Name.ShouldBe($"Brush{i}");
        }

        loaded.Root.Children[1].Children.Count.ShouldBe(1);
        loaded.Root.Children[1].Children[0].Id.ShouldBe(nestedId);
    }

    [Fact]
    public void A_loaded_node_is_findable_by_its_id()
    {
        // The id index is what every editor command resolves through, and it is
        // maintained from NodeAdded/NodeRemoved rather than by walking the
        // graph. A load builds each subtree DETACHED and attaches it in one
        // AddChild, so this asks whether that one event indexes the descendants
        // too - which the graph assertions above cannot see, because they read
        // the child lists directly.
        Scene source = NewScene();
        SceneNode parent = source.Root.CreateChild("Parent");
        SceneNode child = parent.CreateChild("Child");
        SceneNode grandchild = child.CreateChild("Grandchild");
        Guid parentId = parent.Id, childId = child.Id, grandchildId = grandchild.Id;

        Scene loaded = NewScene();
        MapSceneBinder.ApplyTo(MapReader.Read(MapWriter.Write(MapSceneBinder.FromScene(source))), loaded);

        loaded.TryFindById(parentId, out SceneNode? foundParent).ShouldBeTrue("root child");
        foundParent!.Name.ShouldBe("Parent");
        loaded.TryFindById(childId, out SceneNode? foundChild).ShouldBeTrue("one level down");
        foundChild!.Name.ShouldBe("Child");
        loaded.TryFindById(grandchildId, out SceneNode? foundGrandchild).ShouldBeTrue("two levels down");
        foundGrandchild!.Name.ShouldBe("Grandchild");
    }

    [Fact]
    public void Sibling_order_survives_a_round_trip()
    {
        // Child order is traversal order is static-world placement order, and
        // placement order breaks ties in the carve's overlap ordering. A load
        // that reordered siblings would rebuild a level that is valid,
        // different and bit-unequal - and every determinism test in the repo
        // builds its BrushPlacement array by hand, so none of them would see it.
        Scene source = NewScene();
        string[] names = ["Floor", "Wall", "Doorway", "Ceiling", "Trim"];
        foreach (string name in names)
            source.Root.CreateChild(name).Brush = Box();

        Scene loaded = NewScene();
        MapSceneBinder.ApplyTo(MapReader.Read(MapWriter.Write(MapSceneBinder.FromScene(source))), loaded);

        for (int i = 0; i < names.Length; i++)
            loaded.Root.Children[i].Name.ShouldBe(names[i]);
    }

    [Fact]
    public void Every_loaded_brush_is_its_own_instance()
    {
        // CsgCompileCache and PartBrushMeshCache both key on Brush reference
        // identity. Two nodes sharing one instance renders perfectly and makes
        // every duplicate past the first re-carve on every compile, forever.
        Scene source = NewScene();
        Brush shared = Box();
        source.Root.CreateChild("A").Brush = shared;
        source.Root.CreateChild("B").Brush = shared;

        Scene loaded = NewScene();
        MapSceneBinder.ApplyTo(MapReader.Read(MapWriter.Write(MapSceneBinder.FromScene(source))), loaded);

        loaded.Root.Children[0].Brush.ShouldNotBeSameAs(loaded.Root.Children[1].Brush);
    }

    // -- the payloads --------------------------------------------------------

    [Fact]
    public void Both_declared_bits_survive_independently()
    {
        // BrushKind decides admission, Brush.Operation decides sign, they live
        // on different objects and neither is derived from the other. A format
        // that collapsed them would have exactly one way to be wrong per
        // combination, so all four are checked.
        Scene source = NewScene();
        (string Name, BrushKind Kind, BrushOperation Operation)[] cases =
        [
            ("WorldAdd", BrushKind.World, BrushOperation.Additive),
            ("WorldCut", BrushKind.World, BrushOperation.Subtractive),
            ("PartAdd", BrushKind.Part, BrushOperation.Additive),
            ("PartCut", BrushKind.Part, BrushOperation.Subtractive),
        ];

        foreach ((string name, BrushKind kind, BrushOperation operation) in cases)
        {
            SceneNode node = source.Root.CreateChild(name);
            node.BrushKind = kind;
            node.Brush = Box().WithOperation(operation);
        }

        Scene loaded = NewScene();
        MapSceneBinder.ApplyTo(MapReader.Read(MapWriter.Write(MapSceneBinder.FromScene(source))), loaded);

        for (int i = 0; i < cases.Length; i++)
        {
            loaded.Root.Children[i].BrushKind.ShouldBe(cases[i].Kind, cases[i].Name);
            loaded.Root.Children[i].Brush!.Operation.ShouldBe(cases[i].Operation, cases[i].Name);
        }
    }

    [Fact]
    public void A_face_material_round_trips_as_a_path_rather_than_an_id()
    {
        // MaterialRef ids are handed out in first-intern order within one
        // process, so writing the id gives a world that textures itself
        // differently depending on which map loaded first.
        MaterialRef material = MaterialRegistry.Intern("Materials/roundtrip_probe.spectramat");

        Scene source = NewScene();
        source.Root.CreateChild("Wall").Brush = Box(1f, material);

        byte[] bytes = MapWriter.Write(MapSceneBinder.FromScene(source));
        Encoding.UTF8.GetString(bytes).ShouldContain("Materials/roundtrip_probe.spectramat");

        Scene loaded = NewScene();
        MapSceneBinder.ApplyTo(MapReader.Read(bytes), loaded);

        loaded.Root.Children[0].Brush!.FaceSurfaces[0].Material.ShouldBe(material);
    }

    [Fact]
    public void An_explicit_texture_axis_survives_and_a_world_aligned_face_stays_absent()
    {
        Scene source = NewScene();
        Brush brush = Box().WithFaceSurface(2, new FaceSurface(
            MaterialRef.Default,
            new Vector3(0f, 0f, 1f), new Vector3(0f, 1f, 0f),
            uOffset: 0.25f, vOffset: -0.5f, uScale: 2f, vScale: 4f));
        source.Root.CreateChild("Wall").Brush = brush;

        byte[] bytes = MapWriter.Write(MapSceneBinder.FromScene(source));
        Scene loaded = NewScene();
        MapSceneBinder.ApplyTo(MapReader.Read(bytes), loaded);

        FaceSurface[] faces = [.. loaded.Root.Children[0].Brush!.FaceSurfaces];
        faces[2].UAxis.ShouldBe(new Vector3(0f, 0f, 1f));
        faces[2].VAxis.ShouldBe(new Vector3(0f, 1f, 0f));
        faces[2].UOffset.ShouldBe(0.25f);
        faces[2].VOffset.ShouldBe(-0.5f);
        faces[2].UScale.ShouldBe(2f);
        faces[2].VScale.ShouldBe(4f);

        // A world-aligned face encodes as a zero axis, so it must come back as
        // world-aligned rather than as an explicit axis of three zeros.
        faces[0].IsWorldAligned.ShouldBeTrue();
        Encoding.UTF8.GetString(bytes).ShouldNotContain("\"u\":[0,0,0]");
    }

    [Fact]
    public void A_light_survives_with_every_field()
    {
        Scene source = NewScene();
        SceneNode sun = source.Root.CreateChild("Sun");
        sun.Light = new Light
        {
            Kind = LightKind.Point,
            Color = new Vector3(0.2f, 0.4f, 0.8f),
            Intensity = 7.5f,
            Range = 33f,
            Enabled = false,
        };

        Scene loaded = NewScene();
        MapSceneBinder.ApplyTo(MapReader.Read(MapWriter.Write(MapSceneBinder.FromScene(source))), loaded);

        Light light = loaded.Root.Children[0].Light.ShouldNotBeNull();
        light.Kind.ShouldBe(LightKind.Point);
        light.Color.ShouldBe(new Vector3(0.2f, 0.4f, 0.8f));
        light.Intensity.ShouldBe(7.5f);
        light.Range.ShouldBe(33f);
        light.Enabled.ShouldBeFalse();
    }

    [Fact]
    public void A_default_light_round_trips_without_tripping_the_range_guard()
    {
        // Light.Range refuses anything not strictly positive. A writer that
        // omitted the default and a reader that defaulted the missing number to
        // 0f would throw out of the property setter mid-load, naming no node.
        Scene source = NewScene();
        source.Root.CreateChild("Sun").Light = new Light();

        Scene loaded = NewScene();
        Should.NotThrow(() =>
            MapSceneBinder.ApplyTo(MapReader.Read(MapWriter.Write(MapSceneBinder.FromScene(source))), loaded));

        loaded.Root.Children[0].Light!.Range.ShouldBe(10f);
    }

    // -- what it cannot do, said out loud ------------------------------------

    [Fact]
    public void A_mesh_node_is_reported_rather_than_dropped_in_silence()
    {
        // Mesh carries no name, no path and no owning-asset reference, and
        // ModelInstantiator records nothing on the node it builds, so there is
        // genuinely nothing to write. The node keeps its identity, name,
        // placement and children; a map that quietly forgot the props would be
        // worse than one that says it did.
        Scene source = NewScene();
        source.Root.CreateChild("Crate").MeshRenderer = new MeshRenderer(new FakeMesh(), new Material(null));
        source.Root.CreateChild("Wall").Brush = Box();

        var report = new MapSaveReport();
        MapDocument document = MapSceneBinder.FromScene(source, report);

        report.IsComplete.ShouldBeFalse();
        report.MeshRendererNodes.ShouldBe(["Crate"]);
        report.Describe().ShouldNotBeNull();

        // The node itself still round-trips - it is only the geometry that is lost.
        document.Nodes.Count.ShouldBe(2);
        document.Nodes[0].Name.ShouldBe("Crate");
    }

    [Fact]
    public void A_scene_with_nothing_unwritable_reports_complete()
    {
        Scene source = NewScene();
        source.Root.CreateChild("Wall").Brush = Box();

        var report = new MapSaveReport();
        MapSceneBinder.FromScene(source, report);

        report.IsComplete.ShouldBeTrue();
        report.Describe().ShouldBeNull();
    }

    // -- failure -------------------------------------------------------------

    [Fact]
    public void A_hand_edited_plane_set_that_cannot_build_names_the_node_and_the_offset()
    {
        // Perfectly well-formed JSON, so the reader accepts it; Brush's
        // constructor is what rejects it, one whole stage later. Unwrapped that
        // reads as a complaint about plane indices in a map with hundreds of
        // brushes.
        byte[] bytes = Encoding.UTF8.GetBytes("""
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
                  "name": "Impossible",
                  "transform": {"p":[0,0,0]},
                  "brush": {
                    "planes": [
                      [1,0,0,-1],
                      [1,0,0,-1]
                    ],
                    "faces": [{},{}]
                  },
                  "children": []
                }
              ]
            }
            """);

        MapDocument document = MapReader.Read(bytes);
        var thrown = Should.Throw<MapFormatException>(() => MapSceneBinder.ApplyTo(document, NewScene()));

        thrown.NodeName.ShouldBe("Impossible");
        thrown.ByteOffset.ShouldBeGreaterThan(0, "the offset must survive into the scene-building stage");
    }

    // -- the bundle ----------------------------------------------------------

    [Fact]
    public void A_save_with_no_edits_in_it_does_not_touch_the_file()
    {
        // The point is not the saved write. It is that a watcher, a cook and a
        // git status all see nothing, because nothing happened.
        using var bundle = new TemporaryBundle();
        Scene source = NewScene();
        source.Root.CreateChild("Wall").Brush = Box();

        MapBundle.Save(bundle.Path, MapSceneBinder.FromScene(source)).ShouldBeTrue();
        DateTime firstWrite = File.GetLastWriteTimeUtc(MapBundle.DocumentPath(bundle.Path));

        MapBundle.Save(bundle.Path, MapBundle.Load(bundle.Path)).ShouldBeFalse(
            "an unedited document must reproduce its own bytes, so there is nothing to write");
        File.GetLastWriteTimeUtc(MapBundle.DocumentPath(bundle.Path)).ShouldBe(firstWrite);
    }

    [Fact]
    public void A_save_leaves_every_file_it_does_not_reference_alone()
    {
        // The bundle is a folder the user owns. An editor that tidies it is an
        // editor that deletes things.
        using var bundle = new TemporaryBundle();
        Scene source = NewScene();
        source.Root.CreateChild("Wall").Brush = Box();
        MapBundle.Save(bundle.Path, MapSceneBinder.FromScene(source));

        string notes = Path.Combine(bundle.Path, "NOTES.md");
        File.WriteAllText(notes, "do not delete me");
        Directory.CreateDirectory(Path.Combine(bundle.Path, "reference"));

        source.Root.CreateChild("Second").Brush = Box();
        MapBundle.Save(bundle.Path, MapSceneBinder.FromScene(source)).ShouldBeTrue();

        File.ReadAllText(notes).ShouldBe("do not delete me");
        Directory.Exists(Path.Combine(bundle.Path, "reference")).ShouldBeTrue();
    }

    [Fact]
    public void A_bundle_round_trips_through_disk()
    {
        using var bundle = new TemporaryBundle();
        Scene source = NewScene();
        SceneNode wall = source.Root.CreateChild("Wall");
        wall.Brush = Box();
        wall.LocalPosition = new Vector3(2f, 3f, 4f);

        MapBundle.Save(bundle.Path, MapSceneBinder.FromScene(source));
        MapBundle.IsBundle(bundle.Path).ShouldBeTrue();

        Scene loaded = NewScene();
        MapSceneBinder.ApplyTo(MapBundle.Load(bundle.Path), loaded);

        loaded.Root.Children[0].Name.ShouldBe("Wall");
        loaded.Root.Children[0].LocalPosition.ShouldBe(new Vector3(2f, 3f, 4f));
        loaded.Root.Children[0].Brush.ShouldNotBeNull();
    }

    [Fact]
    public void A_directory_with_no_document_in_it_is_not_a_bundle()
    {
        using var bundle = new TemporaryBundle();

        MapBundle.IsBundle(bundle.Path).ShouldBeFalse();
        Should.Throw<FileNotFoundException>(() => MapBundle.Load(bundle.Path));
    }

    private sealed class TemporaryBundle : IDisposable
    {
        public TemporaryBundle()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"spectra_map_{Guid.NewGuid():N}{MapFormat.BundleExtension}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* a temp directory that outlives the run is not a test failure */ }
        }
    }

    /// <summary>A mesh that exists only to be attached; nothing reads it.</summary>
    private sealed class FakeMesh : Mesh
    {
        public override void Draw() { }
        public override void DrawInstanced(InstanceBuffer instances, int count, int firstInstance = 0) { }
        public override void Dispose() { }
    }
}
