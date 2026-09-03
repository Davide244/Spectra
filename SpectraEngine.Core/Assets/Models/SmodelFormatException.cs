using System;
using System.IO;

namespace SpectraEngine.Core.Assets.Models;

/// <summary>
/// A <c>.smodel</c> file was refused at read.
/// </summary>
/// <remarks>
/// <para><b>Reading a cooked model is the operation that throws, and every one of
/// its refusals exists to keep a malformed file from becoming an out-of-range
/// read.</b> The bytes normally arrive as a span into a memory-mapped view, where
/// an unchecked index is not an exception but an access violation with no managed
/// stack, no catch block and nothing in the log naming the file. So every offset
/// and every length is bounds-checked before it is used, and the failure is this
/// type carrying what was wrong and what was expected.</para>
/// <para>It derives from <see cref="IOException"/> rather than the more exact
/// <c>InvalidDataException</c>, which is sealed, so a host that already handles a
/// failed content load generically keeps working without naming this type. It is
/// the same choice <c>PackMountException</c> made, for the same reason.</para>
/// </remarks>
public sealed class SmodelFormatException : IOException
{
    /// <summary>Creates the exception.</summary>
    public SmodelFormatException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception, carrying what actually failed underneath.</summary>
    public SmodelFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
