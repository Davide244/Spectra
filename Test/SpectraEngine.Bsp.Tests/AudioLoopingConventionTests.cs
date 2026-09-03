using SpectraEngine.Core.Audio;
using System.Reflection;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// Guards the one rule the whole audio stage turns on: loop points are
/// buffer-queue arithmetic in sample frames, and <c>AL_LOOPING</c> is never
/// used to express one.
/// </summary>
/// <remarks>
/// <para>OpenAL's looping flag repeats a WHOLE buffer, so it can express
/// exactly one region: the entire sound. Reaching for it is the obvious thing
/// to do and it works perfectly for every test asset anyone writes by hand,
/// which is why the failure only surfaces when somebody authors the first sound
/// with an intro, and then surfaces on every looping asset at once.</para>
/// <para>Two tripwires, because the behaviour cannot be tested through a real
/// driver here. The interface a voice talks to has no way to SAY looping, so no
/// engine code path can set it; and the one file that names the AL enum sets it
/// to false and nothing else. The convention test is the same shape as
/// <c>ComPtrOwnershipConventionTests</c>: enforce the rule where it would
/// actually be broken, in the source.</para>
/// </remarks>
public sealed class AudioLoopingConventionTests
{
    [Fact]
    public void The_backend_seam_cannot_express_a_whole_buffer_loop_at_all()
    {
        // The strongest form of the guarantee: not "nobody sets it" but "there
        // is no member through which it could be set". A future voice type gets
        // this for free.
        MethodInfo[] members = typeof(IAudioBackend).GetMethods();

        foreach (MethodInfo member in members)
        {
            member.Name.ShouldNotContain("Loop", Case.Insensitive,
                "the AL seam must have no way to ask for AL_LOOPING; loops are buffer-queue arithmetic");

            foreach (ParameterInfo parameter in member.GetParameters())
            {
                (parameter.Name ?? string.Empty).ShouldNotContain("loop", Case.Insensitive,
                    $"{member.Name} takes a loop parameter, which the buffer-queue design has no use for");
            }
        }
    }

    [Fact]
    public void The_only_source_naming_the_AL_looping_enum_clears_it()
    {
        string audio = Path.Combine(SourceRoot(), "SpectraEngine.Core", "Audio");
        Directory.Exists(audio).ShouldBeTrue($"expected the audio sources under {audio}");

        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(audio, "*.cs", SearchOption.AllDirectories))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (IsComment(line)) continue;
                if (!line.Contains("SourceBoolean.Looping", StringComparison.Ordinal)) continue;

                // The one legitimate use is clearing it on a pooled source
                // before a voice takes it, so a stale flag cannot follow a
                // handle from one sound to the next.
                if (line.Contains("false", StringComparison.Ordinal)) continue;

                offenders.Add($"{Path.GetFileName(file)}({i + 1}): {line.Trim()}");
            }
        }

        offenders.ShouldBeEmpty(
            "AL_LOOPING repeats a whole buffer and cannot express a loop region inside one; " +
            "loops go through AudioLoopCursor and a buffer queue instead");
    }

    private static bool IsComment(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("*", StringComparison.Ordinal);
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
