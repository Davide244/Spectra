using System;
using System.Collections.Generic;
using System.Numerics;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Gizmos;

namespace SpectraEngine.Editing.Viewport;

/// <summary>
/// Outlines what is selected, in the shape of the thing rather than in the
/// shape of a box around it.
/// </summary>
/// <remarks>
/// <para>
/// <b>It used to be a world-space AABB in magenta, drawn from Core.</b> Three
/// things were wrong with that and only one of them is cosmetic. It <i>lied</i>
/// about anything rotated: a wall turned thirty degrees was outlined by a box
/// half again its size, and the outline claimed geometry that was not selected.
/// It was a THIRD attention colour, beside the shell's red and the gizmo's
/// yellow, on a viewport whose colour budget was already spent. And it lived in
/// <c>SpectraEngine.Core</c>, so every shipped game linked and called a
/// selection renderer for a selection it can never have.
/// </para>
/// <para>
/// <b>Orange, at two weights.</b> Hovered is the same hue at forty per cent, not
/// a second colour - which is how Unreal and Blender do it, and what lets the
/// viewport gain an affordance without gaining a hue. The hue itself sits far
/// enough from the gizmo's yellow highlight to be told apart at speed and reads
/// against sky, against a grey baseplate and inside a dark interior.
/// </para>
/// <para>
/// <b>A group gets a screen-constant cross, not a fixed world size.</b> The old
/// marker was 0.15 world units, which is invisible from thirty metres - so a
/// selected group looked exactly like nothing being selected.
/// </para>
/// </remarks>
public sealed class SelectionOutline
{
    /// <summary>Selected, at full weight.</summary>
    public static readonly Vector3 SelectedColor = new(1f, 0.50f, 0.12f);

    /// <summary>Hovered: the same hue, lower value.</summary>
    public static readonly Vector3 HoverColor = new(0.42f, 0.21f, 0.05f);

    /// <summary>Whether the outline draws at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How many nodes may be outlined in full before the pass falls back to
    /// bounds boxes.
    /// </summary>
    /// <remarks>
    /// Disclosed through <see cref="SkippedLastDraw"/>, never silent: an
    /// outline that quietly stopped after sixty-four of a two-hundred-node
    /// selection would read as "those are the ones I picked".
    /// </remarks>
    public int MaxOutlines { get; set; } = 64;

    /// <summary>Nodes the last draw outlined in their own shape.</summary>
    public int DrawnLastDraw { get; private set; }

    /// <summary>Nodes the last draw could only box.</summary>
    public int SkippedLastDraw { get; private set; }

    private const float GroupCrossPixels = 9f;

    /// <summary>
    /// Outlines the scene's selection, and the hovered node beneath it.
    /// </summary>
    /// <param name="hovered">
    /// What the cursor is over, or null. Skipped when it is already selected:
    /// it already carries the full-weight outline, and brightening it would
    /// promise that this press does something different, which it does not.
    /// </param>
    public void Draw(DebugDraw output, Scene scene, Camera camera, Vector2 viewportSize, SceneNode? hovered)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(camera);

        DrawnLastDraw = 0;
        SkippedLastDraw = 0;

        if (!Enabled)
            return;

        IReadOnlyList<SceneNode> selection = scene.Selection.Items;

        if (hovered is not null && !selection.Contains(hovered))
            DrawNode(output, scene, camera, viewportSize, hovered, HoverColor);

        for (int i = 0; i < selection.Count; i++)
        {
            SceneNode node = selection[i];

            if (DrawnLastDraw >= MaxOutlines)
            {
                if (scene.TryGetWorldBounds(node, out Aabb fallback))
                    output.Box(fallback.Min, fallback.Max, SelectedColor);

                SkippedLastDraw++;
                continue;
            }

            DrawNode(output, scene, camera, viewportSize, node, SelectedColor);
            DrawnLastDraw++;
        }

        // One box around the whole selection, at low weight, when there is more
        // than one thing in it. Deliberately the SAME box the Studio handles
        // stand on, so the outline and the handles cannot disagree about what
        // is being manipulated.
        if (selection.Count > 1)
        {
            Vector3 lo = new(float.PositiveInfinity);
            Vector3 hi = new(float.NegativeInfinity);
            bool any = false;

            for (int i = 0; i < selection.Count; i++)
            {
                if (!scene.TryGetWorldBounds(selection[i], out Aabb next))
                    continue;

                lo = Vector3.Min(lo, next.Min);
                hi = Vector3.Max(hi, next.Max);
                any = true;
            }

            if (any)
                output.Box(lo, hi, SelectedColor * 0.4f);
        }
    }

    private static void DrawNode(
        DebugDraw output, Scene scene, Camera camera, Vector2 viewportSize, SceneNode node, Vector3 color)
    {
        // A brush knows its own shape, so it gets it. This is the case the AABB
        // was actively wrong about.
        if (node.Brush is { } brush)
        {
            PartBrushOverlay.DrawBrushEdges(output, brush, node.WorldMatrix, color);
            return;
        }

        // A mesh gets its LOCAL bounds under the world matrix, which is an
        // oriented box rather than an axis-aligned one: the same correction, one
        // level less exact.
        if (node.MeshRenderer?.Mesh is { HasLocalBounds: true } mesh)
        {
            DrawOrientedBox(output, mesh.LocalBounds, node.WorldMatrix, color);
            return;
        }

        // Neither: a group, or a node whose mesh reports no bounds. A cross at
        // its origin, sized in SCREEN space so it is findable from any distance.
        float depth = MathF.Max(GizmoMath.ViewDepth(camera, node.WorldPosition), 0.01f);
        float size = GroupCrossPixels * GizmoMath.WorldPerPixel(camera, viewportSize.Y, depth);
        output.Cross(node.WorldPosition, MathF.Max(size, 0.01f), color);

        // Plus the subtree's extent at low weight, so selecting a group says
        // what is inside it rather than only where its origin is.
        if (scene.TryGetWorldBounds(node, out Aabb bounds))
            output.Box(bounds.Min, bounds.Max, color * 0.4f);
    }

    /// <summary>
    /// Draws an axis-aligned box transformed by <paramref name="world"/>: the
    /// twelve edges of the local box, each end transformed.
    /// </summary>
    /// <remarks>
    /// <c>DebugDraw.Box</c> is axis-aligned by construction and cannot express
    /// this, which is why the old highlight was axis-aligned too.
    /// </remarks>
    public static void DrawOrientedBox(DebugDraw output, Aabb local, Matrix4x4 world, Vector3 color)
    {
        ArgumentNullException.ThrowIfNull(output);

        Span<Vector3> corners = stackalloc Vector3[8];
        Vector3 lo = local.Min;
        Vector3 hi = local.Max;

        for (int i = 0; i < 8; i++)
        {
            var p = new Vector3(
                (i & 1) == 0 ? lo.X : hi.X,
                (i & 2) == 0 ? lo.Y : hi.Y,
                (i & 4) == 0 ? lo.Z : hi.Z);

            corners[i] = Vector3.Transform(p, world);
        }

        // Bit i of the corner index is axis i, so two corners are joined by an
        // edge exactly when their indices differ in one bit. Twelve pairs.
        for (int i = 0; i < 8; i++)
        {
            for (int bit = 1; bit <= 4; bit <<= 1)
            {
                if ((i & bit) == 0)
                    output.Line(corners[i], corners[i | bit], color);
            }
        }
    }
}
