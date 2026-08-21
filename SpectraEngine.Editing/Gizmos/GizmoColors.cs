using SpectraEngine.Core.Graphics;
using System;
using System.Numerics;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// The one palette all three manipulators draw in, and the primitives they all
/// draw with.
/// </summary>
/// <remarks>
/// <b>An axis must mean the same colour in every tool</b>, and the highlight
/// must mean "this is the handle you are about to grab" wherever it appears —
/// which it cannot if each gizmo carries its own constants. The colours match
/// <c>DebugVisualizations</c>' scene-graph axes, so an axis reads the same in the
/// viewport overlay as it does under the cursor.
/// <para>
/// <b>Threading:</b> render thread only, like the <see cref="DebugDraw"/> these
/// fill. Drawing allocates nothing beyond the line buffer's own amortised
/// growth.
/// </para>
/// </remarks>
public static class GizmoColors
{
    private static readonly Vector3 AxisXColor = new(1f, 0.25f, 0.25f);
    private static readonly Vector3 AxisYColor = new(0.25f, 1f, 0.25f);
    private static readonly Vector3 AxisZColor = new(0.35f, 0.55f, 1f);
    private static readonly Vector3 ScreenColor = new(0.85f, 0.85f, 0.9f);

    // Warm yellow: unused by any other viewport visualisation, so "this is the
    // handle you are about to grab" never reads as some other overlay.
    private static readonly Vector3 HighlightColor = new(1f, 0.9f, 0.2f);

    /// <summary>
    /// The colour a handle is drawn in — its axis colour, or the highlight when
    /// it is the hovered/active one.
    /// </summary>
    public static Vector3 For(GizmoHandle handle, GizmoHandle highlighted)
    {
        if (handle == highlighted && handle != GizmoHandle.None)
            return HighlightColor;

        return handle switch
        {
            // A plane quad takes the colour of the axis it is normal to, which
            // is how a viewer reads "this square slides in the other two".
            GizmoHandle.AxisX or GizmoHandle.PlaneYZ => AxisXColor,
            GizmoHandle.AxisY or GizmoHandle.PlaneZX => AxisYColor,
            GizmoHandle.AxisZ or GizmoHandle.PlaneXY => AxisZColor,
            _ => ScreenColor,
        };
    }

    /// <summary>The colour any highlighted handle is drawn in.</summary>
    public static Vector3 Highlight => HighlightColor;

    /// <summary>
    /// Draws a circle of <paramref name="radius"/> around
    /// <paramref name="centre"/> in the plane perpendicular to
    /// <paramref name="axis"/>, as
    /// <see cref="GizmoHitTesting.RingSegments"/> line segments in the same basis
    /// the hit tester walks — so what is drawn is exactly what is pickable.
    /// </summary>
    public static void DrawCircle(DebugDraw output, Vector3 centre, Vector3 axis, float radius, Vector3 color)
    {
        ArgumentNullException.ThrowIfNull(output);
        GizmoHitTesting.BuildRingBasis(axis, out Vector3 u, out Vector3 v);
        DrawCircle(output, centre, u, v, radius, color);
    }

    /// <summary>
    /// Draws a circle in the plane spanned by the two unit vectors
    /// <paramref name="first"/> and <paramref name="second"/> — the overload for
    /// callers that already have a basis (the screen-facing handles, built in the
    /// camera basis).
    /// </summary>
    public static void DrawCircle(
        DebugDraw output, Vector3 centre, Vector3 first, Vector3 second, float radius, Vector3 color)
    {
        ArgumentNullException.ThrowIfNull(output);

        Vector3 previous = centre + first * radius;
        for (int i = 1; i <= GizmoHitTesting.RingSegments; i++)
        {
            float angle = MathF.Tau * i / GizmoHitTesting.RingSegments;
            Vector3 point = centre + (first * MathF.Cos(angle) + second * MathF.Sin(angle)) * radius;
            output.Line(previous, point, color);
            previous = point;
        }
    }

    /// <summary>
    /// Draws the twelve edges of a cube of half-extent <paramref name="radius"/>
    /// centred at <paramref name="centre"/>, oriented by the gizmo frame — the
    /// scale gizmo's handle, and exactly the box its pick tests against.
    /// </summary>
    public static void DrawBox(
        DebugDraw output, in GizmoGeometry geometry, Vector3 centre, float radius, Vector3 color)
    {
        ArgumentNullException.ThrowIfNull(output);

        Vector3 x = geometry.AxisX * radius;
        Vector3 y = geometry.AxisY * radius;
        Vector3 z = geometry.AxisZ * radius;

        // The four corners of the −z face, then the +z face, then the struts.
        Vector3 a = centre - x - y - z;
        Vector3 b = centre + x - y - z;
        Vector3 c = centre + x + y - z;
        Vector3 d = centre - x + y - z;
        Vector3 e = centre - x - y + z;
        Vector3 f = centre + x - y + z;
        Vector3 g = centre + x + y + z;
        Vector3 h = centre - x + y + z;

        output.Line(a, b, color);
        output.Line(b, c, color);
        output.Line(c, d, color);
        output.Line(d, a, color);

        output.Line(e, f, color);
        output.Line(f, g, color);
        output.Line(g, h, color);
        output.Line(h, e, color);

        output.Line(a, e, color);
        output.Line(b, f, color);
        output.Line(c, g, color);
        output.Line(d, h, color);
    }
}
