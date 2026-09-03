using System;
using System.IO;

namespace Spectra.Kitchen.Models;

/// <summary>
/// A glTF or GLB file was refused, naming what was wrong.
/// </summary>
/// <remarks>
/// <para><b>Every refusal in this reader names the thing it refused, by number
/// or by string.</b> That is the stance <c>SimageReader</c> already takes and it
/// is the same argument: the failure mode of guessing at a construct you do not
/// implement is not an exception, it is an accessor read at a stride the file
/// never meant, which produces geometry that is merely wrong. A message carrying
/// <c>mode 5 (TRIANGLE_STRIP)</c> or <c>KHR_draco_mesh_compression</c> tells the
/// author what to re-export; "could not read the model" tells them
/// nothing.</para>
/// <para>It derives from <see cref="IOException"/> rather than the more exact
/// <c>InvalidDataException</c>, which is sealed - the same choice
/// <c>SmodelFormatException</c> and <c>PackMountException</c> already made, for
/// the same reason. <see cref="Rules.ModelRule"/> catches this one type for the
/// whole reader, so no refusal of a file can arrive at the cook session as
/// "the Model rule failed".</para>
/// </remarks>
public sealed class GltfFormatException : IOException
{
    /// <summary>Creates the exception.</summary>
    public GltfFormatException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception, carrying what actually failed underneath.</summary>
    public GltfFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
