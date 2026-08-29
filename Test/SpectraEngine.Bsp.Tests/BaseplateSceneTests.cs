using Microsoft.Extensions.Logging.Abstractions;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Scene;
using System.Numerics;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The editor's startup scene: a sun and a ground plate, and the seam that
/// picks it over the demo.
/// </summary>
/// <remarks>
/// <b>What is being pinned is that "new" means one thing.</b> The baseplate is
/// what a fresh project boots into AND what "new map" produces, so its shape is
/// a small contract: lit, with real solid under y = 0 for things to stand on
/// and for play mode to walk on. A dark or floorless variant would read as a
/// broken renderer to exactly the audience the editor is aimed at, and nothing
/// else in the engine would notice.
/// </remarks>
public sealed class BaseplateSceneTests
{
    [Fact]
    public void The_baseplate_is_a_sun_and_a_ground_plate()
    {
        var scene = new Scene("Fresh");

        SceneManager.PopulateBaseplate(scene);

        scene.Root.Children.Count.ShouldBe(2);

        SceneNode sun = scene.Root.Children[0];
        sun.Name.ShouldBe("Sun");
        sun.Light.ShouldNotBeNull();
        sun.Light.Kind.ShouldBe(LightKind.Directional);
        sun.Light.Intensity.ShouldBeGreaterThan(0f);

        SceneNode plate = scene.Root.Children[1];
        plate.Name.ShouldBe("Baseplate");
        plate.Brush.ShouldNotBeNull();
        plate.BrushKind.ShouldBe(BrushKind.World);
    }

    [Fact]
    public void The_baseplate_scene_has_solid_ground_under_the_spawn()
    {
        var manager = new SceneManager(NullLogger<SceneManager>.Instance)
        {
            Startup = StartupSceneKind.Baseplate,
        };

        manager.LoadStartupScene(
            new FakeRenderer(),
            new AssetManager(NullLogger<AssetManager>.Instance));

        Scene scene = manager.ActiveScene.ShouldNotBeNull();

        // The plate's top face is y = 0: solid just below it, air just above.
        // Sampled off the origin, because x = 0 and z = 0 are chunk-cell
        // boundaries and a boundary point tests cell routing, not the plate.
        scene.StaticWorld.ContainsPoint(new Vector3(1f, -0.5f, 1f)).ShouldBeTrue();
        scene.StaticWorld.ContainsPoint(new Vector3(1f, 0.5f, 1f)).ShouldBeFalse();

        // The character spawns above the plate, inside its footprint, so play
        // mode lands on the floor rather than falling out of the world.
        manager.PlayerSpawn.Y.ShouldBeGreaterThan(0f);
        MathF.Abs(manager.PlayerSpawn.X).ShouldBeLessThan(32f);
        MathF.Abs(manager.PlayerSpawn.Z).ShouldBeLessThan(32f);
    }

    [Fact]
    public void The_demo_stays_the_default_startup_scene()
    {
        // The demo is the engine's own smoke fixture; only a host that asks
        // for the baseplate gets it.
        new SceneManager(NullLogger<SceneManager>.Instance)
            .Startup.ShouldBe(StartupSceneKind.Demo);
    }
}
