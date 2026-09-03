using System;
using System.Collections.Generic;
using System.Numerics;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// The maps the bake oracle is run over: four levels, each chosen because it is
/// the one shape in which a particular class of bake bug is visible.
/// </summary>
/// <remarks>
/// <para><b>A corpus rather than a fixture, because one map cannot fail in four
/// ways.</b> The oracle itself is one claim - cook, load, and the arrays the
/// runtime received are element-identical to a fresh cache-free compile of the
/// same source - and what a corpus buys is not more claims but more chances for
/// that one claim to be false. A single room exercises one submesh directory of
/// two entries, one cell that owns geometry, and no cell that carries a tree
/// without one, so three quarters of the file's shapes are simply never written.</para>
/// <para><b>Each fixture names what it alone exercises, and a test asserts
/// it.</b> <c>BakeOracleTests.Every_fixture_exercises_what_it_claims</c> measures
/// those properties off the compile rather than trusting this comment, because a
/// fixture that has quietly stopped biting is worse than one that was never
/// written: it reports coverage that does not exist.</para>
/// <para><b>Every material path is UNIQUE per fixture instance</b>, for the reason
/// <c>MapFixture</c> already records: <c>MaterialRegistry</c> is process-global and
/// append-only, so a fixture that wants an interned id and an asset row to
/// DISAGREE cannot get there with paths another test may already have
/// interned.</para>
/// </remarks>
internal sealed class BakeCorpus
{
    /// <summary>
    /// The repo's standing coincident-plane regression fixture: a doorway cut
    /// flush through its wall's base, standing on a floor whose top reaches that
    /// same plane.
    /// </summary>
    /// <remarks>
    /// Delegated to <see cref="MapFixture"/> rather than restated here, so the
    /// room the determinism oracles bake and the room this one bakes are one
    /// arrangement. <c>CoplanarCutSealingTests</c> carries the same geometry
    /// against the live compile, and <c>DemoPlayArea</c>'s 1.2-sunit doorway is
    /// the same case in the shipped demo.
    /// </remarks>
    public const string FlushCoplanarRoom = "flush-coplanar-room";

    /// <summary>
    /// Subtractive cuts of every shape a negative can take: a tunnel open at both
    /// ends, a sealed pocket touching no face of the solid it is inside, a cut
    /// straddling a cell boundary, and an aperture through a second brush.
    /// </summary>
    /// <remarks>
    /// A subtractive brush emits no skin of its own; its whole contribution is
    /// the cavity walls seeded into each brush it cuts, attributed to the CUT
    /// brush's slot and wearing the negative's own materials. That makes it the
    /// case most likely to be silently wrong, and the sealed pocket the sharpest
    /// of the four: nothing about it reaches a face of the slab, so every one of
    /// its six walls exists only because the seeding put it there.
    /// </remarks>
    public const string Cavities = "cavities";

    /// <summary>
    /// One cell wearing seven materials, six of them interned in the REVERSE of
    /// the order the node walk references them, plus a brush wearing none.
    /// </summary>
    /// <remarks>
    /// Submesh ordering is load-bearing for byte identity and the two orders in
    /// play are genuinely different: a <c>ChunkMesh</c> is in ascending
    /// <c>MaterialRef.Id</c>, which is per-process interning order, and the file
    /// is in ascending asset-table row, which the map itself decides. Interning
    /// backwards is what makes the bake's sort a real permutation rather than the
    /// identity, and the brush wearing no material is what puts
    /// <c>ScmapFormat.NoAssetIndex</c> in a directory beside real rows.
    /// </remarks>
    public const string Palette = "palette";

    /// <summary>
    /// A level across a dozen cells: a slab spanning five cells on one axis, a
    /// brush straddling a cell face, a brush straddling a cell corner on all
    /// three axes, an island a hundred units away, and one brush rotated off
    /// every axis.
    /// </summary>
    /// <remarks>
    /// <para>A brush is OWNED by one cell and RESIDENT in every cell its inflated
    /// box touches, so a slab that spans five cells puts its whole skin in one
    /// cell's mesh and leaves the other four carrying a tree and no mesh at all.
    /// That asymmetry is written into the file - a cell with a tree and no mesh
    /// still gets a <c>CBSP</c> blob - and no single-cell fixture can reach
    /// it.</para>
    /// <para><b>One material, deliberately.</b> Every cell's submesh directory is
    /// one entry long, so this fixture is structurally blind to a submesh
    /// ordering bug - which is the point: it is the fixture that proves the
    /// corpus is not four copies of one test.</para>
    /// </remarks>
    public const string Sprawl = "sprawl";

    /// <summary>Every fixture, in the order the oracle runs them.</summary>
    public static readonly string[] Names = [FlushCoplanarRoom, Cavities, Palette, Sprawl];

    private readonly string _name;
    private readonly string _stamp;
    private readonly MapFixture? _room;

    private BakeCorpus(string name, string stamp, MapFixture? room)
    {
        _name = name;
        _stamp = stamp;
        _room = room;
    }

    /// <summary>
    /// The named fixture, with material paths nothing in this process has
    /// interned.
    /// </summary>
    public static BakeCorpus Fresh(string name)
    {
        string stamp = Guid.NewGuid().ToString("N")[..8];
        return new BakeCorpus(
            name, stamp, name == FlushCoplanarRoom ? MapFixture.Fresh(name) : null);
    }

    /// <summary>The fixture as a live scene, ready to be written out as a bundle.</summary>
    public SpectraEngine.Core.Scene.Scene BuildScene() => _name switch
    {
        FlushCoplanarRoom => _room!.BuildScene(),
        Cavities => BuildCavities(),
        Palette => BuildPalette(),
        Sprawl => BuildSprawl(),
        _ => throw new ArgumentOutOfRangeException(nameof(_name), _name, "No such fixture in the corpus."),
    };

    /// <summary>
    /// The same scene with every subtractive brush removed.
    /// </summary>
    /// <remarks>
    /// The negative control for the cavity claim: a cut that removed nothing would
    /// make the two compiles agree, and "the cuts cut" is otherwise unfalsifiable
    /// from inside a single compile.
    /// </remarks>
    public SpectraEngine.Core.Scene.Scene BuildSceneWithoutCuts()
    {
        SpectraEngine.Core.Scene.Scene scene = BuildScene();
        var doomed = new List<SceneNode>();

        foreach (SceneNode node in scene.Root.Traverse())
        {
            if (node.Brush is { Operation: BrushOperation.Subtractive }) doomed.Add(node);
        }

        foreach (SceneNode node in doomed) node.Parent?.RemoveChild(node);
        return scene;
    }

    // A material path nothing in this process has interned, because MaterialRegistry
    // is process-global and append-only. The .spectramat files themselves are not
    // written: MapRule bakes the geometry and never resolves a material, which is
    // what lets the whole corpus be a folder of maps and nothing else.
    private string Path(int index) => $"Materials/{_name}_{_stamp}_{index}.spectramat";

    // --- the fixtures ---------------------------------------------------------

    private SpectraEngine.Core.Scene.Scene BuildCavities()
    {
        var scene = new SpectraEngine.Core.Scene.Scene("BakeCavities");

        MaterialRef slabFace = MaterialRegistry.Intern(Path(0));
        MaterialRef wallFace = MaterialRegistry.Intern(Path(1));
        MaterialRef cutFace = MaterialRegistry.Intern(Path(2));

        // The solid every cut lives in. It spans x = -10..10 and z = -10..10 with
        // its middle on both cell planes, so the cuts below land on either side of
        // a boundary rather than all inside one cell.
        Box(scene, "Slab", new Vector3(0f, 0f, 0f), new Vector3(10f, 2f, 10f), slabFace);

        // Open at both ends: reaches past the slab in x, interior in y and z, so it
        // is a tunnel with two cavity walls rather than six.
        Cut(scene, "Tunnel", new Vector3(0f, 0f, 0f), new Vector3(12f, 0.75f, 1.5f), cutFace);

        // SEALED: inside the slab on all six sides and touching nothing. Every one
        // of its walls exists only because the seeding put it there, which is the
        // sharpest statement the negative case has.
        Cut(scene, "Pocket", new Vector3(5f, 0f, 5f), new Vector3(1.5f, 0.75f, 1.5f), cutFace);

        // Straddles the x = 0 cell plane, so one cut's cavity walls are split
        // between two cells' owner sets.
        Cut(scene, "SplitPocket", new Vector3(0f, 0.5f, -6f), new Vector3(1f, 0.6f, 1f), cutFace);

        // A second solid, and an aperture through it that touches neither its top
        // nor its bottom: a window, which is a cut with four cavity walls.
        Box(scene, "Wall", new Vector3(0f, 3.5f, 6f), new Vector3(10f, 1.5f, 0.5f), wallFace);
        Cut(scene, "Window", new Vector3(0f, 3.5f, 6f), new Vector3(1.2f, 0.6f, 0.75f), cutFace);

        return scene;
    }

    private SpectraEngine.Core.Scene.Scene BuildPalette()
    {
        var scene = new SpectraEngine.Core.Scene.Scene("BakePalette");

        // Interned BACKWARDS. Ids therefore descend across faces 0..5 while the
        // asset rows the bake claims ascend, so the file's ascending-row order is
        // a real permutation of the compile's ascending-id order rather than the
        // identity. Get the sort wrong and every face wears its neighbour's
        // material, which renders as a wrong picture and throws nothing.
        var faces = new MaterialRef[6];
        for (int i = 5; i >= 0; i--) faces[i] = MaterialRegistry.Intern(Path(i));

        // Wholly inside cell (0,0,0), so all seven submeshes below land in ONE
        // directory. Two cells with one entry each would order trivially.
        SceneNode painted = Box(
            scene, "Painted", new Vector3(8f, 8f, 8f), new Vector3(3f), faces[0]);

        for (int i = 1; i < faces.Length; i++)
            painted.Brush = painted.Brush!.WithFaceMaterial(i, faces[i]);

        // No material at all, in the same cell: NoAssetIndex is 0xFFFFFFFF, so it
        // sorts LAST among real rows and the loader must map it back to
        // MaterialRef.Default rather than to whatever row that number reaches.
        SceneNode bare = scene.Root.CreateChild("Bare");
        bare.LocalPosition = new Vector3(16f, 8f, 8f);
        bare.Brush = Brush.CreateBox(new Vector3(-3f), new Vector3(3f));

        return scene;
    }

    private SpectraEngine.Core.Scene.Scene BuildSprawl()
    {
        var scene = new SpectraEngine.Core.Scene.Scene("BakeSprawl");

        // One material for the whole level, deliberately: see the Sprawl remarks.
        MaterialRef only = MaterialRegistry.Intern(Path(0));

        // Five cells wide on x and two deep on z, owned by ONE of them. The other
        // nine carry a tree built from this slab as a resident and no mesh at all.
        Box(scene, "Concourse", new Vector3(48f, -1f, 0f), new Vector3(60f, 1f, 8f), only);

        // Straddles the x = 32 cell face.
        Box(scene, "OnTheFace", new Vector3(32f, 4f, 0f), new Vector3(2f), only);

        // Straddles a cell CORNER: across x = 64, y = 0 and z = 32 at once, so its
        // footprint is eight cells and no axis can be got right by accident.
        Box(scene, "OnTheCorner", new Vector3(64f, 0f, 32f), new Vector3(3f), only);

        // Far away and negative on two axes, because a dictionary-keyed sparse
        // grid must cost the same there as at the origin.
        Box(scene, "Island", new Vector3(-100f, 0f, -100f), new Vector3(4f), only);

        // ROTATED, at an angle nothing divides evenly. Every other brush in the
        // corpus is axis-aligned, so every vertex in it is a number somebody could
        // have typed; this one's are the output of a quaternion composed into a
        // world matrix, which is where a bit-identity claim is worth making at
        // all - and it is rigid, so the placement capture accepts it.
        SceneNode tilted = Box(scene, "Tilted", new Vector3(20f, 2f, 6f), new Vector3(4f, 2f, 3f), only);
        tilted.LocalRotation = Quaternion.CreateFromYawPitchRoll(0.6f, 0.25f, -0.4f);

        return scene;
    }

    // --- helpers --------------------------------------------------------------

    private static SceneNode Box(
        SpectraEngine.Core.Scene.Scene scene,
        string name,
        Vector3 center,
        Vector3 half,
        MaterialRef material)
    {
        SceneNode node = scene.Root.CreateChild(name);
        node.LocalPosition = center;
        node.Brush = Brush.CreateBox(-half, half, material);
        return node;
    }

    private static SceneNode Cut(
        SpectraEngine.Core.Scene.Scene scene,
        string name,
        Vector3 center,
        Vector3 half,
        MaterialRef material)
    {
        SceneNode node = Box(scene, name, center, half, material);
        node.Brush = node.Brush!.WithOperation(BrushOperation.Subtractive);
        return node;
    }
}
