#version 460 core
// Sun dazzle: the blinding bloom of looking straight at the sun, one full-screen draw into the
// linear HDR scene after the godrays, blended (One, OneMinusSrcAlpha): the dazzle writes alpha 0,
// so it is pure added light. It is light in the eye, not in the world, so it
// lies over everything, the foreground included: a bright Gaussian core round the sun's place on
// screen, a fan of rays outside it and a wide veil that lifts the whole view. The frame's strength
// (SunDazzle.Intensity: how directly the camera faces the sun) is in uColor; how much of the sun's
// disc is in open sky comes eased from the 1x1 target dazzle-visibility-fragment.glsl writes.
// Black with alpha 0 changes nothing.
// C# twin: SunDazzle.Glow (Falloff, Rays); keep the two in step.
// The same draw adds the lens flare (LensFlare): a row of tinted circles on the line from the sun
// through the screen center, each a reflection of the sun inside the lens, hidden with the disc
// like the dazzle. The circles are PostProcessConfig.LensFlareGhosts; uFlare carries the frame's
// strength (LensFlare.Intensity). A circle is a faint film of its tint rather than pure light: it
// also writes a little alpha (uFlareCover, LensFlare.Cover), which takes that share of the picture
// behind it away, so its color still reads over a bright sky or a white cloud, where added light
// alone would wash out to white. C# twin: LensFlare.Center and LensFlare.Shape.
in vec2 fUv;
uniform sampler2D uVisibility;   // unit 5: 1x1, the eased share of the sun's disc in open sky
uniform vec2 uSunUv;             // the sun's place on screen; may lie off screen
uniform float uAspect;           // viewport width / height
uniform vec3 uColor;             // the linear sun color times SunDazzle.Intensity
uniform float uCoreRadius;       // PostProcessConfig.SunDazzle*: shares of the screen height
uniform float uVeilShare;
uniform float uVeilReach;
uniform float uRayShare;
uniform float uRayReach;
const int MaxGhosts = 8;                // LensFlare.MaxGhosts
uniform int uGhostCount;
uniform vec4 uGhostShape[MaxGhosts];    // offset along the flare's line, radius, hollow, softness
uniform vec4 uGhostColor[MaxGhosts];    // rgb: the linear tint times its brightness; a: that brightness
uniform vec3 uFlare;                    // the linear sun color times LensFlare.Intensity and LensFlareLight
uniform float uFlareCover;              // LensFlare.Cover: what a circle covers per unit of brightness
out vec4 FragColor;

// LensFlare.Shape: one circle at d radii from its center. Nothing at and beyond its edge, which
// fades over the outer `softness` of the radius; inside, evenly lit for hollow 0 and darkening
// toward the middle as it rises, to a ring at 1.
float GhostShape(float d, float hollow, float softness)
{
    float inside = 1.0 - smoothstep(1.0 - softness, 1.0, d);
    float d2 = d * d;
    return inside * mix(1.0, d2 * d2, hollow);
}

void main()
{
    float visible = texelFetch(uVisibility, ivec2(0), 0).r;
    // Distance from the sun in screen heights, so the glow is round at any aspect
    vec2 d = (fUv - uSunUv) * vec2(uAspect, 1.0);
    float r = length(d);
    float x = r / uCoreRadius;
    float core = exp(-x * x);
    // SunDazzle.Falloff: the core mixed with the veil
    float glow = mix(core, exp(-r / uVeilReach), uVeilShare);
    // SunDazzle.Rays: two waves of whole rays per turn, multiplied and squared into thin rays;
    // they start outside the core
    float a = atan(d.y, d.x);
    float rays = (0.5 + 0.5 * sin(6.0 * a + 0.4)) * (0.5 + 0.5 * sin(10.0 * a + 2.3));
    glow += uRayShare * rays * rays * exp(-r / uRayReach) * (1.0 - core);
    vec3 light = uColor * glow;
    float cover = 0.0;
    // The lens flare: every circle sits at its offset along the line from the screen center to
    // the sun (LensFlare.Center), 1 on the sun and negative across the center from it
    if (uFlare != vec3(0.0)) {
        vec2 p = (fUv - 0.5) * vec2(uAspect, 1.0);
        vec2 sun = (uSunUv - 0.5) * vec2(uAspect, 1.0);
        vec4 ghosts = vec4(0.0);
        for (int i = 0; i < uGhostCount; i++) {
            vec4 shape = uGhostShape[i];
            float gd = length(p - sun * shape.x) / shape.y;
            if (gd < 1.0)
                ghosts += uGhostColor[i] * GhostShape(gd, shape.z, shape.w);
        }
        light += uFlare * ghosts.rgb;
        cover = min(uFlareCover * ghosts.a, 1.0);
    }
    // Premultiplied: the light as it is, the cover as the share of the picture taken away
    FragColor = vec4(light, cover) * visible;
}
