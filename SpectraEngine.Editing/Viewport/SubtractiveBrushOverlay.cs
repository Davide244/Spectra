using System;
using System.Collections.Generic;
using System.Numerics;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Editing.Viewport;

/// <summary>
/// Draws the edges of every subtractive brush, with an inward tick per face
/// showing which way the solid is being removed.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not an affordance, it is the only way the brush can be seen at
/// all.</b> A subtractive brush emits no outward skin by construction — a
/// <see cref="BrushKind.World"/> one contributes cavity walls to the brushes it
/// cuts and nothing of its own; a <see cref="BrushKind.Part"/> one contributes
/// nothing whatsoever. Without this pass a negative brush is an invisible,
/// unpickable object that is nonetheless deleting the level, and the author's
/// only evidence of it is the hole it leaves.
/// </para>
/// <para>
/// <b>Kind-blind, deliberately.</b> Both kinds of subtractive brush render
/// nothing, so both need drawing. Filtering the part-brush set instead would
/// silence the outline on exactly the population that renders nothing.
/// </para>
/// <para>
/// <b>The inward tick earns its cost.</b> An outline alone says "a box is
/// here"; it does not say which side of each face is being taken away, and for
/// a brush whose whole purpose is removal that is the one thing the author
/// needs. The tick is drawn from each face's centroid along the face's inward
/// normal — into the removed volume — so a negative reads as a box pointing at
/// itself.
/// </para>
/// </remarks>
public sealed class SubtractiveBrushOverlay
{
    /// <summary>The outline colour: magenta, distinct from the part-brush cyan and every gizmo axis.</summary>
    public static readonly Vector3 DefaultColor = new(0.95f, 0.25f, 0.75f);

    /// <summary>Whether the overlay draws at all. On in an editor, off in a game.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Outline colour.</summary>
    public Vector3 Color { get; set; } = DefaultColor;

    /// <summary>Inward tick length, as a fraction of the brush's bounding radius.</summary>
    public float TickFraction { get; set; } = 0.18f;

    /// <summary>How many subtractive brushes may be outlined in one frame.</summary>
    public int MaxOutlines { get; set; } = 256;

    /// <summary>Subtractive brushes the last <see cref="Draw"/> outlined.</summary>
    public int DrawnLastDraw { get; private set; }

    /// <summary>
    /// Subtractive brushes the last <see cref="Draw"/> skipped because
    /// <see cref="MaxOutlines"/> was reached. Non-zero means some of the
    /// invisible geometry in this scene is currently drawn nowhere at all.
    /// </summary>
    public int SkippedLastDraw { get; private set; }

    /// <summary>
    /// Outlines every subtractive brush in <paramref name="scene"/>. Render
    /// thread, once per frame, into the depth-off line pass.
    /// </summary>
    public void Draw(DebugDraw output, Scene scene)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(scene);

        DrawnLastDraw = 0;
        SkippedLastDraw = 0;
        if (!Enabled)
            return;

        foreach (SceneNode node in scene.SubtractiveBrushNodes)
        {
            if (node.Brush is not { } brush)
                continue;

            if (DrawnLastDraw >= MaxOutlines)
            {
                SkippedLastDraw++;
                continue;
            }

            DrawnLastDraw++;
            Matrix4x4 world = node.WorldMatrix;
            PartBrushOverlay.DrawBrushEdges(output, brush, world, Color);
            DrawInwardTicks(output, brush, world, Color, TickFraction);
        }
    }

    // One tick per face, from the face centroid along the INWARD normal — the
    // direction the solid is being removed towards.
    private static void DrawInwardTicks(
        DebugDraw output, Brush brush, Matrix4x4 world, Vector3 color, float tickFraction)
    {
        Aabb bounds = brush.LocalBounds;
        float radius = (bounds.Max - bounds.Min).Length() * 0.5f;
        float tick = radius * tickFraction;
        if (tick <= 0f)
            return;

        IReadOnlyList<Polygon> faces = brush.LocalFaces;
        for (int f = 0; f < faces.Count; f++)
        {
            ReadOnlySpan<Vector3> verts = faces[f].VertexSpan;
            if (verts.Length == 0)
                continue;

            var centroid = Vector3.Zero;
            for (int v = 0; v < verts.Length; v++)
                centroid += verts[v];
            centroid /= verts.Length;

            // Face normals point outward, so the removed volume is behind them.
            Vector3 inward = -faces[f].Surface.Normal * tick;

            output.Line(
                Vector3.Transform(centroid, world),
                Vector3.Transform(centroid + inward, world),
                color);
        }
    }
}
