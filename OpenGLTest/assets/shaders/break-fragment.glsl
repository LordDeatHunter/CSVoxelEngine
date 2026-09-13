#version 460 core

uniform sampler2DArray uBreakProgress; // 8 stacked crack frames, layer 0 blank
uniform float uFrame;                  // frame the current progress lands on
uniform float uOpacity;                // fades the black cracks so the block stays readable

in vec2 vUv;
out vec4 FragColor;

void main() {
    vec4 crack = texture(uBreakProgress, vec3(vUv, uFrame));
    FragColor = vec4(crack.rgb, crack.a * uOpacity);
}
