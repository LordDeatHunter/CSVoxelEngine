using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using VoxelEngine.Effects.Lighting;
using static VoxelEngine.Chunks.Chunk;

namespace VoxelEngine.Chunks;

/// <summary>
/// GL-free voxel lighting: the wide light-volume BFS flood (sun + coloured lamp light) and the
/// per-corner light/AO queries the mesher samples. Extracted from <see cref="Chunk"/>; reads
/// Chunk's shared geometry tables and light-volume constants through <c>using static Chunk</c>.
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
        byte side1Id, byte side2Id, byte cornerId)
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
    internal static FaceLight SmoothFaceLight(int face, byte[] padded, int pIdx, byte[] sunLight,
        byte[] blockLight, int lightBase) =>
        FaceLight.FromCorners(face,
            SmoothCornerCode(face, 0, padded, pIdx, sunLight, blockLight, lightBase),
            SmoothCornerCode(face, 1, padded, pIdx, sunLight, blockLight, lightBase),
            SmoothCornerCode(face, 2, padded, pIdx, sunLight, blockLight, lightBase),
            SmoothCornerCode(face, 3, padded, pIdx, sunLight, blockLight, lightBase));

    private static int SmoothCornerCode(int face, int j, byte[] padded, int pIdx, byte[] sunLight,
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
    internal static FaceLight EmitterFaceLight(int face, byte[] padded, int pIdx, byte[] sunLight,
        byte[] blockLight, int lightBase, int fallbackCell) =>
        BlockType.Occludes(padded[pIdx + FaceNeighborDelta[face]])
            ? FaceLight.Flat(sunLight[fallbackCell], (byte)(blockLight[fallbackCell] & BlockLevelMask),
                (byte)(blockLight[fallbackCell] >> 4))
            : SmoothFaceLight(face, padded, pIdx, sunLight, blockLight, lightBase);

    /// <summary>
    /// Per-axis map from a light-volume coordinate (0..LightSize-1) to the neighbourhood chunk
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
    /// not of BFS order or where the volume starts - and neighbouring chunks agree at their seams
    /// wherever both volumes see the same seeds.
    /// <para>
    /// Because a cell can be enqueued more than once here, bit 7 of its byte marks "waiting in the
    /// queue": a cell already waiting is updated in place rather than queued again, so at most
    /// LightCount entries are ever live and the queue runs as a ring over its first LightCount
    /// slots. Every entry clears its mark when it is dequeued, so all marks are gone on return.
    /// </para>
    /// </summary>
    internal static void SpreadBlockLight(byte[] light, byte[] levels, int[] queue, ref int head, ref int tail)
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

            if (lx > 0) RaiseTinted(light, levels, queue, ref t, ref count, c - LightSq, next, slotBits);
            if (lx < LightSize - 1) RaiseTinted(light, levels, queue, ref t, ref count, c + LightSq, next, slotBits);
            if (ly > 0) RaiseTinted(light, levels, queue, ref t, ref count, c - LightSize, next, slotBits);
            if (ly < LightSize - 1) RaiseTinted(light, levels, queue, ref t, ref count, c + LightSize, next, slotBits);
            if (lz > 0) RaiseTinted(light, levels, queue, ref t, ref count, c - 1, next, slotBits);
            if (lz < LightSize - 1) RaiseTinted(light, levels, queue, ref t, ref count, c + 1, next, slotBits);
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
    private static void RaiseTinted(byte[] light, byte[] levels, int[] queue, ref int tail, ref int count,
        int n, int next, int slotBits)
    {
        if (!PassesLight(light[n])) return;
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
    internal static bool PassesLight(byte blockId) => BlockType.PassesLightById[blockId];

    /// <summary>One BFS pass: spreads <paramref name="levels"/> through light-passing cells
    /// (see <see cref="PassesLight"/>) of the light snapshot, losing one level per block.
    /// Shared by the sun and lamp passes. Every cell is still enqueued at most once, since
    /// every traversal costs exactly one level.</summary>
    internal static void SpreadLight(byte[] light, byte[] levels, int[] queue, ref int head, ref int tail)
    {
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

            if (lx > 0 && PassesLight(light[c - LightSq]) && levels[c - LightSq] < next)
                { levels[c - LightSq] = next; queue[tail++] = c - LightSq; }
            if (lx < LightSize - 1 && PassesLight(light[c + LightSq]) && levels[c + LightSq] < next)
                { levels[c + LightSq] = next; queue[tail++] = c + LightSq; }
            if (ly > 0 && PassesLight(light[c - LightSize]) && levels[c - LightSize] < next)
                { levels[c - LightSize] = next; queue[tail++] = c - LightSize; }
            if (ly < LightSize - 1 && PassesLight(light[c + LightSize]) && levels[c + LightSize] < next)
                { levels[c + LightSize] = next; queue[tail++] = c + LightSize; }
            if (lz > 0 && PassesLight(light[c - 1]) && levels[c - 1] < next)
                { levels[c - 1] = next; queue[tail++] = c - 1; }
            if (lz < LightSize - 1 && PassesLight(light[c + 1]) && levels[c + 1] < next)
                { levels[c + 1] = next; queue[tail++] = c + 1; }
        }
    }

    /// <summary>Light-volume index of a chunk-local block.</summary>
    internal static int LightIdx(int x, int y, int z) =>
        ((x + LightBorder) * LightSize + (y + LightBorder)) * LightSize + (z + LightBorder);
}
