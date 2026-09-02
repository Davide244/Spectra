using Spectra.Kitchen.Cache;
using Spectra.Kitchen.Cooking;
using Spectra.Kitchen.Rules;
using System;
using System.Buffers.Binary;
using System.Text;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// The composition of a cache key, field by field.
/// </summary>
/// <remarks>
/// <para><b>A key is a hash, so "the key moved" says nothing about WHICH field
/// moved it.</b> These tests therefore read the canonical stream directly where
/// the layout is being asserted, and only fall back to comparing keys where what
/// is under test is that one field participates at all.</para>
/// <para><b>The properties here are the ones whose failure is silent.</b> A field
/// left out of the key does not throw, does not warn and does not produce a wrong
/// picture: it produces a cache that answers "unchanged, skip it" about something
/// that changed, which surfaces days later as an artifact nobody can explain.</para>
/// </remarks>
public class CookCacheKeyTests
{
    private static readonly RuleDependency[] OneRead =
    [
        new("Textures/a.png", RuleDependencyKind.Read, (UInt128)0x1234),
    ];

    [Fact]
    public void The_stream_opens_with_the_tag_and_the_three_versions_it_is_specified_to_carry()
    {
        byte[] stream = Stream(RuleKind.RawCopy, ruleVersion: 7, CookSettingKeys.None, new CookSettings());

        Encoding.UTF8.GetString(stream, 0, 6).ShouldBe("SCOOK\0");
        BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(6)).ShouldBe(CookCacheKey.CookerVersion);
        BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(10)).ShouldBe((uint)RuleKind.RawCopy);
        BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(14)).ShouldBe(7u);
    }

    [Fact]
    public void The_rule_kind_and_the_rule_version_are_both_in_the_key()
    {
        UInt128 baseline = Key(RuleKind.RawCopy, 1, CookSettingKeys.None, new CookSettings());

        // The version is the only thing that invalidates a cached artifact when a
        // rule's CODE changes, so a rule whose output moved without it serves the
        // old bytes forever and nothing anywhere reports it.
        Key(RuleKind.RawCopy, 2, CookSettingKeys.None, new CookSettings()).ShouldNotBe(baseline);

        // The kind's numbers are append-only for exactly this reason: renumbering
        // one makes every artifact past it a cache hit for a different rule.
        Key(RuleKind.Image, 1, CookSettingKeys.None, new CookSettings()).ShouldNotBe(baseline);
    }

    [Fact]
    public void An_inputs_contents_are_in_the_key_and_so_is_the_order_it_was_read_in()
    {
        RuleDependency a = new("Textures/a.png", RuleDependencyKind.Read, (UInt128)1);
        RuleDependency b = new("Textures/b.png", RuleDependencyKind.Read, (UInt128)2);

        UInt128 baseline = Key([a, b]);

        RuleDependency changed = a with { ContentHash = (UInt128)99 };
        Key([changed, b]).ShouldNotBe(baseline);

        // Declared order, never sorted. A sort would hide a rule whose access
        // order became scheduling-dependent, which is the one thing the whole
        // byte-identity discipline exists to catch.
        Key([b, a]).ShouldNotBe(baseline);
    }

    [Fact]
    public void Adding_a_file_a_rule_probed_and_missed_gives_that_rule_a_different_key()
    {
        RuleDependency source = new("Materials/wall.spectramat", RuleDependencyKind.Read, (UInt128)7);
        const string Texture = "Textures/wall_brick.png";

        UInt128 missed = Key([source, new(Texture, RuleDependencyKind.ProbeMissing, UInt128.Zero)]);
        UInt128 found = Key([source, new(Texture, RuleDependencyKind.ProbeFound, UInt128.Zero)]);

        // The pin the whole design exists for, at the level of the key itself: a
        // miss lives in the trailing missing-probe list and a hit lives in the
        // inputs, so appearing where a rule looked changes BOTH counts and no
        // arrangement of the bytes can reproduce the recorded key.
        found.ShouldNotBe(missed);
    }

    [Fact]
    public void A_setting_moves_the_key_only_for_a_rule_that_declared_it()
    {
        var ship = new CookSettings { Profile = CookProfile.Ship };
        var fast = new CookSettings { Profile = CookProfile.Fast };

        UInt128 readsProfileUnderShip = Key(RuleKind.Map, 1, CookSettingKeys.Profile, ship);
        UInt128 readsProfileUnderFast = Key(RuleKind.Map, 1, CookSettingKeys.Profile, fast);
        readsProfileUnderFast.ShouldNotBe(readsProfileUnderShip);

        // And nothing else. A raw copy is the same bytes at every quality, so
        // hashing the whole settings block into every key would re-copy a
        // project's entire content tree the moment somebody switched profile.
        UInt128 ignoresProfileUnderShip = Key(RuleKind.RawCopy, 1, CookSettingKeys.None, ship);
        UInt128 ignoresProfileUnderFast = Key(RuleKind.RawCopy, 1, CookSettingKeys.None, fast);
        ignoresProfileUnderFast.ShouldBe(ignoresProfileUnderShip);

        // The declaration is what selects, not the setting's presence: a rule that
        // reads the script source mode is untouched by a profile switch too.
        var strip = new CookSettings { Profile = CookProfile.Fast, ScriptSource = ScriptSourceMode.Strip };
        Key(RuleKind.Script, 1, CookSettingKeys.ScriptSource, fast)
            .ShouldBe(Key(RuleKind.Script, 1, CookSettingKeys.ScriptSource, ship));
        Key(RuleKind.Script, 1, CookSettingKeys.ScriptSource, strip)
            .ShouldNotBe(Key(RuleKind.Script, 1, CookSettingKeys.ScriptSource, ship));
    }

    [Fact]
    public void Settings_that_cannot_change_a_cooked_payload_are_not_in_any_key()
    {
        var plain = new CookSettings();
        var everythingElse = new CookSettings
        {
            Jobs = 8,
            Loose = true,
            Strict = true,
            UseCache = false,
            OutputPath = "somewhere/else",
            ManifestPath = "cook.json",
        };

        // A rule may not declare any of these, so no declaration can reach them.
        // They decide how a cook is scheduled, where it is written and in what
        // container, and a cached payload is legitimately shared across all of it.
        Key(RuleKind.Map, 1, AllSettingKeys, everythingElse)
            .ShouldBe(Key(RuleKind.Map, 1, AllSettingKeys, plain));
    }

    [Fact]
    public void The_instruction_set_baseline_rides_in_every_key()
    {
        string token = InstructionSetBaseline.Token;

        token.ShouldNotBeNullOrWhiteSpace();
        token.ShouldContain("avx2=");
        (token.StartsWith("jit;", StringComparison.Ordinal) ||
         token.StartsWith("aot;", StringComparison.Ordinal)).ShouldBeTrue(token);

        // Measured, not assumed: the cook-dependency spike encoded one PNG with
        // one encoder and one set of settings and got two different BC7 payloads
        // on either side of the AVX2 boundary, visually equivalent and byte
        // different. A key without this hands one host the other host's artifact.
        byte[] stream = Stream(RuleKind.Image, 1, CookSettingKeys.None, new CookSettings());
        IndexOf(stream, Encoding.UTF8.GetBytes(token)).ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void The_format_versions_of_every_artifact_a_rule_could_emit_are_in_the_key()
    {
        byte[] stream = Stream(RuleKind.Map, 1, CookSettingKeys.None, new CookSettings());

        // Named in the stream rather than folded into one number, so a cache that
        // failed to invalidate can be read rather than bisected.
        foreach (string tool in new[] { "encoder", "shaderFormat", "mapFormat", "geometryFormat", "packFormat", "isa" })
            IndexOf(stream, Encoding.UTF8.GetBytes(tool)).ShouldBeGreaterThanOrEqualTo(0, tool);
    }

    private const CookSettingKeys AllSettingKeys =
        CookSettingKeys.Profile | CookSettingKeys.Targets | CookSettingKeys.ScriptSource |
        CookSettingKeys.Encoder | CookSettingKeys.KeepBrushSource;

    private static byte[] Stream(
        RuleKind kind, int ruleVersion, CookSettingKeys declared, CookSettings settings) =>
        CookCacheKey.BuildCanonicalStream(kind, ruleVersion, declared, settings, OneRead);

    private static UInt128 Key(
        RuleKind kind, int ruleVersion, CookSettingKeys declared, CookSettings settings) =>
        CookCacheKey.Compute(kind, ruleVersion, declared, settings, OneRead);

    private static UInt128 Key(RuleDependency[] dependencies) =>
        CookCacheKey.Compute(RuleKind.RawCopy, 1, CookSettingKeys.None, new CookSettings(), dependencies);

    private static int IndexOf(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle);
}
