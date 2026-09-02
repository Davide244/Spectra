using System;

namespace Spectra.Kitchen.Cache;

/// <summary>
/// The cook settings a rule's OUTPUT can depend on, declared per rule.
/// </summary>
/// <remarks>
/// <para><b>Per rule, because the alternative invalidates the world.</b> Hashing
/// the whole settings block into every key means changing <c>--script-source</c>
/// re-cooks every texture in the project. A rule declares what it reads, the key
/// carries only that, and a settings change then invalidates exactly the rules
/// that read it and nothing else.</para>
/// <para><b>Only settings that can change a cooked PAYLOAD are here.</b>
/// <c>Jobs</c>, <c>Loose</c>, <c>OutputPath</c>, <c>ManifestPath</c> and
/// <c>UseCache</c> decide how a cook is scheduled, where it is written and in what
/// container - never what the bytes are - so a rule may not declare them and a
/// cached artifact is legitimately shared across all of them. <c>Strict</c> is
/// absent for a subtler reason: it changes the SEVERITY of diagnostics rather than
/// any payload, and the cache never serves a rule that reported one at all (see
/// <see cref="CookCache"/>), so a strict run and a lax run can only share a cache
/// entry for a rule that had nothing to say under either.</para>
/// <para><b>Flags rather than a list, so the declaration costs no allocation</b>
/// on a member that is read once per rule per cook.</para>
/// </remarks>
[Flags]
public enum CookSettingKeys
{
    /// <summary>The rule's output is the same under every setting.</summary>
    None = 0,

    /// <summary>Reads <c>CookSettings.Profile</c>: ship, fast or preview.</summary>
    Profile = 1 << 0,

    /// <summary>Reads <c>CookSettings.Targets</c>: which backends are cooked for.</summary>
    Targets = 1 << 1,

    /// <summary>Reads <c>CookSettings.ScriptSource</c>: embed or strip.</summary>
    ScriptSource = 1 << 2,

    /// <summary>Reads <c>CookSettings.Encoder</c>: managed or native.</summary>
    Encoder = 1 << 3,

    /// <summary>Reads <c>CookSettings.KeepBrushSource</c>.</summary>
    KeepBrushSource = 1 << 4,
}
