#version 330 core
in vec2 fTexCoord;

uniform sampler2D uTextureAtlas;

void main() {
    // Depth-only pass: the depth buffer is the shadow map, no color output needed.
    // Cutout tiles (leaves) still have to punch their holes here, or a crown would
    // cast the shadow of a solid box.
    if (texture(uTextureAtlas, fTexCoord).a < 0.5) discard;
}
