using System;
using System.IO;

namespace SpectraEngine.Core.Assets.Packs;

/// <summary>
/// A <c>.spack</c> file was refused at mount.
/// </summary>
/// <remarks>
/// <para><b>Mounting is the one pack operation that throws.</b> Every lookup
/// after it degrades — a miss is a miss, an unreadable entry is a miss with a
/// warning — because the engine's degrade-don't-crash policy must not depend on
/// which source answered. A pack that is truncated, has the wrong magic, demands
/// a newer reader or fails its digest is a different thing: none of its answers
/// can be trusted, so it never becomes a source at all.</para>
/// <para>It derives from <see cref="IOException"/> rather than the more exact
/// <c>InvalidDataException</c>, which is sealed, so a host that already handles a
/// failed content load generically keeps working without naming this type.</para>
/// </remarks>
public sealed class PackMountException : IOException
{
    /// <summary>Creates the exception.</summary>
    public PackMountException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception, carrying what actually failed underneath.</summary>
    public PackMountException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
