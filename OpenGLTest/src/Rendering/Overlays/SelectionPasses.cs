using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Chunks.Sources;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Data;
using VoxelEngine.Effects.Climate;
using VoxelEngine.Graphics;
using VoxelEngine.Interaction;
using VoxelEngine.Particles;
using VoxelEngine.Player;

namespace VoxelEngine.Rendering.Overlays;

/// <summary>
/// The targeted block's overlays in the selection pass: the mining and placement pop (the
/// block's hitboxes redrawn grown out of the terrain), the breathing selection outline with its
/// display-space face highlight, and the block-breaking crack overlay. Owns the outline, face
/// and pop buffers it draws from; the programs and the crack sprite stay owned by
/// <see cref="RenderManager"/>. Render thread only.
/// </summary>
internal sealed class SelectionPasses : IDisposable
{
    private readonly GL _gl;
    private readonly RenderManager _owner;

    private uint outlineVao;
    private uint outlineVbo;
    private uint outlineEbo;
    /// <summary>Reused world-space corners of the outline beams, rebuilt per hitbox.</summary>
    private readonly Vector3[] _outlineVertices = new Vector3[SelectionOutlineMesh.VertexCount];
    private uint faceVao;
    private uint faceVbo;

    // The mining pop's streaming box batch (see PopBoxEmitter): one reusable VAO/VBO with a
    // static index prefix per box, refilled while a block is being mined. Tree wood tops
    // out at seven boxes (a node plus a tube on every face), so eight leaves headroom and the buffers
    // grow if a future block ever brings more.
    private const int MaxPopBoxes = 8;
    private uint _popVao;
    private uint _popVbo;
    private uint _popEbo;
    private int _popBoxCapacity = MaxPopBoxes;
    private readonly List<Vertex> _popVerts = new(MaxPopBoxes * PopBoxEmitter.MaxQuadsPerBox * 4);
    private readonly List<ushort> _popIndices = new(MaxPopBoxes * PopBoxEmitter.MaxQuadsPerBox * 6);

    // The selection outline's color (sRGB-authored, decoded once)
    private static readonly Vector3 SelectionOutlineColor = ColorSpace.SrgbToLinear(new Vector3(0.8f, 0.8f, 0.85f));
    // Left sRGB: the face highlight is mixed in display space (see RenderBlockSelection)
    private static readonly Vector3 SelectionFaceColorSrgb = new(0.7f, 0.7f, 0.75f);

    private FrameLighting Lighting => _owner.Lighting;
    private TextureUnits Units => _owner.Units;
    private RenderTargets Targets => _owner.Targets;

    internal SelectionPasses(GL gl, RenderManager owner)
    {
        _gl = gl;
        _owner = owner;
        CreateSelectionGeometry();
        CreatePopGeometry();
    }

    /// <summary>
    /// Draws each of the block's hitboxes as a box sampling its crop of the block's face
    /// tiles (see <see cref="PopBoxEmitter"/>), so the mining pop starts from the block's real
    /// shape with the chunk mesh's texel density: at grow 0 the boxes sit exactly inside their
    /// outlines, and a hit grows every axis by the same absolute amount, so tiny blocks jump
    /// as visibly as full cubes. Grown, the boxes enclose the block baked into the chunk mesh,
    /// so the block appears to jump out of the terrain.
    /// </summary>
    internal void RenderBlockPop(Camera camera, Vector3 blockPosition, byte blockId, float grow,
        BlockHitbox[]? boxes = null)
    {
        var type = BlockType.ById[blockId];
        if (type == null) return;

        boxes ??= BlockType.ById[blockId]?.Hitboxes ?? [BlockHitbox.FullCube];
        if (boxes.Length > _popBoxCapacity)
            GrowPopBuffers(boxes.Length);

        _popVerts.Clear();
        _popIndices.Clear();
        ushort indexOffset = 0;
        var grownVec = new Vector3(grow);
        foreach (var box in boxes)
        {
            var half = (box.Size + grownVec) * 0.5f;
            var grown = new BlockHitbox(box.Center - half, box.Center + half);
            PopBoxEmitter.EmitBox(_popVerts, _popIndices, ref indexOffset,
                grown, box, blockPosition, type.FaceTiles, type.BiomeTint);
        }
        if (_popVerts.Count == 0) return;

        _owner.WorldShader.Use();
        _owner.BindWorldTextures();

        _gl.BindVertexArray(_popVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _popVbo);
        _gl.BufferSubData<Vertex>(BufferTargetARB.ArrayBuffer, 0,
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_popVerts));
        var matrix = Matrix4x4.Identity;
        _owner.SetDrawUniforms(matrix, matrix * camera.GetViewProjection(), 1f,
            _owner.LightAt(blockPosition + new Vector3(0.5f)));
        // A biome-tinted block's pop takes the field at the mined cell, so it matches the
        // terrain it jumps out of (the emitter flags the tinted quads and keeps the rgb shade only).
        // The field comes from the world's column source (a client's is streamed, and a column
        // that has not arrived draws untinted); no world (the item debugger) means worldgen.
        var columns = _owner.LightSource?.ColumnSource ?? WorldgenColumnSource.Instance;
        _owner.WorldShader.SetUniform(_owner.LocBiomeTint, type.BiomeTint is { } bt
            && BiomeTint.TryCellTint(columns, bt.Palette, (int)MathF.Floor(blockPosition.X),
                (int)MathF.Floor(blockPosition.Z), out var cellTint)
            ? cellTint
            : Vector3.One);
        unsafe
        {
            _gl.DrawElements(PrimitiveType.Triangles, (uint)_popIndices.Count,
                DrawElementsType.UnsignedShort, null);
        }
        // Shader.SetUniform writes to the bound program, so this reset is only valid here,
        // with worldShader still bound; it keeps uBiomeTint white unless a tinted draw is in
        // flight, for the plain draws that follow (toppling tree, drops, entities)
        _owner.WorldShader.SetUniform(_owner.LocBiomeTint, Vector3.One);
        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// Draws the targeted block's outline and face highlight. Each of the block's hitboxes gets
    /// its own outline; the highlight lands on the hit box's face. <paramref name="grow"/> is the
    /// block's current pop growth so the highlight tracks it while mining. The outline breathes
    /// (<see cref="SelectionPulse"/>) unless <paramref name="mining"/>, which holds it steady so the
    /// crack overlay stays readable.
    /// </summary>
    internal void RenderBlockSelection(Camera camera, Vector3 blockPosition, Vector3 hitNormal, float grow = 0f,
        byte blockId = 0, int hitBoxIndex = 0, BlockHitbox[]? boxes = null, bool mining = false)
    {
        _owner.SelectionShader.Use();

        var viewProj = camera.GetViewProjection();
        _gl.DepthMask(false);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        boxes ??= BlockType.ById[blockId]?.Hitboxes ?? [BlockHitbox.FullCube];
        // sRGB-authored outline colors, decoded: the selection draws into the linear HDR target
        _owner.SelectionShader.SetUniform("uColor", SelectionOutlineColor);
        // Full at the top of each breath: SelectionPulse dips it to SelectionPulseMinOpacity
        _owner.SelectionShader.SetUniform("uOpacity", 1f);
        _owner.SelectionShader.SetUniform("uDisplayMix", 0);
        _owner.SelectionShader.SetUniform("uTime", Lighting.Time);
        _owner.SelectionShader.SetUniform("uPulseDepth", SelectionPulse.Depth(mining));
        _owner.SelectionShader.SetUniform("uPulsePeriod", HudConfig.SelectionPulsePeriodSeconds);

        // The outline is twelve edge beams (SelectionOutlineMesh) rather than GL lines, since
        // LineWidth is unreliable in a core profile. The vertices are already in world space.
        _gl.BindVertexArray(outlineVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, outlineVbo);
        _owner.SelectionShader.SetUniform("uMVP", viewProj);
        // Back faces culled: a beam is a convex box, so each pixel then blends exactly one face of
        // it. Without that the front and back faces both blend, and an alpha a reads as 1-(1-a)^2,
        // which squashed the breath from 70..100 % into ~91..100 %.
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);
        // The outline inflates a hair past the grown box so it never z-fights the block
        var outlinePad = new Vector3(0.01f + grow);
        foreach (var box in boxes)
        {
            var half = (box.Size + outlinePad) * 0.5f;
            var center = blockPosition + box.Center;
            // Thickness grows with distance so the beam keeps roughly the same on-screen width
            var thickness = MathF.Max(HudConfig.SelectionOutlineMinThickness,
                HudConfig.SelectionOutlinePixelScale * Vector3.Distance(camera.Position, center));
            SelectionOutlineMesh.BuildVertices(center - half, center + half, thickness, _outlineVertices);
            _gl.BufferSubData<Vector3>(BufferTargetARB.ArrayBuffer, 0, _outlineVertices);
            unsafe
            {
                _gl.DrawElements(PrimitiveType.Triangles, SelectionOutlineMesh.IndexCount,
                    DrawElementsType.UnsignedInt, null);
            }
        }
        _gl.Disable(EnableCap.CullFace);

        var hitBox = boxes[Math.Clamp(hitBoxIndex, 0, boxes.Length - 1)];
        var faceIndex = FaceIndexFromNormal(hitNormal);
        if (faceIndex >= 0)
        {
            var hitCenter = blockPosition + hitBox.Center;
            var faceModel = Matrix4x4.CreateTranslation(new Vector3(-0.5f))
                            * Matrix4x4.CreateScale(hitBox.Size + new Vector3(grow))
                            * Matrix4x4.CreateTranslation(hitCenter + hitNormal * 0.003f);
            _owner.SelectionShader.SetUniform("uMVP", faceModel * viewProj);
            // The face highlight mixes in display space (selection-fragment.glsl), so its color
            // stays sRGB and it reads the very target it writes: one quad touches each texel
            // once, which is what glTextureBarrier makes legal. Blending is off for that draw.
            _owner.SelectionShader.SetUniform("uColor", SelectionFaceColorSrgb);
            _owner.SelectionShader.SetUniform("uOpacity", 0.15f);
            _owner.SelectionShader.SetUniform("uDisplayMix", 1);
            _owner.SelectionShader.SetUniform("uScene", RenderManager.SceneReadUnit);
            Units.BindUnit(RenderManager.SceneReadUnit, TextureTarget.Texture2D, Targets.HdrColorTex);
            _gl.TextureBarrier();
            _gl.Disable(EnableCap.Blend);

            _gl.BindVertexArray(faceVao);
            _gl.DrawArrays(GLEnum.TriangleFan, faceIndex * 4, 4);

            _gl.Enable(EnableCap.Blend);
            // Never leave the render target sitting on a sampler unit
            Units.BindUnit(RenderManager.SceneReadUnit, TextureTarget.Texture2D, 0);
            Units.ActiveUnit(0);
        }

        _gl.BindVertexArray(0);
        _gl.DepthMask(true);
    }

    /// <summary>
    /// Draws the block-breaking crack overlay on every face of each of the block's hitboxes:
    /// the sprite steps through <see cref="PlayerConfig.BreakOverlayFrames"/> stacked
    /// frames as progress goes 0 -> 1, the first frame being blank (see break-fragment.glsl).
    /// <paramref name="grow"/> is the block's current pop growth so the overlay tracks it while
    /// mining.
    /// </summary>
    internal void RenderBlockBreaking(Camera camera, Vector3 blockPosition, float progress, float grow = 0f,
        byte blockId = 0, BlockHitbox[]? boxes = null)
    {
        if (progress <= 0f) return;

        _owner.BreakShader.Use();
        _gl.DepthMask(false);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        boxes ??= BlockType.ById[blockId]?.Hitboxes ?? [BlockHitbox.FullCube];
        var frame = Math.Clamp((int)(progress * PlayerConfig.BreakOverlayFrames), 0,
            PlayerConfig.BreakOverlayFrames - 1);
        _owner.BreakShader.SetUniform("uFrame", (float)frame);
        Units.BindUnit(0, TextureTarget.Texture2DArray, _owner.BreakProgressTexture);

        _gl.BindVertexArray(faceVao);
        // Inflated slightly past the grown box so the overlay never z-fights the block.
        // UVs are cropped to the face's cell-space footprint so thin boxes sample a strip
        // of the crack sprite at full-texel density instead of a squashed whole frame.
        var pad = new Vector3(0.004f + grow);
        var viewProj = camera.GetViewProjection();
        foreach (var box in boxes)
        {
            var model = Matrix4x4.CreateTranslation(new Vector3(-0.5f))
                        * Matrix4x4.CreateScale(box.Size + pad)
                        * Matrix4x4.CreateTranslation(blockPosition + box.Center);
            _owner.BreakShader.SetUniform("uMVP", model * viewProj);
            for (var face = 0; face < 6; face++)
            {
                var (offset, scale) = BreakOverlayUv.ForFace(face, box);
                _owner.BreakShader.SetUniform("uUvOffset", offset);
                _owner.BreakShader.SetUniform("uUvScale", scale);
                _gl.DrawArrays(GLEnum.TriangleFan, face * 4, 4);
            }
        }

        _gl.BindVertexArray(0);
        _gl.DepthMask(true);
    }

    private void CreateSelectionGeometry()
    {
        // The outline's vertices are rewritten per hitbox each frame; its indices never change.
        Span<uint> outlineIndices = stackalloc uint[SelectionOutlineMesh.IndexCount];
        SelectionOutlineMesh.BuildIndices(outlineIndices);

        outlineVao = _gl.GenVertexArray();
        outlineVbo = _gl.GenBuffer();
        outlineEbo = _gl.GenBuffer();

        _gl.BindVertexArray(outlineVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, outlineVbo);
        unsafe
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(SelectionOutlineMesh.VertexCount * 3 * sizeof(float)), null, BufferUsageARB.DynamicDraw);
        }
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, outlineEbo);
        _gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, outlineIndices, BufferUsageARB.StaticDraw);
        unsafe
        {
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);
        }
        _gl.EnableVertexAttribArray(0);

        Span<float> faceVertices =
        [
            // Cube faces in Chunk.CubeFaces order, each a 4-vertex triangle fan, interleaved as
            // position.xyz + face-local uv. The two in-plane axes carry uv 0..1; the break
            // shader crops them to the hitbox face via uUvOffset/uUvScale, so a full cube
            // samples the whole sprite frame and a thin box a strip of it.
            0f, 0f, 0f, 0f, 0f,   1f, 0f, 0f, 1f, 0f,
            1f, 1f, 0f, 1f, 1f,   0f, 1f, 0f, 0f, 1f,
            1f, 0f, 1f, 1f, 0f,   0f, 0f, 1f, 0f, 0f,
            0f, 1f, 1f, 0f, 1f,   1f, 1f, 1f, 1f, 1f,
            0f, 0f, 1f, 1f, 0f,   0f, 0f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f, 1f,   0f, 1f, 1f, 1f, 1f,
            1f, 0f, 0f, 0f, 0f,   1f, 0f, 1f, 1f, 0f,
            1f, 1f, 1f, 1f, 1f,   1f, 1f, 0f, 0f, 1f,
            0f, 1f, 0f, 0f, 0f,   0f, 1f, 1f, 0f, 1f,
            1f, 1f, 1f, 1f, 1f,   1f, 1f, 0f, 1f, 0f,
            0f, 0f, 1f, 0f, 1f,   1f, 0f, 1f, 1f, 1f,
            1f, 0f, 0f, 1f, 0f,   0f, 0f, 0f, 0f, 0f
        ];

        faceVao = _gl.GenVertexArray();
        faceVbo = _gl.GenBuffer();
        _gl.BindVertexArray(faceVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, faceVbo);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, faceVertices, BufferUsageARB.StaticDraw);
        unsafe
        {
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 5 * sizeof(float), (void*)0);
            _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float),
                (void*)(3 * sizeof(float)));
        }
        _gl.EnableVertexAttribArray(0);
        _gl.EnableVertexAttribArray(1);
        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// The mining pop's streaming buffers: a dynamic VBO refilled with the hitbox boxes every
    /// frame while mining, and a static EBO holding the quad index pattern. Every quad the
    /// emitter writes is sequential (quad k uses vertices 4k..4k+3), so any quad count draws as
    /// a prefix of one pattern; both are sized for <see cref="PopBoxEmitter.MaxQuadsPerBox"/>
    /// quads per box, which a biome-tinted block (a base and a tinted quad per face) needs. The
    /// pattern is generated by running the real emitter over dummy boxes, so it can never
    /// drift from what <see cref="RenderBlockPop"/> streams.
    /// </summary>
    private void CreatePopGeometry()
    {
        _popVao = _gl.GenVertexArray();
        _popVbo = _gl.GenBuffer();
        _popEbo = _gl.GenBuffer();

        var maxQuads = _popBoxCapacity * PopBoxEmitter.MaxQuadsPerBox;
        _gl.BindVertexArray(_popVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _popVbo);
        _gl.BufferData<Vertex>(BufferTargetARB.ArrayBuffer,
            (nuint)(maxQuads * 4 * Vertex.Size),
            new Vertex[maxQuads * 4], BufferUsageARB.DynamicDraw);

        // A plain box emits six sequential quads, so maxQuads / 6 of them lay down exactly the
        // pattern a budget of MaxQuadsPerBox quads per box draws a prefix of
        var dummyVerts = new List<Vertex>(maxQuads * 4);
        var dummyIndices = new List<ushort>(maxQuads * 6);
        ushort offset = 0;
        for (var i = 0; i < maxQuads / 6; i++)
            PopBoxEmitter.EmitBox(dummyVerts, dummyIndices, ref offset,
                BlockHitbox.FullCube, BlockHitbox.FullCube, Vector3.Zero, new ushort[6]);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _popEbo);
        _gl.BufferData<ushort>(BufferTargetARB.ElementArrayBuffer,
            (nuint)(dummyIndices.Count * sizeof(ushort)),
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(dummyIndices),
            BufferUsageARB.StaticDraw);

        Mesh.ConfigureVertexAttribs(_gl);
        _gl.BindVertexArray(0);
    }

    private void GrowPopBuffers(int boxes)
    {
        _gl.DeleteBuffer(_popVbo);
        _gl.DeleteBuffer(_popEbo);
        _gl.DeleteVertexArray(_popVao);
        _popBoxCapacity = boxes * 2;
        CreatePopGeometry();
    }

    private static int FaceIndexFromNormal(Vector3 normal)
    {
        if (normal == new Vector3(0, 0, -1)) return 0;
        if (normal == new Vector3(0, 0, 1)) return 1;
        if (normal == new Vector3(-1, 0, 0)) return 2;
        if (normal == new Vector3(1, 0, 0)) return 3;
        if (normal == new Vector3(0, 1, 0)) return 4;
        if (normal == new Vector3(0, -1, 0)) return 5;
        return -1;
    }

    /// <summary>Frees the outline, face and pop buffers, in the order the renderer always
    /// freed them.</summary>
    public void Dispose()
    {
        _gl.DeleteBuffer(outlineVbo);
        _gl.DeleteBuffer(outlineEbo);
        _gl.DeleteVertexArray(outlineVao);
        _gl.DeleteBuffer(faceVbo);
        _gl.DeleteVertexArray(faceVao);
        _gl.DeleteBuffer(_popVbo);
        _gl.DeleteBuffer(_popEbo);
        _gl.DeleteVertexArray(_popVao);
    }
}
