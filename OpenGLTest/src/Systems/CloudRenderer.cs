using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// Draws the cloud layer: one static mesh built once from <see cref="CloudField"/>, a 2D grid of
/// CloudCellSize-block cells stacked 0..CloudMaxLevels layers high and stitched into a single rounded surface with no
/// interior faces. Each frame the field is drawn 3x3 around the camera with a rigid per-copy
/// offset, so the sky reads as infinite in nine draws; because the noise and the mesh both wrap at
/// the field edge, a mass crossing the seam continues into the neighbouring copy unbroken. Each
/// frame draws the copies twice: a depth pre-pass, then colour, so the rounded surface blends once
/// per pixel however its fillet pieces overlap.
/// The draw is translucent, so <see cref="RenderManager.RenderClouds"/> owns the blend state.
/// </summary>
public sealed class CloudRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;
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
        }

        gl.BindVertexArray(0);
    }

    /// <summary>
    /// Draws the field 3x3 around the camera. Copy offsets snap to field multiples so tiles never
    /// swim; drift is a plain translation wrapped modulo the field on the CPU (the pattern repeats
    /// every field, so the wrap is seamless). Nothing wraps per vertex, so a copy can never tear.
    /// Copies outside the frustum are skipped like every other pass. The remaining uniforms mirror
    /// the world shader's frame state, so clouds sit in the same day/night cycle and fog.
    /// </summary>
    public void Render(Matrix4x4 viewProj, Vector3 cameraPos, float time,
        Vector3 sunDir, Vector3 sunColor, float sunIntensity, Vector3 ambient,
        Vector3 fogColor, float fogNear, float fogFar)
    {
        if (GameSettings.CloudOpacity <= 0f)
            return;

        var field = WorldGenerationConfig.CloudFieldSize;
        var drift = (time * WorldGenerationConfig.CloudDriftBlocksPerSecond) % field;
        var snapX = MathF.Floor(cameraPos.X / field) * field;
        var snapZ = MathF.Floor(cameraPos.Z / field) * field;
        var thickness = WorldGenerationConfig.CloudMaxLevels * WorldGenerationConfig.CloudLayerThickness;

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

        var frustum = new Frustum(viewProj);
        _gl.BindVertexArray(_vao);

        // Pass 1: depth only. Pass 2: colour, depth test only. Whatever the fillet pieces overlap,
        // each pixel blends exactly the nearest cloud surface once, so overlaps never darken and
        // draw order never matters. The second pass relies on the global DepthFunc of Lequal (set
        // in the RenderManager constructor) to let each nearest fragment through again.
        _gl.ColorMask(false, false, false, false);
        _gl.DepthMask(true);
        DrawCopies(frustum, snapX, snapZ, drift, field, thickness);
        _gl.ColorMask(true, true, true, true);
        _gl.DepthMask(false);
        DrawCopies(frustum, snapX, snapZ, drift, field, thickness);

        _gl.BindVertexArray(0);
    }

    /// <summary>One sweep of the 3x3 copies, culled per copy; the caller owns the pass's GL state.</summary>
    private void DrawCopies(Frustum frustum, float snapX, float snapZ, float drift, float field, float thickness)
    {
        for (var ix = -1; ix <= 1; ix++)
        for (var iz = -1; iz <= 1; iz++)
        {
            var minX = snapX + ix * field + drift;
            var minZ = snapZ + iz * field;
            var min = new Vector3(minX, WorldGenerationConfig.CloudHeight, minZ);
            if (!frustum.IntersectsAABB(min, min + new Vector3(field, thickness, field)))
                continue;

            _shader.SetUniform("uOffset", new Vector2(minX, minZ));
            unsafe { _gl.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, null); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shader.Dispose();
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
        GC.SuppressFinalize(this);
    }
}
