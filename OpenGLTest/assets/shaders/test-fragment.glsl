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
// in-scattered water colour is what makes shallows read clear and depth read teal. The planar
// reflection is mixed in by a Schlick fresnel, so the surface is transparent underfoot and a
// mirror at a graze. Opaque faces that look into water (bit 3 of the packed normal) get the
// caustic net on top, projected back up the sun direction to the sea plane.
in vec4 fColor;
in vec2 fTexCoord;
flat in uint fLayer;
in float fSun;
in float fBlock;
in vec3 fNormal;
in vec3 fWorldPos;
in float fSubmerged;
flat in float fAlpha; // per-draw fade from the vertex stage: a uniform cannot vary inside one multi-draw

// One slice per block tile; UVs are tile-local and REPEAT across merged quads.
uniform sampler2DArray uTextureAtlas;
uniform float uFlash;
uniform float uHitFlash;
uniform float uAlphaCutoff;
uniform float uTime;
uniform vec3 uSunDir;
uniform vec3 uSunColor;
uniform float uSunIntensity;
uniform vec3 uAmbient;
uniform vec3 uTorchColor;
uniform float uTorchIntensity;
uniform vec3 uFogColor;
uniform float uFogNear;
uniform float uFogFar;
uniform vec3 uCameraPos;
uniform int uNumPointLights;
uniform vec3 uPointPos[8];
uniform vec3 uPointColor[8];
uniform sampler2DShadow uShadowMap;
uniform mat4 uLightViewProj;
uniform sampler2D uReflectionMap;
uniform mat4 uReflViewProj;
uniform float uWaterPlaneY;
uniform float uReflValid;
uniform float uClipY;
uniform float uShadowStrength;
uniform float uShadowTexel;
uniform float uShadowWorldTexel;
uniform float uShadowSoftness;
uniform float uShadowNormalOffset;

// The frame as it stood just before the water pass: colour to refract, depth to measure the
// water's thickness with. Plain samplers, no compare mode - uSceneDepth is read for its value.
uniform sampler2D uSceneColor;
uniform sampler2D uSceneDepth;
uniform vec2 uInvViewport;
uniform mat4 uInvViewProj;
// The shadow strength before the underwater fade: the caustics still want crisp sun shadows
// even once the diffuse has stopped using them.
uniform float uShadowStrengthDry;
uniform float uUnderwater;
uniform float uWaterNormalScale;
uniform float uWaterRefractStrength;
uniform vec3 uWaterAbsorb;
uniform vec3 uWaterScatter;
uniform float uWaterCausticStrength;
uniform float uWaterFancyNear;
uniform float uWaterFancyFar;
// Average opaque colour of the leaves tile, for LOD foliage below: far crowns are
// drawn opaque (no cutout holes) and transparent texels fall back to this.
uniform vec3 uLodLeafColor;
// Atlas slice of the lava tile: fluid fragments on this layer glow instead of refracting.
uniform float uLavaLayer;

out vec4 FragColor;

// Directional sun shadows. RenderManager renders the casters depth-only through
// uLightViewProj into uShadowMap, so a fragment's own light-space depth compared against
// what is stored there says whether something else got the sunlight first. The ortho
// projection puts clip z in 0..1 exactly like GL's depth write, so all three components
// map with *0.5+0.5 and the comparison is apples to apples.
float SampleShadow(vec3 wp, vec3 N, float ndl, float strength) {
    if (strength <= 0.0) return 1.0;
    // Normal offset: axis-aligned voxel faces at grazing angles otherwise show acne
    vec3 p = wp + N * (uShadowWorldTexel * uShadowNormalOffset * (1.0 - ndl));
    vec4 lp = uLightViewProj * vec4(p, 1.0);
    vec3 ndc = lp.xyz / lp.w;
    vec3 uvz = ndc * 0.5 + 0.5;                 // all three components -> [0,1]
    float edge = max(abs(ndc.x), abs(ndc.y));
    float fade = 1.0 - smoothstep(0.85, 1.0, edge);   // soften the ortho-box boundary
    if (fade <= 0.0 || uvz.z > 1.0) return 1.0;
    uvz.z -= 0.0008;
    if (uShadowSoftness <= 0.0) return mix(1.0, texture(uShadowMap, uvz), fade * strength);
    // Four taps on the diagonals, half a texel out. The sampler filters linearly, so each
    // fetch is already a hardware 2x2 comparison and the four overlap into a smooth ~3x3
    // tent — the same softness as the old (2s+1)^2 loop for 4 fetches instead of 9.
    float o = 0.5 * uShadowTexel * uShadowSoftness;
    float lit = texture(uShadowMap, vec3(uvz.x - o, uvz.y - o, uvz.z))
              + texture(uShadowMap, vec3(uvz.x + o, uvz.y - o, uvz.z))
              + texture(uShadowMap, vec3(uvz.x - o, uvz.y + o, uvz.z))
              + texture(uShadowMap, vec3(uvz.x + o, uvz.y + o, uvz.z));
    return mix(1.0, lit * 0.25, fade * strength);
}

// Analytic slope of WaveHeight (same terms, differentiated), scaled up: the geometry moves a few
// centimetres but the shading wants a visibly rippled surface.
vec3 WaveNormal(vec2 p, float t, float k) {
    vec2 g = vec2(0.0);
    g += 0.035 * 0.9 * cos(dot(p, vec2(0.9, 0.4)) * 0.9 + t * 1.6) * vec2(0.9, 0.4);
    g += 0.025 * 1.4 * cos(dot(p, vec2(-0.5, 0.85)) * 1.4 + t * 2.1) * vec2(-0.5, 0.85);
    g += 0.015 * 2.6 * cos(dot(p, vec2(0.3, -0.95)) * 2.6 + t * 2.9) * vec2(0.3, -0.95);
    g += 0.010 * 4.1 * cos(dot(p, vec2(-0.8, -0.6)) * 4.1 + t * 3.7) * vec2(-0.8, -0.6);
    return normalize(vec3(-g.x * k, 1.0, -g.y * k));
}

vec2 Hash2(vec2 p) {
    vec3 q = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973));
    q += dot(q, q.yzx + 33.33);
    return fract((q.xx + q.yz) * q.zy);
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
        vec3 leafRgb = texColor.a < 0.5 ? uLodLeafColor : texColor.rgb;
        texColor = vec4(leafRgb, 1.0);
    } else if (texColor.a < uAlphaCutoff) discard;
    vec3 albedo = texColor.rgb * fColor.rgb;
    // Hit flash tints the albedo before lighting, so it goes dark with the scene instead of
    // glowing like the emissive uFlash mix at the end.
    albedo = mix(albedo, vec3(1.0, 0.0, 0.0), uHitFlash * 0.6);
    vec3 N = normalize(fNormal);
    float ndl = max(dot(N, uSunDir), 0.0);
    float shadow = SampleShadow(fWorldPos, N, ndl, uShadowStrength);
    // Blend between flat and directional so sides stay readable while the sun orbits;
    // the shadow term only ever dims the direct sun, never the ambient below
    vec3 sun = uSunColor * (fSun * uSunIntensity * (0.35 + 0.65 * ndl) * shadow);
    float flick = 0.9 + 0.1 * sin(uTime * 9.0) * sin(uTime * 7.3 + 1.7);
    vec3 torch = uTorchColor * (fBlock * uTorchIntensity * flick);
    vec3 pl = vec3(0.0);
    for (int i = 0; i < 8; i++) {
        if (i >= uNumPointLights) break;
        vec3 L = uPointPos[i] - fWorldPos;
        float pd = length(L);
        float att = 1.0 / (1.0 + 0.09 * pd + 0.032 * pd * pd);
        pl += uPointColor[i] * att;
    }
    // Ambient is skylight bounce, so it only reaches fragments the sky reaches (fSun).
    // Without this every sealed cave renders at full ambient brightness in daytime.
    // Block and point lights fill in where the sun doesn't reach but never stack on top of
    // it: the direct term is the stronger of the two, so a lamp can't push sunlit ground
    // past white at noon yet still lights caves and the night.
    vec3 light = uAmbient * (0.12 + 0.88 * fSun) + max(sun, torch + pl);
    vec3 col = albedo * light;
    // Wanted by the caustics and the refraction below as well as by the fog at the end
    float dist = distance(fWorldPos, uCameraPos);

    if (fSubmerged > 0.5 && !isWater && dist < 96.0) {
        // Where on the surface did this sunlight come through? Follow the sun back up to the
        // sea plane so walls get the same pattern as the floor beside them.
        float below = max(uWaterPlaneY - fWorldPos.y, 0.0);
        vec2 cp = fWorldPos.xz - uSunDir.xz / max(uSunDir.y, 0.2) * below;
        float causticShadow = SampleShadow(fWorldPos, N, ndl, uShadowStrengthDry);
        float fade = (1.0 - smoothstep(48.0, 96.0, dist)) * exp(-0.06 * below);
        // Caustics redistribute the sunlight rather than add to it: the cells go a little
        // darker and the filaments brighter, so the net still reads on sand the sun has
        // already lit to full brightness (there is no HDR headroom to add into).
        // Seen from above, the surface adds its own glitter on top, so the net backs off
        float w = fSun * causticShadow * fade * uWaterCausticStrength
            * smoothstep(0.1, 0.6, uSunIntensity) * mix(0.6, 1.0, uUnderwater);
        col *= 1.0 + w * (Caustics(cp, uTime) * 1.2 - 0.45);
    }

    float waterSpec = 0.0;
    bool isLava = isWater && abs(float(fLayer) - uLavaLayer) < 0.5;
    if (isLava) {
        // Molten rock glows on its own instead of refracting: albedo-tinted emission with
        // a slow pulse, flat in shadow and at night. Fog still closes in below.
        float pulse = 0.9 + 0.1 * sin(uTime * 2.0 + fWorldPos.x * 1.3 + fWorldPos.z * 1.7);
        col = (albedo * 2.0 + vec3(0.55, 0.13, 0.01)) * pulse;
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
        vec3 nWup = WaveNormal(fWorldPos.xz, uTime, uWaterNormalScale);
        nWup = normalize(nWup + vec3(wob1 * 0.09, 0.0, wob2 * 0.09));   // fine glitter on top of the swell
        vec2 tilt = nWup.xz / max(nWup.y, 0.2);
        vec3 nW = geomN.y > 0.5
            ? nWup
            : normalize(geomN + vec3(tilt.x, 0.0, tilt.y) * 0.35 + vec3(wob1 * 0.03, 0.0, wob2 * 0.03));
        vec3 V = normalize(uCameraPos - fWorldPos);
        float cosT = max(dot(V, nW), 0.0);
        float F = 0.06 + 0.94 * pow(1.0 - cosT, 5.0);

        // Refraction: look up the scene copy a little off to the side of this pixel, along the
        // wave normal. If that spot is in front of the surface (a post, the shore) it is not
        // behind the water, so fall back to the straight-down sample.
        vec2 uv = gl_FragCoord.xy * uInvViewport;
        vec2 ruv = uv + nW.xz * (uWaterRefractStrength / (1.0 + dist * 0.15));
        ruv = clamp(ruv, vec2(0.001), vec2(0.999));
        float refrDepth = texture(uSceneDepth, ruv).r;
        if (refrDepth < gl_FragCoord.z) { ruv = uv; refrDepth = texture(uSceneDepth, uv).r; }
        vec3 behind = texture(uSceneColor, ruv).rgb;

        // How much water the eye looks through: the distance from this surface point to what the
        // scene copy saw behind it. The reconstruction inverts the exact projection that wrote
        // the depth (Direct3D 0..1 style from System.Numerics), so no near/far formula is needed.
        vec4 hp = uInvViewProj * vec4(ruv * 2.0 - 1.0, refrDepth * 2.0 - 1.0, 1.0);
        vec3 scenePos = hp.xyz / hp.w;
        float thickness = distance(scenePos, fWorldPos) * (1.0 - uUnderwater);

        vec3 waterLight = uAmbient * (0.12 + 0.88 * fSun) + uSunColor * (uSunIntensity * fSun * shadow * 0.6)
                          + (torch + pl) * 0.6;
        vec3 refr = behind * exp(-uWaterAbsorb * thickness)
                  + uWaterScatter * waterLight * (1.0 - exp(-0.1 * thickness));

        vec3 refl = uFogColor;
        if (uReflValid > 0.5 && uCameraPos.y > uWaterPlaneY) {
            vec4 rp = uReflViewProj * vec4(fWorldPos, 1.0);
            vec2 rr = rp.xy / rp.w * 0.5 + 0.5 + nW.xz * 0.08;
            refl = texture(uReflectionMap, clamp(rr, 0.001, 0.999)).rgb;
        }
        // No sky to mirror underground: dim the reflection (fallback or stale mirror
        // alike) by the voxel skylight, or sealed caves glow sky-blue in daytime.
        refl *= clamp(0.12 + 0.88 * fSun, 0.0, 1.0);
        vec3 fancyCol = mix(refr, refl, F);

        vec3 H = normalize(V + uSunDir);
        float ndh = max(dot(nW, H), 0.0);
        // Sun glitter needs skylight to exist: without the fSun gate it shines through rock.
        waterSpec = (pow(ndh, 300.0) * 0.45 + pow(ndh, 24.0) * 0.05) * shadow * fSun;
        col = mix(cheap, fancyCol, fancy);
        waterSpec *= fancy;
        col += uSunColor * (waterSpec * uSunIntensity);
        }
    }

    float f = clamp((dist - uFogNear) / (uFogFar - uFogNear), 0.0, 1.0);
    col = mix(col, uFogColor, f * f);
    // Tile alpha carries the translucent mesh's translucency; fAlpha is the chunk fade-in.
    // The discard above still removes the leaf tiles' fully transparent holes in the
    // opaque pass, while the translucent pass keeps every faint texel.
    // Water composited the refracted scene itself, so nothing is left for the blender to
    // show through: its only alpha is the chunk's stream-in fade.
    float alpha = texColor.a * fAlpha;
    if (isWater) alpha = fAlpha;
    FragColor = vec4(mix(col, vec3(1.0), uFlash), alpha);
}
