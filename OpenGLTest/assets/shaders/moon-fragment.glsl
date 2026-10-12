#version 460 core
// The moon disc: drawn like the sun (sun-vertex.glsl places the quad at infinity, around the moon's
// direction, which is the sun's negated), additively (One, One), so black adds nothing and the quad's
// corners vanish. The sprite is a full lit disc; the phase is shaded over it here, so one image
// covers every phase (MoonPhase is the C# twin): each disc point gets a sphere normal (z toward the
// viewer) and is lit where it faces uPhaseLight, softened across the terminator. The unlit part
// keeps only a faint earthshine. A small halo around the disc grows with how much of it is lit.
in vec2 fTexCoord;
flat in uint fLayer;

uniform sampler2DArray uMoonTexture; // single slice, like every Vertex-layout texture
uniform vec3 uMoonColor;     // linear HDR
uniform float uVisibility;
uniform float uDiscFraction; // the disc's radius as a share of the quad's
uniform vec2 uPhaseLight;    // MoonPhase.LightDirection: (x across the disc, z toward the viewer)
uniform float uLitFraction;  // MoonPhase.IlluminatedFraction, for the halo
uniform float uTerminator;   // WorldGenerationConfig.MoonTerminatorSoftness
uniform float uEarthshine;
uniform float uGlowIntensity;
uniform float uGlowExponent;

out vec4 FragColor;

void main()
{
    vec2 c = fTexCoord - 0.5;
    float r = length(c) * 2.0;                       // 0 at center, 1 at the quad's inscribed circle

    vec3 disc = vec3(0.0);
    if (r < uDiscFraction) {                          // never sample outside 0..1: the texture wraps
        vec4 t = texture(uMoonTexture, vec3(c / uDiscFraction + 0.5, float(fLayer)));
        vec2 p = c * (2.0 / uDiscFraction);           // -1..1 across the disc
        vec3 n = vec3(p, sqrt(max(1.0 - dot(p, p), 0.0)));
        float lit = smoothstep(-uTerminator, uTerminator, n.x * uPhaseLight.x + n.z * uPhaseLight.y);
        disc = t.rgb * t.a * mix(uEarthshine, 1.0, lit);
    }

    float glow = r >= uDiscFraction
        ? pow(max(1.0 - (r - uDiscFraction) / (1.0 - uDiscFraction), 0.0), uGlowExponent) * uGlowIntensity * uLitFraction
        : 0.0;

    FragColor = vec4(uMoonColor * (disc + glow) * uVisibility, 1.0);
}
