using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Physics;
using SpectraEngine.Core.Physics.Character;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The line between simulating a character and drawing one.
/// </summary>
/// <remarks>
/// <para>
/// Three things this engine intends to do need a character simulated with no
/// camera, no input device and no renderer present: a dedicated server running
/// the world headlessly, a rollback replaying one tick many times per
/// correction, and a scripted mover that must be bindable without dragging
/// rendering types into the scripting surface. A constructor that demanded a
/// camera would have closed off all three at once, which is what
/// <c>FirstPersonController</c> used to do.
/// </para>
/// <para>
/// <b>The separation is real but not complete, and this file says where it
/// stops.</b> <see cref="Scene.RebuildStaticWorld"/> still takes a
/// <c>Renderer</c> and creates GPU meshes, so compiling a world headlessly is
/// the next coupling to break. The simulation itself is clean; the world it
/// reads is not yet.
/// </para>
/// </remarks>
public sealed class CharacterSimulationBoundaryTests
{
    private const float Dt = PhysicsDefaults.FixedDeltaTime;

    // Types whose presence in the simulation's surface would mean the wrong
    // thing had been coupled to it. Matched by name so this test does not have
    // to reference the graphics or input namespaces to forbid them.
    private static readonly string[] Forbidden =
    [
        "Camera", "InputManager", "ICursorLock", "Renderer", "DebugDraw", "RenderView",
    ];

    [Fact]
    public void The_simulation_names_no_rendering_or_input_type_anywhere_in_its_surface()
    {
        Type type = typeof(CharacterSimulation);

        var referenced = type.GetConstructors()
            .SelectMany(c => c.GetParameters().Select(p => p.ParameterType))
            .Concat(type.GetProperties().Select(p => p.PropertyType))
            .Concat(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)))
            .Select(t => t.Name.TrimEnd('&'))
            .Distinct()
            .ToArray();

        string[] violations = referenced.Where(n => Forbidden.Contains(n)).ToArray();

        Assert.True(violations.Length == 0,
            $"CharacterSimulation's public surface names {string.Join(", ", violations)}. " +
            "A simulation that needs a camera cannot run on a server, in a replay, or under a script.");
    }

    [Fact]
    public void A_character_can_be_spawned_walked_and_respawned_with_no_view_of_any_kind()
    {
        // The whole point, exercised rather than asserted: this test constructs
        // no camera, no input manager and no debug draw, and drives a full
        // spawn, walk, fall and respawn cycle.
        var scene = new Scene("Headless");

        SceneNode floor = scene.Root.CreateChild("floor");
        floor.LocalPosition = new Vector3(0f, -0.5f, 0f);
        floor.Brush = SpectraEngine.Core.Bsp.Brush.CreateBox(
            new Vector3(-4f, -0.5f, -4f), new Vector3(4f, 0.5f, 4f), MaterialRef.Default);

        scene.RebuildStaticWorld(new FakeRenderer());

        var simulation = new CharacterSimulation(scene)
        {
            SpawnPosition = new Vector3(0f, 0.05f, 0f),
            FallOutHeight = -20f,
        };
        simulation.Spawn();

        // Settle onto the floor.
        for (int i = 0; i < 30; i++)
            Assert.False(simulation.Tick(default, Dt));

        Assert.True(simulation.State.Grounded, "the character should be standing on the floor");
        Assert.Equal(simulation.Tuning.SkinWidth, simulation.State.Position.Y, 4);

        // Walk east off the 4-unit slab and keep going until the guard fires.
        var walk = new CharacterCommand { MoveForward = CharacterCommand.Axis(1f), Yaw = 0f };

        bool respawned = false;
        for (int i = 0; i < 400 && !respawned; i++)
            respawned = simulation.Tick(in walk, Dt);

        Assert.True(respawned, "walking off the slab should eventually trip the fall-out guard");
        Assert.Equal(1, simulation.Respawns);
        Assert.Equal(simulation.SpawnPosition, simulation.State.Position);
    }

    [Fact]
    public void State_can_be_captured_and_restored_as_a_plain_struct_copy()
    {
        // What a network correction and a rollback replay both do. It is only
        // free because CharacterState is a struct with nothing reaching out of
        // it, which is a constraint on every field ever added to it.
        var scene = new Scene("Restore");
        SceneNode floor = scene.Root.CreateChild("floor");
        floor.LocalPosition = new Vector3(0f, -0.5f, 0f);
        floor.Brush = SpectraEngine.Core.Bsp.Brush.CreateBox(
            new Vector3(-8f, -0.5f, -8f), new Vector3(8f, 0.5f, 8f), MaterialRef.Default);
        scene.RebuildStaticWorld(new FakeRenderer());

        var simulation = new CharacterSimulation(scene) { SpawnPosition = new Vector3(0f, 0.05f, 0f) };
        simulation.Spawn();
        for (int i = 0; i < 20; i++)
            simulation.Tick(default, Dt);

        CharacterState captured = simulation.State;

        var walk = new CharacterCommand { MoveForward = CharacterCommand.Axis(1f), Yaw = 0f };
        for (int i = 0; i < 40; i++)
            simulation.Tick(in walk, Dt);

        Assert.NotEqual(captured.Position.X, simulation.State.Position.X, 3);

        simulation.Restore(in captured);
        Assert.Equal(captured.Position, simulation.State.Position);
        Assert.Equal(captured.Velocity, simulation.State.Velocity);

        // And replaying the same commands from the restored state reproduces the
        // same result, which is the property rollback actually depends on.
        Vector3 firstRun = default;
        for (int i = 0; i < 40; i++)
            simulation.Tick(in walk, Dt);
        firstRun = simulation.State.Position;

        simulation.Restore(in captured);
        for (int i = 0; i < 40; i++)
            simulation.Tick(in walk, Dt);

        Assert.Equal(firstRun, simulation.State.Position);
    }
}
