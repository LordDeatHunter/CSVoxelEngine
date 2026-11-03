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
/// <para>
/// The far map is cached: the far instance is driven by <see cref="UpdateReused"/>, which holds the
/// center and <see cref="ViewProj"/> until <see cref="FarShadowReuse"/> says to render, and the
/// renderer confirms each render with <see cref="MarkRendered"/>, so <see cref="ViewProj"/> is always
/// the matrix the map holds. The near cascade keeps the plain <see cref="Update"/> every frame.
/// </para>
/// </summary>
internal sealed class ShadowCascade(float radiusBlocks, int mapSize)
{
    /// <summary>How far ahead of the eye, as a fraction of the radius, the box is centered: most
    /// of what the player looks at is in front of them.</summary>
    public const float ForwardFraction = 0.35f;

    private Vector3 _center;
    private bool _centerValid;

    // Reuse bookkeeping, far cascade only: the pose the confirmed map was rendered from, the pose a
    // pending (not yet confirmed) render will hold, whether a confirmed map exists and its age.
    private FarShadowReuse.Pose _renderedPose;
    private FarShadowReuse.Pose _pendingPose;
    private bool _hasMap;
    private int _framesSinceRender;

    /// <summary>True when the last <see cref="UpdateReused"/> moved the box and the map must be
    /// re-rendered this frame; false while the cached map and its <see cref="ViewProj"/> hold.</summary>
    internal bool RenderThisFrame { get; private set; }

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
        var target = TargetOf(eye, viewDir);
        _center = _centerValid
            ? StepCenter(_center, target, sunDir, RadiusBlocks, MapSize)
            : target;
        _centerValid = true;
        ViewProj = LightViewProj(_center, sunDir, RadiusBlocks);
    }

    /// <summary>
    /// The far cascade's per-frame update: asks <see cref="FarShadowReuse.Decide"/> whether the cached
    /// map still holds. On <see cref="FarShadowReuse.Refresh.None"/> the center and
    /// <see cref="ViewProj"/> stay bit-identical and <see cref="RenderThisFrame"/> is false; on any
    /// other cause the box re-centers through <see cref="Update"/> (whole-texel steps toward the
    /// look-following target) and <see cref="RenderThisFrame"/> is true until the renderer confirms
    /// with <see cref="MarkRendered"/>. A render that is never confirmed leaves no map, so the next
    /// call reports <see cref="FarShadowReuse.Refresh.NoMap"/> and renders again.
    /// </summary>
    internal FarShadowReuse.Refresh UpdateReused(Vector3 eye, Vector3 viewDir, Vector3 keyLight, bool moonLit,
        int geometryVersion)
    {
        var frame = new FarShadowReuse.Frame(TargetOf(eye, viewDir), eye, keyLight, moonLit, geometryVersion);
        var age = _framesSinceRender + 1;
        var why = FarShadowReuse.Decide(_renderedPose, frame, _hasMap, age, RadiusBlocks, MapSize);
        if (why == FarShadowReuse.Refresh.None)
        {
            _framesSinceRender = age;
            RenderThisFrame = false;
            return why;
        }

        Update(eye, viewDir, keyLight);
        _pendingPose = new FarShadowReuse.Pose(_center, eye, keyLight, moonLit, geometryVersion);
        _hasMap = false;
        RenderThisFrame = true;
        return why;
    }

    /// <summary>Confirms that the render <see cref="UpdateReused"/> asked for this frame happened, so
    /// the map now holds the pending pose. Does nothing on a reuse frame.</summary>
    internal void MarkRendered()
    {
        if (!RenderThisFrame)
            return;
        _renderedPose = _pendingPose;
        _hasMap = true;
        _framesSinceRender = 0;
    }

    /// <summary>Forgets the cached map, so the next <see cref="UpdateReused"/> renders (a frame with no
    /// shadow pass, or a world torn down).</summary>
    internal void Invalidate()
    {
        _hasMap = false;
        RenderThisFrame = false;
    }

    /// <summary>The point the box wants to center on: <see cref="ForwardFraction"/> of the radius
    /// ahead of the eye along the view.</summary>
    internal Vector3 TargetOf(Vector3 eye, Vector3 viewDir) => eye + viewDir * (RadiusBlocks * ForwardFraction);

    /// <summary>
    /// The light's lateral axes in world space, which the center's whole-texel steps are counted in.
    /// Columns, not rows: <see cref="Vector3.Transform(Vector3, Matrix4x4)"/> treats a matrix as
    /// row-vector form, so the light camera's basis vectors in world space are the columns of its
    /// view rotation.
    /// </summary>
    internal static (Vector3 Right, Vector3 Up) LightAxes(Vector3 sunDir)
    {
        var rotation = Matrix4x4.CreateLookAt(Vector3.Zero, -sunDir, Vector3.UnitY);
        return (new Vector3(rotation.M11, rotation.M21, rotation.M31),
                new Vector3(rotation.M12, rotation.M22, rotation.M32));
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
        var (right, up) = LightAxes(sunDir);
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
