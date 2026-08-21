using Silk.NET.Assimp;

namespace SpectraEngine.Core.Assets;

/// <summary>
/// Tuning for <see cref="ModelImporter.Import"/>. The defaults are what an
/// engine wants for static prop geometry; every switch here exists because some
/// real content needs the other answer.
/// </summary>
/// <remarks>
/// Immutable, so one instance can be shared by concurrent imports. Use
/// <see cref="Default"/> unless a specific file needs otherwise, and prefer
/// <c>with</c> expressions over building one from scratch.
/// </remarks>
public sealed record ModelImportOptions
{
    /// <summary>The settings used when a caller passes none.</summary>
    public static ModelImportOptions Default { get; } = new();

    /// <summary>
    /// Split polygons into triangles. Off only makes sense for a caller that
    /// wants to inspect an untouched file, since nothing downstream can draw an
    /// n-gon.
    /// </summary>
    public bool Triangulate { get; init; } = true;

    /// <summary>
    /// Generate smoothed vertex normals for meshes that carry none. Files that
    /// already have normals keep them either way — this never overwrites
    /// authored data.
    /// </summary>
    public bool GenerateMissingNormals { get; init; } = true;

    /// <summary>
    /// Merge vertices that are identical in every attribute. Shrinks the vertex
    /// buffer substantially for hard-surface props and is what makes an indexed
    /// mesh worth having.
    /// </summary>
    public bool JoinIdenticalVertices { get; init; } = true;

    /// <summary>
    /// Reorder triangles for post-transform vertex-cache locality. Pure win for
    /// static geometry; costs a little import time.
    /// </summary>
    public bool OptimizeVertexCache { get; init; } = true;

    /// <summary>
    /// Run the importer's own consistency validation. Turns a subtly corrupt
    /// file into a diagnosable failure instead of geometry that renders wrong.
    /// </summary>
    public bool Validate { get; init; } = true;

    /// <summary>
    /// Flip the v texture coordinate.
    /// </summary>
    /// <remarks>
    /// The engine samples with v = 0 at the BOTTOM of the image, because
    /// <see cref="ImageDecoder"/> flips decoded rows to the bottom-up upload
    /// convention the renderers document. OBJ already agrees with that, so the
    /// default is off. glTF and most DCC exports put v = 0 at the top; content
    /// authored that way needs this on, and the symptom of forgetting is a
    /// texture that is mirrored vertically rather than one that is missing.
    /// </remarks>
    public bool FlipTextureV { get; init; }

    /// <summary>
    /// Reverse triangle winding. The engine draws counter-clockwise front faces
    /// (see <c>Primitives.Cube</c>); content exported for a clockwise convention
    /// needs this on, or it renders inside-out under backface culling.
    /// </summary>
    public bool FlipWinding { get; init; }

    /// <summary>
    /// Translates these switches into the post-processing flag set the native
    /// importer takes. Internal because the flag values are an Assimp detail
    /// that must not leak past <see cref="ModelImporter"/>.
    /// </summary>
    internal uint BuildPostProcessFlags()
    {
        PostProcessSteps steps = PostProcessSteps.None;

        if (Triangulate) steps |= PostProcessSteps.Triangulate;
        if (GenerateMissingNormals) steps |= PostProcessSteps.GenerateSmoothNormals;
        if (JoinIdenticalVertices) steps |= PostProcessSteps.JoinIdenticalVertices;
        if (OptimizeVertexCache) steps |= PostProcessSteps.ImproveCacheLocality;
        if (Validate) steps |= PostProcessSteps.ValidateDataStructure;
        if (FlipTextureV) steps |= PostProcessSteps.FlipUVs;
        if (FlipWinding) steps |= PostProcessSteps.FlipWindingOrder;

        // Not optional. FindDegenerates collapses zero-area triangles into
        // points/lines and SortByPrimitiveType then splits those off into their
        // own meshes, which the converter drops — that pair is what guarantees
        // every ModelMesh handed downstream is pure, drawable triangles.
        steps |= PostProcessSteps.FindDegenerates | PostProcessSteps.SortByPrimitiveType;

        return (uint)steps;
    }
}
