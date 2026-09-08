#version 330 core
// Underwater sun shafts (god rays). One additive full-screen triangle: for every pixel the
// scene depth says how far the eye can see through the water, and the shader marches that
// segment in uSteps jittered samples, adding the sunlight that actually reaches each point
// (the same shadow map the world shader uses) attenuated by how deep it sits and how far it
// has travelled. The depth is turned back into a world position through uInvViewProj, the
// inverse of the very matrix that wrote it, so the projection's clip-depth convention cancels
// out - the C# System.Numerics projection is Direct3D-style 0..1, and no near/far
// linearization would be correct here.
in vec2 fUv;
uniform sampler2D uSceneDepth;
uniform sampler2DShadow uShadowMap;
uniform mat4 uInvViewProj;
uniform mat4 uLightViewProj;
uniform vec3 uCameraPos;
uniform vec3 uSunDir;
uniform vec3 uSunColor;
uniform float uStrength;
uniform float uWaterPlaneY;
uniform float uMaxDistance;
uniform int uSteps;
uniform float uTime;
out vec4 FragColor;

// Direct sun reaching a point: the same light-space lookup the world shader does, no PCF.
float Lit(vec3 p) {
    vec4 lp = uLightViewProj * vec4(p, 1.0);
    vec3 uvz = lp.xyz / lp.w * 0.5 + 0.5;
    if (uvz.z > 1.0) return 1.0;
    return texture(uShadowMap, uvz);
}

void main() {
    float depth = texture(uSceneDepth, fUv).r;
    // Inverse of the very matrix that wrote the depth, so the projection's clip-depth
    // convention cancels out (the C# projection is Direct3D-style 0..1, not GL -1..1).
    vec4 h = uInvViewProj * vec4(fUv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec3 target = h.xyz / h.w;
    vec3 toTarget = target - uCameraPos;
    float len = min(length(toTarget), uMaxDistance);
    vec3 dir = normalize(toTarget);
    // Interleaved gradient noise: a per-pixel start offset turns 20 steps of banding into grain
    float jitter = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));
    float stepLen = len / float(uSteps);
    float accum = 0.0;
    for (int i = 0; i < uSteps; i++) {
        float t = (float(i) + jitter) * stepLen;
        vec3 p = uCameraPos + dir * t;
        float below = uWaterPlaneY - p.y;
        if (below <= 0.0) continue;                 // above the surface there is air, no shaft
        accum += Lit(p) * exp(-0.08 * below) * exp(-0.03 * t) * stepLen;
    }
    // Brighter looking toward the sun (forward scattering), never zero looking away
    float phase = 0.2 + 0.8 * pow(max(dot(dir, uSunDir), 0.0), 4.0);
    FragColor = vec4(uSunColor * (accum * uStrength * phase), 1.0);
}
