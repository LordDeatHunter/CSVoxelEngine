#version 460 core
// Sun dazzle visibility: how much of the sun's disc is in open sky this frame, written into a 1x1
// target that dazzle-fragment.glsl reads. The single fragment taps the scene depth at uTaps points
// spread evenly over the disc (a Vogel spiral) and counts the ones where nothing was drawn (depth
// at the far plane: no terrain, tree, cloud, mob or held item); a tap off the screen counts as
// hidden, so the dazzle leaves with the sun. Drawn with (SrcAlpha, OneMinusSrcAlpha) blending and
// uEase as its alpha, so the target eases toward the count (SunDazzle.EaseShare) with no readback.
uniform sampler2D uSceneDepth;   // unit 4: the HDR scene's own depth texture (not a copy)
uniform vec2 uSunUv;             // the sun's place on screen; may lie off screen
uniform vec2 uDiscUv;            // the disc's radius in uv on each axis
uniform int uTaps;
uniform float uEase;             // share of the way to this frame's count
out vec4 FragColor;

void main()
{
    float open = 0.0;
    for (int i = 0; i < uTaps; i++) {
        float ring = sqrt((float(i) + 0.5) / float(uTaps));
        float turn = float(i) * 2.39996323;   // the golden angle
        vec2 uv = uSunUv + vec2(cos(turn), sin(turn)) * ring * uDiscUv;
        if (uv == clamp(uv, vec2(0.0), vec2(1.0)) && textureLod(uSceneDepth, uv, 0.0).r >= 0.9999999)
            open += 1.0;
    }
    FragColor = vec4(open / float(uTaps), 0.0, 0.0, uEase);
}
