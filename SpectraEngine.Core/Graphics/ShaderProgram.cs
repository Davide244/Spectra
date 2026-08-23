using SpectraEngine.Core.Graphics.Shaders;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace SpectraEngine.Core.Graphics;

public abstract class ShaderProgram : IDisposable
{
    public abstract void Use();

    /// <summary>
    /// Replaces this program's compiled code with <paramref name="blob"/>,
    /// preserving the object identity so all references (materials etc.) keep
    /// working. On failure the old code stays in place and <paramref name="error"/>
    /// carries the message. Backends that don't support reload return false.
    /// </summary>
    public abstract bool TryReload(PipelineBlob blob, [NotNullWhen(false)] out string? error);

    public abstract void SetUniform(string name, Matrix4x4 value);
    public abstract void SetUniform(string name, Vector4 value);
    public abstract void SetUniform(string name, Vector3 value);
    public abstract void SetUniform(string name, Vector2 value);
    public abstract void SetUniform(string name, float value);
    public abstract void SetUniform(string name, int value);

    /// <summary>Fills a <c>vec4[N]</c> uniform array.</summary>
    /// <remarks>
    /// <para>
    /// <b>Only <c>vec4</c> and <c>mat4</c> arrays exist here, and that is a
    /// measured restriction rather than an unfinished one.</b> An HLSL constant
    /// buffer pads every array element up to a 16-byte boundary, so a
    /// <c>float[8]</c> occupies 116 bytes and a <c>vec3[8]</c> 124, while the
    /// equivalent C# arrays are 32 and 96 bytes packed tight. GLSL packs them
    /// tight too. Copying one into the other is not an error at any layer: it
    /// writes a contiguous run of bytes into a strided layout, and the shader
    /// reads garbage from element one onwards, on D3D only, while OpenGL renders
    /// it perfectly. <c>vec4</c> (16) and <c>mat4</c> (64) are the only element
    /// types whose managed stride already equals the shader's, so one byte
    /// buffer serves all three backends. <c>CBufferPackingTests</c> measures
    /// this by compiling a buffer and reflecting it.
    /// </para>
    /// <para>
    /// <b>A length that does not match the shader's array is refused, not
    /// clamped.</b> Truncating leaves a stale tail from whatever was uploaded
    /// last, which renders as plausible nonsense for one draw and is very hard
    /// to trace back here.
    /// </para>
    /// </remarks>
    public abstract void SetUniform(string name, ReadOnlySpan<Vector4> values);

    /// <summary>Fills a <c>mat4[N]</c> uniform array.</summary>
    /// <remarks>
    /// <para>
    /// <b>No transpose, deliberately, exactly like the single-matrix
    /// overload.</b> <see cref="Matrix4x4"/> is row-major in memory; fxc packs
    /// constant-buffer matrices column-major by default and GLSL's <c>mat4</c>
    /// is column-major, so the same bytes denote the same matrix on all three
    /// backends. Three defaults happening to line up, which is why it is written
    /// down here and asserted in <c>CBufferPackingTests</c> rather than left to
    /// be rediscovered. Adding a transpose here to "fix" it would break every
    /// matrix in the engine.
    /// </para>
    /// <para>Length rules are the same as the <see cref="Vector4"/> overload.</para>
    /// </remarks>
    public abstract void SetUniform(string name, ReadOnlySpan<Matrix4x4> values);

    /// <summary>Binds <paramref name="texture"/> to texture unit <paramref name="unit"/> and assigns it to the named sampler.</summary>
    public abstract void SetTexture(string name, int unit, Texture texture);

    public abstract void Dispose();
}
