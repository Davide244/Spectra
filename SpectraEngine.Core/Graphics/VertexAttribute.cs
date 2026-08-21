using System;

namespace SpectraEngine.Core.Graphics;

public readonly struct VertexAttribute
{
    public uint Location { get; }
    public uint ComponentCount { get; }

    public VertexAttribute(uint location, uint componentCount)
    {
        Location = location;
        ComponentCount = componentCount;
    }

    private static readonly VertexAttribute[] _standardLayout =
    [
        new(location: 0, componentCount: 3),
        new(location: 1, componentCount: 3),
        new(location: 2, componentCount: 2),
    ];

    /// <summary>
    /// The engine's standard interleaved vertex layout (8 floats): position (3),
    /// normal (3), uv (2). Matches <c>Primitives.Cube</c> and
    /// <c>CsgWorld.BuildMesh</c> output.
    /// </summary>
    public static ReadOnlySpan<VertexAttribute> StandardLayout => _standardLayout;
}
