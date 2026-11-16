#version 460 core
in vec2 fUv;
flat in uint fLayer;
in vec4 fTint;
in float fDist;

uniform sampler2DArray uSprite;
uniform vec3 uFogColor;
uniform float uFogNear;
uniform float uFogFar;

out vec4 FragColor;

void main() {
    vec4 tex = texture(uSprite, vec3(fUv, float(fLayer)));
    float alpha = tex.a * fTint.a;
    if (alpha < 0.01) discard;

    vec3 col = tex.rgb * fTint.rgb;

    float f = clamp((fDist - uFogNear) / (uFogFar - uFogNear), 0.0, 1.0);
    col = mix(col, uFogColor, f * f);

    FragColor = vec4(col, alpha);
}
