using System;
using System.Numerics;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Assets.Sources;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Core.Maps.Compiled;

/// <summary>
/// Loads a baked <c>.scmap</c> into a live scene, running ZERO CSG.
/// </summary>
/// <remarks>
/// <para><b>This is the shipped game's map path, and the editor's is deliberately
/// a different one.</b> The editor authors nodes from a <c>.smap</c> and calls
/// <c>Scene.RebuildStaticWorld</c>, which is the synchronous cache-free compile
/// the async pipeline keeps precisely for load time. Here nothing is compiled: the
/// chunk meshes go from the file's own bytes to the GPU, the per-cell trees are
/// read over the same mapping, and the scene refuses to carve for as long as the
/// result is installed.</para>
/// <para><b>The order of the four passes is the design.</b> (1) The header,
/// geometry version, vertex layout and compile constants gate in
/// <see cref="ScmapReader"/> before a single table is trusted - a map baked on
/// another cell size or another weld band is a world whose chunks do not line up,
/// which renders as gaps rather than as an error. (2) <c>ASTB</c> is walked IN
/// TABLE ORDER and every material path interned, building the file-index to
/// <see cref="MaterialRef"/> remap; ids are per-process interning order and table
/// indices are the file's, and conflating them mis-textures the world only when a
/// second map interns first, which is exactly the coincidence that hides the bug.
/// (3) The nodes are rebuilt in ONE FORWARD PASS, which is what
/// <c>ParentIndex &lt; SelfIndex</c> was written to permit. (4) The chunks are
/// adopted.</para>
/// <para><b>A node whose geometry was baked gets NO brush, and that is the whole
/// runtime half of the double-geometry hazard.</b> A <c>--keep-brush-source</c>
/// cook puts a world brush's planes in <c>BRSH</c> as well as its surfaces in the
/// chunks; a loader that helpfully rebuilt the brush would hand the scene a
/// placement, the placement would arm a compile, and every wall would be drawn
/// twice with z-fighting that reads as depth precision rather than as a map
/// loader. <see cref="ScmapBrushSource.IsReCarvable"/> is the only predicate this
/// file asks.</para>
/// </remarks>
public static class CompiledMapLoader
{
    /// <summary>
    /// Replaces <paramref name="scene"/>'s graph and static world with the
    /// compiled map in <paramref name="file"/>. Render thread only.
    /// </summary>
    /// <param name="scene">The scene to load into. Its existing graph is replaced.</param>
    /// <param name="renderer">Where the chunk meshes are created.</param>
    /// <param name="file">
    /// The map's bytes. <b>Ownership passes to this call</b>: on success the scene
    /// holds them for the world's lifetime (the BSP nodes are a window into them,
    /// and on a mounted pack that window is a memory-mapped view), and on failure
    /// they are released here.
    /// </param>
    /// <param name="source">
    /// What to call the map in a message: a logical asset path, not a machine
    /// path, so the same failure reads the same way from a pack and from a loose
    /// cook directory.
    /// </param>
    /// <exception cref="ScmapFormatException">The file is not a readable, loadable <c>.scmap</c>.</exception>
    public static CompiledMapLoadReport Load(
        Scene.Scene scene, Renderer renderer, ContentBlob file, string source)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(source);

        var report = new CompiledMapLoadReport();

        try
        {
            ScmapDocument document = ScmapReader.Read(file.Span, source);
            report.SkippedSections = document.SkippedSectionCount;

            // Any previous adoption goes first: it owns a mapping, and it also
            // holds the guard, which the graph replacement below would otherwise
            // trip on its way out of an authored world.
            scene.ReleaseCompiledStaticWorld(renderer);

            MaterialRef[] materials = InternAssets(in document, report);
            RebuildGraph(scene, in document, materials, report);

            scene.AdoptCompiledStaticWorld(renderer, in document, materials, file, report);
            return report;
        }
        catch
        {
            // Nothing downstream took the bytes, so this call still owns them. A
            // blob left undisposed on a failed load keeps a pack reference alive
            // for the life of the process, which is a mount that can never be
            // released and no message anywhere.
            file.Dispose();
            throw;
        }
    }

    // ASTB, in TABLE ORDER, and the order is the contract: the remap is indexed by
    // the file's own row number, so interning out of order would build a correct
    // dictionary and a wrong array.
    private static MaterialRef[] InternAssets(scoped in ScmapDocument document, CompiledMapLoadReport report)
    {
        var materials = new MaterialRef[document.Assets.Length];
        int interned = 0;

        for (int i = 0; i < materials.Length; i++)
        {
            // A row that is not a material has no MaterialRef to be. Model rows are
            // here for a mesh instance's PayloadIndex to name, and every other kind
            // is reserved; a row this build has no lane for resolves to the default
            // rather than being interned as a material path, which would put a
            // model into the material registry under its own name.
            if (document.Assets[i].AssetKind != PackEntryKind.Material)
            {
                materials[i] = MaterialRef.Default;
                continue;
            }

            materials[i] = MaterialRegistry.Intern(document.AssetPath(i));
            interned++;
        }

        report.MaterialsInterned = interned;
        return materials;
    }

    // One forward pass, no fixup table: records are pre-order and every parent
    // index is strictly less than its child's, which the reader has already
    // checked, so a parent node exists by the time its child is read.
    private static void RebuildGraph(
        Scene.Scene scene,
        scoped in ScmapDocument document,
        ReadOnlySpan<MaterialRef> materials,
        CompiledMapLoadReport report)
    {
        for (int i = scene.Root.Children.Count - 1; i >= 0; i--)
            scene.Root.RemoveChild(scene.Root.Children[i]);

        scene.Name = document.SceneName;

        ScmapBrushSource brushes = document.HasBrushSource
            ? document.BrushSource()
            : default;

        // node index -> BRSH record, built once. The link runs one way in the
        // file, from the brush to its node, so a loader that wants the other
        // direction builds it here rather than making the format carry both.
        var brushOfNode = new int[document.Nodes.Length];
        brushOfNode.AsSpan().Fill(-1);
        for (int b = 0; b < brushes.Brushes.Length; b++)
            brushOfNode[(int)brushes.Brushes[b].NodeIndex] = b;

        var nodes = new SceneNode[document.Nodes.Length];

        for (int i = 0; i < nodes.Length; i++)
        {
            ScmapNodeRecord record = document.Nodes[i];
            string name = document.NodeName(i);

            // The deserialisation door, exactly as the authored path uses it: the
            // other constructor mints a fresh id, which would break every command
            // that addresses a node by id and every entity wire that names one.
            var node = new SceneNode(name, record.NodeId)
            {
                LocalTransform = new Transform
                {
                    Position = record.LocalPosition,
                    Rotation = record.LocalRotation,
                    Scale = record.LocalScale,
                },
            };

            AttachPayload(node, in record, i, name, brushes, brushOfNode[i], materials, report);

            if (record.ParentIndex < 0) scene.Root.AddChild(node);
            else nodes[record.ParentIndex].AddChild(node);

            nodes[i] = node;
        }

        report.NodesLoaded = nodes.Length;
    }

    private static void AttachPayload(
        SceneNode node,
        scoped in ScmapNodeRecord record,
        int index,
        string name,
        scoped in ScmapBrushSource brushes,
        int brushRecord,
        ReadOnlySpan<MaterialRef> materials,
        CompiledMapLoadReport report)
    {
        switch (record.PayloadKind)
        {
            case ScmapPayloadKind.StaticWorldBrush:
                // NOTHING is attached, whether or not BRSH carries this brush's
                // planes. Its surfaces are already in the chunk meshes; giving the
                // node a brush would put it in the placement list, and the level
                // would be drawn twice.
                if (brushRecord >= 0) report.BakedBrushSourceSkipped();
                break;

            case ScmapPayloadKind.PartBrush:
                if (brushRecord < 0)
                {
                    // A part's planes live nowhere else - it is never baked into a
                    // chunk and its mesh is built at runtime from its own Brush -
                    // so a file without them ships a level whose parts are
                    // invisible, which is worth a name rather than a silence.
                    report.PartBrushWithoutSource(name);
                    break;
                }

                // Kind BEFORE brush: the brush setter reads the kind to decide
                // which lane the brush joins, so the reverse order admits a part
                // brush to the static world for one write.
                node.BrushKind = BrushKind.Part;

                if (TryBuildBrush(in brushes, brushRecord, in record, materials, out Brush? brush))
                    node.Brush = brush;
                else
                    report.BrushRefused(name);

                break;

            case ScmapPayloadKind.MeshInstance:
                // NAMED, never dropped in silence. The format gives a mesh instance
                // one asset row and no submesh index (MeshSource.SubmeshIndex has
                // no table to name), so binding submesh 0 would be right for a
                // single-submesh prop and quietly wrong for every other one. The
                // node arrives where it belongs, drawing nothing, and says so.
                report.MeshInstanceUnbound(name);
                break;

            default:
                // None and PrefabRoot carry nothing to attach; the reader has
                // already refused every value this build has no meaning for, and
                // the retired kind 3 by name.
                _ = index;
                break;
        }
    }

    private static bool TryBuildBrush(
        scoped in ScmapBrushSource brushes,
        int brushRecord,
        scoped in ScmapNodeRecord node,
        ReadOnlySpan<MaterialRef> materials,
        out Brush? brush)
    {
        ScmapBrushRecord record = brushes.Brushes[brushRecord];
        int start = (int)record.PlaneStart;
        int count = (int)record.PlaneCount;

        var planes = new Plane[count];
        var faces = new FaceSurface[count];

        try
        {
            for (int k = 0; k < count; k++)
            {
                planes[k] = brushes.Planes[start + k];

                ScmapFaceRecord face = brushes.Faces[start + k];
                MaterialRef material =
                    face.AssetIndex != ScmapFormat.NoAssetIndex && face.AssetIndex < (uint)materials.Length
                        ? materials[(int)face.AssetIndex]
                        : MaterialRef.Default;

                faces[k] = new FaceSurface(
                    material, face.UAxis, face.VAxis,
                    face.UOffset, face.VOffset, face.UScale, face.VScale);
            }

            // Identity, because a node-attached brush IGNORES Brush.Transform - the
            // scene snapshots the node's world matrix into a placement instead - so
            // the standalone transform is not in the file and reconstructing one
            // here would be inventing a value nothing reads.
            brush = new Brush(
                planes,
                Matrix4x4.Identity,
                faces,
                node.IsSubtractiveBrush ? BrushOperation.Subtractive : BrushOperation.Additive);

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            // Brush and FaceSurface validate convexity, boundedness, duplicate
            // planes and unusable scales, and they throw from deep inside CSG code
            // that has never heard of a file. A refused brush is one prop or one
            // wall missing from a level somebody can still see and still fix, which
            // is the same trade the authored path makes for a missing model.
            brush = null;
            return false;
        }
    }
}
