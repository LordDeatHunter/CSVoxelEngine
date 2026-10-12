using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace VoxelEngine.Rendering;

/// <summary>
/// One directional shadow cascade: an orthographic box of half-extent <see cref="RadiusBlocks"/>
/// looking down the sun direction, its depth-only FBO and <c>sampler2DShadow</c> texture, and the
/// box center it carries forward frame to frame through <see cref="StepCenter"/>. Each cascade owns
/// its own center, so two cascades can never share (or corrupt) one another's texel lattice.
/// The constructor and <see cref="Update"/> are GL-free; only <see cref="Create"/> and
/// <see cref="Dispose"/> touch GL, and those only on the main thread.
/// </summary>
internal sealed class ShadowCascade(float radiusBlocks, int mapSize)
{
    /// <summary>How far ahead of the eye, as a fraction of the radius, the box is centered: most
    /// of what the player looks at is in front of them.</summary>
    public const float ForwardFraction = 0.35f;

    private Vector3 _center;
    private bool _centerValid;

    public float RadiusBlocks { get; } = radiusBlocks;
    public int MapSize { get; } = mapSize;
    public uint Fbo { get; private set; }
    public uint DepthTex { get; private set; }
    public Matrix4x4 ViewProj { get; private set; } = Matrix4x4.Identity;

    /// <summary>The current, texel-stepped box center (for tests).</summary>
    internal Vector3 Center => _center;

    /// <summary>World size of one shadow texel across the box.</summary>
    public float WorldTexel => 2f * RadiusBlocks / MapSize;

    /// <summary>
    /// World blocks -> uvz.z: the ortho box's 4r-block depth range is halved into 0.5 of the
    /// uvz range, so one whole uvz unit is 8r blocks.
    /// </summary>
    public float BiasScale => 1f / (8f * RadiusBlocks);

    /// <summary>
    /// Allocates the cascade's shadow map: one depth texture, no color attachment. It is sampled
    /// as a <c>sampler2DShadow</c>, so the compare mode does the depth test in hardware and Linear
    /// filtering gives free 2x2 bilinear PCF on top of the shader's own kernel. Outside the ortho
    /// box the border color reads as depth 1 (farthest), i.e. fully lit. Leaves this FBO and the
    /// texture bound; the caller rebinds the scene target and resets its unit tracking.
    /// </summary>
    public void Create(GL gl)
    {
        var size = (uint)MapSize;

        DepthTex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, DepthTex);
        unsafe
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.DepthComponent24,
                size, size, 0, PixelFormat.DepthComponent, PixelType.Float, null);
        }

        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToBorder);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToBorder);
        Span<float> border = [1f, 1f, 1f, 1f];
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBorderColor, border);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode,
            (int)GLEnum.CompareRefToTexture);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)GLEnum.Lequal);

        Fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, Fbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, DepthTex, 0);
        gl.DrawBuffer(DrawBufferMode.None);
        gl.ReadBuffer(ReadBufferMode.None);

        var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new Exception($"Shadow framebuffer ({MapSize}x{MapSize}) incomplete: {status}");
    }

    /// <summary>
    /// Builds this frame's light-space matrix: the box centered a little ahead of the eye and
    /// looking down the sun direction. <see cref="StepCenter"/> carries that center forward in
    /// whole shadow texels, or the sub-texel drift as the player walks makes every shadow edge crawl.
    /// </summary>
    public void Update(Vector3 eye, Vector3 viewDir, Vector3 sunDir)
    {
        var target = eye + viewDir * (RadiusBlocks * ForwardFraction);
        _center = _centerValid
            ? StepCenter(_center, target, sunDir, RadiusBlocks, MapSize)
            : target;
        _centerValid = true;
        ViewProj = LightViewProj(_center, sunDir, RadiusBlocks);
    }

    /// <summary>The orthographic light view-projection of a box of half-extent
    /// <paramref name="radius"/> centered on <paramref name="center"/>.</summary>
    internal static Matrix4x4 LightViewProj(Vector3 center, Vector3 sunDir, float radius)
    {
        var d = 2.5f * radius;
        var lightView = Matrix4x4.CreateLookAt(center + sunDir * d, center, Vector3.UnitY);
        // The sun's 0.35 Z tilt keeps it more than 19 degrees off vertical, so UnitY is
        // never parallel to the view direction and the look-at stays well defined
        var lightProj = Matrix4x4.CreateOrthographic(2f * radius, 2f * radius, d - 2f * radius, d + 2f * radius);
        return lightView * lightProj;
    }

    /// <summary>
    /// Advances a shadow box center toward the point this frame wants to center on, so the light
    /// view can be rebuilt around it. The center is a running value rather than a fresh function of
    /// the desired point, and that is the whole point: it follows the desired point in whole
    /// shadow-texel steps along the light's lateral axes and tracks its depth exactly.
    /// <para>
    /// Quantizing the movement (not the position) is what keeps the map still. Whole-texel lateral
    /// steps are invisible - geometry and lookup shift by the same integer count of texels - while
    /// anything with a fractional texel in it resamples the map and shimmers. Deriving the snapped
    /// center from scratch each frame instead compares the point against a lattice that rotates
    /// with the sun, so the "nearest" lattice vertex hops by a fractional texel every frame and
    /// every shadow edge twitches at the frame rate; the further the camera sits from the world
    /// origin, the faster the lattice sweeps past it and the worse it gets. Measuring the move from
    /// the previous center sidesteps that entirely: a camera that has not moved asks for a zero
    /// move, so the projection is identical and nothing twitches, however the sun turns.
    /// </para>
    /// </summary>
    internal static Vector3 StepCenter(Vector3 previous, Vector3 desired, Vector3 sunDir,
        float radiusBlocks, int mapSize)
    {
        var texel = 2f * radiusBlocks / mapSize;
        var delta = desired - previous;
        var along = Vector3.Dot(delta, sunDir);
        var lateral = delta - sunDir * along;
        // Columns, not rows: Vector3.Transform treats a matrix as row-vector form, so the camera's
        // basis vectors in world space are the columns of the view rotation.
        var rotation = Matrix4x4.CreateLookAt(Vector3.Zero, -sunDir, Vector3.UnitY);
        var right = new Vector3(rotation.M11, rotation.M21, rotation.M31);
        var up = new Vector3(rotation.M12, rotation.M22, rotation.M32);
        var stepsRight = MathF.Round(Vector3.Dot(lateral, right) / texel);
        var stepsUp = MathF.Round(Vector3.Dot(lateral, up) / texel);
        return previous + sunDir * along + right * (stepsRight * texel) + up * (stepsUp * texel);
    }

    /// <summary>Deletes the FBO and the depth texture.</summary>
    public void Dispose(GL gl)
    {
        gl.DeleteFramebuffer(Fbo);
        gl.DeleteTexture(DepthTex);
    }
}
