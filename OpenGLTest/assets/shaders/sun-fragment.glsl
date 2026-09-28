#version 460 core
// Drawn with additive blending (One, One): black contributes nothing, so the quad's corners
// disappear and the glow brightens the sky instead of greying it. Never write anything but
// light here - there is no alpha to hide behind.
// The glow is dithered before the 8-bit write so its falloff does not band, but only where the
// quad already contributes light, since additive noise would otherwise speckle the whole quad.
// The sky itself is dithered in its own pass (sky-fragment.glsl), drawn just before this one.
in vec2 fTexCoord;
flat in uint fLayer;

uniform sampler2DArray uSunTexture; // single slice, like every Vertex-layout texture
uniform vec3 uSunColor;
uniform float uVisibility;
uniform float uDiscFraction;
uniform float uGlowIntensity;
uniform float uGlowExponent;

out vec4 FragColor;

// Cheap static value hash (vignette-fragment.glsl's Hash) for the output dither.
float Hash12(vec2 p)
{
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

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

    vec3 col = uSunColor * (disc + glow) * uVisibility;
    float lit = smoothstep(0.0, 4.0 / 255.0, max(col.r, max(col.g, col.b)));
    col = max(col + (Hash12(gl_FragCoord.xy) - 0.5) / 255.0 * lit, 0.0);
    FragColor = vec4(col, 1.0);
}
