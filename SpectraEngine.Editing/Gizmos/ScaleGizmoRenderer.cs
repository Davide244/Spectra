using SpectraEngine.Core.Graphics;
using System;
using System.Numerics;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// Draws the scale gizmo into a <see cref="DebugDraw"/>: a cube-capped shaft per
/// axis handle the style offers (three in the classic layout, six per-face in
/// Studio's), plus a cube at the centre for a uniform resize.
/// </summary>
/// <remarks>
/// <b>Cubes, not arrowheads</b> — the shape difference is how a user tells at a
/// glance that this gizmo resizes rather than moves, and it is the shape both
/// Hammer's and Roblox Studio's resize handles use. Everything comes out of
/// <see cref="GizmoGeometry"/>, so the drawn cubes are exactly the boxes
/// <see cref="ScaleGizmoHitTester"/> tests against.
/// <para>
/// <b>Threading:</b> render thread only, like the <see cref="DebugDraw"/> it
/// fills.
/// </para>
/// </remarks>
public static class ScaleGizmoRenderer
{
    /// <summary>
    /// Pushes the whole gizmo into <paramref name="output"/>.
    /// </summary>
    /// <param name="output">The frame's debug line accumulator.</param>
    /// <param name="geometry">This frame's gizmo geometry.</param>
    /// <param name="highlighted">
    /// The handle to draw highlighted — the hovered one while idle, the active
    /// one during a drag, or <see cref="GizmoHandle.None"/> for neither.
    /// </param>
    public static void Draw(DebugDraw output, in GizmoGeometry geometry, GizmoHandle highlighted)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (geometry.IsBehindCamera || geometry.AxisLength <= 0f)
            return;

        for (GizmoHandle handle = GizmoHandle.AxisX; handle <= geometry.LastAxisHandle; handle++)
        {
            Vector3 color = GizmoColors.For(handle, highlighted);

            if (geometry.TryGetAxisSegment(handle, out Vector3 start, out Vector3 tip))
                output.Line(start, tip, color);

            if (geometry.TryGetHandleBox(handle, out Vector3 centre, out float radius))
                GizmoColors.DrawBox(output, in geometry, centre, radius, color);
        }

        if (geometry.TryGetHandleBox(GizmoHandle.Screen, out Vector3 uniform, out float uniformRadius))
        {
            GizmoColors.DrawBox(
                output, in geometry, uniform, uniformRadius, GizmoColors.For(GizmoHandle.Screen, highlighted));
        }
    }
}
