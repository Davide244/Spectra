using Microsoft.Extensions.Logging;
using Silk.NET.Maths;
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

    /// <summary>
    /// One entry per <see cref="Renderer.BeginPass"/>, recording what the pass
    /// asked to clear and how big its target was when it opened.
    /// </summary>
    /// <remarks>
    /// The pass seam is otherwise only observable on a real device, and the
    /// mistakes it can make are exactly the ones a GPU test would not report as
    /// a failure: an unbalanced pair, a viewport sized from the window instead
    /// of the target, a clear that quietly stopped happening.
    /// </remarks>
    public List<(PassClear Clear, Vector2D<int> Size, RenderTarget? Target)> Passes { get; } = [];

    /// <summary>Passes begun and not yet ended. Zero at the end of a well-formed frame.</summary>
    public int OpenPasses { get; private set; }

    /// <summary>Every render target ever created, in creation order.</summary>
    public List<FakeRenderTarget> CreatedRenderTargets { get; } = [];

    /// <summary>Targets still registered, mirroring the real backends' tracking lists.</summary>
    public List<FakeRenderTarget> LiveRenderTargets { get; } = [];

    /// <summary>Attachment count of each pass, so a multi-target bind is observable.</summary>
    public List<int> PassTargetCounts { get; } = [];

    protected override void BeginPassCore(
        RenderTarget? target, ReadOnlySpan<RenderTarget> targets, in PassClear clear)
    {
        Passes.Add((clear, PassSize, target));
        PassTargetCounts.Add(targets.Length);
        OpenPasses++;
    }

    /// <summary>Overlay lines this stub was asked to draw, one entry per call.</summary>
    public int OverlayFlushes { get; private set; }

    protected override void FlushDebugDrawCore(SpectraEngine.Core.Scene.Camera camera) => OverlayFlushes++;


    protected override void FlushWorldLinesCore(
        SpectraEngine.Core.Scene.Camera camera, ShaderProgram program, float nudge, GBuffer? gbuffer) { }

    /// <summary>Full-screen passes this renderer was asked to draw.</summary>
    public int FullscreenDraws { get; private set; }

    protected override void DrawFullscreen(PostPass pass) => FullscreenDraws++;

    protected override void EndPassCore(RenderTarget? target, ReadOnlySpan<RenderTarget> targets) => OpenPasses--;

    public override RenderTarget CreateRenderTarget(in RenderTargetDesc desc)
    {
        desc.Validate();

        var target = new FakeRenderTarget(desc);
        target.Unregister = () => LiveRenderTargets.Remove(target);
        CreatedRenderTargets.Add(target);
        LiveRenderTargets.Add(target);
        return target;
    }

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

    // One pipeline, and it is the one already running.
    public override bool TrySelectPipeline(string name) =>
        string.Equals(name, "Fake", StringComparison.OrdinalIgnoreCase);

    // No rasteriser, so no viewport. Present because the shadow atlas needs one.
    protected override void SetViewportCore(int x, int y, int width, int height) { }

    public override Mesh CreateMesh(ReadOnlySpan<float> vertices, ReadOnlySpan<uint> indices,
        ReadOnlySpan<VertexAttribute> attributes, MeshCpuAccess cpuAccess = MeshCpuAccess.Retained)
    {
        if (CreateMeshBudget <= 0)
            throw new InvalidOperationException("Simulated CreateMesh failure (CreateMeshBudget exhausted).");
        if (CreateMeshBudget != int.MaxValue)
            CreateMeshBudget--;

        var mesh = new FakeMesh(vertices.ToArray(), indices.ToArray(), attributes, cpuAccess);
        // Same wiring CreateTexture uses, so DestroyMesh removes it from the
        // live list exactly once.
        mesh.Unregister = () => LiveMeshes.Remove(mesh);
        CreatedMeshes.Add(mesh);
        LiveMeshes.Add(mesh);
        return mesh;
    }

    public override InstanceBuffer CreateInstanceBuffer(
        int capacityInstances, ReadOnlySpan<VertexAttribute> attributes, ShaderProgram program)
        => throw new NotSupportedException("This renderer creates no GPU resources.");

    public override Texture CreateTexture(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        TextureFormat format,
        TextureColorSpace colorSpace,
        TextureFilter filter = TextureFilter.Linear,
        TextureWrap wrap = TextureWrap.Repeat)
    {
        var texture = new FakeTexture(pixels.ToArray(), width, height, format, colorSpace, filter, wrap);
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
        : this(vertices, indices, VertexAttribute.StandardLayout, MeshCpuAccess.Retained)
    {
    }

    public FakeMesh(
        float[] vertices, uint[] indices, ReadOnlySpan<VertexAttribute> attributes, MeshCpuAccess cpuAccess)
    {
        VertexData = vertices;
        IndexData = indices;
        IndexCount = (uint)indices.Length;

        // The shared base helper, deliberately: the fake must starve
        // Positions/Normals/Indices for a GPU-only mesh exactly as the real
        // backends do, or the headless suites cannot catch a consumer reading
        // arrays a MeshCpuAccess.None mesh no longer has. VertexData/IndexData
        // above stay populated either way; they are this fake's own upload
        // oracle, not the engine's CPU mirror.
        InitializeCpuData(vertices, indices, attributes, cpuAccess);
    }

    public override void Draw()
    {
        // Nothing to draw without a GPU.
    }

    public override void DrawInstanced(InstanceBuffer instances, int instanceCount, int firstInstance = 0)
    {
    }

    public override void Dispose() => Disposed = true;
}

/// <summary>
/// CPU-only <see cref="Texture"/> produced by <see cref="FakeRenderer"/>: keeps
/// the uploaded pixel bytes and the sampling state it was created with, and
/// records disposal so asset-ownership bugs (a texture leaked past unload, or
/// one destroyed while a handle still points at it) are observable.
/// </summary>
/// <summary>
/// A GPU-free <see cref="RenderTarget"/>. Its colour attachment keeps its
/// identity across a resize, exactly as the real ones must, so a test can assert
/// on the property whose absence would strand every material sampling it.
/// </summary>
internal sealed class FakeRenderTarget : RenderTarget
{
    private readonly FakeTexture _color;
    private readonly FakeTexture? _depth;

    public FakeRenderTarget(in RenderTargetDesc desc)
    {
        Desc = desc;
        Width = desc.Width;
        Height = desc.Height;
        _color = new FakeTexture(
            [], desc.Width, desc.Height, desc.ColorFormat, desc.ColorSpace, desc.Filter, desc.Wrap);
        if (desc.Depth)
        {
            _depth = new FakeTexture(
                [], desc.Width, desc.Height, TextureFormat.Depth32Float, TextureColorSpace.Linear,
                TextureFilter.Nearest, TextureWrap.Clamp);
        }
    }

    public bool Disposed { get; private set; }

    /// <summary>Sizes this target has been through, so a test can see a resize actually happened.</summary>
    public List<(int Width, int Height)> Resizes { get; } = [];

    public override Texture ColorTexture => _color;

    public override Texture? DepthTexture => _depth;

    public override void Resize(int width, int height)
    {
        if (width == Width && height == Height) return;

        Width = width;
        Height = height;
        _color.ResizeInPlace(width, height);
        _depth?.ResizeInPlace(width, height);
        Resizes.Add((width, height));
    }

    public override void Dispose() => Disposed = true;
}

internal sealed class FakeTexture : Texture
{
    public byte[] Pixels { get; }
    public TextureFilter Filter { get; }
    public TextureWrap Wrap { get; }

    public bool Disposed { get; private set; }

    public FakeTexture(byte[] pixels, int width, int height, TextureFormat format,
        TextureColorSpace colorSpace, TextureFilter filter, TextureWrap wrap)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
        Format = format;
        // Resolved exactly as the three real backends do, so a test that asserts
        // on the colour space is asserting on the same rule they follow.
        ColorSpace = TextureFormatInfo.Resolve(format, colorSpace);
        Filter = filter;
        Wrap = wrap;
    }

    /// <summary>Mirrors a real attachment's in-place resize: same object, new size.</summary>
    public void ResizeInPlace(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public override void Dispose() => Disposed = true;
}
