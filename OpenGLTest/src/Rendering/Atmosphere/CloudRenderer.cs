using System;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Data;
using VoxelEngine.Effects.Sky;
using VoxelEngine.Rendering.Geometry;
using VoxelEngine.UI;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Rendering.Atmosphere;

/// <summary>
/// Draws the cloud layer: one static mesh built once from <see cref="CloudField"/>, a 2D grid of
/// CloudCellSize-block cells stacked 0..CloudMaxLevels layers high and stitched into a single rounded surface with no
/// interior faces. Each frame the field is drawn 3x3 around the camera with a rigid per-copy
/// offset, so the sky reads as infinite; the surviving copies ride one instance stream (a
/// per-instance offset attribute). Inside each copy the field is cut into tiles
/// (<see cref="CloudTiles"/>), and only the tile runs that may rasterize in the pass's own frustum
/// are drawn, as one <c>glMultiDrawElementsIndirect</c> per sweep whose commands are an
/// order-preserving subsequence of the full instanced draw (same vertices, same instance offset
/// through <c>BaseInstance</c>, same relative order); a dropped run rasterizes no fragment, so the
/// fragment stream is exactly that of drawing every copy whole. Because the noise and the mesh both wrap at the field edge, a mass crossing the
/// seam continues into the neighboring copy unbroken. Each frame draws the runs twice: a depth
/// pre-pass, then color, so the rounded surface blends once per pixel however its fillet pieces
/// overlap. The draw is translucent, so <see cref="SkyPasses.RenderClouds"/> owns the blend state.
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
    private readonly uint _commandBuffer;
    private readonly CloudTiles _tiles;
    private readonly DrawElementsIndirectCommand[] _commands;
    private readonly bool[] _tileScratch;
    private bool _disposed;

    /// <summary>
    /// This frame's silver-lining strength (<see cref="CloudSilverLining.Strength"/> of the sun's
    /// elevation), set by <see cref="FrameLighting.SetLighting"/>; 0, the default, draws none.
    /// </summary>
    public float SilverLining { get; set; }

    /// <param name="gl">The context.</param>
    /// <param name="cloudShadowUnit">The texture unit the cloud-shadow coverage map stays bound on;
    /// the silver lining reads the cloud's thickness from it.</param>
    public CloudRenderer(GL gl, int cloudShadowUnit)
    {
        _gl = gl;
        var (vertices, indices, cellIndexStart) = CloudField.BuildMesh(CloudField.BuildLevels());
        _tiles = new CloudTiles(vertices, indices, cellIndexStart, CloudField.Cells,
            SkyConfig.CloudTileCells, SkyConfig.CloudTileCullMarginBlocks);
        _commands = new DrawElementsIndirectCommand[_tiles.MaxCommands];
        _tileScratch = new bool[_tiles.TilesPerSide * _tiles.TilesPerSide];

        _shader = new Shader(gl,
            FileUtils.ReadFile("assets/shaders/cloud-vertex.glsl"),
            FileUtils.ReadFile("assets/shaders/cloud-fragment.glsl"));

        // The silver lining's tuning and the coverage map never change at run time
        _shader.Use();
        _shader.SetUniform("uSilverExponent", SkyConfig.CloudSilverLiningExponent);
        _shader.SetUniform("uSilverStep", SkyConfig.CloudSilverLiningStepBlocks);
        _shader.SetUniform("uCloudShadowMap", cloudShadowUnit);
        _shader.SetUniform("uCloudCells", CloudField.Cells);
        _shader.SetUniform("uCloudMaxLevels", SkyConfig.CloudMaxLevels);
        _shader.SetUniform("uCloudHeight", SkyConfig.CloudHeight);
        _shader.SetUniform("uCloudLayer", SkyConfig.CloudLayerThickness);
        _shader.SetUniform("uCloudField", SkyConfig.CloudFieldSize);

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

        // Clouds need no atlas, color or light, so they get their own slim format: position and
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
            // field. A command's BaseInstance picks its copy's element of this stream. The bind
            // below is what VertexAttribPointer captures into the vertex array, so the draw itself
            // needs no ArrayBuffer bind, and DynamicStorageBit is what lets the per-frame
            // NamedBufferSubData refill it.
            _instanceVbo = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
            gl.BufferStorage(BufferStorageTarget.ArrayBuffer, (nuint)(CloudCopies.MaxCopies * InstanceStride),
                null, BufferStorageMask.DynamicStorageBit);
            gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, InstanceStride, (void*)0);
            gl.EnableVertexAttribArray(2);
            gl.VertexAttribDivisor(2, 1);
        }

        gl.BindVertexArray(0);

        // The indirect commands of the culled tile runs, refilled per pass (main view and mirror).
        // The draw-indirect binding is not vertex-array state, so the bind here only creates the
        // storage and Render binds it around its own draws.
        unsafe
        {
            _commandBuffer = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, _commandBuffer);
            gl.BufferStorage(BufferStorageTarget.DrawIndirectBuffer,
                (nuint)(_tiles.MaxCommands * sizeof(DrawElementsIndirectCommand)), null,
                BufferStorageMask.DynamicStorageBit);
            gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, 0);
        }
    }

    /// <summary>
    /// Draws the field 3x3 around the camera. Copy offsets snap to field multiples so tiles never
    /// swim; drift is a plain translation along the wind, wrapped modulo the field on both axes on the CPU (the pattern repeats
    /// every field, so the wrap is seamless). Nothing wraps per vertex, so a copy can never tear.
    /// Copies outside the frustum are skipped like every other pass, then the tiles of the survivors
    /// that cannot rasterize in this pass's frustum (built from <paramref name="viewProj"/>, so the
    /// mirror culls against its own mirrored view) are dropped, and the rest go up as indirect
    /// commands, so each sweep is a single multi-draw. The remaining uniforms mirror the world
    /// shader's frame state, so clouds sit in the same day/night cycle and fog: <paramref name="sky"/>
    /// is the frame's dome palette, which the fog samples in the view direction (falling back to
    /// the flat <paramref name="fogColor"/> by <paramref name="fogWet"/> underwater or in lava).
    /// <paramref name="sunColor"/>, <paramref name="sunIntensity"/> and <paramref name="ambient"/>
    /// are display-space (sRGB) light, not the world's linear uniforms; fog and sky stay linear.
    /// The color pass mixes over the scene in display space by reading the render target it
    /// draws into (cloud-fragment.glsl): the caller binds that target's color texture to
    /// <paramref name="sceneUnit"/> and unbinds it afterwards, and this method issues the
    /// <c>glTextureBarrier</c> and turns blending off around that one sweep.
    /// </summary>
    public void Render(Matrix4x4 viewProj, Vector3 cameraPos, double time,
        Vector3 sunDir, Vector3 sunColor, float sunIntensity, Vector3 ambient,
        Vector3 fogColor, float fogNear, float fogFar, in SkyPalette sky, float fogWet, int sceneUnit)
    {
        if (GameSettings.CloudOpacity <= 0f)
            return;

        var field = SkyConfig.CloudFieldSize;
        var drift = CloudCopies.Drift(time, field);
        var snapX = CloudCopies.Snap(cameraPos.X, field);
        var snapZ = CloudCopies.Snap(cameraPos.Z, field);
        var thickness = SkyConfig.CloudMaxLevels * SkyConfig.CloudLayerThickness;

        // Both sweeps draw the same tile runs in the same order, so both culls run once and both
        // sweeps draw the one command list. The culls sit up here, beside the opacity guard and
        // ahead of every GL call, so the nothing-visible exits are state-neutral: returning after the
        // program or vertex-array bind would leave that binding current for whatever pass runs next.
        Span<Vector2> offsets = stackalloc Vector2[CloudCopies.MaxCopies];
        var frustum = new Frustum(viewProj);
        var count = CloudCopies.Gather(frustum, snapX, snapZ, drift, field, thickness, offsets);
        if (count == 0)
            return;
        var commandCount = _tiles.Gather(frustum, offsets[..count], _tileScratch, _commands);
        if (commandCount == 0)
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
        _shader.SetUniform("uSkyZenith", sky.Zenith);
        _shader.SetUniform("uSkyHorizon", sky.Horizon);
        _shader.SetUniform("uSkyGlow", sky.Glow);
        _shader.SetUniform("uSkyShape", SkyPalette.Shape);
        _shader.SetUniform("uFogWet", fogWet);
        _shader.SetUniform("uScene", sceneUnit);
        _shader.SetUniform("uSilverStrength", SilverLining);
        _shader.SetUniform("uCloudDrift", drift);

        _gl.NamedBufferSubData(_instanceVbo, 0, offsets[..count]);
        _gl.NamedBufferSubData(_commandBuffer, 0, _commands.AsSpan(0, commandCount));
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, _commandBuffer);

        DrawSweeps(commandCount);

        _gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, 0);
        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// The two sweeps over the bound command list: depth only, then color with the depth test
    /// alone. Whatever the fillet pieces overlap, each pixel blends exactly the nearest cloud
    /// surface once, so overlaps never darken and draw order never matters. The second sweep relies
    /// on the global DepthFunc of Lequal (set in the RenderManager constructor) to let each nearest
    /// fragment through again.
    /// </summary>
    private void DrawSweeps(int commandCount)
    {
        _gl.ColorMask(false, false, false, false);
        _gl.DepthMask(true);
        DrawCopies(commandCount);
        _gl.ColorMask(true, true, true, true);
        _gl.DepthMask(false);
        // The color sweep reads the target it writes (one surviving surface per pixel, thanks
        // to the pre-pass), so earlier writes must be made visible and the blender kept out.
        _gl.TextureBarrier();
        _gl.Disable(EnableCap.Blend);
        DrawCopies(commandCount);
        _gl.Enable(EnableCap.Blend);
    }

    /// <summary>
    /// Pays the cloud draw's first-use cost during load instead of on the first frames: the
    /// driver finishes the program for the state it actually runs under, and the first
    /// command and instance uploads and multi-draws happen here, not in the first frame's
    /// main view or the mirror's first frame. It draws the field's first triangle into a private
    /// 1x1 target of the scene target's formats (RGBA16F color, DepthComponent24 depth),
    /// reading that target's color through <paramref name="sceneUnit"/>, through both sweeps
    /// under each cull face the passes use (back in the main view, front in the mirror), each
    /// after its own uploads. Changes no frame drawn after load: every GL state it touches is
    /// put back, the program through <see cref="Shader.Use"/>'s cache (the cloud program stays
    /// current, as after the constructor), and the scene unit is bound and unbound through
    /// <paramref name="units"/>, so its cache stays true; the private textures are deleted only
    /// once nothing binds them. Unit 8 is never touched.
    /// </summary>
    internal unsafe void WarmUp(TextureUnits units, int sceneUnit)
    {
        // The CPU side of the frame path too: the copy and tile gathers over a top-down view of
        // the whole field, whose commands are then replaced by the one warm-up command below.
        var field = SkyConfig.CloudFieldSize;
        var center = new Vector3(field * 0.5f, SkyConfig.CloudHeight, field * 0.5f);
        var viewProj = Matrix4x4.CreateLookAt(center + new Vector3(0f, 100f, 0f), center, -Vector3.UnitZ)
                       * Matrix4x4.CreateOrthographic(field, field, 1f, 200f);
        Span<Vector2> offsets = stackalloc Vector2[CloudCopies.MaxCopies];
        var frustum = new Frustum(viewProj);
        var count = CloudCopies.Gather(frustum, center.X, center.Z, Vector2.Zero, field,
            SkyConfig.CloudMaxLevels * SkyConfig.CloudLayerThickness, offsets);
        _tiles.Gather(frustum, offsets[..count], _tileScratch, _commands);
        offsets[0] = Vector2.Zero;
        _commands[0] = new DrawElementsIndirectCommand { Count = 3, InstanceCount = 1 };

        Span<int> viewport = stackalloc int[4];
        _gl.GetInteger(GetPName.Viewport, viewport);
        var drawFbo = (uint)_gl.GetInteger(GetPName.DrawFramebufferBinding);
        var readFbo = (uint)_gl.GetInteger(GetPName.ReadFramebufferBinding);
        Span<bool> colorMask = stackalloc bool[4];
        _gl.GetBoolean(GetPName.ColorWritemask, colorMask);
        _gl.GetBoolean(GetPName.DepthWritemask, out var depthMask);
        var blend = _gl.IsEnabled(EnableCap.Blend);
        var blendSrcRgb = _gl.GetInteger(GetPName.BlendSrcRgb);
        var blendDstRgb = _gl.GetInteger(GetPName.BlendDstRgb);
        var blendSrcAlpha = _gl.GetInteger(GetPName.BlendSrcAlpha);
        var blendDstAlpha = _gl.GetInteger(GetPName.BlendDstAlpha);
        var cull = _gl.IsEnabled(EnableCap.CullFace);
        var cullMode = _gl.GetInteger(GetPName.CullFaceMode);
        var depthTest = _gl.IsEnabled(EnableCap.DepthTest);
        var vao = (uint)_gl.GetInteger(GetPName.VertexArrayBinding);
        var indirect = (uint)_gl.GetInteger(GLEnum.DrawIndirectBufferBinding);

        _gl.CreateTextures(TextureTarget.Texture2D, 1, out uint color);
        _gl.TextureStorage2D(color, 1, SizedInternalFormat.Rgba16f, 1, 1);
        _gl.CreateTextures(TextureTarget.Texture2D, 1, out uint depth);
        _gl.TextureStorage2D(depth, 1, SizedInternalFormat.DepthComponent24, 1, 1);
        _gl.CreateFramebuffers(1, out uint fbo);
        _gl.NamedFramebufferTexture(fbo, FramebufferAttachment.ColorAttachment0, color, 0);
        _gl.NamedFramebufferTexture(fbo, FramebufferAttachment.DepthAttachment, depth, 0);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        _gl.Viewport(0, 0, 1, 1);
        _gl.ColorMask(true, true, true, true);
        _gl.DepthMask(true);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        // The callers' state around Render: blended, depth-tested, depth writes off, culled
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);
        _gl.Enable(EnableCap.CullFace);
        units.BindUnit(sceneUnit, TextureTarget.Texture2D, color);

        _shader.Use();
        _shader.SetUniform("uMVP", viewProj);
        _shader.SetUniform("uScene", sceneUnit);
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, _commandBuffer);
        foreach (var face in (ReadOnlySpan<TriangleFace>)[TriangleFace.Back, TriangleFace.Front])
        {
            _gl.CullFace(face);
            _gl.NamedBufferSubData(_instanceVbo, 0, offsets[..1]);
            _gl.NamedBufferSubData(_commandBuffer, 0, _commands.AsSpan(0, 1));
            DrawSweeps(1);
        }

        units.BindUnit(sceneUnit, TextureTarget.Texture2D, 0);
        units.ActiveUnit(0);
        _gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, indirect);
        _gl.BindVertexArray(vao);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, drawFbo);
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, readFbo);
        _gl.Viewport(viewport[0], viewport[1], (uint)viewport[2], (uint)viewport[3]);
        _gl.ColorMask(colorMask[0], colorMask[1], colorMask[2], colorMask[3]);
        _gl.DepthMask(depthMask);
        _gl.BlendFuncSeparate((BlendingFactor)blendSrcRgb, (BlendingFactor)blendDstRgb,
            (BlendingFactor)blendSrcAlpha, (BlendingFactor)blendDstAlpha);
        SetEnabled(EnableCap.Blend, blend);
        _gl.CullFace((TriangleFace)cullMode);
        SetEnabled(EnableCap.CullFace, cull);
        SetEnabled(EnableCap.DepthTest, depthTest);
        _gl.DeleteFramebuffer(fbo);
        _gl.DeleteTexture(color);
        _gl.DeleteTexture(depth);
    }

    private void SetEnabled(EnableCap cap, bool on)
    {
        if (on) _gl.Enable(cap);
        else _gl.Disable(cap);
    }

    /// <summary>
    /// One sweep of the bound command list: every visible tile run of every surviving copy, in
    /// copy-slot order and ascending index order inside a copy, as a single multi-draw; the caller
    /// owns the pass's GL state and the vertex-array and draw-indirect bindings.
    /// </summary>
    private void DrawCopies(int commandCount)
    {
        unsafe
        {
            _gl.MultiDrawElementsIndirect(PrimitiveType.Triangles, DrawElementsType.UnsignedInt, (void*)0,
                (uint)commandCount, 0);
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
        _gl.DeleteBuffer(_commandBuffer);
        _gl.DeleteVertexArray(_vao);
        GC.SuppressFinalize(this);
    }
}
