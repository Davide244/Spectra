using Silk.NET.OpenGL;
using System;

namespace SpectraEngine.Core.Graphics.OpenGL;

/// <summary>
/// A GL buffer of per-instance attributes, plus the layout describing it.
/// </summary>
/// <remarks>
/// <b>The layout travels with the buffer because GL binds it into the VAO, not
/// into the draw call.</b> D3D takes the input layout at draw time and the
/// buffer separately; GL has no equivalent, so a mesh has to wire this buffer's
/// attribute pointers into its own vertex array before it can draw from it. The
/// mesh does that once per buffer it meets (see
/// <c>OpenGLMesh.DrawInstanced</c>), which is why the attributes have to be
/// reachable from here rather than passed per draw.
/// </remarks>
internal sealed class OpenGLInstanceBuffer : InstanceBuffer
{
    private readonly GL _gl;
    private bool _disposed;

    /// <summary>The GL buffer name, for the mesh wiring its attribute pointers.</summary>
    internal uint Handle { get; }

    /// <summary>The per-instance attributes, in the order they were declared.</summary>
    internal VertexAttribute[] Attributes { get; }

    /// <summary>Bytes between one instance's data and the next.</summary>
    internal uint Stride { get; }

    /// <summary>
    /// Bumped on every reallocation. A mesh caches which buffer it has already
    /// wired into its vertex array, and a name alone is not enough to key that
    /// on: GL reuses buffer names, so a freed buffer and a fresh one can share
    /// one and the mesh would skip rewiring against different storage.
    /// </summary>
    internal uint Generation { get; }

    private static uint _nextGeneration = 1;

    internal OpenGLInstanceBuffer(GL gl, int capacityInstances, ReadOnlySpan<VertexAttribute> attributes, int floatsPerInstance)
    {
        _gl = gl;
        Capacity = capacityInstances;
        FloatsPerInstance = floatsPerInstance;
        Attributes = attributes.ToArray();
        Stride = (uint)(floatsPerInstance * sizeof(float));
        Generation = _nextGeneration++;

        Handle = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, Handle);
        unsafe
        {
            // Allocated empty and filled by Update. DynamicDraw because it is
            // rewritten every frame by construction.
            gl.BufferData(
                BufferTargetARB.ArrayBuffer,
                (nuint)(capacityInstances * Stride),
                null,
                BufferUsageARB.DynamicDraw);
        }
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Orphaning belongs here rather than in <see cref="Append"/>: handing the
    /// driver a fresh allocation lets it keep serving in-flight draws from the
    /// old one instead of stalling, but doing it per append would also throw
    /// away everything earlier appends in the same frame wrote.
    /// </remarks>
    protected override unsafe void OnBeginFrame()
    {
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, Handle);
        _gl.BufferData(
            BufferTargetARB.ArrayBuffer, (nuint)(Capacity * Stride), null, BufferUsageARB.DynamicDraw);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
    }

    /// <inheritdoc/>
    public override unsafe int Append(ReadOnlySpan<float> data, int instanceCount)
    {
        ValidateUpdate(data, instanceCount);
        int first = Cursor;
        if (instanceCount == 0)
            return first;

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, Handle);
        fixed (float* p = data)
        {
            _gl.BufferSubData(
                BufferTargetARB.ArrayBuffer,
                (nint)((nuint)first * Stride),
                (nuint)(data.Length * sizeof(float)),
                p);
        }
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);

        Cursor = first + instanceCount;
        return first;
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _gl.DeleteBuffer(Handle);
    }
}
