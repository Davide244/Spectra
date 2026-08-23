using Silk.NET.OpenGL;
using SpectraEngine.Core.Graphics.Shaders;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text;

namespace SpectraEngine.Core.Graphics.OpenGL;

internal sealed class OpenGLShaderProgram : ShaderProgram
{
    private readonly GL _gl;
    private uint _handle;
    private readonly Dictionary<string, int> _uniformCache = new();
    private bool _disposed;

    private OpenGLShaderProgram(GL gl, uint handle)
    {
        _gl = gl;
        _handle = handle;
    }

    public override bool TryReload(PipelineBlob blob, [NotNullWhen(false)] out string? error)
    {
        if (blob.Backend != GraphicsBackend.OpenGL)
        {
            error = $"Expected OpenGL blob, got {blob.Backend}";
            return false;
        }
        if (blob.Format != ShaderDataFormat.SourceText)
        {
            error = $"OpenGL requires SourceText format, got {blob.Format}";
            return false;
        }

        string vertexSource = Encoding.UTF8.GetString(blob.VertexData
            ?? throw new InvalidOperationException("Compiled shader has no vertex stage"));
        string fragmentSource = Encoding.UTF8.GetString(blob.FragmentData
            ?? throw new InvalidOperationException("Compiled shader has no fragment stage"));

        uint newHandle;
        try
        {
            newHandle = LinkProgram(_gl, vertexSource, fragmentSource);
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return false;
        }

        uint old = _handle;
        _handle = newHandle;
        _uniformCache.Clear();
        if (old != 0)
            _gl.DeleteProgram(old);
        error = null;
        return true;
    }

    internal static OpenGLShaderProgram Create(GL gl, string vertexSource, string fragmentSource)
    {
        uint program = LinkProgram(gl, vertexSource, fragmentSource);
        return new OpenGLShaderProgram(gl, program);
    }

    private static uint LinkProgram(GL gl, string vertexSource, string fragmentSource)
    {
        uint vertexShader = CompileShader(gl, ShaderType.VertexShader, vertexSource);
        uint fragmentShader = CompileShader(gl, ShaderType.FragmentShader, fragmentSource);

        uint program = gl.CreateProgram();
        gl.AttachShader(program, vertexShader);
        gl.AttachShader(program, fragmentShader);
        gl.LinkProgram(program);

        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int status);
        if (status != (int)GLEnum.True)
        {
            string log = gl.GetProgramInfoLog(program);
            gl.DeleteProgram(program);
            gl.DeleteShader(vertexShader);
            gl.DeleteShader(fragmentShader);
            throw new InvalidOperationException($"Shader program link failed: {log}");
        }

        gl.DetachShader(program, vertexShader);
        gl.DetachShader(program, fragmentShader);
        gl.DeleteShader(vertexShader);
        gl.DeleteShader(fragmentShader);

        return program;
    }

    public override void Use()
    {
        _gl.UseProgram(_handle);
    }

    public override unsafe void SetUniform(string name, ReadOnlySpan<Vector4> values)
    {
        int location = GetLocation(name);
        if (location < 0 || values.Length == 0) return;

        // GL guarantees that a basic-type array's name resolves to element zero,
        // so one location plus a count fills the whole array. No per-element
        // "name[i]" lookup, and therefore no string allocated per light per
        // frame.
        fixed (Vector4* p = values)
            _gl.Uniform4(location, (uint)values.Length, (float*)p);
    }

    public override unsafe void SetUniform(string name, ReadOnlySpan<Matrix4x4> values)
    {
        int location = GetLocation(name);
        if (location < 0 || values.Length == 0) return;

        // transpose: false, matching the single-matrix overload below. See the
        // base class for why that is correct and why "fixing" it is not.
        fixed (Matrix4x4* p = values)
            _gl.UniformMatrix4(location, (uint)values.Length, false, (float*)p);
    }

    public override unsafe void SetUniform(string name, Matrix4x4 value)
    {
        int location = GetLocation(name);
        if (location < 0) return;
        _gl.UniformMatrix4(location, 1, false, (float*)&value);
    }

    public override void SetUniform(string name, Vector4 value)
    {
        int location = GetLocation(name);
        if (location < 0) return;
        _gl.Uniform4(location, value.X, value.Y, value.Z, value.W);
    }

    public override void SetUniform(string name, Vector3 value)
    {
        int location = GetLocation(name);
        if (location < 0) return;
        _gl.Uniform3(location, value.X, value.Y, value.Z);
    }

    public override void SetUniform(string name, Vector2 value)
    {
        int location = GetLocation(name);
        if (location < 0) return;
        _gl.Uniform2(location, value.X, value.Y);
    }

    public override void SetUniform(string name, float value)
    {
        int location = GetLocation(name);
        if (location < 0) return;
        _gl.Uniform1(location, value);
    }

    public override void SetUniform(string name, int value)
    {
        int location = GetLocation(name);
        if (location < 0) return;
        _gl.Uniform1(location, value);
    }

    public override void SetTexture(string name, int unit, Texture texture)
    {
        if (texture is not OpenGLTexture gl)
            throw new ArgumentException($"Texture must be OpenGLTexture, got {texture.GetType().Name}", nameof(texture));

        int location = GetLocation(name);
        if (location < 0) return;

        gl.Bind(unit);
        _gl.Uniform1(location, unit);
    }

    private int GetLocation(string name)
    {
        if (_uniformCache.TryGetValue(name, out int cached))
            return cached;

        int location = _gl.GetUniformLocation(_handle, name);
        _uniformCache[name] = location;
        return location;
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _gl.DeleteProgram(_handle);
    }

    private static uint CompileShader(GL gl, ShaderType type, string source)
    {
        uint shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);

        gl.GetShader(shader, ShaderParameterName.CompileStatus, out int status);
        if (status != (int)GLEnum.True)
        {
            string log = gl.GetShaderInfoLog(shader);
            gl.DeleteShader(shader);
            throw new InvalidOperationException($"{type} compilation failed: {log}");
        }

        return shader;
    }
}
