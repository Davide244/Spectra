using SpectraEngine.Core.Assets;
using SpectraEngine.Editor.Shell;
using System.IO;

namespace SpectraEngine.Editor.Tests;

/// <summary>
/// What an asset drag carries, and what a drop does with it.
/// </summary>
/// <remarks>
/// <b>The gesture cannot be tested and both of its decisions can.</b> A drag is
/// a pointer, a compositor and an OLE session; what is actually capable of being
/// wrong is the conversion from a browsed filesystem path to the engine's own
/// content-relative identity, and the rule about which drops can be honoured.
/// Both are pure functions, so both are here rather than left as reasoning.
/// </remarks>
public sealed class ContentDragTests
{
    private static string Root => Path.Combine(Path.GetTempPath(), "spectra-assets");

    private static string Under(params string[] parts) =>
        Path.Combine([Root, .. parts]);

    // --- The payload ---------------------------------------------------------

    [Fact]
    public void A_dragged_file_carries_the_path_the_engine_names_it_by()
    {
        // The identity every other layer uses: forward slashes, relative to the
        // content root, no separator in front. Not the filesystem path - a
        // payload spelling identity a fifth way resolves nothing at the drop
        // while every log line reads healthy, because the path it names really
        // does exist.
        ContentDragPayload.TryCreate(
            Root, Under("Models", "crate.obj"), ContentKind.Model, out ContentDragPayload? payload)
            .ShouldBeTrue();

        payload.ShouldNotBeNull();
        payload.ContentPath.ShouldBe("Models/crate.obj");
        payload.Kind.ShouldBe(ContentKind.Model);
        payload.Name.ShouldBe("crate.obj");
    }

    [Fact]
    public void The_carried_path_is_the_one_ContentRoot_would_normalize_to()
    {
        // Stated as an equality against the engine's own normalizer rather than
        // against a literal, because the claim is not "this string" but "the
        // same string the asset caches, the map codec and the pack's id hash
        // all key on". A second normalizer here would be free to drift.
        ContentDragPayload.TryCreate(
            Root, Under("Textures", "Props", "wall_brick.png"), ContentKind.Texture,
            out ContentDragPayload? payload).ShouldBeTrue();

        payload!.ContentPath.ShouldBe(
            ContentRoot.NormalizeRelativePath(Path.Combine("Textures", "Props", "wall_brick.png")));
    }

    [Fact]
    public void A_folder_is_not_an_asset()
    {
        ContentDragPayload.TryCreate(Root, Under("Models"), ContentKind.Folder, out _)
            .ShouldBeFalse();
    }

    [Fact]
    public void A_browser_with_no_project_open_produces_no_payload()
    {
        // The browser's root is null until a project opens, and a relative path
        // against nothing is not a thing that can be computed. Refused at the
        // source, so no drag ever starts carrying one.
        ContentDragPayload.TryCreate(null, Under("Models", "crate.obj"), ContentKind.Model, out _)
            .ShouldBeFalse();
    }

    [Fact]
    public void A_file_outside_the_content_root_is_refused_rather_than_carried()
    {
        // GetRelativePath answers with '..' segments, which
        // NormalizeRelativePath refuses because a content reference must stay
        // inside the root. Caught here so a mis-rooted browser declines the drag
        // instead of handing the render thread a path it will reject three
        // threads later, with the user watching an empty node appear.
        string outside = Path.Combine(Path.GetTempPath(), "somewhere-else", "prop.obj");

        ContentDragPayload.TryCreate(Root, outside, ContentKind.Model, out _)
            .ShouldBeFalse();
    }

    // --- The drop decision ---------------------------------------------------

    [Fact]
    public void A_model_dropped_into_a_composited_viewport_with_a_session_is_placed()
    {
        AssetDropPolicy.Refuse(Model(), hasSession: true, viewportAcceptsDrops: true)
            .ShouldBeNull();
    }

    [Fact]
    public void A_native_viewport_refuses_the_drop_IN_WORDS_and_names_the_way_out()
    {
        // The whole reason the capability is asked for rather than assumed. The
        // two viewports render an identical picture, so a drop that silently
        // does nothing over one of them is indistinguishable from a broken
        // drag - and the fix is a command-line switch nobody would guess at.
        string? refusal = AssetDropPolicy.Refuse(
            Model(), hasSession: true, viewportAcceptsDrops: false);

        refusal.ShouldNotBeNullOrWhiteSpace();
        refusal.ShouldContain("--viewport=composition");
    }

    [Fact]
    public void A_drop_with_no_session_says_to_open_a_project_first()
    {
        string? refusal = AssetDropPolicy.Refuse(
            Model(), hasSession: false, viewportAcceptsDrops: true);

        refusal.ShouldNotBeNullOrWhiteSpace();
        refusal.ShouldContain("project");
    }

    [Fact]
    public void The_missing_session_outranks_the_viewport_and_the_kind()
    {
        // Order follows what the user can act on: with no project open, nothing
        // else is true yet, and telling somebody their viewport cannot take a
        // texture is a fact about a session that does not exist.
        var texture = new ContentDragPayload(ContentKind.Texture, "Textures/x.png", "x.png");

        string? refusal = AssetDropPolicy.Refuse(
            texture, hasSession: false, viewportAcceptsDrops: false);

        refusal.ShouldNotBeNullOrWhiteSpace();
        refusal.ShouldContain("project");
    }

    [Fact]
    public void Only_a_model_can_be_placed_yet_and_everything_else_is_told_so()
    {
        // A texture or a material dropped into the scene is a reasonable thing
        // to try and there is nothing to do with it yet. Answered with a
        // sentence naming the file rather than with a cursor, because the "no
        // entry" pointer says the shell did not understand the gesture when in
        // fact it understood it perfectly.
        foreach (ContentKind kind in new[]
        {
            ContentKind.Texture, ContentKind.Material, ContentKind.Shader, ContentKind.Other,
        })
        {
            AssetDropPolicy.CanPlace(kind).ShouldBeFalse();

            var payload = new ContentDragPayload(kind, "Assets/thing.dat", "thing.dat");
            string? refusal = AssetDropPolicy.Refuse(
                payload, hasSession: true, viewportAcceptsDrops: true);

            refusal.ShouldNotBeNullOrWhiteSpace();
            refusal.ShouldContain("thing.dat");
        }

        AssetDropPolicy.CanPlace(ContentKind.Model).ShouldBeTrue();
        AssetDropPolicy.CanPlace(ContentKind.Folder).ShouldBeFalse();
    }

    private static ContentDragPayload Model() =>
        new(ContentKind.Model, "Models/crate.obj", "crate.obj");
}
