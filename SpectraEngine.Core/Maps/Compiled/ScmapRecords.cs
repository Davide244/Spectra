using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using SpectraEngine.Core.Assets.Packs;

namespace SpectraEngine.Core.Maps.Compiled;

/// <summary>
/// One 16-byte <c>ASTB</c> record: an asset this map references, by PATH.
/// </summary>
/// <remarks>
/// <para><b>A <c>MaterialRef.Id</c> is never written here, and that is a reviewed
/// rule rather than a note.</b> The registry hands out ids in per-process
/// interning order and they are meaningful only for the life of the process, so a
/// cook that serialised one produces a file that loads perfectly in the test that
/// wrote it and mis-textures the entire world the moment a second map interns
/// first. The wrong version is also SHORTER CODE, which is why it is written down
/// where the field is. <see cref="PathString"/> indexes <c>STRT</c>, a load walks
/// the table in order calling <c>MaterialRegistry.Intern</c>, and the resulting
/// file-index-to-<c>MaterialRef</c> remap is applied to every geometry
/// reference.</para>
/// <para><b><see cref="ContentHash"/> is advisory.</b> It says which cooked bytes
/// the map was baked against, so a mismatch against the resident pack WARNS and
/// never fails: a texture recooked on its own is the normal case a patch pack
/// exists for, and refusing the map would make every content fix a full map
/// rebake.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct ScmapAssetEntry
{
    /// <summary>
    /// What kind of asset this is, as a <see cref="PackEntryKind"/> value widened
    /// to a word.
    /// </summary>
    /// <remarks>
    /// The pack's vocabulary rather than a second one of this format's own: an
    /// <c>ASTB</c> row names exactly the thing a pack entry names, and two enums
    /// for one concept is how a material becomes a model in a log line.
    /// </remarks>
    public readonly uint Kind;

    /// <summary>Index into <c>STRT</c> of this asset's logical content-relative path.</summary>
    public readonly uint PathString;

    /// <summary>Low 64 bits of the cooked payload's content hash, or zero when unknown.</summary>
    public readonly ulong ContentHash;

    /// <summary>Builds one asset-table record. Every field is assigned.</summary>
    public ScmapAssetEntry(PackEntryKind kind, uint pathString, ulong contentHash)
    {
        Kind = (uint)kind;
        PathString = pathString;
        ContentHash = contentHash;
    }

    /// <summary>The asset kind, as the enum rather than as the raw word.</summary>
    public PackEntryKind AssetKind => (PackEntryKind)Kind;
}

/// <summary>
/// One 80-byte <c>NODE</c> record: an authored node, in pre-order.
/// </summary>
/// <remarks>
/// <para><b><see cref="ParentIndex"/> is always less than the record's own
/// index</b>, which is what makes a single forward pass rebuild the whole graph
/// with no fixup table: a parent exists by the time its child is read. It is
/// pre-order because that is <c>SceneNode.Traverse</c>'s order, and sibling order
/// is authored data: traversal order is placement order is carve order is the
/// bit-identity oracles.</para>
/// <para><b>The transform is the authored ten floats, never a world matrix.</b>
/// A world matrix is derived by composition, and replaying the same composition
/// reproduces bit-identical matrices, which is what the compile cache's exact
/// matrix equality and the bake oracle both depend on. Storing a baked matrix
/// would break that oracle in a way that looks like a floating-point mystery.</para>
/// <para><b>Every authored node gets a record, brushes dissolved into chunks
/// included.</b> Only the brush GEOMETRY payload is dropped. A node is eighty
/// bytes and dropping one saves nothing worth having while breaking identity: the
/// id is what entity wiring, the id index, undo and every script reference resolve
/// through, and a target name IS a node name, so dropping wall nodes would
/// silently break connections that target them.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct ScmapNodeRecord
{
    /// <summary>Bit 3 of <c>PayloadFlags</c>: the low bit of the declared realm.</summary>
    public const int RealmShift = 3;

    /// <summary>Bit 5 of <c>PayloadFlags</c>: the low bit of the declared state.</summary>
    public const int StateShift = 5;

    /// <summary>The two-bit width both the realm and the state fields have.</summary>
    public const int TwoBitMask = 0x3;

    /// <summary>
    /// The node's <c>Guid</c> as sixteen RFC 4122 bytes, held as the integer those
    /// bytes read as.
    /// </summary>
    /// <remarks>
    /// <b>Big-endian on purpose, and therefore not a <see cref="Guid"/> field.</b>
    /// <c>System.Guid</c>'s in-memory layout byte-swaps its first three components
    /// on a little-endian machine, so a raw <c>Guid</c> field would put the bytes
    /// on disk in an order that does not match the hex the authored map spells the
    /// same id with. Storing the RFC order means an id can be grepped for in
    /// <c>map.json</c> and found in a hex dump of the compiled map, character for
    /// character. Convert with <see cref="EncodeId"/> and <see cref="DecodeId"/>,
    /// which are the only two places the byte order is spelled.
    /// </remarks>
    public readonly UInt128 Id;

    /// <summary>Index into <c>STRT</c> of the node's name, which is also its target name.</summary>
    public readonly uint NameString;

    /// <summary>Index of this node's parent record, or -1 for a root. Always less than this record's index.</summary>
    public readonly int ParentIndex;

    /// <summary>Authored local position.</summary>
    public readonly Vector3 LocalPosition;

    /// <summary>Authored local rotation, stored x, y, z, w.</summary>
    public readonly Quaternion LocalRotation;

    /// <summary>Authored local scale.</summary>
    public readonly Vector3 LocalScale;

    /// <summary>What the payload is. See <see cref="ScmapPayloadKind"/>.</summary>
    public readonly ushort PayloadKindRaw;

    /// <summary>Flags plus the two-bit realm and state fields. See <see cref="ScmapPayloadFlags"/>.</summary>
    public readonly ushort PayloadFlagsRaw;

    /// <summary>
    /// Index into whatever table <see cref="PayloadKindRaw"/> names, or zero when
    /// the kind has no table. Unused for a baked brush, which carries no geometry
    /// of its own.
    /// </summary>
    public readonly uint PayloadIndex;

    /// <summary>Reserved; written zero.</summary>
    public readonly ulong Reserved;

    /// <summary>Builds one node record. Every field is assigned.</summary>
    public ScmapNodeRecord(
        Guid id,
        uint nameString,
        int parentIndex,
        Vector3 localPosition,
        Quaternion localRotation,
        Vector3 localScale,
        ScmapPayloadKind payloadKind,
        ScmapPayloadFlags payloadFlags,
        ScmapNodeRealm declaredRealm = ScmapNodeRealm.Inherit,
        ScmapNodeState declaredState = ScmapNodeState.Inherit,
        uint payloadIndex = 0)
    {
        Id = EncodeId(id);
        NameString = nameString;
        ParentIndex = parentIndex;
        LocalPosition = localPosition;
        LocalRotation = localRotation;
        LocalScale = localScale;
        PayloadKindRaw = (ushort)payloadKind;
        PayloadFlagsRaw = ComposeFlags(payloadFlags, declaredRealm, declaredState);
        PayloadIndex = payloadIndex;
        Reserved = 0;
    }

    /// <summary>The node's id, decoded from its RFC 4122 bytes.</summary>
    public Guid NodeId => DecodeId(Id);

    /// <summary>What the payload is.</summary>
    public ScmapPayloadKind PayloadKind => (ScmapPayloadKind)PayloadKindRaw;

    /// <summary>The flag bits, with the realm and state fields masked out.</summary>
    public ScmapPayloadFlags PayloadFlags =>
        (ScmapPayloadFlags)(PayloadFlagsRaw & ~((TwoBitMask << RealmShift) | (TwoBitMask << StateShift)));

    /// <summary>The declared realm, never the effective one.</summary>
    public ScmapNodeRealm DeclaredRealm => (ScmapNodeRealm)((PayloadFlagsRaw >> RealmShift) & TwoBitMask);

    /// <summary>The declared state, never the effective one. May read <see cref="ScmapNodeState.Invalid"/>.</summary>
    public ScmapNodeState DeclaredState => (ScmapNodeState)((PayloadFlagsRaw >> StateShift) & TwoBitMask);

    /// <summary>
    /// Whether the brush subtracts. False for any payload kind that is not a
    /// brush, because the bit is meaningless there and a future kind is free to
    /// leave it zero.
    /// </summary>
    public bool IsSubtractiveBrush =>
        PayloadKind is ScmapPayloadKind.StaticWorldBrush or ScmapPayloadKind.PartBrush
        && (PayloadFlagsRaw & (ushort)ScmapPayloadFlags.SubtractiveBrush) != 0;

    /// <summary>
    /// Packs the flag bits and the two enum fields into one half-word.
    /// </summary>
    /// <remarks>
    /// One expression of the bit allocation, called by the writer and mirrored by
    /// the accessors above. Two expressions of a bitfield is how a realm of
    /// <c>Server</c> gets written into bit 1 and read back as a flag.
    /// </remarks>
    public static ushort ComposeFlags(ScmapPayloadFlags flags, ScmapNodeRealm realm, ScmapNodeState state)
    {
        int reserved = (TwoBitMask << RealmShift) | (TwoBitMask << StateShift);
        if (((ushort)flags & reserved) != 0)
        {
            throw new ArgumentException(
                $"Payload flags '{flags}' set a bit inside the realm or state field. Those four bits are a " +
                "pair of two-bit enums, not flags; pass them as the realm and state arguments.", nameof(flags));
        }

        return (ushort)((ushort)flags
            | (((int)realm & TwoBitMask) << RealmShift)
            | (((int)state & TwoBitMask) << StateShift));
    }

    /// <summary>
    /// Turns a <see cref="Guid"/> into the integer its RFC 4122 bytes read as on
    /// this machine.
    /// </summary>
    public static UInt128 EncodeId(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);
        return BinaryPrimitives.ReadUInt128LittleEndian(bytes);
    }

    /// <summary>The inverse of <see cref="EncodeId"/>.</summary>
    public static Guid DecodeId(UInt128 value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128LittleEndian(bytes, value);
        return new Guid(bytes, bigEndian: true);
    }
}

/// <summary>
/// One 64-byte <c>CHDR</c> record: where one chunk cell's baked geometry lives.
/// </summary>
/// <remarks>
/// <para><b><see cref="BoundsMin"/> and <see cref="BoundsMax"/> are the cell's
/// TRUE render bounds, never the cell cube.</b> A border-spanning brush is owned
/// by exactly one cell and its surfaces routinely overhang, so culling against the
/// cell cube makes the overhang vanish while it is plainly visible.</para>
/// <para><b>A cell with no owned render geometry has <see cref="MeshSize"/>
/// zero</b>, which is legal and common rather than an error: the compile produces
/// no mesh artifact for a resident-only cell, and the directory mirrors the
/// compile.</para>
/// <para>Records are sorted by <c>ChunkCoord.CompareTo</c>, which is the pinned
/// canonical order the determinism oracles use and which also makes a point lookup
/// a binary search.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct ScmapChunkRecord
{
    /// <summary>Cell coordinate on the X axis.</summary>
    public readonly int X;

    /// <summary>Cell coordinate on the Y axis.</summary>
    public readonly int Y;

    /// <summary>Cell coordinate on the Z axis.</summary>
    public readonly int Z;

    /// <summary>Minimum corner of the cell's true render bounds.</summary>
    public readonly Vector3 BoundsMin;

    /// <summary>Maximum corner of the cell's true render bounds.</summary>
    public readonly Vector3 BoundsMax;

    /// <summary>Offset of this cell's mesh blob from the start of <c>CMSH</c>.</summary>
    public readonly uint MeshOffset;

    /// <summary>Bytes in this cell's mesh blob; zero when the cell owns no render geometry.</summary>
    public readonly uint MeshSize;

    /// <summary>Offset of this cell's BSP blob from the start of <c>CBSP</c>.</summary>
    public readonly uint BspOffset;

    /// <summary>Bytes in this cell's BSP blob; zero when the cell has no tree.</summary>
    public readonly uint BspSize;

    /// <summary>Index into <c>RGNI</c>, which is reserved and never written. Zero.</summary>
    public readonly uint RegionIndex;

    /// <summary>Per-cell flags. None defined in v1; written zero.</summary>
    public readonly uint Flags;

    /// <summary>Reserved; written zero.</summary>
    public readonly uint Reserved;

    /// <summary>Builds one chunk-directory record. Every field is assigned.</summary>
    public ScmapChunkRecord(
        int x,
        int y,
        int z,
        Vector3 boundsMin,
        Vector3 boundsMax,
        uint meshOffset,
        uint meshSize,
        uint bspOffset,
        uint bspSize)
    {
        X = x;
        Y = y;
        Z = z;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
        MeshOffset = meshOffset;
        MeshSize = meshSize;
        BspOffset = bspOffset;
        BspSize = bspSize;
        RegionIndex = 0;
        Flags = 0;
        Reserved = 0;
    }
}

/// <summary>
/// One 32-byte <c>META</c> spawn record.
/// </summary>
/// <remarks>
/// Twenty-eight bytes of content padded to thirty-two, so the array can be cast in
/// place out of a section that is itself 16-byte aligned. The padding is declared
/// as a field rather than left implicit, because an undeclared gap is exactly the
/// byte that picks up stack garbage and turns a byte-identity oracle red in a way
/// that is very hard to bisect.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct ScmapSpawn
{
    /// <summary>Where a player enters.</summary>
    public readonly Vector3 Position;

    /// <summary>Which way they face, stored x, y, z, w.</summary>
    public readonly Quaternion Rotation;

    /// <summary>Reserved; written zero.</summary>
    public readonly uint Reserved;

    /// <summary>Builds one spawn record. Every field is assigned.</summary>
    public ScmapSpawn(Vector3 position, Quaternion rotation)
    {
        Position = position;
        Rotation = rotation;
        Reserved = 0;
    }
}
