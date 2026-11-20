#version 460 core
in vec2 fTexCoord;
flat in uint fLayer;

uniform sampler2DArray uTextureAtlas;
// LOD foliage is drawn opaque in the world pass (the uLodLeafColor fallback), so it must cast
// solid, or a crown magnified over a 2-8 block cell casts a sieve. The plain shadowShader never
// sets this, and its default of 0 keeps the cutout test for every other caster.
uniform int uOpaqueCasters;

#ifdef TREE_PROXY
layout (std430, binding = 3) readonly buffer TreePayload { uint treeWords[]; };
flat in uint fTreeBase;
in vec2 fTreeXZ;
bool TreeTileVisible() {
    int side = int(treeWords[fTreeBase + 7u]);
    ivec2 tile = ivec2(floor(fTreeXZ / 16.0));
    if (any(lessThan(tile, ivec2(0))) || any(greaterThanEqual(tile, ivec2(side))))
        return false;
    int index = tile.x * side + tile.y;
    uint bits = treeWords[fTreeBase + treeWords[fTreeBase + 3u] + uint(index >> 5)];
    return (bits & (1u << uint(index & 31))) != 0u;
}
#endif

void main() {
#ifdef TREE_PROXY
    if (!TreeTileVisible()) discard;
#endif
    // Depth-only pass: the depth buffer is the shadow map, no color output needed.
    // Cutout tiles (leaves) still have to punch their holes here, or a crown would
    // cast the shadow of a solid box.
    if (uOpaqueCasters == 0 && texture(uTextureAtlas, vec3(fTexCoord, float(fLayer))).a < 0.5) discard;
}
