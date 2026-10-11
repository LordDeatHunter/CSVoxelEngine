#version 460 core
// Godrays: light shafts through trees, clouds and gaps in dry air, the underwater shafts' march
// (rays-fragment.glsl) run over air. For every pixel the scene depth says how far the eye can see;
// the shader marches that segment (capped at uMaxDistance, inside the near shadow cascade) in
// uSteps jittered samples and adds the sunlight that reaches each point: the near cascade's shadow
// map (trees, terrain) times the cloud shadow up the sun ray. Shadowed air adds nothing, so the
// shafts are the sunlit air between shadows, brightest looking toward the sun (the phase). The
// frame's strength (Godrays.Intensity) is decided on the CPU.
// Like the underwater shafts it renders into a HALF-RESOLUTION target, writing the distance it
// marched into alpha, and rays-upsample-fragment.glsl composites it back at full resolution in
// display space. The depth is turned back into a world position through uInvViewProj, the inverse
// of the very matrix that wrote it, so the projection's clip-depth convention cancels out.
in vec2 fUv;
uniform sampler2D uSceneDepth;      // the HDR scene's own depth texture (not a copy)
uniform sampler2DShadow uShadowMap; // the near cascade
uniform mat4 uInvViewProj;
uniform mat4 uLightViewProj;
uniform vec3 uCameraPos;
uniform vec3 uSunDir;
uniform vec3 uSunColor;             // display space, like the underwater shafts
uniform float uStrength;            // Godrays.Intensity: the frame's strength, 0..1
uniform float uDensity;             // scattering per block of sunlit air (GodraysDensityPerBlock)
uniform float uMaxAdd;              // the most the shafts add to a display value (GodraysMaxAdd)
uniform float uMaxDistance;
uniform int uSteps;
uniform float uPhaseExponent;
uniform float uPhaseFloor;
uniform float uSkyBlocks;           // sunlit air counted past the march for a ray that reaches open sky

// The cloud shadow, read from the coverage map on unit 8 like test-fragment.glsl's CloudShadow:
// project the point up the sun ray to the cloud base and read the cell there.
uniform sampler2D uCloudShadowMap;
uniform int uCloudCells;
uniform float uCloudHeight;
uniform float uCloudField;
uniform vec2 uCloudDrift;
uniform float uCloudShadow;         // what a full-thickness cloud removes; 0 with the clouds off

out vec4 FragColor;

// Direct sun reaching a point: the same light-space lookup the world shader does, no PCF. Outside
// the cascade's box the air counts as lit.
float Lit(vec3 p)
{
    vec4 lp = uLightViewProj * vec4(p, 1.0);
    vec3 uvz = lp.xyz / lp.w * 0.5 + 0.5;
    if (uvz.z > 1.0 || any(lessThan(uvz.xy, vec2(0.0))) || any(greaterThan(uvz.xy, vec2(1.0)))) return 1.0;
    return texture(uShadowMap, uvz);
}

float CloudLit(vec3 p)
{
    if (uCloudShadow <= 0.0 || uSunDir.y <= 0.0 || p.y >= uCloudHeight) return 1.0;
    vec2 cp = p.xz + uSunDir.xz / max(uSunDir.y, 0.2) * (uCloudHeight - p.y);
    ivec2 cell = ivec2(floor((cp - uCloudDrift) / uCloudField * float(uCloudCells)));
    cell = ((cell % uCloudCells) + uCloudCells) % uCloudCells;
    return 1.0 - uCloudShadow * texelFetch(uCloudShadowMap, cell, 0).r;
}

void main()
{
    float depth = texture(uSceneDepth, fUv).r;
    vec4 h = uInvViewProj * vec4(fUv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec3 target = h.xyz / h.w;
    vec3 toTarget = target - uCameraPos;
    float len = min(length(toTarget), uMaxDistance);
    vec3 dir = normalize(toTarget);
    // Interleaved gradient noise: a per-pixel start offset turns the step banding into grain
    float jitter = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));
    float stepLen = len / float(uSteps);
    float accum = 0.0;
    for (int i = 0; i < uSteps; i++) {
        vec3 p = uCameraPos + dir * ((float(i) + jitter) * stepLen);
        accum += Lit(p) * CloudLit(p) * stepLen;
    }
    // A ray that leaves the march into open sky (nothing drawn there wrote depth: no terrain, tree
    // or cloud) keeps crossing sunlit air beyond it; one that ends on far terrain or a cloud does
    // not. That is the contrast the composite streaks outward from the sun, through the gaps
    // between trees, hills and clouds.
    if (depth >= 0.9999999)
        accum += uSkyBlocks;
    // Forward scattering: brightest looking toward the sun, a faint floor elsewhere (Godrays.Phase)
    float phase = mix(uPhaseFloor, 1.0, pow(max(dot(dir, uSunDir), 0.0), uPhaseExponent));
    // The share of the light scattered over the sunlit path (Godrays.Scatter): it saturates, so a
    // long open ray toward the sun adds at most uMaxAdd instead of washing the sky out, while the
    // shorter sunlit stretches between shadows stay in its linear part and read as shafts
    float scatter = 1.0 - exp(-accum * uDensity);
    // Alpha carries the distance marched, for the upsample's depth-aware weights
    FragColor = vec4(uSunColor * (uMaxAdd * uStrength * phase * scatter), len);
}
