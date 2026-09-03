using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.Maths;
using SpectraEngine.Bsp.Tests;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Hosting;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// Dropping a model file into the viewport, from the render thread's side of
/// the boundary.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gesture itself cannot be driven headlessly and the placement can.</b>
/// A drag is Avalonia, a compositor, a pointer and an OLE session; what decides
/// whether the result is correct is a verb on <c>SceneEditorHost</c> that takes
/// a path and a viewport point. So the verb is what has tests: where the node
/// lands, that it is one history entry, that it is selected, that a refusal is a
/// refusal, and that a model nobody can resolve still leaves something in the
/// scene with the reason attached.
/// </para>
/// <para>
/// <b>The degradation cases are the point of the file.</b> A drop is the one
/// gesture in this editor with no keyboard equivalent, so a drop that silently
/// does nothing is indistinguishable from a drag the shell never received - and
/// the failure it hides is a content problem the level designer is the only
/// person who can fix.
/// </para>
/// </remarks>
public sealed class ModelInsertTests
{
    // A model the repo's own content root really has, so the success path is
    // measured against a real import rather than a fixture that agrees with
    // whatever the importer happens to do.
    private const string Crate = "Models/crate.obj";

    private static SceneEditorHost NewHost(Scene scene, Renderer? renderer = null)
    {
        renderer ??= new CompilingRenderer();

        // The host measures its viewport from the renderer's latch in its
        // constructor, and a zero-sized one makes every later pick undefined.
        renderer.SetFramebufferSize(new Vector2D<int>(1280, 720));

        return new SceneEditorHost(
            NullLoggerFactory.Instance,
            scene,
            renderer,
            new InputManager(NullLogger<InputManager>.Instance));
    }

    // A scene with a real asset manager over the repo's Assets folder. The
    // manager is returned so a test can release its GPU resources, which for a
    // FakeRenderer is bookkeeping rather than a driver call but is the contract
    // either way.
    private static (SceneEditorHost Host, AssetManager Assets) NewHostWithAssets(Scene scene)
    {
        var renderer = new FakeRenderer();
        var assets = new AssetManager(
            NullLogger<AssetManager>.Instance, ContentRoot.Path, hotReloadEnabled: false);
        assets.AttachRenderer(renderer);
        scene.Assets = assets;

        return (NewHost(scene, renderer), assets);
    }

    // A plate whose top face is at y = 1, with the camera looking at the point
    // the centre ray crosses it. The same fixture the brush-insert placement
    // test uses, for the same reason: it makes the expected height a constant.
    private static void AimAtAPlate(Scene scene)
    {
        SceneNode plate = scene.Root.CreateChild("Plate");
        plate.Brush = Brush.CreateBox(new Vector3(-8f, -1f, -8f), new Vector3(8f, 1f, 8f));

        scene.Camera.Position = new Vector3(0.5f, 8f, 4f);
        scene.Camera.LookAt(new Vector3(0.5f, 1f, 0.5f));
    }

    // --- The success path ----------------------------------------------------

    [Fact]
    public void A_dropped_model_arrives_with_geometry_and_names_where_it_came_from()
    {
        var scene = new Scene("Editor");
        (SceneEditorHost host, AssetManager assets) = NewHostWithAssets(scene);

        ModelInsertReport report = host.InsertModel(Crate);

        report.Placed.ShouldBeTrue();
        report.IsComplete.ShouldBeTrue(report.Describe());
        report.Unresolved.ShouldBeNull();
        report.Refused.ShouldBeNull();

        scene.Root.Children.Count.ShouldBe(1);
        SceneNode node = scene.Root.Children[0];
        node.Id.ShouldBe(report.NodeId);

        // Named for the FILE, not for whatever the importer called its root
        // node: a dropped asset is recognised in the tree by what was dragged.
        node.Name.ShouldBe("crate");

        // The renderer proves the geometry landed; MeshSource is what makes the
        // drop survive a save and a reload, and it is written by
        // ModelInstantiator rather than here.
        CollectSources(node).ShouldNotBeEmpty("a dropped model must record which file it came from");
        foreach (MeshSource source in CollectSources(node))
            source.ModelPath.ShouldBe(Crate);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void A_dropped_model_is_one_history_entry_and_is_selected()
    {
        var scene = new Scene("Editor");
        (SceneEditorHost host, AssetManager assets) = NewHostWithAssets(scene);

        host.InsertModel(Crate);

        SceneNode node = scene.Root.Children[0];
        scene.Selection.Items.ShouldBe([node]);
        host.UndoDepth.ShouldBe(1, "a whole model subtree is one thing the user did");

        Guid id = node.Id;
        host.Apply(EditorHostCommand.Undo);
        scene.Root.Children.ShouldBeEmpty();

        // Back under the same id, like every other structural verb, so a shell
        // holding the id keeps working.
        host.Apply(EditorHostCommand.Redo);
        scene.Root.Children.Count.ShouldBe(1);
        scene.Root.Children[0].Id.ShouldBe(id);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void A_dropped_model_rests_on_the_surface_it_was_aimed_at()
    {
        // crate.obj's own pivot is at its base (y runs 0 to 32), so this case
        // passes with a clearance of zero and proves only that nothing pushed
        // it off the plane. The test below is the one that bites.
        var scene = new Scene("Editor");
        AimAtAPlate(scene);
        (SceneEditorHost host, AssetManager assets) = NewHostWithAssets(scene);

        host.InsertModel(Crate);

        SceneNode node = scene.Root.Children[^1];
        MeasureAlongY(node, out float min, out float max);

        max.ShouldBeGreaterThan(min, "the crate has to have some height for this to mean anything");
        min.ShouldBe(1f, 0.001f, "a dropped model rests flush on the surface under the cursor");

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void A_model_whose_pivot_is_at_its_CENTRE_is_lifted_rather_than_half_buried()
    {
        // The falsification for the case above. Every model in the repo's own
        // content root happens to be authored with its origin at its base, so a
        // clearance of zero satisfies all of them and the measurement could be
        // deleted with every assertion still green. This fixture is authored
        // centred on its own origin, which is just as common in exported
        // content, and it is the only shape that can tell the two apart:
        // resting the PIVOT on the surface would bury half of it.
        var scene = new Scene("Editor");
        AimAtAPlate(scene);

        string root = CenteredCubeContentRoot(out string modelPath);
        try
        {
            var renderer = new FakeRenderer();
            var assets = new AssetManager(
                NullLogger<AssetManager>.Instance, root, hotReloadEnabled: false);
            assets.AttachRenderer(renderer);
            scene.Assets = assets;

            SceneEditorHost host = NewHost(scene, renderer);
            ModelInsertReport report = host.InsertModel(modelPath);
            report.IsComplete.ShouldBeTrue(report.Describe());

            SceneNode node = scene.Root.Children[^1];
            MeasureAlongY(node, out float min, out float max);

            (max - min).ShouldBe(2f, 0.01f, "the fixture is two units tall");
            min.ShouldBe(1f, 0.001f, "the model's lowest point sits on the surface");
            node.LocalPosition.Y.ShouldBe(2f, 0.01f, "its pivot is therefore a unit above the surface");

            assets.ReleaseGraphicsResources();
        }
        finally
        {
            System.IO.Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_drop_aims_at_the_point_it_was_dropped_on_rather_than_the_view_centre()
    {
        // The whole reason the verb takes a viewport point: a drop lands where
        // the pointer was, not where the camera happens to look. Two drops at
        // opposite corners of the same plate must land in different places.
        var scene = new Scene("Editor");
        AimAtAPlate(scene);
        (SceneEditorHost host, AssetManager assets) = NewHostWithAssets(scene);

        host.InsertModel(Crate, new Vector2(320f, 300f));
        Vector3 left = scene.Root.Children[^1].LocalPosition;

        host.Apply(EditorHostCommand.Undo);

        host.InsertModel(Crate, new Vector2(960f, 300f));
        Vector3 right = scene.Root.Children[^1].LocalPosition;

        right.X.ShouldBeGreaterThan(left.X, "a drop on the right of the viewport lands to the right");

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void A_model_file_that_carries_its_own_root_translation_still_lands_where_it_was_dropped()
    {
        // The file's root TRANSLATION is discarded, because a drop says where
        // the thing goes; a glTF whose scene root sits far from the origin would
        // otherwise land far from the cursor and read as the drop having missed.
        // Measured against the same plate: whatever crate.obj's own root says,
        // the result is on the surface.
        var scene = new Scene("Editor");
        AimAtAPlate(scene);
        (SceneEditorHost host, AssetManager assets) = NewHostWithAssets(scene);

        host.InsertModel(Crate);

        SceneNode node = scene.Root.Children[^1];
        MeasureAlongY(node, out float min, out _);
        min.ShouldBe(1f, 0.001f);

        // And the placement really is the node's own position rather than an
        // offset inherited from the file.
        node.LocalPosition.Y.ShouldBe(node.WorldPosition.Y, 0.0001f);

        assets.ReleaseGraphicsResources();
    }

    // --- Degradation ---------------------------------------------------------

    [Fact]
    public void A_scene_with_no_asset_manager_still_places_a_node_and_says_why_it_is_empty()
    {
        // The map loader's rule, applied to a drop: a missing prop is a missing
        // decoration, not an exception out of the middle of an edit.
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);

        ModelInsertReport report = host.InsertModel(Crate);

        report.Placed.ShouldBeTrue("a node the user can see and delete beats silence");
        report.IsComplete.ShouldBeFalse();
        report.Unresolved.ShouldNotBeNullOrWhiteSpace();
        report.Refused.ShouldBeNull("nothing refused this; it simply could not be resolved");

        scene.Root.Children.Count.ShouldBe(1);
        SceneNode node = scene.Root.Children[0];
        node.MeshRenderer.ShouldBeNull();
        node.Name.ShouldBe("crate", "the node still says what was asked for");
        scene.Selection.Items.ShouldBe([node]);
        host.UndoDepth.ShouldBe(1, "an empty placeholder is still one thing to undo");
    }

    [Fact]
    public void A_model_the_project_does_not_have_places_a_node_naming_the_file()
    {
        var scene = new Scene("Editor");
        (SceneEditorHost host, AssetManager assets) = NewHostWithAssets(scene);

        ModelInsertReport report = host.InsertModel("Models/there_is_no_such_prop.obj");

        report.Placed.ShouldBeTrue();
        report.Unresolved.ShouldNotBeNullOrWhiteSpace();
        report.Describe().ShouldContain("there_is_no_such_prop.obj");
        scene.Root.Children[0].MeshRenderer.ShouldBeNull();

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void An_asset_path_that_escapes_the_content_root_is_reported_rather_than_resolved()
    {
        // ContentRoot.NormalizeRelativePath throws on a rooted path and on one
        // carrying '..', which is exactly what a payload built from an absolute
        // filesystem path would produce. Caught and reported, so a mis-built
        // drag says so rather than reaching outside the project.
        var scene = new Scene("Editor");
        (SceneEditorHost host, AssetManager assets) = NewHostWithAssets(scene);

        ModelInsertReport escape = host.InsertModel("../../secrets.obj");
        escape.Unresolved.ShouldNotBeNullOrWhiteSpace();

        host.Apply(EditorHostCommand.Undo);

        ModelInsertReport rooted = host.InsertModel(@"C:\elsewhere\prop.obj");
        rooted.Unresolved.ShouldNotBeNullOrWhiteSpace();

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void An_empty_path_is_refused_outright_rather_than_placing_anything()
    {
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);

        ModelInsertReport report = host.InsertModel("   ");

        report.Placed.ShouldBeFalse();
        report.Refused.ShouldNotBeNullOrWhiteSpace();
        report.Unresolved.ShouldBeNull("nothing was placed, so nothing is missing geometry");
        scene.Root.Children.ShouldBeEmpty();
        host.UndoDepth.ShouldBe(0);
    }

    // --- Refusal -------------------------------------------------------------

    [Fact]
    public void A_drop_is_refused_while_play_mode_owns_the_scene()
    {
        // The same gate every mutating verb goes through, and it matters more
        // here than for a menu item: a UI's view of play mode is a publish
        // interval stale, and a drag that started before play began lands after.
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);
        host.Suspend();

        ModelInsertReport report = host.InsertModel(Crate);

        report.Placed.ShouldBeFalse();
        report.Refused.ShouldNotBeNullOrWhiteSpace();
        report.Describe().ShouldContain("not placed");
        scene.Root.Children.ShouldBeEmpty();
        host.UndoDepth.ShouldBe(0);
    }

    [Fact]
    public void A_refusal_and_an_unresolved_model_are_reported_as_different_things()
    {
        // Flattened into one string they read the same, and the two answers are
        // opposite: a refusal means try again, an unresolved model means a node
        // is in the scene and in the history and the asset is what is wrong.
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);

        ModelInsertReport unresolved = host.InsertModel(Crate);
        host.Suspend();
        ModelInsertReport refused = host.InsertModel(Crate);

        unresolved.Placed.ShouldBeTrue();
        refused.Placed.ShouldBeFalse();
        unresolved.Describe().ShouldNotBe(refused.Describe());
    }

    // --- Helpers -------------------------------------------------------------

    // A throwaway content root holding one cube authored from -1 to +1 on every
    // axis. Written as an OBJ rather than assembled in memory because the point
    // is to go through the real importer: a fixture that skipped it would prove
    // nothing about where a dropped file lands.
    private static string CenteredCubeContentRoot(out string modelPath)
    {
        string root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "spectra-drop-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "Models"));

        modelPath = "Models/centered_cube.obj";
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(root, "Models", "centered_cube.obj"),
            """
            v -1 -1 -1
            v  1 -1 -1
            v  1  1 -1
            v -1  1 -1
            v -1 -1  1
            v  1 -1  1
            v  1  1  1
            v -1  1  1
            f 1 3 2
            f 1 4 3
            f 5 6 7
            f 5 7 8
            f 1 2 6
            f 1 6 5
            f 2 3 7
            f 2 7 6
            f 3 4 8
            f 3 8 7
            f 4 1 5
            f 4 5 8

            """);

        return root;
    }

    private static List<MeshSource> CollectSources(SceneNode root)
    {
        var found = new List<MeshSource>();
        Walk(root);
        return found;

        void Walk(SceneNode node)
        {
            if (node.MeshSource is { } source)
                found.Add(source);

            for (int i = 0; i < node.Children.Count; i++)
                Walk(node.Children[i]);
        }
    }

    // The world-space extent of a whole subtree along +Y, through the same
    // measurement the gizmo box and the resize tool use.
    private static void MeasureAlongY(SceneNode root, out float min, out float max)
    {
        var nodes = new List<SceneNode>();
        Walk(root);

        GizmoSelectionBounds.TryMeasure(
            nodes, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ,
            out Vector3 low, out Vector3 high).ShouldBeTrue();

        min = low.Y;
        max = high.Y;

        void Walk(SceneNode node)
        {
            nodes.Add(node);
            for (int i = 0; i < node.Children.Count; i++)
                Walk(node.Children[i]);
        }
    }
}
