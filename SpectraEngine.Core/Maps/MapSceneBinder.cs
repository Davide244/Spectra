using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;
using System.IO;
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
/// not, in two directions. Going out, a mesh is written as a reference to a
/// model file, so one built in code has nothing to name - see
/// <see cref="MapSaveReport"/>. Coming back,
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

        // A mesh is written as a REFERENCE, never as geometry: vertices belong
        // in the cooked artifact, and an authored map names the source file the
        // same way a face names a material path.
        if (node.MeshRenderer is not null)
        {
            if (node.MeshSource is { } source)
            {
                mapped.Mesh = new MapMeshSource
                {
                    Model = source.ModelPath,
                    Submesh = source.MeshIndex,
                };
            }
            else
            {
                // A mesh built from raw arrays has no file behind it, so there
                // is nothing to name. Permanent rather than unfinished, and
                // reported rather than dropped in silence: a map that quietly
                // forgets a prop is worse than one that says it did.
                report?.RecordUnsourcedMesh(node);
            }
        }

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
    public static void ApplyTo(MapDocument document, Scene.Scene scene) =>
        ApplyTo(document, scene, null);

    /// <summary>
    /// Replaces the scene's graph with the document's, recording anything that
    /// could not be resolved into <paramref name="report"/>.
    /// </summary>
    /// <exception cref="MapFormatException">A node's brush cannot be built.</exception>
    public static void ApplyTo(MapDocument document, Scene.Scene scene, MapLoadReport? report)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(scene);

        for (int i = scene.Root.Children.Count - 1; i >= 0; i--)
            scene.Root.RemoveChild(scene.Root.Children[i]);

        scene.Name = document.Scene.Name;

        foreach (MapNode node in document.Nodes)
            scene.Root.AddChild(ToSceneNode(node, scene.Assets, report));
    }

    private static SceneNode ToSceneNode(MapNode mapped, AssetManager? assets, MapLoadReport? report)
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

        if (mapped.Mesh is { } mesh)
            AttachMesh(node, mesh, assets, report);

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
            node.AddChild(ToSceneNode(child, assets, report));

        return node;
    }

    /// <summary>
    /// Resolves a model reference back into a live renderer.
    /// </summary>
    /// <remarks>
    /// <b>Every failure here degrades to a node with no renderer and a line in
    /// the report, never an exception.</b> A missing or changed model is a
    /// content problem, and the engine's standing rule is that content errors
    /// must not reach the draw loop: the rest of the level is perfectly good and
    /// a level designer needs to see it in order to fix the prop. That is the
    /// opposite of the brush path, which throws, because a brush that cannot be
    /// built is a hole in the world rather than a missing decoration.
    /// </remarks>
    private static void AttachMesh(
        SceneNode node, MapMeshSource mesh, AssetManager? assets, MapLoadReport? report)
    {
        if (assets is null)
        {
            report?.RecordUnresolved(node.Name, mesh.Model, "the scene has no asset manager attached");
            return;
        }

        try
        {
            ModelAsset model = assets.LoadModel(mesh.Model);
            if (model.Data is not { } data)
            {
                report?.RecordUnresolved(node.Name, mesh.Model, model.Error ?? "the model is not loaded");
                return;
            }

            // The index is positional, so a re-exported model can name a
            // submesh that is no longer there. Checked rather than trusted: an
            // unchecked index is an IndexOutOfRangeException from inside a load,
            // which says nothing about which node or which file.
            if (mesh.Submesh >= model.Meshes.Count)
            {
                report?.RecordUnresolved(node.Name, mesh.Model,
                    $"submesh {mesh.Submesh} does not exist; the model has {model.Meshes.Count}");
                return;
            }

            node.MeshRenderer = new MeshRenderer(
                model.Meshes[mesh.Submesh], model.MaterialFor(data.Meshes[mesh.Submesh]));
            node.MeshSource = new MeshSource(mesh.Model, mesh.Submesh);
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            report?.RecordUnresolved(node.Name, mesh.Model, ex.Message);
        }
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
/// <b>A mesh built in code cannot be saved, and that is permanent rather than
/// unfinished.</b> A node whose renderer came from a model file carries a
/// <see cref="MeshSource"/> and is written as a reference to it. A node whose
/// mesh came from raw vertex arrays - <c>Primitives.Cube()</c>, a procedural
/// generator, anything handed straight to <c>Renderer.CreateMesh</c> - has no
/// file to name, so the node saves with its identity, name, placement and
/// children, and loses its geometry.
/// </para>
/// <para>
/// The only ways to close that are to write the vertices into the authored map,
/// which is derived data in a file whose whole rule is that it holds none, or
/// to give procedural geometry a recipe worth naming. Both are real designs,
/// and neither belongs inside a codec.
/// </para>
/// </remarks>
public sealed class MapSaveReport
{
    private readonly List<string> _unsourced = [];

    /// <summary>Nodes whose mesh had no model behind it to name.</summary>
    public IReadOnlyList<string> UnsourcedMeshNodes => _unsourced;

    /// <summary>Whether anything was lost.</summary>
    public bool IsComplete => _unsourced.Count == 0;

    internal void RecordUnsourcedMesh(SceneNode node) => _unsourced.Add(node.Name);

    /// <summary>A one-line summary for a log, or null when nothing was lost.</summary>
    public string? Describe() => _unsourced.Count == 0
        ? null
        : $"{_unsourced.Count} mesh node(s) saved without geometry, because their meshes were built in "
          + $"code and name no model file: {Join(_unsourced)}";

    internal static string Join(List<string> names) =>
        string.Join(", ", names.Count > 8 ? names.GetRange(0, 8) : names)
        + (names.Count > 8 ? ", ..." : string.Empty);
}

/// <summary>
/// What a load could not resolve.
/// </summary>
/// <remarks>
/// <b>A map that names a model the project no longer has still loads.</b> The
/// node arrives with its identity, placement and children and no renderer, and
/// the reason lands here. That follows the engine's standing rule that content
/// errors must not reach the draw loop, and it is the difference between a
/// level designer seeing their level with one prop missing and seeing an
/// exception.
/// </remarks>
public sealed class MapLoadReport
{
    private readonly List<string> _unresolved = [];

    /// <summary>One line per node whose model reference could not be resolved.</summary>
    public IReadOnlyList<string> UnresolvedMeshes => _unresolved;

    /// <summary>Whether everything the map named was found.</summary>
    public bool IsComplete => _unresolved.Count == 0;

    internal void RecordUnresolved(string nodeName, string modelPath, string reason) =>
        _unresolved.Add($"{nodeName} -> {modelPath} ({reason})");

    /// <summary>A one-line summary for a log, or null when nothing was missing.</summary>
    public string? Describe() => _unresolved.Count == 0
        ? null
        : $"{_unresolved.Count} mesh node(s) loaded without geometry: {MapSaveReport.Join(_unresolved)}";
}
