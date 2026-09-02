namespace Spectra.Kitchen.Cooking;

/// <summary>
/// What a cook is FOR, which is the one setting rules are allowed to branch on.
/// </summary>
/// <remarks>
/// It is part of the cache key of every rule that DECLARES it
/// (<c>CookSettingKeys.Profile</c>), so two profiles never share a cached artifact
/// of a rule whose output varies by profile, and they do share one for a rule
/// whose output cannot - a raw copy is the same bytes at every quality, and
/// re-copying a project's whole content tree on a profile switch would be a cache
/// that costs more than it saves. The names are the command line's (<c>ship</c>,
/// <c>fast</c>, <c>preview</c>) and there is deliberately no second vocabulary for
/// them.
/// </remarks>
public enum CookProfile
{
    /// <summary>Everything at final quality. The default, and what a build server runs.</summary>
    Ship,

    /// <summary>Quality traded for cook time, for a local iteration loop.</summary>
    Fast,

    /// <summary>What the editor's cooked-accurate preview asks for.</summary>
    Preview,
}

/// <summary>Whether cooked scripts keep their source text beside their bytecode.</summary>
public enum ScriptSourceMode
{
    /// <summary>Keep the source, so a stack trace can quote the line it names.</summary>
    Embed,

    /// <summary>Drop it.</summary>
    Strip,
}

/// <summary>Which block-compression encoder a cook uses.</summary>
/// <remarks>
/// <b>Managed is the baseline and cannot stop being one.</b> The editor hosts this
/// library in process and the editor is AOT-published, so an encoder that needs a
/// per-RID native binary can only ever be an opt-in throughput escape hatch.
/// </remarks>
public enum CookEncoder
{
    /// <summary>Pure managed, no native dependency.</summary>
    Managed,

    /// <summary>An opt-in native encoder, for cook throughput on a build machine.</summary>
    Native,
}
