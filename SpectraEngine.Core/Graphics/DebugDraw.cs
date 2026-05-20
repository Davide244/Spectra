using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// Per-frame line-primitive accumulator for debug visualisations. Subsystems
/// push lines, boxes, crosses, arrows, and polylines; the renderer uploads the
/// resulting interleaved vertex stream as <c>GL_LINES</c> at the end of the
/// frame. Cleared each frame by the engine.
/// </summary>
public sealed class DebugDraw
{
    // Interleaved: pos.xyz, color.rgb — 6 floats per vertex.
    private const int FloatsPerVertex = 6;

    private readonly List<float> _data = [];

    public int VertexCount => _data.Count / FloatsPerVertex;

    /// <summary>Interleaved vertex stream (position + colour) ready for GPU upload.</summary>
    public ReadOnlySpan<float> Vertices => CollectionsMarshal.AsSpan(_data);

    public void Clear() => _data.Clear();

    public void Line(Vector3 a, Vector3 b, Vector3 color)
    {
        AddVertex(a, color);
        AddVertex(b, color);
    }

    /// <summary>12-line wireframe of an axis-aligned box.</summary>
    public void Box(Vector3 min, Vector3 max, Vector3 color)
    {
        Vector3 a = new(min.X, min.Y, min.Z);
        Vector3 b = new(max.X, min.Y, min.Z);
        Vector3 c = new(min.X, max.Y, min.Z);
        Vector3 d = new(max.X, max.Y, min.Z);
        Vector3 e = new(min.X, min.Y, max.Z);
        Vector3 f = new(max.X, min.Y, max.Z);
        Vector3 g = new(min.X, max.Y, max.Z);
        Vector3 h = new(max.X, max.Y, max.Z);

        Line(a, b, color); Line(b, d, color); Line(d, c, color); Line(c, a, color);
        Line(e, f, color); Line(f, h, color); Line(h, g, color); Line(g, e, color);
        Line(a, e, color); Line(b, f, color); Line(c, g, color); Line(d, h, color);
    }

    /// <summary>Three-axis cross centred at <paramref name="p"/>.</summary>
    public void Cross(Vector3 p, float size, Vector3 color)
    {
        Line(p - new Vector3(size, 0f, 0f), p + new Vector3(size, 0f, 0f), color);
        Line(p - new Vector3(0f, size, 0f), p + new Vector3(0f, size, 0f), color);
        Line(p - new Vector3(0f, 0f, size), p + new Vector3(0f, 0f, size), color);
    }

    /// <summary>Line from <paramref name="origin"/> to <paramref name="tip"/> with a small arrowhead at the tip.</summary>
    public void Arrow(Vector3 origin, Vector3 tip, Vector3 color)
    {
        Line(origin, tip, color);

        Vector3 dir = tip - origin;
        float length = dir.Length();
        if (length < 1e-6f) return;

        Vector3 normalized = dir / length;
        Vector3 reference = MathF.Abs(normalized.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 side = Vector3.Normalize(Vector3.Cross(normalized, reference)) * length * 0.2f;
        Vector3 back = tip - normalized * length * 0.2f;

        Line(tip, back + side, color);
        Line(tip, back - side, color);
    }

    /// <summary>Connects consecutive points; closes the loop if <paramref name="closed"/>.</summary>
    public void Polyline(IReadOnlyList<Vector3> points, bool closed, Vector3 color)
    {
        if (points.Count < 2) return;
        for (int i = 0; i < points.Count - 1; i++)
            Line(points[i], points[i + 1], color);
        if (closed)
            Line(points[points.Count - 1], points[0], color);
    }

    private void AddVertex(Vector3 p, Vector3 c)
    {
        _data.Add(p.X); _data.Add(p.Y); _data.Add(p.Z);
        _data.Add(c.X); _data.Add(c.Y); _data.Add(c.Z);
    }
}
