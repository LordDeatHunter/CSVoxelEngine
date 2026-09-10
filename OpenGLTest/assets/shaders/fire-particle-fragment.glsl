#version 460 core
in vec4 fTint;
in float fDist;

uniform vec3 uFogColor;
uniform float uFogNear;
uniform float uFogFar;

out vec4 FragColor;

void main() {
    vec3 col = fTint.rgb;
    float f = clamp((fDist - uFogNear) / (uFogFar - uFogNear), 0.0, 1.0);
    col = mix(col, uFogColor, f * f);
    FragColor = vec4(col, fTint.a);
}
