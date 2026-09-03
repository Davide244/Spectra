using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Xunit;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// Writes the compiled-map fixture from a SECOND process, so a determinism oracle
/// can compare bytes across the string hash seed.
/// </summary>
/// <remarks>
/// <para><b>Why a second process at all.</b> .NET randomises the string hash seed
/// per process, so an order that leaked out of a dictionary or a hash set is
/// stable inside one test host and different between two runs of the same tool.
/// An in-process comparison structurally cannot see that class of bug, which is
/// exactly what the cook's own determinism oracles already say and why they drive
/// the real <c>scook</c> binary through <c>Process.Start</c>.</para>
/// <para><b>Why this binary rather than scook.</b> The compiled-map writer has no
/// CLI verb until the map bake gives the cook a map to bake, so there is nothing
/// to run yet. Re-entering this test binary through an environment variable and a
/// module initializer is the smallest thing that produces the same bytes from a
/// fresh process, and it is temporary: once <c>scook</c> bakes maps, the existing
/// clean-cook oracle covers this file and this harness can go.</para>
/// <para><b>The module initializer runs before the generated entry point</b>,
/// because the entry point lives in this same module. The child therefore writes
/// its file and exits before a single test is discovered. The parent passes a
/// filter naming no class as well, so a child that somehow reached the runner
/// still runs nothing rather than recursing into this very test.</para>
/// </remarks>
internal static class ScmapChildProcess
{
    private const string OutputVariable = "SPECTRA_SCMAP_CHILD_OUTPUT";

    /// <summary>The compiled map the child writes.</summary>
    public const string MapFileName = "fixture.scmap";

    /// <summary>
    /// Evidence that the child really had its own hash seed, so a byte-identity
    /// pass is not vacuous.
    /// </summary>
    public const string SeedFileName = "seed-probe.txt";

    /// <summary>The test binary, or null when it is not where this expects it.</summary>
    public static string? BinaryPath { get; } = Locate();

    /// <summary>Skips the calling test, by name, when there is no binary to run.</summary>
    public static void Require() =>
        Assert.SkipWhen(
            BinaryPath is null,
            "The test apphost is not beside the test assembly, so the two-process oracle has nothing " +
            "to launch. Build it with: dotnet build");

    [ModuleInitializer]
    internal static void WriteFixtureWhenAsked()
    {
        string? directory = Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrEmpty(directory)) return;

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, MapFileName), ScmapFixture.Build());
            File.WriteAllText(Path.Combine(directory, SeedFileName), SeedProbe());
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Environment.Exit(1);
        }
    }

    /// <summary>Runs the child and returns what it wrote.</summary>
    public static Result Run(string directory)
    {
        string path = BinaryPath
            ?? throw new InvalidOperationException("The test apphost was not found; call Require() first.");

        var info = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        info.Environment[OutputVariable] = directory;

        // Belt and braces: if the module initializer ever stopped running, this
        // makes the child run no tests at all rather than recursing into the very
        // test that launched it.
        info.ArgumentList.Add("-class");
        info.ArgumentList.Add("Spectra.Kitchen.Tests.NoSuchClassInChildMode");

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Could not start '{path}'.");

        // Both pipes drained at once: reading one to the end and then the other
        // deadlocks the moment the child fills the pipe nobody is reading.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        process.WaitForExit();

        string mapPath = Path.Combine(directory, MapFileName);
        string seedPath = Path.Combine(directory, SeedFileName);

        return new Result(
            process.ExitCode,
            File.Exists(mapPath) ? File.ReadAllBytes(mapPath) : [],
            File.Exists(seedPath) ? File.ReadAllText(seedPath) : string.Empty,
            stdout.Result,
            stderr.Result);
    }

    /// <summary>
    /// The process's own string hash seed, rendered.
    /// </summary>
    /// <remarks>
    /// Not decoration. Byte identity across two processes proves nothing unless the
    /// two processes really did hash differently, and a randomised seed is the one
    /// thing this oracle depends on that nothing else in the run would notice
    /// disappearing.
    /// </remarks>
    public static string SeedProbe() =>
        string.Join(
            ',',
            "Materials/zulu.spectramat".GetHashCode(),
            "zeta_room".GetHashCode(),
            "Determinism".GetHashCode());

    /// <summary>One run of the child.</summary>
    public readonly record struct Result(int ExitCode, byte[] Map, string SeedProbe, string Stdout, string Stderr);

    private static string? Locate()
    {
        string name = OperatingSystem.IsWindows() ? "Spectra.Kitchen.Tests.exe" : "Spectra.Kitchen.Tests";
        string path = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(path) ? path : null;
    }
}
