#version 460 core
// World surface shading. Opaque terrain gets atlas colour, baked voxel light, a directional
// sun with shadow-map lookup, torch light and point lights, then fog.
//
// Water (flagged by vertex alpha 254) is a small physical model instead of a tint. Its normal
// is the analytic slope of the same wave sum the vertex shader displaces by, so the shading
// ripples far more than the geometry does. What lies behind the surface is read out of a copy
// of the frame taken just before the water pass (uSceneColor/uSceneDepth), offset sideways
// along the wave normal for refraction; the distance from the surface to what that copy saw
// is how thick the water is, and Beer-Lambert absorption over that thickness plus an
// in-scattered water colour is what makes shallows read clear and depth read teal; where that
// thickness runs out against a block or a bank a thin foam band rises. The planar
// reflection is mixed in by a Schlick fresnel, so the surface is transparent underfoot and a
// mirror at a graze. Opaque faces that look into water (bit 3 of the packed normal) get the
// caustic net on top, projected back up the sun direction to the sea plane.
// Seen from below (a per-fragment side test against the face normal, not the sea plane, since
// a pond stands above sea level) the surface is a Snell window: the scene copy is shown through
// it distorted by the wave normal, the inside fresnel goes to total internal reflection past the
// critical angle, and that band fades to the underwater fog colour instead of sampling the
// sea-level mirror, which is never valid from that side.
in vec4 fColor;
in vec2 fTexCoord;
flat in uint fLayer;
in float fSun;
in float fBlock;
in vec3 fNormal;
flat in vec3 fTangent;
flat in vec3 fBitangent;
in vec3 fWorldPos;
in float fSubmerged;
// Baked fluid surface (Vertex.WaterSurface) above a submerged face.
flat in float fWaterSurfaceY;
in vec3 fWaterFlow; // flow xy * strength, z = falling (fluid quads only; see test-vertex.glsl)
flat in float fAlpha; // per-draw fade from the vertex stage: a uniform cannot vary inside one multi-draw
flat in float fTintGain; // BiomeTint.Gain on a biome-tinted face (Vertex.Normal bit 4), else 1
flat in float fSwayFlag; // 1 on a leaf-sway face (Vertex.Normal bit 5: leaves, rooted plants), else 0

// One slice per block tile; UVs are tile-local and REPEAT across merged quads.
uniform sampler2DArray uTextureAtlas;
// Index-parallel to uTextureAtlas: RGB a tangent-space normal, A a metallic multiplier. Only
// the reflective tiles have real art here; every other slice is flat and fully permissive.
uniform sampler2DArray uMaterialAtlas;
// Entries in the tile-material buffer below, and the switch for the whole reflective path:
// it stays 0 wherever that buffer is not bound (the item debugger), so the buffer is never read.
uniform int uMaterialCount;
uniform float uOreSpecScale;  // WorldGenerationConfig.OreSpecLinearScale
// Snow glints: the snow tile's atlas slice, the glint strength (0, the default and the
// ItemDebugger's, switches the term off) and the share of texels carrying a glinting facet.
uniform float uSnowLayer;
uniform float uSnowSparkle;
uniform float uSnowSparkleDensity;
// Leaf backlight (LeafBacklight is the twin): the strength (0, the default and the ItemDebugger's,
// switches it off), the exponent of the lobe around the sun, how far toward the sun the
// light-through test looks, in blocks, and the linear tint of the light a leaf lets through.
uniform float uLeafBacklight;
uniform float uLeafBacklightPower;
uniform float uLeafBacklightReach;
uniform vec3 uLeafBacklightTint;
// Grass wind shimmer (GrassShimmer is the twin): the strength (0, the default and the
// ItemDebugger's, switches it off), the band wavelength in blocks and the distance fade window.
uniform float uGrassShimmer;
uniform float uGrassShimmerWavelength;
uniform float uGrassShimmerFadeNear;
uniform float uGrassShimmerFadeFar;
// Ice glint (IceGlint is the twin): the ice tile's atlas slice, the sun highlight's strength (0, the
// default and the ItemDebugger's, switches the whole term off), its Blinn exponent and the share of
// the mirrored sky a face takes at a full graze.
uniform float uIceLayer;
uniform float uIceGlint;
uniform float uIceShine;
uniform float uIceSheen;
// Glass sheen (GlassSheen is the twin): the glass tile's atlas slice (glass blocks and panes share
// it) and the share of the mirrored sky a face takes at a full graze; 0, the default and the
// ItemDebugger's, switches it off.
uniform float uGlassLayer;
uniform float uGlassSheen;
// The leaf sway's wind (test-vertex.glsl): downwind direction * WindLean and the gusts' travelled
// offset; the grass shimmer rolls its bands along it.
uniform vec2 uWind;
uniform vec2 uWindOffset;
// How reflective each atlas slice is: (metallic, roughness, normalStrength, unused). Keyed by
// tile rather than by block, which is what keeps this feature off the vertex entirely - fLayer
// already rides every vertex. See TileMaterials.
layout (std430, binding = 1) readonly buffer TileMaterialBuffer { vec4 uTileMaterials[]; };
uniform float uFlash;
uniform float uHitFlash;
uniform float uAlphaCutoff;
uniform float uTime;
uniform vec3 uSunDir;
uniform vec3 uSunColor;
uniform float uSunIntensity;
uniform vec3 uAmbient;
// Ambient a downward face sees (RenderManager.GroundAmbientOf(uAmbient))
uniform vec3 uGroundAmbient;
// The block-light colour arrives per vertex (test-vertex.glsl, decoded from the palette)
in vec3 fBlockTint;
uniform float uTorchIntensity;
uniform vec3 uFogColor;
uniform float uFogNear;
uniform float uFogFar;
uniform vec3 uCameraPos;
// The sky dome's palette (SkyPalette), for fog that matches the dome in the view direction.
uniform vec3 uSkyZenith;
uniform vec3 uSkyHorizon;
uniform vec3 uSkyGlow;
uniform vec4 uSkyShape;       // x horizon band, y haze width, z haze strength, w glow exponent
// 0 dry, 1 fully underwater/in lava: blends the dome-sampled fog back to the flat uFogColor.
uniform float uFogWet;
uniform int uNumPointLights;
uniform vec3 uPointPos[8];
uniform vec3 uPointColor[8];
// Point lights from index uFireLightStart on are fires (campfires, lit kilns) and get a
// bounded range window; 0 range (a program that never sets it) leaves every light unwindowed.
uniform int uFireLightStart;
uniform float uFireLightRange;
uniform sampler2DShadow uShadowMap;
uniform mat4 uLightViewProj;
uniform sampler2D uReflectionMap;
uniform mat4 uReflViewProj;
uniform float uReflValid;
uniform float uClipY;
uniform float uShadowStrength;
uniform float uShadowTexel;
uniform float uShadowWorldTexel;
uniform float uShadowSoftness;
uniform float uShadowNormalOffset;
uniform float uShadowBiasTexels;
uniform float uShadowBiasScale;
// The far cascade: a coarse, silhouette-only box sampled where the near box fades out.
uniform sampler2DShadow uShadowMapFar;
uniform mat4 uLightViewProjFar;
uniform float uShadowWorldTexelFar;
uniform float uShadowBiasScaleFar;
// Cloud shadows: an R8 coverage map baked from CloudField's level grid (unit 8), the cloud
// layer's base height, the field size and this frame's drift. uCloudShadowStrength 0 (the
// default, and the ItemDebugger's) switches the whole path off.
uniform sampler2D uCloudShadowMap;
uniform float uCloudShadowStrength;
uniform float uCloudHeight;
uniform float uCloudField;
uniform vec2 uCloudDrift;

// The frame as it stood just before the water pass: colour to refract, depth to measure the
// water's thickness with. Plain samplers, no compare mode - uSceneDepth is read for its value.
uniform sampler2D uSceneColor;
uniform sampler2D uSceneDepth;
uniform vec2 uInvViewport;
uniform mat4 uInvViewProj;
// The same frame's forward view-projection: the SSR march projects its steps through it.
uniform mat4 uSceneViewProj;
// Screen-space reflection for water the planar mirror cannot serve. uWaterSsrMode is 0 (off:
// reflections disabled, or nothing near to reflect in), 1 (blend in by the surface's distance
// from the mirror plane uWaterPlaneY) or 2 (the mirror is invalid this frame: SSR everywhere).
uniform float uWaterSsrMode;
uniform float uWaterPlaneY;
uniform int uWaterSsrSteps;
uniform float uWaterSsrStepStart;
uniform float uWaterSsrStepGrowth;
uniform float uWaterSsrThickness;   // hit tolerance behind the depth buffer, in current steps
uniform float uWaterSsrPlaneNear;
uniform float uWaterSsrPlaneFar;
// The shadow strength before the underwater fade: the caustics still want crisp sun shadows
// even once the diffuse has stopped using them.
uniform float uShadowStrengthDry;
uniform float uUnderwater;
uniform float uWaterNormalScale;
uniform float uWaterRefractStrength;
// Multiplies the refraction offset for a face seen from behind: the Snell window squeezes the
// whole sky into ~97 degrees, so the same wave tilt swings the view through it further.
uniform float uWaterWindowRefract;
// Water's index of refraction over air's. The window's critical angle and the total internal
// reflection past it both follow from it; it is physics, not tuning, hence not a uniform.
const float WaterEta = 1.333;
uniform vec3 uWaterAbsorb;
uniform vec3 uWaterScatter;
uniform float uWaterCausticStrength;
// Wet albedo (SH-22): a submerged face darkens its albedo by uWetDarken, but only below its
// own baked water surface, so dry ground beside or above the water keeps its colour.
uniform float uWetDarken;
uniform float uWaterFancyNear;
uniform float uWaterFancyFar;
uniform float uWaterFlowStretch;  // fraction of the along-flow slope damped at full strength
uniform float uWaterFlowScroll;   // blocks/s the ripples travel at full strength
uniform float uWaterFlowPeriod;   // seconds per flow-map phase; 0 turns the flow map off
uniform float uWaterFallScroll;   // blocks/s the streaks run down a falling face
uniform float uWaterFallStreak;   // tangent tilt of the falling streaks; 0 turns them off
// Shore foam: the view-ray water depth at which the band ends (0, the default and the
// ItemDebugger's, turns it off), its opacity at the contact line and its linear albedo.
uniform float uWaterFoamWidth;
uniform float uWaterFoamStrength;
uniform vec3 uWaterFoamColor;
// Average opaque colour of the leaves tile, for LOD foliage below: far crowns are
// drawn opaque (no cutout holes) and transparent texels fall back to this.
uniform vec3 uLodLeafColor;
// Atlas slice of the lava tile: fluid fragments on this layer glow instead of refracting.
uniform float uLavaLayer;
uniform float uLavaNormalScale;  // slope-to-normal gain of the lava swell (WorldGenerationConfig.LavaNormalScale)
uniform float uLavaCatchLight;   // strength of the point-light catch-light on lava
uniform float uLavaCatchPower;   // Blinn exponent of that catch-light
// How far the lava glow breathes above and below its own brightness on the slow noise field
// (LavaPulse is the twin); 0, the default and the ItemDebugger's, switches it off.
uniform float uLavaPulse;

out vec4 FragColor;

// Exact piecewise sRGB transfer functions (post-fragment.glsl's LinearToSrgb and the
// ColorSpace twins), used only by the fog mix below.
vec3 LinearToSrgb(vec3 c)
{
    c = max(c, 0.0);
    vec3 lo = c * 12.92;
    vec3 hi = 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055;
    return mix(hi, lo, lessThanEqual(c, vec3(0.0031308)));
}

vec3 SrgbToLinear(vec3 c)
{
    vec3 lo = c / 12.92;
    vec3 hi = pow((c + 0.055) / 1.055, vec3(2.4));
    return mix(hi, lo, lessThanEqual(c, vec3(0.04045)));
}

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

// A short world-space reflection march against the scene copies (units 3/4): from P along R,
// each step projected through uSceneViewProj and compared with the stored depth. The compare
// is raw window depth (ndc.z * 0.5 + 0.5) against the texel, and the hit is reconstructed with
// the same * 2 - 1 as the refraction path - the projection is Direct3D-style, so never
// linearize. A ray that ends behind the scene within the thickness tolerance is a hit; one
// that ends far behind has passed behind an occluder and misses. The copy holds no water or
// glass, so SSR never reflects those - acceptable for a fallback.
vec3 WaterSsr(vec3 P, vec3 R, vec3 miss)
{
    vec3 p = P + R * 0.05;
    float stepLen = uWaterSsrStepStart;
    for (int i = 0; i < 8; i++) {
        if (i >= uWaterSsrSteps) break;
        p += R * stepLen;
        vec4 c = uSceneViewProj * vec4(p, 1.0);
        if (c.w <= 0.0) break;
        vec3 ndc = c.xyz / c.w;
        vec2 uv = ndc.xy * 0.5 + 0.5;
        if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) break;
        float sd = texture(uSceneDepth, uv).r;
        if (ndc.z * 0.5 + 0.5 > sd) {
            vec4 sp = uInvViewProj * vec4(uv * 2.0 - 1.0, sd * 2.0 - 1.0, 1.0);
            if (distance(sp.xyz / sp.w, p) < stepLen * uWaterSsrThickness) {
                // Fade toward the miss colour near the screen border, where the copy runs out.
                vec2 e2 = min(uv, 1.0 - uv);
                float edge = smoothstep(0.0, 0.08, min(e2.x, e2.y));
                return mix(miss, texture(uSceneColor, uv).rgb, edge);
            }
            break;
        }
        stepLen *= uWaterSsrStepGrowth;
    }
    return miss;
}

// Directional sun shadows. RenderManager renders the casters depth-only through
// uLightViewProj into uShadowMap (the near cascade) and through uLightViewProjFar into
// uShadowMapFar (the coarse far cascade), so a fragment's own light-space depth compared
// against what is stored there says whether something else got the sunlight first. The ortho
// projection puts clip z in 0..1 exactly like GL's depth write, so all three components
// map with *0.5+0.5 and the comparison is apples to apples.
//
// One cascade's lookup: normal offset, projection into its box, the box-edge fade (0 outside
// the box or past its far plane) and the slope-scaled bias. Returns the fade; uvz is the
// biased compare coordinate to sample with.
float CascadeLookup(mat4 vp, float worldTexel, float biasScale, vec3 wp, vec3 N, float ndl, out vec3 uvz) {
    // Normal offset: axis-aligned voxel faces at grazing angles otherwise show acne
    vec3 p = wp + N * (worldTexel * uShadowNormalOffset * (1.0 - ndl));
    vec4 lp = vp * vec4(p, 1.0);
    vec3 ndc = lp.xyz / lp.w;
    uvz = ndc * 0.5 + 0.5;                      // all three components -> [0,1]
    float edge = max(abs(ndc.x), abs(ndc.y));
    float fade = 1.0 - smoothstep(0.85, 1.0, edge);   // soften the ortho-box boundary
    if (uvz.z > 1.0) fade = 0.0;
    // Slope-scaled depth bias. One shadow texel spans texel*cot(theta) of depth on a receiver
    // whose normal sits theta off the light axis, and anything less than that makes the
    // surface shadow itself. Deriving it from the fragment's own normal is what keeps the
    // bias honest: a single constant large enough for a grazing sun also pushed every shadow
    // a whole block clear of its caster once the sun climbed, so shadows read as detached and
    // their edges wandered across the terrain's own block steps. theta is clamped at 16
    // texels (about 4 degrees off the light) - past that the fade above owns the result.
    float cot = sqrt(max(1.0 - ndl * ndl, 0.0)) / max(ndl, 0.0625);
    uvz.z -= (cot + uShadowBiasTexels) * worldTexel * biasScale;
    return fade;
}

float SampleShadow(vec3 wp, vec3 N, float ndl, float strength) {
    if (strength <= 0.0) return 1.0;
    // litN / litF are raw 0..1 comparison results; fade and strength are applied once below.
    vec3 uvzN;
    float fadeN = CascadeLookup(uLightViewProj, uShadowWorldTexel, uShadowBiasScale, wp, N, ndl, uvzN);
    float litN = 1.0;
    if (fadeN > 0.0) {
        if (uShadowSoftness <= 0.0) {
            litN = texture(uShadowMap, uvzN);
        } else {
            // Four taps on the diagonals, half a texel out. The sampler filters linearly, so each
            // fetch is already a hardware 2x2 comparison and the four overlap into a smooth ~3x3
            // tent — the same softness as the old (2s+1)^2 loop for 4 fetches instead of 9.
            float o = 0.5 * uShadowTexel * uShadowSoftness;
            litN = (texture(uShadowMap, vec3(uvzN.x - o, uvzN.y - o, uvzN.z))
                  + texture(uShadowMap, vec3(uvzN.x + o, uvzN.y - o, uvzN.z))
                  + texture(uShadowMap, vec3(uvzN.x - o, uvzN.y + o, uvzN.z))
                  + texture(uShadowMap, vec3(uvzN.x + o, uvzN.y + o, uvzN.z))) * 0.25;
        }
    }
    // The far cascade only where the near one has faded: one silhouette-only tap (the
    // sampler's hardware 2x2 bilinear compare), no PCF kernel by design (PERF_TODO).
    float litF = 1.0, fadeF = 0.0;
    if (fadeN < 1.0) {
        vec3 uvzF;
        fadeF = CascadeLookup(uLightViewProjFar, uShadowWorldTexelFar, uShadowBiasScaleFar, wp, N, ndl, uvzF);
        if (fadeF > 0.0) litF = texture(uShadowMapFar, uvzF);
    }
    // Inside the near core (fadeN = 1) this is exactly the old mix(1, litN, strength).
    float lit = mix(mix(1.0, litF, fadeF), litN, fadeN);
    return mix(1.0, lit, strength);
}

// How much of the direct sun the cloud layer lets through at p: follow the sun ray up to the
// cloud base and read the baked coverage there. uSunDir points toward the sun, so the cloud
// casting onto p sits sun-ward of it. Skipped at night, above the layer and wherever no sky
// light reaches (fSun 0: a sealed cave never pays the tap).
float CloudShadow(vec3 p) {
    if (uCloudShadowStrength <= 0.0 || uSunDir.y <= 0.0 || p.y >= uCloudHeight || fSun <= 0.0)
        return 1.0;
    vec2 cp = p.xz + uSunDir.xz / max(uSunDir.y, 0.2) * (uCloudHeight - p.y);
    // Mirrors CloudCopies.FieldUv; the map's REPEAT wrap tiles it like the 3x3 cloud copies.
    vec2 uv = (cp - uCloudDrift) / uCloudField;
    return 1.0 - uCloudShadowStrength * texture(uCloudShadowMap, uv).r;
}

// Analytic slope of WaveHeight (same terms, differentiated). WaveSlope is the copy of
// test-vertex.glsl's WaveHeight terms and must change with them.
vec2 WaveSlope(vec2 p, float t) {
    vec2 g = vec2(0.0);
    g += 0.035 * 0.9 * cos(dot(p, vec2(0.9, 0.4)) * 0.9 + t * 1.6) * vec2(0.9, 0.4);
    g += 0.025 * 1.4 * cos(dot(p, vec2(-0.5, 0.85)) * 1.4 + t * 2.1) * vec2(-0.5, 0.85);
    g += 0.015 * 2.6 * cos(dot(p, vec2(0.3, -0.95)) * 2.6 + t * 2.9) * vec2(0.3, -0.95);
    g += 0.010 * 4.1 * cos(dot(p, vec2(-0.8, -0.6)) * 4.1 + t * 3.7) * vec2(-0.8, -0.6);
    return g;
}

// Analytic slope of test-vertex.glsl's LavaWaveHeight (same terms, differentiated). Must change in
// lockstep with it, or the lava's shading disagrees with its swell.
vec2 LavaWaveSlope(vec2 p, float t) {
    vec2 g = vec2(0.0);
    g += 0.040 * 0.35 * cos(dot(p, vec2(0.8, 0.6)) * 0.35 + t * 0.35) * vec2(0.8, 0.6);
    g += 0.025 * 0.55 * cos(dot(p, vec2(-0.6, 0.8)) * 0.55 + t * 0.5) * vec2(-0.6, 0.8);
    g += 0.015 * 0.9 * cos(dot(p, vec2(0.2, -0.98)) * 0.9 + t * 0.7) * vec2(0.2, -0.98);
    g += 0.008 * 1.5 * cos(dot(p, vec2(-0.9, -0.4)) * 1.5 + t * 0.9) * vec2(-0.9, -0.4);
    return g;
}

// The slope scaled up into a normal: the geometry moves a few centimetres but the shading wants
// a visibly rippled surface.
vec3 WaveNormal(vec2 p, float t, float k) {
    vec2 g = WaveSlope(p, t);
    return normalize(vec3(-g.x * k, 1.0, -g.y * k));
}

// WaveNormal carried along a flow: a two-phase flow map. Each phase samples the wave slope at an
// offset that slides downstream over one period and snaps back; the two phases sit half a period
// apart with triangle weights that sum to 1, so each snap lands while its phase weighs 0. The
// offsets are bounded by span (never an unbounded world or time phase), so neighbouring cells
// with different flows meet without a seam, and still water (flow 0) is exactly WaveNormal.
// Damping the along-flow slope stretches the crests along the flow without shifting their phase.
vec3 FlowWaveNormal(vec2 p, float t, float k, vec2 flow) {
    float fs = length(flow);
    if (fs < 1e-3 || uWaterFlowPeriod <= 0.0) return WaveNormal(p, t, k);
    float ph0 = fract(t / uWaterFlowPeriod);
    float ph1 = fract(ph0 + 0.5);
    vec2 span = flow * uWaterFlowScroll * uWaterFlowPeriod;
    vec2 g = WaveSlope(p - span * ph0, t) * (1.0 - abs(2.0 * ph0 - 1.0))
           + WaveSlope(p - span * ph1, t) * (1.0 - abs(2.0 * ph1 - 1.0));
    vec2 dir = flow / fs;
    g -= dir * dot(dir, g) * (uWaterFlowStretch * fs);
    return normalize(vec3(-g.x * k, 1.0, -g.y * k));
}

vec2 Hash2(vec2 p) {
    vec3 q = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973));
    q += dot(q, q.yzx + 33.33);
    return fract((q.xx + q.yz) * q.zy);
}

float Hash12(vec2 p) {
    vec3 q = fract(vec3(p.xyx) * 0.1031);
    q += dot(q, q.yzx + 33.33);
    return fract((q.x + q.y) * q.z);
}

// Smooth value noise on the Hash12 lattice, 0..1.
float ValueNoise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(Hash12(i), Hash12(i + vec2(1.0, 0.0)), f.x),
               mix(Hash12(i + vec2(0.0, 1.0)), Hash12(i + vec2(1.0, 1.0)), f.x), f.y);
}

// The shore foam's breakup pattern: two octaves drifting slowly against each other, so the
// ragged edge of the band crawls instead of sitting still. 0..1.
float FoamField(vec2 p, float t) {
    return 0.6 * ValueNoise(p * 1.8 + vec2(t * 0.21, t * 0.09))
         + 0.4 * ValueNoise(p * 4.3 + vec2(-t * 0.13, t * 0.27));
}

// The lava glow pulse's field: two octaves of value noise drifting slowly against each other, so
// broad patches of the surface brighten and dim over seconds. 0..1. GL-free twin: LavaPulse.Field.
float LavaPulseField(vec2 p, float t) {
    return 0.6 * ValueNoise(p * 0.22 + vec2(t * 0.11, -t * 0.07))
         + 0.4 * ValueNoise(p * 0.47 + vec2(-t * 0.09, t * 0.13));
}

// FoamField carried along a flow by the same bounded two-phase flow map FlowWaveNormal uses, so
// foam on a stream travels downstream with the ripples and cells of different flow never seam.
float FlowFoam(vec2 p, float t, vec2 flow) {
    float fs = length(flow);
    if (fs < 1e-3 || uWaterFlowPeriod <= 0.0) return FoamField(p, t);
    float ph0 = fract(t / uWaterFlowPeriod);
    float ph1 = fract(ph0 + 0.5);
    vec2 span = flow * uWaterFlowScroll * uWaterFlowPeriod;
    return FoamField(p - span * ph0, t) * (1.0 - abs(2.0 * ph0 - 1.0))
         + FoamField(p - span * ph1, t) * (1.0 - abs(2.0 * ph1 - 1.0));
}

// Block-light flicker, desynchronised by position. Each corner of a coarse xz lattice
// (FlickerCell blocks apart) flickers on its own hashed phase and the four corner VALUES are
// blended bilinearly. Blending the phase instead would turn the flicker into a wave sweeping
// across a wall (the phase gradient reads as motion) and would wrap around at 2*pi midway
// between corners; a per-cell step with no blend would put a seam on every cell edge.
const float FlickerCell = 6.0;

float FlickAt(vec2 c) {
    float ph = Hash12(c) * 6.2831853;
    return 0.9 + 0.1 * sin(uTime * 9.0 + ph) * sin(uTime * 7.3 + 1.7 + ph * 1.7);
}

float BlockFlicker(vec3 p) {
    vec2 q = p.xz / FlickerCell;
    vec2 i = floor(q);
    vec2 f = smoothstep(0.0, 1.0, fract(q));
    float a = FlickAt(i);
    float b = FlickAt(i + vec2(1.0, 0.0));
    float c = FlickAt(i + vec2(0.0, 1.0));
    float d = FlickAt(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

// Scales a screen-space tap offset down to nothing as its base point nears the screen edge, so a
// refracted or reflected lookup fades back to the straight sample instead of clamping onto the
// border texel and smearing it into a streak. The fade is sized from the offset actually applied
// rather than a fixed margin, which keeps the tap on screen at any refraction slider value: the
// move stays under 0.75 * e until the edge distance e reaches 1.5 * |off|, and past that
// |off| <= e anyway.
vec2 FadeTapOffset(vec2 base, vec2 off) {
    float e = min(min(base.x, base.y), min(1.0 - base.x, 1.0 - base.y));
    return off * smoothstep(0.0, max(length(off), 1e-4) * 1.5, e);
}

// Distance from the second-nearest to the nearest animated Voronoi centre: small along the
// cell borders, which is where refracted sunlight piles up into the bright caustic net.
float VoronoiEdge(vec2 p, float t) {
    vec2 n = floor(p), f = fract(p);
    float f1 = 8.0, f2 = 8.0;
    for (int j = -1; j <= 1; j++)
    for (int i = -1; i <= 1; i++) {
        vec2 g = vec2(float(i), float(j));
        vec2 o = g + 0.5 + 0.4 * sin(t + 6.2831 * Hash2(n + g)) - f;
        float d = dot(o, o);
        if (d < f1) { f2 = f1; f1 = d; } else if (d < f2) { f2 = d; }
    }
    return sqrt(f2) - sqrt(f1);
}

// Two layers of net, at different scales and drifting different ways, combined by min so the
// borders of either show. A sharp filament plus a wider soft halo: filaments alone read as
// hairlines once they are refracted and absorbed on the way back up.
float Caustics(vec2 p, float t) {
    float a = VoronoiEdge(p * 0.45 + vec2(t * 0.05, 0.0), t * 0.7);
    float b = VoronoiEdge(p * 0.62 + vec2(3.1, -t * 0.04), -t * 0.6);
    float e = min(a, b);
    float sharp = pow(1.0 - clamp(e * 2.0, 0.0, 1.0), 2.5);
    float soft = 1.0 - clamp(e * 0.7, 0.0, 1.0);
    return sharp + 0.2 * soft;
}

// pcg3d (Jarzynski & Olano 2020): three well-mixed 32-bit hashes of an integer cell. Integer
// maths, so a texel far from the origin hashes as well as one beside it.
uvec3 Pcg3d(uvec3 v) {
    v = v * 1664525u + 1013904223u;
    v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
    v ^= v >> 16u;
    v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
    return v;
}

// Texels per block of the snow tile (32 px art), which is the grid the glints sit on.
const float SnowTexels = 32.0;

// Snow sparkle: a few texels of the snow tile carry an ice-crystal facet tilted at random off
// the face, and a facet glints - its texel flashes with mirrored sun - while the half vector
// between the eye and the sun lines up with it. The texel and its facet are a hash of the texel's place in
// texture-pixel space on the face's own plane, so they stay put on the surface, while the half
// vector moves with the camera and the sun: glints come and go as you move. 0..1, before the
// caller's sun gate.
float SnowGlint(vec3 wp, vec3 N, vec3 T, vec3 B, vec3 V) {
    vec2 st = vec2(dot(wp, T), dot(wp, B)) * SnowTexels;
    // Which plane of faces: a face sits on a texel boundary, so the rounded depth is stable on
    // it and differs between a face and the one behind it (the sign follows the normal).
    float plane = floor(dot(wp, N) * SnowTexels + 0.5);
    vec2 texel = floor(st);
    vec3 h = vec3(Pcg3d(uvec3(ivec3(int(texel.x), int(texel.y), int(plane))))) / 4294967295.0;
    if (h.x >= uSnowSparkleDensity) return 0.0;
    // The facet: loose crystals lie every which way, so its normal is a cosine-weighted random
    // direction over the face's hemisphere, picked by the other two hashes. Some facet then
    // catches the sun from any viewpoint the face is lit from, most of them toward the sun.
    float phi = 6.2831853 * h.y;
    float sinT = sqrt(1.0 - h.z);
    vec3 facet = (T * cos(phi) + B * sin(phi)) * sinT + N * sqrt(h.z);
    vec3 H = normalize(V + uSunDir);
    // A narrow mirror lobe (full within ~5 degrees, gone past ~9): only a small share of the
    // facets catch the sun from any one place, and a step of the camera hands the glint to
    // others. The whole texel lights, a square on the art's own pixel grid.
    return smoothstep(0.988, 0.996, dot(facet, H));
}

// Schlick fresnel with the water/wet-surface F0 of 0.06, shared by the ore path and the wet
// sheen. GL-free twin: SurfaceFresnel.Schlick. The water surface keeps its own inline copies,
// which carry the inside-view total-internal-reflection variants.
float Schlick(float cosT) {
    return 0.06 + 0.94 * pow(1.0 - cosT, 5.0);
}

void main() {
    // The reflection pass clips everything below the sea surface out of the mirror.
    if (fWorldPos.y < uClipY) discard;
    // ponytail: the water flag (254) and the opaque-LOD-foliage flag (253) ride in
    // fColor.a (255 = everything else); vertex alpha is otherwise unused. Decode the
    // exact byte so the foliage flag is never mistaken for water.
    int alphaByte = int(fColor.a * 255.0 + 0.5);
    bool isWater = (alphaByte == 254);
    bool lodLeaves = (alphaByte == 253);
    float wob1 = 0.0;
    float wob2 = 0.0;
    if (isWater) {
        // Animated ripple phases; they wobble the lighting normal below, never the UVs
        // (shifting UVs would bleed across atlas tile borders).
        wob1 = sin(fWorldPos.x * 2.1 + uTime * 1.6 + sin(fWorldPos.z * 1.7 + uTime * 1.1));
        wob2 = sin(fWorldPos.z * 2.7 - uTime * 1.9 + sin(fWorldPos.x * 1.3 - uTime * 0.8));
    }
    vec4 texColor = texture(uTextureAtlas, vec3(fTexCoord, float(fLayer)));
    // Leaf tiles have transparent holes; cut them out so the canopy sees through instead of
    // blending ghosts. The cutoff is a uniform: the opaque pass uses 0.5 for the leaf holes,
    // the translucent pass uses 0 so glass's faint interior blends instead of vanishing.
    // Far LOD crowns are opaque instead: holes fall back to the average leaf colour so no
    // sky shows through the magnified texels.
    if (lodLeaves) {
        // A biome-tinted canopy falls back to its textureless tile's grey (sRGB 98/255,
        // WorldGenerationConfig.BiomeTintTextureGrey, decoded) so the tint still applies in the holes.
        vec3 leafRgb = texColor.a < 0.5 ? (fTintGain > 1.0 ? vec3(0.1221) : uLodLeafColor) : texColor.rgb;
        texColor = vec4(leafRgb, 1.0);
    } else if (texColor.a < uAlphaCutoff) discard;
    vec3 albedo = texColor.rgb * fColor.rgb * fTintGain; // biome tint gain, see test-vertex.glsl
    // Hit flash tints the albedo before lighting, so it goes dark with the scene instead of
    // glowing like the emissive uFlash mix at the end.
    albedo = mix(albedo, vec3(1.0, 0.0, 0.0), uHitFlash * 0.6);
    vec3 N = normalize(fNormal);
    float ndl = max(dot(N, uSunDir), 0.0);
    float shadow = SampleShadow(fWorldPos, N, ndl, uShadowStrength);
    // Clouds dim the direct sun only; the ambient below stays untouched.
    float cloudShadow = CloudShadow(fWorldPos);
    shadow *= cloudShadow;
    // Blend between flat and directional so sides stay readable while the sun orbits;
    // the shadow term only ever dims the direct sun, never the ambient below
    vec3 sun = uSunColor * (fSun * uSunIntensity * (0.35 + 0.65 * ndl) * shadow);
    // Gated so unlit fragments (and the LOD horizon, which carries no block light) skip the
    // four-corner flicker lookup entirely
    float flick = fBlock > 0.0 ? BlockFlicker(fWorldPos) : 1.0;
    // The block-light ramp was tuned as a display-space gradient; raising it to ~2.2 in linear
    // light reproduces that falloff instead of letting every lamp pool spread twice as far.
    vec3 torch = fBlockTint * pow(fBlock * uTorchIntensity * flick, 2.2);
    vec3 pl = vec3(0.0);
    for (int i = 0; i < 8; i++) {
        if (i >= uNumPointLights) break;
        vec3 L = uPointPos[i] - fWorldPos;
        float pd = length(L);
        float att = 1.0 / (1.0 + 0.09 * pd + 0.032 * pd * pd);
        // Fires get a range window instead of a literal fBlock gate: a lit campfire or kiln
        // bakes no emission (its lit state is block-entity state), so fBlock is 0 around it
        // and a gate would switch the light off. The window bounds how far a fire leaks
        // through walls, and the half-Lambert facing term darkens a wall's back face while
        // keeping the floor pool bright. TNT strobes (below uFireLightStart) are untouched.
        float w = 1.0;
        if (i >= uFireLightStart && uFireLightRange > 0.0) {
            float r = clamp(1.0 - pd * pd / (uFireLightRange * uFireLightRange), 0.0, 1.0);
            w = r * r * clamp(dot(N, L / max(pd, 1e-4)) * 0.5 + 0.5, 0.0, 1.0);
        }
        pl += uPointColor[i] * (att * w);
    }
    // Ambient is skylight bounce, so it only reaches fragments the sky reaches (fSun).
    // Without this every sealed cave renders at full ambient brightness in daytime.
    // Block and point lights fill in where the sun doesn't reach but never stack on top of
    // it: the direct term is the stronger of the two, so a lamp can't push sunlit ground
    // past white at noon yet still lights caves and the night.
    // Hemispheric ambient: floors face the sky, while ceilings and overhangs see the warm,
    // darker bounce off the ground; side faces get the average of the two.
    vec3 hemi = mix(uGroundAmbient, uAmbient, 0.5 + 0.5 * N.y);
    vec3 light = hemi * (0.12 + 0.88 * fSun) + max(sun, torch + pl);
    // The reflective tiles (the ores) carry their own sheen, so the wet and rim terms skip them
    // rather than stacking a second fresnel on top. fLayer is flat: this resolves per quad.
    bool oreTile = !isWater && uMaterialCount > 0 && int(fLayer) < uMaterialCount
        && uTileMaterials[fLayer].x > 0.0;
    // Wet albedo: a submerged face is wet only at or below its own baked water surface
    // (fWaterSurfaceY), so the dry top of a side face poking out of a thin flow keeps its colour.
    // Water darkens what it soaks, so the albedo drops exactly once, here before the light
    // multiply, and the caustics below see the wet albedo too. GL-free twin:
    // SurfaceFresnel.WetWeight.
    float wet = (isWater || oreTile || fSubmerged < 0.5) ? 0.0 : step(fWorldPos.y, fWaterSurfaceY);
    albedo *= mix(1.0, uWetDarken, wet);
    vec3 col = albedo * light;
    // Wanted by the caustics and the refraction below as well as by the fog at the end
    float dist = distance(fWorldPos, uCameraPos);

    // Reflective surfaces: the ores. fLayer is a flat varying, so this branch resolves the same
    // way for every fragment of a quad and the diffuse tiles never pay for the texture fetch.
    if (oreTile) {
        vec4 tileMat = uTileMaterials[fLayer];
        // Highlights on a 32px tile alias hard once a texel goes subpixel, so the whole path
        // fades out with distance instead of sparkling along the horizon and through the LOD.
        float nearFade = 1.0 - smoothstep(48.0, 112.0, dist);
        if (nearFade > 0.0) {
            vec4 matSample = texture(uMaterialAtlas, vec3(fTexCoord, float(fLayer)));
            float metallic = tileMat.x * matSample.a;
            float roughness = clamp(tileMat.y, 0.02, 1.0);

            // Tangent-space normal. T and B are Chunk.TiledUv's u and v directions, which is
            // the convention the generator writes against (+G = +v).
            vec3 tn = matSample.rgb * 2.0 - 1.0;
            tn.xy *= tileMat.z;
            vec3 Nm = normalize(fTangent * tn.x + fBitangent * tn.y + N * tn.z);
            vec3 Vm = normalize(uCameraPos - fWorldPos);

            // Blinn-Phong exponent from roughness. The textbook 2/r^4 - 2 puts polished obsidian
            // past 150000, where only texels aimed exactly at the half vector light up and the
            // highlight aliases into single-pixel sparkle on a 32px tile; squaring lands it near
            // 550, right alongside the water glitter's own pow(ndh, 300). This is about aliasing,
            // not headroom, so it stays in linear/HDR.
            float shine = 2.0 / (roughness * roughness) - 2.0;
            vec3 Hm = normalize(Vm + uSunDir);
            // Gated by skylight and the shadow exactly as the water glitter is, or the highlight
            // shines through solid rock into sealed caves.
            // Scaled by metallic like the sky term below, and for the same reason: a rough
            // surface's lobe is broad enough to cover most of a face, so an unscaled one adds a
            // near-flat wash instead of a highlight - it turned coal into bright silver.
            float spec = pow(max(dot(Nm, Hm), 0.0), shine) * metallic * fSun * shadow
                       * smoothstep(0.0, 0.2, uSunIntensity);

            // Metals tint what they reflect. Taking the albedo's hue at full brightness rather
            // than the albedo itself matters here: these ores are very dark (obsidian's luma sits
            // near 0.1), so tinting by the raw colour would extinguish the highlight it is meant
            // to colour.
            float peak = max(max(albedo.r, albedo.g), max(albedo.b, 0.001));
            vec3 tint = mix(vec3(1.0), albedo / peak, metallic);

            // Grazing-weighted Schlick, the same one the water surface uses. An F0 derived from
            // metallic would instead mirror the flat sky colour at full strength head-on and
            // flatten obsidian into a pale slab with no material left in it. Dimmed by skylight
            // so there is no sky to reflect underground.
            float F = Schlick(max(dot(Vm, Nm), 0.0));
            vec3 skyColor = uFogColor * clamp(0.12 + 0.88 * fSun, 0.0, 1.0);

            // Metal is a dark body with a bright specular: the body pays for the highlight, so
            // the diffuse darkens by the same 0.6 it did before linear lighting. Easing that off
            // let the lifted body and the broad lobe below smear coal's specks into a grey wash.
            col *= 1.0 - metallic * 0.6 * nearFade;
            // The sky is mixed in, not added. A reflection replaces what the surface would
            // otherwise show, and adding it instead piled a whole extra sky on top of the albedo
            // at grazing angles, where F approaches 1 - obsidian came out pale blue rather than
            // black glass catching the sky. Capped below a full mirror so it keeps its own colour.
            col = mix(col, skyColor, F * metallic * 0.65 * nearFade);
            // Added in linear light, where the display encode lifts a small add on these
            // near-black texels far more than the same add did in display space; uOreSpecScale
            // (WorldGenerationConfig.OreSpecLinearScale) brings the lift back to the old look.
            col += uSunColor * (spec * uSunIntensity * uOreSpecScale) * tint * nearFade;
        }
    }

    // Snow sparkle, on the snow tile only (see SnowGlint). Gated by the direct sun the face
    // really gets - skylight, the shadow map and the cloud shadow (both already in shadow), the
    // sun on the face's own side and up - so the glints vanish in shadow and at night. A glint
    // mirrors the sun disc itself, so it wants nearly the full sun: the shadow term is
    // thresholded rather than scaled, or a shadow the low sun has faded (its strength drops as
    // the sun sets) or a thin cloud would still let dimmed glints through. Faded out
    // with distance before a texel turns subpixel and the glints would only shimmer, and at a
    // graze where the texels squash. Added in linear light like the ore highlight, so on snow
    // already past the tonemap clip (most of a sunny day's flat tops) it is swallowed by the clip.
    if (uSnowSparkle > 0.0 && !isWater && abs(float(fLayer) - uSnowLayer) < 0.5) {
        vec3 Vs = normalize(uCameraPos - fWorldPos);
        float gate = fSun * smoothstep(0.6, 0.95, shadow) * smoothstep(0.0, 0.15, ndl) * smoothstep(0.0, 0.2, uSunIntensity)
                   * (1.0 - smoothstep(14.0, 36.0, dist)) * smoothstep(0.08, 0.3, dot(N, Vs));
        if (gate > 0.0)
            col += uSunColor * (uSunIntensity * uSnowSparkle * gate
                                * SnowGlint(fWorldPos, N, fTangent, fBitangent, Vs));
    }

    // Leaf backlight, on leaf-sway faces only (canopy leaves and rooted plants, bit 5): a leaf
    // between the eye and the sun glows warm, lit through. The lobe is a power of how directly
    // the view ray points at the sun. Whether sunlight reaches this leaf at all is the shadow map
    // read a little toward the sun from it, with the sun as the normal: the faces the eye sees
    // from the dark side face away from the sun and read as self-shadowed at their own position,
    // so only a crown's thin, sunlit rim lets light through while leaves deep in its own shadow
    // stay dark. The shadow pass is untouched. GL-free twin: LeafBacklight.Weight.
    if (uLeafBacklight > 0.0 && fSwayFlag > 0.5) {
        float forward = dot((fWorldPos - uCameraPos) / max(dist, 1e-4), uSunDir);
        float gate = pow(max(forward, 0.0), uLeafBacklightPower) * fSun * cloudShadow
                   * smoothstep(0.0, 0.1, uSunDir.y) * smoothstep(0.0, 0.2, uSunIntensity) * (1.0 - uUnderwater);
        if (gate > 0.001) {
            float through = SampleShadow(fWorldPos + uSunDir * uLeafBacklightReach, uSunDir, 1.0, uShadowStrength);
            col += albedo * uLeafBacklightTint * uSunColor * (uSunIntensity * uLeafBacklight * gate * through);
        }
    }

    // Grass wind shimmer, on biome-tinted up faces only (the grass tops: bit 4, face 4; never the
    // swaying canopy nor the far crowns): soft bright bands lie across the wind and roll downwind
    // with the leaf sway's gusts. The pattern is read at the position carried back by the gusts'
    // travelled offset (uWindOffset), exactly as the sway samples its gust wave, and weighted by
    // that same gust, so the bands travel and swell with the canopy. It brightens the lit grass by
    // a share of its own light, so it darkens with shade and night, and fades out with distance
    // before a band turns into shimmering pixels. GL-free twin: GrassShimmer.
    if (uGrassShimmer > 0.0 && fTintGain > 1.0 && fSwayFlag < 0.5 && !lodLeaves && N.y > 0.5) {
        float fade = 1.0 - smoothstep(uGrassShimmerFadeNear, uGrassShimmerFadeFar, dist);
        if (fade > 0.0) {
            vec2 wd = uWind / max(length(uWind), 1e-6);
            vec2 g = fWorldPos.xz - uWindOffset;
            float along = dot(g, wd);
            float across = dot(g, vec2(-wd.y, wd.x));
            float k = 6.2831853 / uGrassShimmerWavelength;
            float wave = sin(along * k + 1.5 * sin(across * k * 0.31 + along * k * 0.17));
            float gust = 0.6 + 0.4 * sin(uTime * 0.5 + g.x * 0.08 + g.y * 0.06);
            col += albedo * light * (uGrassShimmer * fade * gust * smoothstep(0.35, 1.0, wave));
        }
    }

    // Ice glint, on the ice tile only (a separate tile term like the snow sparkle, not the ore
    // material path): a Schlick-weighted mirror of the sky in the reflected direction, mixed in like
    // the ore's sky so a grazing face turns sky-coloured rather than piling a sky on top, and a Blinn
    // sun highlight added in linear light. The sky is dimmed by skylight (nothing to mirror
    // underground) and the highlight is gated by the direct sun the face really gets: skylight, the
    // shadow and cloud shadow, the sun on the face's own side. GL-free twin: IceGlint.
    if (uIceGlint > 0.0 && !isWater && abs(float(fLayer) - uIceLayer) < 0.5) {
        vec3 Vi = normalize(uCameraPos - fWorldPos);
        vec3 skyR = mix(SkySample(reflect(-Vi, N)), uFogColor, uFogWet) * fSun;
        col = mix(col, skyR, Schlick(max(dot(Vi, N), 0.0)) * uIceSheen);
        float spec = pow(max(dot(N, normalize(Vi + uSunDir)), 0.0), uIceShine)
                   * fSun * shadow * smoothstep(0.0, 0.15, ndl) * smoothstep(0.0, 0.2, uSunIntensity);
        col += uSunColor * (uSunIntensity * uIceGlint * spec);
    }

    // Glass sheen, on the glass tile only (glass blocks and panes), which draws blended in the
    // translucent pass: at a graze the pane mirrors the sky. Glass is mostly see-through, so tinting
    // its colour alone would vanish under its low alpha; instead the sky is folded into the blend
    // itself, raising the alpha to a + F - aF and the colour to (c a (1 - F) + sky F) / alpha, which
    // over any background is exactly the plain glass composite mixed toward the sky by F (the Schlick
    // fresnel times uGlassSheen). abs() reads a face seen from inside the cube the same way, and the
    // mirrored sky is dimmed by skylight, so there is nothing to mirror underground. GL-free twin:
    // GlassSheen.
    float glassAlpha = -1.0;
    if (uGlassSheen > 0.0 && !isWater && abs(float(fLayer) - uGlassLayer) < 0.5) {
        vec3 Vg = normalize(uCameraPos - fWorldPos);
        float side = dot(Vg, N);
        vec3 Ng = side < 0.0 ? -N : N;
        float F = Schlick(abs(side)) * uGlassSheen;
        vec3 skyG = mix(SkySample(reflect(-Vg, Ng)), uFogColor, uFogWet) * fSun;
        float a = texColor.a;
        glassAlpha = a + F - a * F;
        col = (col * (a * (1.0 - F)) + skyG * F) / max(glassAlpha, 1e-4);
    }

    if (fSubmerged > 0.5 && !isWater && dist < 96.0 && fWorldPos.y <= fWaterSurfaceY) {
        // Where on the surface did this sunlight come through? Follow the sun back up to this
        // face's own water surface (baked by the mesher), not the sea plane, so a pond above sea
        // level gets its net too and walls match the floor beside them.
        float below = max(fWaterSurfaceY - fWorldPos.y, 0.0);
        vec2 cp = fWorldPos.xz - uSunDir.xz / max(uSunDir.y, 0.2) * below;
        float causticShadow = SampleShadow(fWorldPos, N, ndl, uShadowStrengthDry);
        float fade = (1.0 - smoothstep(48.0, 96.0, dist)) * exp(-0.06 * below);
        // Caustics redistribute the sunlight: the cells lose a little and the filaments gain
        // focused sun on top. In the HDR target the filaments may go past 1 and roll off
        // through the tonemap clamp, so the net reads on brightly lit sand too.
        // Seen from above, the surface adds its own glitter on top, so the net backs off
        float w = fSun * causticShadow * cloudShadow * fade * uWaterCausticStrength
            * smoothstep(0.1, 0.6, uSunIntensity) * mix(0.6, 1.0, uUnderwater);
        col *= 1.0 - 0.25 * w;
        col += albedo * uSunColor * (uSunIntensity * w * Caustics(cp, uTime) * 1.2);
    }

    float waterSpec = 0.0;
    bool isLava = isWater && abs(float(fLayer) - uLavaLayer) < 0.5;
    if (isLava) {
        // Molten rock glows on its own instead of refracting: albedo-tinted emission with
        // a slow pulse, flat in shadow and at night. Fog still closes in below.
        // Only the molten part of the tile glows: the additive floor is a saturated orange
        // constant, so applying it to every texel lifted the dark rock blobs in the lava
        // tile into a pale salmon (their near-neutral albedo plus the orange floor adds a
        // near-equal amount to all three channels). Weigh it by how orange the texel is -
        // rock stays near-neutral, so it keeps its own dark colour and only magma glows.
        // The glow is evaluated on the display-space (sRGB) albedo and clipped to 1 there, then
        // decoded, exactly as 28198cc's 8-bit target saw it: doubling the *linear* albedo and
        // adding a decoded floor pushes red far past green, so the tonemap clamp left the lava
        // saturated red instead of the old orange-yellow.
        vec3 albedoS = LinearToSrgb(albedo);
        float molten = clamp((albedoS.r - max(albedoS.g, albedoS.b)) * 4.0, 0.0, 1.0);
        float pulse = 0.9 + 0.1 * sin(uTime * 2.0 + fWorldPos.x * 1.3 + fWorldPos.z * 1.7);
        col = SrgbToLinear(min(mix(albedoS, albedoS * 2.0 + vec3(0.55, 0.13, 0.01), molten) * pulse, vec3(1.0)));
        // Glow pulse: the whole surface breathes in brightness on a slow noise field, in linear
        // light after the display-space clip above, so the dimming phase reaches the clipped molten
        // texels too while the brightening one pushes them toward white through the tonemap.
        if (uLavaPulse > 0.0)
            col *= 1.0 + uLavaPulse * (2.0 * LavaPulseField(fWorldPos.xz, uTime) - 1.0);
        // A glossy crust catches the nearby fires, kilns and TNT as small highlights riding the
        // swell, plus a faint fresnel sheen of the sky. The torch/block channel is excluded:
        // lava's own emission saturates fBlock on every lava fragment, so it carries no direction.
        vec2 lg = LavaWaveSlope(fWorldPos.xz, uTime);
        vec3 nL = N.y > 0.5 ? normalize(vec3(-lg.x * uLavaNormalScale, 1.0, -lg.y * uLavaNormalScale)) : N;
        vec3 Vl = normalize(uCameraPos - fWorldPos);
        vec3 spec = vec3(0.0);
        for (int i = 0; i < 8; i++) {
            if (i >= uNumPointLights) break;
            vec3 L = uPointPos[i] - fWorldPos;
            float pd = length(L);
            vec3 Ld = L / max(pd, 1e-4);
            float att = 1.0 / (1.0 + 0.09 * pd + 0.032 * pd * pd);
            // Same fire range window as the diffuse point-light loop above.
            float w = 1.0;
            if (i >= uFireLightStart && uFireLightRange > 0.0) {
                float r = clamp(1.0 - pd * pd / (uFireLightRange * uFireLightRange), 0.0, 1.0);
                w = r * r * clamp(dot(nL, Ld) * 0.5 + 0.5, 0.0, 1.0);
            }
            spec += uPointColor[i] * (att * w) * pow(max(dot(nL, normalize(Ld + Vl)), 0.0), uLavaCatchPower);
        }
        spec += uAmbient * fSun * (0.04 + 0.96 * pow(1.0 - max(dot(nL, Vl), 0.0), 5.0)) * 0.25;
        col += spec * uLavaCatchLight * (1.0 - 0.6 * molten);
    } else if (isWater) {
        // Far water skips the wave/reflection/refraction model below: past the full-detail
        // ring each pixel covers metres of surface, so a geometric-normal fresnel between
        // saturated scatter and the sky mirror reads the same for no texture fetches. The
        // Near->Far band blends the two so the switch never pops; lava keeps its glow path
        // above regardless of distance.
        float fancy = 1.0 - smoothstep(uWaterFancyNear, uWaterFancyFar, dist);
        vec3 waterLightCheap = uAmbient * (0.12 + 0.88 * fSun) + uSunColor * (uSunIntensity * fSun * shadow * 0.6)
                     + (torch + pl) * 0.6;
        float skyDim = clamp(0.12 + 0.88 * fSun, 0.0, 1.0);
        vec3 vCheap = normalize(uCameraPos - fWorldPos);
        float cosTcheap = max(dot(vCheap, N), 0.0);
        float fCheap = 0.06 + 0.94 * pow(1.0 - cosTcheap, 5.0);
        vec3 cheap = mix(uWaterScatter * waterLightCheap, uFogColor * skyDim, fCheap);
        if (fancy <= 0.0) {
            col = cheap;
            waterSpec = 0.0;
        } else {
        // WaveNormal always points up: correct for the surface top, but using it for a
        // vertical waterfall pins V.N near 0, so the Schlick fresnel below saturates to 1
        // and every underground fall becomes a full mirror of the (daytime) sky fallback.
        // Tops keep the wave normal; sides keep their geometric normal and borrow only
        // the wave tilt, so a fall viewed head-on mostly refracts instead of mirroring.
        vec3 geomN = normalize(fNormal);
        vec3 V = normalize(uCameraPos - fWorldPos);
        // Which side of this face the eye is on, per fragment rather than against the sea plane:
        // a pond stands above sea level, and one frame can hold several bodies at different heights.
        bool fromBehind = dot(geomN, V) < 0.0;
        vec3 nWup = FlowWaveNormal(fWorldPos.xz, uTime, uWaterNormalScale, fWaterFlow.xy);
        nWup = normalize(nWup + vec3(wob1 * 0.09, 0.0, wob2 * 0.09));   // fine glitter on top of the swell
        vec2 tilt = nWup.xz / max(nWup.y, 0.2);
        vec3 nW;
        if (geomN.y > 0.5) {
            nW = nWup;
        } else {
            vec3 sideN = geomN + vec3(tilt.x, 0.0, tilt.y) * 0.35 + vec3(wob1 * 0.03, 0.0, wob2 * 0.03);
            // Falling water: vertical streaks running down the face, faster than any ripple.
            // Keyed off the per-quad falling bit, so a still or merely flowing side takes nothing.
            if (fWaterFlow.z > 0.5) {
                vec3 tangent = vec3(-geomN.z, 0.0, geomN.x);
                float u = dot(fWorldPos.xz, tangent.xz);
                float v = fWorldPos.y + uTime * uWaterFallScroll;
                float streak = sin(u * 7.3 + sin(v * 0.9) * 1.5) * 0.6 + sin(u * 13.1 - v * 0.4) * 0.4;
                sideN += tangent * (streak * uWaterFallStreak)
                       + vec3(0.0, sin(v * 2.3) * uWaterFallStreak * 0.3, 0.0);
            }
            nW = normalize(sideN);
        }
        // Seen from behind the wave normal has to face the eye as well, or V.N pins at 0 and the
        // fresnel saturates into a full mirror - which from under the sea was a flat sheet of fog.
        if (fromBehind) nW = -nW;
        float cosT = max(dot(V, nW), 0.0);
        float F;
        if (fromBehind) {
            // Inside looking out: Snell's window. Past the critical angle (sin > 1/eta, ~48.6
            // degrees off the normal) nothing gets through and the underside is a mirror of the
            // water below; inside it, Schlick on the TRANSMITTED angle, which is what a
            // dense-to-thin interface reflects by. The rise to 1 is steep but continuous, and
            // the wave normal ripples its edge.
            float sinT2 = WaterEta * WaterEta * (1.0 - cosT * cosT);
            F = sinT2 >= 1.0 ? 1.0 : 0.06 + 0.94 * pow(1.0 - sqrt(1.0 - sinT2), 5.0);
        } else {
            F = 0.06 + 0.94 * pow(1.0 - cosT, 5.0);
        }

        // Refraction: look up the scene copy a little off to the side of this pixel, along the
        // wave normal. If that spot is in front of the surface (a post, the shore) it is not
        // behind the water, so fall back to the straight-down sample.
        vec2 uv = gl_FragCoord.xy * uInvViewport;
        float refractStrength = uWaterRefractStrength * (fromBehind ? uWaterWindowRefract : 1.0);
        vec2 off = nW.xz * (refractStrength / (1.0 + dist * 0.15));
        vec2 ruv = uv + FadeTapOffset(uv, off);
        // Safety net only: the edge fade above is what keeps the tap off the border texel (and
        // with it the stretched-edge streak); this narrow clamp never engages in practice.
        ruv = clamp(ruv, vec2(0.0005), vec2(0.9995));
        float refrDepth = texture(uSceneDepth, ruv).r;
        if (refrDepth < gl_FragCoord.z) { ruv = uv; refrDepth = texture(uSceneDepth, uv).r; }
        vec3 behind = texture(uSceneColor, ruv).rgb;

        // How much water the eye looks through: the distance from this surface point to what the
        // scene copy saw behind it. The reconstruction inverts the exact projection that wrote
        // the depth (Direct3D 0..1 style from System.Numerics), so no near/far formula is needed.
        vec4 hp = uInvViewProj * vec4(ruv * 2.0 - 1.0, refrDepth * 2.0 - 1.0, 1.0);
        vec3 scenePos = hp.xyz / hp.w;
        // From behind, the scene copy beyond the surface is air: there is no water to absorb
        // through. The water between the eye and the surface is what the underwater fog
        // (uFogNear/uFogFar) already charges for.
        float thickness = fromBehind ? 0.0 : distance(scenePos, fWorldPos) * (1.0 - uUnderwater);

        // Shore foam: a thin pale band that rises as that depth goes to zero, i.e. where the
        // surface meets a block, a bank or a thin trickle. Bounded by uWaterFoamWidth, so open
        // water never foams; never from below (the Snell window) nor on a side face, and never
        // with the eye submerged. The ripple phases (wob) jiggle it and the flow map carries it
        // downstream, so the ragged edge moves; the contact line itself stays mostly solid, with
        // a gap wherever the pattern peaks (the threshold reaches past 1 there). It fades
        // out over the same 64..160 blocks as the vertex waves: a band a third of a block wide
        // turns subpixel out there and would only shimmer.
        float foam = 0.0;
        float foamFade = 1.0 - smoothstep(64.0, 160.0, dist);
        if (!fromBehind && geomN.y > 0.5 && uWaterFoamWidth > 0.0 && foamFade > 0.0) {
            float edge = 1.0 - distance(scenePos, fWorldPos) / uWaterFoamWidth;
            if (edge > 0.0) {
                vec2 fp = fWorldPos.xz + vec2(wob1, wob2) * 0.08;
                float n = mix(0.15, 1.05, FlowFoam(fp, uTime, fWaterFlow.xy));
                foam = smoothstep(n - 0.1, n + 0.1, edge) * uWaterFoamStrength * (1.0 - uUnderwater) * foamFade;
            }
        }

        vec3 waterLight = uAmbient * (0.12 + 0.88 * fSun) + uSunColor * (uSunIntensity * fSun * shadow * 0.6)
                          + (torch + pl) * 0.6;
        vec3 refr = behind * exp(-uWaterAbsorb * thickness)
                  + uWaterScatter * waterLight * (1.0 - exp(-0.1 * thickness));

        // From behind, this fallback IS the look: past the critical angle the underside mirrors
        // the water body below, and the underwater fog colour (already blended to the tint by
        // SetLighting, dimmed by skylight just below) stands in for that scene. The sea-level
        // mirror is only valid for a face seen from above, whatever height the eye is at.
        vec3 refl = uFogColor;
        if (uReflValid > 0.5 && !fromBehind) {
            vec4 rp = uReflViewProj * vec4(fWorldPos, 1.0);
            vec2 rbase = rp.xy / rp.w * 0.5 + 0.5;
            vec2 rr = rbase + FadeTapOffset(rbase, nW.xz * 0.08);
            // Same safety net as the refraction tap; the fade is what prevents the streak.
            refl = texture(uReflectionMap, clamp(rr, 0.0005, 0.9995)).rgb;
        }
        // No sky to mirror underground: dim the reflection (fallback or stale mirror
        // alike) by the voxel skylight, or sealed caves glow sky-blue in daytime.
        refl *= skyDim;   // the cheap path's clamp(0.12 + 0.88 * fSun) above
        // Off the sea plane (a pond above sea level) the planar mirror reflects the wrong
        // height, so a short screen-space march takes over by the surface's plane error.
        // Fluid quads carry flow codes rather than fWaterSurfaceY, so the fragment's own Y is
        // its local surface. Hits are already-lit scene colour and stay undimmed - a torch-lit
        // cave pool must not reflect black; only the miss colour takes the skylight dim.
        if (!fromBehind && uWaterSsrMode > 0.5) {
            float planeErr = abs(fWorldPos.y - uWaterPlaneY);
            float ssrW = uWaterSsrMode > 1.5 ? 1.0 : smoothstep(uWaterSsrPlaneNear, uWaterSsrPlaneFar, planeErr);
            if (ssrW > 0.001) {
                vec3 R = reflect(-V, nW);
                R.y = max(R.y, 0.02);
                R = normalize(R);
                vec3 miss = mix(SkySample(R), uFogColor, uFogWet) * skyDim;
                refl = mix(refl, WaterSsr(fWorldPos, R, miss), ssrW);
            }
        }
        vec3 fancyCol = mix(refr, refl, F);

        vec3 H = normalize(V + uSunDir);
        float ndh = max(dot(nW, H), 0.0);
        // Sun glitter needs skylight to exist: without the fSun gate it shines through rock.
        // From below the sun is seen refracted through the window (it is in the scene copy),
        // not mirrored off it, so there is no glitter on that side.
        waterSpec = fromBehind ? 0.0 : (pow(ndh, 300.0) * 0.45 + pow(ndh, 24.0) * 0.05) * shadow * fSun;
        col = mix(cheap, fancyCol, fancy);
        waterSpec *= fancy;
        col += uSunColor * (waterSpec * uSunIntensity);
        // The foam is a pale diffuse film over the surface: lit exactly like an opaque face (the
        // shared light term, so it darkens in shadow, cloud shadow and at night), mixed over the
        // composed water in linear light like the fresnel mix above, and fogged by the common
        // fog below. It covers the glitter it sits on. (foamFade ends well inside the fancy
        // band, so the cheap far path never needs it.)
        col = mix(col, uWaterFoamColor * light, foam);
        }
    }

    float f = clamp((dist - uFogNear) / (uFogFar - uFogNear), 0.0, 1.0);
    // Fog toward the dome's own colour in the view direction, so a fully fogged fragment is
    // exactly the sky behind it (seamless LOD edge against the graded sky). The outer branch
    // keeps the cost at zero inside FogNear; underwater (uFogWet ~1) the sample would be
    // thrown away, so it is skipped. In the mirror pass uCameraPos is the mirrored eye.
    if (f > 0.0) {
        vec3 fogCol = uFogColor;
        if (uFogWet < 0.999) {
            vec3 fogDir = (fWorldPos - uCameraPos) / max(dist, 1e-4);
            fogCol = mix(SkySample(fogDir), uFogColor, uFogWet);
        }
        // Squared ramp, mixed in display space in dry air: the same f*f mix done in linear
        // light reads visibly hazier (a half-fogged dark forest lands far brighter once
        // encoded), so the dry mix encodes both ends, mixes, and decodes again - the pre-SH-10
        // look. The display mix clamps the fogged colour to 1 first (only fogged fragments pay
        // that clamp). Underwater and in lava (uFogWet 1) it stays the linear mix; the two are
        // blended by uFogWet rather than branched, so the wet transition never pops. Both still
        // reach exactly fogCol at uFogFar, which is what keeps the LOD edge seamless.
        float ff = f * f;
        vec3 linearMix = mix(col, fogCol, ff);
        vec3 displayMix = SrgbToLinear(mix(LinearToSrgb(min(col, vec3(1.0))),
                                           LinearToSrgb(min(fogCol, vec3(1.0))), ff));
        col = mix(displayMix, linearMix, uFogWet);
    }
    // Tile alpha carries the translucent mesh's translucency; fAlpha is the chunk fade-in.
    // The discard above still removes the leaf tiles' fully transparent holes in the
    // opaque pass, while the translucent pass keeps every faint texel.
    // Water composited the refracted scene itself, so nothing is left for the blender to
    // show through: its only alpha is the chunk's stream-in fade.
    float texAlpha = texColor.a;
    // The glass sheen folded the mirrored sky into the blend (see above)
    if (glassAlpha >= 0.0) texAlpha = glassAlpha;
#ifdef CHUNK_DRAW
    // The atlas samples its mips, so a leaf texel that survived the cutoff can still carry a
    // filtered alpha below 1; blended as-is the sky would tint the canopy through it. In the
    // alpha-tested opaque chunk pass a surviving fragment is opaque. Only the chunk program:
    // the plain one also draws the kiln's molten pool, whose tile alpha is meant to blend.
    if (uAlphaCutoff > 0.0) texAlpha = 1.0;
#endif
    float alpha = texAlpha * fAlpha;
    if (isWater) alpha = fAlpha;
    // An HDR white well past 1, so a primed TNT strobes to display white after the clamp.
    vec3 outCol = mix(col, vec3(4.0), uFlash);
    // No dither here: the frame is dithered once, after the tonemap, in post-fragment.glsl.
    FragColor = vec4(outCol, alpha);
}
