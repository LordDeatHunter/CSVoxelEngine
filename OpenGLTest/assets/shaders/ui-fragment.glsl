#version 460 core
in vec4 fColor;
in vec2 fTexCoord;
flat in uint fLayer;

// Every UI texture is an array: the block atlas (icons pick their tile through fLayer),
// and single-slice sprites, font and crosshair on layer 0.
uniform sampler2DArray uTex;
uniform vec4 uColor;
uniform bool uUseTexture;
// Alpha discard threshold for cutout draws (extruded item sprites, cutout block
// icons). 0 disables: without it the sprite's transparent front texels would still
// write depth and swallow the extrusion walls behind them whenever the slab turns.
uniform float uAlphaCutoff;
// The bound texture is SRGB8_ALPHA8 (the block atlas or an item-mesh texture shared with the
// lit world), so the sampler hands back decoded linear values. The UI draws after the tonemap
// into the gamma default framebuffer, so re-encode them to write today's bytes. UI-only
// textures (font, crosshair, HUD icons) are linear uploads and leave this false.
uniform bool uSrgbTex;

out vec4 FragColor;

// Exact piecewise sRGB encode; GLSL twin of ColorSpace.LinearToSrgb (and of post-fragment.glsl's).
vec3 LinearToSrgb(vec3 c)
{
    c = max(c, vec3(0.0));
    return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c));
}

void main()
{
    // Vertex colour bakes in per-face shading, uColor is the per-draw tint. Solid shapes skip
    // the sampler entirely so they don't need a texture bound.
    vec4 texel = vec4(1.0);

    if (uUseTexture)
    {
        texel = texture(uTex, vec3(fTexCoord, float(fLayer)));
        if (uAlphaCutoff > 0.0 && texel.a < uAlphaCutoff)
            discard;
        if (uSrgbTex)
            texel.rgb = LinearToSrgb(texel.rgb);
    }

    FragColor = fColor * uColor * texel;
}
