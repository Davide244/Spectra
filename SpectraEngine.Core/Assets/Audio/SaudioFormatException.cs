using System;
using System.IO;

namespace SpectraEngine.Core.Assets.Audio;

/// <summary>
/// A <c>.saudio</c> file was refused at read.
/// </summary>
/// <remarks>
/// <para><b>Every refusal this type carries exists to keep a malformed file from
/// becoming an out-of-range read.</b> The bytes normally arrive as a span into a
/// memory-mapped view, where an unchecked index is not an exception but an
/// access violation with no managed stack, no catch block and nothing in the log
/// naming the file. So every offset and every length is bounds-checked before it
/// is used, and the failure is this type saying what was wrong and what was
/// expected.</para>
/// <para><b>The other half of the audio failure surface is silent rather than
/// loud</b>, which is why a named exception is worth having here at all: a
/// header that parses and lies produces a sound that is merely wrong - a rate
/// field that disagrees with the payload plays the whole asset at the wrong
/// pitch, a loop end past the sound hangs the fill loop - and none of that
/// raises anything. Reading is the one moment those can still be caught.</para>
/// <para>It derives from <see cref="IOException"/> rather than the more exact
/// <c>InvalidDataException</c>, which is sealed, so a host that already handles
/// a failed content load generically keeps working without naming this type. It
/// is the same choice <c>SmodelFormatException</c> and <c>PackMountException</c>
/// made, for the same reason.</para>
/// </remarks>
public sealed class SaudioFormatException : IOException
{
    /// <summary>Creates the exception.</summary>
    public SaudioFormatException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception, carrying what actually failed underneath.</summary>
    public SaudioFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
