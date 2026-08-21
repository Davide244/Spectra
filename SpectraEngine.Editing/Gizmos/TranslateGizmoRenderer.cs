using SpectraEngine.Core.Graphics;
using System;
using System.Numerics;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// Draws the translate gizmo into a <see cref="DebugDraw"/>: three axis arrows,
/// three plane quads, and a screen-facing centre disc, with the hovered or
/// active handle highlighted.
/// </summary>
/// <remarks>
/// <b>The debug line path is the right pipeline for a manipulator, not a
/// shortcut.</b> It already renders depth-off — always on top — on all three
/// backends, which is exactly the property a gizmo needs: a handle you cannot
/// see is a handle you cannot grab, and a translate gizmo lives by definition
/// inside the object it moves. Reusing it also means the gizmo costs one shared
/// line batch per frame instead of its own pipeline on every backend.
/// <para>
/// Everything drawn here comes out of <see cref="GizmoGeometry"/> — the same
/// struct <see cref="TranslateGizmoHitTester"/> picks against — so the drawn
/// shape and the pickable shape cannot drift apart. Colours come from
/// <see cref="GizmoColors"/>, shared with the other two tools.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only, like the <see cref="DebugDraw"/> it
/// fills. Drawing allocates nothing beyond the line buffer's own amortised
/// growth.
/// </para>
/// </remarks>
public static class TranslateGizmoRenderer
{
    // Arrowhead proportions, in units of the axis length.
    private const float HeadLengthFactor = 0.22f;
    private const float HeadRadiusFactor = 0.07f;

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

        // Behind the camera the projection is mirrored and meaningless; the hit
        // tester refuses to pick there too, so drawing would offer a handle
        // that cannot be grabbed.
        if (geometry.IsBehindCamera || geometry.AxisLength <= 0f)
            return;

        for (GizmoHandle handle = GizmoHandle.AxisX; handle <= GizmoHandle.AxisZ; handle++)
            DrawAxis(output, in geometry, handle, GizmoColors.For(handle, highlighted));

        for (GizmoHandle handle = GizmoHandle.PlaneYZ; handle <= GizmoHandle.PlaneXY; handle++)
            DrawPlane(output, in geometry, handle, GizmoColors.For(handle, highlighted));

        // Built in the camera basis, so the disc faces the viewer exactly as the
        // ray-vs-disc pick assumes.
        GizmoColors.DrawCircle(
            output,
            geometry.Pivot,
            geometry.ViewRight,
            geometry.ViewUp,
            geometry.ScreenRadius,
            GizmoColors.For(GizmoHandle.Screen, highlighted));
    }

    private static void DrawAxis(
        DebugDraw output, in GizmoGeometry geometry, GizmoHandle handle, Vector3 color)
    {
        if (!geometry.TryGetAxisSegment(handle, out Vector3 start, out Vector3 tip))
            return;

        output.Line(start, tip, color);

        // A four-bladed head rather than DebugDraw.Arrow's two: the two-line
        // head disappears edge-on, and a manipulator is looked at from every
        // angle by definition.
        Vector3 direction = geometry.Axis(handle);
        geometry.AxisPerpendiculars(handle, out Vector3 first, out Vector3 second);

        Vector3 baseCentre = tip - direction * (geometry.AxisLength * HeadLengthFactor);
        float radius = geometry.AxisLength * HeadRadiusFactor;
        Vector3 offsetA = first * radius;
        Vector3 offsetB = second * radius;

        Vector3 cornerA = baseCentre + offsetA;
        Vector3 cornerB = baseCentre + offsetB;
        Vector3 cornerC = baseCentre - offsetA;
        Vector3 cornerD = baseCentre - offsetB;

        output.Line(tip, cornerA, color);
        output.Line(tip, cornerB, color);
        output.Line(tip, cornerC, color);
        output.Line(tip, cornerD, color);

        // Close the head's base so it reads as a solid cone from the side.
        output.Line(cornerA, cornerB, color);
        output.Line(cornerB, cornerC, color);
        output.Line(cornerC, cornerD, color);
        output.Line(cornerD, cornerA, color);
    }

    private static void DrawPlane(
        DebugDraw output, in GizmoGeometry geometry, GizmoHandle handle, Vector3 color)
    {
        if (!geometry.TryGetPlaneQuad(handle, out Vector3 corner, out Vector3 first, out Vector3 second, out float size))
            return;

        Vector3 u = first * size;
        Vector3 v = second * size;

        output.Line(corner, corner + u, color);
        output.Line(corner + u, corner + u + v, color);
        output.Line(corner + u + v, corner + v, color);
        output.Line(corner + v, corner, color);
    }
}
