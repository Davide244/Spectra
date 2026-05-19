namespace SpectraEngine.Core.Bsp;

/// <summary>Where a polygon lies relative to a splitting plane.</summary>
public enum PolygonClassification
{
    /// <summary>Every vertex lies on the plane (within tolerance).</summary>
    Coplanar,

    /// <summary>The polygon lies entirely on the plane's normal side.</summary>
    Front,

    /// <summary>The polygon lies entirely behind the plane.</summary>
    Back,

    /// <summary>The polygon has vertices on both sides and must be split.</summary>
    Spanning,
}
