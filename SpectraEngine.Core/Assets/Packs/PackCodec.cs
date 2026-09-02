namespace SpectraEngine.Core.Assets.Packs;

/// <summary>
/// How one pack entry's payload is stored, as one byte in
/// <see cref="PackEntry.Codec"/>.
/// </summary>
/// <remarks>
/// <para><b>Compression is per entry, never solid and never whole-pack.</b> Solid
/// compression destroys random access and mapping in place, and this engine
/// streams a sparse chunked open world whose load order is not knowable ahead of
/// time.</para>
/// <para><b><see cref="None"/> is the default and is not laziness.</b> BC-compressed
/// texture blocks and cooked geometry are already entropy-dense, and compressing
/// them forfeits the zero-copy read that is the entire point of the container.</para>
/// </remarks>
public enum PackCodec : byte
{
    /// <summary>
    /// Stored verbatim. The only codec whose payload can be read in place off a
    /// mapped view, so it is what every cooked binary format should use.
    /// </summary>
    None = 0,

    /// <summary>
    /// RFC 1951 deflate, via the in-box <c>DeflateStream</c>. No compression
    /// library is ever vendored: all the candidates carried undocumented
    /// NativeAOT posture and two of them a determinism hazard.
    /// </summary>
    Deflate = 1,

    /// <summary>
    /// Reserved, and not implemented. Zstandard ships in-box in
    /// <c>System.IO.Compression</c> in .NET 11 and is absent from .NET 10, which
    /// this solution targets. The id is nailed down now so the .NET 11 upgrade is
    /// an implementation rather than a format version.
    /// </summary>
    Zstandard = 2,
}
