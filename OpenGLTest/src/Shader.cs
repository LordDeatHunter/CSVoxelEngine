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

    /// <param name="defines">
    /// Optional preprocessor prefix injected right after the <c>#version</c> line of both
    /// sources, so one GLSL file can be compiled as two program variants out of the same text:
    /// <c>#define CHUNK_DRAW</c> builds the pooled chunk-draw variant, which reads its per-draw
    /// origin and alpha from the ChunkInfo SSBO (see <c>ChunkDrawBatch</c>), while the plain
    /// program keeps the <c>uModel</c>/<c>uMVP</c> uniform path that entities and LOD use.
    /// </param>
    public Shader(GL gl, string vertexSource, string fragmentSource, string? defines = null)
    {
        _gl = gl;

        var vertexShader = CompileShader(ShaderType.VertexShader, InjectDefines(vertexSource, defines));
        var fragmentShader = CompileShader(ShaderType.FragmentShader, InjectDefines(fragmentSource, defines));

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

    /// <summary>
    /// Returns <paramref name="source"/> with <paramref name="defines"/> spliced in right after
    /// its first line. <c>#version</c> must stay the first line of a GLSL source, so the prefix
    /// cannot simply be prepended; the trailing <c>#line 2</c> makes the compiler report the
    /// original line numbers for everything after the injected block, so an error in a variant
    /// build still points at the right line of the file on disk. A null or empty prefix returns
    /// the source unchanged, which is what every plain (non-variant) compile does.
    /// </summary>
    internal static string InjectDefines(string source, string? defines)
    {
        if (string.IsNullOrEmpty(defines)) return source;

        // IndexOf('\n') + 1 keeps a CRLF first line intact: the \r stays on the #version line
        var newline = source.IndexOf('\n');
        var end = newline < 0 ? source.Length : newline + 1;
        return source[..end] + defines + "\n#line 2\n" + source[end..];
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

    // PERF_TODO R5. worldShader.Use() alone ran 20x a frame, most of them back-to-back in
    // EntityPass, and ApplyFrameUniforms/SetWorldUniform flip programs twice per call. Every
    // glUseProgram in this codebase goes through this method - verified with
    //   grep -rn "\.UseProgram[(]" OpenGLTest/src --include=*.cs
    // which must return exactly one line, the _gl.UseProgram below - so one static handle
    // covers every shader-owning class with no invalidation list: there is no path that can
    // change the bound program behind this cache's back. If anyone ever adds a raw
    // gl.UseProgram elsewhere, this cache goes stale silently; re-run that grep.
    //
    // Two traps in that grep, both already paid for. The unqualified `grep -rn "UseProgram"`
    // returns two hits - the second is a doc-comment mention in RenderManager - so it can
    // never return a single line. And the pattern is written with the bracket class [(]
    // rather than a plain ( so that THIS comment does not match it either; spelled the
    // obvious way, the check finds itself here and reports two hits forever.
    //
    // Render thread only, like every other GL call here (AGENTS.md) - plain field, no lock.
    private static uint _boundProgram;

    /// <summary>
    /// Makes this program current, skipping the <c>glUseProgram</c> when it already is
    /// (PERF_TODO R5). See the comment on <see cref="_boundProgram"/> for why one static
    /// field is sufficient and needs no invalidation list.
    /// </summary>
    public void Use()
    {
        if (_boundProgram == Handle) return;
        _gl.UseProgram(Handle);
        _boundProgram = Handle;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        _gl.DeleteProgram(Handle);
        // Belt-and-braces: GL is free to hand the deleted name back out to the next
        // CreateProgram, and a recycled handle matching a stale _boundProgram would make a
        // later Use() skip a bind it needed. Shaders are only ever disposed at shutdown today,
        // so this never fires in practice - it costs one comparison and removes the hazard.
        if (_boundProgram == Handle) _boundProgram = 0;
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