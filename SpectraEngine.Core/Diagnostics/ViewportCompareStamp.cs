using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Serialization;
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace SpectraEngine.Core.Diagnostics;

/// <summary>
/// What the last <c>--viewport-compare</c> run measured, left where another
/// process can find it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because a double sRGB encode is invisible to everything else.</b>
/// The shared route is a UNORM resource wearing an sRGB view on D3D11 and a
/// private sRGB target copied across a bridge on D3D12, and encoding a second
/// time washes the picture out with no exception, no HRESULT and nothing on the
/// debug layer. <c>--viewport-compare</c> is the only detector, it runs in the
/// demo executable against a windowless composited surface, and the editor shell
/// - which is the thing that has to decide whether to trust the composited
/// viewport - is a different process that cannot watch it happen.
/// </para>
/// <para>
/// <b>One record, not a history, and that is the claim being made.</b> The
/// question is "was the LATEST comparison on this machine green", so a file
/// holding the latest comparison answers it exactly. A list keyed by adapter
/// would answer a different and weaker question: whether some run, at some
/// point, on some driver, once agreed.
/// </para>
/// <para>
/// <b>Keyed by BACKEND, and the adapter is information rather than key - which
/// is a narrower claim than it should be, stated rather than hidden.</b> D3D11
/// hands its resolve target over directly while D3D12 goes through a D3D11On12
/// bridge, so a verdict for one says nothing about the other and the backend is
/// load-bearing. The adapter is not, because the producer cannot name it:
/// <see cref="Renderer.AdapterName"/> reports the SELECTION rather than the
/// device, and is the literal string <c>system default</c> unless
/// <c>--adapter=</c> was given. What that leaves unguarded is exactly one case -
/// a hybrid machine where the comparison ran on one GPU and the shell composites
/// on the other - and closing it means making the renderer name the adapter it
/// actually opened. The file is per user on the machine it measured, so
/// everything else about "this machine" is implicit in where it lives.
/// </para>
/// <para>
/// <b>A missing or damaged file is "no measurement", never an error.</b> It is a
/// per-user cache of a machine measurement; a shell that refused to start over
/// one would have turned a gate into a dependency.
/// </para>
/// </remarks>
/// <param name="Adapter">What the producer called the adapter it ran on, for a reader.</param>
/// <param name="Backend">The graphics backend it ran on.</param>
/// <param name="Green">Whether the two pictures agreed.</param>
/// <param name="RecordedUtc">When, so a reader can say how old the answer is.</param>
public sealed record ViewportCompareStamp(
    string Adapter, GraphicsBackend Backend, bool Green, DateTime RecordedUtc)
{
    /// <summary>Where the stamp lives for this user, beside the editor's own settings.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Spectra", "viewport-compare.json");

    /// <summary>
    /// Whether this stamp is a green verdict for the given backend.
    /// </summary>
    /// <remarks>
    /// The whole reason the stamp is read, in one place, so no caller invents a
    /// looser rule. A null stamp is not green, which is the right answer for a
    /// machine nobody has measured: the colour route has to be shown to work,
    /// never assumed to.
    /// </remarks>
    public static bool IsGreenFor(ViewportCompareStamp? stamp, GraphicsBackend backend) =>
        stamp is { Green: true } && stamp.Backend == backend;

    /// <summary>Reads the stamp, or null when there is nothing to read.</summary>
    public static ViewportCompareStamp? Load() => Load(DefaultPath);

    /// <summary>Reads from an explicit path, for tests.</summary>
    public static ViewportCompareStamp? Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
            return null;

        try
        {
            return Read(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the stamp. Failures are swallowed: a full disk must not turn a
    /// measurement into a failed run.
    /// </summary>
    /// <returns>Whether the file was written.</returns>
    public bool Save() => Save(DefaultPath);

    /// <summary>Writes to an explicit path, for tests.</summary>
    public bool Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
                Directory.CreateDirectory(folder);

            File.WriteAllBytes(path, CanonicalJson.Write(Write));
            return true;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return false;
        }
    }

    private void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("adapter", Adapter);
        writer.WriteString("backend", BackendName(Backend));
        writer.WriteBoolean("green", Green);
        writer.WriteString("recordedUtc", RecordedUtc.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private static ViewportCompareStamp? Read(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(CanonicalJson.StripBom(utf8), CanonicalJson.ReaderOptions);

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return null;

        string adapter = string.Empty;
        GraphicsBackend backend = GraphicsBackend.D3D11;
        bool haveBackend = false;
        bool green = false;
        DateTime recorded = DateTime.MinValue;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("adapter"))
            {
                reader.Read();
                adapter = reader.GetString() ?? string.Empty;
            }
            else if (reader.ValueTextEquals("backend"))
            {
                reader.Read();
                haveBackend = TryParseBackend(reader.GetString(), out backend);
            }
            else if (reader.ValueTextEquals("green"))
            {
                reader.Read();
                green = reader.GetBoolean();
            }
            else if (reader.ValueTextEquals("recordedUtc"))
            {
                reader.Read();
                DateTime.TryParse(
                    reader.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out recorded);
            }
            else
            {
                // A member a newer build wrote. Skipped rather than preserved:
                // this is a cache of one measurement, not an authored document.
                reader.Read();
                reader.Skip();
            }
        }

        // A stamp with no backend names nothing it could be a verdict about, so
        // it is the same as no stamp at all rather than a verdict that matches
        // whatever it is asked about.
        if (!haveBackend)
            return null;

        return new ViewportCompareStamp(adapter, backend, green, recorded);
    }

    /// <summary>
    /// Hand-written, never <c>Enum.ToString</c> or <c>Enum.Parse</c>: reflection
    /// over enum names is what trimming removes, so the round trip would work in
    /// every debug run and fail in a published one.
    /// </summary>
    private static string BackendName(GraphicsBackend backend) => backend switch
    {
        GraphicsBackend.OpenGL => "opengl",
        GraphicsBackend.Vulkan => "vulkan",
        GraphicsBackend.D3D11 => "d3d11",
        GraphicsBackend.D3D12 => "d3d12",
        _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unknown graphics backend."),
    };

    private static bool TryParseBackend(string? text, out GraphicsBackend backend)
    {
        switch (text)
        {
            case "opengl": backend = GraphicsBackend.OpenGL; return true;
            case "vulkan": backend = GraphicsBackend.Vulkan; return true;
            case "d3d11": backend = GraphicsBackend.D3D11; return true;
            case "d3d12": backend = GraphicsBackend.D3D12; return true;
            default: backend = GraphicsBackend.D3D11; return false;
        }
    }

    // Everything a damaged or unreachable file can throw, in one place so the
    // read and the write agree about what is survivable.
    private static bool IsRecoverable(Exception ex) =>
        ex is JsonException or IOException or UnauthorizedAccessException
            or InvalidOperationException or FormatException or NotSupportedException;
}
