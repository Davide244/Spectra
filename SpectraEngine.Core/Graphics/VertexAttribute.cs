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
}
