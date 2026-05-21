using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Core.Scene;

public sealed class SceneManager
{
    private readonly ILogger<SceneManager> _logger;

    private SceneNode? _spinner;
    private double _elapsed;

    public SceneManager(ILogger<SceneManager> logger)
    {
        _logger = logger;
    }

    /// <summary>The scene currently being simulated and rendered, if one is loaded.</summary>
    public Scene? ActiveScene { get; private set; }

    public void Initialize()
    {
        _logger.LogInformation("Scene manager initialized");
    }

    /// <summary>
    /// Builds a placeholder scene that exercises the graph: a spinning cube with
    /// a smaller child cube, which orbits the parent purely by inheriting its
    /// rotation. Requires an initialized renderer for GPU resource creation.
    /// </summary>
    public void LoadDemoScene(Renderer renderer)
    {
        var scene = new Scene("Demo");
        scene.Camera.Position = new Vector3(0f, 1.5f, 5f);
        scene.Camera.LookAt(Vector3.Zero);

        var (vertices, indices) = Primitives.Cube();
        ReadOnlySpan<VertexAttribute> layout =
        [
            new(location: 0, componentCount: 3),
            new(location: 1, componentCount: 3),
            new(location: 2, componentCount: 2),
        ];
        var cubeMesh = renderer.CreateMesh(vertices, indices, layout);
        var shader = renderer.DefaultShader
            ?? throw new InvalidOperationException("Renderer has no default shader; initialize it first.");

        // A procedural checker as the diffuse texture. Linear mipmap minifies
        // cleanly when seen at distance; repeat lets world-scale UVs tile.
        byte[] checker = Primitives.CheckerboardRgb8();
        var checkerTexture = renderer.CreateTexture(
            checker, 16, 16, TextureFormat.Rgb8,
            TextureFilter.LinearMipmap, TextureWrap.Repeat);

        var center = scene.Root.CreateChild("SpinningCube");
        center.MeshRenderer = new MeshRenderer(cubeMesh,
            new Material(shader)
                .SetVector3("uBaseColor", new Vector3(1f, 0.55f, 0.2f))
                .SetTexture("uDiffuse", 0, checkerTexture));
        _spinner = center;

        var orbiter = center.CreateChild("Orbiter");
        orbiter.LocalTransform = new Transform
        {
            Position = new Vector3(2f, 0f, 0f),
            Rotation = Quaternion.Identity,
            Scale = new Vector3(0.4f, 0.4f, 0.4f),
        };
        orbiter.MeshRenderer = new MeshRenderer(cubeMesh,
            new Material(shader)
                .SetVector3("uBaseColor", new Vector3(0.3f, 0.6f, 1f))
                .SetTexture("uDiffuse", 0, checkerTexture));

        BuildStaticWorld(scene, renderer, shader, layout, checkerTexture);

        ActiveScene = scene;
        _logger.LogInformation("Demo scene '{Name}' loaded", scene.Name);
    }

    // Builds the static, brush-based half of the demo: a floor slab and two
    // pillars, partitioned into a BSP tree and meshed for rendering.
    private void BuildStaticWorld(Scene scene, Renderer renderer, ShaderProgram shader, ReadOnlySpan<VertexAttribute> layout, Texture diffuse)
    {
        // The pillars sit flush on the floor's top (y = -1.0); CSG resolves the
        // coincident faces at the interface.
        var brushes = new List<Brush>
        {
            Brush.CreateBox(new Vector3(-3f, -1.2f, -3f), new Vector3(3f, -1.0f, 3f)),
            Brush.CreateBox(new Vector3(-2.2f, -1.0f, -2.2f), new Vector3(-1.8f, 1.2f, -1.8f)),
            Brush.CreateBox(new Vector3(1.8f, -1.0f, 1.8f), new Vector3(2.2f, 1.2f, 2.2f)),
        };

        var world = CsgWorld.Build(brushes);
        scene.StaticWorld = world;

        var (vertices, indices) = world.BuildMesh();
        var worldMesh = renderer.CreateMesh(vertices, indices, layout);
        var worldNode = scene.Root.CreateChild("StaticWorld");
        worldNode.MeshRenderer = new MeshRenderer(worldMesh,
            new Material(shader)
                .SetVector3("uBaseColor", new Vector3(0.55f, 0.55f, 0.6f))
                .SetTexture("uDiffuse", 0, diffuse));

        // Sanity-check CSG and the BSP queries against the geometry we built.
        bool floorSolid = world.Bsp.ContainsPoint(new Vector3(0f, -1.1f, 0f));
        bool pillarSolid = world.Bsp.ContainsPoint(new Vector3(-2f, 0f, -2f));
        bool airEmpty = !world.Bsp.ContainsPoint(new Vector3(0f, 3f, 0f));
        bool rayHitsFloor = world.Bsp.Raycast(
            new Vector3(0f, 3f, 0f), -Vector3.UnitY, 10f, out var hit);

        _logger.LogInformation(
            "Static world: {Brushes} brushes -> {Surfaces} carved surfaces; " +
            "floor-solid={Floor}, pillar-solid={Pillar}, air-empty={Air}, ray-hit={Hit} at y={Y:0.000}",
            brushes.Count, world.Surfaces.Count, floorSolid, pillarSolid, airEmpty, rayHitsFloor, hit.Point.Y);
    }

    public void Update(double deltaTime)
    {
        _elapsed += deltaTime;

        if (_spinner is not null)
        {
            _spinner.LocalRotation = Quaternion.CreateFromYawPitchRoll(
                (float)_elapsed * 0.6f,
                (float)_elapsed * 0.4f,
                0f);
        }
    }

    public void Shutdown()
    {
        ActiveScene = null;
        _spinner = null;
        _logger.LogInformation("Scene manager shut down");
    }
}
