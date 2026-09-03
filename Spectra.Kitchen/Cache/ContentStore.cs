using System;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Threading;

namespace Spectra.Kitchen.Cache;

/// <summary>
/// The content-addressed store of cooked payloads:
/// <c>.spectra-cook/cas/&lt;2 hex&gt;/&lt;30 hex&gt;</c>.
/// </summary>
/// <remarks>
/// <para><b>A payload's name IS its hash</b>, so two rules that emit identical
/// bytes share one file and a re-cook that produces what is already there writes
/// nothing. That is also what makes a hit checkable: the store can verify what it
/// hands back against the name it was found under, which no timestamp-keyed cache
/// can do.</para>
/// <para><b>Uncompressed, deliberately.</b> The pack writer decides a payload's
/// codec, and storing a compressed form here would make the cache's contents
/// depend on a pack-level choice that is not in the cache key. It would also
/// forfeit the property above, since the entry's name is a hash of the COOKED
/// bytes.</para>
/// <para><b>Two hex characters of shard.</b> Not for lookup speed, which a
/// filesystem gives for free, but because a single directory of a hundred thousand
/// entries is slow to list and hostile to every tool a person would point at it.
/// </para>
/// <para><b>A write is a temp file plus a rename, and a read re-hashes.</b> The
/// two together are what stop a cook that was killed mid-write from being served
/// as a hit forever: the rename is atomic, so a partial file never acquires the
/// name, and if one somehow does the read reports a miss and the rule re-runs.
/// Re-hashing costs a pass over bytes that were just read from disk, against a
/// wrong artifact shipping, which is not a trade worth thinking about twice.</para>
/// <para><b>It holds no lock, and that is the stronger answer rather than the
/// lazier one.</b> The scheduler stores payloads from N workers, but the design
/// above already survives concurrent writers: a temp file per writer, an atomic
/// rename onto a name that is a hash of the bytes being written, and a read that
/// verifies what it got. That survives two <c>scook</c> PROCESSES sharing one
/// project's cache, which no lock taken in this one could, and it means the disk
/// write - the slowest thing in a cook that is hitting its cache - is not
/// serialised behind the graph.</para>
/// </remarks>
public sealed class ContentStore
{
    private const string CasFolder = "cas";
    private const int ShardLength = 2;

    private static int _tempCounter;

    private readonly string _casRoot;

    /// <summary>Opens (without creating) the store under <paramref name="cacheRoot"/>.</summary>
    public ContentStore(string cacheRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _casRoot = Path.Combine(cacheRoot, CasFolder);
    }

    /// <summary>The <c>cas/</c> directory, which may not exist until something is stored.</summary>
    public string Root => _casRoot;

    /// <summary>Whether a payload with this hash is present and readable.</summary>
    public bool Contains(UInt128 hash) => File.Exists(PathOf(hash));

    /// <summary>
    /// Stores <paramref name="payload"/> and returns its hash, which is its name.
    /// </summary>
    public UInt128 Put(ReadOnlySpan<byte> payload)
    {
        UInt128 hash = XxHash128.HashToUInt128(payload);
        string full = PathOf(hash);

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        // Always written rather than skipped when the name exists: skipping would
        // leave a corrupt entry (which TryGet reports as a miss) corrupt forever,
        // so the rule would re-run on every cook and never repair the store.
        string temp = full + ".t" +
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" +
            Interlocked.Increment(ref _tempCounter).ToString(CultureInfo.InvariantCulture);

        try
        {
            using (FileStream stream = File.Create(temp))
                stream.Write(payload);

            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        return hash;
    }

    /// <summary>
    /// The payload stored under <paramref name="hash"/>, or false when it is
    /// absent, unreadable, or does not hash to its own name.
    /// </summary>
    public bool TryGet(UInt128 hash, out byte[] payload)
    {
        string full = PathOf(hash);

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            payload = [];
            return false;
        }

        if (XxHash128.HashToUInt128(bytes) != hash)
        {
            payload = [];
            return false;
        }

        payload = bytes;
        return true;
    }

    /// <summary>The path a payload with this hash is stored at.</summary>
    public string PathOf(UInt128 hash)
    {
        // Upper-case hex throughout the cooker: the manifest prints ids and hashes
        // this way, so a person can grep one string in both places.
        string name = hash.ToString("X32", CultureInfo.InvariantCulture);
        return Path.Combine(_casRoot, name[..ShardLength], name[ShardLength..]);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leaked temp file costs disk and nothing else; failing a cook on
            // its own cleanup would cost the artifact.
        }
    }
}
