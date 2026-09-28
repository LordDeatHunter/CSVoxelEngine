#version 460 core
// The sky dome: one full-screen triangle drawn first each frame (and into the water mirror),
// depth off, so everything after it occludes it. The view ray is rebuilt from the inverse of a
// translation-free view-projection, graded from the horizon haze band up to the zenith by
// elevation, with a warm azimuthal glow toward the sun while it is low. The world and cloud
// fog sample the very same SkySample in their view direction, so a fully fogged fragment is
// exactly the colour of the dome behind it. The sun disc is added on top by sun-fragment.glsl.
in vec2 fUv;

uniform mat4 uInvViewRotProj; // inverse(viewRotation * projection), no translation
uniform vec3 uSkyZenith;
uniform vec3 uSkyHorizon;
uniform vec3 uSkyGlow;        // dusk-scaled on the CPU: zero at noon and at night
uniform vec4 uSkyShape;       // x horizon band, y haze width, z haze strength, w glow exponent
uniform vec3 uSunDir;
uniform vec3 uSkyTint;        // what the dome fades toward in lava
uniform float uSkyTintAmount;

out vec4 FragColor;

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

// Cheap static value hash (vignette-fragment.glsl's Hash) for the output dither.
float Hash12(vec2 p)
{
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

void main()
{
    vec2 ndc = fUv * 2.0 - 1.0;
    // No translation in the matrix, so this point lies on the view ray from the origin
    // whatever the clip-depth convention is.
    vec4 h = uInvViewRotProj * vec4(ndc, 0.5, 1.0);
    vec3 dir = normalize(h.xyz / h.w);

    vec3 col = SkySample(dir);
    // V-6: moon (visibility from -elevation) and stars are added here, before the tint

    col = mix(col, uSkyTint, uSkyTintAmount);
    col += (Hash12(gl_FragCoord.xy) - 0.5) / 255.0;
    FragColor = vec4(max(col, 0.0), 1.0);
}
