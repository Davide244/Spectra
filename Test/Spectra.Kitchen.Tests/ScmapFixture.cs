using System;
using System.Numerics;
using Spectra.Kitchen.Maps;
using SpectraEngine.Core;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Maps.Compiled;
using SpectraEngine.Core.Scene;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// The one compiled-map fixture, shared by the in-process tests and by the
/// two-process determinism oracle.
/// </summary>
/// <remarks>
/// <para><b>Every value in it is a literal</b>: fixed ids, fixed names, fixed
/// floats, fixed cells. The determinism oracle compares the bytes two separate
/// processes produce from this function, so anything derived from a clock, a
/// machine path, a random source or an iteration order would make the oracle
/// report a difference that is not the one it is hunting.</para>
/// <para><b>The orders are deliberately wrong three different ways.</b> Assets are
/// added in an order that is neither alphabetical nor the order their strings
/// would sort in; nodes are named so pre-order is not alphabetical; and cells are
/// added in an order the directory must sort out of. A fixture whose natural order
/// already matched the canonical one would pass just as happily with the ordering
/// rules removed.</para>
/// </remarks>
internal static class ScmapFixture
{
    /// <summary>How many section codes this build claims and steps over: ENTT, ECON, SCPT, LUAB, LUAS.</summary>
    public const int ReservedEmptySections = 5;

    /// <summary>The scene name, which is the first string interned after the empty one.</summary>
    public const string SceneName = "Determinism";

    /// <summary>Asset paths, in the order the fixture references them.</summary>
    public static readonly string[] AssetPaths =
    [
        "Materials/zulu.spectramat",
        "Materials/alpha.spectramat",
        "Textures/mid.png",
        "Models/crate.smodel",
    ];

    /// <summary>Node names, in pre-order.</summary>
    public static readonly string[] NodeNames =
    [
        "World",
        "zeta_room",
        "Wall",
        "Cut",
        "alpha_room",
        "Lamp",
        "Crate",
    ];

    /// <summary>
    /// The whole string table, in the canonical first-reference order: the empty
    /// string, the scene name, the asset paths, then the node names.
    /// </summary>
    public static string[] ExpectedStrings()
    {
        var all = new string[1 + 1 + AssetPaths.Length + NodeNames.Length];
        all[0] = string.Empty;
        all[1] = SceneName;
        AssetPaths.CopyTo(all, 2);
        NodeNames.CopyTo(all, 2 + AssetPaths.Length);
        return all;
    }

    /// <summary>
    /// The authored local transform of each node, index-aligned to
    /// <see cref="NodeNames"/>.
    /// </summary>
    /// <remarks>
    /// Public so the bit-identity test compares the file against what was
    /// AUTHORED. Reading the expectation back out of the same file would compare
    /// the file to itself and pass however the floats were mangled on the way in.
    /// </remarks>
    public static readonly Transform[] Transforms =
    [
        Identity,
        Placed(12.5f, 0f, -4.25f),
        Placed(1.0000001f, -3.3333333f, 1.4012985e-45f),
        Placed(-0.5f, 2.5f, 0.5f),
        Placed(-64f, 0f, 128f),
        Placed(0f, 3f, 0f),
        Placed(2f, 0.5f, -2f),
    ];

    /// <summary>The fixture's bytes.</summary>
    public static byte[] Build() => CreateBuilder().Build(Digest, EngineInfo.MapFormatVersion);

    /// <summary>The digest the fixture stamps: a literal, so two processes agree.</summary>
    public static UInt128 Digest => new(0x0123456789ABCDEFul, 0xFEDCBA9876543210ul);

    /// <summary>The fixture, before it is written, so a test can perturb it.</summary>
    public static ScmapBuilder CreateBuilder()
    {
        var builder = new ScmapBuilder(SceneName);

        builder.AddSpawn(new ScmapSpawnSource(
            new Vector3(0f, 64.5f, -12.25f),
            Quaternion.CreateFromYawPitchRoll(0.75f, 0f, 0f)));

        builder.AddAsset(new ScmapAssetSource(PackEntryKind.Material, AssetPaths[0], 0x1111_2222_3333_4444ul));
        builder.AddAsset(new ScmapAssetSource(PackEntryKind.Material, AssetPaths[1], 0x5555_6666_7777_8888ul));
        builder.AddAsset(new ScmapAssetSource(PackEntryKind.Image, AssetPaths[2], 0x9999_AAAA_BBBB_CCCCul));
        builder.AddAsset(new ScmapAssetSource(PackEntryKind.Model, AssetPaths[3], 0xDDDD_EEEE_FFFF_0000ul));

        // Pre-order, parent index strictly less than the child's own.
        builder.AddNode(new ScmapNodeSource(
            NodeId(0), NodeNames[0], -1, Transforms[0], ScmapPayloadKind.None));

        builder.AddNode(new ScmapNodeSource(
            NodeId(1), NodeNames[1], 0, Transforms[1], ScmapPayloadKind.None));

        builder.AddNode(new ScmapNodeSource(
            NodeId(2), NodeNames[2], 1, Transforms[2], ScmapPayloadKind.StaticWorldBrush));

        builder.AddNode(new ScmapNodeSource(
            NodeId(3), NodeNames[3], 1, Transforms[3],
            ScmapPayloadKind.StaticWorldBrush,
            ScmapPayloadFlags.SubtractiveBrush));

        builder.AddNode(new ScmapNodeSource(
            NodeId(4), NodeNames[4], 0, Transforms[4], ScmapPayloadKind.None));

        builder.AddNode(new ScmapNodeSource(
            NodeId(5), NodeNames[5], 4, Transforms[5], ScmapPayloadKind.None));

        builder.AddNode(new ScmapNodeSource(
            NodeId(6), NodeNames[6], 4, Transforms[6],
            ScmapPayloadKind.MeshInstance,
            ScmapPayloadFlags.IsEntityOwned,
            PayloadIndex: 3));

        // Out of canonical order on every axis, so the sort has something to do.
        builder.AddChunk(Cell(2, 0, -1));
        builder.AddChunk(Cell(-3, 4, 0));
        builder.AddChunk(Cell(2, 0, -9));
        builder.AddChunk(Cell(-3, 1, 7));
        builder.AddChunk(Cell(0, 0, 0));

        return builder;
    }

    /// <summary>The cells the fixture carries, in the order the directory must put them.</summary>
    public static ChunkCoord[] SortedCells =>
    [
        new(-3, 1, 7),
        new(-3, 4, 0),
        new(0, 0, 0),
        new(2, 0, -9),
        new(2, 0, -1),
    ];

    /// <summary>A literal id per node, so two processes stamp the same bytes.</summary>
    public static Guid NodeId(int index) =>
        Guid.Parse($"3f2a1c88-4b6d-4a19-9d0e-77c1f0a2b3{index:x2}");

    private static Transform Identity => new()
    {
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        Scale = Vector3.One,
    };

    private static Transform Placed(float x, float y, float z) => new()
    {
        // A rotation that is not the identity and not axis-aligned, and a scale
        // that is not one, because the round-trip claim is bit identity of ten
        // floats rather than of three.
        Position = new Vector3(x, y, z),
        Rotation = Quaternion.Normalize(new Quaternion(0.1f, -0.7f, 0.3f, 0.64f)),
        Scale = new Vector3(1.5f, 0.25f, 3.0000002f),
    };

    private static ScmapChunkSource Cell(int x, int y, int z)
    {
        var coord = new ChunkCoord(x, y, z);

        // Deliberately NOT the cell cube: a border-spanning brush is owned by one
        // cell and overhangs it, so the directory carries the true render bounds.
        return new ScmapChunkSource(
            coord,
            new Aabb(coord.MinCorner - new Vector3(0.5f), coord.MaxCorner + new Vector3(1.25f)));
    }
}
