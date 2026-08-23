using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.Shaders;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// Shared fixtures for the scene spatial-index tests: a CPU-only mesh built
/// straight from positions and indices (where <see cref="FakeMesh"/> models an
/// upload and de-interleaves them out of a vertex stream), a do-nothing shader
/// so <see cref="Material"/> can exist without a GPU, and factories for spatial
/// scene nodes.
/// </summary>
internal static class SpatialTestHelpers
{
    /// <summary>One shared material — the spatial index never looks at it.</summary>
    public static readonly Material NoopMaterial = new(new NoopShaderProgram());

    /// <summary>Axis-aligned cube mesh centred on the origin with the given half-extent.</summary>
    public static TestMesh CreateCubeMesh(float half)
    {
        Vector3[] positions =
        [
            new(-half, -half, -half), new(half, -half, -half),
            new(-half, half, -half), new(half, half, -half),
            new(-half, -half, half), new(half, -half, half),
            new(-half, half, half), new(half, half, half),
        ];
        // Two triangles per face; winding is irrelevant — the scene raycast is
        // double-sided by contract.
        uint[] indices =
        [
            0, 1, 3, 0, 3, 2, // -Z
            4, 6, 7, 4, 7, 5, // +Z
            0, 2, 6, 0, 6, 4, // -X
            1, 5, 7, 1, 7, 3, // +X
            0, 4, 5, 0, 5, 1, // -Y
            2, 3, 7, 2, 7, 6, // +Y
        ];
        return new TestMesh(positions, indices);
    }

    /// <summary>Attaches a cube-mesh node at <paramref name="position"/> under <paramref name="parent"/>.</summary>
    public static SceneNode CreateMeshNode(SceneNode parent, string name, Vector3 position, float half = 0.5f)
    {
        SceneNode node = parent.CreateChild(name);
        node.LocalPosition = position;
        node.MeshRenderer = new MeshRenderer(CreateCubeMesh(half), NoopMaterial);
        return node;
    }

    /// <summary>Attaches a box-brush node at <paramref name="position"/> under <paramref name="parent"/>.</summary>
    public static SceneNode CreateBrushNode(SceneNode parent, string name, Vector3 position, float half = 0.5f)
    {
        SceneNode node = parent.CreateChild(name);
        node.LocalPosition = position;
        node.Brush = Brush.CreateBox(new Vector3(-half), new Vector3(half));
        return node;
    }

    /// <summary>
    /// The same world AABB the spatial index maintains for a node, computed
    /// independently — the oracle for the brute-force query comparisons.
    /// </summary>
    public static Aabb WorldBoundsOf(SceneNode node)
    {
        bool has = false;
        Aabb result = default;
        if (node.Brush is { } brush)
        {
            result = brush.LocalBounds.Transform(node.WorldMatrix);
            has = true;
        }
        if (node.MeshRenderer is { } mr)
        {
            Aabb meshBox = mr.Mesh.LocalBounds.Transform(node.WorldMatrix);
            result = has
                ? new Aabb(Vector3.Min(result.Min, meshBox.Min), Vector3.Max(result.Max, meshBox.Max))
                : meshBox;
            has = true;
        }
        has.ShouldBeTrue($"'{node.Name}' is not a spatial node");
        return result;
    }
}

/// <summary>
/// CPU-only mesh with real triangle data, for exercising the per-triangle
/// raycast narrow phase headlessly.
/// </summary>
internal sealed class TestMesh : Mesh
{
    public TestMesh(Vector3[] positions, uint[] indices)
    {
        Positions = positions;
        Indices = indices;
        IndexCount = (uint)indices.Length;
        LocalBounds = Aabb.FromPoints(positions);
    }

    public override void Draw()
    {
        // Nothing to draw without a GPU.
    }

    public override void Dispose()
    {
    }
}

/// <summary>
/// A shader that records what was written to it, standing in for the real
/// backends' uniform state. Every backend keeps that state between draws — GL
/// on the program object, D3D11/D3D12 in a persistent constant-buffer shadow —
/// so a uniform a material never writes keeps the previous draw's value, and
/// this double makes that observable headlessly.
/// </summary>
internal sealed class RecordingShaderProgram : ShaderProgram
{
    public Dictionary<string, Vector3> Vectors3 { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, float> Floats { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Texture> Textures { get; } = new(StringComparer.Ordinal);

    public override void Use()
    {
    }

    public override bool TryReload(PipelineBlob blob, [NotNullWhen(false)] out string? error)
    {
        error = "RecordingShaderProgram cannot reload.";
        return false;
    }

    public override void SetUniform(string name, Matrix4x4 value) { }
    public override void SetUniform(string name, Vector4 value) { }
    public override void SetUniform(string name, Vector3 value) => Vectors3[name] = value;
    public override void SetUniform(string name, Vector2 value) { }
    public override void SetUniform(string name, float value) => Floats[name] = value;
    public override void SetUniform(string name, int value) => Ints[name] = value;

    /// <summary>Array uniforms recorded whole, so a light upload is assertable headlessly.</summary>
    public Dictionary<string, Vector4[]> Vector4Arrays { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, Matrix4x4[]> Matrix4Arrays { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, int> Ints { get; } = new(StringComparer.Ordinal);

    public override void SetUniform(string name, ReadOnlySpan<Vector4> values) => Vector4Arrays[name] = values.ToArray();
    public override void SetUniform(string name, ReadOnlySpan<Matrix4x4> values) => Matrix4Arrays[name] = values.ToArray();

    public override void SetTexture(string name, int unit, Texture texture) => Textures[name] = texture;

    public override void Dispose()
    {
    }
}

/// <summary>Inert shader so a <see cref="Material"/> can exist in headless tests.</summary>
internal sealed class NoopShaderProgram : ShaderProgram
{
    public override void Use()
    {
    }

    public override bool TryReload(PipelineBlob blob, [NotNullWhen(false)] out string? error)
    {
        error = "NoopShaderProgram cannot reload.";
        return false;
    }

    public override void SetUniform(string name, Matrix4x4 value) { }
    public override void SetUniform(string name, Vector4 value) { }
    public override void SetUniform(string name, Vector3 value) { }
    public override void SetUniform(string name, Vector2 value) { }
    public override void SetUniform(string name, float value) { }
    public override void SetUniform(string name, int value) { }
    public override void SetUniform(string name, ReadOnlySpan<Vector4> values) { }
    public override void SetUniform(string name, ReadOnlySpan<Matrix4x4> values) { }

    public override void SetTexture(string name, int unit, Texture texture) { }

    public override void Dispose()
    {
    }
}
