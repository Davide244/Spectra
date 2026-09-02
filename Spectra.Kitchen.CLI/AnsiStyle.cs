namespace Spectra.Kitchen.CLI;

/// <summary>
/// The escape sequences, or nothing at all when colour is off.
/// </summary>
/// <remarks>
/// A copy of <c>ssc</c>'s, deliberately: the two tools share a look and neither
/// should have to reference the other to keep it. The colours are the same values
/// so a person switching between them is reading one palette.
/// </remarks>
internal readonly struct AnsiStyle
{
    private readonly bool _on;
    public AnsiStyle(bool on) { _on = on; }

    public bool Enabled => _on;
    private string Code(string seq) => _on ? $"\u001b[{seq}m" : string.Empty;

    public string Reset       => Code("0");
    public string Title       => Code("1;36");
    public string Header      => Code("1;33");
    public string Command     => Code("1;32");
    public string Flag        => Code("32");
    public string Placeholder => Code("36");
    public string Value       => Code("35");
    public string Dim         => Code("2");
    public string Error       => Code("31;1");
    public string Warning     => Code("33;1");
    public string Info        => Code("36;1");
    public string Success     => Code("32;1");
    public string Path        => Code("36");
}

internal static class ConsoleColor
{
    public static bool ShouldUseForStderr()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
            return false;
        var force = Environment.GetEnvironmentVariable("FORCE_COLOR");
        bool forced = !string.IsNullOrEmpty(force) && force != "0";
        if (!forced && Console.IsErrorRedirected) return false;
        bool vtReady = ConsoleVT.TryEnableForStderr();
        return vtReady || forced;
    }

    public static bool ShouldUseForStdout()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
            return false;
        var force = Environment.GetEnvironmentVariable("FORCE_COLOR");
        bool forced = !string.IsNullOrEmpty(force) && force != "0";
        if (!forced && Console.IsOutputRedirected) return false;
        bool vtReady = ConsoleVT.TryEnableForStdout();
        return vtReady || forced;
    }
}
