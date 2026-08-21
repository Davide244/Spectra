using System;
using System.Collections.Generic;
using System.Numerics;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;

namespace SpectraEngine.Core.Scene;

/// <summary>Bitfield of debug visualisations that can be enabled simultaneously.</summary>
[Flags]
public enum DebugVisualization
{
    None = 0,

    /// <summary>Draw every static-world triangle and every scene-graph mesh triangle as line edges.</summary>
    Wireframe = 1 << 0,

    /// <summary>Small cross at every CSG polygon vertex — surfaces T-junction welds and snap effects.</summary>
    Vertices = 1 << 1,

    /// <summary>Wireframe boxes around brush world AABBs and scene-graph mesh bounds.</summary>
    Aabbs = 1 << 2,

    /// <summary>Short arrow along the outward normal of every static-world face and every scene-graph mesh triangle.</summary>
    Normals = 1 << 3,

    /// <summary>RGB axes gizmo at each scene node + lines connecting children to parents.</summary>
    SceneGraph = 1 << 4,
}

/// <summary>
/// Pushes debug primitives describing a scene's static world into a
/// <see cref="DebugDraw"/> for the renderer to flush. Each visualisation walks
/// the same <see cref="CsgWorld"/> data the engine renders normally — no
/// auxiliary structures.
/// </summary>
public static class DebugVisualizations
{
    private static readonly Vector3 WireframeColor = new(0.1f, 1f, 0.2f);
    private static readonly Vector3 VertexColor = new(1f, 1f, 0.2f);
    private static readonly Vector3 AabbColor = new(1f, 0.5f, 0.1f);
    private static readonly Vector3 NormalColor = new(0.6f, 0.8f, 1f);
    private static readonly Vector3 AxisXColor = new(1f, 0.25f, 0.25f);
    private static readonly Vector3 AxisYColor = new(0.25f, 1f, 0.25f);
    private static readonly Vector3 AxisZColor = new(0.35f, 0.55f, 1f);
    private static readonly Vector3 HierarchyColor = new(0.6f, 0.6f, 0.8f);
    // Magenta: unused by any opt-in visualisation, so a selected node stays
    // recognisable even with every debug flag enabled at once.
    private static readonly Vector3 SelectionColor = new(1f, 0.3f, 0.9f);
    private const float VertexCrossSize = 0.04f;
    private const float NormalArrowLength = 0.25f;
    private const float NodeAxisLength = 0.3f;
    private const float SelectionMarkerSize = 0.15f;

    /// <summary>Pushes every visualisation enabled in <paramref name="flags"/> into <paramref name="output"/>.</summary>
    public static void Draw(DebugDraw output, Scene scene, DebugVisualization flags)
    {
        if (flags == DebugVisualization.None)
            return;

        if (scene.StaticWorld is { } world)
        {
            if (flags.HasFlag(DebugVisualization.Wireframe))
                DrawCsgWireframe(output, world);
            if (flags.HasFlag(DebugVisualization.Vertices))
                DrawVertices(output, world);
            if (flags.HasFlag(DebugVisualization.Aabbs))
                DrawBrushAabbs(output, world);
            if (flags.HasFlag(DebugVisualization.Normals))
                DrawCsgNormals(output, world);
        }

        if (flags.HasFlag(DebugVisualization.Wireframe))
            DrawMeshWireframes(output, scene);
        if (flags.HasFlag(DebugVisualization.Aabbs))
            DrawMeshAabbs(output, scene);
        if (flags.HasFlag(DebugVisualization.Normals))
            DrawMeshNormals(output, scene);
        if (flags.HasFlag(DebugVisualization.SceneGraph))
            DrawSceneGraph(output, scene);
    }

    /// <summary>
    /// Outlines every selected node's world AABB. Deliberately NOT part of
    /// <see cref="Draw"/>'s flag-gated visualisations: selection feedback is
    /// editor UI, so the engine calls this every frame regardless of debug
    /// flags. Bounds come from the scene's spatial index — the same boxes
    /// culling and raycasts use (brush world bounds for brush nodes, mesh
    /// bounds for mesh nodes, their union for both) — rather than being
    /// recomputed here. A selected non-spatial node (a pure group) has no
    /// bounds and gets a small cross at its origin instead. With nothing
    /// selected this is a zero-iteration loop — effectively free.
    /// </summary>
    public static void DrawSelectionHighlight(DebugDraw output, Scene scene)
    {
        IReadOnlyList<SceneNode> selection = scene.Selection.Items;
        for (int i = 0; i < selection.Count; i++)
        {
            SceneNode node = selection[i];
            if (scene.Bvh.TryGetWorldBounds(node, out Aabb bounds))
                output.Box(bounds.Min, bounds.Max, SelectionColor);
            else
                output.Cross(node.WorldPosition, SelectionMarkerSize, SelectionColor);
        }
    }

    private static void DrawCsgWireframe(DebugDraw output, CsgWorld world)
    {
        // Triangulate each polygon (fan from vertex 0) and emit every edge,
        // so what you see is exactly what the rasteriser sees.
        foreach (Polygon poly in world.Surfaces)
        {
            foreach ((Vector3 a, Vector3 b, Vector3 c) in poly.Triangulate())
            {
                output.Line(a, b, WireframeColor);
                output.Line(b, c, WireframeColor);
                output.Line(c, a, WireframeColor);
            }
        }
    }

    private static void DrawMeshWireframes(DebugDraw output, Scene scene)
    {
        foreach (SceneNode node in scene.Nodes)
        {
            if (node.MeshRenderer is not { } mr) continue;
            DrawTrianglesAsLines(output, mr.Mesh, node.WorldMatrix, WireframeColor);
        }
    }

    private static void DrawTrianglesAsLines(DebugDraw output, Mesh mesh, Matrix4x4 worldMatrix, Vector3 color)
    {
        IReadOnlyList<Vector3> positions = mesh.Positions;
        IReadOnlyList<uint> indices = mesh.Indices;
        if (positions.Count == 0 || indices.Count < 3) return;

        for (int i = 0; i + 2 < indices.Count; i += 3)
        {
            Vector3 a = Vector3.Transform(positions[(int)indices[i]], worldMatrix);
            Vector3 b = Vector3.Transform(positions[(int)indices[i + 1]], worldMatrix);
            Vector3 c = Vector3.Transform(positions[(int)indices[i + 2]], worldMatrix);
            output.Line(a, b, color);
            output.Line(b, c, color);
            output.Line(c, a, color);
        }
    }

    private static void DrawVertices(DebugDraw output, CsgWorld world)
    {
        foreach (Polygon poly in world.Surfaces)
        {
            var verts = poly.Vertices;
            for (int i = 0; i < verts.Count; i++)
                output.Cross(verts[i], VertexCrossSize, VertexColor);
        }
    }

    private static void DrawBrushAabbs(DebugDraw output, CsgWorld world)
    {
        // Placements, not Brush.WorldBounds: the compiled world was carved at
        // the snapshot transforms, which a live (still-moving) brush node's
        // own Transform no longer reflects.
        foreach (BrushPlacement placement in world.Placements)
        {
            Aabb b = placement.WorldBounds;
            output.Box(b.Min, b.Max, AabbColor);
        }
    }

    private static void DrawMeshAabbs(DebugDraw output, Scene scene)
    {
        foreach (SceneNode node in scene.Nodes)
        {
            if (node.MeshRenderer is not { } mr) continue;
            Aabb worldBounds = mr.Mesh.LocalBounds.Transform(node.WorldMatrix);
            output.Box(worldBounds.Min, worldBounds.Max, AabbColor);
        }
    }

    private static void DrawCsgNormals(DebugDraw output, CsgWorld world)
    {
        foreach (Polygon poly in world.Surfaces)
        {
            Vector3 center = Vector3.Zero;
            var verts = poly.Vertices;
            for (int i = 0; i < verts.Count; i++)
                center += verts[i];
            center /= verts.Count;

            output.Arrow(center, center + poly.Surface.Normal * NormalArrowLength, NormalColor);
        }
    }

    private static void DrawMeshNormals(DebugDraw output, Scene scene)
    {
        foreach (SceneNode node in scene.Nodes)
        {
            if (node.MeshRenderer is not { } mr) continue;

            IReadOnlyList<Vector3> positions = mr.Mesh.Positions;
            IReadOnlyList<uint> indices = mr.Mesh.Indices;
            if (positions.Count == 0 || indices.Count < 3) continue;
            Matrix4x4 world = node.WorldMatrix;

            for (int i = 0; i + 2 < indices.Count; i += 3)
            {
                Vector3 a = Vector3.Transform(positions[(int)indices[i]], world);
                Vector3 b = Vector3.Transform(positions[(int)indices[i + 1]], world);
                Vector3 c = Vector3.Transform(positions[(int)indices[i + 2]], world);

                Vector3 centroid = (a + b + c) / 3f;
                Vector3 edge0 = b - a;
                Vector3 edge1 = c - a;
                Vector3 normal = Vector3.Cross(edge0, edge1);
                if (normal.LengthSquared() < 1e-12f) continue;
                normal = Vector3.Normalize(normal);

                output.Arrow(centroid, centroid + normal * NormalArrowLength, NormalColor);
            }
        }
    }

    private static void DrawSceneGraph(DebugDraw output, Scene scene)
    {
        foreach (SceneNode node in scene.Nodes)
        {
            Matrix4x4 world = node.WorldMatrix;
            Vector3 origin = node.WorldPosition;

            // Basis vectors transformed as directions (ignoring translation).
            Vector3 right = Vector3.TransformNormal(Vector3.UnitX, world) * NodeAxisLength;
            Vector3 up = Vector3.TransformNormal(Vector3.UnitY, world) * NodeAxisLength;
            Vector3 forward = Vector3.TransformNormal(Vector3.UnitZ, world) * NodeAxisLength;

            output.Line(origin, origin + right, AxisXColor);
            output.Line(origin, origin + up, AxisYColor);
            output.Line(origin, origin + forward, AxisZColor);

            // Parent → child line: makes the hierarchy visible at a glance.
            if (node.Parent is not null)
                output.Line(node.Parent.WorldPosition, origin, HierarchyColor);
        }
    }
}
