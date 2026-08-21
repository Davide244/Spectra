using SpectraEngine.Core.Assets;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// Content-root resolution and the path normalisation that produces asset cache
/// keys. The test run is itself a developer build, so
/// <see cref="ContentRoot.Path"/> must land on the repo's Assets folder — the
/// same rule that lets shader hot-reload find its sources.
/// </summary>
public sealed class ContentRootTests
{
    [Fact]
    public void Resolves_to_the_repo_assets_folder_in_a_developer_build()
    {
        ContentRoot.IsDeveloperBuild.ShouldBeTrue(
            "the test assembly runs from bin/ inside the source tree, so the solution file is findable");

        string root = ContentRoot.Path;
        Path.GetFileName(root).ShouldBe(ContentRoot.DirectoryName);
        Path.IsPathRooted(root).ShouldBeTrue();
        Directory.Exists(root).ShouldBeTrue($"content root '{root}' should exist");

        // The repo Assets folder, not the copy beside the test binary: the
        // latter sits under bin/, which the source root never does.
        root.Replace('\\', '/').ShouldNotContain("/bin/");
        File.Exists(Path.Combine(root, "Textures", "dev_grid.png")).ShouldBeTrue();
    }

    [Fact]
    public void Resolution_is_cached_so_repeated_reads_do_not_rewalk_the_tree()
    {
        // Reference equality: the same interned string instance comes back,
        // which is only true if the walk ran once and the result was cached.
        ReferenceEquals(ContentRoot.Path, ContentRoot.Path).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Textures/dev_grid.png")]
    [InlineData("Textures\\dev_grid.png")]
    [InlineData("./Textures/dev_grid.png")]
    [InlineData("Textures//dev_grid.png")]
    [InlineData("/Textures/dev_grid.png")]
    public void Normalizes_separators_and_noise_segments_to_one_canonical_key(string input)
        => ContentRoot.NormalizeRelativePath(input).ShouldBe("Textures/dev_grid.png");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("./")]
    [InlineData("../Textures/dev_grid.png")]
    [InlineData("Textures/../../secrets.png")]
    public void Rejects_empty_and_escaping_paths(string input)
        => Should.Throw<ArgumentException>(() => ContentRoot.NormalizeRelativePath(input));

    [Fact]
    public void Rejects_rooted_paths()
        => Should.Throw<ArgumentException>(
            () => ContentRoot.NormalizeRelativePath(Path.Combine(ContentRoot.Path, "Textures", "dev_grid.png")));

    [Fact]
    public void Resolves_absolute_paths_under_the_given_root()
    {
        string absolute = ContentRoot.ResolveAbsolute(ContentRoot.Path, "Textures\\dev_grid.png");

        Path.IsPathRooted(absolute).ShouldBeTrue();
        File.Exists(absolute).ShouldBeTrue();
        absolute.ShouldStartWith(ContentRoot.Path);
        // Both spellings of the same asset must land on the same file.
        ContentRoot.ResolveAbsolute(ContentRoot.Path, "Textures/dev_grid.png").ShouldBe(absolute);
    }
}
