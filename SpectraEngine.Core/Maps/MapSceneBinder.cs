using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Core.Maps;

/// <summary>
/// Projects a live <see cref="Scene.Scene"/> to a <see cref="MapDocument"/> and
/// builds one back.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the lossy half of the round trip, and what it loses is stated
/// rather than discovered.</b> The document round trip is exact; this one is
/// not, in two directions. Going out, a <see cref="MeshRenderer"/> cannot be
/// written at all - see <see cref="MapSaveReport"/>. Coming back,
/// <c>Brush</c>'s constructor re-normalises every plane, so a hand-authored
/// <c>[2, 0, 0, -64]</c> becomes <c>[1, 0, 0, -32]</c> on the next save. That
/// second one is a canonicalisation rather than a defect, since it is the same
/// plane, but it is why byte identity is a claim about documents and never
/// about scenes.
/// </para>
/// <para>
/// <b>Every brush gets its own instance, and that is not an implementation
/// detail.</b> <c>CsgCompileCache</c> and <c>PartBrushMeshCache</c> both key on
/// <c>Brush</c> reference identity, so sharing one instance across two nodes
/// would make every duplicate past the first re-carve on every compile,
/// forever, while rendering perfectly.
/// </para>
/// <para>
/// <b>Render-thread only</b>, like every other scene mutation.
/// </para>
/// </remarks>
public static class MapSceneBinder
{
    // --- scene -> document --------------------------------------------------

    /// <summary>Projects <paramref name="scene"/>'s graph to a document.</summary>
    public static MapDocument FromScene(Scene.Scene scene) => FromScene(scene, null);

    /// <summary>
    /// Projects <paramref name="scene"/>'s graph to a document, recording what
    /// could not be written into <paramref name="report"/>.
    /// </summary>
    public static MapDocument FromScene(Scene.Scene scene, MapSaveReport? report)
    {
        ArgumentNullException.ThrowIfNull(scene);

        var document = new MapDocument();
        document.Scene.Name = scene.Name;

        foreach (SceneNode child in scene.Root.Children)
            document.Nodes.Add(NodeToMap(child, report));

        return document;
    }

    private static MapNode NodeToMap(SceneNode node, MapSaveReport? report)
    {
        var mapped = new MapNode
        {
            Id = node.Id,
            Name = node.Name,
            // Omitted when World, which is what a file with no 'kind' means.
            Kind = node.BrushKind == BrushKind.Part ? BrushKind.Part : null,
            Transform = ToMap(node.LocalTransform),
        };

        if (node.Brush is { } brush)
            mapped.Brush = BrushToMap(brush);

        if (node.Light is { } light)
        {
            mapped.Light = new MapLight
            {
                Kind = light.Kind,
                Color = light.Color,
                Intensity = light.Intensity,
                Range = light.Range,
                Enabled = light.Enabled,
            };
        }

        // A mesh renderer holds two live objects and no source reference of any
        // kind: Mesh has no name, no path and no owning asset, and
        // ModelInstantiator records nothing on the node it builds. So there is
        // genuinely nothing to write, and the node saves as a placed, named,
        // identified node with no renderer on it. Counted rather than dropped
        // in silence, because a map that quietly forgets the props is worse
        // than one that says it did.
        if (node.MeshRenderer is not null)
            report?.RecordMeshRenderer(node);

        foreach (SceneNode child in node.Children)
            mapped.Children.Add(NodeToMap(child, report));

        return mapped;
    }

    private static MapBrush BrushToMap(Brush brush)
    {
        var mapped = new MapBrush
        {
            Operation = brush.Operation,
            Transform = brush.Transform,
        };

        foreach (Plane plane in brush.LocalPlanes)
            mapped.Planes.Add(new Vector4(plane.Normal.X, plane.Normal.Y, plane.Normal.Z, plane.D));

        foreach (FaceSurface face in brush.FaceSurfaces)
            mapped.Faces.Add(FaceToMap(face));

        return mapped;
    }

    private static MapFace FaceToMap(FaceSurface face)
    {
        var mapped = new MapFace
        {
            // The path, never the id: MaterialRef ids are handed out in
            // first-intern order within one process, so the same map loaded
            // second gets different ones. TryGetPath answers false for the
            // default material, which is exactly the case that writes nothing.
            Material = MaterialRegistry.TryGetPath(face.Material, out string path) ? path : null,
            // A zero axis IS the world-aligned encoding, so it is written as an
            // absent member rather than as three zeros.
            UAxis = face.IsWorldAligned ? null : face.UAxis,
            VAxis = face.IsWorldAligned ? null : face.VAxis,
            UOffset = face.UOffset,
            VOffset = face.VOffset,
            // FaceSurface treats a stored zero scale as 1 and launders it on
            // most paths, so the effective value is what round-trips. Writing
            // the raw 0 would produce a file the FaceSurface constructor then
            // refuses to load.
            UScale = face.UScale != 0f ? face.UScale : 1f,
            VScale = face.VScale != 0f ? face.VScale : 1f,
        };

        return mapped;
    }

    private static MapTransform ToMap(Transform transform) => new()
    {
        Position = transform.Position,
        Rotation = transform.Rotation,
        Scale = transform.Scale,
    };

    // --- document -> scene --------------------------------------------------

    /// <summary>
    /// Replaces <paramref name="scene"/>'s graph with the document's.
    /// </summary>
    /// <remarks>
    /// The root itself is never replaced: <c>Scene.Root</c> is get-only and
    /// owns the scene back-pointer every setter's side effects run through.
    /// </remarks>
    /// <exception cref="MapFormatException">A node's brush cannot be built.</exception>
    public static void ApplyTo(MapDocument document, Scene.Scene scene)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(scene);

        for (int i = scene.Root.Children.Count - 1; i >= 0; i--)
            scene.Root.RemoveChild(scene.Root.Children[i]);

        scene.Name = document.Scene.Name;

        foreach (MapNode node in document.Nodes)
            scene.Root.AddChild(ToSceneNode(node));
    }

    private static SceneNode ToSceneNode(MapNode mapped)
    {
        // The deserialisation door. The other constructor mints a fresh id,
        // which would break every command that addresses a node by id and every
        // undo of a delete.
        var node = new SceneNode(mapped.Name, mapped.Id)
        {
            LocalTransform = new Transform
            {
                Position = mapped.Transform.Position,
                Rotation = mapped.Transform.Rotation,
                Scale = mapped.Transform.Scale,
            },
        };

        // Kind BEFORE brush, exactly as the demo places props: the brush setter
        // reads the kind to decide which lane the brush joins, so the reverse
        // order admits a part brush to the static world for one write.
        if (mapped.Kind is { } kind)
            node.BrushKind = kind;

        if (mapped.Brush is { } brush)
            node.Brush = ToBrush(brush, mapped);

        if (mapped.Light is { } light)
        {
            node.Light = new Light
            {
                Kind = light.Kind,
                Color = light.Color,
                Intensity = light.Intensity,
                Range = light.Range,
                Enabled = light.Enabled,
            };
        }

        // Appended in order, because child order is traversal order is
        // static-world placement order, and placement order breaks ties in the
        // carve. A load that reordered siblings would build a level that is
        // valid, different, and bit-unequal to the one that was saved.
        foreach (MapNode child in mapped.Children)
            node.AddChild(ToSceneNode(child));

        return node;
    }

    private static Brush ToBrush(MapBrush mapped, MapNode owner)
    {
        var planes = new Plane[mapped.Planes.Count];
        for (int i = 0; i < planes.Length; i++)
        {
            Vector4 p = mapped.Planes[i];
            planes[i] = new Plane(p.X, p.Y, p.Z, p.W);
        }

        var faces = new FaceSurface[mapped.Faces.Count];
        for (int i = 0; i < faces.Length; i++)
        {
            MapFace face = mapped.Faces[i];
            MaterialRef material = string.IsNullOrEmpty(face.Material)
                ? MaterialRef.Default
                : MaterialRegistry.Intern(face.Material);

            try
            {
                faces[i] = new FaceSurface(
                    material,
                    face.UAxis ?? Vector3.Zero,
                    face.VAxis ?? Vector3.Zero,
                    face.UOffset, face.VOffset, face.UScale, face.VScale);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                throw new MapFormatException(
                    $"Face {i}: {ex.Message}", owner.Name, owner.SourceOffset, ex);
            }
        }

        try
        {
            return new Brush(planes, mapped.Transform, faces, mapped.Operation);
        }
        catch (ArgumentException ex)
        {
            // Brush validates convexity, boundedness and duplicate planes, and
            // throws from deep inside CSG code that has never heard of a file.
            // Unwrapped, a hand-edited map reports "Planes 2 and 5 are
            // near-coplanar duplicates" and names neither the node nor the
            // place in the document.
            throw new MapFormatException(
                $"This brush cannot be built: {ex.Message}", owner.Name, owner.SourceOffset, ex);
        }
    }
}

/// <summary>
/// What a save could not write down.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mesh nodes are the one real gap in v1, and it is structural rather than
/// unfinished.</b> <c>MeshRenderer</c> holds a <c>Mesh</c> and a
/// <c>Material</c>, both live objects. <c>Mesh</c> carries no name, no path and
/// no owning-asset reference - <c>Renderer.CreateMesh</c> takes raw vertex and
/// index spans, so nothing at creation time could record an origin even if
/// there were a field for it - and <c>ModelInstantiator</c> writes only a name,
/// a transform and the renderer onto each node it builds. So a saved mesh node
/// keeps its identity, name, placement and children, and loses its geometry.
/// </para>
/// <para>
/// Closing it means recording <c>(model path, submesh index, import options)</c>
/// on the node at instantiation time, and all three parts are needed: the index
/// is positional into <c>ModelAsset.Meshes</c>, and the options reshape that
/// list - while being pinned to whichever caller loaded the path first. That is
/// a scene-graph change with a real design in front of it, not something to
/// guess at inside a codec.
/// </para>
/// </remarks>
public sealed class MapSaveReport
{
    private readonly List<string> _meshNodes = [];

    /// <summary>Nodes whose <see cref="MeshRenderer"/> could not be written.</summary>
    public IReadOnlyList<string> MeshRendererNodes => _meshNodes;

    /// <summary>Whether anything was lost.</summary>
    public bool IsComplete => _meshNodes.Count == 0;

    internal void RecordMeshRenderer(SceneNode node) => _meshNodes.Add(node.Name);

    /// <summary>A one-line summary for a log, or null when nothing was lost.</summary>
    public string? Describe() => _meshNodes.Count == 0
        ? null
        : $"{_meshNodes.Count} mesh node(s) saved without geometry (no model reference is recorded on a "
          + $"node): {string.Join(", ", _meshNodes.Count > 8 ? _meshNodes.GetRange(0, 8) : _meshNodes)}"
          + (_meshNodes.Count > 8 ? ", ..." : string.Empty);
}
