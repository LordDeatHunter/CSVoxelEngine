using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Data;
using VoxelEngine.Entities;
using VoxelEngine.Graphics;

namespace VoxelEngine.Rendering;

/// <summary>
/// Five hearts floating above every live mob, always facing the camera. Uses the same
/// <c>assets/textures/ui/heart.png</c> / <c>heart_empty.png</c> sprites as the player HUD
/// (with the same 1px side-padding crop), but drawn as world-space camera-facing billboards
/// in the entity pass rather than screen-space UI, so they sit above the mob's head,
/// occlude against terrain and fog out with distance like the other billboard batches.
/// Each heart is an empty shell with a left-aligned fill overlay cropped to the exact
/// fraction, so the row reads the HP percentage whatever the entity's pool is.
/// Back-to-front so the blend reads correctly. Main thread only.
/// </summary>
public sealed class EntityHealthbarRenderer : IDisposable
{
    /// <summary>Hearts per row; the pool is spread proportionally across them.</summary>
    public const int HeartCount = WorldGenerationConfig.EntityHealthbarHearts;

    /// <summary>Full-heart sprite, as a single-slice array texture like every other sprite.</summary>
    public const string HeartSpritePath = "assets/textures/ui/heart.png";

    /// <summary>Empty-shell sprite, as a single-slice array texture like every other sprite.</summary>
    public const string HeartEmptySpritePath = "assets/textures/ui/heart_empty.png";

    // The HUD sprites are 32px wide with 1px of empty padding down each side; only the
    // middle 30px are drawn, exactly like UIRenderer.DrawHearts.
    private const float HeartUMin = 1f / 32f;
    private const float HeartUMax = 31f / 32f;

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;
    private readonly uint _heartTexture;
    private readonly uint _heartEmptyTexture;
    private readonly int _maxQuads;
    private bool _disposed;

    private readonly Vertex[] _verts;
    private readonly float[] _depths;
    private readonly int[] _order;
    private readonly float[] _fills = new float[HeartCount];

    // Corner signs in the winding the billboard batches share: bottom-left, top-left,
    // top-right, bottom-right.
    private static readonly (float X, float Y)[] CornerSigns =
        [(-1f, -1f), (-1f, 1f), (1f, 1f), (1f, -1f)];

    public EntityHealthbarRenderer(GL gl)
    {
        _gl = gl;
        _maxQuads = WorldGenerationConfig.MobsMaxAlive * HeartCount;
        _shader = new Shader(gl,
            FileUtils.ReadFile("assets/shaders/entity-healthbar-vertex.glsl"),
            FileUtils.ReadFile("assets/shaders/entity-healthbar-fragment.glsl"));
        _shader.Use();
        _shader.SetUniform("uSprite", 0);
        _heartTexture = TextureLoader.LoadFromFile(gl, HeartSpritePath, srgb: true);
        _heartEmptyTexture = TextureLoader.LoadFromFile(gl, HeartEmptySpritePath, srgb: true);

        _verts = new Vertex[_maxQuads * 4];
        _depths = new float[WorldGenerationConfig.MobsMaxAlive];
        _order = new int[WorldGenerationConfig.MobsMaxAlive];

        var indices = new ushort[_maxQuads * 6];
        for (var i = 0; i < _maxQuads; i++)
        {
            var v = (ushort)(i * 4);
            var o = i * 6;
            indices[o + 0] = v;
            indices[o + 1] = (ushort)(v + 1);
            indices[o + 2] = (ushort)(v + 2);
            indices[o + 3] = v;
            indices[o + 4] = (ushort)(v + 2);
            indices[o + 5] = (ushort)(v + 3);
        }

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);

        _vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        gl.BufferData<Vertex>(BufferTargetARB.ArrayBuffer,
            (nuint)(_verts.Length * Vertex.Size), _verts, BufferUsageARB.DynamicDraw);

        _ebo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        gl.BufferData<ushort>(BufferTargetARB.ElementArrayBuffer,
            (nuint)(indices.Length * sizeof(ushort)), indices, BufferUsageARB.StaticDraw);

        Mesh.ConfigureVertexAttribs(gl);
        gl.BindVertexArray(0);
    }

    /// <summary>
    /// Per-heart fill fractions for a <paramref name="health"/> / <paramref name="maxHealth"/>
    /// pool spread across <see cref="HeartCount"/> hearts: 1 is a full heart, 0 an empty
    /// shell, anything between a proportionally cropped fill. Out-of-range health clamps,
    /// and a non-positive pool fills nothing instead of dividing by zero.
    /// </summary>
    public static void HeartFills(int health, int maxHealth, Span<float> fills)
    {
        if (fills.Length != HeartCount)
            throw new ArgumentOutOfRangeException(nameof(fills),
                $"Expected room for {HeartCount} hearts, got {fills.Length}.");
        if (maxHealth <= 0)
        {
            fills.Fill(0f);
            return;
        }

        var value = MathUtils.Clamp01((float)health / maxHealth) * HeartCount;
        for (var i = 0; i < HeartCount; i++)
            fills[i] = MathUtils.Clamp01(value - i);
    }

    /// <summary>
    /// World Y of the hearts row's center for a body centered at
    /// <paramref name="bodyCenterY"/> whose head sits <paramref name="headAboveCenter"/>
    /// above it (already scaled — pass <see cref="Mob.HeadAboveCenter"/>).
    /// </summary>
    public static float BarCenterY(float bodyCenterY, float headAboveCenter) =>
        bodyCenterY + headAboveCenter + WorldGenerationConfig.EntityHealthbarLiftBlocks;

    /// <summary>
    /// Draws one five-heart row above each mob as two blended, depth-tested batches with
    /// no depth writes: first every empty shell, then every non-empty fill over them.
    /// </summary>
    public void Render(IReadOnlyList<Mob> mobs, Matrix4x4 viewProj, Vector3 cameraPos,
        Vector3 forward, Vector3 fogColor, float fogNear, float fogFar)
    {
        var count = Math.Min(mobs.Count, WorldGenerationConfig.MobsMaxAlive);
        if (count == 0) return;

        forward.BillboardBasis(out var right, out var up);

        var size = WorldGenerationConfig.EntityHeartSizeBlocks;

        for (var i = 0; i < count; i++)
        {
            _depths[i] = Vector3.DistanceSquared(mobs[i].Position, cameraPos);
            _order[i] = i;
        }
        Array.Sort(_depths, _order, 0, count);

        _shader.Use();
        _shader.SetUniform("uVP", viewProj);
        _shader.SetUniform("uCameraPos", cameraPos);
        _shader.SetUniform("uFogColor", fogColor);
        _shader.SetUniform("uFogNear", fogNear);
        _shader.SetUniform("uFogFar", fogFar);

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);

        // Every empty shell first, far to near.
        var quads = 0;
        for (var slot = 0; slot < count; slot++)
        {
            var mob = mobs[_order[count - 1 - slot]];
            var center = new Vector3(mob.Position.X,
                BarCenterY(mob.Position.Y, mob.HeadAboveCenter),
                mob.Position.Z);
            for (var h = 0; h < HeartCount; h++)
            {
                var heartCenter = center + right * ((h - (HeartCount - 1) * 0.5f) * size);
                WriteQuad(quads * 4, heartCenter, size, right, up, HeartUMin, HeartUMax);
                quads++;
            }
        }
        DrawBatch(_heartEmptyTexture, quads);

        // Then the fills over them: each heart's fraction as a left-aligned overlay cropped
        // to that fraction of the sprite, so the row reads the exact percentage.
        quads = 0;
        for (var slot = 0; slot < count; slot++)
        {
            var mob = mobs[_order[count - 1 - slot]];
            HeartFills(mob.Health, mob.MaxHealth, _fills);
            var center = new Vector3(mob.Position.X,
                BarCenterY(mob.Position.Y, mob.HeadAboveCenter),
                mob.Position.Z);
            for (var h = 0; h < HeartCount; h++)
            {
                var fill = _fills[h];
                if (fill <= 0f) continue;
                var heartCenter = center + right * ((h - (HeartCount - 1) * 0.5f) * size);
                var width = size * fill;
                var fillCenter = heartCenter + right * ((width - size) * 0.5f);
                WriteQuad(quads * 4, fillCenter, width, right, up,
                    HeartUMin, HeartUMin + (HeartUMax - HeartUMin) * fill);
                quads++;
            }
        }
        DrawBatch(_heartTexture, quads);

        _gl.BindVertexArray(0);
        _gl.DepthMask(true);
    }

    private void WriteQuad(int baseVert, Vector3 center, float width, Vector3 right, Vector3 up,
        float uMin, float uMax)
    {
        var size = WorldGenerationConfig.EntityHeartSizeBlocks;
        var halfW = width * 0.5f;
        var halfH = size * 0.5f;
        // V=0 is the image top (rows upload top-first), so the quad top takes V=0,
        // exactly like the HUD hearts.
        Span<Vector2> uvs = stackalloc Vector2[4];
        uvs[0] = new Vector2(uMin, 1f);
        uvs[1] = new Vector2(uMin, 0f);
        uvs[2] = new Vector2(uMax, 0f);
        uvs[3] = new Vector2(uMax, 1f);
        for (var c = 0; c < 4; c++)
        {
            var (sx, sy) = CornerSigns[c];
            _verts[baseVert + c] = new Vertex(
                center + right * (sx * halfW) + up * (sy * halfH),
                255, 255, 255, 255, uvs[c], 1f, 0f, Vector3.UnitY);
        }
    }

    private void DrawBatch(uint texture, int quads)
    {
        if (quads == 0) return;
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2DArray, texture);
        _gl.BufferSubData<Vertex>(BufferTargetARB.ArrayBuffer, 0,
            new ReadOnlySpan<Vertex>(_verts, 0, quads * 4));
        unsafe
        {
            _gl.DrawElements(PrimitiveType.Triangles, (uint)(quads * 6),
                DrawElementsType.UnsignedShort, null);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shader.Dispose();
        _gl.DeleteTexture(_heartTexture);
        _gl.DeleteTexture(_heartEmptyTexture);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
        GC.SuppressFinalize(this);
    }
}
