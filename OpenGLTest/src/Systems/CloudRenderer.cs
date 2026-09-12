using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// Draws the cloud layer: one static mesh built once from <see cref="CloudField"/>, a 2D grid of
/// CloudCellSize-block cells stacked 0..CloudMaxLevels layers high and stitched into a single rounded surface with no
/// interior faces. Each frame the field is drawn 3x3 around the camera with a rigid per-copy
/// offset, so the sky reads as infinite; the surviving copies ride one instance stream (a
/// per-instance offset attribute), so a sweep is one instanced draw rather than one draw per copy.
/// Because the noise and the mesh both wrap at the field edge, a mass crossing the seam continues
/// into the neighbouring copy unbroken. Each frame draws the copies twice: a depth pre-pass, then
/// colour, so the rounded surface blends once per pixel however its fillet pieces overlap.
/// The draw is translucent, so <see cref="RenderManager.RenderClouds"/> owns the blend state.
/// </summary>
public sealed class CloudRenderer : IDisposable
{
    /// <summary>Bytes per instance: one <c>vec2</c> copy offset, the only thing that varies per copy.</summary>
    private const uint InstanceStride = 8;

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;
    private readonly uint _instanceVbo;
    private readonly uint _indexCount;
    private bool _disposed;

    public CloudRenderer(GL gl)
    {
        _gl = gl;
        var (vertices, indices) = CloudField.BuildMesh(CloudField.BuildLevels());
        _indexCount = (uint)indices.Length;

        _shader = new Shader(gl,
            FileUtils.ReadFile("assets/shaders/cloud-vertex.glsl"),
            FileUtils.ReadFile("assets/shaders/cloud-fragment.glsl"));

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);

        _vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        gl.BufferData<CloudVertex>(BufferTargetARB.ArrayBuffer,
            (nuint)(vertices.Length * CloudVertex.Size), vertices, BufferUsageARB.StaticDraw);

        _ebo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer,
            (nuint)(indices.Length * sizeof(uint)), indices, BufferUsageARB.StaticDraw);

        // Clouds need no atlas, colour or light, so they get their own slim format: position and
        // flat face normal, nothing shared with the packed world Vertex.
        unsafe
        {
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, CloudVertex.Size, (void*)0);
            gl.EnableVertexAttribArray(0);

            gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, CloudVertex.Size, (void*)12);
            gl.EnableVertexAttribArray(1);

            // Location 2 is the per-instance copy offset: one vec2 per surviving copy of the 3x3.
            // The divisor is what makes it per instance rather than per vertex; without it every
            // instance would read the same offset and the sweep would collapse onto one untiled
            // field. The bind below is what VertexAttribPointer captures into the vertex array, so
            // the draw itself needs no ArrayBuffer bind, and DynamicStorageBit is what lets the
            // per-frame NamedBufferSubData refill it.
            _instanceVbo = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
            gl.BufferStorage(BufferStorageTarget.ArrayBuffer, (nuint)(CloudCopies.MaxCopies * InstanceStride),
                null, BufferStorageMask.DynamicStorageBit);
            gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, InstanceStride, (void*)0);
            gl.EnableVertexAttribArray(2);
            gl.VertexAttribDivisor(2, 1);
        }

        gl.BindVertexArray(0);
    }

    /// <summary>
    /// Draws the field 3x3 around the camera. Copy offsets snap to field multiples so tiles never
    /// swim; drift is a plain translation wrapped modulo the field on the CPU (the pattern repeats
    /// every field, so the wrap is seamless). Nothing wraps per vertex, so a copy can never tear.
    /// Copies outside the frustum are skipped like every other pass, and the survivors go up as one
    /// instance stream, so each sweep is a single draw. The remaining uniforms mirror the world
    /// shader's frame state, so clouds sit in the same day/night cycle and fog.
    /// </summary>
    public void Render(Matrix4x4 viewProj, Vector3 cameraPos, float time,
        Vector3 sunDir, Vector3 sunColor, float sunIntensity, Vector3 ambient,
        Vector3 fogColor, float fogNear, float fogFar)
    {
        if (GameSettings.CloudOpacity <= 0f)
            return;

        var field = WorldGenerationConfig.CloudFieldSize;
        var drift = CloudCopies.Drift(time, field);
        var snapX = CloudCopies.Snap(cameraPos.X, field);
        var snapZ = CloudCopies.Snap(cameraPos.Z, field);
        var thickness = WorldGenerationConfig.CloudMaxLevels * WorldGenerationConfig.CloudLayerThickness;

        // Both sweeps draw the same surviving copies in the same order, so the cull runs once and
        // both sweeps draw the one packed array. The cull sits up here, beside the opacity guard and
        // ahead of every GL call, so the nothing-visible exit is state-neutral: returning after the
        // program or vertex-array bind would leave that binding current for whatever pass runs next.
        Span<Vector2> offsets = stackalloc Vector2[CloudCopies.MaxCopies];
        var count = CloudCopies.Gather(new Frustum(viewProj), snapX, snapZ, drift, field, thickness, offsets);
        if (count == 0)
            return;

        _shader.Use();
        _shader.SetUniform("uMVP", viewProj);
        _shader.SetUniform("uCameraPos", cameraPos);
        _shader.SetUniform("uSunDir", sunDir);
        _shader.SetUniform("uSunColor", sunColor);
        _shader.SetUniform("uSunIntensity", sunIntensity);
        _shader.SetUniform("uAmbient", ambient);
        _shader.SetUniform("uFogColor", fogColor);
        _shader.SetUniform("uFogNear", fogNear);
        _shader.SetUniform("uFogFar", fogFar);
        _shader.SetUniform("uOpacity", GameSettings.CloudOpacity);

        _gl.NamedBufferSubData(_instanceVbo, 0, offsets[..count]);
        _gl.BindVertexArray(_vao);

        // Pass 1: depth only. Pass 2: colour, depth test only. Whatever the fillet pieces overlap,
        // each pixel blends exactly the nearest cloud surface once, so overlaps never darken and
        // draw order never matters. The second pass relies on the global DepthFunc of Lequal (set
        // in the RenderManager constructor) to let each nearest fragment through again.
        _gl.ColorMask(false, false, false, false);
        _gl.DepthMask(true);
        DrawCopies(count);
        _gl.ColorMask(true, true, true, true);
        _gl.DepthMask(false);
        DrawCopies(count);

        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// One sweep of every surviving copy as a single instanced draw, in the order
    /// <see cref="CloudCopies.Gather"/> packed them into the instance buffer; the caller owns the
    /// pass's GL state.
    /// </summary>
    private void DrawCopies(int count)
    {
        unsafe
        {
            _gl.DrawElementsInstanced(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt,
                null, (uint)count);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shader.Dispose();
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteBuffer(_instanceVbo);
        _gl.DeleteVertexArray(_vao);
        GC.SuppressFinalize(this);
    }
}
