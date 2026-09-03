using System;

namespace SpectraEngine.Core.Maps.Compiled;

/// <summary>
/// A <c>.scmap</c> file is not one, is a version this engine does not read, or is
/// internally inconsistent.
/// </summary>
/// <remarks>
/// Its own type rather than <see cref="InvalidOperationException"/> for the reason
/// every other format in this engine has one: a host that wants to degrade to a
/// blank level and say so has to be able to tell a malformed map from a bug in the
/// loader, and catching the base type catches both.
/// </remarks>
public sealed class ScmapFormatException : Exception
{
    /// <summary>Creates the exception with a message naming what was wrong.</summary>
    public ScmapFormatException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the failure underneath it.</summary>
    public ScmapFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
