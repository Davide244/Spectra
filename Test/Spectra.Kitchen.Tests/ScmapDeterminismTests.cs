using System;
using System.IO;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// The compiled map is a function of the map, never of the process that wrote it.
/// </summary>
/// <remarks>
/// <para><b>Two PROCESSES, for the one reason that matters.</b> .NET randomises the
/// string hash seed per process, so an emission order that leaked out of a
/// dictionary or a hash set is perfectly stable inside one test host and different
/// between two runs of the same tool. That is the failure somebody reports as "CI
/// says the map changed and nothing changed", and an in-process comparison
/// structurally cannot detect it: both builds would share the seed. The cook's own
/// determinism oracles already run the real binary through <c>Process.Start</c> for
/// exactly this reason, and this file copies that mechanism rather than inventing a
/// second one.</para>
/// <para><b>The seed probe is what stops the oracle passing vacuously.</b> Byte
/// identity across two processes says nothing unless the two really did hash
/// differently, and if randomised string hashing were ever disabled for this
/// process this test would go on reporting a pass while proving nothing at all. So
/// each child writes its own hash codes beside its map and the two are required to
/// disagree.</para>
/// </remarks>
public class ScmapDeterminismTests
{
    [Fact]
    public void Two_processes_write_the_same_compiled_map_byte_for_byte()
    {
        ScmapChildProcess.Require();

        using var workspace = new TempDirectory();

        ScmapChildProcess.Result first = ScmapChildProcess.Run(Path.Combine(workspace.Path, "a"));
        ScmapChildProcess.Result second = ScmapChildProcess.Run(Path.Combine(workspace.Path, "b"));

        first.ExitCode.ShouldBe(0, first.Stderr);
        second.ExitCode.ShouldBe(0, second.Stderr);

        first.Map.Length.ShouldBeGreaterThan(ScmapFixtureFloor);
        second.Map.ShouldBe(first.Map);
    }

    [Fact]
    public void The_two_processes_really_did_hash_strings_differently()
    {
        // The non-vacuity check. Without it, a build where randomised string
        // hashing was somehow off would report the strongest possible pass while
        // measuring nothing.
        ScmapChildProcess.Require();

        using var workspace = new TempDirectory();

        ScmapChildProcess.Result first = ScmapChildProcess.Run(Path.Combine(workspace.Path, "a"));
        ScmapChildProcess.Result second = ScmapChildProcess.Run(Path.Combine(workspace.Path, "b"));

        first.SeedProbe.ShouldNotBeNullOrEmpty();
        second.SeedProbe.ShouldNotBeNullOrEmpty();
        second.SeedProbe.ShouldNotBe(first.SeedProbe);
    }

    [Fact]
    public void A_child_process_writes_what_this_process_writes()
    {
        // The bridge between the two-process oracle and every in-process assertion
        // in this suite: they are the same bytes, so what the other tests prove
        // about the fixture is proven about what the children compared.
        ScmapChildProcess.Require();

        using var workspace = new TempDirectory();
        ScmapChildProcess.Result child = ScmapChildProcess.Run(Path.Combine(workspace.Path, "a"));

        child.ExitCode.ShouldBe(0, child.Stderr);
        child.Map.ShouldBe(ScmapFixture.Build());
    }

    // Header plus a twelve-section table plus five non-empty bodies is well past
    // this; the floor exists so an empty file cannot pass the identity test.
    private const int ScmapFixtureFloor = 512;

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "spectra-scmap-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not a test failure.
            }
        }
    }
}
