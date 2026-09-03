using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;

namespace Spectra.Kitchen.Cache;

/// <summary>
/// Modification time plus size per input, and it short-circuits ONE thing:
/// re-hashing a file whose bytes cannot have changed.
/// </summary>
/// <remarks>
/// <para><b>Content hashes are the truth. This is an optimisation and may never
/// become anything else.</b> Timestamp-keyed invalidation gets two ordinary
/// workflows wrong in opposite directions: a <c>git checkout</c> rewrites every
/// timestamp without changing a byte, so a timestamp cache rebuilds a project that
/// did not change; and reverting a file to content it held before leaves a new
/// timestamp on identical bytes, so the rebuild it forces produces exactly the
/// artifact that was already cached. Hashing answers both correctly, and this
/// class only decides whether the hash has to be recomputed.</para>
/// <para><b>Which is why it is a separate file from the graph.</b> Discarding it
/// costs a pass over the inputs and can never cost correctness, so it must be
/// independently discardable: a stat cache that could only be thrown away
/// alongside the dependency graph would make a cheap repair expensive.</para>
/// <para><b>The residual hazard is stated rather than hidden.</b> A file rewritten
/// with different content, the same length and a timestamp the filesystem reports
/// as unchanged is served from this cache with its old hash. That is inherent to
/// every stat cache ever written, it needs sub-granularity edits to reach, and
/// <c>--no-cache</c> is the escape hatch.</para>
/// <para><b>One lock over the whole of <see cref="TryGetHash"/>, read included,
/// because the scheduler asks from N workers.</b> Hashing outside the lock would
/// be the faster shape and it makes two workers able to hash one file twice: the
/// ANSWER is the same either way, and <see cref="ShortCircuits"/> and
/// <see cref="Rehashes"/> would then depend on scheduling, which is a diagnostic
/// counter nobody can compare between two runs. What is serialised is a re-hash,
/// which only happens for a file that actually changed, so it is proportional to
/// the edit rather than to the project.</para>
/// </remarks>
public sealed class StatCache
{
    private const uint Magic = 0x54415343; // "CSAT" little-endian
    private const uint FormatVersion = 1;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private int _shortCircuits;
    private int _rehashes;
    private bool _dirty;

    /// <summary>Files whose hash was answered without reading them.</summary>
    public int ShortCircuits { get { lock (_gate) return _shortCircuits; } }

    /// <summary>Files that had to be read and hashed.</summary>
    public int Rehashes { get { lock (_gate) return _rehashes; } }

    /// <summary>Whether anything changed since this was loaded.</summary>
    public bool IsDirty { get { lock (_gate) return _dirty; } }

    /// <summary>Entries held.</summary>
    public int Count { get { lock (_gate) return _entries.Count; } }

    /// <summary>
    /// The content hash of the file at <paramref name="fullPath"/>, or false when
    /// there is nothing there.
    /// </summary>
    /// <param name="contentPath">Normalised content-relative path, which is the key.</param>
    /// <param name="fullPath">Where it is on this machine.</param>
    /// <param name="hash">The hash of its bytes.</param>
    public bool TryGetHash(string contentPath, string fullPath, out UInt128 hash)
    {
        lock (_gate)
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                // Dropped rather than kept: an entry for a file that is gone would be
                // served the moment a file of the same length reappeared with the same
                // timestamp, and a deleted-then-restored input is exactly the case
                // somebody hits while bisecting.
                if (_entries.Remove(contentPath)) _dirty = true;

                hash = UInt128.Zero;
                return false;
            }

            long ticks = info.LastWriteTimeUtc.Ticks;
            long length = info.Length;

            if (_entries.TryGetValue(contentPath, out Entry entry) &&
                entry.Ticks == ticks &&
                entry.Length == length)
            {
                _shortCircuits++;
                hash = entry.Hash;
                return true;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(fullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable is not absent, but for the cache the answer is the same:
                // no hash, so no key, so no hit, so the rule runs and reports the real
                // failure itself.
                hash = UInt128.Zero;
                return false;
            }

            _rehashes++;
            hash = XxHash128.HashToUInt128(bytes);
            _entries[contentPath] = new Entry(ticks, length, hash);
            _dirty = true;
            return true;
        }
    }

    /// <summary>Loads the stat cache, or an empty one when it is absent or unreadable.</summary>
    public static StatCache Load(string path)
    {
        var cache = new StatCache();

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return cache;
        }

        try
        {
            cache.Read(bytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException)
        {
            // An unreadable stat cache is discarded in silence and costs a pass
            // over the inputs. It is the one part of the cache whose loss cannot
            // be wrong, which is why it does not get a diagnostic.
            cache._entries.Clear();
        }

        return cache;
    }

    /// <summary>Writes the stat cache, creating its directory.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        lock (_gate)
        {
            // Sorted, so the file is a function of what is in it rather than of the
            // order a dictionary happened to enumerate. Nothing reads these bytes for
            // identity, but a cache file that churns on every save is a cache file
            // nobody can diff when it misbehaves.
            var keys = new List<string>(_entries.Keys);
            keys.Sort(StringComparer.Ordinal);

            var bytes = new List<byte>(16 + (keys.Count * 48));
            CacheBytes.U32(bytes, Magic);
            CacheBytes.U32(bytes, FormatVersion);
            CacheBytes.U32(bytes, (uint)keys.Count);

            foreach (string key in keys)
            {
                Entry entry = _entries[key];
                CacheBytes.Str(bytes, key);
                CacheBytes.U64(bytes, (ulong)entry.Ticks);
                CacheBytes.U64(bytes, (ulong)entry.Length);
                CacheBytes.U128(bytes, entry.Hash);
            }

            File.WriteAllBytes(path, [.. bytes]);
            _dirty = false;
        }
    }

    private void Read(ReadOnlySpan<byte> bytes)
    {
        var reader = new CacheReader(bytes);
        if (reader.U32() != Magic) throw new InvalidDataException("Not a stat cache.");
        if (reader.U32() != FormatVersion) throw new InvalidDataException("Stat cache version mismatch.");

        uint count = reader.U32();
        for (uint i = 0; i < count; i++)
        {
            string key = reader.Str();
            long ticks = (long)reader.U64();
            long length = (long)reader.U64();
            UInt128 hash = reader.U128();
            _entries[key] = new Entry(ticks, length, hash);
        }
    }

    private readonly record struct Entry(long Ticks, long Length, UInt128 Hash);
}
