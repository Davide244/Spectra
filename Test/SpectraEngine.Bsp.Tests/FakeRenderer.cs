using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.Shaders;
using System.Numerics;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// A GPU-free <see cref="Renderer"/> for exercising the static-world compile
/// and asset-loading paths headlessly. Meshes and textures are recorded
/// CPU-side as <see cref="FakeMesh"/> / <see cref="FakeTexture"/> instances
/// (keeping the exact byte arrays for determinism assertions); shader creation
/// still throws — a test reaching it is a test that silently grew a real GPU
/// dependency.
/// </summary>
internal sealed class FakeRenderer : Renderer
{
    /// <summary>Every mesh ever created, in creation order (destroyed ones included).</summary>
    public List<FakeMesh> CreatedMeshes { get; } = [];

    /// <summary>
    /// Meshes still registered with this renderer. Mirrors the real backends'
    /// tracking list (and <see cref="LiveTextures"/>), so a test can prove
    /// <see cref="Renderer.DestroyMesh"/> deregistered as well as disposed —
    /// the leak a plain <c>Dispose</c> would hide, because the dead instance
    /// would sit in the tracking list until shutdown.
    /// </summary>
    public List<FakeMesh> LiveMeshes { get; } = [];

    /// <summary>Every texture ever created, in creation order (destroyed ones included).</summary>
    public List<FakeTexture> CreatedTextures { get; } = [];

    /// <summary>
    /// Textures still registered with this renderer. Mirrors the real backends'
    /// tracking list, so a test can prove <see cref="Renderer.DestroyTexture"/>
    /// deregistered as well as disposed.
    /// </summary>
    public List<FakeTexture> LiveTextures { get; } = [];

    /// <summary>
    /// Remaining <see cref="CreateMesh"/> calls that succeed before the
    /// renderer starts throwing; <see cref="int.MaxValue"/> (the default)
    /// never fails. Lets swap-atomicity tests fail the Nth creation of a
    /// multi-chunk batch and observe the rollback.
    /// </summary>
    public int CreateMeshBudget { get; set; } = int.MaxValue;

    public FakeRenderer()
        : base(NullLogger<Renderer>.Instance, new ThrowingShaderCompiler())
    {
        // The real backends set this in Initialize, and material loading reads
        // it as the fallback program for every material file. A no-op stand-in
        // keeps the headless path realistic without compiling anything.
        DefaultShader = new NoopShaderProgram();
    }

    /// <summary>
    /// Drops the default shader, standing in for a backend whose shader
    /// compilation failed. Lets a test prove the asset manager still produces a
    /// usable (non-null) default material.
    /// </summary>
    public void ClearDefaultShader() => DefaultShader = null;

    // Arbitrary: nothing in the headless paths ever branches on the backend.
    public override GraphicsBackend Backend => GraphicsBackend.OpenGL;

    public override string CurrentPipelineName => "Fake";

    public override string NextPipeline() => "Fake";

    public override Mesh CreateMesh(ReadOnlySpan<float> vertices, ReadOnlySpan<uint> indices, ReadOnlySpan<VertexAttribute> attributes)
    {
        if (CreateMeshBudget <= 0)
            throw new InvalidOperationException("Simulated CreateMesh failure (CreateMeshBudget exhausted).");
        if (CreateMeshBudget != int.MaxValue)
            CreateMeshBudget--;

        var mesh = new FakeMesh(vertices.ToArray(), indices.ToArray(), attributes);
        // Same wiring CreateTexture uses, so DestroyMesh removes it from the
        // live list exactly once.
        mesh.Unregister = () => LiveMeshes.Remove(mesh);
        CreatedMeshes.Add(mesh);
        LiveMeshes.Add(mesh);
        return mesh;
    }

    public override Texture CreateTexture(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        TextureFormat format,
        TextureFilter filter = TextureFilter.Linear,
        TextureWrap wrap = TextureWrap.Repeat)
    {
        var texture = new FakeTexture(pixels.ToArray(), width, height, format, filter, wrap);
        // Same wiring the real backends use, so DestroyTexture removes it from
        // the live list exactly once.
        texture.Unregister = () => LiveTextures.Remove(texture);
        CreatedTextures.Add(texture);
        LiveTextures.Add(texture);
        return texture;
    }

    public override ShaderProgram CreateShader(string vertexSource, string fragmentSource)
        => throw new NotSupportedException("FakeRenderer does not create shaders.");

    public override ShaderProgram CreateShader(PipelineBlob blob)
        => throw new NotSupportedException("FakeRenderer does not create shaders.");

    // The base constructor wires an IShaderCompiler into the hot-reloader; the
    // headless tests never compile shaders, so any call is a test defect.
    private sealed class ThrowingShaderCompiler : IShaderCompiler
    {
        public CompiledShaderFile Compile(string source, ReadOnlySpan<GraphicsBackend> targets)
            => throw new NotSupportedException("FakeRenderer does not compile shaders.");
    }
}

/// <summary>
/// CPU-only <see cref="Mesh"/> produced by <see cref="FakeRenderer"/>: keeps
/// the raw arrays it was created from so tests can compare successive compiles
/// bit-for-bit, and records disposal so mesh-lifetime bugs (leaked or
/// prematurely destroyed static-world meshes) are observable.
/// </summary>
/// <remarks>
/// It also de-interleaves positions and normals and computes
/// <see cref="Mesh.LocalBounds"/>, exactly as every real backend's mesh does at
/// creation time. Without that, anything a headless test uploads through
/// <see cref="FakeRenderer"/> would be invisible to the scene's spatial index
/// and its per-triangle raycast, and "the model is in the BVH" would be
/// untestable. Constructing one with no vertex data still yields no geometry,
/// which is how tests exercise the raycast's GPU-only-mesh fallback.
/// </remarks>
internal sealed class FakeMesh : Mesh
{
    public float[] VertexData { get; }
    public uint[] IndexData { get; }

    public bool Disposed { get; private set; }

    public FakeMesh(float[] vertices, uint[] indices)
        : this(vertices, indices, VertexAttribute.StandardLayout)
    {
    }

    public FakeMesh(float[] vertices, uint[] indices, ReadOnlySpan<VertexAttribute> attributes)
    {
        VertexData = vertices;
        IndexData = indices;
        IndexCount = (uint)indices.Length;
        Indices = indices;

        (Vector3[] positions, Vector3[] normals) = ExtractStreams(vertices, attributes);
        Positions = positions;
        Normals = normals;
        LocalBounds = positions.Length == 0
            ? new Aabb(Vector3.Zero, Vector3.Zero)
            : Aabb.FromPoints(positions);
    }

    // Mirrors the backends' de-interleaving: attribute location 0 is position,
    // location 1 is normal, everything else is ignored.
    private static (Vector3[] Positions, Vector3[] Normals) ExtractStreams(
        float[] vertices, ReadOnlySpan<VertexAttribute> attributes)
    {
        int stride = 0;
        int positionOffset = -1;
        int normalOffset = -1;
        for (int i = 0; i < attributes.Length; i++)
        {
            if (attributes[i].Location == 0) positionOffset = stride;
            else if (attributes[i].Location == 1) normalOffset = stride;
            stride += (int)attributes[i].ComponentCount;
        }

        if (positionOffset < 0 || stride == 0)
            return ([], []);

        int vertexCount = vertices.Length / stride;
        var positions = new Vector3[vertexCount];
        Vector3[] normals = normalOffset >= 0 ? new Vector3[vertexCount] : [];
        for (int i = 0; i < vertexCount; i++)
        {
            int b = i * stride;
            positions[i] = new Vector3(
                vertices[b + positionOffset],
                vertices[b + positionOffset + 1],
                vertices[b + positionOffset + 2]);
            if (normalOffset >= 0)
                normals[i] = new Vector3(
                    vertices[b + normalOffset],
                    vertices[b + normalOffset + 1],
                    vertices[b + normalOffset + 2]);
        }
        return (positions, normals);
    }

    public override void Draw()
    {
        // Nothing to draw without a GPU.
    }

    public override void Dispose() => Disposed = true;
}

/// <summary>
/// CPU-only <see cref="Texture"/> produced by <see cref="FakeRenderer"/>: keeps
/// the uploaded pixel bytes and the sampling state it was created with, and
/// records disposal so asset-ownership bugs (a texture leaked past unload, or
/// one destroyed while a handle still points at it) are observable.
/// </summary>
internal sealed class FakeTexture : Texture
{
    public byte[] Pixels { get; }
    public TextureFilter Filter { get; }
    public TextureWrap Wrap { get; }

    public bool Disposed { get; private set; }

    public FakeTexture(byte[] pixels, int width, int height, TextureFormat format,
        TextureFilter filter, TextureWrap wrap)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
        Format = format;
        Filter = filter;
        Wrap = wrap;
    }

    public override void Dispose() => Disposed = true;
}
