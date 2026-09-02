using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System;

namespace SpectraEngine.Entities.Generator;

/// <summary>
/// Where in a file something is, as VALUES rather than as a
/// <see cref="Location"/>.
/// </summary>
/// <remarks>
/// <b>A <see cref="Location"/> holds the syntax tree it came from</b>, and a
/// syntax tree holds the whole file's green nodes; carrying one into an
/// incremental generator's model pins that tree in the cache and makes every
/// model unequal to the identical model from the next compilation. Three value
/// fields say the same thing and compare the way a model has to.
/// </remarks>
internal readonly record struct LocationInfo(string FilePath, TextSpan Span, LinePositionSpan LineSpan)
{
    /// <summary>The location this describes, rebuilt where a diagnostic is reported.</summary>
    public Location ToLocation() => Location.Create(FilePath, Span, LineSpan);

    /// <summary>The location of <paramref name="node"/>, or null when it has none.</summary>
    public static LocationInfo? From(SyntaxNode? node) => From(node?.GetLocation());

    /// <summary>The location of the first declaration of <paramref name="symbol"/>.</summary>
    public static LocationInfo? From(ISymbol? symbol)
    {
        if (symbol is null || symbol.Locations.Length == 0)
            return null;

        return From(symbol.Locations[0]);
    }

    private static LocationInfo? From(Location? location)
    {
        if (location is null || location.SourceTree is null)
            return null;

        return new LocationInfo(
            location.SourceTree.FilePath,
            location.SourceSpan,
            location.GetLineSpan().Span);
    }
}

/// <summary>
/// One diagnostic the transform stage decided on, kept as values so it can ride
/// the model into the source-output stage.
/// </summary>
/// <remarks>
/// <b>Diagnostics are decided in the TRANSFORM and reported in the OUTPUT.</b>
/// Reporting from the transform is not possible (there is no
/// <see cref="SourceProductionContext"/> there) and re-deciding them in the
/// output would mean the output stage needs the symbols the transform exists to
/// discard.
/// </remarks>
internal sealed record DiagnosticInfo(
    DiagnosticDescriptor Descriptor,
    LocationInfo? Location,
    EquatableArray<string> MessageArguments) : IEquatable<DiagnosticInfo>
{
    public static DiagnosticInfo Create(
        DiagnosticDescriptor descriptor,
        LocationInfo? location,
        params string[] messageArguments) =>
        new(descriptor, location, new EquatableArray<string>(messageArguments));

    /// <summary>The diagnostic this describes, rebuilt at report time.</summary>
    public Diagnostic ToDiagnostic()
    {
        var arguments = new object?[MessageArguments.Count];
        for (int i = 0; i < MessageArguments.Count; i++)
            arguments[i] = MessageArguments[i];

        return Diagnostic.Create(Descriptor, Location?.ToLocation(), arguments);
    }
}
