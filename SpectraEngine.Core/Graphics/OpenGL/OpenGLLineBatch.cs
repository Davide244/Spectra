using System;
using Silk.NET.OpenGL;

namespace SpectraEngine.Core.Graphics.OpenGL;

/// <summary>
/// A dynamic VBO that uploads interleaved (position + colour) line vertices
/// and draws them as <c>GL_LINES</c>. Grows on demand via <c>BufferData</c>;
/// reuses the existing allocation via <c>BufferSubData</c> otherwise.
/// </summary>
internal sealed class OpenGLLineBatch : IDisposable
{
    private const int FloatsPerVertex = 6;

    private readonly GL _gl;
    private readonly uint _vao;
    private readonly uint _vbo;
    private uint _vertexCapacity;
    private bool _disposed;

    public unsafe OpenGLLineBatch(GL gl)
    {
        _gl = gl;
        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();

        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);

        // Layout matches the DebugLine shader: location 0 = position, 1 = colour.
        int stride = FloatsPerVertex * sizeof(float);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)0);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)(3 * sizeof(float)));
        gl.EnableVertexAttribArray(1);

        gl.BindVertexArray(0);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
    }

    public unsafe void Draw(ReadOnlySpan<float> interleavedVertices, uint vertexCount)
    {
        if (vertexCount == 0)
            return;

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);

        nuint byteCount = (nuint)(interleavedVertices.Length * sizeof(float));
        fixed (float* p = interleavedVertices)
        {
            if (vertexCount > _vertexCapacity)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer, byteCount, p, BufferUsageARB.DynamicDraw);
                _vertexCapacity = vertexCount;
            }
            else
            {
                _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, byteCount, p);
            }
        }

        _gl.DrawArrays(GLEnum.Lines, 0, vertexCount);
        _gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
    }
}
