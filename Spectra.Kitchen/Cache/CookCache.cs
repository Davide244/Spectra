using Spectra.Kitchen.Cooking;
using Spectra.Kitchen.Rules;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;

namespace Spectra.Kitchen.Cache;

/// <summary>What a cached rule run produced, ready to be emitted again.</summary>
/// <param name="Dependencies">Exactly what the rule touched when it ran, misses included.</param>
/// <param name="Emissions">Its outputs, with their payloads read back out of the store.</param>
public sealed record CachedRun(
    IReadOnlyList<RuleDependency> Dependencies,
    IReadOnlyList<RuleEmission> Emissions);

/// <summary>
/// The incremental cook: a dependency graph, a content-addressed store of
/// payloads, and a stat cache that only ever saves a re-hash.
/// </summary>
/// <remarks>
/// <para><b>A hit is decided by RESTATING the recorded observations against
/// today's filesystem, never by predicting what the rule would do.</b> Each
/// recorded dependency is looked up again as it stands now, a key is built from
/// the result, and the run is replayed only if that key equals the recorded one.
/// The cache therefore needs no model of any rule and cannot be wrong about one:
/// anything it cannot restate identically is a miss.</para>
/// <para><b>Which is what makes a negative dependency work.</b> A path the rule
/// probed and did not find is restated as found the moment somebody adds the file,
/// which moves it out of the trailing missing-probe list and into the inputs. Both
/// counts change, so the key cannot match, so the rule re-runs. Without that, a
/// watch loop serves a broken cook forever while reporting success, which is the
/// single most common incremental-build bug and the reason this whole shape
/// exists.</para>
/// <para><b>A rule that reported a diagnostic is never cached.</b> The store holds
/// bytes and the graph holds dependencies; neither holds what the rule SAID. A hit
/// would therefore drop a warning on every run after the first, which is an
/// incremental build quietly hiding the thing it was asked to be loud about. A
/// rule with something to say simply re-runs and says it again, and it costs
/// nothing today because no rule in this build reports anything.</para>
/// <para><b>Every failure here degrades to a miss.</b> An unreadable graph, a
/// payload that has left the store, a dependency the filesystem refuses: each one
/// means the rule runs, which is slow and correct. The cache has exactly one way
/// to be wrong, and it is to claim a hit it should not have.</para>
/// <para><b>The scheduler calls this from N workers, and each of the three parts
/// is safe for its own reason rather than all of them behind one lock.</b>
/// <see cref="CookGraph"/> and <see cref="StatCache"/> each hold a lock, because
/// each is a dictionary somebody mutates; <see cref="ContentStore"/> holds none,
/// because its temp-file-plus-rename already survives concurrent writers and
/// survives them across processes too. One lock over the whole cache would have
/// been simpler and would have put the payload write - the slowest step - inside
/// it, which is most of what there is to parallelise. What makes the COMPOSITION
/// safe is the work list rather than any lock: exactly one work item per source
/// path, so no two workers ever read and write one record.</para>
/// </remarks>
public sealed class CookCache
{
    /// <summary>The folder a project's cook cache lives in, at the project root.</summary>
    public const string DirectoryName = ".spectra-cook";

    private const string GraphFileName = "graph.bin";
    private const string StatFileName = "stat.bin";

    private readonly string _root;
    private readonly CookGraph _graph;
    private readonly StatCache _stat;
    private readonly ContentStore _store;

    private int _hits;
    private int _misses;

    /// <summary>Opens the cache rooted at <paramref name="root"/>, reading what is there.</summary>
    public CookCache(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        _root = Path.GetFullPath(root);
        _graph = CookGraph.Load(Path.Combine(_root, GraphFileName));
        _stat = StatCache.Load(Path.Combine(_root, StatFileName));
        _store = new ContentStore(_root);
    }

    /// <summary>The cache folder.</summary>
    public string Root => _root;

    /// <summary>Rules answered from the cache.</summary>
    public int Hits => Volatile.Read(ref _hits);

    /// <summary>Rules that had to run.</summary>
    public int Misses => Volatile.Read(ref _misses);

    /// <summary>Inputs whose hash was answered without reading them.</summary>
    public int StatShortCircuits => _stat.ShortCircuits;

    /// <summary>Inputs that had to be read and hashed.</summary>
    public int StatRehashes => _stat.Rehashes;

    /// <summary>Why a graph file was discarded, or null.</summary>
    public string? DiscardedReason => _graph.DiscardedReason;

    /// <summary>
    /// Answers <paramref name="sourcePath"/> from the cache, or reports a miss.
    /// </summary>
    /// <param name="contentRoot">Absolute path of the project's content root.</param>
    /// <param name="sourcePath">Normalised content-relative path of the asset.</param>
    /// <param name="rule">The rule that would cook it.</param>
    /// <param name="settings">The settings this cook is running under.</param>
    /// <param name="run">The replay, on a hit.</param>
    public bool TryReplay(
        string contentRoot,
        string sourcePath,
        IRule rule,
        CookSettings settings,
        [NotNullWhen(true)] out CachedRun? run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(settings);

        run = null;

        if (!_graph.TryGet(sourcePath, out CookGraphRecord record))
        {
            Interlocked.Increment(ref _misses);
            return false;
        }

        IReadOnlyList<RuleDependency> restated = Restate(contentRoot, record.Dependencies);
        UInt128 key = CookCacheKey.Compute(
            rule.Kind, rule.Version, rule.SettingsRead, settings, restated);

        CookGeneration? generation = null;
        for (int i = 0; i < record.Generations.Count; i++)
        {
            if (record.Generations[i].Key != key) continue;

            generation = record.Generations[i];
            break;
        }

        if (generation is null)
        {
            Interlocked.Increment(ref _misses);
            return false;
        }

        var emissions = new List<RuleEmission>(generation.Outputs.Count);
        for (int i = 0; i < generation.Outputs.Count; i++)
        {
            CachedOutput output = generation.Outputs[i];
            if (!_store.TryGet(output.ContentHash, out byte[] payload))
            {
                // A payload that has left the store is a miss, not a failure. The
                // rule runs, emits the same bytes, and Put restores the entry, so
                // a cache somebody deleted half of repairs itself.
                Interlocked.Increment(ref _misses);
                return false;
            }

            emissions.Add(new RuleEmission(output.Path, output.Kind, payload));
        }

        Interlocked.Increment(ref _hits);

        // The RESTATED dependencies rather than the recorded ones. They are equal
        // in every field the key hashes - that is what made this a hit - and the
        // restated list is the one that describes the filesystem the manifest is
        // about to be written against.
        run = new CachedRun(restated, emissions);
        return true;
    }

    /// <summary>Records one rule run so a later cook can replay it.</summary>
    public void Record(
        string sourcePath,
        IRule rule,
        CookSettings settings,
        IReadOnlyList<RuleDependency> dependencies,
        IReadOnlyList<RuleEmission> emissions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(emissions);

        var outputs = new CachedOutput[emissions.Count];
        for (int i = 0; i < emissions.Count; i++)
        {
            RuleEmission emission = emissions[i];
            UInt128 hash = _store.Put(emission.Payload);
            outputs[i] = new CachedOutput(emission.Path, emission.Kind, hash, emission.Payload.Length);
        }

        UInt128 key = CookCacheKey.Compute(
            rule.Kind, rule.Version, rule.SettingsRead, settings, dependencies);

        _graph.Set(sourcePath, dependencies, key, outputs);
    }

    /// <summary>Drops records for assets that are no longer in the project.</summary>
    public void RetainOnly(IReadOnlyCollection<string> liveSourcePaths) => _graph.RetainOnly(liveSourcePaths);

    /// <summary>Persists the graph and the stat cache, if either moved.</summary>
    public void Save()
    {
        if (_graph.IsDirty) _graph.Save(Path.Combine(_root, GraphFileName));
        if (_stat.IsDirty) _stat.Save(Path.Combine(_root, StatFileName));
    }

    // The recorded dependency list is a list of QUESTIONS the rule asked. Asking
    // them again is all a hit test is, and asking them in the recorded order is
    // what keeps the restated key comparable to the recorded one at all.
    private IReadOnlyList<RuleDependency> Restate(
        string contentRoot, IReadOnlyList<RuleDependency> recorded)
    {
        var restated = new RuleDependency[recorded.Count];
        for (int i = 0; i < recorded.Count; i++)
        {
            RuleDependency dependency = recorded[i];
            string full = Path.Combine(
                contentRoot, dependency.Path.Replace('/', Path.DirectorySeparatorChar));

            restated[i] = dependency.Kind switch
            {
                // A read has to be re-read (through the stat cache, which usually
                // answers from mtime and size) because its CONTENTS are in the key.
                RuleDependencyKind.Read => _stat.TryGetHash(dependency.Path, full, out UInt128 hash)
                    ? new RuleDependency(dependency.Path, RuleDependencyKind.Read, hash)
                    : new RuleDependency(dependency.Path, RuleDependencyKind.ProbeMissing, UInt128.Zero),

                // Existence only, so no hash and no read: a rule that asked whether
                // a file exists does not change when its bytes do, and hashing it
                // anyway would make every probe cost a read.
                _ => File.Exists(full)
                    ? new RuleDependency(dependency.Path, RuleDependencyKind.ProbeFound, UInt128.Zero)
                    : new RuleDependency(dependency.Path, RuleDependencyKind.ProbeMissing, UInt128.Zero),
            };
        }

        return restated;
    }
}
