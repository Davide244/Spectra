using Spectra.Kitchen.Cooking;
using SpectraEngine.Core.Graphics;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Spectra.Kitchen.Cache;

/// <summary>
/// Renders the settings a rule declared into the sorted key/value pairs the cache
/// key hashes.
/// </summary>
/// <remarks>
/// <para><b>Sorted ordinal by key, so no dictionary's iteration order can leak
/// into an artifact's identity.</b> The flags could be walked in bit order and get
/// a deterministic answer for free; sorting is written out anyway because the
/// property being relied on is "the order is decided here", and a bit order is a
/// property of the numbers somebody chose in an enum.</para>
/// <para><b>Every value is spelled the way the command line spells it.</b> Two
/// vocabularies for one setting is how a cache key ends up disagreeing with a
/// manifest about which profile produced an artifact.</para>
/// </remarks>
public static class CookSettingsDigest
{
    /// <summary>
    /// The pairs <paramref name="declared"/> selects out of
    /// <paramref name="settings"/>, sorted ordinal by key.
    /// </summary>
    public static List<KeyValuePair<string, string>> Describe(CookSettings settings, CookSettingKeys declared)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var pairs = new List<KeyValuePair<string, string>>(4);

        if ((declared & CookSettingKeys.Profile) != 0)
            pairs.Add(new("profile", CookManifest.ToWire(settings.Profile)));

        if ((declared & CookSettingKeys.Targets) != 0)
            pairs.Add(new("targets", DescribeTargets(settings.Targets)));

        if ((declared & CookSettingKeys.ScriptSource) != 0)
            pairs.Add(new("scriptSource", ToWire(settings.ScriptSource)));

        if ((declared & CookSettingKeys.Encoder) != 0)
            pairs.Add(new("encoder", ToWire(settings.Encoder)));

        if ((declared & CookSettingKeys.KeepBrushSource) != 0)
            pairs.Add(new("keepBrushSource", settings.KeepBrushSource ? "true" : "false"));

        if ((declared & CookSettingKeys.AudioSampleRate) != 0)
        {
            // Invariant, like every other number that reaches a cache key: a
            // culture that groups thousands would spell 48000 as "48,000" on one
            // machine and "48000" on another, which is two cache entries for one
            // setting and a pack that can never be byte-identical between them.
            pairs.Add(new(
                "audioSampleRate",
                settings.AudioSampleRate.ToString(CultureInfo.InvariantCulture)));
        }

        pairs.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        return pairs;
    }

    /// <summary>The script source mode's spelling, which is the command line's.</summary>
    public static string ToWire(ScriptSourceMode mode) => mode switch
    {
        ScriptSourceMode.Embed => "embed",
        ScriptSourceMode.Strip => "strip",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown script source mode."),
    };

    /// <summary>The encoder's spelling, which is the command line's.</summary>
    public static string ToWire(CookEncoder encoder) => encoder switch
    {
        CookEncoder.Managed => "managed",
        CookEncoder.Native => "native",
        _ => throw new ArgumentOutOfRangeException(nameof(encoder), encoder, "Unknown cook encoder."),
    };

    /// <summary>The backend's spelling, which is <c>ssc</c>'s and the project manifest's.</summary>
    public static string ToWire(GraphicsBackend backend) => backend switch
    {
        GraphicsBackend.OpenGL => "opengl",
        GraphicsBackend.Vulkan => "vulkan",
        GraphicsBackend.D3D11 => "d3d11",
        GraphicsBackend.D3D12 => "d3d12",
        _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unknown graphics backend."),
    };

    // In the order they were given, NOT sorted: a shader rule emits one blob per
    // target and the order it was asked for is a declared order, exactly as a
    // rule's inputs are. Sorting here would make two command lines that ask for
    // different blob orders share one cache entry.
    private static string DescribeTargets(IReadOnlyList<GraphicsBackend> targets)
    {
        if (targets.Count == 0) return string.Empty;

        var text = new StringBuilder(32);
        for (int i = 0; i < targets.Count; i++)
        {
            if (i > 0) text.Append(',');
            text.Append(ToWire(targets[i]));
        }

        return text.ToString();
    }
}
