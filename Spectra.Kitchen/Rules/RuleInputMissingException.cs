using System;

namespace Spectra.Kitchen.Rules;

/// <summary>
/// A rule read a path with nothing at it.
/// </summary>
/// <remarks>
/// The miss is already recorded as a negative dependency by the time this is
/// thrown, so the rule re-runs once the file appears. The exception exists so a
/// rule can stop where it is rather than carrying a null through its own logic;
/// the session turns it into an <c>SC1002</c> against the rule's source file.
/// </remarks>
public sealed class RuleInputMissingException : Exception
{
    /// <summary>Creates the exception for <paramref name="contentPath"/>.</summary>
    public RuleInputMissingException(string contentPath, string sourcePath)
        : base($"Cooking '{sourcePath}' needs '{contentPath}', which is not in the content root.")
    {
        ContentPath = contentPath;
        SourcePath = sourcePath;
    }

    /// <summary>The content-relative path that was not there.</summary>
    public string ContentPath { get; }

    /// <summary>The asset being cooked when the read failed.</summary>
    public string SourcePath { get; }
}
