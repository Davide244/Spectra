using System.IO;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// The compiled map is a function of the map, never of the process that baked it.
/// </summary>
/// <remarks>
/// <para><b>Two PROCESSES, for the one reason that matters.</b> .NET randomises the
/// string hash seed per process, so an emission order that leaked out of a
/// dictionary or a hash set is perfectly stable inside one test host and different
/// between two runs of the same tool. That is the failure somebody reports as "CI
/// says the map changed and nothing changed", and an in-process comparison
/// structurally cannot detect it: both bakes would share the seed.</para>
/// <para><b>Through the real <c>scook</c> binary, which is what the map bake made
/// possible.</b> Until the cook had a map to bake, the compiled-map writer had no
/// CLI route at all, and these oracles re-entered the TEST binary through an
/// environment variable and a module initializer to get a second process. That
/// harness is retired: <c>scook cook --loose</c> writes the map as a real file, so
/// these tests use the same mechanism <c>CookDeterminismTests</c> already uses for
/// packs and there is one way to get a second process rather than two.</para>
/// <para><b>A hash-order leak is a MAP problem before it is a pack one.</b> A pack
/// sorts its entries by asset id, so a scheduling or iteration leak inside one
/// entry's payload is invisible in the pack's own ordering and shows up only as the
/// payload's bytes; a compiled map carries a string table in first-reference order,
/// an asset table in walk order and a chunk directory sorted by cell, and every one
/// of those is a place a dictionary could leak into the file.</para>
/// </remarks>
public class ScmapDeterminismTests
{
    // Header, a thirteen-section table, five tables and two geometry sections over
    // a five-brush room is far past this; the floor exists so an empty or truncated
    // file cannot pass the identity tests.
    private const int CompiledMapFloor = 2048;

    [Fact]
    public void Two_clean_bakes_in_two_processes_are_byte_identical()
    {
        ScookProcess.Require();

        using var project = new TempProject();
        WriteFixture(project);

        byte[] first = Bake(project, "clean-a", "--no-cache");
        byte[] second = Bake(project, "clean-b", "--no-cache");

        first.Length.ShouldBeGreaterThan(CompiledMapFloor);
        second.ShouldBe(first);
    }

    [Fact]
    public void One_worker_and_many_workers_bake_the_same_map()
    {
        ScookProcess.Require();

        using var project = new TempProject();
        WriteFixture(project);

        byte[] serial = Bake(project, "j1", "--no-cache", "-j", "1");
        byte[] parallel = Bake(project, "j8", "--no-cache", "-j", "8");

        parallel.ShouldBe(serial);
    }

    [Fact]
    public void A_cached_bake_and_a_clean_bake_are_byte_identical()
    {
        ScookProcess.Require();

        using var project = new TempProject();
        WriteFixture(project);

        // The first run fills .spectra-cook/ as a side effect; the second is the
        // one under test.
        byte[] clean = Bake(project, "cold");
        byte[] cached = Bake(project, "warm");

        cached.ShouldBe(clean);
    }

    [Fact]
    public void Keeping_the_brush_source_re_bakes_rather_than_serving_the_cached_map()
    {
        ScookProcess.Require();

        using var project = new TempProject();
        WriteFixture(project);

        byte[] without = Bake(project, "plain");
        byte[] with = Bake(project, "kept", "--keep-brush-source");

        // The map rule declares KeepBrushSource and nothing else, so the switch has
        // to reach the cache key: served from cache the second file would be the
        // first one, which is the stale artifact a per-rule declaration exists to
        // prevent. Declaring one too few is exactly this, silently.
        with.ShouldNotBe(without);
        with.Length.ShouldBeGreaterThan(without.Length);
    }

    // A room rather than a box: two materials so a cell's submesh directory has
    // something to order, a doorway cut flush through its wall's base so the
    // coincident-plane case is in the bytes, and a part brush so the brush-source
    // section is present whatever the cook was asked for.
    private static void WriteFixture(TempProject project)
    {
        MapFixture fixture = MapFixture.Fresh();
        fixture.WriteMaterials(project);
        fixture.WriteBundle(project, "Room.smap");
    }

    // --loose rather than a pack, so a failure names the MAP rather than handing
    // back two containers to bisect. The cook is otherwise identical: the same
    // rules run over the same work list and the same bytes are emitted.
    private static byte[] Bake(TempProject project, string label, params string[] extra)
    {
        string output = Path.Combine(project.Root, label);

        ScookProcess.Result run = ScookProcess.Run(
            ["cook", project.Root, "--loose", "-o", output, .. extra]);

        run.ExitCode.ShouldBe(0, $"scook failed: {run.Stderr}");
        return File.ReadAllBytes(Path.Combine(output, "Maps", "Room.scmap"));
    }
}
