using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using VoxelEngine.Effects.Lighting;
using static VoxelEngine.Chunks.Chunk;
using static VoxelEngine.Chunks.Meshing.ChunkFaceTables;

namespace VoxelEngine.Chunks.Meshing;

/// <summary>
/// GL-free voxel lighting: the wide light-volume BFS flood (sun + colored lamp light) and the
/// per-corner light/AO queries the mesher samples. Extracted from <see cref="Chunk"/>; reads
/// Chunk's shared geometry tables and light-volume constants through <c>using static Chunk</c>,
/// and the AO neighbor table through <c>using static ChunkFaceTables</c>.
/// </summary>
internal static class ChunkLighting
{
    // Same face normals as LightFaceDelta, for the air cell in front of a face
    internal static readonly int[] LightFaceDelta = new int[6];
    // The AO side/side/corner cells as light-volume index deltas (LightSize stride, not the
    // padded one), read by SmoothCornerLight
    private static readonly int[][] LightAODeltas = new int[6][];

    static ChunkLighting()
    {
        for (var i = 0; i < 6; i++)
        {
            LightFaceDelta[i] = LightDelta(CubeFaces[i].normal);

            var lightDeltas = new int[12];
            for (var j = 0; j < 4; j++)
            for (var k = 0; k < 3; k++)
                lightDeltas[j * 3 + k] = LightDelta(AONeighbors[i][j][k]);
            LightAODeltas[i] = lightDeltas;
        }
    }

    private static int LightDelta(Vector3 o) => (int)o.X * LightSq + (int)o.Y * LightSize + (int)o.Z;

    /// <summary>
    /// Minecraft-style smooth light of corner <paramref name="j"/> of face <paramref name="face"/>
    /// of the cell at <paramref name="lightBase"/>: sun and lamp light each summed over four
    /// samples - the air cell in front of the face and the side1, side2 and corner cells the AO
    /// of that corner reads - returned as <c>sunSum | blockSum &lt;&lt; 6 | tint &lt;&lt; 12</c>
    /// (each sum 0..60, so a vertex takes <c>sum / 60</c>, which equals <c>level / 15</c> exactly
    /// under uniform light; <c>tint</c> is the 10-bit <see cref="LightTint"/> code of the same four
    /// (level, slot) samples, so the low 12 bits alone are the light). A side sample that does not pass light is replaced by the front cell's value: a
    /// blocked cell always stores 0, so counting it would darken the corner a second time on top
    /// of the AO that cell already casts. The corner sample likewise counts only if it passes
    /// light and at least one side is open, so light never leaks in through a sealed diagonal. A
    /// substituted sample counts with the front cell's color slot as well as its level.
    /// <paramref name="j"/> follows the <c>CubeFaces[face].face</c> corner order, the same
    /// assumption the per-corner AO makes of a quad's positions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int SmoothCornerLight(int face, int j, byte[] sunLight, byte[] blockLight, int lightBase,
        ushort side1Id, ushort side2Id, ushort cornerId)
    {
        var front = lightBase + LightFaceDelta[face];
        var d = LightAODeltas[face];
        var passes = BlockType.PassesLightById;
        var open1 = passes[side1Id];
        var open2 = passes[side2Id];
        var openC = passes[cornerId] && (open1 || open2);
        var i1 = open1 ? lightBase + d[j * 3] : front;
        var i2 = open2 ? lightBase + d[j * 3 + 1] : front;
        var iC = openC ? lightBase + d[j * 3 + 2] : front;
        var sun = sunLight[front] + sunLight[i1] + sunLight[i2] + sunLight[iC];
        // blockLight holds level | slot << 4 (see ComputeLight)
        int bF = blockLight[front], b1 = blockLight[i1], b2 = blockLight[i2], bC = blockLight[iC];
        int lF = bF & BlockLevelMask, l1 = b1 & BlockLevelMask, l2 = b2 & BlockLevelMask, lC = bC & BlockLevelMask;
        var tint = LightTint.FromSamples(lF, bF >> 4, l1, b1 >> 4, l2, b2 >> 4, lC, bC >> 4);
        return sun | (lF + l1 + l2 + lC) << 6 | tint << 12;
    }

    /// <summary>
    /// The four <see cref="SmoothCornerLight"/> codes of face <paramref name="face"/> of the cell
    /// at padded index <paramref name="pIdx"/> (light index <paramref name="lightBase"/>), for an
    /// emitter to interpolate over the geometry it draws inside the cell (<see cref="FaceLight"/>).
    /// </summary>
    internal static FaceLight SmoothFaceLight(int face, ushort[] padded, int pIdx, byte[] sunLight,
        byte[] blockLight, int lightBase) =>
        FaceLight.FromCorners(face,
            SmoothCornerCode(face, 0, padded, pIdx, sunLight, blockLight, lightBase),
            SmoothCornerCode(face, 1, padded, pIdx, sunLight, blockLight, lightBase),
            SmoothCornerCode(face, 2, padded, pIdx, sunLight, blockLight, lightBase),
            SmoothCornerCode(face, 3, padded, pIdx, sunLight, blockLight, lightBase));

    private static int SmoothCornerCode(int face, int j, ushort[] padded, int pIdx, byte[] sunLight,
        byte[] blockLight, int lightBase)
    {
        var d = AODeltas[face];
        return SmoothCornerLight(face, j, sunLight, blockLight, lightBase,
            padded[pIdx + d[j * 3]], padded[pIdx + d[j * 3 + 1]], padded[pIdx + d[j * 3 + 2]]);
    }

    /// <summary>
    /// An emitter's light for face <paramref name="face"/>: the cell face's smooth corner light
    /// when the neighbor in front of it is open, otherwise the flat light of
    /// <paramref name="fallbackCell"/> - a face backed against an occluder must not average the
    /// occluder's zero light, or it renders black.
    /// </summary>
    internal static FaceLight EmitterFaceLight(int face, ushort[] padded, int pIdx, byte[] sunLight,
        byte[] blockLight, int lightBase, int fallbackCell) =>
        BlockType.Occludes(padded[pIdx + FaceNeighborDelta[face]])
            ? FaceLight.Flat(sunLight[fallbackCell], (byte)(blockLight[fallbackCell] & BlockLevelMask),
                (byte)(blockLight[fallbackCell] >> 4))
            : SmoothFaceLight(face, padded, pIdx, sunLight, blockLight, lightBase);

    /// <summary>
    /// Per-axis map from a light-volume coordinate (0..LightSize-1) to the neighborhood chunk
    /// index (0..2) and the block index inside it (0..Size-1). The volume spans LightBorder cells
    /// into each neighbor: [1, LightBorder) of the previous chunk, all of this one, then
    /// [0, LightBorder) of the next. Shared by FillLightSnapshot and ComputeLight so the block
    /// snapshot and the sun seeds can never disagree about which column a cell sits in.
    /// </summary>
    internal static void BuildLightAxisMap(Span<int> mapChunk, Span<int> mapLocal)
    {
        for (var l = 0; l < LightSize; l++)
        {
            if (l < LightBorder) { mapChunk[l] = 0; mapLocal[l] = l + 1; }
            else if (l < LightBorder + Size) { mapChunk[l] = 1; mapLocal[l] = l - LightBorder; }
            else { mapChunk[l] = 2; mapLocal[l] = l - LightBorder - Size; }
        }
    }

    /// <summary>
    /// Copies the LightSize^3 block volume centered on this chunk straight from the 3x3x3
    /// neighboring chunks' block storages, so the fill loop does zero dictionary lookups.
    /// <paramref name="neighbors"/> (each chunk's storage, read once) comes pre-resolved from <see cref="ChunkMeshBuilder.TryResolveNeighbors"/>,
    /// which guarantees the 6 face neighbors are present; the corner and edge slots may still
    /// be null and read as air, which only costs a little light accuracy in the diagonal
    /// corners of the wide light volume. Same thread-safety convention as FillPaddedSnapshot
    /// (block edits race the read; the edit's own remesh wins after).
    /// </summary>
    internal static void FillLightSnapshot(ushort[] ids, ChunkBlocks?[] neighbors)
    {
        // Per-axis map from volume coordinate to (chunk 0..2, local block 0..15). The volume
        // spans LightBorder cells into each neighbor: [1, LightBorder) of the previous
        // chunk, the full own chunk, then [0, LightBorder) of the next one.
        Span<int> mapChunk = stackalloc int[LightSize];
        Span<int> mapLocal = stackalloc int[LightSize];
        BuildLightAxisMap(mapChunk, mapLocal);

        // The z axis splits into runs of one neighbor chunk with consecutive local indices, so
        // each (lx, ly) row is a few span copies instead of a per-cell lookup. Every slot of
        // the map belongs to exactly one run, so a row is fully overwritten (the rented array
        // arrives dirty).
        Span<int> runStart = stackalloc int[LightSize];
        Span<int> runLength = stackalloc int[LightSize];
        var runCount = 0;
        for (var lz = 0; lz < LightSize; lz++)
        {
            var extends = lz > 0 && mapChunk[lz] == mapChunk[lz - 1] && mapLocal[lz] == mapLocal[lz - 1] + 1;
            if (extends) { runLength[runCount - 1]++; continue; }
            runStart[runCount] = lz;
            runLength[runCount] = 1;
            runCount++;
        }

        for (var lx = 0; lx < LightSize; lx++)
        for (var ly = 0; ly < LightSize; ly++)
        {
            var rowBase = (lx * LightSize + ly) * LightSize;
            var neighborRow = (mapChunk[lx] * 3 + mapChunk[ly]) * 3;
            var blockRow = (mapLocal[lx] << (2 * SizeShift)) | (mapLocal[ly] << SizeShift);
            for (var r = 0; r < runCount; r++)
            {
                var zStart = runStart[r];
                var target = ids.AsSpan(rowBase + zStart, runLength[r]);
                var neighbor = neighbors[neighborRow + mapChunk[zStart]];
                if (neighbor == null) { target.Clear(); continue; }
                // The low SizeShift bits of blockRow are 0, so blockRow | local == blockRow + local
                // and the run's local z indices are contiguous in Idx order.
                neighbor.CopyRun(blockRow | mapLocal[zStart], runLength[r], target);
            }
        }
    }

    /// <summary>
    /// Flood-fills skylight and lamp light over the wide light snapshot (see
    /// FillLightSnapshot), so lamps and skylight changes up to LightPropagationRadius
    /// blocks away still light this chunk's faces. Sun seeds are air or leaf cells at or
    /// above the terrain height (nothing generated is ever solid above it, and canopies
    /// pass light); lamp seeds are emissive cells. Both spread through air and leaves
    /// losing one level per block, so caves and overhangs fall off into darkness. All
    /// inputs are local (snapshot + height cache), so this is thread-safe and needs no
    /// cross-chunk state. Seeds all enter at the same max level, so a FIFO queue visits
    /// every cell at its final level first, each cell is enqueued at most once, and the
    /// caller-sized queue never overflows.
    /// <para>
    /// Every passable sky cell is set to 15, but only the sun seeds that touch a passable
    /// non-seed cell are queued. That is exact because every sun seed enters at the one max
    /// level and SpreadLight is a monotone max BFS: a seed whose passable neighbors are all 15
    /// writes nothing, and any shortest path from a seed to a lit cell can start at the last
    /// seed on it, which touches a non-seed and so is queued. Seeding sun at varying levels
    /// would break this.
    /// </para>
    /// <paramref name="sun"/> and <paramref name="block"/> must be zeroed and hold at
    /// least LightCount entries each, as must <paramref name="queue"/>.
    /// <para>
    /// <paramref name="block"/> stores <c>level | slot &lt;&lt; 4</c>: the lamp level in bits 0-3
    /// (<see cref="BlockLevelMask"/>) and the <see cref="BlockLightPalette"/> slot of the light's
    /// color in bits 4-6, so every read of a level must mask. <paramref name="sun"/> is plain
    /// levels. Returns whether any lamp seed carries a non-default slot; when none does, every
    /// slot is 0 and the byte is exactly the old level.
    /// </para>
    /// <para>
    /// With <paramref name="skyCover"/> (some chunk of the 27-chunk neighborhood may hold a
    /// <c>coversSky</c> block, <see cref="AnySkyCover"/>), a sun seed must also lie above the highest
    /// covering cell of its volume column (<see cref="RaiseSeedsUnderSkyCover"/>). That is the battle
    /// tower's dark interior: the tower stands above the heightmap, which must not be raised (the far
    /// LOD, water and spawns read it), and its slabs, 7 apart, always fall inside a chunk's volume
    /// (LightBorder 15 above it). Every seed still enters at 15, so the queued-seed exactness above
    /// holds unchanged. The rule reads only the volume's own blocks, so a client's streamed copy lights
    /// exactly like the server, and a collapse's light-radius remesh relights the ruin. Without the
    /// flag nothing extra runs.
    /// </para>
    /// </summary>
    internal static bool ComputeLight(Chunk chunk, ushort[] ids, byte[] sun, byte[] block, int[] queue, bool skyCover)
    {
        var worldY0 = chunk.LowerBoundY - LightBorder;

        var head = 0;
        var tail = 0;

        // The LightSize^2 volume columns only ever land in the 3x3 chunk columns around this one
        // (LightBorder < Size), so their heightmaps are fetched once by GenerateMeshData into
        // MeshScratch.Columns (ChunkLighting.FillColumns) instead of probing the column source per
        // column (PERF_TODO C2). Same nine columns, same heights - only the number of probes changes.
        Span<int> mapChunk = stackalloc int[LightSize];
        Span<int> mapLocal = stackalloc int[LightSize];
        BuildLightAxisMap(mapChunk, mapLocal);

        var columns = MeshScratch.Current.Columns;

        // Sunlight seeds: air cells at or above the terrain height see the sky directly.
        // Leaves seed too: canopies pass light, so the sky reaches straight through them
        // (branch tops under leaves stay lit; only depth falloff dims deep cover). Tree
        // wood seeds as well (see PassesLight): a thin limb never casts a full shadow.
        // seedStart[col] is the first volume row of a column at or above its terrain height, so
        // ly >= seedStart[col] is exactly worldY0 + ly >= height (clamped into 0..LightSize).
        Span<int> seedStart = stackalloc int[LightSq];
        for (var lx = 0; lx < LightSize; lx++)
        {
            var columnRow = mapChunk[lx] * 3;
            var heightRow = mapLocal[lx] << SizeShift;
            var colRow = lx * LightSize;
            for (var lz = 0; lz < LightSize; lz++)
            {
                var height = columns[columnRow + mapChunk[lz]].Heights[heightRow | mapLocal[lz]];
                seedStart[colRow + lz] = Math.Clamp(height - worldY0, 0, LightSize);
            }
        }
        if (skyCover) RaiseSeedsUnderSkyCover(ids, seedStart);

        // Every passable seed is set to 15, but only the seeds touching a passable non-seed cell
        // are queued: the cell above a seed is a seed or opaque, the one below is a non-seed only
        // at the column's first seed row, and a lateral one only under its own column's seedStart.
        var passes = BlockType.PassesLightById;
        for (var lx = 0; lx < LightSize; lx++)
        for (var lz = 0; lz < LightSize; lz++)
        {
            var col = lx * LightSize + lz;
            var start = seedStart[col];
            if (start >= LightSize) continue;

            var westStart = lx > 0 ? seedStart[col - LightSize] : 0;
            var eastStart = lx < LightSize - 1 ? seedStart[col + LightSize] : 0;
            var northStart = lz > 0 ? seedStart[col - 1] : 0;
            var southStart = lz < LightSize - 1 ? seedStart[col + 1] : 0;
            var sideTop = Math.Max(Math.Max(westStart, eastStart), Math.Max(northStart, southStart));

            var columnBase = (lx * LightSize) * LightSize + lz;
            for (var ly = start; ly < LightSize; ly++)
            {
                var i = columnBase + ly * LightSize;
                if (!passes[ids[i]]) continue;
                sun[i] = 15;

                var touchesNonSeed = ly == start && ly > 0 && passes[ids[i - LightSize]];
                if (!touchesNonSeed && ly < sideTop)
                {
                    // An absent neighbor column has start 0, which never exceeds ly
                    touchesNonSeed = (westStart > ly && passes[ids[i - LightSq]])
                        || (eastStart > ly && passes[ids[i + LightSq]])
                        || (northStart > ly && passes[ids[i - 1]])
                        || (southStart > ly && passes[ids[i + 1]]);
                }

                if (touchesNonSeed) queue[tail++] = i;
            }
        }

        SpreadLight(ids, sun, queue, ref head, ref tail);

        // Lamp seeds: emissive cells glow at full strength, then spread like sunlight, each
        // carrying its color slot in bits 4-6.
        head = 0;
        tail = 0;
        var anyTinted = false;
        var emissionById = BlockType.Emission;
        var slotById = BlockType.LightSlot;
        for (var i = 0; i < LightCount; i++)
        {
            var id = ids[i];
            var emission = emissionById[id];
            if (emission == 0) continue;
            var slot = slotById[id];
            block[i] = (byte)(emission | slot << 4);
            anyTinted |= slot != 0;
            queue[tail++] = i;
        }

        // All-default light keeps the plain pass: the values (slot bits all 0) and the cost are
        // exactly what they were before colored light existed
        if (anyTinted)
            SpreadBlockLight(ids, block, queue, ref head, ref tail);
        else
            SpreadLight(ids, block, queue, ref head, ref tail);
        return anyTinted;
    }

    /// <summary>Whether any resolved chunk of a mesh build's 27-chunk neighborhood may hold a
    /// <c>coversSky</c> block (<see cref="Chunk.MayHaveSkyCover"/>), which turns the sky-cover rule of
    /// <see cref="ComputeLight"/> on.</summary>
    internal static bool AnySkyCover(Chunk?[] neighbors)
    {
        foreach (var neighbor in neighbors)
            if (neighbor is { MayHaveSkyCover: true })
                return true;
        return false;
    }

    /// <summary>
    /// Raises each volume column's first sun seed row to one above the highest covering cell at or
    /// above it (<see cref="BlockType.CoversSkyById"/>), scanning top-down, so the cells under a tower
    /// slab get no sky. A covering block never passes light, so the cell under a raised start stays
    /// opaque, which is what the queued-seed rule of <see cref="ComputeLight"/> assumes.
    /// </summary>
    private static void RaiseSeedsUnderSkyCover(ushort[] ids, Span<int> seedStart)
    {
        var covers = BlockType.CoversSkyById;
        for (var lx = 0; lx < LightSize; lx++)
        for (var lz = 0; lz < LightSize; lz++)
        {
            var col = lx * LightSize + lz;
            var start = seedStart[col];
            var columnBase = (lx * LightSize) * LightSize + lz;
            for (var ly = LightSize - 1; ly >= start; ly--)
            {
                if (!covers[ids[columnBase + ly * LightSize]]) continue;
                seedStart[col] = ly + 1;
                break;
            }
        }
    }

    /// <summary>
    /// The nine chunk columns' terrain around chunk (chunkX, chunkZ), written to
    /// columns[dx * 3 + dz] for chunk column (chunkX - 1 + dx, chunkZ - 1 + dz). Because
    /// LightBorder (15) is smaller than Size (16) the light volume never reaches past those nine,
    /// and neither does the padded snapshot's water-surface lookup or the +1 tint corners, so
    /// fetching them once replaces LightSize^2 cache probes (PERF_TODO C2) and serves every
    /// terrain read of the build. Returns false when <paramref name="source"/> lacks one (a
    /// streamed column that has not arrived): the build must then be abandoned, never fed a
    /// substitute, and the dirty gate in <see cref="ChunkManager"/> keeps such a build from being
    /// scheduled in the first place. The worldgen source always fills all nine.
    /// </summary>
    internal static bool FillColumns(IColumnSource source, int chunkX, int chunkZ, ColumnTerrain[] columns)
    {
        for (var dx = 0; dx < 3; dx++)
        for (var dz = 0; dz < 3; dz++)
            if (!source.TryGet(chunkX - 1 + dx, chunkZ - 1 + dz, out columns[dx * 3 + dz]))
                return false;
        return true;
    }

    /// <summary>
    /// <see cref="SpreadLight"/> for lamp light that carries a color slot (<c>level | slot
    /// &lt;&lt; 4</c>). The dominant-color rule: a cell takes the slot of whichever source reaches
    /// it brightest, and at an equal level the lower slot wins. A tie re-enqueues the cell so the
    /// lower slot keeps spreading; that terminates because at a fixed level a cell's slot only ever
    /// decreases. Both rules are order-free, so the result is a function of the seed set alone -
    /// not of BFS order or where the volume starts - and neighboring chunks agree at their seams
    /// wherever both volumes see the same seeds.
    /// <para>
    /// Because a cell can be enqueued more than once here, bit 7 of its byte marks "waiting in the
    /// queue": a cell already waiting is updated in place rather than queued again, so at most
    /// LightCount entries are ever live and the queue runs as a ring over its first LightCount
    /// slots. Every entry clears its mark when it is dequeued, so all marks are gone on return.
    /// </para>
    /// </summary>
    internal static void SpreadBlockLight(ushort[] ids, byte[] levels, int[] queue, ref int head, ref int tail)
    {
        // The seeds are queued unmarked at [head, tail); mark them
        for (var i = head; i < tail; i++) levels[queue[i]] |= QueuedMark;
        var count = tail - head;
        var h = head % LightCount;
        var t = tail % LightCount;
        while (count > 0)
        {
            var c = queue[h];
            h = h + 1 == LightCount ? 0 : h + 1;
            count--;
            var value = (byte)(levels[c] & ~QueuedMark);
            levels[c] = value;
            var level = value & BlockLevelMask;
            if (level <= 1) continue;
            var next = level - 1;
            var slotBits = value & BlockSlotMask;

            var lx = c / LightSq;
            var rem = c - lx * LightSq;
            var ly = rem / LightSize;
            var lz = rem - ly * LightSize;

            if (lx > 0) RaiseTinted(ids, levels, queue, ref t, ref count, c - LightSq, next, slotBits);
            if (lx < LightSize - 1) RaiseTinted(ids, levels, queue, ref t, ref count, c + LightSq, next, slotBits);
            if (ly > 0) RaiseTinted(ids, levels, queue, ref t, ref count, c - LightSize, next, slotBits);
            if (ly < LightSize - 1) RaiseTinted(ids, levels, queue, ref t, ref count, c + LightSize, next, slotBits);
            if (lz > 0) RaiseTinted(ids, levels, queue, ref t, ref count, c - 1, next, slotBits);
            if (lz < LightSize - 1) RaiseTinted(ids, levels, queue, ref t, ref count, c + 1, next, slotBits);
        }
        head = tail = 0;
    }

    // Bit 7 of a blockLight byte while SpreadBlockLight has the cell waiting in its queue
    private const byte QueuedMark = 0x80;

    // Color slot bits of a blockLight byte
    private const int BlockSlotMask = 0x70;

    /// <summary>One neighbor step of <see cref="SpreadBlockLight"/>: a dimmer neighbor, or an
    /// equally lit one holding a higher slot, takes <c>next | slotBits</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RaiseTinted(ushort[] ids, byte[] levels, int[] queue, ref int tail, ref int count,
        int n, int next, int slotBits)
    {
        if (!PassesLight(ids[n])) return;
        var ln = levels[n];
        var nl = ln & BlockLevelMask;
        // next > 0 always holds here (the caller's level is at least 2)
        if (nl > next || (nl == next && slotBits >= (ln & BlockSlotMask))) return;
        if ((ln & QueuedMark) != 0)
        {
            // Already waiting: its turn will spread the new value
            levels[n] = (byte)(next | slotBits | QueuedMark);
            return;
        }
        levels[n] = (byte)(next | slotBits | QueuedMark);
        queue[tail] = n;
        tail = tail + 1 == LightCount ? 0 : tail + 1;
        count++;
    }

    /// <summary>Whether voxel light travels through a block: air, plus leaves, whose canopy
    /// stays lit through instead of casting solid shade, plus tree wood, which is mostly far
    /// thinner than a full cube and should dapple rather than block, plus the
    /// ankle-high pebbles, which are too small to shade. Everything else
    /// (rock, TNT) still blocks. AO likewise ignores leaves and thin shapes
    /// (see <see cref="BlockType.OccludesAO"/>); geometry culling does not
    /// (see <see cref="BlockType.Occludes"/>). Which blocks pass is data now:
    /// <c>light.passes</c> in each block's JSON, defaulting to "not a full cube".</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool PassesLight(ushort blockId) => BlockType.PassesLightById[blockId];

    /// <summary>One BFS pass: spreads <paramref name="levels"/> through light-passing cells
    /// (see <see cref="PassesLight"/>) of the light snapshot, losing one level per block.
    /// Shared by the sun and lamp passes. Every cell is still enqueued at most once, since
    /// every traversal costs exactly one level.</summary>
    internal static void SpreadLight(ushort[] ids, byte[] levels, int[] queue, ref int head, ref int tail)
    {
        var passes = BlockType.PassesLightById;
        while (head < tail)
        {
            var c = queue[head++];
            var level = levels[c];
            if (level <= 1) continue;
            var next = (byte)(level - 1);

            var lx = c / LightSq;
            var rem = c - lx * LightSq;
            var ly = rem / LightSize;
            var lz = rem - ly * LightSize;

            if (lx > 0 && passes[ids[c - LightSq]] && levels[c - LightSq] < next)
                { levels[c - LightSq] = next; queue[tail++] = c - LightSq; }
            if (lx < LightSize - 1 && passes[ids[c + LightSq]] && levels[c + LightSq] < next)
                { levels[c + LightSq] = next; queue[tail++] = c + LightSq; }
            if (ly > 0 && passes[ids[c - LightSize]] && levels[c - LightSize] < next)
                { levels[c - LightSize] = next; queue[tail++] = c - LightSize; }
            if (ly < LightSize - 1 && passes[ids[c + LightSize]] && levels[c + LightSize] < next)
                { levels[c + LightSize] = next; queue[tail++] = c + LightSize; }
            if (lz > 0 && passes[ids[c - 1]] && levels[c - 1] < next)
                { levels[c - 1] = next; queue[tail++] = c - 1; }
            if (lz < LightSize - 1 && passes[ids[c + 1]] && levels[c + 1] < next)
                { levels[c + 1] = next; queue[tail++] = c + 1; }
        }
    }

    /// <summary>Light-volume index of a chunk-local block.</summary>
    internal static int LightIdx(int x, int y, int z) =>
        ((x + LightBorder) * LightSize + (y + LightBorder)) * LightSize + (z + LightBorder);
}
