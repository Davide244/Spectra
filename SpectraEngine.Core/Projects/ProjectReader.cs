using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Serialization;
using SpectraEngine.Core.Windowing;
using System;
using System.Text.Json;

namespace SpectraEngine.Core.Projects;

/// <summary>
/// Reads a project manifest, carrying through every member it does not
/// recognise.
/// </summary>
public static class ProjectReader
{
    /// <exception cref="ProjectFormatException">The document is malformed, or names a value outside a closed vocabulary.</exception>
    public static SpectraProject Read(ReadOnlySpan<byte> utf8)
    {
        utf8 = CanonicalJson.StripBom(utf8);
        var reader = new Utf8JsonReader(utf8, CanonicalJson.ReaderOptions);

        try
        {
            var project = new SpectraProject();
            ReadProject(ref reader, utf8, project);
            return project;
        }
        catch (ProjectFormatException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new ProjectFormatException(
                $"The project file is not valid JSON: {ex.Message}",
                ex.BytePositionInLine ?? reader.TokenStartIndex, ex);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new ProjectFormatException(ex.Message, reader.TokenStartIndex, ex);
        }
    }

    private static void ReadProject(ref Utf8JsonReader reader, ReadOnlySpan<byte> utf8, SpectraProject project)
    {
        Expect(ref reader, JsonTokenType.StartObject, "the project root must be an object");

        bool sawMinimumReadable = false;
        int anchor = -1;

        while (NextMember(ref reader, out string member))
        {
            switch (member)
            {
                case ProjectFormat.FormatVersionMember:
                    project.FormatVersion = ReadInt(ref reader, member);
                    anchor = 0;
                    break;

                case ProjectFormat.MinimumReadableMember:
                    project.MinimumReadableVersion = ReadInt(ref reader, member);
                    RefuseUnreadable(project.MinimumReadableVersion, ref reader);
                    sawMinimumReadable = true;
                    anchor = 1;
                    break;

                case ProjectFormat.EngineMember:
                    project.Engine = ReadString(ref reader, member);
                    anchor = 2;
                    break;

                case ProjectFormat.NameMember:
                    project.Name = ReadString(ref reader, member);
                    anchor = 3;
                    break;

                case ProjectFormat.IdMember:
                    project.Id = ReadGuid(ref reader);
                    anchor = 4;
                    break;

                case ProjectFormat.StartupMapMember:
                    project.StartupMap = ReadString(ref reader, member);
                    anchor = 5;
                    break;

                case ProjectFormat.MapsMember:
                    ReadStringArray(ref reader, member, project.Maps);
                    anchor = 6;
                    break;

                case ProjectFormat.DisplayMember:
                    ReadDisplay(ref reader, utf8, project.Display);
                    anchor = 7;
                    break;

                case ProjectFormat.DefaultBackendMember:
                    project.DefaultBackend = ReadBackend(ref reader, member);
                    anchor = 8;
                    break;

                case ProjectFormat.AllowedBackendsMember:
                    ReadBackendArray(ref reader, member, project.AllowedBackends);
                    anchor = 9;
                    break;

                default:
                    // 'packs', 'input', 'bootScript', 'entityDefinitions' and
                    // 'settings' all land here: specified, and with nothing in
                    // the tree to bind to yet.
                    project.Unknown.Add(new PreservedMember(
                        member, CanonicalJson.CaptureValue(ref reader, utf8), anchor));
                    break;
            }
        }

        if (!sawMinimumReadable)
            RefuseUnreadable(project.MinimumReadableVersion, ref reader);
    }

    private static void RefuseUnreadable(int minimumReadable, ref Utf8JsonReader reader)
    {
        if (minimumReadable > EngineInfo.ProjectFormatVersion)
        {
            throw new ProjectFormatException(
                $"This project needs a reader for project format {minimumReadable}; this engine "
                + $"implements {EngineInfo.ProjectFormatVersion}.", reader.TokenStartIndex);
        }
    }

    private static void ReadDisplay(
        ref Utf8JsonReader reader, ReadOnlySpan<byte> utf8, ProjectDisplay display)
    {
        Expect(ref reader, JsonTokenType.StartObject, "'display' must be an object");

        int anchor = -1;
        while (NextMember(ref reader, out string member))
        {
            switch (member)
            {
                case ProjectFormat.WidthMember:
                    display.Width = ReadPositiveInt(ref reader, member);
                    anchor = 0;
                    break;
                case ProjectFormat.HeightMember:
                    display.Height = ReadPositiveInt(ref reader, member);
                    anchor = 1;
                    break;
                case ProjectFormat.VsyncMember:
                    display.Vsync = ReadBool(ref reader, member);
                    anchor = 2;
                    break;
                case ProjectFormat.ModeMember:
                    string mode = ReadString(ref reader, member);
                    if (!ProjectFormat.TryParseWindowMode(mode, out WindowMode parsed))
                    {
                        throw Fail(ref reader,
                            $"'{ProjectFormat.ModeMember}' must be 'windowed' or 'fullscreen', not '{mode}'");
                    }
                    display.Mode = parsed;
                    anchor = 3;
                    break;
                default:
                    display.Unknown.Add(new PreservedMember(
                        member, CanonicalJson.CaptureValue(ref reader, utf8), anchor));
                    break;
            }
        }
    }

    private static GraphicsBackend ReadBackend(ref Utf8JsonReader reader, string member)
    {
        string value = ReadString(ref reader, member);
        if (!ProjectFormat.TryParseBackend(value, out GraphicsBackend backend))
        {
            // Never a fall-through to a default backend. A mistyped 'd3d1' that
            // silently became OpenGL would ship a game rendering through a path
            // nobody tested, which is worse than refusing to start.
            throw Fail(ref reader,
                $"'{member}' must be one of opengl, vulkan, d3d11, d3d12; got '{value}'");
        }
        return backend;
    }

    private static void ReadBackendArray(
        ref Utf8JsonReader reader, string member, List<GraphicsBackend> into)
    {
        Expect(ref reader, JsonTokenType.StartArray, $"'{member}' must be an array");

        while (Read(ref reader) && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw Fail(ref reader, $"'{member}' must be an array of backend names");

            string value = reader.GetString() ?? string.Empty;
            if (!ProjectFormat.TryParseBackend(value, out GraphicsBackend backend))
                throw Fail(ref reader, $"'{member}' names an unknown backend '{value}'");

            into.Add(backend);
        }
    }

    private static void ReadStringArray(ref Utf8JsonReader reader, string member, List<string> into)
    {
        Expect(ref reader, JsonTokenType.StartArray, $"'{member}' must be an array");

        while (Read(ref reader) && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw Fail(ref reader, $"'{member}' must be an array of strings");

            into.Add(reader.GetString() ?? string.Empty);
        }
    }

    // --- primitives ---------------------------------------------------------

    private static bool NextMember(ref Utf8JsonReader reader, out string member)
    {
        reader.Read();
        if (reader.TokenType == JsonTokenType.EndObject)
        {
            member = string.Empty;
            return false;
        }
        member = reader.GetString() ?? string.Empty;
        return true;
    }

    private static bool Read(ref Utf8JsonReader reader)
    {
        if (reader.Read()) return true;
        throw Fail(ref reader, "the document ended in the middle of a value");
    }

    private static void Expect(ref Utf8JsonReader reader, JsonTokenType expected, string what)
    {
        Read(ref reader);
        if (reader.TokenType != expected)
            throw Fail(ref reader, what);
    }

    private static string ReadString(ref Utf8JsonReader reader, string member)
    {
        Read(ref reader);
        if (reader.TokenType != JsonTokenType.String)
            throw Fail(ref reader, $"'{member}' must be a string");
        return reader.GetString() ?? string.Empty;
    }

    private static int ReadInt(ref Utf8JsonReader reader, string member)
    {
        Read(ref reader);
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int value))
            throw Fail(ref reader, $"'{member}' must be a whole number");
        return value;
    }

    private static int ReadPositiveInt(ref Utf8JsonReader reader, string member)
    {
        int value = ReadInt(ref reader, member);
        // A zero-sized window is not creatable, and the failure would otherwise
        // surface three layers down inside a windowing backend.
        if (value <= 0)
            throw Fail(ref reader, $"'{member}' must be greater than zero, not {value}");
        return value;
    }

    private static bool ReadBool(ref Utf8JsonReader reader, string member)
    {
        Read(ref reader);
        if (reader.TokenType is not (JsonTokenType.True or JsonTokenType.False))
            throw Fail(ref reader, $"'{member}' must be true or false");
        return reader.GetBoolean();
    }

    private static Guid ReadGuid(ref Utf8JsonReader reader)
    {
        Read(ref reader);
        if (reader.TokenType != JsonTokenType.String || !reader.TryGetGuid(out Guid value))
            throw Fail(ref reader, $"'{ProjectFormat.IdMember}' must be a GUID string");
        return value;
    }

    private static ProjectFormatException Fail(ref Utf8JsonReader reader, string message) =>
        new(message, reader.TokenStartIndex);
}
