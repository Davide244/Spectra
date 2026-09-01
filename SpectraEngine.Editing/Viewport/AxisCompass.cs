using System;
using System.Numerics;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Gizmos;

namespace SpectraEngine.Editing.Viewport;

/// <summary>
/// The three world axes, drawn at a fixed corner of the viewport at a fixed
/// screen size, with a line-drawn letter on each.
/// </summary>
/// <remarks>
/// <para>
/// <b>It belongs in the OVERLAY lane, never the world lane.</b> The grid is
/// world content and must be occluded; a compass is chrome and nothing may
/// occlude it, or the one thing that says which way you are facing disappears
/// exactly when you fly inside something and get lost.
/// </para>
/// <para>
/// <b>Placed in VIEW space and sized through the same world-per-pixel relation
/// the gizmo handles use.</b> A fixed offset from the camera along its own
/// basis holds a constant corner position; scaling by
/// <see cref="GizmoMath.WorldPerPixel"/> at that offset holds a constant screen
/// size. Both are needed: either alone gives a compass that either drifts or
/// grows.
/// </para>
/// <para>
/// <b>The letters are eight line segments, not text.</b> Nothing in the engine
/// can draw a glyph into the viewport - that waits on the composited surface -
/// and three axis stubs with no labels are three coloured sticks whose identity
/// you have to already know. X is two crossed strokes, Y is three, Z is three;
/// eight lines buys the compass its entire readability.
/// </para>
/// </remarks>
public sealed class AxisCompass
{
    /// <summary>Whether the compass draws at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The compass's arm length, in screen pixels.</summary>
    public float SizePixels { get; set; } = 30f;

    /// <summary>Distance from the viewport's bottom-right corner, in pixels.</summary>
    public float MarginPixels { get; set; } = 52f;

    // DISPLAY colours, like every other overlay value, because this lane skips
    // the tone curve. The same three hues the gizmo handles and the inspector's
    // axis letters wear.
    private static readonly Vector3 XColor = new(1f, 0.35f, 0.33f);
    private static readonly Vector3 YColor = new(0.42f, 0.85f, 0.36f);
    private static readonly Vector3 ZColor = new(0.38f, 0.58f, 1f);

    // The negative stub is short and unlabelled: without it +X and -X are the
    // same red stick and the compass says nothing about which way you are
    // looking down an axis.
    private const float NegativeFraction = 0.38f;

    /// <summary>
    /// Emits the compass into <paramref name="output"/> for
    /// <paramref name="camera"/>.
    /// </summary>
    /// <param name="output">The DEPTH-OFF overlay buffer.</param>
    /// <param name="viewportSize">The viewport in pixels.</param>
    public void Draw(DebugDraw output, Camera camera, Vector2 viewportSize)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(camera);

        if (!Enabled || viewportSize.X <= 0f || viewportSize.Y <= 0f)
            return;

        // Far enough in front to clear the near plane by a wide margin, and near
        // enough that floating-point precision on the axis stubs is irrelevant.
        const float Depth = 4f;

        float worldPerPixel = GizmoMath.WorldPerPixel(camera, viewportSize.Y, Depth);
        if (worldPerPixel <= 0f)
            return;

        float arm = SizePixels * worldPerPixel;

        // Half the viewport minus the margin and the compass's own reach, so the
        // widget sits fully inside the corner rather than half off it.
        float inset = MarginPixels + SizePixels;
        float right = ((viewportSize.X * 0.5f) - inset) * worldPerPixel;
        float down = ((viewportSize.Y * 0.5f) - inset) * worldPerPixel;

        Vector3 origin = camera.Position
            + (camera.Forward * Depth)
            + (camera.Right * right)
            - (camera.Up * down);

        DrawAxis(output, origin, Vector3.UnitX, arm, XColor);
        DrawAxis(output, origin, Vector3.UnitY, arm, YColor);
        DrawAxis(output, origin, Vector3.UnitZ, arm, ZColor);

        // The letters ride at the arm's tip, billboarded onto the camera's own
        // right/up basis so they read the same whatever the axis is doing.
        float glyph = arm * 0.30f;
        DrawX(output, origin + (Vector3.UnitX * (arm + (glyph * 1.6f))), camera, glyph, XColor);
        DrawY(output, origin + (Vector3.UnitY * (arm + (glyph * 1.6f))), camera, glyph, YColor);
        DrawZ(output, origin + (Vector3.UnitZ * (arm + (glyph * 1.6f))), camera, glyph, ZColor);
    }

    private static void DrawAxis(DebugDraw output, Vector3 origin, Vector3 axis, float arm, Vector3 color)
    {
        output.Line(origin, origin + (axis * arm), color);
        output.Line(origin, origin - (axis * arm * NegativeFraction), color * 0.35f);
    }

    // Two strokes.
    private static void DrawX(DebugDraw output, Vector3 at, Camera camera, float s, Vector3 color)
    {
        Vector3 r = camera.Right * s * 0.55f;
        Vector3 u = camera.Up * s;
        output.Line(at - r - u, at + r + u, color);
        output.Line(at - r + u, at + r - u, color);
    }

    // Three: two arms down to a stem.
    private static void DrawY(DebugDraw output, Vector3 at, Camera camera, float s, Vector3 color)
    {
        Vector3 r = camera.Right * s * 0.55f;
        Vector3 u = camera.Up * s;
        output.Line(at - r + u, at, color);
        output.Line(at + r + u, at, color);
        output.Line(at, at - u, color);
    }

    // Three: top, diagonal, bottom.
    private static void DrawZ(DebugDraw output, Vector3 at, Camera camera, float s, Vector3 color)
    {
        Vector3 r = camera.Right * s * 0.55f;
        Vector3 u = camera.Up * s;
        output.Line(at - r + u, at + r + u, color);
        output.Line(at + r + u, at - r - u, color);
        output.Line(at - r - u, at + r - u, color);
    }
}
