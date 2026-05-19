using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Core.Graphics.OpenGL;

internal sealed class OpenGLShaderProgram : ShaderProgram
{
    private readonly GL _gl;
    private readonly uint _handle;
    private readonly Dictionary<string, int> _uniformCache = new();
    private bool _disposed;

    private OpenGLShaderProgram(GL gl, uint handle)
    {
        _gl = gl;
        _handle = handle;
    }

    internal static OpenGLShaderProgram Create(GL gl, string vertexSource, string fragmentSource)
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

        return new OpenGLShaderProgram(gl, program);
    }

    public override void Use()
    {
        _gl.UseProgram(_handle);
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
