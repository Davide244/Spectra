using System;
using System.Collections.Generic;
using System.Numerics;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Editing.Viewport;

/// <summary>
/// Draws the edges of every <see cref="BrushKind.Part"/> brush, so a part is
/// never mistaken for world geometry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not cosmetic.</b> A part brush and a world brush are drawn
/// from the same planes with the same materials and are indistinguishable at
/// rest — but they behave differently in ways that show up as apparent engine
/// bugs. A part does not carve and is not carved, so two overlapping parts
/// interpenetrate where two world brushes would have merged into one skin; a
/// part face left coplanar with a world face z-fights permanently; and a seam
/// that would have been welded stays two independent surfaces. Every one of
/// those reads as "the renderer is broken" unless the editor says, at the
/// moment you look at it, that this brush is a part.
/// </para>
/// <para>
/// <b>Oriented edges, not a bounding box.</b> The outline is the brush's own
/// face loops under the node's world matrix, so a rotated part reads as the
/// solid it is. An AABB would be cheaper and would lie about every part that is
/// not axis-aligned — which, for anything simulated, is most of them.
/// </para>
/// <para>
/// <b>The budget is disclosed, never silent.</b> At Roblox scale "outline every
/// part" is both a frame cost and visual noise, so the pass stops after
/// <see cref="MaxOutlines"/> brushes and reports how many it skipped through
/// <see cref="SkippedLastDraw"/>. A cap that quietly truncates would read as
/// "all parts are outlined" while some silently were not — which is exactly the
/// class of lie this overlay exists to prevent.
/// </para>
/// </remarks>
public sealed class PartBrushOverlay
{
    /// <summary>The outline colour: cyan, unused by any gizmo axis or the selection highlight.</summary>
    public static readonly Vector3 DefaultColor = new(0.25f, 0.85f, 0.95f);

    /// <summary>Whether the overlay draws at all. On in an editor, off in a game.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Outline colour.</summary>
    public Vector3 Color { get; set; } = DefaultColor;

    /// <summary>
    /// How many part brushes may be outlined in one frame before the pass gives
    /// up. Reached only in scenes with a lot of parts, where the outlines would
    /// be unreadable anyway.
    /// </summary>
    public int MaxOutlines { get; set; } = 256;

    /// <summary>Part brushes the last <see cref="Draw"/> outlined.</summary>
    public int DrawnLastDraw { get; private set; }

    /// <summary>
    /// Part brushes the last <see cref="Draw"/> skipped because
    /// <see cref="MaxOutlines"/> was reached. Non-zero means the viewport is
    /// showing an incomplete picture, and something should say so.
    /// </summary>
    public int SkippedLastDraw { get; private set; }

    /// <summary>
    /// Outlines every part brush in <paramref name="scene"/>. Render thread,
    /// once per frame, into the same depth-off line pass the gizmos use — the
    /// outline has to be visible through the solid it describes, or it only
    /// tells you about parts you can already see the front of.
    /// </summary>
    public void Draw(DebugDraw output, Scene scene)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(scene);

        DrawnLastDraw = 0;
        SkippedLastDraw = 0;
        if (!Enabled)
            return;

        // The scene's part set, not a graph walk: parts are the population that
        // moves every frame, so an O(world) pass here would put back exactly
        // the cost BrushKind removed.
        foreach (SceneNode node in scene.PartBrushNodes)
        {
            if (node.Brush is not { } brush)
                continue;

            if (DrawnLastDraw >= MaxOutlines)
            {
                SkippedLastDraw++;
                continue;
            }

            DrawnLastDraw++;
            DrawBrushEdges(output, brush, node.WorldMatrix, Color);
        }
    }

    /// <summary>
    /// Draws one brush's face loops under <paramref name="world"/>. Shared
    /// edges are drawn twice — once per adjoining face — which costs a few
    /// duplicate lines and saves building an edge set every frame.
    /// </summary>
    public static void DrawBrushEdges(DebugDraw output, Brush brush, Matrix4x4 world, Vector3 color)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(brush);

        IReadOnlyList<Polygon> faces = brush.LocalFaces;
        for (int f = 0; f < faces.Count; f++)
        {
            ReadOnlySpan<Vector3> verts = faces[f].VertexSpan;
            if (verts.Length < 2)
                continue;

            Vector3 previous = Vector3.Transform(verts[^1], world);
            for (int v = 0; v < verts.Length; v++)
            {
                Vector3 current = Vector3.Transform(verts[v], world);
                output.Line(previous, current, color);
                previous = current;
            }
        }
    }
}
