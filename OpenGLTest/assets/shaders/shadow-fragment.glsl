#version 460 core
in vec2 fTexCoord;
flat in uint fLayer;

uniform sampler2DArray uTextureAtlas;

void main() {
    // Depth-only pass: the depth buffer is the shadow map, no color output needed.
    // Cutout tiles (leaves) still have to punch their holes here, or a crown would
    // cast the shadow of a solid box.
    if (texture(uTextureAtlas, vec3(fTexCoord, float(fLayer))).a < 0.5) discard;
}
