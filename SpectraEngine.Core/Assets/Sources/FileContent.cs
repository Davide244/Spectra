using System;
using System.IO;
using System.Threading;

namespace SpectraEngine.Core.Assets.Sources;

/// <summary>
/// The one place content bytes are read off the filesystem.
/// </summary>
/// <remarks>
/// The retry exists because a hot-reload notification arrives while the art tool
/// that saved the file may still hold a write lock on it, and one transient
/// sharing violation must not drop the reload. It lives here rather than in
/// <see cref="ImageDecoder"/>, where it started, so that
/// <see cref="LooseFileSource"/> and the decoder's own file convenience cannot
/// end up with two sets of retry constants that drift apart.
/// </remarks>
internal static class FileContent
{
    // How long an editor may plausibly hold a write lock on a file it is saving.
    private const int ReadRetryDelayMs = 20;
    private const int ReadAttempts = 3;

    /// <summary>
    /// Reads the whole file into a pooled blob the caller disposes. Any thread.
    /// </summary>
    /// <exception cref="IOException">The file could not be read.</exception>
    public static ContentBlob Read(string absolutePath)
    {
        for (int attempt = 0; attempt < ReadAttempts; attempt++)
        {
            try
            {
                // FileShare.ReadWrite: a file being written elsewhere is exactly
                // the case the retry exists for, so opening must not add a lock
                // of its own on top of it.
                using var stream = new FileStream(
                    absolutePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                // The length is read once and the blob sized to it: a file that
                // grows between the two would leave the tail of the rented
                // buffer holding the previous tenant's bytes, and ReadExactly is
                // what turns a short read into a failure instead of that.
                long length = stream.Length;
                if (length > int.MaxValue)
                    throw new IOException($"Content file '{absolutePath}' is larger than 2 GB.");

                ContentBlob blob = ContentBlob.Rent((int)length, out Span<byte> destination);
                try
                {
                    stream.ReadExactly(destination);
                }
                catch
                {
                    blob.Dispose();
                    throw;
                }

                return blob;
            }
            catch (IOException) when (attempt < ReadAttempts - 1)
            {
                Thread.Sleep(ReadRetryDelayMs);
            }
        }

        throw new IOException($"Could not read '{absolutePath}' after {ReadAttempts} attempts.");
    }
}
