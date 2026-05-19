using SpectraEngine.Core.Graphics;

namespace SpectraEngine.Core.Scene;

/// <summary>
/// A renderable attached to a <see cref="SceneNode"/>: the geometry to draw and
/// the material to draw it with. GPU lifetime of the <see cref="Mesh"/> is owned
/// by the renderer that created it, not by this component.
/// </summary>
public sealed class MeshRenderer
{
    public MeshRenderer(Mesh mesh, Material material)
    {
        Mesh = mesh;
        Material = material;
    }

    public Mesh Mesh { get; }

    public Material Material { get; }
}
