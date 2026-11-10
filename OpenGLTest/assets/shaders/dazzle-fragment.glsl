#version 460 core
// Sun dazzle: the blinding bloom of looking straight at the sun, one additive (One, One) full-screen
// draw into the linear HDR scene after the godrays. It is light in the eye, not in the world, so it
// lies over everything, the foreground included: a bright Gaussian core round the sun's place on
// screen, a fan of rays outside it and a wide veil that lifts the whole view. The frame's strength
// (SunDazzle.Intensity: how directly the camera faces the sun) is in uColor; how much of the sun's
// disc is in open sky comes eased from the 1x1 target dazzle-visibility-fragment.glsl writes.
// Black adds nothing, so there is no alpha to hide behind: never write anything but light here.
// C# twin: SunDazzle.Glow (Falloff, Rays); keep the two in step.
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
out vec4 FragColor;

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
    FragColor = vec4(uColor * (visible * glow), 0.0);
}
