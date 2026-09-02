using Spectra.Kitchen.Cooking;
using Spectra.Kitchen.Rules;
using SpectraEngine.Core;
using SpectraEngine.Core.Assets.Packs;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace Spectra.Kitchen.Cache;

/// <summary>
/// The identity of one rule run: <c>XxHash128</c> over a canonical byte stream of
/// everything that can change what that rule emits.
/// </summary>
/// <remarks>
/// <para><b>The stream, in this order and nothing else:</b></para>
/// <code>
/// "SCOOK\0"                                     6-byte tag, fixed length
/// u32 CookerVersion
/// u32 RuleKindId
/// u32 RuleVersion
/// u32 settingCount   || per setting, SORTED ordinal by key: str key, str value
/// u32 toolCount      || per tool, in a FIXED declared order: str name, str value
/// u32 inputCount     || per input, IN DECLARED ORDER: u128 pathId, u128 contentHash
/// u32 missingCount   || per missing probe, in declared order: u128 pathId
/// </code>
/// <para>where <c>str</c> is a <c>u32</c> UTF-8 byte count followed by the bytes,
/// and every integer is little-endian.</para>
/// <para><b>Everything is length-prefixed so no two different streams can collide
/// by concatenation.</b> Separator bytes would need an escaping rule the moment a
/// value could contain one, and the failure mode of getting that wrong is not an
/// exception: it is two different rule runs hashing to one key, which the cache
/// then reports as "unchanged, skip it".</para>
/// <para><b>Inputs are hashed IN DECLARED ORDER, not sorted.</b> The order is the
/// rule's first-access order, which <c>RuleContext</c> maintains deliberately; a
/// sort would hide a rule whose access order became scheduling-dependent, and that
/// is precisely the class of bug the byte-identity oracles exist to catch.</para>
/// <para><b>A path enters the stream as its pack asset id, not as its
/// characters.</b> That is <c>XxHash128</c> of the normalised path's UTF-8 with the
/// same ASCII case fold every asset cache and every pack entry already uses, so
/// two spellings of one asset are one dependency here exactly as they are one
/// entry there.</para>
/// <para><b>A probe that FOUND a file contributes a zero content hash, and a probe
/// that missed is in the trailing list instead.</b> Moving a path between those two
/// sections changes both counts, so a file appearing where a rule looked and did
/// not find one can never produce the recorded key. That is the whole reason the
/// negative dependencies are recorded at all.</para>
/// <para><b><see cref="CookerVersion"/> is raised by hand whenever this
/// composition or the shared machinery behind it changes.</b> It is what retires
/// every cached artifact in one move, and nothing derives it: a key layout that
/// changes without it serves old artifacts under new keys forever, and the
/// artifacts are valid, so nothing reports it.</para>
/// </remarks>
public static class CookCacheKey
{
    /// <summary>
    /// Version of the cache key's own composition. Raise it whenever the stream
    /// above changes shape, or whenever cooker-wide machinery that is not a rule
    /// can change what rules emit.
    /// </summary>
    public const uint CookerVersion = 1;

    /// <summary>
    /// Version of the block-compression encoder, carried as a tool version.
    /// </summary>
    /// <remarks>
    /// <b>The reserved slot, honestly named.</b> There is no BC encoder in this
    /// build, so the value says so; it becomes BCnEncoder.Net's assembly version
    /// when the image rule lands. Raise it BY HAND whenever the encoder can change
    /// its output, for the same reason a rule version is raised by hand: an
    /// encoder that changes without it serves cached artifacts from the old
    /// encoder forever.
    /// </remarks>
    public const string EncoderVersion = "none";

    // A fixed-length literal rather than a length-prefixed string: it is the same
    // six bytes in every stream, so prefixing it would cost four bytes to say so.
    private static ReadOnlySpan<byte> Tag => "SCOOK\0"u8;

    /// <summary>The key of one rule run over <paramref name="dependencies"/>.</summary>
    public static UInt128 Compute(
        RuleKind kind,
        int ruleVersion,
        CookSettingKeys declaredSettings,
        CookSettings settings,
        IReadOnlyList<RuleDependency> dependencies) =>
        XxHash128.HashToUInt128(
            BuildCanonicalStream(kind, ruleVersion, declaredSettings, settings, dependencies));

    /// <summary>
    /// The canonical bytes <see cref="Compute"/> hashes.
    /// </summary>
    /// <remarks>
    /// Exposed so a test can assert on the composition rather than only on the
    /// fact that two keys differ: a key is a hash, and a hash that moved says
    /// nothing about WHICH field moved it.
    /// </remarks>
    public static byte[] BuildCanonicalStream(
        RuleKind kind,
        int ruleVersion,
        CookSettingKeys declaredSettings,
        CookSettings settings,
        IReadOnlyList<RuleDependency> dependencies)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dependencies);

        var stream = new KeyStream();

        stream.Raw(Tag);
        stream.U32(CookerVersion);
        stream.U32((uint)kind);
        stream.U32((uint)ruleVersion);

        List<KeyValuePair<string, string>> pairs = CookSettingsDigest.Describe(settings, declaredSettings);
        stream.U32((uint)pairs.Count);
        for (int i = 0; i < pairs.Count; i++)
        {
            stream.Str(pairs[i].Key);
            stream.Str(pairs[i].Value);
        }

        WriteToolVersions(stream);

        // Two passes over one list rather than two lists: the inputs keep their
        // declared order and so do the misses, and building intermediate lists to
        // achieve that would allocate per rule for nothing.
        int inputs = 0, missing = 0;
        for (int i = 0; i < dependencies.Count; i++)
        {
            if (dependencies[i].IsMissing) missing++;
            else inputs++;
        }

        stream.U32((uint)inputs);
        for (int i = 0; i < dependencies.Count; i++)
        {
            RuleDependency dependency = dependencies[i];
            if (dependency.IsMissing) continue;

            stream.U128(PackAssetId.FromNormalized(dependency.Path));
            stream.U128(dependency.ContentHash);
        }

        stream.U32((uint)missing);
        for (int i = 0; i < dependencies.Count; i++)
        {
            RuleDependency dependency = dependencies[i];
            if (!dependency.IsMissing) continue;

            stream.U128(PackAssetId.FromNormalized(dependency.Path));
        }

        return stream.ToArray();
    }

    // A FIXED SEQUENCE, appended to and never reordered: the count and the order
    // are both hashed, so moving an entry re-keys every cached artifact in the
    // project. The names are here rather than derived so that adding a tool is a
    // visible edit to this list.
    private static void WriteToolVersions(KeyStream stream)
    {
        stream.U32(6);

        Tool(stream, "encoder", EncoderVersion);
        Tool(stream, "shaderFormat", EngineInfo.ShaderFormatVersion);
        Tool(stream, "mapFormat", EngineInfo.MapFormatVersion);
        Tool(stream, "geometryFormat", EngineInfo.GeometryFormatVersion);
        Tool(stream, "packFormat", EngineInfo.PackFormatVersion);

        // The instruction-set baseline is a tool version in the sense that
        // matters: it is a property of the toolchain rather than of the content or
        // of anything the user typed. See InstructionSetBaseline for the
        // measurement that put it here.
        Tool(stream, "isa", InstructionSetBaseline.Token);
    }

    private static void Tool(KeyStream stream, string name, string value)
    {
        stream.Str(name);
        stream.Str(value);
    }

    // A tool version is a string rather than a u32 because most of them are not
    // one number: an assembly version is four, and an instruction-set baseline is
    // a set. Forcing those through a u32 means inventing a lossy encoding per
    // tool, and a lossy encoding inside a cache key is a collision waiting to be
    // reported as a stale artifact.
    private static void Tool(KeyStream stream, string name, uint value) =>
        Tool(stream, name, value.ToString(CultureInfo.InvariantCulture));

    private static void Tool(KeyStream stream, string name, int value) =>
        Tool(stream, name, value.ToString(CultureInfo.InvariantCulture));

    private sealed class KeyStream
    {
        private readonly ArrayBufferWriter<byte> _bytes = new(256);

        public void Raw(ReadOnlySpan<byte> value) => _bytes.Write(value);

        public void U32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_bytes.GetSpan(sizeof(uint)), value);
            _bytes.Advance(sizeof(uint));
        }

        // Written as two explicit little-endian halves rather than by reinterpreting
        // the struct, so the stream is the same on a big-endian machine instead of
        // being silently reversed there.
        public void U128(UInt128 value)
        {
            Span<byte> span = _bytes.GetSpan(16);
            BinaryPrimitives.WriteUInt64LittleEndian(span, (ulong)value);
            BinaryPrimitives.WriteUInt64LittleEndian(span[8..], (ulong)(value >> 64));
            _bytes.Advance(16);
        }

        public void Str(string value)
        {
            int count = Encoding.UTF8.GetByteCount(value);
            U32((uint)count);

            if (count == 0) return;

            Encoding.UTF8.GetBytes(value, _bytes.GetSpan(count));
            _bytes.Advance(count);
        }

        public byte[] ToArray() => _bytes.WrittenSpan.ToArray();
    }
}
