using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spectra.Kitchen.Cooking;
using SpectraEngine.Bsp.Tests;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Assets.Sources;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.Shaders;
using SpectraEngine.Core.Projects;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Texture = SpectraEngine.Core.Graphics.Texture;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// The milestone: a project cooked into a pack, mounted with nothing else, and
/// serving the three asset kinds a frame actually needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every other suite here proves one link.</b> The cook tests prove a pack was
/// written, the reader tests prove one can be parsed, and the asset tests prove a
/// manager resolves loose files. A slice can pass all three and still not boot,
/// because what joins them is a STRING: the pack's entry ids are hashes of the
/// exact path <see cref="ContentRoot.NormalizeRelativePath"/> produces, and a
/// second normalisation anywhere in the chain makes every lookup miss - which
/// degrades to a default material and a magenta placeholder while every log line
/// reads healthy.
/// </para>
/// <para>
/// <b>So the assertions are all about what is NOT there.</b> The tree holds no
/// loose file the shipped mount can reach, the material names a <c>.png</c> that
/// was never packed, and a texture that fell back would be the placeholder's
/// <c>Rgb8</c> rather than the cooked <c>Bc7</c>. Nothing here throws when it
/// breaks; the format is the only thing that says so.
/// </para>
/// </remarks>
public class PackedBootTests
{
    private const string TexturePath = "Textures/wall.png";
    private const string MaterialPath = "Materials/wall.spectramat";
    private const string ShaderPath = "Shaders/Lit.spectrashade";
    private const string NotesPath = "Data/notes.txt";

    [Fact]
    public void A_shipped_boot_resolves_a_texture_a_material_and_a_shader_out_of_the_pack_alone()
    {
        using var project = new TempProject();
        WriteContent(project);
        CookInto(project);

        using ProjectContentMount mount = Open(project, ContentMountProfile.Shipped);

        // Nothing loose is mounted, so the authored PNG is unreachable and the
        // material's slot can only be served by the .simage the cook emitted.
        mount.Content.Exists(TexturePath).ShouldBeFalse(
            "the pack carries the cooked image, and an authored PNG is never copied beside it");

        using AssetManager assets = Attach(project, mount);
        Material material = assets.LoadMaterial(MaterialPath);

        material.ShouldNotBe(assets.DefaultMaterial);
        material.TryGetTexture("uDiffuse", out _, out Texture? bound).ShouldBeTrue();

        // The format IS the assertion: a slot that fell back binds the 8x8
        // magenta checker, which is Rgb8, and nothing else about the material
        // would look wrong.
        bound.Format.ShouldBe(TextureFormat.Bc7);

        // And the shader the frame binds comes out of the pack already compiled,
        // which is what "a shipped game carries no compiler" means.
        ResolvedShader shader = BaseShaderResolver.ResolveBuiltIn(
            mount.Content, "Lit.spectrashade", GraphicsBackend.OpenGL, NullLogger.Instance);

        shader.Cooked.ShouldNotBeNull();
        shader.Source.ShouldBeNull("a cooked blob beats the source beside it");
        shader.WatchPath.ShouldBeNull("a packed shader has no file for a watcher to watch");

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void A_pack_serves_the_same_path_however_a_caller_spells_it()
    {
        // The identity claim, asked of the mounted stack rather than of the pack
        // reader: one asset whether the content came from a folder or an archive
        // is what makes the whole layer a source swap.
        using var project = new TempProject();
        WriteContent(project);
        CookInto(project);

        using ProjectContentMount mount = Open(project, ContentMountProfile.Shipped);

        mount.Content.Exists(NotesPath).ShouldBeTrue();
        mount.Content.Exists(@"Data\notes.txt").ShouldBeTrue();
        mount.Content.Exists("/Data/notes.txt").ShouldBeTrue();
        mount.Content.Exists("Data/absent.txt").ShouldBeFalse();
    }

    [Fact]
    public void A_shipped_boot_reports_hot_reload_off_and_says_why()
    {
        // Silently no-opping is the failure this exists to prevent: an asset
        // manager with hot reload nominally on over a pure-pack stack attaches no
        // watcher at all, so the only symptom is that saving a file stops doing
        // anything.
        using var project = new TempProject();
        WriteContent(project);
        CookInto(project);

        var log = new CapturingLogger();
        using ProjectContentMount mount = Open(project, ContentMountProfile.Shipped, log);

        mount.HotReloadEnabled.ShouldBeFalse();
        mount.HotReloadDisabledReason.ShouldNotBeNullOrWhiteSpace();

        log.MessagesAt(LogLevel.Information)
            .ShouldContain(line => line.Contains("Hot reload OFF") && line.Contains("watcher"), log.Describe());

        // The mechanism, not only the announcement: a pack answers no watch path,
        // which is exactly why the flag has to be forced rather than trusted.
        mount.Content.TryGetWatchPath(NotesPath, out _).ShouldBeFalse();
    }

    [Fact]
    public void A_dev_boot_lets_a_loose_file_shadow_the_packed_one_and_says_so()
    {
        using var project = new TempProject();
        WriteContent(project);
        CookInto(project);

        // Edited AFTER the cook, which is the whole artist workflow: the pack
        // still holds what was baked, and the file on disk has moved on.
        project.WriteAsset(NotesPath, "edited\n");

        var log = new CapturingLogger();
        using ProjectContentMount mount = Open(project, ContentMountProfile.Dev, log);

        mount.HotReloadEnabled.ShouldBeTrue();
        mount.HotReloadDisabledReason.ShouldBeNull();
        Read(mount.Content, NotesPath).ShouldBe("edited\n");

        // The material shadows too - it is packed verbatim and the file is still
        // on disk - so the claim is about this path rather than about the count.
        MountShadowing shadowing = mount.Shadowings.Single(entry => entry.Path == NotesPath);
        shadowing.WinnerPriority.ShouldBe(PackMountBand.Loose);
        shadowing.ShadowedPriority.ShouldBe(PackMountBand.Base);

        log.MessagesAt(LogLevel.Information)
            .ShouldContain(line => line.Contains("Mount shadowing") && line.Contains(NotesPath), log.Describe());

        // A loose file has a watch path, so hot reload keeps working over a
        // cooked build - which is the point of laying the two bands this way.
        mount.Content.TryGetWatchPath(NotesPath, out _).ShouldBeTrue();
    }

    [Fact]
    public void A_shipped_boot_serves_the_cook_even_where_a_loose_file_disagrees()
    {
        // The mirror of the dev case, and the reason the profiles are separate:
        // a shipped run must not quietly resolve out of a source tree that
        // happens to be beside it.
        using var project = new TempProject();
        WriteContent(project);
        CookInto(project);
        project.WriteAsset(NotesPath, "edited\n");

        using ProjectContentMount mount = Open(project, ContentMountProfile.Shipped);

        Read(mount.Content, NotesPath).ShouldBe("cooked\n");
        mount.Shadowings.ShouldBeEmpty();
    }

    [Fact]
    public void A_manifest_that_names_its_packs_mounts_exactly_those_in_order()
    {
        using var project = new TempProject();
        WriteContent(project);
        CookInto(project);

        // Written where the convention would NOT look, so a resolver that
        // ignored the manifest would fail the mount rather than pass by luck.
        string listed = Path.Combine(project.Root, "packs", "base.spack");
        Directory.CreateDirectory(Path.GetDirectoryName(listed)!);
        File.Copy(ProjectPacks.ConventionalPackPath(project.Layout), listed);

        project.Layout.Project.Packs.Add("packs/base.spack");

        ProjectPacks.Resolve(project.Layout).ShouldBe([Path.GetFullPath(listed)]);

        using ProjectContentMount mount = Open(project, ContentMountProfile.Shipped);
        mount.PackPaths.ShouldBe([Path.GetFullPath(listed)]);
        mount.Content.Exists(NotesPath).ShouldBeTrue();
    }

    [Fact]
    public void An_uncooked_project_is_refused_at_the_mount_and_told_how_to_cook()
    {
        // Never a silent fall back to loose files: a shipped game running on
        // whatever content it could still reach ships holes, and the loose mode
        // is a thing a host asks for by not mounting at all.
        using var project = new TempProject();
        WriteContent(project);

        Should.Throw<PackMountException>(() => Open(project, ContentMountProfile.Shipped))
            .Message.ShouldContain("scook cook");
    }

    // --- helpers -------------------------------------------------------------

    private static void WriteContent(TempProject project)
    {
        project.WriteAsset(TexturePath, TempProject.Png(width: 8, height: 8, seed: 7));
        project.WriteAsset(
            MaterialPath,
            $"shader = Lit\ntexture uDiffuse = {TexturePath}, nearest, clamp\n");

        // The engine's own lit shader rather than a toy, so what is packed is a
        // real two-stage pipeline with a vertex input table and a generated
        // instanced twin.
        project.WriteAsset(ShaderPath, BaseShaders.Lit);
        project.WriteAsset(NotesPath, "cooked\n");
    }

    // One backend, because the fake renderer reports OpenGL and compiling the
    // other two would triple the cook for blobs nothing in this file binds.
    private static void CookInto(TempProject project)
    {
        CookResult result = new CookSession(
            project.Layout,
            new CookSettings { UseCache = false, Targets = [GraphicsBackend.OpenGL] }).Run();

        result.Succeeded.ShouldBeTrue(
            string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.ToString())));
    }

    private static ProjectContentMount Open(
        TempProject project, ContentMountProfile profile, ILogger? logger = null) =>
        ProjectContentMount.Open(logger ?? NullLogger.Instance, project.Layout, profile);

    // The content ROOT stays the project's Assets folder even with nothing loose
    // mounted: it is the filesystem anchor a model import and an asset's stated
    // SourcePath resolve against, and what the stack decides is where the BYTES
    // come from.
    private static AssetManager Attach(TempProject project, ProjectContentMount mount)
    {
        var assets = new AssetManager(
            NullLogger<AssetManager>.Instance,
            project.Layout.AssetsPath,
            mount.Content,
            mount.HotReloadEnabled);

        assets.AttachRenderer(new FakeRenderer());
        return assets;
    }

    private static string Read(IContentSource content, string path)
    {
        content.TryOpen(path, out ContentBlob? blob).ShouldBeTrue();
        using (blob!)
            return Encoding.UTF8.GetString(blob.Span);
    }
}
