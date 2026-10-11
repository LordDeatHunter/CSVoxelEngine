#version 460 core
// The sky dome: one full-screen triangle drawn first each frame (and into the water mirror),
// depth off, so everything after it occludes it. The view ray is rebuilt from the inverse of a
// translation-free view-projection, graded from the horizon haze band up to the zenith by
// elevation, with a warm azimuthal glow toward the sun while it is low. The world and cloud
// fog sample the very same SkySample in their view direction, so a fully fogged fragment is
// exactly the colour of the dome behind it. The sun disc is added on top by sun-fragment.glsl.
// The stars are added in main only, after SkySample, so the fog and SkySample's twins never see
// them: a fogged fragment melts into the starless dome colour, which near the horizon (where fog
// is thick) is what the dome shows anyway, since the stars fade out into the haze.
in vec2 fUv;

uniform mat4 uInvViewRotProj; // inverse(viewRotation * projection), no translation
uniform vec3 uSkyZenith;
uniform vec3 uSkyHorizon;
uniform vec3 uSkyGlow;        // dusk-scaled on the CPU: zero at noon and at night
uniform vec4 uSkyShape;       // x horizon band, y haze width, z haze strength, w glow exponent
uniform vec3 uSunDir;
uniform vec3 uSkyTint;        // what the dome fades toward in lava
uniform float uSkyTintAmount;

// Stars (StarField is the C# twin). Per frame: how visible they are (0 by day), the sky's turn
// with the sun (cos, sin), and the cos of the outer and inner radius of the starless disc
// around the sun. The rest is WorldGenerationConfig tuning, set once by SkyRenderer.
uniform float uStarVisibility;
uniform vec2 uStarSpin;
uniform vec2 uStarSunMask;
uniform float uStarCells;
uniform float uStarDensity;
uniform float uStarMargin;
uniform float uStarBrightness;
uniform float uStarFaintest;
uniform float uStarCurve;
uniform float uStarRadiusPx;
uniform float uStarBrightRadius;
uniform float uStarHorizonFade;
uniform float uStarTime;         // seconds, wrapped on the CPU to keep float precision
uniform float uStarTwinkleDepth;
uniform vec2 uStarTwinkleRate;   // slowest and fastest twinkle, cycles per second

// The cloud layer hides the stars behind it: the drawn clouds only cover the dome at their
// opacity, which would leave bright stars glinting through, so the stars are first dimmed by the
// cloud cover along the view ray (CloudCover is the C# twin). The coverage map is the cloud
// shadow's, bound once on unit 8 for the whole run, read cell by cell (texelFetch) so the cover
// ends exactly at the cells the cloud mesh is built on.
uniform sampler2D uCloudShadowMap;
uniform int uCloudCells;      // texels per side of the map
uniform int uCloudMaxLevels;
uniform float uCloudHeight;
uniform float uCloudLayer;    // one layer's thickness
uniform float uCloudField;
uniform vec2 uCloudFog;       // the dry fog's near and far, which thin the drawn clouds
uniform vec2 uCloudDrift;
uniform float uCloudBlocking; // CloudCover.Blocking: 1 at the default cloud opacity, 0 clouds off
uniform vec3 uEye;            // the eye the dome is seen from (the mirrored one in the mirror)

// The sun's lens glare (SunGlare is the C# twin): the linear sun colour times this frame's
// intensity, worked out on the CPU from the camera's facing, the sun's height and the cloud over
// it; zero in the mirror, underwater and in lava. Spread as cos(angle to the sun)^uGlareExponent.
uniform vec3 uGlareColor;
uniform float uGlareExponent;

// The moon (drawn after the dome by moon-fragment.glsl, opposite the sun) hides the stars behind its
// disc, including its unlit part: how visible it is this frame, and the cos of the outer and inner
// radius of the starless disc (feathered by a pixel or so).
uniform float uMoonVisibility;
uniform vec2 uMoonMask;

// The aurora (Aurora is the C# twin of its fade and drift): slow green and violet curtains in the
// night dome, seen only from cold biomes. uAurora is this frame's strength (0 by day, in warm lands
// and wherever the eye's climate is unknown); uAuroraDrift is each noise's drift in cells, wrapped
// into its period on the CPU (uAuroraPeriods: the curtain sectors, their detail, the lower edge's
// wave, the fine folds), so the curtains drift on forever with no jump.
uniform float uAurora;
uniform float uAuroraBrightness;
uniform vec4 uAuroraPeriods;
uniform vec4 uAuroraDrift;

out vec4 FragColor;

const float QuarterPi = 0.78539816;

// Twin copies: sky-fragment.glsl, test-fragment.glsl, cloud-fragment.glsl; C# reference SkyPalette.Sample - change all four together
vec3 SkySample(vec3 dir)
{
    float e = max(dir.y, 0.0);
    float az = (dir.x * uSunDir.x + dir.z * uSunDir.z)
             / sqrt(max((dir.x * dir.x + dir.z * dir.z) * (uSunDir.x * uSunDir.x + uSunDir.z * uSunDir.z), 1e-6));
    float band = smoothstep(0.0, uSkyShape.x, e);
    float lobe = pow(max(az, 0.0), uSkyShape.w) * (1.0 - band);
    float haze = uSkyShape.z * (1.0 - smoothstep(0.0, uSkyShape.y, e));
    return mix(uSkyHorizon * (1.0 + haze), uSkyZenith, band) + uSkyGlow * lobe;
}

// Integer hash (lowbias32); twin of StarField.Hash.
uint StarHash(uint x)
{
    x ^= x >> 16;
    x *= 0x7feb352du;
    x ^= x >> 15;
    x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

// Twin of StarField.CellHash.
uint StarCellHash(uint face, uvec2 cell)
{
    return StarHash(cell.x ^ StarHash(cell.y ^ StarHash(face + 0x9e3779b9u)));
}

// The top 24 bits of a hash as a uniform value in [0, 1); twin of StarField.Unit.
float StarUnit(uint h)
{
    return float(h >> 8) * (1.0 / 16777216.0);
}

// Twin of StarField.FaceDirection.
vec3 StarFaceDirection(uint face, vec2 uv)
{
    if (face < 2u) return vec3(face == 0u ? 1.0 : -1.0, uv.y, uv.x);
    if (face < 4u) return vec3(uv.x, face == 2u ? 1.0 : -1.0, uv.y);
    return vec3(uv.x, uv.y, face == 4u ? 1.0 : -1.0);
}

// The star light in star-space direction d, px being one pixel's angular size: the one cell d
// falls in (cube face, equi-angular warp, grid; StarField.CellOf), whether it holds a star
// (StarField.Probability, weighted by the cell's solid angle), and that star's soft point.
vec3 Stars(vec3 d, float px)
{
    vec3 a = abs(d);
    uint face;
    vec2 uv;
    if (a.x >= a.y && a.x >= a.z) { face = d.x > 0.0 ? 0u : 1u; uv = d.zy / a.x; }
    else if (a.y >= a.z) { face = d.y > 0.0 ? 2u : 3u; uv = d.xz / a.y; }
    else { face = d.z > 0.0 ? 4u : 5u; uv = d.xy / a.z; }

    vec2 g = (atan(uv) / QuarterPi * 0.5 + 0.5) * uStarCells;
    vec2 cellF = min(floor(g), vec2(uStarCells - 1.0));
    uvec2 cell = uvec2(cellF);
    uint h = StarCellHash(face, cell);

    // StarField.RelativeSolidAngle at the cell centre
    vec2 uc = tan(((cellF + 0.5) / uStarCells * 2.0 - 1.0) * QuarterPi);
    vec2 uc2 = uc * uc;
    float weight = (1.0 + uc2.x) * (1.0 + uc2.y) / pow(1.0 + uc2.x + uc2.y, 1.5);
    if (StarUnit(h) >= uStarDensity * weight) return vec3(0.0);

    uint h2 = StarHash(h);
    uint h3 = StarHash(h2);
    vec2 jitter = vec2(float(h2 & 0xffffu), float(h2 >> 16)) / 65535.0;
    vec2 inCell = uStarMargin + jitter * (1.0 - 2.0 * uStarMargin);
    vec3 star = normalize(StarFaceDirection(face, tan(((cellF + inCell) / uStarCells * 2.0 - 1.0) * QuarterPi)));
    float b = mix(uStarFaintest, 1.0, pow(StarUnit(h3), uStarCurve));

    // A gaussian a fixed number of pixels wide, a little wider for the bright ones; the chord
    // stands in for the angle, which it equals to well under a pixel at this size.
    float sigma = px * uStarRadiusPx * mix(1.0, uStarBrightRadius, b);
    vec3 off = d - star;
    float point = exp(-dot(off, off) / (sigma * sigma));

    // A faint colour temperature per star, from cool blue-white to warm yellow-white.
    vec3 tint = mix(vec3(0.75, 0.85, 1.0), vec3(1.0, 0.88, 0.7), StarUnit(StarHash(h3)));

    // The twinkle: a rate and phase of the star's own (StarField.TwinkleOf), two sines at an
    // irrational ratio so it never visibly repeats (StarField.Twinkle).
    uint h4 = StarHash(StarHash(h3));
    float rate = mix(uStarTwinkleRate.x, uStarTwinkleRate.y, StarUnit(h4));
    float phase = StarUnit(StarHash(h4)) * 6.2831853;
    float wave = 6.2831853 * rate * uStarTime + phase;
    float twinkle = 1.0 - uStarTwinkleDepth * (0.5 + 0.3 * sin(wave) + 0.2 * sin(2.31 * wave + 1.7 * phase));
    return tint * (uStarBrightness * b * point * twinkle);
}

// How many layers the field cell under world column p stacks; twin of CloudCover.LevelAt.
// Mirrors CloudCopies.FieldUv, wrapped by hand like the copies (texelFetch ignores REPEAT).
int CloudLevelAt(vec2 p)
{
    ivec2 cell = ivec2(floor((p - uCloudDrift) / uCloudField * float(uCloudCells)));
    cell = ((cell % uCloudCells) + uCloudCells) % uCloudCells;
    return int(round(texelFetch(uCloudShadowMap, cell, 0).r * float(uCloudMaxLevels)));
}

// How much of the drawn cloud layer stands between uEye and the sky along d, 0..1; twin of
// CloudCover.AlongRay: tested on every layer boundary the ray climbs through, so a ray entering
// through a cloud's side wall is caught too, and thinned with distance like the drawn clouds.
float CloudCoverAlongRay(vec3 d)
{
    if (uCloudBlocking <= 0.0 || d.y <= 1e-3 || uEye.y >= uCloudHeight) return 0.0;
    float tBase = (uCloudHeight - uEye.y) / d.y;
    bool covered = false;
    for (int k = 0; k <= uCloudMaxLevels && !covered; k++) {
        float t = tBase + float(k) * uCloudLayer / d.y;
        covered = CloudLevelAt(uEye.xz + d.xz * t) >= max(k, 1);
    }
    if (!covered) return 0.0;
    float f = clamp((tBase - uCloudFog.x) / (uCloudFog.y - uCloudFog.x), 0.0, 1.0);
    return uCloudBlocking * (1.0 - f * f * f);
}

// A hash of one noise cell, 0..1.
float AuroraHash(float n)
{
    return fract(sin(n * 127.1 + 311.7) * 43758.5453);
}

// Smooth 1D value noise that repeats every `period` cells, so it closes seamlessly round the horizon.
float AuroraNoise(float x, float period)
{
    float i = floor(x);
    float f = fract(x);
    f = f * f * (3.0 - 2.0 * f);
    return mix(AuroraHash(mod(i, period)), AuroraHash(mod(i + 1.0, period)), f);
}

// The curtains' light in direction d (above the horizon): broad sectors along the horizon where
// curtains hang (a noise of the view azimuth), fine vertical folds inside them, a wavy lower edge
// that is brightest, and a glow fading up from it, green low and violet high.
vec3 AuroraCurtains(vec3 d)
{
    float a = atan(d.z, d.x) / 6.2831853 + 0.5;      // 0..1 once round the horizon
    vec4 x = a * uAuroraPeriods + uAuroraDrift;
    float sector = smoothstep(0.3, 0.65, 0.65 * AuroraNoise(x.x, uAuroraPeriods.x) + 0.35 * AuroraNoise(x.y, uAuroraPeriods.y));
    if (sector <= 0.0) return vec3(0.0);
    float base = 0.14 + 0.16 * AuroraNoise(x.z, uAuroraPeriods.z);
    float folds = 0.45 + 0.55 * AuroraNoise(x.w, uAuroraPeriods.w);
    float h = d.y - base;
    float v = smoothstep(-0.06, 0.015, h) * exp(-max(h, 0.0) / 0.2);
    vec3 tint = mix(vec3(0.08, 1.0, 0.42), vec3(0.55, 0.18, 1.0), smoothstep(0.03, 0.4, h));
    return tint * (uAuroraBrightness * sector * folds * v);
}

void main()
{
    vec2 ndc = fUv * 2.0 - 1.0;
    // No translation in the matrix, so this point lies on the view ray from the origin
    // whatever the clip-depth convention is.
    vec4 h = uInvViewRotProj * vec4(ndc, 0.5, 1.0);
    vec3 dir = normalize(h.xyz / h.w);
    // One pixel's angular size, for the stars' pixel-sized points. Taken here, in uniform
    // control flow, before any branch.
    float px = max(length(dFdx(dir)), length(dFdy(dir)));

    vec3 col = SkySample(dir);
    // SunGlare.Radial; added in main only, like the stars, so the fog never sees it
    col += uGlareColor * pow(max(dot(dir, uSunDir), 0.0), uGlareExponent);

    if (uStarVisibility > 0.0 && dir.y > 0.0) {
        float fade = uStarVisibility
                   * smoothstep(0.0, uStarHorizonFade, dir.y)                   // horizon haze
                   * (1.0 - smoothstep(uStarSunMask.x, uStarSunMask.y, dot(dir, uSunDir))) // sun disc
                   * (1.0 - uMoonVisibility * smoothstep(uMoonMask.x, uMoonMask.y, dot(dir, -uSunDir))); // moon disc
        if (fade > 0.0) {
            // Star space turns with the sun (StarField.ToStarSpace), so the stars set with it
            vec3 sd = vec3(uStarSpin.x * dir.x + uStarSpin.y * dir.y,
                           -uStarSpin.y * dir.x + uStarSpin.x * dir.y, dir.z);
            vec3 star = Stars(sd, px);
            // Most pixels hold no star, so the cloud test (up to three map reads) runs only
            // where one actually shines
            if (dot(star, star) > 1e-10)
                col += star * (fade * (1.0 - CloudCoverAlongRay(dir)));
        }
    }

    // The aurora, after the stars and like them only here (the fog never sees it), hidden behind
    // the clouds; the cloud test runs only where a curtain shines.
    if (uAurora > 0.0 && dir.y > 0.0) {
        vec3 aurora = AuroraCurtains(dir);
        if (dot(aurora, aurora) > 1e-10)
            col += aurora * (uAurora * (1.0 - CloudCoverAlongRay(dir)));
    }

    col = mix(col, uSkyTint, uSkyTintAmount);
    FragColor = vec4(max(col, 0.0), 1.0);
}
