using System;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Chunks;

namespace VoxelEngine.Rendering;

/// <summary>
/// The GL side of the opaque chunk pass's occlusion cull (<see cref="TerrainPasses.RenderWorld"/>):
/// between two segments of the front-to-back run it builds a max-depth pyramid of the depth already
/// in the frame (<c>hiz-build-compute.glsl</c>, twin <see cref="HizPyramid"/>) and zeroes, in place
/// in the <see cref="Geometry.ChunkDrawBatch"/> ring, the <c>InstanceCount</c> of every later command
/// whose chunk lies strictly behind it (<c>hiz-cull-compute.glsl</c>, twin <see cref="HizOcclusion"/>).
/// A culled chunk has no fragment that could pass the depth test, and every chunk that is drawn keeps
/// its place in the order, so the frame is pixel-identical (see <see cref="HizOcclusion"/>).
/// <para>
/// <b>Barriers.</b> The depth the earlier segments rendered is read with <c>texelFetch</c> by the next
/// dispatch with no barrier of its own (framebuffer writes are ordered before later commands' texture
/// fetches). Every pyramid level is written with <c>imageStore</c> and read by the next level with
/// <c>imageLoad</c> and by the cull with <c>texelFetch</c>, so each level dispatch is followed by
/// <c>SHADER_IMAGE_ACCESS | TEXTURE_FETCH</c>, the last level included: without it the cull could
/// fetch the previous boundary's texels and cull a chunk that is in view. The cull's SSBO writes into
/// the indirect buffer are followed by <c>COMMAND</c>, so the next multi-draw reads them.
/// </para>
/// <para>
/// <b>Bindings.</b> SSBO binding 0 stays the ChunkInfo ring (the cull reads each command's origin
/// there), 1 the tile materials; the cull binds the indirect buffer to binding 2 on every call, since
/// <c>ChunkDrawBatch.Grow</c> renames it. Texture units <see cref="RenderManager.HizDepthUnit"/> and
/// <see cref="RenderManager.HizPyramidUnit"/> and image units 0 and 1 are this class's alone; the depth
/// unit is unbound again before the next segment draws into that depth. Both units are above
/// <see cref="TextureUnitCache.TrackedUnits"/>, so <see cref="TextureUnits.BindUnit"/> always rebinds
/// them, and deleting the pyramid on a resize cannot stale the tracked set.
/// </para>
/// Render thread only; nothing is allocated per frame.
/// </summary>
internal sealed class ChunkOcclusionCuller : IDisposable
{
    private const int BuildGroupSize = 8;
    private const int CullGroupSize = 64;
    private const uint SrcImageUnit = 0;
    private const uint DstImageUnit = 1;
    private const uint CommandSsboBinding = 2;

    private readonly GL _gl;
    private readonly RenderManager _owner;
    private readonly Shader _buildFromDepth;
    private readonly Shader _buildFromLevel;
    private readonly Shader _cull;

    private readonly int _locDepthSrcSize, _locDepthDstSize, _locLevelSrcSize, _locLevelDstSize;
    private readonly int _locFirst, _locCount, _locViewProj, _locViewport, _locLevels;

    private uint _pyramid;
    private int _frameWidth, _frameHeight;
    private int _padWidth, _padHeight, _levels;

    internal ChunkOcclusionCuller(GL gl, RenderManager owner)
    {
        _gl = gl;
        _owner = owner;

        var buildSource = FileUtils.ReadFile("assets/shaders/hiz-build-compute.glsl");
        _buildFromDepth = Shader.CreateCompute(gl, buildSource, "#define FROM_DEPTH");
        _buildFromLevel = Shader.CreateCompute(gl, buildSource);
        _cull = Shader.CreateCompute(gl, FileUtils.ReadFile("assets/shaders/hiz-cull-compute.glsl"));

        _buildFromDepth.Use();
        _buildFromDepth.SetUniform("uDepth", RenderManager.HizDepthUnit);
        _locDepthSrcSize = _buildFromDepth.GetUniformLocation("uSrcSize");
        _locDepthDstSize = _buildFromDepth.GetUniformLocation("uDstSize");

        _locLevelSrcSize = _buildFromLevel.GetUniformLocation("uSrcSize");
        _locLevelDstSize = _buildFromLevel.GetUniformLocation("uDstSize");

        // Everything the twin fixes is uploaded once; uniform values live in the program.
        _cull.Use();
        _cull.SetUniform("uHiz", RenderManager.HizPyramidUnit);
        _cull.SetUniform("uReach", HizOcclusion.ReachBlocks);
        _cull.SetUniform("uChunkSize", (float)Chunk.Size);
        _cull.SetUniform("uDepthMargin", HizOcclusion.DepthMargin);
        _cull.SetUniform("uMinClipW", HizOcclusion.MinClipW);
        _cull.SetUniform("uScreenClamp", HizOcclusion.ScreenClampPixels);
        _cull.SetUniform("uSafety", HizOcclusion.SafetyTexels);
        _locFirst = _cull.GetUniformLocation("uFirst");
        _locCount = _cull.GetUniformLocation("uCount");
        _locViewProj = _cull.GetUniformLocation("uViewProj");
        _locViewport = _cull.GetUniformLocation("uViewport");
        _locLevels = _cull.GetUniformLocation("uLevels");
    }

    /// <summary>
    /// Builds the pyramid from the scene depth as it stands and zeroes the <c>InstanceCount</c> of
    /// every command of ring indices <paramref name="first"/> .. <paramref name="first"/> +
    /// <paramref name="count"/> whose chunk it hides. <paramref name="viewProj"/> must be the matrix
    /// the chunk program draws them with. Leaves the cull program bound, so the caller re-binds its
    /// own before drawing.
    /// </summary>
    internal void Cull(in Matrix4x4 viewProj, uint commandBuffer, int first, int count)
    {
        var frame = _owner.Targets.ViewportSize;
        if (count <= 0 || frame.X <= 0 || frame.Y <= 0) return;

        EnsurePyramid(frame.X, frame.Y);
        BuildPyramid();

        _cull.Use();
        _cull.SetUniform(_locFirst, first);
        _cull.SetUniform(_locCount, count);
        _cull.SetUniform(_locViewProj, viewProj);
        _gl.Uniform2(_locViewport, _frameWidth, _frameHeight);
        _cull.SetUniform(_locLevels, _levels);
        _owner.Units.BindUnit(RenderManager.HizPyramidUnit, TextureTarget.Texture2D, _pyramid);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, CommandSsboBinding, commandBuffer);

        _gl.DispatchCompute((uint)((count + CullGroupSize - 1) / CullGroupSize), 1, 1);
        _gl.MemoryBarrier(MemoryBarrierMask.CommandBarrierBit);

        // BindWorldTextures leaves unit 0 active, and later passes rely on that.
        _owner.Units.ActiveUnit(0);
    }

    private void BuildPyramid()
    {
        _buildFromDepth.Use();
        _gl.Uniform2(_locDepthSrcSize, _frameWidth, _frameHeight);
        _gl.Uniform2(_locDepthDstSize, _padWidth, _padHeight);
        _owner.Units.BindUnit(RenderManager.HizDepthUnit, TextureTarget.Texture2D, _owner.Targets.HdrDepthTex);
        _gl.BindImageTexture(DstImageUnit, _pyramid, 0, false, 0, BufferAccessARB.WriteOnly, InternalFormat.R32f);
        Dispatch(_padWidth, _padHeight);
        // The next segment draws into this depth; it must not stay bound to a unit meanwhile.
        _owner.Units.BindUnit(RenderManager.HizDepthUnit, TextureTarget.Texture2D, 0);

        _buildFromLevel.Use();
        for (var level = 1; level < _levels; level++)
        {
            int srcW = LevelSize(_padWidth, level - 1), srcH = LevelSize(_padHeight, level - 1);
            int dstW = LevelSize(_padWidth, level), dstH = LevelSize(_padHeight, level);
            _gl.Uniform2(_locLevelSrcSize, srcW, srcH);
            _gl.Uniform2(_locLevelDstSize, dstW, dstH);
            _gl.BindImageTexture(SrcImageUnit, _pyramid, level - 1, false, 0, BufferAccessARB.ReadOnly, InternalFormat.R32f);
            _gl.BindImageTexture(DstImageUnit, _pyramid, level, false, 0, BufferAccessARB.WriteOnly, InternalFormat.R32f);
            Dispatch(dstW, dstH);
        }
    }

    private void Dispatch(int width, int height)
    {
        _gl.DispatchCompute((uint)((width + BuildGroupSize - 1) / BuildGroupSize),
            (uint)((height + BuildGroupSize - 1) / BuildGroupSize), 1);
        _gl.MemoryBarrier(MemoryBarrierMask.ShaderImageAccessBarrierBit | MemoryBarrierMask.TextureFetchBarrierBit);
    }

    private static int LevelSize(int padded, int level) => Math.Max(1, padded >> level);

    /// <summary>(Re)creates the pyramid when the frame size changed: an immutable R32F mip chain of
    /// <see cref="HizPyramid.PaddedSize"/> per axis, every level of it, so <c>texelFetch</c> at any
    /// level reads a complete texture.</summary>
    private void EnsurePyramid(int width, int height)
    {
        if (_pyramid != 0 && width == _frameWidth && height == _frameHeight) return;

        if (_pyramid != 0) _gl.DeleteTexture(_pyramid);

        _frameWidth = width;
        _frameHeight = height;
        _padWidth = HizPyramid.PaddedSize(width);
        _padHeight = HizPyramid.PaddedSize(height);
        _levels = HizPyramid.LevelCount(_padWidth, _padHeight);

        _gl.CreateTextures(TextureTarget.Texture2D, 1, out _pyramid);
        _gl.TextureStorage2D(_pyramid, (uint)_levels, SizedInternalFormat.R32f, (uint)_padWidth, (uint)_padHeight);
        _gl.TextureParameter(_pyramid, TextureParameterName.TextureMinFilter, (int)GLEnum.NearestMipmapNearest);
        _gl.TextureParameter(_pyramid, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TextureParameter(_pyramid, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TextureParameter(_pyramid, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
    }

    public void Dispose()
    {
        _buildFromDepth.Dispose();
        _buildFromLevel.Dispose();
        _cull.Dispose();
        if (_pyramid != 0) _gl.DeleteTexture(_pyramid);
        _pyramid = 0;
    }
}
