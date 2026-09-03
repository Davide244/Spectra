using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// Runs the real <c>scook</c> binary and collects what it said.
/// </summary>
/// <remarks>
/// <para><b>The out-of-process sibling of <c>ScookCliTests.Invoke</c>, and both
/// are wanted.</b> Driving <c>Program.Run</c> with its own writers is what keeps
/// the CLI contract tests as fast as everything else in this suite, and it is
/// blind to anything that varies per PROCESS - the string hash seed above all,
/// which is exactly what the determinism oracles are hunting. So the tests that
/// assert on a message go through the fast path and the tests that assert on
/// bytes go through this one.</para>
/// <para><b>The binary sits beside the test assembly</b>, because this project
/// references <c>Spectra.Kitchen.CLI</c> and its output is copied here. When it
/// is absent the tests SKIP with the command that produces it rather than
/// failing: a missing build output is not a broken cook.</para>
/// </remarks>
internal static class ScookProcess
{
    /// <summary>The binary, or null when it has not been built beside these tests.</summary>
    public static string? BinaryPath { get; } = Locate();

    /// <summary>Skips the calling test, by name, when there is no binary to run.</summary>
    public static void Require() =>
        Assert.SkipWhen(
            BinaryPath is null,
            "scook is not beside the test binary. It is build output of Spectra.Kitchen.CLI, " +
            "which this project references - build it with: dotnet build");

    /// <summary>Runs <c>scook</c> to completion.</summary>
    public static Result Run(params string[] args)
    {
        string path = BinaryPath
            ?? throw new InvalidOperationException("scook was not found; call Require() first.");

        var info = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // Fixed, so a run cannot depend on where the test host happened to be
            // started from - which is the same class of thing these tests exist to
            // rule out of the cook itself.
            WorkingDirectory = AppContext.BaseDirectory,
        };

        // --no-color first, for the reason ScookCliTests already records: appended,
        // it is swallowed as the argument of a trailing option.
        info.ArgumentList.Add("--no-color");
        foreach (string arg in args) info.ArgumentList.Add(arg);

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Could not start '{path}'.");

        // Both pipes are drained at once. Reading one to the end and then the other
        // deadlocks the moment the child fills the pipe nobody is reading, which for
        // a tool that prints one line is a bug lying in wait for the day it prints
        // more.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        process.WaitForExit();

        return new Result(process.ExitCode, stdout.Result, stderr.Result);
    }

    /// <summary>One run of the tool.</summary>
    public readonly record struct Result(int ExitCode, string Stdout, string Stderr);

    private static string? Locate()
    {
        string name = OperatingSystem.IsWindows() ? "scook.exe" : "scook";
        string path = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(path) ? path : null;
    }
}
