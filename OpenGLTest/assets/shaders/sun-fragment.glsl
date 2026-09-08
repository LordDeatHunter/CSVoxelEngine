#version 460 core
// Drawn with additive blending (One, One): black contributes nothing, so the quad's corners
// disappear and the glow brightens the sky instead of greying it. Never write anything but
// light here - there is no alpha to hide behind.
in vec2 fTexCoord;
flat in uint fLayer;

uniform sampler2DArray uSunTexture; // single slice, like every Vertex-layout texture
uniform vec3 uSunColor;
uniform float uVisibility;
uniform float uDiscFraction;
uniform float uGlowIntensity;
uniform float uGlowExponent;

out vec4 FragColor;

void main()
{
    vec2 c = fTexCoord - 0.5;
    float r = length(c) * 2.0;                       // 0 at centre, 1 at the quad's inscribed circle

    vec3 disc = vec3(0.0);
    if (r < uDiscFraction) {                          // never sample outside 0..1: the texture wraps
        vec4 t = texture(uSunTexture, vec3(c / uDiscFraction + 0.5, float(fLayer)));
        disc = t.rgb * t.a;
    }

    float glow = pow(max(1.0 - r, 0.0), uGlowExponent) * uGlowIntensity;

    FragColor = vec4(uSunColor * (disc + glow) * uVisibility, 1.0);
}
