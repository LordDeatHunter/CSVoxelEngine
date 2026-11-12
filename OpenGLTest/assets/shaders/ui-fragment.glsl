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
// Alpha multiplier for the whole draw: a panel's open/close fade (UIRenderer.SetOverlay). 1 otherwise.
uniform float uFade;

// Rarity chrome (RarityChromeRenderer; GL-free twin RarityChrome, the formula not the bits). With uChrome 1 the
// quad is a list row or the tooltip box: fTexCoord runs 0..1 across it with y down, and the draw's color and
// texture are ignored. Every ordinary draw leaves uChrome 0.
uniform int uChrome;
// (size x, size y, sheen slant, sparkle density), sizes in canvas units
uniform vec4 uChromeBox;
// (r, g, b, opacity)
uniform vec4 uChromeColor;
// (wash, glow, sheen (0 while it rests), sparkle)
uniform vec4 uChromeLayers;
// (border width, glow reach, sheen half-width, sparkle cell size), in canvas units
uniform vec4 uChromeShape;
// (sheen center along p.x + slant * p.y, pulse factor, twinkle phase 0..1, wash lead: 0 left edge, 1 top edge)
uniform vec4 uChromeMotion;

out vec4 FragColor;

// Exact piecewise sRGB encode; GLSL twin of ColorSpace.LinearToSrgb (and of post-fragment.glsl's).
vec3 LinearToSrgb(vec3 c)
{
    c = max(c, vec3(0.0));
    return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c));
}

// The rarity chrome, one function per RarityChrome twin; p = uv * box size, in canvas units with y down.
// The sine hash of a sparkle cell: a fraction in [0, 1).
float ChromeHash(vec2 cell)
{
    return fract(sin(dot(cell, vec2(127.1, 311.7))) * 43758.5453);
}

// How far p is from the box's nearest edge.
float ChromeEdge(vec2 p)
{
    vec2 nearest = min(p, uChromeBox.xy - p);
    return min(nearest.x, nearest.y);
}

// The border band: 1 inside the border width, a half-unit soft step out of it.
float ChromeBorder(float edge)
{
    return 1.0 - smoothstep(uChromeShape.x - 0.5, uChromeShape.x + 0.5, edge);
}

// The inner glow: 1 on the edge, falling off over the glow reach.
float ChromeGlow(float edge)
{
    return exp(-edge / uChromeShape.y);
}

// The wash: strongest at the leading edge (left, or top for a lead of 1), quadratic to 0 at the far one.
float ChromeWash(vec2 uv)
{
    float t = 1.0 - mix(uv.x, uv.y, uChromeMotion.w);
    return t * t;
}

// The slanted sheen band: 1 on its center line, 0 a half-width away.
float ChromeSheen(vec2 p)
{
    return 1.0 - smoothstep(0.0, uChromeShape.z, abs(p.x + uChromeBox.z * p.y - uChromeMotion.x));
}

// A twinkling four-point star at a hashed spot in a share (the density) of the cells. A star's hash is below the
// density, so u rescales it to 0..1: its spot and twinkle phase spread evenly and the stars twinkle out of step.
float ChromeSparkle(vec2 p)
{
    vec2 q = p / uChromeShape.w;
    vec2 cell = floor(q);
    float h = ChromeHash(cell);
    if (h >= uChromeBox.w)
        return 0.0;
    float u = h / uChromeBox.w;
    vec2 d = abs(q - cell - 0.25 - 0.5 * vec2(fract(u * 17.0), fract(u * 31.0)));
    float core = max(0.0, 1.0 - length(d) * 5.0);
    float arms = max(0.0, 1.0 - d.x * 12.0) * max(0.0, 1.0 - d.y * 2.5)
               + max(0.0, 1.0 - d.y * 12.0) * max(0.0, 1.0 - d.x * 2.5);
    float twinkle = pow(max(0.0, 0.5 + 0.5 * sin(6.2831853 * (uChromeMotion.z + u))), 6.0);
    return min(1.0, core + arms) * twinkle;
}

// The chrome's straight-alpha color before uFade: the rarity tint (the brighter of the wash and the pulsing
// rim) under the white light of the sheen and the sparkles, its alpha scaled by the chrome's opacity.
vec4 RarityChrome(vec2 uv)
{
    vec2 p = uv * uChromeBox.xy;
    float edge = ChromeEdge(p);
    float rim = max(ChromeBorder(edge), uChromeLayers.y * ChromeGlow(edge)) * uChromeMotion.y;
    float tint = max(uChromeLayers.x * ChromeWash(uv), rim);
    float light = clamp(uChromeLayers.z * ChromeSheen(p) + uChromeLayers.w * ChromeSparkle(p), 0.0, 1.0);
    float a = light + tint * (1.0 - light);
    vec3 rgb = a > 0.0 ? (vec3(light) + uChromeColor.rgb * tint * (1.0 - light)) / a : vec3(0.0);
    return vec4(rgb, a * uChromeColor.a);
}

void main()
{
    if (uChrome != 0)
    {
        FragColor = RarityChrome(fTexCoord);
        FragColor.a *= uFade;
        return;
    }

    // Vertex color bakes in per-face shading, uColor is the per-draw tint. Solid shapes skip
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
    FragColor.a *= uFade;
}
