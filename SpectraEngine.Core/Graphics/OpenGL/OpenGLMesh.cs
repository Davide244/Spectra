using Silk.NET.OpenGL;
using System;

namespace SpectraEngine.Core.Graphics.OpenGL;

internal sealed class OpenGLMesh : Mesh
{
    private readonly GL _gl;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;
    private bool _disposed;

    private OpenGLMesh(GL gl, uint vao, uint vbo, uint ebo, uint indexCount)
    {
        _gl = gl;
        _vao = vao;
        _vbo = vbo;
        _ebo = ebo;
        IndexCount = indexCount;
    }

    internal static unsafe OpenGLMesh Create(GL gl, ReadOnlySpan<float> vertices, ReadOnlySpan<uint> indices,
        ReadOnlySpan<VertexAttribute> attributes, MeshCpuAccess cpuAccess)
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

        var mesh = new OpenGLMesh(gl, vao, vbo, ebo, (uint)indices.Length);
        mesh.InitializeCpuData(vertices, indices, attributes, cpuAccess);
        return mesh;
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
