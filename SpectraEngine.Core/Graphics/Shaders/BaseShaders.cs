using System;
using System.IO;
using System.Linq;

namespace SpectraEngine.Core.Graphics.Shaders;

/// <summary>
/// Accessor for the engine's built-in SpectraShade sources, embedded as
/// assembly resources so they survive AOT publishing and single-file deployment.
/// </summary>
public static class BaseShaders
{
    /// <summary>The built-in lit shader — diffuse + ambient from one directional light.</summary>
    public static string Lit => ReadEmbedded("Lit.spectrashade");

    /// <summary>The unlit per-vertex-coloured shader used by the debug-draw renderer.</summary>
    public static string DebugLine => ReadEmbedded("DebugLine.spectrashade");

    private static string ReadEmbedded(string fileName)
    {
        var assembly = typeof(BaseShaders).Assembly;
        string? resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.Ordinal));
        if (resourceName is null)
            throw new InvalidOperationException(
                $"Embedded shader resource '{fileName}' not found in {assembly.GetName().Name}.");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
