namespace SpectraEngine.Core.Assets.Models;

/// <summary>
/// What one vertex attribute in a <c>.smodel</c> means.
/// </summary>
/// <remarks>
/// <b>Append-only, exactly like the light-kind numbering.</b> These values are
/// hashed into the header's vertex layout id and stored per attribute, so
/// inserting a semantic renumbers every one after it: a file cooked before the
/// insert would then declare, say, <c>Uv0</c> where it means <c>Tangent4</c>, and
/// the layout id would change with it so the mismatch reports as a layout change
/// rather than as the renumbering it is.
/// </remarks>
public enum SmodelSemantic : byte
{
    /// <summary>Object-space position.</summary>
    Position = 0,

    /// <summary>Object-space normal.</summary>
    Normal = 1,

    /// <summary>Tangent with the bitangent sign in <c>w</c>, which is why it is four components.</summary>
    Tangent4 = 2,

    /// <summary>The first texture coordinate set.</summary>
    Uv0 = 3,

    /// <summary>The second texture coordinate set, typically a lightmap.</summary>
    Uv1 = 4,

    /// <summary>The first vertex colour set.</summary>
    Color0 = 5,

    /// <summary>Joint indices for skinning.</summary>
    BlendIndices = 6,

    /// <summary>Joint weights for skinning.</summary>
    BlendWeights = 7,
}
