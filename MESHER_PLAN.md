# Binary greedy mesher, array-texture atlas, GL 4.6 + multi-draw indirect

## Context

The user asked whether a "binary voxel" greedy mesher (bitmask face culling + greedy quad
merging, the TanTanDev / cgerikj approach) can be implemented with texture support, and whether
compute shaders could take it further. Decisions taken with the user: implement the CPU binary
mesher, **also raise the GL context to 4.3+ in this plan** and follow with a GPU-side draw phase,
and tile textures through a **`GL_TEXTURE_2D_ARRAY`** (chosen for future mipmapping; it is also
at least as fast as `fract()` tiling since the hardware does the wrap).

What the code does today (`AGENTS.md:7` claims "greedy per-face meshing", which is wrong):
`Chunk.GenerateMeshDataFromSnapshot` (`OpenGLTest/src/Chunk.cs:873-1037`) is a per-voxel,
per-baked-quad emitter. Every visible cube face is its own 4-vertex/6-index quad. Facts that
shape the design:

1. **UVs cannot tile.** `Vertex.UvX/UvY` are absolute atlas UVs clamped to 0..1
   (`Data/Vertex.cs:14-15,38-39`). The atlas is a 4×4 2D grid, no gutters, `NEAREST`, mipmaps
   generated but never sampled (`TextureAtlas.cs`, `TextureLoader.cs:39-44`).
2. **Only some blocks are mergeable.** Full-cube `Model` blocks (9 opaque + leaves + glass) are;
   tapered wood (5 blocks), pebbles and water have procedural emitters that must stay.
3. **GL 3.3 core** (`Systems/WindowManager.cs:20`); all 17 shaders are `#version 330 core`.
   One VAO/VBO/EBO and one `glDrawElements` per chunk per pass (`Mesh.cs`, `Chunk.Render`),
   three uniforms per draw (`RenderManager.SetDrawUniforms`: model, MVP, fade alpha).
4. **Profile** (PERF_TODO.md): rest frame ~4.0 ms CPU / ~1.7 ms GPU; `worldPass` ~1.5 ms CPU for
   ~2000 draws; a mesh build is ~0.26 ms on a pool thread and never touches the frame. So the
   mesher buys vertex count, upload size and GC; **the draw-submission cost is what the GPU
   phase removes**, and the mesher's uniform quad output is its prerequisite.

## Phases

| Phase | Deliverable | Depends on |
|---|---|---|
| 1 | Array textures + 32-byte vertex with tile-local float UVs (all emitters, shaders, UI) | — |
| 2 | Binary greedy mesher inside `Chunk.GenerateMeshDataFromSnapshot`, differential tests | 1 |
| 3 | Context bump to GL 4.6 core, `#version 460 core`, no behaviour change | — |
| 4 | Chunk geometry pool + per-pass `glMultiDrawElementsIndirect`, per-chunk data in an SSBO | 2, 3 |
| 5 (optional, measured) | Compute-shader frustum cull writing the indirect buffer + count | 4 |

Each phase is a separate commit with its own before/after numbers in `PERF_TODO.md`.

---

## Phase 1 — Array textures and tile-local UVs

### Rule: every texture a `Vertex`-layout mesh samples is a `TEXTURE_2D_ARRAY`
The world shader draws chunks and block models from the atlas **and** item drops from standalone
sprite PNGs (`RenderManager.RenderItemDrops`, `:1308-1324`, texture from `ItemMeshCache.cs:48`);
the UI shader draws block icons from the atlas, item sprites, the font atlas and the crosshair.
Making the block atlas an array while those stay 2D would force a sampler branch in two shaders.
Instead every one of them becomes an array (the atlas with N layers, the rest with 1 layer), so
all four shader pairs sample `sampler2DArray` with `vec3(uv, layer)` and no branch exists.

- `TextureLoader.Upload` uploads a 1-layer array (`TexStorage3D` + `TexSubImage3D`,
  `GenerateMipmap(Texture2DArray)`); new `UploadArray(gl, layers, w, h)` for the atlas. Callers
  that set sampler state afterwards switch target: the sun's `Linear` filter
  (`RenderManager.cs:255-258`), `BitmapFont` (`UI/BitmapFont.cs:154-156`, `Linear` +
  `ClampToEdge`). Bind sites use `TextureTarget.Texture2DArray` (`RenderManager.BindWorldTextures`,
  `RenderShadows :489-490`, `UIRenderer.DrawMesh`).
- `TextureAtlas.Stitch` validates the shared tile size and returns the ordered tile images;
  `Build` uploads them as layers in sorted-file-name order; `TileOf(name)` returns a `ushort`
  layer; `LayerCount` is exposed; `TilesPerSide`/`TileUVSize` are deleted (only readers:
  `ModelBaker:116`, `Chunk.CropUv:1482`, `LodRegion:317`, `UvRect.Tile:25`).
- Sampler state on the atlas stays `REPEAT` / `NEAREST` / `NEAREST` so screenshots stay
  comparable; `NearestMipmapLinear` is the one-line follow-up the array makes safe.
- `BlockType.Point` (`BlockType.cs:177`) is replaced by `ushort` wherever it means a tile:
  `FaceTiles`, `Top/Bottom/Side`, `BakedQuad.Tile`, `BakedModel.FaceTiles`, `LodRegion`,
  `BlockCubeCache`. Mechanical; the compiler finds every site.

### `Data/Vertex.cs` — 32 bytes

| offset | field | type | meaning |
|---|---|---|---|
| 0 | Position | 3 × float | unchanged |
| 12 | PackedColor | 4 × byte | unchanged (shade rgb, alpha 254 = water) |
| 16 | Uv | 2 × float | **UV in texture-tile units** (0..1 on a single tile, 0..W on a merged quad; a font sub-rect is exact) |
| 24 | Sun | byte | unchanged |
| 25 | Block | byte | unchanged |
| 26 | Normal | byte | unchanged (bits 0-2 face, bit 3 submerged) |
| 27 | Sway | byte | unchanged |
| 28 | Layer | ushort | array layer; 0 for single-layer textures |
| 30 | Reserved | ushort | zero |

Why float and not fixed point: merged quads need integer UVs up to 16 with per-face flips, and
the font atlas (up to 2048 px, `BitmapFont.cs:30-31`) needs sub-texel precision; a 16-bit fixed
point cannot hold both, and a per-draw scale uniform would couple every draw site to the vertex
encoding. PERF item 9 packed UVs to save 4 bytes, but its own measurement found vertex fetch was
never the bottleneck; the greedy pass removes far more bytes than this adds.

- Primary constructor gains `ushort layer`; the old signature stays as an overload meaning
  "single-layer texture, layer 0, UV in 0..1" (`Primitives.CreateRectangle/CreateExtrudedSprite`,
  `BitmapFont`, the sun quad).
- `Mesh.cs`: location 2 = `(2, 2, Float, false, stride, 16)`; 3/4/5/6 move to 24/25/26/27;
  new location 7 = `VertexAttribIPointer(7, 1, UnsignedShort, stride, 28)`.

### Shaders (`test`, `shadow`, `ui`, `sun` vertex + fragment; `cloud-*` has its own vertex)
Vertex: `layout (location = 7) in uint vLayer; flat out uint fLayer; ... fLayer = vLayer;`,
`fTexCoord = vTexCoord` as today. Fragment: `uniform sampler2DArray uTextureAtlas;` (`uTex` in
the UI, `uSunTexture` for the sun) and `texture(sampler, vec3(fTexCoord, float(fLayer)))`.
Sites: `test-fragment.glsl:157`, `shadow-fragment.glsl:10`, `ui-fragment.glsl:23`; the sun
fragment keeps `fTexCoord - 0.5`. `flat` is correct: every emitter writes the same layer on all
four corners of a quad. The reflection pass reuses the world shader.

### Emitters → (layer, local UV)
- `Content/ModelBaker.BakeFace` (`:116-137`): `Uvs` become tile-local 0..1 (drop the
  `(tile.X + u) * tileUv` multiply); `BakedQuad.Tile` becomes `ushort Layer`.
- `Chunk.CropUv` (`:1466-1483`) → `TiledUv(int face, Vector3 local, Vector3 extent)`, returning
  the local UV only and mirroring `CropUv` term for term with the extent in place of 1:
  ```
  u = face switch { 0 => local.X, 1 => extent.X - local.X, 2 => extent.Z - local.Z, 3 => local.Z, _ => local.X };
  v = face switch { 4 => extent.Z - local.Z, 5 => local.Z, _ => extent.Y - local.Y };
  ```
  `CropUv(face, local)` = `TiledUv(face, local, Vector3.One)`. Working in 3D keeps the
  top/bottom faces right (their U runs along X and V along Z, unlike the side faces) without any
  per-face row/col bookkeeping. The reviewer verified `CropUv` reproduces `ModelBaker`'s
  `FlipU/FlipV` output (`ModelBaker.cs:29-44,131-137`) corner for corner on all six faces.
- `Chunk.EmitQuad` (`:1420`), `EmitBoxFace`, `EmitTubeCap`, `EmitFluid`, the baked-quad loop:
  pass the layer to the new constructor.
- `LodRegion.AddQuad` (`:309-352`): local corners (0,1)/(0,0)/(1,0)/(1,1), `layer = tile`.
- `Data/UvRect.cs`: `record struct UvRect(Vector2 Min, Vector2 Max, ushort Layer = 0)`;
  `UvRect.Tile(ushort layer)` = full 0..1 rect on that layer; `Primitives.Vert` passes it.
- `MeshBufferPool` doc comment: ~96 KB → ~128 KB per pooled list.

### Tests (Phase 1)
- `VertexPackingRoundTrips`: 32 bytes; UV (13.5, 0.25) and layer 300 round-trip; the legacy
  overload yields layer 0.
- `ContentTests`: baked `cube_all` quads have local UVs in {0,1} and their texture's layer;
  `TextureAtlas.LayerCount` equals the PNG count and `TileOf("stone")` is its sorted index.
  `TestContent.EnsureLoaded` keeps using `RegisterTilesWithoutUpload`.
- Visual: `basic.json`, `glass.json`, `water.json` screenshots identical to `HEAD` (texture
  orientation on grass side / log top / TNT, leaf holes, glass, HUD icons, item drops and
  sprites, font, crosshair, sun disc).

---

## Phase 2 — Binary greedy mesher

### Eligibility flag (`BlockType`, `Content/ContentRegistry.BakeModels`)
`internal static readonly bool[] GreedyById` beside `OccludesById` (`BlockType.cs:41-53`), read
through `BlockType.IsGreedy(byte)` (AggressiveInlining). Set in `BakeModels`
(`ContentRegistry.cs:96-140`) when: `Renderer == Model`, `baked.IsFullCube`, exactly 6 quads, all
`IsFullFace && Shade && CullFace == Face`, and each quad's local UVs equal
`Chunk.TiledUv(face, corner, One)` corner for corner (a rotated or cropped face UV keeps the
scalar path). Cutout (leaves, sway) and translucent (glass, cullSelf) blocks **are** greedy:
their culling rule is the same one and the destination list is chosen per block type. Extend
`SelfCheckBuiltInParents` (`:553-564`) to assert all four built-in parents bake greedy.

### Entry point
`GenerateMeshData(ChunkManager)` forwards to a new
`internal ChunkMeshData? GenerateMeshData(ChunkManager, bool greedy)`; the differential test
calls both. **No static switch**: xUnit runs test classes in parallel (no `xunit.runner.json`
or collections exist) and the pool threads would read it. `IsFullyEnclosed` and
`_solidCount == 0` stay in front.

### The pass (`Chunk.cs`, new `MeshScratch.cs`)
Runs in `GenerateMeshDataFromSnapshot` before the scalar loop, taking `ref indexOffset` /
`ref tIndexOffset` (`:879,885`) so the scalar loop continues from them; the scalar loop skips
cells with `IsGreedy(id)`.

Scratch in a `[ThreadStatic] MeshScratch` (~30 KB; `GenerateMeshData` has no `await`, so pool
threads, `ImmediateRemesh` on the main thread and `TopplingTree` each own one):
```
uint[3][324]  greedyCols    // per axis, 18-bit columns over the other two padded coords: cell holds a greedy block
uint[3][324]  occluderCols  // same, bit = BlockType.Occludes(cell)
long[16][256] faceEntries   // per depth slice of the current face: (key << 16) | (row << 8) | col, plus a count per slice
ushort[16]    rows          // bit plane for one key while merging
```
Padded index is `(x+1)*324 + (y+1)*18 + (z+1)`; bit index along an axis is the padded coord
(0..17); chunk cells are bits 1..16.

**A. Fill columns** — one pass over `padded` (18³): axis X column `y*18+z` bit `x`;
Y `x*18+z` bit `y`; Z `x*18+y` bit `z`.

**B. Face masks** — per axis/column: `visPos = greedy & ~(occ >> 1)` (the +1 neighbour does not
occlude), `visNeg = greedy & ~(occ << 1)`, both `& 0x1FFFE`. Halo occluders in bits 0/17 still
cull border faces exactly as `padded[pIdx + FaceNeighborDelta]` does today. Buried columns cost
two ops.

**C. Bucket and key** — walk each column's set bits once with `TrailingZeroCount` and append to
`faceEntries[depth]`. Per entry compute what the scalar path computes (`Chunk.cs:955-1013`):
`id`; skip if `type.CullSelf && neighbour == id`; 4 corner AO via `AODeltas[f]` + `VertexAO`
(8 bits); `sun = sunLight[lightCell]`, `block = blockLight[lightCell]` (nibbles,
`lightCell = LightIdx(x,y,z) + LightFaceDelta[f]`); `submerged = IsFluid(neighbour)`.
Key = `id | ao << 8 | sun << 16 | block << 20 | submerged << 24`. Per slice, `Array.Sort` the
≤ 256 longs so equal keys are contiguous.

**D. Merge per key run** — clear `rows`, set `rows[row] |= 1 << col`, then:
```
alongCol = ao[pairCol.a] == ao[pairCol.b] && ao[pairCol.c] == ao[pairCol.d]   // AO constant along the col axis
alongRow = likewise for the row axis
for row in 0..15: while rows[row] != 0:
  start = tzcnt(rows[row])
  width = alongCol ? tzcnt(~(rows[row] >> start)) : 1        // ushort promoted to int: bounded by 16 - start
  mask = ((1 << width) - 1) << start;  height = 1
  if alongRow: while row+height < 16 && (rows[row+height] & mask) == mask: rows[row+height] &= ~mask; height++
  rows[row] &= ~mask
  EmitGreedyQuad(f, d, start, row, width, height, key)
```
**Why `alongCol`/`alongRow`:** equal keys guarantee every cell has the same four corner values,
not that the shade is continuous across cells. For ordinary occluders the shared-vertex geometry
makes it continuous anyway, but a neighbour that casts AO without occluding (leaves, tapered
wood: `castsAO: true, occludes: false`) can give two same-key cells a shade step at their shared
edge, and merging would smooth it. Merging only along axes where the key's AO is constant makes
the big quad's bilinear shade equal the per-cell quads exactly, with no reliance on that argument.
Uniform-AO terrain merges in both axes; a strip beside a wall merges along the wall.

Corner pairs (from `CubeFaces[f].face`, `Chunk.cs:1530-1537`): c0–c1 runs along **y** on faces
0-3 and along **z** on faces 4-5; c0–c3 runs along **x** on faces 0,1,4,5 and along **z** on
faces 2,3. Row/col axes: X faces row = y, col = z → col pairs (c0,c3),(c1,c2), row pairs
(c0,c1),(c3,c2); Y faces row = x, col = z → col pairs (c0,c1),(c3,c2), row pairs (c0,c3),(c1,c2);
Z faces row = y, col = x → col pairs (c0,c3),(c1,c2), row pairs (c0,c1),(c3,c2). Encode as one
small static table.

**E. Emit** — `extent` = 1 on the face's plane axis and (width, height) mapped onto the two
in-plane axes; corner `j` position = `origin + CubeFaces[f].face[j] * extent` (so key AO corner
`j` lands on the matching corner); UV = `TiledUv(f, face[j] * extent, extent)`;
`layer = type.FaceTiles[f]`; shade = `ShadeLut[(f<<2)|ao_j]`; sun/block from the key; normal
`CubeFaces[f].normal`; sway = `type.Sway`; alpha 255; lists =
`type.Translucent ? tVertices : vertices`. Guard `list.Count > ushort.MaxValue - 3` **per
destination list before each 4-vertex add**, as `EmitFluid` does (`:1084`), and add the same
guard to the scalar baked-quad loop (`:945-1033`, which has none today).

### Tests (Phase 2) — `OpenGLTest.Tests/MeshingTests.cs`, new `GreedyMeshingTests.cs`
Light is part of the key, and sunlight at chunk (0,0,0) follows the real terrain heights
(`Chunk.cs:236-255`), so adjacent cells can differ. Tests that assert merged counts build their
walled area **high above the surface** (centre `new Vector3(0, 8, 0)`: the whole 46³ light
volume is 15) — add a `WalledArea(Vector3 centre)` overload.

Changed counts: `SharedFaceIsCulledBetweenCubes` 40/60 → **24/36** (two floating stone cubes,
uniform AO 3 everywhere, faces ±Y/±Z merge); `TouchingGlassCullsTheSharedFaces` 40/60 → **24/36**
(`glass.json` has `castsAO: false`). Unchanged: `SingleCubeEmitsSixFaces` 24/36,
`GlassNeverHidesAStoneFace` 24 + 20, `HoleInNeighbourPlaneEmitsOneFace` 4/6,
`AirPocketInsideSolidChunkStillMeshes` 24, `SubmergedFacesAreFlagged`, `Enclosed`, `AllAir`.

New:
- **Differential equivalence** (the important one): seeded random chunks mixing stone/dirt/
  glass/leaves/lamp/air plus pebbles, branches and water so the fallback is exercised, meshed
  with `greedy: true` and `false`. Rasterise both into `(x, y, z, face)` → (layer, shade
  **evaluated bilinearly** at each of the 4 unit-cell corners, sun, block, submerged, sway,
  list). Every quad must be axis-aligned with integer extents; the maps must be equal.
- **Tiled UVs**: a 3×2 slab yields one +Y quad with local UV extents (3, 2) in the orientation
  the scalar path gives the four corner cells; a wall strip's side quads likewise.
- **AO splits merges**: 16×16 floor plus one block on top → the top face is several quads and
  the cells beside the pillar carry darker corners; a strip along a wall still merges along it;
  a leaf block beside a stone strip does not merge across the shade step.
- **Flags**: built-in parents greedy; a face with `uv: [0,0,8,8]` or `rotation: 90` is not
  greedy and emits the same face count through the scalar path.
- **Ushort guard**: a checkerboard chunk does not throw in either list.

---

## Phase 3 — GL 4.6 core context

- `Systems/WindowManager.cs:20`: `new APIVersion(4, 6)`, keep `ContextProfile.Core`.
- All `assets/shaders/*.glsl`: `#version 460 core`. Nothing else changes; 3.3 code is valid 4.6.
- `RenderManager` startup line already prints the GL version; verify it says 4.6 and screenshots
  match. macOS is dropped by this (stuck at 4.1); note it in `AGENTS.md`.
- Rationale for 4.6 over 4.3: `gl_DrawID` (`ARB_shader_draw_parameters`),
  `glMultiDrawElementsIndirectCount` (`ARB_indirect_parameters`) and `BufferStorage` (4.4) are
  core in 4.6 and needed by Phases 4-5; the user's RTX 3070 and any 2015+ desktop driver has it.

---

## Phase 4 — Chunk geometry pool + multi-draw indirect

Goal: one `glMultiDrawElementsIndirect` per chunk pass (shadow, reflection, opaque, translucent)
instead of ~2000 `glDrawElements` + 3 uniform sets each. `Mesh` stays for drops, TNT, icons,
LOD, UI.

### New `Systems/ChunkGeometryPool.cs`
- Two pools (opaque, translucent), each: one big vertex buffer, one big index buffer
  (`BufferStorage`, `DynamicStorageBit`; start at e.g. 256 MB / 64 MB, grow by reallocating and
  `CopyBufferSubData` when a free-list allocation fails), one VAO with the `Vertex` attribs.
- Free-list allocator over both buffers (first-fit with coalescing; blocks in units of 64
  vertices / 96 indices to bound fragmentation). Returns a `GeometrySlice { VertexOffset,
  VertexCount, IndexOffset, IndexCount }`.
- Upload: `BufferSubData` from the `ChunkMeshData` lists on the main thread in
  `Chunk.UpdateMesh` (`Chunk.cs:1485-1500`), replacing `new Mesh(...)`; free the old slice.
  Indices stay `ushort` relative to the slice — MDI's `baseVertex` adds the vertex offset.
- `Chunk` keeps `OpaqueSlice?`/`TranslucentSlice?`; `HasMesh`/`HasTranslucentMesh`/
  `HasWaterMesh` keep their meaning; `Chunk.Render()` / `RenderTranslucent()` disappear.

### Per-chunk data SSBO
`struct ChunkDraw { vec4 originAndAlpha; }` in a `ChunkInfo` SSBO indexed by `gl_DrawID`
(compacted per pass: one entry per emitted command). The vertex shader replaces `uModel`/`uMVP`
with `uViewProj` + `origin`, and `uAlpha` with `.w`. Entity draws (TNT, drops, trees) keep the
uniform path through a second program variant compiled with a `#define CHUNK_DRAW` — `Shader`
gains the `defines` prefix PERF item 5 already prototyped (stash "PERF_TODO item 5").

### Command buffers
A `DrawElementsIndirectCommand[]` per pass (count, instanceCount = 1, firstIndex, baseVertex,
baseInstance = 0) in a persistent-mapped ring (3 frames, `MapPersistentBit | MapCoherentBit`),
filled by the existing CPU culls — `RenderShadows` (`:496-524`), `RenderWorld`
(`:1062-1097`, keeps its front-to-back sort), `CullWater`/`RenderWorldTranslucent`
(`:1123-1141`, keeps back-to-front), the reflection pass (`:761`) — followed by one
`MultiDrawElementsIndirect(Triangles, UnsignedShort, offset, count, 0)`. The lifetime fade
(`chunk.lifetime`) goes into the SSBO entry, and PERF item 10's "real delta time" lands here.

### Tests / verification (Phase 4)
- Unit tests for the free-list allocator (alloc/free/coalesce/grow, deterministic offsets).
- `LastVisibleChunkCount` and screenshots unchanged; `worldPass` CPU expected to fall from
  ~1.5 ms to well under 0.5 ms at the spawn view; GPU time unchanged or better (one VAO bind).
- Streaming flight: no holes, no stale slices (the command list is rebuilt every frame from
  `Renderable`, so a freed slice is never referenced).

---

## Phase 5 (optional) — Compute frustum cull

Only if Phase 4 leaves a measurable CPU cull cost (~4k AABB tests + sort is likely < 0.2 ms).
`chunk-cull.comp`: reads an SSBO of `{ aabbMin, aabbMax, slice, flags }` for every chunk in
`Renderable` (kept in step by `ChunkManager.SyncRenderable`), tests the pass frustum, appends a
command with `atomicAdd` on a count buffer; the pass draws with
`MultiDrawElementsIndirectCount`. Front-to-back order is lost (fine for depth-tested opaque;
the translucent pass keeps the CPU path for its sort). `MemoryBarrier(CommandBarrierBit |
ShaderStorageBarrierBit)` between dispatch and draw.

---

## Files to modify

Phase 1: `src/TextureAtlas.cs`, `src/TextureLoader.cs`, `src/Data/Vertex.cs`, `src/Mesh.cs`,
`src/BlockType.cs`, `src/Content/{ModelBaker,BakedQuad,BakedModel,ContentRegistry}.cs`,
`src/Chunk.cs` (UV plumbing, `TiledUv`), `src/LodRegion.cs`, `src/UI/Primitives.cs`,
`src/UI/BitmapFont.cs`, `src/Data/UvRect.cs`, `src/BlockCubeCache.cs`, `src/ItemMeshCache.cs`,
`src/Systems/{RenderManager,UIRenderer}.cs`, `assets/shaders/{test,shadow,ui,sun}-{vertex,fragment}.glsl`,
`src/Systems/MeshBufferPool.cs` (comment), tests.
Phase 2: `src/Chunk.cs`, new `src/MeshScratch.cs`, `src/BlockType.cs`,
`src/Content/ContentRegistry.cs`, tests.
Phase 3: `src/Systems/WindowManager.cs`, every `assets/shaders/*.glsl`.
Phase 4: new `src/Systems/ChunkGeometryPool.cs`, `src/Chunk.cs`, `src/Systems/{RenderManager,
ChunkManager}.cs`, `src/Shader.cs` (defines prefix), `assets/shaders/test-vertex.glsl`,
`shadow-vertex.glsl`, tests.
Docs: `AGENTS.md` (mesher description at `:7`/`:65`, vertex layout, array textures, GL 4.6,
pool), `PERF_TODO.md` (items 11-14 with measurements).

## Verification (all phases)

1. `dotnet build OpenGLTest.sln -c Release`;
   `dotnet test OpenGLTest.Tests/silkgltriangle.Tests.csproj -c Release` (71 existing + new).
2. Visual regression per phase: `VOXEL_SCRIPT=OpenGLTest/visual-examples/basic.json` (also
   `glass.json`; `water.json` with `VOXEL_TIME=70`) from a `HEAD` `git worktree` and from the
   branch; PNGs in `visual/<name>/` identical apart from noise.
3. Perf protocol from `PERF_TODO.md` "Measuring": spawn view at rest and the 23 s flight, with
   `// TEMP-DIAG` counters for resident vertices/indices, build ms, pool misses. Record
   before/after FPS, CPU/GPU ms, `worldPass`, `chunkApply`, gen0/gen2 per second. Expected:
   Phase 2 — large vertex drop in open terrain, smaller in forests (leaf keys fragment), frame
   time unchanged at rest; Phase 4 — `worldPass`/`shadows`/`waterPass` CPU collapse. Strip
   `TEMP-DIAG` before each commit.
4. In game: dig into a cliff and across a chunk border, glass beside glass and stone, stand
   underwater looking at the floor (caustic flag), leaf sway, TNT flash, drops and icons, HUD
   font, crosshair, sun disc.

## Decisions and assumptions

- Merge only where the result is pixel-identical (identical key **and** AO constant along the
  merged axis). Merging across lighting differences would change the look.
- Positions stay `float` (procedural emitters, LOD regions and the shared `Mesh` users need
  them) and UVs go back to `float`, hence 32 bytes; a later "vertex pulling" compaction (8-byte
  quads in an SSBO) is compatible with Phase 4's pool and out of scope.
- `NEAREST` min filter is kept in Phase 1 so screenshots stay comparable; enabling mipmaps is a
  one-line follow-up the array makes safe.
- GL 4.6 rather than 4.3: same desktop coverage, and Phases 4-5 need its core features; macOS
  support is consciously dropped.

---

## Status

Build with `~/.dotnet/dotnet.exe` (the system `dotnet` has no SDK); run tests with
`DOTNET_ROLL_FORWARD=LatestMajor` (only the .NET 10 runtime is installed, the test project
targets net9.0).

- [x] **Phase 1 — array textures + 32-byte vertex** (done 2026-09-08, uncommitted on `worldgen`).
  `TextureAtlas` loads one slice per PNG (sorted, ordinal) into a `TEXTURE_2D_ARRAY`;
  `TextureLoader.Upload` makes every other mesh texture a single-slice array; `Vertex` is 32
  bytes with float tile-local UVs and `ushort Layer` at attribute location 7
  (`VertexAttribIPointer`); `BlockType.Point` is gone (tiles are `ushort` slices);
  `BakedQuad.Uvs` are tile-local; `Chunk.TiledUv(face, local, extent)` is the single UV
  orientation rule (`CropUv` = extent 1); all eight world/shadow/UI/sun shaders sample
  `sampler2DArray`. 128 tests pass (new: `AtlasIsOneSlicePerTextureInFileNameOrder`,
  `BakedQuadsCarryTileLocalUvs`, updated `VertexPackingRoundTrips` and the two LOD tile tests).
  Visual check: `basic`/`glass`/`water` scenarios run before and after; every shot is visually
  identical (textures, canopy, glass, icons, water, caustics). Pixel diffs are non-zero only
  because two runs differ in wave/sway phase and chunk-streaming progress; the `basic` 5 s
  "spawn" shot is unusable in both runs (spawn chunks not loaded yet). AGENTS.md and
  PERF_TODO.md (item 11) updated. Not measured for FPS (no geometry change; item 9 showed
  vertex fetch is not the bottleneck).
- [x] **Phase 2 — binary greedy mesher** (done 2026-09-08, uncommitted on `worldgen`).
  `BlockType.GreedyById`/`IsGreedy` set in `BakeModels` via `ContentRegistry.BakesGreedy`
  (six full shaded quads culled against their own face, on the `CubeFaces` corners, with plain
  `TiledUv` UVs; `SelfCheckBuiltInParents` asserts all four parents qualify). `Chunk.EmitGreedyFaces`
  runs before the per-voxel loop, which skips greedy cells; `GenerateMeshData(manager, greedy)`
  is the internal test seam (`InternalsVisibleTo` added for the test project). Scratch lives in
  `MeshScratch.Current` (`[ThreadStatic]`, ~20 KB). Steps A/B/D/E are as planned; step C
  deviates: instead of bucketing entries per slice and sorting, each slice's visible faces go
  through a 512-slot open-addressed key table that maps straight to one 16×16 bit plane per
  key (cheaper than sorting up to 256 longs per slice, and no `faceEntries` buffer). The
  `alongCol`/`alongRow` rule is exactly the necessary condition: two same-key cells share a
  corner value iff the key's AO is constant along their axis, so no separate "leaf beside a
  strip" test exists — the differential test (random soup of stone/dirt/grass/log/glass/
  leaves/lamp/pebble/branch/water in the centre and all six neighbours, four seeds, chunk Y
  0/1/2/8) rasterises both paths into unit cell faces with bilinear shade and UV-mod-1 at every
  cell corner and compares them, plus verbatim comparison of all non-grid quads (random soup
  merges only a few quads in ten thousand, so it asserts "no more vertices", not "fewer"). Other new
  tests: eligibility of every shipped block and of hand-built models (cropped UV, rotation,
  missing cullface, shade=false), tiled UV extents on a 3×2 slab and a 1×2×3 wall, a bare
  16×16 floor = 24 vertices, a pillar splitting the floor top, a strip beside a wall merging
  along it (3 top quads), full-leaf chunk within ushort indices on both paths. Both
  merged-count tests moved to `MeshTestWorld.HighAbove` (chunk Y = 8). 141 tests pass.
  Measured (headless, generated chunks, per-quad -> greedy vertices): spawn forest -16 %,
  sea/beach -20 %, deep caves -14 %; build per non-empty chunk +12 % in the forest, within
  2 % elsewhere. The small gain is the terrain's: around spawn only ~95 of 3258 grass-top
  cells have an adjacent same-key neighbour (AO differs almost everywhere on the noise
  surface, canopies fragment the same way; sunlight is uniform there). Visual check: `basic`,
  `glass`, `water` from a `HEAD` worktree and the branch, diffed pixel-by-pixel — remaining
  differences are wave/sway phase, HUD numbers and streaming progress; the overlook, panes
  and walked shots were eyeballed and match (sand noise continues seamlessly across merged
  quads). At-rest frame identical (PERF_TODO Measurements, item 11 phase 2).
- [x] **Phase 3 — GL 4.6 core context** (done 2026-09-08, uncommitted on `worldgen`; implemented
  via the plan-and-implement workflow). `WindowManager` requests `APIVersion(4, 6)` with
  `ContextProfile.Core`; all 16 shaders that carry a directive declare `#version 460 core` (BOM on
  `selection-vertex.glsl` and the mixed CRLF/LF endings preserved byte for byte;
  `selection-frag.glsl` is a 3-byte BOM-only orphan nobody loads, left alone as a cleanup
  candidate). No other shader or C# change. New headless
  `ShaderVersionTests.EveryShaderDeclaresVersion460Core` guards the directive for the shaders
  Phase 4 adds. 140 tests pass (baseline was 139, not the 141 recorded above). Startup line on
  this machine: `GPU: Quadro T1000 with Max-Q Design/PCIe/SSE2 | NVIDIA Corporation | GL 4.6.0
  NVIDIA 597.06` — note the NVIDIA driver already handed a 3.3 core request its highest core
  version, so the line is confirmatory; the diff is the evidence. `basic`, `glass`, `water`
  (VOXEL_TIME=70) scenarios ran clean (no compile/link errors, 15 PNGs, shore shot eyeballed).
  AGENTS.md (new requirements paragraph, macOS drop) and PERF_TODO.md item 11 updated. Leftover
  noticed: AGENTS.md still says "There are no test projects and no CI", which is stale.
- [x] **Phase 4 — geometry pool + multi-draw indirect** (done 2026-09-09 on `worldgen`;
  implemented via the plan-and-implement workflow). Chunks no longer own `Mesh` objects: all
  chunk geometry lives in two shared pools and each chunk pass issues exactly one
  `glMultiDrawElementsIndirect`.
  **Pools and allocator.** New `Systems/ChunkGeometryPool.cs` keeps one `BufferStorage`
  (`DynamicStorageBit`) vertex buffer, one index buffer and one VAO per pool (opaque,
  translucent), and hands out a `GeometrySlice { UnitOffset, UnitCount, VertexCount,
  IndexCount }` whose `VertexOffset`/`IndexOffset` are computed from the unit offset. Behind it,
  new `Systems/GeometryAllocator.cs` is a GL-free first-fit free list with coalescing over a
  *single* unit space: one unit is 64 vertices **and** 96 indices, so one allocation covers both
  buffers at the same offset and nothing is wasted (quads are exactly 4:6). Fixed units bound
  fragmentation, and keeping the allocator free of GL makes its offsets deterministic and unit
  testable. Uploads go through the `CopyWriteBuffer` target on purpose — binding
  `ElementArrayBuffer` would rewrite whichever VAO a `Mesh` left bound — and `BufferSubData`
  copies synchronously, so `ChunkManager.ApplyMesh` still recycles its pooled lists immediately.
  Growth doubles the buffers, `CopyBufferSubData`s the old contents across, re-points the VAO
  and logs one line. `Vertex.Size`/`sizeof(ushort)` arithmetic widens to `long` before the cast
  at the GL boundary, so a doubling past 131072 units cannot overflow.
  **Draw batch.** New `Systems/ChunkDrawBatch.cs` owns a persistent-mapped
  (`MapWriteBit|MapPersistentBit|MapCoherentBit`) 3-slot ring of `DrawElementsIndirectCommand`
  (the 20-byte GL 4.6 §10.4 layout, asserted at type load) plus a parallel `vec4 originAndAlpha`
  ChunkInfo SSBO at binding 0, bound once with `BindBufferBase`. Passes call
  `BeginFrame`/`BeginPass`/`Add`/`Draw`; each pass draws its own contiguous run.
  *Deviation from the plan:* a `FenceSync`/`ClientWaitSync` per slot was added — VSync is off, so
  a GPU more than two frames behind would otherwise read a slot the CPU is rewriting — and the
  pass's SSBO base is passed as a `uDrawBase` int uniform (`draws[uDrawBase + gl_DrawID]`)
  instead of `BindBufferRange`, which would have needed
  `SHADER_STORAGE_BUFFER_OFFSET_ALIGNMENT` padding.
  **Shaders.** `Shader` gained the `defines` prefix that PERF item 5 had prototyped (the stash it
  named no longer existed, so it was written fresh): `InjectDefines` splices the defines plus a
  `#line 2` after the `#version` line, so error line numbers still point at the file on disk.
  `test-vertex.glsl` and `shadow-vertex.glsl` now carry a `#ifdef CHUNK_DRAW` variant that reads
  `ChunkInfo` at `uDrawBase + gl_DrawID` and transforms with `uViewProj`; a `TO_WORLD(p)` macro
  (translation by the chunk origin in the chunk variant, `uModel * p` in the plain one) keeps the
  wave and sway blocks byte-identical across both files and both variants, which
  `ChunkDrawShaderTests` now guards. *Deviation:* `test-fragment.glsl` was touched too (3 lines,
  not in the plan's file list) — `uniform float uAlpha` became the `flat in float fAlpha`
  varying, because a uniform cannot vary between the sub-draws of one multi-draw and the plan
  itself moves alpha into the SSBO's `.w`. `Mesh.ConfigureVertexAttribs` was extracted so the
  pool VAO cannot drift from the `Mesh` layout.
  **Wiring.** `RenderManager` compiles `chunkShader`/`chunkShadowShader` as second instances of
  the same sources with `#define CHUNK_DRAW`, mirrors every frame and one-time world uniform onto
  both programs (`InitWorldProgram`, `ApplyFrameUniformsTo`, `SetWorldUniform` for the three
  reflection sets), owns both pools and the batch, and converts the four chunk loops — shadow,
  reflection, opaque front-to-back, translucent back-to-front — to command fills followed by one
  MDI each. Every cull, sort and `LastVisibleChunkCount` assignment is exactly where it was.
  The pools are threaded `Program -> World -> ChunkManager -> Chunk.UpdateMesh` (both parameters
  default to null so the headless tests keep constructing `new ChunkManager(null!)`);
  `Chunk` keeps `OpaqueSlice?`/`TranslucentSlice?` plus the pool each came from, `HasMesh`/
  `HasTranslucentMesh`/`HasWaterMesh` keep their meaning, and `Chunk.Render()`/
  `RenderTranslucent()` are gone. `SetDrawUniforms` stays for LOD, drops, TNT, falling blocks,
  trees and block pops. `chunk.lifetime` finally advances by real delta time (PERF item 10),
  clamped to 0.1 s per frame; `LodRegion.Lifetime` keeps its 1/60 step.
  **Tests.** 153 pass (140 baseline + 13): `ShaderDefinesTests` (3, the injection and CRLF/null
  cases), `GeometryAllocatorTests` (7, first-fit determinism, hole reuse, coalescing in both
  directions, failure leaving state untouched, `Grow` extending the tail, double free) and
  `ChunkDrawShaderTests` (3, the byte-identical sway blocks, the CHUNK_DRAW declarations, the
  `fAlpha` varying).
  **Visual check.** `basic`, `glass` and `water` (all with `VOXEL_TIME=70`) run from a `HEAD`
  worktree and from the branch: 15 PNGs each, every log free of exceptions and compile/link
  errors, and a TEMP-DIAG `gl.GetError()` probe after all four MDI calls reported `NoError` in
  every scenario. Pixel diffs on the animated shots are large in absolute terms (up to 29.6 %),
  so a same-build control run was taken as a noise floor and lands at the same magnitude
  (30.4 % on the same shot); the differences are wave phase, cloud drift, sub-block camera
  position and streaming progress. Reflection, refraction, caustics, leaf holes, glass, LOD
  horizon and the HUD were eyeballed and match.
  **Perf** (this machine is a Quadro T1000 and far slower than the RTX 3070 of items 1-9, so only
  the interleaved pairs are comparable): `worldPass` 4.74 / 3.67 -> 0.50 / 0.51 ms, `shadows`
  1.31 / 1.23 -> 0.26 / 0.27, `waterPass` 1.83 / 1.19 -> 0.09 / 0.09, `reflect` 3.63 / 3.59 ->
  0.83 / 0.85 — the four chunk passes together 11.5 -> 1.7 ms, frame 28.20 / 32.12 -> 26.56 /
  30.73 ms. The session was GPU-bound, so much of that is the stall relocating into `lighting`
  (15.0 -> 24.2 ms, the frame's first GL call) rather than a pure saving — the unconverted
  `lodPass` and `uiPass` shrank 3x too — but `worldPass` (9.5x) and `waterPass` (20x) fell far
  beyond that redistribution. Visible-chunk count unchanged (2148 on both trees). Details in
  `PERF_TODO.md` item 11 and the `11 (phase 4)` Measurements row.
  **Leftovers noticed:** the opaque pool grows twice during the first stream-in (65536 ->
  131072 -> 262144 units) and the translucent one once (8192 -> 16384), settling at 244801 /
  262144 units (478 of 512 MB) and 12732 / 16384 — raising `OpaqueInitialUnits` to 262144 would
  trade 512 MB allocated up front for two fewer growth copies. Slices are never compacted, so a
  long session could fragment the pools (occupancy was 93 % here, so nothing to act on yet).
  `LodRegion.Lifetime` still steps at 1/60, so a chunk and the LOD under it fade at slightly
  different rates for half a second. AGENTS.md's stale "no test projects and no CI" line, flagged
  by the Phase 3 entry, was fixed here.
- [ ] **Phase 5 (optional) — compute frustum cull.**
