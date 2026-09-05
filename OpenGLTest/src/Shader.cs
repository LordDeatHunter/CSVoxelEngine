using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace silkgltriangle;

public class Shader : IDisposable
{
    private readonly GL _gl;
    public uint Handle { get; }

    private bool _disposed;

    public Shader(GL gl, string vertexSource, string fragmentSource)
    {
        _gl = gl;

        var vertexShader = CompileShader(ShaderType.VertexShader, vertexSource);
        var fragmentShader = CompileShader(ShaderType.FragmentShader, fragmentSource);

        Handle = _gl.CreateProgram();
        _gl.AttachShader(Handle, vertexShader);
        _gl.AttachShader(Handle, fragmentShader);
        _gl.LinkProgram(Handle);

        _gl.GetProgram(Handle, GLEnum.LinkStatus, out var status);
        if (status == 0)
        {
            var infoLog = _gl.GetProgramInfoLog(Handle);
            throw new Exception($"Program linking failed:\n{infoLog}");
        }

        _gl.DetachShader(Handle, vertexShader);
        _gl.DetachShader(Handle, fragmentShader);
        _gl.DeleteShader(vertexShader);
        _gl.DeleteShader(fragmentShader);
    }

    private uint CompileShader(ShaderType type, string source)
    {
        var shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);

        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out var status);

        if (status != 0) return shader;

        var infoLog = _gl.GetShaderInfoLog(shader);
        throw new Exception($"{type} compilation failed:\n{infoLog}");
    }

    public void Use() => _gl.UseProgram(Handle);

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        _gl.DeleteProgram(Handle);
        _disposed = true;
    }

    // glGetUniformLocation does a string lookup in the driver; cache locations since
    // uniforms are set per chunk per frame
    private readonly Dictionary<string, int> _uniformLocations = new();

    public int GetUniformLocation(string name)
    {
        if (_uniformLocations.TryGetValue(name, out var location))
            return location;

        location = _gl.GetUniformLocation(Handle, name);
        _uniformLocations[name] = location;
        return location;
    }

    public void SetUniform(string name, float value)
    {
        var loc = GetUniformLocation(name);
        _gl.Uniform1(loc, value);
    }

    public void SetUniform(string name, int value)
    {
        var loc = GetUniformLocation(name);
        _gl.Uniform1(loc, value);
    }

    public void SetUniform(string name, Vector3 value)
    {
        var loc = GetUniformLocation(name);
        _gl.Uniform3(loc, value.X, value.Y, value.Z);
    }

    /// <summary>Sets a fixed-size vec3 array uniform (point light positions/colors).</summary>
    public void SetUniformArray3(string name, Vector3[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            var loc = GetUniformLocation($"{name}[{i}]");
            _gl.Uniform3(loc, values[i].X, values[i].Y, values[i].Z);
        }
    }

    public void SetUniform(string name, Vector4 value)
    {
        var loc = GetUniformLocation(name);
        _gl.Uniform4(loc, value.X, value.Y, value.Z, value.W);
    }

    /// <summary>Sets a GLSL bool uniform, which the API takes as an int.</summary>
    public void SetUniform(string name, bool value)
    {
        var loc = GetUniformLocation(name);
        _gl.Uniform1(loc, value ? 1 : 0);
    }

    public void SetUniform(string name, Matrix4x4 value, bool transpose = false)
    {
        var loc = GetUniformLocation(name);
        unsafe
        {
            fixed (float* ptr = &MemoryMarshal.GetReference(MemoryMarshal.CreateSpan(ref value.M11, 16)))
            {
                _gl.UniformMatrix4(loc, 1, transpose, ptr);
            }
        }
    }
}