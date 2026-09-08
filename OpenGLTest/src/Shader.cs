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

    // Locations of the elements of a fixed-size array uniform, so a caller that sets one
    // every frame never rebuilds the "name[i]" strings the lookup would need
    private readonly Dictionary<string, int[]> _uniformArrayLocations = new();

    /// <summary>
    /// Resolves and caches the locations of <paramref name="count"/> elements of the array
    /// uniform <paramref name="name"/>. Hot draw loops should hold on to the returned array
    /// and pass it to <see cref="SetUniformArray3(int[], ReadOnlySpan{Vector3})"/> rather than
    /// going through the name each time, which allocates one string per element.
    /// </summary>
    public int[] GetUniformArrayLocations(string name, int count)
    {
        if (_uniformArrayLocations.TryGetValue(name, out var locations) && locations.Length == count)
            return locations;

        locations = new int[count];
        for (var i = 0; i < count; i++)
            locations[i] = GetUniformLocation($"{name}[{i}]");

        _uniformArrayLocations[name] = locations;
        return locations;
    }

    public void SetUniform(string name, float value) => SetUniform(GetUniformLocation(name), value);

    public void SetUniform(int location, float value) => _gl.Uniform1(location, value);

    public void SetUniform(string name, int value) => SetUniform(GetUniformLocation(name), value);

    public void SetUniform(int location, int value) => _gl.Uniform1(location, value);

    public void SetUniform(string name, Vector2 value) => SetUniform(GetUniformLocation(name), value);

    public void SetUniform(int location, Vector2 value) => _gl.Uniform2(location, value.X, value.Y);

    public void SetUniform(string name, Vector3 value) => SetUniform(GetUniformLocation(name), value);

    public void SetUniform(int location, Vector3 value) => _gl.Uniform3(location, value.X, value.Y, value.Z);

    /// <summary>Sets a fixed-size vec3 array uniform (point light positions/colors).</summary>
    public void SetUniformArray3(string name, Vector3[] values) =>
        SetUniformArray3(GetUniformArrayLocations(name, values.Length), values);

    /// <summary>
    /// Sets a fixed-size vec3 array uniform through locations already resolved by
    /// <see cref="GetUniformArrayLocations"/>, so no strings are built per call.
    /// </summary>
    public void SetUniformArray3(int[] locations, ReadOnlySpan<Vector3> values)
    {
        var count = Math.Min(locations.Length, values.Length);
        for (var i = 0; i < count; i++)
            _gl.Uniform3(locations[i], values[i].X, values[i].Y, values[i].Z);
    }

    public void SetUniform(string name, Vector4 value) => SetUniform(GetUniformLocation(name), value);

    public void SetUniform(int location, Vector4 value) =>
        _gl.Uniform4(location, value.X, value.Y, value.Z, value.W);

    /// <summary>Sets a GLSL bool uniform, which the API takes as an int.</summary>
    public void SetUniform(string name, bool value) => SetUniform(GetUniformLocation(name), value);

    /// <summary>Sets a GLSL bool uniform, which the API takes as an int.</summary>
    public void SetUniform(int location, bool value) => _gl.Uniform1(location, value ? 1 : 0);

    public void SetUniform(string name, Matrix4x4 value, bool transpose = false) =>
        SetUniform(GetUniformLocation(name), value, transpose);

    public void SetUniform(int location, Matrix4x4 value, bool transpose = false)
    {
        unsafe
        {
            fixed (float* ptr = &MemoryMarshal.GetReference(MemoryMarshal.CreateSpan(ref value.M11, 16)))
            {
                _gl.UniformMatrix4(location, 1, transpose, ptr);
            }
        }
    }
}