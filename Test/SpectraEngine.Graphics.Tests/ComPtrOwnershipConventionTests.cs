using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// Guards the ownership rule across the whole D3D surface rather than one file
/// at a time: no backend source may wrap a raw COM pointer with
/// <c>new ComPtr&lt;T&gt;(p)</c>, because that AddRefs and leaks the resource
/// (see <see cref="ComOwnershipTests"/> for the refcount proof).
/// </summary>
/// <remarks>
/// The reference-counting behaviour itself cannot be tested through the
/// renderers — they need a device — so the rule is enforced where it is
/// actually broken: in the source. The first attempt at this fix converted five
/// of thirty sites and left the mesh, texture and shader paths leaking; this
/// test is what makes that visible without a GPU and a memory profiler.
/// </remarks>
public sealed class ComPtrOwnershipConventionTests
{
    // Wrapping a pointer-valued expression. Deliberately narrow: it must not
    // match `new ComPtr<T>[n]` (an array of empty handles, which owns nothing)
    // or the one legitimate wrap, inside ComOwnership.Own itself.
    private static readonly Regex Wrap = new(@"new\s+ComPtr<[^>]+>\s*\(", RegexOptions.Compiled);

    [Fact]
    public void No_D3D_source_wraps_a_raw_pointer_instead_of_owning_it()
    {
        var offenders = new List<string>();

        foreach (string file in GraphicsSources())
        {
            if (Path.GetFileName(file) == "ComOwnership.cs") continue; // the one place the wrap belongs

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (IsComment(line)) continue;
                if (Wrap.IsMatch(line))
                    offenders.Add($"{Path.GetFileName(file)}({i + 1}): {line.Trim()}");
            }
        }

        offenders.ShouldBeEmpty(
            "every freshly created COM pointer must be handed to ComOwnership.Own; " +
            "`new ComPtr<T>(p)` AddRefs and the resource is then never destroyed");
    }

    private static bool IsComment(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("///", StringComparison.Ordinal)
            || trimmed.StartsWith("*", StringComparison.Ordinal);
    }

    private static IEnumerable<string> GraphicsSources()
    {
        string root = SourceRoot();
        string graphics = Path.Combine(root, "SpectraEngine.Core", "Graphics");
        Directory.Exists(graphics).ShouldBeTrue($"expected the graphics sources under {graphics}");
        return Directory.EnumerateFiles(graphics, "*.cs", SearchOption.AllDirectories);
    }

    // The same walk ContentRoot uses: the nearest ancestor holding a solution
    // file is the repo root. These tests only ever run out of the repo.
    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("*.slnx").Length > 0 || dir.GetFiles("*.sln").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"No solution file above {AppContext.BaseDirectory}; the source-convention test needs the repo.");
    }
}
