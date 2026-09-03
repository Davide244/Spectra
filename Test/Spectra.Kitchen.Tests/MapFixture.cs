using System;
using System.IO;
using System.Numerics;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Maps;
using SpectraEngine.Core.Scene;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// Real map bundles for the bake, written as folders of text exactly as the editor
/// writes one.
/// </summary>
/// <remarks>
/// <para><b>The room is the repo's flush-coplanar-cut regression fixture</b>, not a
/// box chosen for convenience: a wall whose doorway cuts flush through its own
/// bottom plane, standing on a floor that reaches that same plane. Move either by a
/// hundredth of a unit and the opening compiles correctly, which is precisely why a
/// bake tested against anything else would prove nothing about the case the carve
/// nearly got wrong. <c>CoplanarCutSealingTests</c> carries the same arrangement
/// against the live compile.</para>
/// <para><b>Two materials and a part brush, deliberately.</b> One material per cell
/// makes every submesh directory one entry long, so an ordering rule and an
/// asset-index remap would both be unfalsifiable; a part brush makes the
/// <c>BRSH</c> section present whatever the cook was asked for, which is what
/// separates "kept because the cook asked" from "kept because its planes live
/// nowhere else".</para>
/// <para><b>Every material path is UNIQUE per fixture instance.</b>
/// <c>MaterialRegistry</c> is process-global and append-only, so a test that wants
/// an id and an asset index to disagree cannot get there with paths another test
/// may already have interned.</para>
/// </remarks>
internal sealed class MapFixture
{
    private MapFixture(string wall, string floor)
    {
        WallMaterial = wall;
        FloorMaterial = floor;
    }

    /// <summary>The material every wall face wears.</summary>
    public string WallMaterial { get; }

    /// <summary>The material the floor and the part wear.</summary>
    public string FloorMaterial { get; }

    /// <summary>
    /// A fixture whose material paths nothing in this process has interned.
    /// </summary>
    public static MapFixture Fresh([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        string stamp = Guid.NewGuid().ToString("N")[..8];
        return new MapFixture(
            $"Materials/{caller}_{stamp}_wall.spectramat",
            $"Materials/{caller}_{stamp}_floor.spectramat");
    }

    /// <summary>
    /// The room, as a live scene: a floor, a wall, a doorway cut flush to the
    /// wall's base, and a part brush standing on the floor.
    /// </summary>
    public SpectraEngine.Core.Scene.Scene BuildScene(bool withDoorway = true, bool withPart = true)
    {
        var scene = new SpectraEngine.Core.Scene.Scene("BakeRoom");

        MaterialRef wall = MaterialRegistry.Intern(WallMaterial);
        MaterialRef floor = MaterialRegistry.Intern(FloorMaterial);

        // The floor's top is exactly y = 0, which is the wall's base and the plane
        // the cut reaches. That coincidence is the fixture.
        SceneNode ground = Box(scene, "Floor", new Vector3(0f, -0.5f, 0f), new Vector3(6f, 0.5f, 6f), floor);

        // One face retextured, so the floor's own cell wears TWO materials. Without
        // it every submesh directory in the file is one entry long and the ordering
        // rule and the asset-index remap are both unfalsifiable.
        ground.Brush = ground.Brush!.WithFaceMaterial(0, wall);
        Box(scene, "Wall", new Vector3(0f, 1.5f, -4.25f), new Vector3(6f, 1.5f, 0.25f), wall);
        Box(scene, "BackWall", new Vector3(0f, 1.5f, 4.25f), new Vector3(6f, 1.5f, 0.25f), wall);

        if (withDoorway)
        {
            SceneNode cut = Box(
                scene, "Doorway", new Vector3(0f, 1.2f, -4.25f), new Vector3(1f, 1.2f, 0.25f), wall);

            cut.Brush = cut.Brush!.WithOperation(BrushOperation.Subtractive);
        }

        if (withPart)
        {
            SceneNode part = Box(scene, "Crate", new Vector3(2f, 0.5f, 2f), new Vector3(0.5f), floor);
            part.BrushKind = BrushKind.Part;
        }

        return scene;
    }

    /// <summary>Writes the room out as a real <c>.smap</c> bundle under the project.</summary>
    public string WriteBundle(TempProject project, string bundleName, bool withDoorway = true, bool withPart = true)
    {
        SpectraEngine.Core.Scene.Scene scene = BuildScene(withDoorway, withPart);
        return WriteBundle(project, bundleName, scene);
    }

    /// <summary>Writes any scene out as a real <c>.smap</c> bundle under the project.</summary>
    public static string WriteBundle(
        TempProject project, string bundleName, SpectraEngine.Core.Scene.Scene scene)
    {
        string bundle = Path.Combine(project.Layout.MapsPath, bundleName);
        Directory.CreateDirectory(bundle);
        MapBundle.Save(bundle, MapSceneBinder.FromScene(scene));
        return bundle;
    }

    /// <summary>
    /// A <c>.spectramat</c> for each of the fixture's materials, so a cooked pack
    /// carries what the map names.
    /// </summary>
    public void WriteMaterials(TempProject project)
    {
        foreach (string path in new[] { WallMaterial, FloorMaterial })
            project.WriteAsset(path, "shader = lit\n");
    }

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
}
