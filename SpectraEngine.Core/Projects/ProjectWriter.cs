using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Serialization;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace SpectraEngine.Core.Projects;

/// <summary>Writes a <see cref="SpectraProject"/> as canonical UTF-8 JSON.</summary>
/// <remarks>
/// Every rule is <see cref="CanonicalJson"/>'s, so the project file and the map
/// document are byte-compatible in style: same indent, same line ending, same
/// escaping, same trailing newline. A person editing both in one session should
/// never notice they are different formats.
/// </remarks>
public static class ProjectWriter
{
    public static byte[] Write(SpectraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return CanonicalJson.Write(writer => WriteProject(writer, project));
    }

    private static void WriteProject(Utf8JsonWriter writer, SpectraProject project)
    {
        writer.WriteStartObject();

        CanonicalJson.Flush(writer, project.Unknown, -1);

        writer.WriteNumber(ProjectFormat.FormatVersionMember, project.FormatVersion);
        CanonicalJson.Flush(writer, project.Unknown, 0);

        writer.WriteNumber(ProjectFormat.MinimumReadableMember, project.MinimumReadableVersion);
        CanonicalJson.Flush(writer, project.Unknown, 1);

        writer.WriteString(ProjectFormat.EngineMember, project.Engine);
        CanonicalJson.Flush(writer, project.Unknown, 2);

        writer.WriteString(ProjectFormat.NameMember, project.Name);
        CanonicalJson.Flush(writer, project.Unknown, 3);

        writer.WriteString(ProjectFormat.IdMember, project.Id.ToString("D"));
        CanonicalJson.Flush(writer, project.Unknown, 4);

        // Omitted rather than written as null when a project has no maps yet:
        // an absent member reads as "not chosen", and null reads as a value
        // somebody set on purpose.
        if (!string.IsNullOrEmpty(project.StartupMap))
            writer.WriteString(ProjectFormat.StartupMapMember, project.StartupMap);
        CanonicalJson.Flush(writer, project.Unknown, 5);

        // One path per line. A map list is edited by hand and reviewed in a
        // diff, so adding a level should be one added line.
        var maps = new List<byte[]>(project.Maps.Count);
        foreach (string map in project.Maps)
            maps.Add(CanonicalJson.Compact(w => w.WriteStringValue(map)));
        CanonicalJson.WriteRecordArray(writer, ProjectFormat.MapsMember, maps);
        CanonicalJson.Flush(writer, project.Unknown, 6);

        writer.WritePropertyName(ProjectFormat.DisplayMember);
        writer.WriteRawValue(CompactDisplay(project.Display));
        CanonicalJson.Flush(writer, project.Unknown, 7);

        if (project.DefaultBackend is { } backend)
            writer.WriteString(ProjectFormat.DefaultBackendMember, ProjectFormat.ToWire(backend));
        CanonicalJson.Flush(writer, project.Unknown, 8);

        // An empty list is omitted, because "no restriction" is the absence of
        // a restriction rather than an empty one.
        if (project.AllowedBackends.Count > 0)
        {
            writer.WritePropertyName(ProjectFormat.AllowedBackendsMember);
            writer.WriteRawValue(CanonicalJson.Compact(w =>
            {
                w.WriteStartArray();
                foreach (GraphicsBackend allowed in project.AllowedBackends)
                    w.WriteStringValue(ProjectFormat.ToWire(allowed));
                w.WriteEndArray();
            }));
        }
        CanonicalJson.Flush(writer, project.Unknown, 9);

        writer.WriteEndObject();
    }

    private static byte[] CompactDisplay(ProjectDisplay display) => CanonicalJson.Compact(w =>
    {
        w.WriteStartObject();
        CanonicalJson.Flush(w, display.Unknown, -1);

        w.WriteNumber(ProjectFormat.WidthMember, display.Width);
        CanonicalJson.Flush(w, display.Unknown, 0);

        w.WriteNumber(ProjectFormat.HeightMember, display.Height);
        CanonicalJson.Flush(w, display.Unknown, 1);

        w.WriteBoolean(ProjectFormat.VsyncMember, display.Vsync);
        CanonicalJson.Flush(w, display.Unknown, 2);

        w.WriteString(ProjectFormat.ModeMember, ProjectFormat.ToWire(display.Mode));
        CanonicalJson.Flush(w, display.Unknown, 3);

        w.WriteEndObject();
    });
}
