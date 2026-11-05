using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Content.Stations;
using VoxelEngine.Data;
using VoxelEngine.Effects.Climate;
using VoxelEngine.Effects.Lighting;
using VoxelEngine.Rendering.Geometry;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features.Underground;
using VoxelEngine.WorldGen.Trees;
using static VoxelEngine.Chunks.Chunk;
using static VoxelEngine.Chunks.Meshing.ChunkLighting;
using static VoxelEngine.Chunks.Meshing.ChunkFaceTables;

namespace VoxelEngine.Chunks.Meshing;

/// <summary>The per-chunk mesh build behind <see cref="Chunk.GenerateMeshData(ChunkManager)"/>: neighborhood resolve, padded and light snapshots, the light flood (<see cref="ChunkLighting"/>) and the per-voxel emitter loop after <see cref="ChunkMesher.EmitGreedyFaces"/>. Runs on mesh pool threads and on the main thread; allocation-free apart from the result, with every scratch array rented or thread-static.</summary>
internal static class ChunkMeshBuilder
{
    /// <inheritdoc cref="Chunk.GenerateMeshData(ChunkManager, bool)"/>
    internal static ChunkMeshData? GenerateMeshData(Chunk chunk, ChunkManager chunkManager, bool greedy)
    {
        // Resolve before renting or touching anything: an abandoned build should cost nothing.
        // The neighborhood lives in the thread's mesh scratch, not in two fresh arrays.
        var scratch = MeshScratch.Current;
        var neighbors = scratch.Neighbors;
        var neighborStates = scratch.NeighborStates;
        if (!TryResolveNeighbors(chunk, chunkManager, neighbors, neighborStates))
        {
            scratch.ClearNeighbors();
            return null;
        }

        // Nothing to draw: either all air, or a solid block of occluders whose six neighbors
        // seal every boundary face. The second case is most of the deep terrain in the load
        // sphere, and skipping it saves the light flood and the per-voxel walk below.
        if (chunk.SolidCount == 0 || IsFullyEnclosed(chunk, neighbors))
        {
            // The resolve fills only the slots it finds, so a stale neighbor left here would let
            // this thread's next build pass the missing-face-neighbor check
            scratch.ClearNeighbors();
            return new ChunkMeshData([], [], [], [], false);
        }

        // The nine chunk columns' terrain (heights, water levels, corner climate), resolved once
        // for the light flood, the water-surface codes and the tint. False is a streamed column
        // that has not arrived: the caller re-dirties exactly as for a vanished neighbor. Nothing
        // has been rented yet, so this exit needs no finally.
        if (!ChunkLighting.FillColumns(chunkManager.ColumnSource, (int)chunk.Position.X, (int)chunk.Position.Z, scratch.Columns))
        {
            scratch.ClearNeighbors();
            scratch.ClearColumns();
            return null;
        }

        var padded = ArrayPool<byte>.Shared.Rent(PaddedCount);
        var paddedState = ArrayPool<byte>.Shared.Rent(PaddedCount);
        // Rented arrays arrive dirty: the light snapshot is fully overwritten by the fill,
        // but the outputs must start at zero. Oversized rents are fine — everything below
        // only touches the first LightCount entries.
        var lightBlocks = ArrayPool<byte>.Shared.Rent(LightCount);
        var sunLight = ArrayPool<byte>.Shared.Rent(LightCount);
        var blockLight = ArrayPool<byte>.Shared.Rent(LightCount);
        var queue = ArrayPool<int>.Shared.Rent(LightCount);
        try
        {
            FillPaddedSnapshot(padded, paddedState, neighbors, neighborStates);
            FillLightSnapshot(lightBlocks, neighbors);
            scratch.ClearNeighbors();
            Array.Clear(sunLight, 0, LightCount);
            Array.Clear(blockLight, 0, LightCount);
            var anyTinted = ComputeLight(chunk, lightBlocks, sunLight, blockLight, queue);
            // Keep the chunk's own cells of the flood for entity lighting: a fresh array owned
            // by this build alone, published by UpdateMesh with the mesh it belongs to. A chunk
            // lit by colored light appends a second half holding each cell's color slot.
            var packed = new byte[anyTinted ? BlockCount * 2 : BlockCount];
            for (var x = 0; x < Size; x++)
            for (var y = 0; y < Size; y++)
            for (var z = 0; z < Size; z++)
            {
                var li = LightIdx(x, y, z);
                var b = blockLight[li];
                packed[Idx(x, y, z)] = (byte)(sunLight[li] << 4 | (b & BlockLevelMask));
                if (anyTinted) packed[BlockCount + Idx(x, y, z)] = (byte)(b >> 4);
            }
            return GenerateMeshDataFromSnapshot(chunk, padded, paddedState, sunLight, blockLight, chunkManager.MeshBuffers, greedy)
                with { Light = packed };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(padded);
            ArrayPool<byte>.Shared.Return(paddedState);
            ArrayPool<byte>.Shared.Return(lightBlocks);
            ArrayPool<byte>.Shared.Return(sunLight);
            ArrayPool<byte>.Shared.Return(blockLight);
            ArrayPool<int>.Shared.Return(queue);
            scratch.ClearColumns();
        }
    }

    // Slots of the six face-adjacent chunks in the 3x3x3 (cx*3+cy)*3+cz neighborhood
    // (slot 13 is this chunk itself). Meshing cannot proceed without these; the twenty
    // edge and corner slots only feed light and fluid corner heights, so they may be null.
    private static readonly int[] FaceNeighborSlots = [4, 10, 12, 14, 16, 22];

    /// <summary>
    /// Resolves the 3x3x3 chunk neighborhood once per mesh build, so both snapshot fills work
    /// from the same picture of the world instead of each racing the loader on its own. A
    /// neighbor that exists but has no block data yet counts as absent, exactly like one that
    /// was never loaded. Returns false when any of the six FACE neighbors is missing, which
    /// makes the mesh unbuildable (see <see cref="GenerateMeshData"/>); edge and corner
    /// neighbors are allowed to stay null and read as air.
    /// </summary>
    private static bool TryResolveNeighbors(Chunk chunk, ChunkManager chunkManager, Chunk?[] neighbors, byte[]?[] neighborStates)
    {
        // Hoisted out of the 26 probes below, and FLOORED rather than truncated so this path
        // into the chunk key matches VoxelCoords.ChunkPosOf's convention on the negative side
        var px = MathUtils.FloorToInt(chunk.Position.X);
        var py = MathUtils.FloorToInt(chunk.Position.Y);
        var pz = MathUtils.FloorToInt(chunk.Position.Z);

        for (var cx = 0; cx < 3; cx++)
        for (var cy = 0; cy < 3; cy++)
        for (var cz = 0; cz < 3; cz++)
        {
            var ni = (cx * 3 + cy) * 3 + cz;
            if (ni == 13)
            {
                neighbors[ni] = chunk;
                neighborStates[ni] = chunk.StateArray;
                continue;
            }

            // Each neighbor's state array is read once into the array: the field is volatile
            // and may be allocated by a main-thread fluid edit while the snapshot is filled
            if (chunkManager.Chunks.TryGetValue(px + cx - 1, py + cy - 1, pz + cz - 1, out var neighbor)
                && neighbor.IsBlockDataReady)
            {
                neighbors[ni] = neighbor;
                neighborStates[ni] = neighbor.StateArray;
                continue;
            }

            // The scratch is thread-static: a slot an earlier build filled must not survive as
            // a stale neighbor of this one
            neighbors[ni] = null;
            neighborStates[ni] = null;
        }

        foreach (var slot in FaceNeighborSlots)
        {
            if (neighbors[slot] == null)
                return false;
        }

        return true;
    }

    /// <summary>
    /// True when every cell of this chunk occludes and the 16x16 plane of each face
    /// neighbor that touches this chunk occludes too, so no face of any block can be seen.
    /// An air pocket anywhere inside (<c>_solidCount &lt; BlockCount</c>) has real faces around
    /// it and fails the test. Reads neighbor block arrays directly, the same tolerated race as
    /// the snapshot fills: an edit's own remesh of both chunks wins afterwards.
    /// </summary>
    private static bool IsFullyEnclosed(Chunk chunk, Chunk?[] neighbors)
    {
        if (chunk.SolidCount != BlockCount) return false;

        var occludes = BlockType.OccludesById;
        for (var i = 0; i < BlockCount; i++)
        {
            if (!occludes[chunk.Blocks[i]]) return false;
        }

        // Face neighbors (slot = (cx*3+cy)*3+cz): the plane that faces this chunk is the
        // neighbor's far plane along that axis.
        var negX = neighbors[4]!.Blocks;
        var posX = neighbors[22]!.Blocks;
        var negY = neighbors[10]!.Blocks;
        var posY = neighbors[16]!.Blocks;
        var negZ = neighbors[12]!.Blocks;
        var posZ = neighbors[14]!.Blocks;
        for (var a = 0; a < Size; a++)
        for (var b = 0; b < Size; b++)
        {
            if (!occludes[negX[Idx(Size - 1, a, b)]] || !occludes[posX[Idx(0, a, b)]]) return false;
            if (!occludes[negY[Idx(a, Size - 1, b)]] || !occludes[posY[Idx(a, 0, b)]]) return false;
            if (!occludes[negZ[Idx(a, b, Size - 1)]] || !occludes[posZ[Idx(a, b, 0)]]) return false;
        }

        return true;
    }

    /// <summary>
    /// Copies this chunk plus a 1-block border from the 26 surrounding chunks into a flat
    /// snapshot, so meshing does zero dictionary lookups. <paramref name="paddedState"/> is
    /// filled the same way with the per-block state bytes the fluid emitter needs (0 where the
    /// neighbor chunk is missing or has no state array yet). The arrays come pre-resolved from
    /// <see cref="TryResolveNeighbors"/>, so the 6 face neighbors are always present; a null
    /// edge or corner slot reads as air, which only affects fluid corner heights.
    /// </summary>
    private static void FillPaddedSnapshot(byte[] padded, byte[] paddedState, Chunk?[] neighbors, byte[]?[] neighborStates)
    {
        for (var px = 0; px < Padded; px++)
        {
            var cx = px == 0 ? 0 : px == Padded - 1 ? 2 : 1;
            var lx = (px - 1) & SizeMask;

            for (var py = 0; py < Padded; py++)
            {
                var cy = py == 0 ? 0 : py == Padded - 1 ? 2 : 1;
                var ly = (py - 1) & SizeMask;
                var rowBase = px * PaddedSq + py * Padded;
                var neighborRow = (cx * 3 + cy) * 3;

                for (var pz = 0; pz < Padded; pz++)
                {
                    var cz = pz == 0 ? 0 : pz == Padded - 1 ? 2 : 1;
                    var ni = neighborRow + cz;
                    var neighbor = neighbors[ni];
                    var blockIndex = Idx(lx, ly, (pz - 1) & SizeMask);
                    padded[rowBase + pz] = neighbor?.Blocks[blockIndex] ?? 0;
                    paddedState[rowBase + pz] = neighborStates[ni]?[blockIndex] ?? 0;
                }
            }
        }
    }

    /// <summary>
    /// Turns the padded block snapshot into vertices. Plain full cubes (<see cref="BlockType.IsGreedy"/>)
    /// go through the binary greedy pass first (<see cref="ChunkMesher.EmitGreedyFaces"/>), which merges
    /// identical neighboring faces into one quad. The per-voxel loop then handles the rest: each
    /// block picks an emitter from its <see cref="BlockType.Renderer"/> — the procedural ones
    /// (tree wood, pebbles, branch piles) build their geometry from tiles, everything else
    /// replays its baked model's quads.
    /// </summary>
    private static ChunkMeshData GenerateMeshDataFromSnapshot(Chunk chunk, byte[] padded, byte[] paddedState,
        byte[] sunLight, byte[] blockLight, MeshBufferPool pool, bool greedy)
    {
        // Rented, not allocated: ChunkManager returns them once the mesh is on the GPU
        var vertices = pool.RentVertices();
        var indices = pool.RentIndices();
        ushort indexOffset = 0;

        // Rent blended output only at its first emission. A dry chunk must not consume pooled
        // wet buffers: empty translucent lists are deliberately not retained by the chunk pool.
        List<Vertex>? tVertices = null;
        List<ushort>? tIndices = null;
        ushort tIndexOffset = 0;

        // Glass shares the translucent lists, so "has a translucent mesh" is not "has water";
        // the reflection and scene-copy passes need the narrower answer.
        var hasWater = false;

        // Exposed hot-fluid cells (lava open to the air), allocated only when one turns up
        List<ushort>? vents = null;
        List<ushort>? springVents = null;
        // Chunk-local flame points of the blocks that burn one (torches), likewise lazy
        List<Vector3>? flames = null;

        // World origin of this chunk, for the procedural emitters' per-cell world position; a
        // submerged face whose column leaves the padded snapshot reads its water level from the
        // build's resolved columns instead (SubmergedSurfaceCode)
        var chunkBaseY = chunk.LowerBoundY;
        var worldX0 = (int)chunk.Position.X * Size;
        var worldZ0 = (int)chunk.Position.Z * Size;
        var columns = MeshScratch.Current.Columns;

        // Biome tint of grass and leaves, both palettes from the stored corner climate of the
        // four lattice columns (no noise evaluation)
        var fields = BiomeTint.FieldsFromNeighborhood(columns);
        // Corner scratch of a biome-tinted face in the per-voxel loop below
        Span<byte> tShade = stackalloc byte[4];
        Span<float> tSun = stackalloc float[4];
        Span<float> tBlock = stackalloc float[4];
        Span<ushort> tTint = stackalloc ushort[4];

        if (greedy)
            ChunkMesher.EmitGreedyFaces(MeshScratch.Current, padded, paddedState, chunkBaseY,
                sunLight, blockLight, vertices, indices, ref indexOffset, pool,
                ref tVertices, ref tIndices, ref tIndexOffset, in fields);

        for (var x = 0; x < Size; x++)
        for (var y = 0; y < Size; y++)
        for (var z = 0; z < Size; z++)
        {
            var blockId = chunk.Blocks[Idx(x, y, z)];
            if (blockId == 0) continue;
            // Already emitted (merged) by the greedy pass above
            if (greedy && BlockType.IsGreedy(blockId)) continue;

            var type = BlockType.ById[blockId];
            if (type == null) continue;

            var basePos = new Vector3(x, y, z);
            var pIdx = (x + 1) * PaddedSq + (y + 1) * Padded + (z + 1);

            // Tree wood thinner than a full cube is a node box plus a tube toward every
            // connected neighbor (see EmitTreeWood); a full-thickness or detached cell falls
            // through to the plain model path below, which then also culls its faces against
            // a same-block full-thickness neighbor, since tree wood never occludes
            var cullSameFullWood = false;
            if (type.Renderer == BlockRenderer.TreeWood)
            {
                if (TreeWoodShape.IsThin(paddedState[pIdx]))
                {
                    TreeWoodEmitter.EmitTreeWood(vertices, indices, ref indexOffset, padded, paddedState, pIdx, sunLight, blockLight,
                        x, y, z, type, basePos);
                    continue;
                }

                // A pixel-rim trunk shell with an exposed corner is beveled from its four edge
                // neighbors (TreeWoodShape.RimCornerMask; the diagonals never change it, since a
                // cell only carves its own material); see EmitRimWood. Mask 0 is a plain cube.
                if (BlockType.HasPixelRim(blockId))
                {
                    var rimMask = TreeWoodEmitter.RimMaskInSnapshot(padded, paddedState, pIdx, blockId);
                    if (rimMask != 0)
                    {
                        TreeWoodEmitter.EmitRimWood(vertices, indices, ref indexOffset, padded, paddedState, pIdx, sunLight,
                            blockLight, x, y, z, type, basePos, rimMask);
                        continue;
                    }
                }

                cullSameFullWood = true;
            }

            // Pebbles render as 1-3 ankle-high boxes sharing the cell; see EmitPebbles
            if (type.Renderer == BlockRenderer.Pebbles)
            {
                StackEmitter.EmitPebbles(worldX0, worldZ0, vertices, indices, ref indexOffset, padded, paddedState, pIdx, sunLight, blockLight,
                    x, y, z, type, basePos);
                continue;
            }

            // Branch piles render as 1-4 crossed sticks sharing the cell; see EmitBranchPile
            if (type.Renderer == BlockRenderer.BranchPile)
            {
                StackEmitter.EmitBranchPile(worldX0, worldZ0, vertices, indices, ref indexOffset, padded, paddedState, pIdx, sunLight, blockLight,
                    x, y, z, type, basePos);
                continue;
            }

            // Layered snow renders as one box whose height is its stack depth; see EmitSnowLayers
            if (type.Renderer == BlockRenderer.SnowLayers)
            {
                StackEmitter.EmitSnowLayers(worldX0, chunkBaseY, worldZ0, vertices, indices, ref indexOffset, padded, paddedState, pIdx, sunLight, blockLight,
                    x, y, z, type, basePos);
                continue;
            }

            // A fluid's sloped surface is read out of paddedState and goes into the
            // translucent lists; see EmitFluid
            if (type.Renderer == BlockRenderer.Fluid)
            {
                FluidEmitter.EmitFluid(pool, ref tVertices, ref tIndices, ref tIndexOffset, padded, paddedState, pIdx,
                    sunLight, blockLight, x, y, z, type, basePos);
                hasWater = true;
                if (IsHotFluidVent(padded[pIdx], padded[pIdx + FaceNeighborDelta[4]]))
                    (vents ??= []).Add((ushort)Idx(x, y, z));
                // A water top inside a hot spring steams (its site is the same pure hash the
                // carve read, so the vents match the pond)
                else if (WorldFeatureConfig.HotSpringsEnabled
                         && padded[pIdx] == TerrainGenerator.SeaBlock
                         && padded[pIdx + FaceNeighborDelta[4]] == BlockType.Air
                         && HotSpringGenerator.IsSite(worldX0 + x, worldZ0 + z))
                    (springVents ??= []).Add((ushort)Idx(x, y, z));
                continue;
            }

            // A pane is a post plus an arm toward each neighboring pane or solid full cube, read
            // from the snapshot (see PaneEmitter); a glass pane draws blended like glass
            if (type.Renderer == BlockRenderer.Pane)
            {
                if (type.Translucent)
                {
                    tVertices ??= pool.RentTranslucentVertices();
                    tIndices ??= pool.RentTranslucentIndices();
                    PaneEmitter.EmitPane(tVertices, tIndices, ref tIndexOffset, padded, paddedState, pIdx,
                        sunLight, blockLight, x, y, z, type, basePos);
                }
                else
                    PaneEmitter.EmitPane(vertices, indices, ref indexOffset, padded, paddedState, pIdx,
                        sunLight, blockLight, x, y, z, type, basePos);
                continue;
            }

            // A multiblock part is a solid, occluding cell that draws nothing: the anchor's
            // model already covers the whole 2x2x2 volume it belongs to.
            if (type.Renderer == BlockRenderer.MultiblockPart) continue;

            // ...and the anchor draws that whole model, pre-rotated to the facing its state
            // byte's low two bits carry (see MultiblockShape), as does a facing block such as a
            // wall torch, turned about its own cell (see HorizontalFacing); an openable block also
            // reads its open bit (DoorState), picking one of its eight models.
            var model = type.RotatedModels != null
                ? type.RotatedModels[BlockType.ShapeIndex(paddedState[pIdx], type.RotatedModels.Length)]
                : type.BakedModel;
            if (model == null) continue;

            // A block with a flame (a torch) records the flame's exact chunk-local point, turned with
            // its facing like the model, for the world's flame particles
            if (type.FlamePointAt(paddedState[pIdx]) is { } flame)
                (flames ??= []).Add(basePos + flame);

            // Canopy leaves and plants ride the wind in the vertex shader; everything else stays
            // planted. A rooted plant's per-vertex weight rides the alpha byte (ModelVertexAlpha).
            var sway = type.Sway ? 1f : 0f;
            var lightBase = LightIdx(x, y, z);
            var quads = model.Quads;

            // Translucent blocks (glass) replay the same baked quads into the blended
            // lists drawn after the opaque world; culling, AO and lighting below are shared.
            ref var dstOffset = ref (type.Translucent ? ref tIndexOffset : ref indexOffset);

            for (var q = 0; q < quads.Length; q++)
            {
                var quad = quads[q];
                // The quern's plate and handle spin as a dynamic entity (see QuernManager):
                // the chunk keeps only the base quads below the model's air gap.
                if (type.Renderer == BlockRenderer.Quern && QuernRenderer.IsTopQuad(quad)) continue;
                var cullFace = quad.CullFace;
                int lightCell;
                // Bit 3 of the packed normal: this face looks into water, so the fragment
                // shader paints caustics on it. Interior sub-cell quads have no neighbor to
                // look into and stay false.
                var submerged = false;
                ushort waterSurface = 0;

                if (cullFace >= 0)
                {
                    var neighbor = padded[pIdx + FaceNeighborDelta[cullFace]];
                    // Only opaque neighbors hide a face. Leaves are cutout, so they never
                    // hide: a face culled behind one shows through its texture holes as an
                    // X-ray into the ground. Leaf-vs-leaf keeps both sides (Minecraft
                    // fancy-style) so the canopy still reads solid through the holes, while
                    // leaf-vs-rock draws the rock side only (the leaf side culls as before,
                    // so no coplanar double quad z-fights). CullSelf additionally hides a
                    // face against the same block, the way glass panes fuse.
                    if (BlockType.Occludes(neighbor) || (type.CullSelf && neighbor == blockId)) continue;
                    // Stacked full-thickness tree wood hides its shared faces itself - unless the
                    // neighbor is a beveled pixel-rim shell whose face toward this cell is
                    // carved, which would open a hole (a core, a detached log or a mask-0 shell
                    // beside a rounded corner keeps its face). Non-rim species never read a mask.
                    if (cullSameFullWood && neighbor == blockId
                        && WoodState.IsFullCube(paddedState[pIdx + FaceNeighborDelta[cullFace]])
                        && (!BlockType.HasPixelRim(blockId)
                            || TreeWoodEmitter.RimFaceFull(padded, paddedState, pIdx + FaceNeighborDelta[cullFace], blockId,
                                TreeWoodShape.Opposite(cullFace)))) continue;

                    // Light of the air cell in front of the face, from the wide light volume
                    // (so lamps across the chunk border still reach): the flat fallback for
                    // quads that do not smooth; a full face averages per corner below.
                    // A neighbor that would read as black light is exactly one that occludes,
                    // and that face is already gone, so no fallback is needed here.
                    lightCell = lightBase + LightFaceDelta[cullFace];
                    submerged = BlockType.IsFluid(neighbor);
                    // The water surface the caustics measure depth from: this face's own body
                    // of water, resolved exactly as the greedy pass keys it
                    if (submerged)
                        waterSurface = WaterSurfaceCodes.BakedWaterSurface(padded, paddedState, pIdx + FaceNeighborDelta[cullFace],
                            chunkBaseY, columns);
                }
                else
                {
                    // An interior quad of a sub-cell model has no neighbor to read: light it
                    // from its own cell, which passes light precisely because the model is not
                    // a full cube (the generalized form of the pebble trick).
                    lightCell = lightBase;
                }

                // Meshes are indexed with ushort (mirrors EmitFluid): stop emitting into a
                // list that is full instead of wrapping the offset
                var dstVertices = type.Translucent ? tVertices ??= pool.RentTranslucentVertices() : vertices;
                var dstIndices = type.Translucent ? tIndices ??= pool.RentTranslucentIndices() : indices;
                if (dstVertices.Count > ushort.MaxValue - 3) continue;

                var sun = sunLight[lightCell] / 15f;
                var cellBlock = blockLight[lightCell];
                var block = (cellBlock & BlockLevelMask) / 15f;
                var flatTint = (ushort)LightTint.Flat(cellBlock & BlockLevelMask, cellBlock >> 4);
                var face = quad.Face;
                var positions = quad.Positions;
                var uvs = quad.Uvs;
                var normal = quad.Normal;
                // A sub-cell model block (a cross-shaped plant) can be climate-tinted on every
                // quad; a greedy block here is the per-voxel replay and reads type.BiomeTint
                // instead (see below), so the two never apply at once.
                var modelTint = type.ModelBiomeTint;

                if (!quad.Shade)
                {
                    // Minecraft's per-element "shade": false — full brightness, no face
                    // brightness and no ambient occlusion
                    for (var j = 0; j < 4; j++)
                    {
                        var vertex = new Vertex(basePos + positions[j], 255, 255, 255,
                            ChunkMesher.ModelVertexAlpha(type, positions[j].Y), uvs[j], quad.Tile,
                            sun, block, normal, sway, submerged, waterSurface, flatTint);
                        dstVertices.Add(modelTint is { } palette ? ChunkMesher.ApplyModelTint(vertex, fields.Field(palette)) : vertex);
                    }
                }
                else if (quad.IsFullFace)
                {
                    // Only a quad that fills its cube face has meaningful corner neighbors,
                    // so per-corner AO applies exactly as it did for hand-written cubes. A face
                    // that culls against its own side (exactly the set the greedy pass takes,
                    // see ModelBakeSteps.BakesGreedy) also averages its light per corner.
                    var aoDeltas = AODeltas[face];
                    var smooth = cullFace == face;

                    // A biome-tinted block BakesGreedy, so this is one of its six plain cube
                    // faces (smooth, positions and UVs in CubeFaces order): the corners are
                    // computed exactly as below and handed to the emitter the greedy pass uses.
                    if (type.BiomeTint is { } bt)
                    {
                        for (var j = 0; j < 4; j++)
                        {
                            var side1Id = padded[pIdx + aoDeltas[j * 3 + 0]];
                            var side2Id = padded[pIdx + aoDeltas[j * 3 + 1]];
                            var cornerId = padded[pIdx + aoDeltas[j * 3 + 2]];
                            var ao = VertexAO(BlockType.OccludesAO(side1Id), BlockType.OccludesAO(side2Id),
                                BlockType.OccludesAO(cornerId));
                            tShade[j] = ShadeLut[(face << 2) | ao];
                            var code = SmoothCornerLight(face, j, sunLight, blockLight, lightBase, side1Id, side2Id, cornerId);
                            tSun[j] = (code & 63) / SmoothLightDivisor;
                            tBlock[j] = ((code >> 6) & 63) / SmoothLightDivisor;
                            tTint[j] = (ushort)(code >> 12);
                        }
                        ChunkMesher.EmitTintedCubeFace(dstVertices, dstIndices, ref dstOffset, face, basePos, Vector3.One,
                            tShade, tSun, tBlock, tTint, bt, fields.Field(bt.Palette), submerged, waterSurface, sway);
                        continue;
                    }

                    for (var j = 0; j < 4; j++)
                    {
                        var side1Id = padded[pIdx + aoDeltas[j * 3 + 0]];
                        var side2Id = padded[pIdx + aoDeltas[j * 3 + 1]];
                        var cornerId = padded[pIdx + aoDeltas[j * 3 + 2]];

                        var ao = VertexAO(BlockType.OccludesAO(side1Id), BlockType.OccludesAO(side2Id),
                            BlockType.OccludesAO(cornerId));
                        var shade = ShadeLut[(face << 2) | ao];

                        var vSun = sun;
                        var vBlock = block;
                        var vTint = flatTint;
                        if (smooth)
                        {
                            var code = SmoothCornerLight(face, j, sunLight, blockLight, lightBase, side1Id, side2Id, cornerId);
                            vSun = (code & 63) / SmoothLightDivisor;
                            vBlock = ((code >> 6) & 63) / SmoothLightDivisor;
                            vTint = (ushort)(code >> 12);
                        }

                        var vertex = new Vertex(basePos + positions[j], shade, shade, shade,
                            ChunkMesher.ModelVertexAlpha(type, positions[j].Y), uvs[j], quad.Tile,
                            vSun, vBlock, normal, sway, submerged, waterSurface, vTint);
                        dstVertices.Add(modelTint is { } palette ? ChunkMesher.ApplyModelTint(vertex, fields.Field(palette)) : vertex);
                    }
                }
                else
                {
                    // A sub-cell quad floats inside the cell, where corner sampling would
                    // darken it by neighbors it does not touch: shade it flat and unoccluded
                    var shade = ShadeLut[(face << 2) | 3];
                    for (var j = 0; j < 4; j++)
                    {
                        var vertex = new Vertex(basePos + positions[j], shade, shade, shade,
                            ChunkMesher.ModelVertexAlpha(type, positions[j].Y), uvs[j], quad.Tile,
                            sun, block, normal, sway, submerged, waterSurface, flatTint);
                        dstVertices.Add(modelTint is { } palette ? ChunkMesher.ApplyModelTint(vertex, fields.Field(palette)) : vertex);
                    }
                }

                dstIndices.Add((ushort)(dstOffset + 0));
                dstIndices.Add((ushort)(dstOffset + 1));
                dstIndices.Add((ushort)(dstOffset + 2));
                dstIndices.Add((ushort)(dstOffset + 0));
                dstIndices.Add((ushort)(dstOffset + 2));
                dstIndices.Add((ushort)(dstOffset + 3));

                dstOffset += 4;
            }
        }

        return new ChunkMeshData(vertices, indices, tVertices ?? new List<Vertex>(), tIndices ?? new List<ushort>(), hasWater)
            { HotFluidVents = vents?.ToArray(), SpringVents = springVents?.ToArray(), FlamePoints = flames?.ToArray() };
    }
}
