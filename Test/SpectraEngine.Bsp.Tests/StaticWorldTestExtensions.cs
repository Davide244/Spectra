using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// Helpers for the scene suites that compile single-material worlds — the
/// default brush has no material, so every chunk of theirs uploads as exactly
/// one GPU submesh. Asserting that ("single item") on the way through is the
/// point: a suite that silently grew a second material would fail here rather
/// than quietly assert against whichever piece happened to come first.
/// </summary>
internal static class StaticWorldTestExtensions
{
    /// <summary>The one GPU mesh of a chunk that wears a single material.</summary>
    public static Mesh SingleMesh(this StaticWorldChunkMesh chunk) =>
        chunk.Submeshes.ShouldHaveSingleItem().Mesh;

    /// <summary>The one GPU mesh of a chunk that wears a single material, as the fake backend's type.</summary>
    public static FakeMesh SingleFakeMesh(this StaticWorldChunkMesh chunk) =>
        (FakeMesh)chunk.SingleMesh();

    /// <summary>The material the chunk's single submesh resolved to at upload time.</summary>
    public static Material? SingleMaterial(this StaticWorldChunkMesh chunk) =>
        chunk.Submeshes.ShouldHaveSingleItem().Material;
}
