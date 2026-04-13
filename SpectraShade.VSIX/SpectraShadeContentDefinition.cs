using System.ComponentModel.Composition;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Utilities;

namespace SpectraShade.VSIX;

/// <summary>
/// Registers the .spectrashade file extension with Visual Studio
/// and associates it with the SpectraShade content type.
/// </summary>
public static class SpectraShadeContentDefinition
{
    [Export]
    [Name("spectrashade")]
    [BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]
    internal static ContentTypeDefinition? SpectraShadeContentType;

    [Export]
    [FileExtension(".spectrashade")]
    [ContentType("spectrashade")]
    internal static FileExtensionToContentTypeDefinition? SpectraShadeFileExtension;
}
