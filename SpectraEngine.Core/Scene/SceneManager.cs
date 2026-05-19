using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Graphics;
using System;
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
        ];
        var cubeMesh = renderer.CreateMesh(vertices, indices, layout);
        var shader = renderer.DefaultShader
            ?? throw new InvalidOperationException("Renderer has no default shader; initialize it first.");

        var center = scene.Root.CreateChild("SpinningCube");
        center.MeshRenderer = new MeshRenderer(
            cubeMesh,
            new Material(shader) { BaseColor = new Vector3(1f, 0.55f, 0.2f) });
        _spinner = center;

        var orbiter = center.CreateChild("Orbiter");
        orbiter.LocalTransform = new Transform
        {
            Position = new Vector3(2f, 0f, 0f),
            Rotation = Quaternion.Identity,
            Scale = new Vector3(0.4f, 0.4f, 0.4f),
        };
        orbiter.MeshRenderer = new MeshRenderer(
            cubeMesh,
            new Material(shader) { BaseColor = new Vector3(0.3f, 0.6f, 1f) });

        ActiveScene = scene;
        _logger.LogInformation("Demo scene '{Name}' loaded", scene.Name);
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
