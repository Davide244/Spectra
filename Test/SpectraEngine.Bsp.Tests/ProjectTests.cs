using SpectraEngine.Core;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Maps;
using SpectraEngine.Core.Projects;
using SpectraEngine.Core.Scene;
using SpectraEngine.Core.Windowing;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// A game project is a folder of text with a manifest at its root, and the
/// manifest round-trips byte for byte like every other authored document.
/// </summary>
/// <remarks>
/// <para>
/// <b>The point of the manifest is that maps are plural.</b> A game is made of
/// levels, and until something named them there was nowhere to say which ones
/// exist or which one boots. Everything else in the file - display defaults,
/// backends - is there because a shipped game has to come up without a command
/// line.
/// </para>
/// </remarks>
public sealed class ProjectTests
{
    private const string Canonical = """
        {
          "spectraproject": 1,
          "minimumReadableVersion": 1,
          "engine": "1.0.0",
          "name": "MyGame",
          "id": "6f2b7c19-40ad-4b1e-9c0f-2e5d81a37b44",
          "startupMap": "Maps/Lobby.smap",
          "maps": [
            "Maps/Lobby.smap",
            "Maps/Arena.smap"
          ],
          "packs": ["base.spack"],
          "display": {"width":1600,"height":900,"vsync":false,"mode":"fullscreen"},
          "defaultBackend": "d3d12",
          "allowedBackends": ["d3d11","d3d12"]
        }
        """;

    // A project that has never been cooked names no packs at all, which is the
    // overwhelmingly common shape and the one a newly bound member is most
    // likely to start writing an empty array into.
    private const string WithoutPacks = """
        {
          "spectraproject": 1,
          "minimumReadableVersion": 1,
          "engine": "1.0.0",
          "name": "MyGame",
          "id": "6f2b7c19-40ad-4b1e-9c0f-2e5d81a37b44",
          "maps": [],
          "display": {"width":1280,"height":720,"vsync":true,"mode":"windowed"}
        }
        """;

    // 'packs' between two members this engine still carries: 'input' anchored to
    // 'maps' before it, 'settings' anchored to 'packs' after it. Byte identity
    // over this file is the only thing that can say where a BOUND 'packs' is
    // emitted, because a preserved one reproduces its own position for free.
    private const string PacksBetweenCarriedMembers = """
        {
          "spectraproject": 1,
          "minimumReadableVersion": 1,
          "engine": "1.0.0",
          "name": "MyGame",
          "id": "6f2b7c19-40ad-4b1e-9c0f-2e5d81a37b44",
          "startupMap": "Maps/Lobby.smap",
          "maps": [
            "Maps/Lobby.smap"
          ],
          "input": {"jump":"Space"},
          "packs": ["cooked/MyGame.spack","cooked/Patch1.spack"],
          "settings": {"difficulty":"normal"},
          "display": {"width":1280,"height":720,"vsync":true,"mode":"windowed"}
        }
        """;

    // -- the document --------------------------------------------------------

    [Fact]
    public void A_canonical_manifest_survives_a_read_and_a_write_byte_for_byte()
    {
        byte[] source = Utf8(Canonical);

        byte[] written = ProjectWriter.Write(ProjectReader.Read(source));

        if (!source.AsSpan().SequenceEqual(written))
        {
            throw new Xunit.Sdk.XunitException(
                $"--- expected ---\n{Canonical}\n--- actual ---\n{Encoding.UTF8.GetString(written)}");
        }
    }

    [Theory]
    [InlineData(nameof(WithoutPacks))]
    [InlineData(nameof(PacksBetweenCarriedMembers))]
    public void The_corpus_survives_a_read_and_a_write_byte_for_byte(string which)
    {
        string text = which == nameof(WithoutPacks) ? WithoutPacks : PacksBetweenCarriedMembers;
        byte[] source = Utf8(text);

        byte[] written = ProjectWriter.Write(ProjectReader.Read(source));

        if (!source.AsSpan().SequenceEqual(written))
        {
            throw new Xunit.Sdk.XunitException(
                $"--- expected ---\n{text}\n--- actual ---\n{Encoding.UTF8.GetString(written)}");
        }
    }

    [Fact]
    public void Packs_are_bound_in_manifest_order_and_the_carried_members_keep_their_places()
    {
        // Order is the mod and patch story: the mount stack resolves a path to
        // the LAST source that serves it, so a list read out of order silently
        // stops a patch applying.
        SpectraProject project = ProjectReader.Read(Utf8(PacksBetweenCarriedMembers));

        project.Packs.ShouldBe(["cooked/MyGame.spack", "cooked/Patch1.spack"]);

        // And 'packs' has left the preserved arm entirely, which is what the
        // byte-identity case above is actually proving about its anchor.
        project.Unknown.Select(member => member.Name).ShouldBe(["input", "settings"]);
    }

    [Fact]
    public void A_project_that_names_no_packs_writes_no_packs_member()
    {
        // Not tidiness: the round trip has to be exact for a file that predates
        // the member, and an empty array is a value somebody set on purpose.
        SpectraProject project = ProjectReader.Read(Utf8(WithoutPacks));

        project.Packs.ShouldBeEmpty();
        Encoding.UTF8.GetString(ProjectWriter.Write(project)).ShouldNotContain("packs");
    }

    [Fact]
    public void The_manifest_decodes_to_the_values_it_states()
    {
        SpectraProject project = ProjectReader.Read(Utf8(Canonical));

        project.FormatVersion.ShouldBe(EngineInfo.ProjectFormatVersion);
        project.Name.ShouldBe("MyGame");
        project.Id.ShouldBe(Guid.Parse("6f2b7c19-40ad-4b1e-9c0f-2e5d81a37b44"));
        project.StartupMap.ShouldBe("Maps/Lobby.smap");
        project.Maps.ShouldBe(["Maps/Lobby.smap", "Maps/Arena.smap"]);
        project.Display.Width.ShouldBe(1600);
        project.Display.Height.ShouldBe(900);
        project.Display.Vsync.ShouldBeFalse();
        project.Display.Mode.ShouldBe(WindowMode.BorderlessFullscreen);
        project.DefaultBackend.ShouldBe(GraphicsBackend.D3D12);
        project.AllowedBackends.ShouldBe([GraphicsBackend.D3D11, GraphicsBackend.D3D12]);
        project.Packs.ShouldBe(["base.spack"]);
    }

    [Fact]
    public void A_member_with_nothing_to_bind_to_is_carried()
    {
        // 'input' and 'settings' are specified and there is nothing in the tree
        // to bind them to, so decoding them would produce values that mean
        // nothing. Same three-tier rule the map uses.
        SpectraProject project = ProjectReader.Read(Utf8(PacksBetweenCarriedMembers));

        project.Unknown.Count.ShouldBe(2);
        project.Unknown[0].Name.ShouldBe("input");
        project.Unknown[1].Name.ShouldBe("settings");
    }

    [Fact]
    public void A_manifest_that_names_an_unknown_backend_is_refused()
    {
        // Never a fall-through to a default. A mistyped 'd3d1' silently becoming
        // OpenGL would ship a game rendering through a path nobody tested.
        var thrown = Should.Throw<ProjectFormatException>(() => ProjectReader.Read(Utf8("""
            {
              "spectraproject": 1,
              "minimumReadableVersion": 1,
              "engine": "1.0.0",
              "name": "G",
              "id": "6f2b7c19-40ad-4b1e-9c0f-2e5d81a37b44",
              "maps": [],
              "display": {"width":1280,"height":720,"vsync":true,"mode":"windowed"},
              "defaultBackend": "d3d1"
            }
            """)));

        thrown.Message.ShouldContain("d3d1");
        thrown.ByteOffset.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void A_zero_sized_window_is_refused_at_the_file_rather_than_three_layers_down()
    {
        var thrown = Should.Throw<ProjectFormatException>(() => ProjectReader.Read(Utf8("""
            {
              "spectraproject": 1,
              "minimumReadableVersion": 1,
              "engine": "1.0.0",
              "name": "G",
              "id": "6f2b7c19-40ad-4b1e-9c0f-2e5d81a37b44",
              "maps": [],
              "display": {"width":0,"height":720,"vsync":true,"mode":"windowed"}
            }
            """)));

        thrown.Message.ShouldContain("width");
    }

    [Fact]
    public void A_project_that_needs_a_newer_reader_is_refused_loudly()
    {
        Should.Throw<ProjectFormatException>(() => ProjectReader.Read(Utf8("""
            {
              "spectraproject": 9,
              "minimumReadableVersion": 9,
              "engine": "9.9.9",
              "name": "G",
              "id": "6f2b7c19-40ad-4b1e-9c0f-2e5d81a37b44",
              "maps": [],
              "display": {"width":1280,"height":720,"vsync":true,"mode":"windowed"}
            }
            """))).Message.ShouldContain("9");
    }

    // -- the folder ----------------------------------------------------------

    [Fact]
    public void A_created_project_has_the_canonical_layout_and_reopens()
    {
        using var temp = new TemporaryFolder();

        ProjectLayout created = ProjectLayout.Create(temp.Path, "MyGame");

        Directory.Exists(created.AssetsPath).ShouldBeTrue();
        Directory.Exists(created.MapsPath).ShouldBeTrue();
        Directory.Exists(created.ScriptsPath).ShouldBeTrue();
        File.Exists(Path.Combine(temp.Path, ".gitignore")).ShouldBeTrue();

        // The .gitattributes rule is what stops a Windows checkout rewriting
        // every map bundle underneath the person editing it.
        File.ReadAllText(Path.Combine(temp.Path, ".gitattributes"))
            .ShouldContain("**/*.smap/** text eol=lf");

        ProjectLayout reopened = ProjectLayout.Open(created.ManifestPath);
        reopened.Project.Name.ShouldBe("MyGame");
        reopened.Project.Id.ShouldBe(created.Project.Id);
    }

    [Fact]
    public void A_project_opens_from_its_folder_as_well_as_its_file()
    {
        // Both are what a person means: double-clicking gives the file, dragging
        // a folder or typing a path gives the directory.
        using var temp = new TemporaryFolder();
        ProjectLayout.Create(temp.Path, "MyGame");

        ProjectLayout.Open(temp.Path).Project.Name.ShouldBe("MyGame");
    }

    [Fact]
    public void A_folder_with_two_projects_in_it_is_refused_rather_than_guessed_at()
    {
        // Which project a folder IS is not something to guess, and the guess
        // would be alphabetical.
        using var temp = new TemporaryFolder();
        ProjectLayout.Create(temp.Path, "Alpha");
        File.WriteAllText(Path.Combine(temp.Path, "Beta.spectraproj"), "{}");

        Should.Throw<FileNotFoundException>(() => ProjectLayout.Open(temp.Path))
            .Message.ShouldContain("Beta");
    }

    [Fact]
    public void Saving_a_manifest_with_no_edits_in_it_does_not_touch_the_file()
    {
        using var temp = new TemporaryFolder();
        ProjectLayout created = ProjectLayout.Create(temp.Path, "MyGame");

        ProjectLayout.Open(created.ManifestPath).Save().ShouldBeFalse(
            "an unedited manifest must reproduce its own bytes, so there is nothing to write");
    }

    [Fact]
    public void Scaffolding_never_clobbers_a_file_that_is_already_there()
    {
        // These become the user's files the moment the folder exists, and a
        // scaffold that overwrites a hand-edited .gitignore is one nobody runs
        // twice.
        using var temp = new TemporaryFolder();
        Directory.CreateDirectory(temp.Path);
        File.WriteAllText(Path.Combine(temp.Path, ".gitignore"), "# mine");

        ProjectLayout.Create(temp.Path, "MyGame");

        File.ReadAllText(Path.Combine(temp.Path, ".gitignore")).ShouldBe("# mine");
    }

    // -- maps in a project ---------------------------------------------------

    [Fact]
    public void Map_discovery_finds_bundles_on_disk_in_a_stable_order()
    {
        using var temp = new TemporaryFolder();
        ProjectLayout project = ProjectLayout.Create(temp.Path, "MyGame");

        foreach (string name in new[] { "Zeta", "Alpha", "Mid" })
            MapBundle.Save(Path.Combine(project.MapsPath, name + MapFormat.BundleExtension), new MapDocument());

        // A folder that is not a bundle must not be mistaken for one.
        Directory.CreateDirectory(Path.Combine(project.MapsPath, "NotAMap.smap"));

        project.DiscoverMaps().ShouldBe(
            ["Maps/Alpha.smap", "Maps/Mid.smap", "Maps/Zeta.smap"],
            "Directory enumeration has no documented order, and a list that reshuffles cannot be reviewed");
    }

    [Fact]
    public void A_project_loads_a_map_it_names()
    {
        using var temp = new TemporaryFolder();
        ProjectLayout project = ProjectLayout.Create(temp.Path, "MyGame");

        var scene = new Scene("Lobby");
        scene.Root.CreateChild("Floor").Brush =
            SpectraEngine.Core.Bsp.Brush.CreateBox(new System.Numerics.Vector3(-4f, -1f, -4f),
                                                   new System.Numerics.Vector3(4f, 0f, 4f), default);

        MapBundle.Save(
            Path.Combine(project.MapsPath, "Lobby" + MapFormat.BundleExtension),
            MapSceneBinder.FromScene(scene));

        project.Project.Maps.Add("Maps/Lobby.smap");
        project.Project.StartupMap = "Maps/Lobby.smap";
        project.Save();

        ProjectLayout reopened = ProjectLayout.Open(temp.Path);
        reopened.Project.StartupMap.ShouldBe("Maps/Lobby.smap");

        MapDocument map = reopened.LoadMap(reopened.Project.StartupMap!);
        map.Scene.Name.ShouldBe("Lobby");
        map.Nodes[0].Name.ShouldBe("Floor");
        map.Nodes[0].Brush.ShouldNotBeNull();
    }

    // -- helpers -------------------------------------------------------------

    private static byte[] Utf8(string text) =>
        Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\n") + "\n");

    private sealed class TemporaryFolder : IDisposable
    {
        public TemporaryFolder() =>
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"spectra_project_{Guid.NewGuid():N}");

        public string Path { get; }

        public void Dispose()
        {
            // DirectoryNotFoundException is an IOException, so one clause covers
            // both the "never created" and the "still locked" cases.
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* a temp directory that outlives the run is not a failure */ }
        }
    }
}
