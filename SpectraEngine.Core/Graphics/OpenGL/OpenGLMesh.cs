using Silk.NET.OpenGL;
using SpectraEngine.Core.Bsp;
using System;
using System.Numerics;

namespace SpectraEngine.Core.Graphics.OpenGL;

internal sealed class OpenGLMesh : Mesh
{
    private readonly GL _gl;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;
    private bool _disposed;

    private OpenGLMesh(GL gl, uint vao, uint vbo, uint ebo, uint indexCount,
        Vector3[] positions, Vector3[] normals, uint[] indices, Aabb localBounds)
    {
        _gl = gl;
        _vao = vao;
        _vbo = vbo;
        _ebo = ebo;
        IndexCount = indexCount;
        Positions = positions;
        Normals = normals;
        Indices = indices;
        LocalBounds = localBounds;
    }

    internal static unsafe OpenGLMesh Create(GL gl, ReadOnlySpan<float> vertices, ReadOnlySpan<uint> indices, ReadOnlySpan<VertexAttribute> attributes)
    {
        uint vao = gl.GenVertexArray();
        uint vbo = gl.GenBuffer();
        uint ebo = gl.GenBuffer();

        gl.BindVertexArray(vao);

        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (float* v = vertices)
        {
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), v, BufferUsageARB.StaticDraw);
        }

        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        fixed (uint* i = indices)
        {
            gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), i, BufferUsageARB.StaticDraw);
        }

        uint stride = 0;
        for (int i = 0; i < attributes.Length; i++)
            stride += attributes[i].ComponentCount * sizeof(float);

        uint offset = 0;
        for (int i = 0; i < attributes.Length; i++)
        {
            var attr = attributes[i];
            gl.VertexAttribPointer(attr.Location, (int)attr.ComponentCount, VertexAttribPointerType.Float, false, stride, (void*)offset);
            gl.EnableVertexAttribArray(attr.Location);
            offset += attr.ComponentCount * sizeof(float);
        }

        gl.BindVertexArray(0);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, 0);

        // Keep CPU-side geometry alongside the GPU buffers for bounds and debug
        // visualisations — small extra memory, big convenience.
        (Vector3[] positions, Vector3[] normals) = ExtractStreams(vertices, attributes);
        Aabb localBounds = ComputeBounds(positions);

        return new OpenGLMesh(gl, vao, vbo, ebo, (uint)indices.Length,
            positions, normals, indices.ToArray(), localBounds);
    }

    // Pulls per-vertex positions (location 0) and normals (location 1) out of
    // an interleaved attribute stream. Other attributes are ignored.
    private static (Vector3[] Positions, Vector3[] Normals) ExtractStreams(
        ReadOnlySpan<float> vertices, ReadOnlySpan<VertexAttribute> attributes)
    {
        int stride = 0;
        int positionOffset = -1;
        int normalOffset = -1;
        for (int i = 0; i < attributes.Length; i++)
        {
            if (attributes[i].Location == 0) positionOffset = stride;
            else if (attributes[i].Location == 1) normalOffset = stride;
            stride += (int)attributes[i].ComponentCount;
        }

        if (positionOffset < 0 || stride == 0)
            return ([], []);

        int vertexCount = vertices.Length / stride;
        var positions = new Vector3[vertexCount];
        Vector3[] normals = normalOffset >= 0 ? new Vector3[vertexCount] : [];

        for (int i = 0; i < vertexCount; i++)
        {
            int b = i * stride;
            positions[i] = new Vector3(
                vertices[b + positionOffset],
                vertices[b + positionOffset + 1],
                vertices[b + positionOffset + 2]);

            if (normalOffset >= 0)
                normals[i] = new Vector3(
                    vertices[b + normalOffset],
                    vertices[b + normalOffset + 1],
                    vertices[b + normalOffset + 2]);
        }
        return (positions, normals);
    }

    private static Aabb ComputeBounds(Vector3[] positions)
    {
        if (positions.Length == 0)
            return new Aabb(Vector3.Zero, Vector3.Zero);

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < positions.Length; i++)
        {
            min = Vector3.Min(min, positions[i]);
            max = Vector3.Max(max, positions[i]);
        }
        return new Aabb(min, max);
    }

    public override unsafe void Draw()
    {
        _gl.BindVertexArray(_vao);
        _gl.DrawElements(PrimitiveType.Triangles, IndexCount, DrawElementsType.UnsignedInt, null);
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _gl.DeleteBuffer(_ebo);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
    }
}
