using System.Runtime.InteropServices;

namespace SpectraEngine.Core.Assets.Models;

/// <summary>
/// One level of detail, exactly as its twelve bytes sit in a <c>LODS</c> section.
/// </summary>
/// <remarks>
/// An LOD is a range of submeshes over the model's one shared vertex and index
/// buffer, which is the whole reason the format keeps a single pair of buffers:
/// switching level is then a change of draw range with zero GPU resource churn,
/// where per-LOD buffers would make it a create and a destroy at exactly the
/// moment the camera is moving.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct SmodelLod
{
    /// <summary>
    /// The projected height, as a fraction of the viewport, below which this
    /// level is the one to draw. Compared against a screen measurement rather
    /// than a distance so the choice does not change with field of view.
    /// </summary>
    public readonly float ScreenHeightThreshold;

    /// <summary>Index of this level's first submesh in <c>SUBM</c>.</summary>
    public readonly uint FirstSubmesh;

    /// <summary>How many consecutive submeshes this level covers.</summary>
    public readonly uint SubmeshCount;

    /// <summary>Builds one level-of-detail record. Every field is assigned.</summary>
    public SmodelLod(float screenHeightThreshold, uint firstSubmesh, uint submeshCount)
    {
        ScreenHeightThreshold = screenHeightThreshold;
        FirstSubmesh = firstSubmesh;
        SubmeshCount = submeshCount;
    }
}
