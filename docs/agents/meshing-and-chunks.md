# Meshing and chunk drawing

The vertex layout and texture arrays, the material atlas, the greedy chunk mesher, sky cover, biome tint, chunk drawing through indirect multi-draw and the Hi-Z occlusion cull.

**Textures and the vertex layout.** Every texture a `Vertex`-layout mesh samples is a
`TEXTURE_2D_ARRAY` (`TextureLoader.Upload`/`UploadArray`): the block atlas has one slice per
`assets/textures/blocks/*.png` in sorted file-name order (`TextureAtlas.TileOf(name)` returns the
slice as a `ushort`, `BlockType.FaceTiles/Top/Bottom/Side` hold it), and item sprites, the font,
the crosshair and the sun are single-slice arrays on layer 0. **Item sprites are authored at 32x32** (and so are the block
tiles); the only exception is the six large sword sprites at 42x42 (iron, tin and bronze, swords and heads), whose blade is longer than a 32px frame. The
extruded sprite is built from the file's own pixel size (its thickness is `size / width * 2`, `SpritePose.HalfThickness`),
so one sprite's size never depends on another's. All eight world/shadow/UI/sun
shaders therefore declare `sampler2DArray` and read `vec3(fTexCoord, float(fLayer))`; there is
no 2D-texture path for meshes. `Vertex` is 32 bytes: position (3 floats), packed rgba, **UV as
two floats in tile units** (0..1 across one tile, 0..W across a quad merged over W cells — the
sampler's REPEAT tiles it, so UVs are never atlas-absolute), sun/block bytes at 24-25, bytes
26-27 read by GL as **one integer ushort** at attribute location 5 (`Normal` + `LightTint`: bits
0-2 face, 3 submerged, 4 biome tint (`IsBiomeTinted`), 5 sway, 6-15 the 10-bit block-light tint code, `Vertex.BlockTint`,
`IsSwaying`; attribute 6 is gone; on a sway vertex the **alpha byte is its sway weight**,
`Vertex.SwayWeight`/`EncodeSwayWeight`: 255 on canopy leaves, eased from 0 at a rooted plant's base
to 255 at its tip, never 253/254, and the vertex shader hands the fragment an alpha of 1, so
the exact-byte water/LOD-foliage flags only ever see non-swaying vertices), the `ushort Layer` at 28 (attribute location 7, an integer attribute) and the `ushort
WaterSurface` at 30 (attribute location 8, also integer): the baked water surface over a
submerged face in quarter blocks (`Vertex.EncodeWaterSurface`, 0 on every dry face). The mesher
gets it from `WaterSurfaceCodes.SubmergedSurfaceCode`, which walks the fluid column in the padded snapshot and
resolves a column that leaves the snapshot from the build's resolved `ColumnTerrain.WaterLevel`
(`MeshScratch.Columns`, worldgen or streamed, see "Chunk meshing"); it rides the greedy key
as a 7-bit code in bits 25-31 (`GreedyKey`), so a merge never crosses two water surfaces. On a **fluid** quad
(alpha 254) the same ushort means something else: the per-corner flow code
(`Vertex.EncodeWaterFlow` of `FluidFlow.Corner`, signed 7-bit X and Z plus a falling bit, 0 =
still), one per vertex so neighboring cells agree at shared corners. That is safe only because
the shader reads the surface meaning exclusively on non-water submerged faces. Baked model
quads (`BakedQuad.Uvs` + `.Tile`) and every procedural emitter go through `ChunkFaceTables.TiledUv(face,
local, extent)`, the single definition of per-face texture orientation; `CropUv` is its
extent-1 case. Bind these textures with `TextureTarget.Texture2DArray`.
The block and material atlases sample their mip chains (`TextureLoader.UploadArray(...,
sampleMips: true)`: min filter `NearestMipmapLinear` plus `AtlasAnisotropy`, mag stays `Nearest`,
so close blocks keep crisp pixels while distant ground stops sparkling); every other texture stays
plain `Nearest`. Right after `ContentRegistry.BakeModels`, `TextureAtlas.ApplyCutoutMips`
overwrites levels 1..n of every tile a cutout block wears (`BlockType.CutoutTiles`) with a
`CutoutMipmap` chain whose alpha is rescaled per level to keep the coverage at
`LightingConfig.AlphaCutoff` that level 0 has, so leaf canopies do not thin with distance.

**The material atlas.** `assets/textures/blocks_material/` holds a second `TEXTURE_2D_ARRAY`
(`TextureAtlas.BuildMaterial`, texture unit 6) that is *index-parallel* to the block atlas: slice
`i` describes the same tile as atlas slice `i`, so the world shader's existing `fLayer` addresses
both and nothing new rides the vertex. RGB is a tangent-space normal and A a metallic multiplier;
a tile with no file there gets a flat `(128, 128, 255, 255)` default, which is why only the three
reflective ores need art. **Put normal maps in that sibling folder, never in
`assets/textures/blocks/`** - `TextureAtlas.Load` globs `*.png` unfiltered, so a file dropped in
there becomes an extra atlas slice and renumbers every tile after it.

How reflective each slice is comes from the owning block's `material` JSON, gathered by
`TileMaterials.Build` during `ContentRegistry.BakeModels` into one `vec4` per slice and uploaded as
an SSBO at binding 1 (binding 0 is `ChunkDrawBatch`'s `ChunkInfo`). Material is keyed by tile rather
than by block precisely so the feature stays off the vertex - no new attribute, no wider `Vertex`,
no change to the greedy merge key - at the cost that two blocks sharing a texture share its
material, which `TileMaterials.Build` warns about instead of silently resolving. `uMaterialCount`
is the switch for the whole path and stays 0 wherever the buffer is not bound (the ItemDebugger
loads the same shaders), so an unbound buffer is never read. `test-vertex.glsl` emits the `fTangent`
/ `fBitangent` frame the normal map is read in, and it has to agree with `ChunkFaceTables.TiledUv`'s per-face
UV orientation or the relief comes out rotated or mirrored per face. `visual-examples/ores.json` is the screenshot check, and wants a `VOXEL_TIME` since the highlight
tracks the sun. The ore body darkens by `metallic * 0.6` and the sun highlight, added in linear light,
is scaled by `SurfaceShadingConfig.OreSpecLinearScale` (`uOreSpecScale`), because the sRGB encode
lifts an add on the near-black body far more than it did in display space and smeared coal's
specks into gray. Every other (non-ore, non-water) tile is plain diffuse: the SH-23 grazing sky
rim was removed because it tinted grazing foliage teal. The one exception is the **snow sparkle**,
not part of the material system: the snow block's tile (`uSnowLayer`, its `Top`, set in
`InitWorldProgram`; `uSnowSparkle` 0 switches it off, as in the ItemDebugger) gets a separate term
(`SnowGlint`): a `SnowSparkleDensity` share of texels, hashed (`Pcg3d`) from the texel's place on
the face's own plane in texture-pixel space (`SnowTexels` must equal the tile size,
`SnowSparkleTests`), carries a random cosine-weighted facet, and its whole texel flashes with
`SnowSparkleStrength` of the sun while the eye/sun half vector lines up with it, so glints move with
the camera. Gated by skylight, a thresholded shadow and cloud shadow (no dimmed glints in a shadow
the low sun has faded), the sun on the face's side, distance (gone by 36 blocks) and a grazing view.
Sunlit snow sits ~2x past the tonemap clip for most of a day, so the glints only read when it is
under the clip, i.e. a low sun: `visual-examples/snow-sparkle.json` at `VOXEL_TIME=405`.

The **ice glint** is a second such tile term, on the ice block's tile (`uIceLayer`; `uIceGlint` 0
switches it off, as in the ItemDebugger): a fresnel sheen of the sky the face mirrors
(`SkySample` of the reflected view ray, blended to `uFogColor` by `uFogWet`, dimmed by skylight)
mixed over the lit ice by `IceSheenStrength * Schlick`, the way the ore mixes its sky, plus a
Blinn sun highlight (`IceGlintShine`) of `IceGlintStrength` times the direct sun, added in linear
light and gated by skylight, the shadow and cloud shadow and the sun on the face's side. Ice is
translucent but its tile is opaque, so it needs no alpha handling in the translucent pass. Twin
`IceGlint` (`SheenWeight`, `Highlight`; `IceGlintTests`); `visual-examples/ice-glint.json` at
`VOXEL_TIME=405` places a patch of ice on the frozen plain and frames the highlight, the grazing
sheen and an overhead view.

The **ore twinkle** rides the material path itself (`oreTile`, a non-zero `material.metallic`,
inside the near fade): an `OreTwinkleDensity` share of the texels, hashed with `Pcg3d` from the
texel's place on the face's plane exactly like the snow glint (salted so the two never pick the same
texels), flash once per cycle on their own hashed rate (`OreTwinkleRateMin..Max` per second) and
phase, a squared tent of half-width `OreTwinkleWidth` (in cycles) around mid-cycle, added untinted as
`OreTwinkleStrength` times the direct sun. Gated like the snow glint by skylight, a thresholded
shadow and cloud shadow and the sun on the face's side, and faded out between
`OreTwinkleFadeNearBlocks` and `OreTwinkleFadeFarBlocks`, so ore in a cave (no skylight) never
twinkles. Twin `OreTwinkle` (`Cycle`, `Flash`, `Gate`; `OreTwinkleTests`); `uOreTwinkle` 0 switches
it off. `ores.json`'s spawn spot now sits under a canopy, so `visual-examples/ore-twinkle.json`
(`VOXEL_TIME=147`) lays coal, malachite and obsidian on the frozen plain and takes four shots 0.4 s
apart.

**Chunk meshing.** The column terrain a build reads (heights for the sun seeding, water levels,
the tint climate) comes from the 3x3 chunk columns around it, resolved once:
`ChunkMeshBuilder.GenerateMeshData` (behind `Chunk.GenerateMeshData`, the one caller of `GenerateMeshDataFromSnapshot`), right after the neighbor resolve and the empty/enclosed early
return, fills `MeshScratch.Columns` (`dx * 3 + dz` order) through
`ChunkLighting.FillColumns(chunkManager.ColumnSource, ...)`, and the sun seeding, the water-surface
codes and the tint fields all read that array (only the pebble, branch-pile and untouched-snow
emitters still read seed-hashed worldgen per cell, see the multiplayer "Known gaps"); a missing
streamed column makes the build return `null`, which the caller treats like a vanished neighbor
(see the threading rules).
`ChunkMeshBuilder.GenerateMeshDataFromSnapshot` runs two passes over the padded 18³
block snapshot. The id snapshots (the padded `ushort[]` and the light flood's id copy, both rented
from `ArrayPool<ushort>`) are dense `ushort`, decoded from the palette storage (`ChunkBlocks`, see the
threading rules) of each chunk in the neighborhood, captured once per build in `MeshScratch.NeighborBlocks`
(`TryResolveNeighbors` fills it beside `Neighbors`/`NeighborStates`): `FillPaddedSnapshot` copies each row's
16 middle cells with one `ChunkBlocks.CopyRun` (a fill for a Uniform storage, a gather for an Indexed one, a
span copy for a Direct one) and reads the two halo cells with `Get`, and `ChunkLighting.FillLightSnapshot`
copies its runs with `CopyRun` the same way. The per-voxel pass reads ids from the padded snapshot, never
the live chunk, so one build sees one consistent set of cells. The state, sun and block-light snapshots
stay `byte`. The enclosed early return (`IsFullyEnclosed`: a full chunk whose cells all occlude and whose
six face neighbors seal the shared planes builds nothing, not even a light volume) answers from the palette
first: a Uniform or Indexed storage whose every `IdSpan` id occludes passes in O(1) or a palette's length,
and a face neighbor whose palette all occludes seals its face; otherwise (a stale air entry left by edits,
a real non-occluder, any Direct storage) it falls back to the exact scan of the chunk's cells and of the
neighbor's facing 16x16 plane, so a solid chunk built by edits keeps the skip. The binary greedy pass (`EmitGreedyFaces`, scratch in the `[ThreadStatic]`
`MeshScratch`) takes every block flagged `BlockType.IsGreedy`: a `Model`-renderer block whose
baked model is exactly the six untransformed cube faces (`ModelBakeSteps.BakesGreedy`; all four
built-in parents qualify, so stone, dirt, grass, logs, TNT, lamps, leaves and glass all do). It
builds per-axis bit columns of greedy and occluding cells, finds visible faces with one
shift-and-mask per column (the halo bits cull border faces exactly like the neighbor read),
keys each face by one int (`GreedyKey`: block id in bits 0-15, the four 2-bit corner AO values
in 16-23, the submerged flag in 24, a submerged face's 7-bit water surface code in 25-31; bit 31
may make it negative, which is safe because the plane table hashes it as `(uint)key` and an
empty slot is entry 0) and by the four corner
**smooth light** sums (Minecraft smooth lighting, `ChunkLighting.SmoothCornerLight`): each corner's sun
and lamp light is summed over the front cell plus the same side/side/corner cells AO reads (a
side that blocks light, or a corner behind two blocked sides, is replaced by the front cell, so
an occluder darkens only through AO and light never leaks round a sealed diagonal), one exact
0..60 sum per channel, 12 bits a corner, traveling in a parallel 48-bit key
(`MeshScratch.PlaneLightKeys`) beside the int key, plus a 10-bit per-corner **tint code** in a
third, 40-bit key (`MeshScratch.PlaneTintKeys`). **Colored block light** is a dominant-color
palette tint, not RGB propagation: each block-light cell stores `level | slot << 4` (level bits
0-3, `BlockLightPalette` slot bits 4-6, so every level read masks `& 15`), seeded from
`BlockType.LightSlot`; a chunk with no tinted seed runs the unchanged `SpreadLight`, one with any
runs its twin `SpreadBlockLight`, where the brighter source wins and a tie at equal level goes to
the **lower slot** (re-queued so it propagates), which makes the result depend only on the seed
set, never on BFS order. `SmoothCornerLight` resolves the corner's four (level, slot) samples
through `LightTint.FromSamples` into `slotA | slotB << 3 | q << 6`, B's share being `q / 30`
(`LightTint.WeightDivisor`, t ≤ 0.5, canonical so equal mixes give equal codes), returned in
bits 12-21 of the corner code; the merge never crosses a tint change either, and the shader
lerps the decoded colors linearly, so greedy output stays pixel-identical. A red and a blue
pool therefore meet at a border softened over about a cell rather than adding up to purple.
`FaceLight` and the fluid corner light carry the tint into the procedural emitters (a blocked
fluid sample takes the lowest slot among the brightest open ones), the published light
snapshot gains a slot half (length `2 * BlockCount`) only in chunks that had a tinted seed, so
`EntityLight` hands entities an `EntityLightSample.Tint`, and the LOD bakes code 0. Uniform light gives `4L/60 == L/15`, so a
uniformly lit face is byte-identical to the old one-sample-per-face light. It gathers each key's
faces per depth slice into a 16×16 bit plane and merges rectangles — but only along an axis
where the key's AO and both corner lights are constant, so the merged quad's interpolated shade
and light equal the per-cell values exactly. Merged quads carry UVs 0..W / 0..H in tile units
(`TiledUv`) and the sampler's REPEAT tiles them. The per-voxel loop then skips greedy cells and
emits everything else as before (tree wood — `TreeWoodEmitter.EmitTreeWood`'s node box and connecting tubes
for a thin cell, `TreeWoodEmitter.EmitRimWood`'s stair-stepped `TreeWoodShape.RimBoxes` for a beveled
pixel-rim trunk shell — pebbles, fluids, any model with cropped or rotated UVs); its full, shaded,
culled cube faces use the same corner rule, and the procedural emitters (tree wood, pebbles, branch
piles, snow layers) bilinearly interpolate the smooth corner light
of the cell face they sample (`FaceLight`, flat where that face's front neighbor occludes). A snow layer drops a side facing an opaque cube or snow at least as deep, and a full stack's top under an opaque cube or more snow (`StackEmitter.SnowFaceHidden`, `SnowLayerCullingTests`).
Fluids average each corner over the same own/dx/dz/diagonal cells `CornerHeight` reads, with a
blocked cell replaced by the brightest open one, an order-independent rule so neighboring fluid
cells agree at every shared corner. `GenerateMeshData(manager, greedy: false)` is the test seam;
`GreedyMeshingTests` rasterizes both outputs into unit cell faces and requires them to be equal
(`SmoothLightTests` locks down the light rules themselves). Because a merge never crosses a
lighting or AO difference, this world's rough noise terrain and leaf canopies only give ~15-20 %
fewer vertices (`plans/PERF.md` item 11), not the 5-10× of flat worlds; smooth light left that
almost untouched (+0.2 % vertices over eight sampled surface and cave chunks, mesh-build time
within noise), since generated terrain is mostly uniformly lit and only lamp and shadow
gradients split merges.

**Chests are not meshed.** A chest block (`BlockType.Chest`) emits no quads: `GenerateMeshDataFromSnapshot` records its
cell in `ChunkMeshData.ChestCells` (local `Chunk.Idx` indices, published as `Chunk.ChestCells` like the flame points) and
the entity pass draws it from the shared chest model with its lid as it stands (`ChestPasses`, see "Chest model and lid").
`ChunkRenderLists.ChestChunks` (`ChunkManager.ChestChunks`) lists the chunks with one, apart from `Renderable`, because a
chunk holding nothing but a chest has no mesh at all.

**Sky cover (BT-10).** The sun seeds of a light volume normally start at the column's terrain height, which a battle
tower stands above (its heightmap must not be raised: the far LOD, water and spawns read it). A block flagged
`coversSky` (the three tower bricks) closes that gap. `Chunk.MayHaveSkyCover` is a sticky flag that a chunk may hold
one: the worldgen fill's `Finish` sets it for every generated chunk from the palette (`BlockType.ContainsSkyCover`
over `ChunkBlocks.IdSpan`, a few ids instead of 4096 cells; a stale entry could only raise it), `ChunkRestore` sets it
the same way on every restore (a save, an imported map, a client's streamed chunk) and `Chunk.SetBlockRaw` raises it when it writes a
covering id; an edit never clears it. `ChunkMeshBuilder` passes `ChunkLighting.AnySkyCover` (any of the 27 resolved
neighbors flagged) to `ComputeLight`, and only then does `RaiseSeedsUnderSkyCover` scan each volume column top-down and
start its seeds one above the highest covering cell at or above the heightmap start. Every seed still enters at 15, so
the pruned-seed flood stays exact, and the rule reads only the volume's blocks: singleplayer, a headless server and a
client compute byte-identical light with no wire change. The tower's slabs are 7 apart and the light border 15, so every
floor cell sees its slab inside its own volume. The collapse's light-radius remesh relights the ruin, whose rubble does
not cover. A chunk with no flagged neighbor pays 27 field reads. Known gap: a room cell in the volume above a chunk
whose slab lies past the volume's top still seeds sun, which can reach the chunk only down the helical stair, more than
10 cells away. `TowerInteriorLightTests`, `LightFloodTests`, `ChunkSkyCoverTests`.

**Biome-tinted grass and leaves.** Grass and every species' leaves (`oak_leaves`, `spruce_leaves`,
`baobab_leaves`, `stone_pine_leaves`, all on the `foliage` palette) take their color from the
climate at their position, Minecraft style, while `FaceTiles` keep the default colored tiles, so
inventory icons, item drops and the held block never change. It is block data: a `biomeTint`
object on the block JSON (`BlockDefinition.BiomeTintDefinition`) names a `palette` (`grass`, the
default, or `foliage`) and a `faces` map keyed by face name (`north`..`down`, or the shorthands
`side` and `all`). A face entry is either `{ "tile" }`, a tinted tile drawn *instead of* the
model's face (one quad; the leaves use it on every face), or `{ "overlay", "base"? }`, a tinted
cutout tile drawn *over* the face with `base` optionally replacing the untinted tile beneath (the
grass side: a `dirt` base, then `grass_side_textureless`). `ModelBakeSteps.ResolveBiomeTints`
resolves it in `ContentRegistry.BakeModels` into `BlockType.BiomeTint` (`Content/Blocks/BiomeTintFaces.cs`: `Palette` plus
per-face `Base`/`Tinted` tiles, `NoTile` where a face has none) and throws naming the file unless
the block `BakesGreedy` and is not translucent, so both mesh paths can treat every tinted face as a
plain cube face; an `overlay` is further refused on a cutout block, which has no opaque base quad.
The alpha-tested tint tiles (every overlay, and a cutout block's replacement tile) join
`BlockType.CutoutTiles`, so they get the coverage-preserving mips a leaf tile gets. The color is
`TerrainGenerator.Climate`, the temperature and humidity noises normalized through the biome ramps
to warmth 0..1 (exactly 0.5 across the temperate band) and wetness 0..1 (the two humidity ramps
read the same way, `BiomeClassifier.WetnessOf`: 0 fully dry, exactly 0.5 between the dry and the
semi-dry ramp, 1 wet), so the palette's three rows of a cold and a warm sRGB corner
(`SurfaceShadingConfig.GrassColorColdDry/WarmDry`, `ColdMid/WarmMid`, `ColdWet/WarmWet` and the
slightly darker `FoliageColor*`) sit on the biome lines: Dry on the desert and tundra, Mid on the
steppe, the savanna and the dry taiga edge, Wet on the forest and taiga. `BiomeTint.PaletteSrgb`
interpolates them piecewise (Dry to Mid up to wetness 0.5, Mid to Wet above), and each Mid corner
is yellower than the plain average of its Dry and Wet corners, so the grass dries monotonically
and the dry grasslands read dry (`TheGrassDriesMonotonically`). `BiomeTint` samples it on a 16-block lattice of
chunk-column corners: `TintsAt` reads the climate once and returns both palettes (through
`TintsFromClimate`), `FieldsFor(cx, cz)` gives a chunk column's `BiomeTintFields` (a grass and a
foliage `BiomeTintField`, four corners each) and `BiomeTintField.Sample` interpolates bilinearly in
weighted-sum form, which makes two columns sharing lattice points agree bit for bit, so the color
is continuous across chunk borders. The lattice climate is stored per column:
`TerrainGenerator.Compute` evaluates `Climate` once at the column's minimum corner into
`ColumnData.CornerWarmth/CornerWetness`, and the chunk mesher reads that through
`ChunkManager.ColumnSource` (`BiomeTint.FieldsFromNeighborhood` over the build's resolved 3x3
`ColumnTerrain`; `TryFieldsFor`/`TryCellTint` for any `IColumnSource`, false when a streamed corner
column is missing; `FieldsFor`/`CellTint` are those over `WorldgenColumnSource`), so a build
evaluates no noise and a multiplayer client tints from the server's streamed climate.
`FieldsAt`/`LatticeTints` still evaluate `Climate` directly (the felled tree's arbitrary origin, the
far LOD); the stored value is the same call at the same int-to-float lattice arguments, so both are
the same bits, which `BiomeTintTests.TheStoredCornerClimateReproducesTheDirectLatticeEvaluation`
pins. Because
it is a function of the vertex *position*, not of the cell, the greedy key is unchanged and a
merge never splits (a merged quad interpolates per triangle on the GPU where the field is bilinear;
over 16 blocks of a field with a wavelength of thousands that cross term is far below a byte step,
and it is accepted). The tint rides the vertex rgb as `shade * tint` (`BiomeTint.Shade`), flagged by
`Vertex.Normal` bit 4 (`IsBiomeTinted`), and the world fragment multiplies a flagged face by
`BiomeTint.Gain` (4, mirrored by the literal `4.0` behind `fTintGain` in `test-vertex.glsl` — keep
the two together): the textureless tiles are gray around `SurfaceShadingConfig.BiomeTintTextureGray`
(sRGB 98), so a byte rgb capped at 1 needs that headroom to reach grass green. The two grass tiles
are supplied art, and every leaf block has its own gray `<name>_leaves_textureless.png` beside it
(Rec. 601 luminance of `<name>_leaves.png`, alpha copied, scaled to the same mean gray), so the tint
multiplies the same art at any species' color. Both chunk mesh paths go through the one `ChunkMesher.EmitTintedCubeFace`, which draws the
base quad first and then the coplanar tinted quad, the overlay winning by draw order under
`Lequal`; the reflection and shadow passes reuse the same lists and inherit the order, and the
shadow pass's alpha test punches the overlay's holes over the opaque base. The far LOD reads the
same lattice from the region's own origin (`rx * regionChunkSpan`, never the prefetch origin
`baseChunkX`) through `LodFaceEmitter.AddBlockFace`, for grass tops and sides and for the canopy bumps, and a
tinted canopy's transparent texels fall back to the textureless gray (`vec3(0.1221)`, the decoded
98) instead of `uLodLeafColor`, so the tint still applies there; chunks, LOD and the felled tree
all bake the tint into their rgb. The felled tree is meshed by the real chunk mesher through
`CellBatchMesher.Build` (one scratch chunk per chunk its cells span, each at its real position), so
every chunk uses its own `BiomeTint.FieldsFor` and a falling canopy keeps the climate it stood in.
The mining pop and the break debris are standalone copies drawn at many
cells from streamed or cached geometry, so they wear the `BiomeTint` tiles (`PopBoxEmitter.EmitBox`'s
`biomeTint` argument; `BlockCubeCache.GetTinted`/`BuildTintedCube`, one cached debris mesh per
block, which only the debris asks for) with gray shade-only rgb, and the cell's color comes from
the plain program's per-draw `uBiomeTint` uniform (`BiomeTint.TryCellTint` at the mined cell over the
world's `ColumnSource` — the pop reads `RenderManager.LightSource.ColumnSource`, the debris gets
`BlockDebrisSystem.Burst`'s `columns:` argument — carried by `BlockDebris.Tint` for each cube, and
untinted white when a streamed column is missing), multiplied into bit-4 vertices in the vertex
shader. Its white
default is written in `RenderManager.InitWorldProgram`, which binds the program first;
`Shader.SetUniform` writes to whatever program is bound, so every per-draw set sits after
`worldShader.Use()` and is reset to white straight after the pop or debris draw for the plain
draws that follow (the chunk program has no such uniform). `PopBoxEmitter.MaxQuadsPerBox` (12: six
faces, each at most a base and a tinted quad) sizes the pop's streaming buffers and its static
index pattern. `visual-examples/grass-tint.json` (run with `VOXEL_TIME=147`) frames the forest
toward the desert edge, a grass rise, a broadleaf crown, the LOD meet, the taiga spruce, a mining
pop next to the default hotbar icon and a felled tree. Tests: `BiomeTintTests` (field continuity,
the palette range Gain can reach, climate vs biome classification), `BiomeTintMeshTests` (both mesh
paths, overlay order, no merge fragmentation, the field override, leaves keep sway),
`BiomeTintLodTests` (the LOD matches the chunk mesher's field, canopies carry the foliage field),
`BiomeTintTextureTests` (the tiles are gray at the Gain's gray and keep their source holes),
`BiomeTintPopTests`, `TopplingTreeTintTests` and the `biomeTint` rules in `ContentTests`.

**Chunk drawing.** Chunks do not own `Mesh` objects. `Chunk.UpdateMesh` uploads its vertex and
index lists into the two `ChunkGeometryPool`s owned by `RenderManager` (opaque and translucent:
one `BufferStorage` VBO + EBO + VAO each, allocated by `GeometryAllocator`, a first-fit free list
with coalescing in units of 64 vertices / 96 indices — one unit covers both buffers at the same
offset, since quads are exactly 4:6 — growing by doubling plus `CopyBufferSubData`), and keeps a
`GeometrySlice` per list (`OpaqueSlice`/`TranslucentSlice`; `HasMesh`/`HasTranslucentMesh` are
just those being non-null). Every frame each chunk pass — shadow, reflection, opaque
front-to-back, translucent back-to-front — frustum-culls and sorts on the CPU exactly as before,
but instead of a draw call per chunk it appends a `DrawElementsIndirectCommand` plus a
`vec4(origin, fade alpha)` per draw into `ChunkDrawBatch`'s persistent-mapped 3-slot ring (fenced
with `FenceSync`/`ClientWaitSync`, since VSync is off) and issues one
`MultiDrawElementsIndirect`. The chunk vertex shaders are compiled twice from the same source
(`Shader`'s `defines` prefix): the `#define CHUNK_DRAW` variant reads the `ChunkInfo` SSBO at
binding 0 at `uDrawBase + gl_DrawID` and transforms with `uViewProj`, while the plain variant
keeps `uModel`/`uMVP`/`uAlpha` for drops, TNT, falling blocks, toppling trees, block pops and debris
(`SetDrawUniforms`; the far LOD draws through the `CHUNK_DRAW` program too, `TerrainPasses.RenderLod` -> `chunkShader`), and takes its light per draw as `uEntitySun`/`uEntityBlock`, sampled from the
world at the entity's cell by `EntityLight.Sample` (each chunk keeps its mesh-time light snapshot
for that) and passed through `SetDrawUniforms`'s required `EntityLightSample` argument. Per-draw fade alpha reaches the fragment stage as the `flat` varying
`fAlpha`, because a uniform cannot vary between the sub-draws of one multi-draw. The wave and
sway blocks of `test-vertex.glsl` and `shadow-vertex.glsl` must stay byte-identical — a
`TO_WORLD(p)` macro is what lets them be, and `ChunkDrawShaderTests` guards the sway block and
its weight decode (both read `vColor.a` at location 1).

**Cave cull.** Every mesh build also records the chunk's face-to-face visibility set (`ChunkVisibility.Compute`, a
flood fill over its open, non-occluding cells: bit `a * 6 + b` when one open run touches faces a and b; all-air chunks
see through everything, enclosed ones through nothing), carried by `ChunkMeshData.Visibility` onto `Chunk.Visibility`;
a remesh that changes it bumps `ChunkRenderLists.VisibilityVersion`. `WorldRenderer.Render` calls
`ChunkManager.UpdateCaveCull` before the chunk passes, and `ChunkVisibilityGraph` re-walks (only when the camera's chunk,
`MeshVersion` or `VisibilityVersion` moved) from the camera's chunk, open on every face, breadth-first over the load
sphere: a chunk entered through one face is left only through the faces its set joins to it and never back against a
direction the path already took (Minecraft's advanced cave culling); a position with no loaded, meshed chunk counts as
open. Reached chunks carry the walk's stamp (`Chunk.CaveStamp`), and `TerrainPasses.RenderWorld` and `CullWater` skip
the rest (`ChunkVisibilityGraph.Sees`), so a sealed cave, or the underside of the surface seen from inside the rock, is
never drawn. The shadow cascades and the mirror are not cave-culled (a hidden chunk can still cast onto a visible one),
and the LOD cover still counts every meshed chunk. `PostProcessConfig.ChunkCaveCullEnabled` switches it off;
`ChunkVisibilityGraphTests`.

**Opaque occlusion cull (PERF_TODO F9).** The camera's opaque pass (`TerrainPasses.RenderWorld`)
adds its whole front-to-back run in one `BeginPass` but draws it as consecutive
`ChunkDrawBatch.DrawRange` segments, split at the shares of the run in
`PostProcessConfig.ChunkOcclusionSplits` (`HizOcclusion.SegmentStarts`). At each split
`ChunkOcclusionCuller.Cull` builds a half-resolution max-depth pyramid (R32F, padded to a power of
two, `hiz-build-compute.glsl`, twin `HizPyramid`) from the HDR depth the LOD and the earlier segments
wrote, then `hiz-cull-compute.glsl` (twin `HizOcclusion`) tests every later command: the chunk box
from its `ChunkInfo` origin grown by `HizOcclusion.ReachBlocks` (`ShadowCasterCull.CasterReachBlocks`:
the multiblock overhang plus the sway; opaque vertices have no waves), projected with the very
`uViewProj` the chunk program draws with, its rect rounded outward plus one texel, and a command whose
nearest corner depth lies strictly behind the pyramid max over the rect plus `DepthMargin` gets its
`InstanceCount` zeroed in place in the ring (a corner at or behind the eye plane always draws). Nothing
is reordered and a culled chunk has no fragment that could pass `LEQUAL`, fading or not, so the frame
is pixel-identical (a temporary occlusion-query and bit-diff check found 0 differences across the
scenarios, see PERF_TODO F9). Barriers: image access plus texture fetch after every pyramid level, the
last included, and `COMMAND` after the cull. Bindings: SSBO 0 ChunkInfo, 1 tile materials, 2 the
indirect ring (rebound every call, since a ring grow renames it); texture units
`RenderManager.HizDepthUnit` (10, unbound again before the next segment draws into that depth) and
`HizPyramidUnit` (11); image units 0 and 1. `ChunkOcclusionCullEnabled` switches it off. The cost sits
inside the `worldPass` row. The shadow cascades, the mirror and the translucent pass are not culled
this way (light-space occlusion measured ~0%).
