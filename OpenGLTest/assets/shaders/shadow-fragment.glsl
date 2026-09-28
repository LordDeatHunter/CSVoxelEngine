#version 460 core
in vec2 fTexCoord;
flat in uint fLayer;

uniform sampler2DArray uTextureAtlas;
// LOD foliage is drawn opaque in the world pass (the uLodLeafColor fallback), so it must cast
// solid, or a crown magnified over a 2-8 block cell casts a sieve. The plain shadowShader never
// sets this, and its default of 0 keeps the cutout test for every other caster.
uniform int uOpaqueCasters;

void main() {
    // Depth-only pass: the depth buffer is the shadow map, no color output needed.
    // Cutout tiles (leaves) still have to punch their holes here, or a crown would
    // cast the shadow of a solid box.
    if (uOpaqueCasters == 0 && texture(uTextureAtlas, vec3(fTexCoord, float(fLayer))).a < 0.5) discard;
}
