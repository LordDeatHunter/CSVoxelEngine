using System;
using System.Numerics;
using VoxelEngine.UI.Menus;

namespace VoxelEngine.Rendering;

/// <summary>
/// The GL-free receiver test behind the near shadow cascade's caster cull: which chunk casters can
/// write a near shadow texel that some consumer samples this frame. A caster that cannot is skipped,
/// and the skip is exact (pixel-identical), not an approximation.
/// <para>
/// <b>Only the near cascade is receiver-culled.</b> The far map is cached (PERF_TODO F3,
/// <see cref="FarShadowReuse"/>) and sampled for many frames after it was drawn, so a map exact
/// only for this frame's receiver hull would be stale for the next view: a far re-render draws
/// every caster in its full cascade box. The consumer list below still names every reader of units
/// 1 and 7; the far map's readers sample it through <c>uLightViewProjFar</c>, which carries the held
/// <see cref="ShadowCascade.ViewProj"/> the cached map was rendered with.
/// </para>
/// <para>
/// <b>Why a drop changes no pixel.</b> The shadow pass is depth-only with <c>LEQUAL</c> and no color
/// write, so every texel stores the minimum depth over the casters that cover its center, in any
/// draw order. A caster whose rasterized footprint covers no texel center that any sample's
/// bilinear 2x2 footprint touches leaves every sampled texel bit-identical when it is dropped.
/// </para>
/// <para>
/// <b>The consumers</b> (every shader sampling texture units 1 or 7) and the point set each one
/// samples at:
/// <list type="bullet">
/// <item>The world program (<c>test-fragment.glsl</c>'s <c>SampleShadow</c>): every chunk, far-LOD,
/// entity, water and held-item fragment of the main frame. Each is drawn through
/// <c>Camera.GetViewProjection</c> and its <c>fWorldPos</c> is the very (post-displacement) position
/// its <c>gl_Position</c> was built from, so after clipping it lies inside the camera's view pyramid.
/// The far map is read wherever the near one fades (<c>fadeN &lt; 1</c>), which includes points
/// inside the near box, so a receiver-culled cascade must take the whole pyramid (the cascade's own
/// ortho frustum test then clips it to its box).</item>
/// <item>The leaf backlight samples at <c>fWorldPos + uSunDir * reach</c> with <c>N = uSunDir</c> and
/// <c>ndl = 1</c>: no normal offset, and a step along the sun is pure light depth in the ortho light
/// view, so its light-space XY is the fragment's own.</item>
/// <item>The caustics call the same <c>SampleShadow</c> with the fragment's own normal.</item>
/// <item>The water mirror draws the world program through
/// <see cref="ReflectionCull.MirrorView"/> times the camera projection, so its receivers lie inside
/// the mirrored pyramid; it is unioned whenever <see cref="ReflectionCull.MirrorMayRender"/> holds
/// (the pass's own refresh decision comes after the shadow pass, and a reused mirror samples an old
/// texture, no current shadow map).</item>
/// <item>The underwater rays and the godrays march <c>p = uCameraPos + dir * t</c>,
/// <c>t &lt;= min(|T - uCameraPos|, uMaxDistance)</c>, where <c>T</c> is a depth-buffer point
/// unprojected through the camera's inverse view-projection and <c>uCameraPos</c> is
/// <c>Camera.RenderEye</c>. Every march point is a convex combination of <c>RenderEye</c> and
/// <c>T</c>, so it lies in the hull of <c>RenderEye</c> and the pyramid. The float unprojection of a
/// sky depth of 1 slides <c>T</c> a little along its view ray, so callers build the far corners at
/// twice <c>Camera.FarPlane</c>: a longer pyramid only contains the shorter one, and the cascade
/// boxes lie far inside either.</item>
/// </list>
/// Clouds read unit 8 (another map), particles and the UI read neither unit.
/// </para>
/// <para>
/// <b>The rule.</b> The ortho light projection is affine in x and y, so the projection of each
/// receiver hull (the eye, an extra point and the four far corners) is the 2D convex hull of the
/// projected points. A sample's texel centers lie within <see cref="ReceiverReachTexels"/> texels
/// (Chebyshev) of its receiver's projection. A caster's vertices lie in its box grown by
/// <see cref="CasterReachBlocks"/>, whose light-space rectangle grown by the receiver reach is tested
/// against each hull with an exact separating-axis test. Depth is never tested: the region is
/// unbounded toward and away from the sun.
/// </para>
/// <para>
/// <b>Invariant.</b> A new shader sampling either shadow map, or a world-program draw through any
/// other view (a portal, a preview), must add its own <see cref="AddViewPyramid"/> or call
/// <see cref="KeepAll"/>, or it would sample texels whose casters were dropped. Keep
/// <see cref="SwayReachBlocks"/> in step with <c>shadow-vertex.glsl</c>'s sway block and
/// <see cref="ReceiverReachTexels"/> with <c>test-fragment.glsl</c>'s <c>CascadeLookup</c> and
/// <c>SampleShadow</c> (see the shader-sync skill).
/// </para>
/// Render thread only; nothing is allocated per frame.
/// </summary>
internal sealed class ShadowCasterCull
{
    /// <summary>
    /// How far <c>shadow-vertex.glsl</c>'s leaf sway can move a vertex on any axis: X is
    /// <c>w1 * 0.055 + w2 * 0.028</c> (at most 0.083), Z is <c>w2 * 0.055 - w1 * 0.028</c> (at most
    /// 0.083), Y is <c>(w1 + w2) * 0.018</c> (at most 0.036), each plus the <c>uWind</c> lean (length
    /// <c>SkyConfig.WindLean</c>, X and Z only), all times the sway weight and the gust (each at most
    /// 1) and <c>uSwayStrength</c> (at most <see cref="SettingsRows.LeafSwayMax"/>). The shadow vertex
    /// applies no waves.
    /// </summary>
    internal const float SwayReachBlocks = (0.055f + 0.028f + SkyConfig.WindLean) * SettingsRows.LeafSwayMax;

    /// <summary>
    /// How far a caster's drawn vertices can reach past its recorded box: a multiblock anchor's
    /// model past its cell (<see cref="ReflectionCull.MeshOverhangBlocks"/>, applied on
    /// every side though it only reaches toward +X, +Y and +Z, which is conservative) plus the sway.
    /// </summary>
    internal const float CasterReachBlocks = ReflectionCull.MeshOverhangBlocks + SwayReachBlocks;

    /// <summary>
    /// One extra texel of slack for the float differences between this CPU projection, the GPU's own
    /// projection of receivers and casters, and the marches' unprojection of their targets.
    /// </summary>
    internal const float SafetyTexels = 1f;

    private const int MaxHulls = 2;
    private const int MaxPoints = 6;

    // The light view-projection's x and y rows and their absolute values (row-vector convention:
    // x' = M11 x + M21 y + M31 z + M41).
    private float _x1, _x2, _x3, _x4, _y1, _y2, _y3, _y4;
    private float _ax1, _ax2, _ax3, _ay1, _ay2, _ay3;
    private float _marginNdc;
    private float _casterReach;
    private int _hullCount;
    private bool _keepAll;

    private readonly float[] _px = new float[MaxPoints];
    private readonly float[] _py = new float[MaxPoints];
    private readonly float[] _chainX = new float[2 * MaxPoints];
    private readonly float[] _chainY = new float[2 * MaxPoints];

    // Per hull: its outward edge normals and offsets (inside: n . p <= d) and its xy bounds.
    private readonly float[] _nx = new float[MaxHulls * MaxPoints];
    private readonly float[] _ny = new float[MaxHulls * MaxPoints];
    private readonly float[] _d = new float[MaxHulls * MaxPoints];
    private readonly int[] _edgeCount = new int[MaxHulls];
    private readonly float[] _minX = new float[MaxHulls];
    private readonly float[] _maxX = new float[MaxHulls];
    private readonly float[] _minY = new float[MaxHulls];
    private readonly float[] _maxY = new float[MaxHulls];

    /// <summary>
    /// How many of a cascade's texels (Chebyshev, around a receiver's projection) one shadow sample
    /// can touch, mirroring <c>test-fragment.glsl</c>'s <c>CascadeLookup</c> and <c>SampleShadow</c>.
    /// <paramref name="pcfKernel"/> is whether the cascade runs the near cascade's four-tap kernel
    /// (only while <c>ShadowSoftnessTexels</c> is above 0, as the shader branches).
    /// </summary>
    internal static float ReceiverReachTexels(bool pcfKernel) =>
        1f // the hardware 2x2 bilinear compare reaches the texel centers within one texel
        + LightingConfig.ShadowNormalOffsetTexels // normal offset: unit N times (1 - ndl) <= 1 texels
        + (pcfKernel ? 0.5f * LightingConfig.ShadowSoftnessTexels : 0f) // the PCF diagonal taps
        + SafetyTexels;

    /// <summary>
    /// Starts a cascade's frame: its light view-projection and map size, how many texels a sample can
    /// reach (<see cref="ReceiverReachTexels"/>) and how far a caster's vertices reach past its box
    /// (<see cref="CasterReachBlocks"/>). Clears every receiver hull and any keep-all.
    /// </summary>
    internal void Begin(in Matrix4x4 lightViewProj, int mapSize, float receiverReachTexels, float casterReachBlocks)
    {
        _x1 = lightViewProj.M11; _x2 = lightViewProj.M21; _x3 = lightViewProj.M31; _x4 = lightViewProj.M41;
        _y1 = lightViewProj.M12; _y2 = lightViewProj.M22; _y3 = lightViewProj.M32; _y4 = lightViewProj.M42;
        _ax1 = MathF.Abs(_x1); _ax2 = MathF.Abs(_x2); _ax3 = MathF.Abs(_x3);
        _ay1 = MathF.Abs(_y1); _ay2 = MathF.Abs(_y2); _ay3 = MathF.Abs(_y3);
        _marginNdc = receiverReachTexels * 2f / mapSize;
        _casterReach = casterReachBlocks;
        _hullCount = 0;
        _keepAll = false;
    }

    /// <summary>
    /// Adds a view's receivers: the pyramid from the eye of the rigid look-at
    /// <paramref name="view"/> out to the four corners at <paramref name="farDistance"/> of the
    /// symmetric perspective <paramref name="projection"/>, plus <paramref name="extraPoint"/> (the
    /// marches' origin). A degenerate or non-finite pyramid makes the cascade keep every caster.
    /// </summary>
    internal void AddViewPyramid(in Matrix4x4 view, in Matrix4x4 projection, float farDistance, Vector3 extraPoint)
    {
        if (_hullCount >= MaxHulls)
            throw new InvalidOperationException($"A shadow cascade takes at most {MaxHulls} receiver pyramids.");

        // CreateLookAt's columns are the camera basis in world space.
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        var back = new Vector3(view.M13, view.M23, view.M33);
        var eye = -(view.M41 * right + view.M42 * up + view.M43 * back);
        var center = eye - back * farDistance;
        var halfRight = right * (farDistance / projection.M11);
        var halfUp = up * (farDistance / projection.M22);

        Project(0, eye);
        Project(1, extraPoint);
        Project(2, center - halfRight - halfUp);
        Project(3, center + halfRight - halfUp);
        Project(4, center + halfRight + halfUp);
        Project(5, center - halfRight + halfUp);

        for (var i = 0; i < MaxPoints; i++)
        {
            if (float.IsFinite(_px[i]) && float.IsFinite(_py[i])) continue;
            _keepAll = true;
            return;
        }

        if (!BuildHull(_hullCount)) _keepAll = true;
        _hullCount++;
    }

    /// <summary>Makes this cascade keep every caster until the next <see cref="Begin"/>.</summary>
    internal void KeepAll() => _keepAll = true;

    /// <summary>
    /// Whether a caster drawn inside the box <paramref name="min"/>..<paramref name="max"/> (grown by
    /// the caster reach) could cover a texel any receiver samples. True when no receiver region is
    /// known.
    /// </summary>
    internal bool MayShadowReceivers(Vector3 min, Vector3 max)
    {
        if (_keepAll || _hullCount == 0) return true;

        var c = (min + max) * 0.5f;
        var h = (max - min) * 0.5f + new Vector3(_casterReach);
        var cx = _x1 * c.X + _x2 * c.Y + _x3 * c.Z + _x4;
        var cy = _y1 * c.X + _y2 * c.Y + _y3 * c.Z + _y4;
        var ex = _ax1 * h.X + _ax2 * h.Y + _ax3 * h.Z + _marginNdc;
        var ey = _ay1 * h.X + _ay2 * h.Y + _ay3 * h.Z + _marginNdc;

        for (var hull = 0; hull < _hullCount; hull++)
            if (Overlaps(hull, cx, cy, ex, ey)) return true;
        return false;
    }

    private bool Overlaps(int hull, float cx, float cy, float ex, float ey)
    {
        if (cx + ex < _minX[hull] || cx - ex > _maxX[hull]) return false;
        if (cy + ey < _minY[hull] || cy - ey > _maxY[hull]) return false;

        var start = hull * MaxPoints;
        for (var e = start; e < start + _edgeCount[hull]; e++)
        {
            var nearest = _nx[e] * cx + _ny[e] * cy - (MathF.Abs(_nx[e]) * ex + MathF.Abs(_ny[e]) * ey);
            if (nearest > _d[e]) return false;
        }

        return true;
    }

    private void Project(int i, Vector3 p)
    {
        _px[i] = _x1 * p.X + _x2 * p.Y + _x3 * p.Z + _x4;
        _py[i] = _y1 * p.X + _y2 * p.Y + _y3 * p.Z + _y4;
    }

    /// <summary>
    /// Andrew's monotone chain over the projected points into hull <paramref name="hull"/>'s edges,
    /// counter-clockwise, collinear and duplicate points dropped. False when fewer than three
    /// vertices remain, or more than there are points: on a nearly flat projection the float
    /// <see cref="Cross"/> can keep near-collinear points in both chains.
    /// <para>
    /// Each edge's offset and the bounds are then pushed out to every projected point, so the region
    /// contains all of them even where rounding left the chain slightly non-convex (on an exact hull
    /// this changes nothing).
    /// </para>
    /// </summary>
    private bool BuildHull(int hull)
    {
        // Insertion sort by x, then y.
        for (var i = 1; i < MaxPoints; i++)
        {
            var x = _px[i];
            var y = _py[i];
            var j = i - 1;
            for (; j >= 0 && (_px[j] > x || (_px[j] == x && _py[j] > y)); j--)
            {
                _px[j + 1] = _px[j];
                _py[j + 1] = _py[j];
            }

            _px[j + 1] = x;
            _py[j + 1] = y;
        }

        var k = 0;
        for (var i = 0; i < MaxPoints; i++) k = PushChain(k, 2, i);
        var lowerEnd = k + 1;
        for (var i = MaxPoints - 2; i >= 0; i--) k = PushChain(k, lowerEnd, i);

        var vertices = k - 1; // the chain ends back on its first point
        if (vertices < 3 || vertices > MaxPoints) return false;

        var start = hull * MaxPoints;
        for (var v = 0; v < vertices; v++)
        {
            float ax = _chainX[v], ay = _chainY[v], bx = _chainX[v + 1], by = _chainY[v + 1];
            var nx = by - ay;
            var ny = ax - bx;
            _nx[start + v] = nx;
            _ny[start + v] = ny;
            var d = nx * ax + ny * ay;
            for (var i = 0; i < MaxPoints; i++) d = MathF.Max(d, nx * _px[i] + ny * _py[i]);
            _d[start + v] = d;
        }

        _minX[hull] = _maxX[hull] = _px[0];
        _minY[hull] = _maxY[hull] = _py[0];
        for (var i = 1; i < MaxPoints; i++)
        {
            _minX[hull] = MathF.Min(_minX[hull], _px[i]);
            _maxX[hull] = MathF.Max(_maxX[hull], _px[i]);
            _minY[hull] = MathF.Min(_minY[hull], _py[i]);
            _maxY[hull] = MathF.Max(_maxY[hull], _py[i]);
        }

        _edgeCount[hull] = vertices;
        return true;
    }

    /// <summary>Pops chain points that do not turn left before point <paramref name="i"/>, keeping
    /// at least <paramref name="floor"/> - 1 of them, then appends it.</summary>
    private int PushChain(int k, int floor, int i)
    {
        while (k >= floor && Cross(k - 2, k - 1, i) <= 0f) k--;
        _chainX[k] = _px[i];
        _chainY[k] = _py[i];
        return k + 1;
    }

    private float Cross(int a, int b, int p) =>
        (_chainX[b] - _chainX[a]) * (_py[p] - _chainY[a]) - (_chainY[b] - _chainY[a]) * (_px[p] - _chainX[a]);
}
