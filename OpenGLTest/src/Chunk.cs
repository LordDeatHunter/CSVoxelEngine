using System;
using System.Buffers;
using System.Collections.Generic;
using Silk.NET.OpenGL;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace silkgltriangle;

/// <summary>
/// Represents a 16x16x16 chunk of blocks in the world.
/// </summary>
public class Chunk : IDisposable
{
    public const int Size = 16;
    public const int SizeShift = 4; // log2(Size), for shift/mask coordinate math
    public const int SizeMask = Size - 1;
    public const int BlockCount = Size * Size * Size;

    // Meshing reads a snapshot of this chunk plus a 1-block border from neighbors
    internal const int Padded = Size + 2;
    internal const int PaddedSq = Padded * Padded;
    private const int PaddedCount = Padded * PaddedSq;

    // Voxel light travels up to LightPropagationRadius blocks through air, so it is
    // computed over a wider volume centered on this chunk: a lamp just across the
    // border must still light this chunk's faces. The 3x3x3 neighbor chunks cover it
    // (LightBorder < Size). A padded-snapshot index (px,py,pz) maps to light index
    // (px+LightOffset, py+LightOffset, pz+LightOffset).
    internal const int LightBorder = WorldGenerationConfig.LightPropagationRadius;
    internal const int LightSize = Size + LightBorder * 2;
    private const int LightSq = LightSize * LightSize;
    private const int LightCount = LightSize * LightSq;
    private const int LightOffset = LightBorder - 1;

    // Tile layout lives on TextureAtlas (set once when the atlas is stitched at startup);
    // meshing only reads it, so background mesh threads need no synchronization.

    // Flat block storage; index = (x << 8) | (y << 4) | z
    private readonly byte[] _blocks = new byte[BlockCount];

    // Per-block state byte, parallel to _blocks and lazily allocated: null means "all zero",
    // which is what every non-fluid cell and every generated sea cell reads as (see
    // FluidState). Written on the main thread only; mesh threads copy the field to a local and
    // treat null as zeros, the same tolerated race as _blocks (an edit's own remesh wins after).
    private volatile byte[]? _state;

    // The light the current mesh was baked with, one byte per cell packed sun << 4 | block in
    // Idx order (see ChunkMeshData.Light). Published on the main thread by UpdateMesh, read on
    // the main thread by entity draws (EntityLight), never mutated after publish and swapped
    // by reference, so a reader sees one whole snapshot. Null = no snapshot (not meshed yet,
    // or an all-air/fully-enclosed chunk whose build skipped the light flood).
    private volatile byte[]? _light;

    // Counts non-air cells, water included: its only consumer is the all-air mesh fast path,
    // and a chunk holding nothing but sea water still has a surface to draw.
    private int _solidCount;

    /// <summary>
    /// Test seam: the non-air cell count <see cref="GenerateBlockData"/> reports. It exists so
    /// the vectorised recount there can be pinned against an independent scalar oracle walking
    /// <see cref="GetLocalBlock"/>; nothing in the engine reads it.
    /// </summary>
    internal int SolidCount => _solidCount;

    private static readonly Vector3[] NeighborOffsets = Vector3Directions.CardinalDirections;
    public double lifetime = 0.0f;
    public const float MAX_LIFETIME = 0.5f;

    public Vector3 Position { get; }

    /// <summary>World-space corners of this chunk's cell, computed once (PERF_TODO R11).
    /// <see cref="Position"/> is get-only and set only in the constructor, so these never
    /// need invalidating. Four render passes frustum-test these every frame.</summary>
    public Vector3 WorldMin { get; }

    /// <inheritdoc cref="WorldMin"/>
    public Vector3 WorldMax { get; }

    /// <summary>
    /// Where this chunk's opaque geometry lives in the render manager's opaque
    /// <see cref="ChunkGeometryPool"/>, or <c>null</c> when there is nothing to draw.
    /// The chunk passes turn these into <c>glMultiDrawElementsIndirect</c> commands.
    /// </summary>
    public GeometrySlice? OpaqueSlice { get; private set; }

    /// <summary>
    /// Where this chunk's fluid and glass geometry lives in the render manager's translucent
    /// <see cref="ChunkGeometryPool"/>, or <c>null</c> when there is nothing to draw. Fluid
    /// surfaces live apart from the opaque geometry so RenderManager can draw them blended,
    /// after everything opaque (see RenderManager.RenderWorldTranslucent).
    /// </summary>
    public GeometrySlice? TranslucentSlice { get; private set; }

    // The pools the two slices came from, so Dispose can hand the space back without an
    // unload having to thread a pool reference in.
    private ChunkGeometryPool? _opaquePool;
    private ChunkGeometryPool? _translucentPool;

    /// <summary>
    /// Whether block data has been fully generated and is ready for meshing.
    /// </summary>
    public volatile bool IsBlockDataReady;

    /// <summary>
    /// Whether this chunk has an uploaded, non-empty mesh worth drawing.
    /// </summary>
    public bool HasMesh => OpaqueSlice != null;

    /// <summary>
    /// Whether this chunk has an uploaded fluid mesh for the blended pass. An ocean chunk
    /// holding nothing but water has one of these and no opaque mesh at all.
    /// </summary>
    public bool HasTranslucentMesh => TranslucentSlice != null;

    /// <summary>
    /// Whether this chunk's translucent mesh actually holds fluid. Glass shares that mesh, so
    /// <see cref="HasTranslucentMesh"/> cannot answer this, and the water-only work — the
    /// planar reflection and the scene colour/depth copies — keys on this instead.
    /// </summary>
    public bool HasWaterMesh { get; private set; }

    /// <summary>
    /// Whether a mesh has been applied at least once, even an empty one (all air, or solid with
    /// no exposed face). Main-thread only. The LOD uses it to tell "chunk is drawn" apart from
    /// "chunk has nothing to draw", which <see cref="HasMesh"/> cannot.
    /// </summary>
    public bool IsMeshed { get; private set; }

    /// <summary>
    /// This chunk's index in <see cref="ChunkManager.Renderable"/>, or -1 while it is not in
    /// that list (no mesh at all, or unloaded). Maintained by <see cref="ChunkManager"/> on
    /// the main thread only.
    /// </summary>
    public int RenderIndex = -1;

    /// <summary>
    /// This chunk's index in <see cref="ChunkManager.TranslucentRenderable"/>, or -1 while
    /// it is not in that list (no translucent mesh, or unloaded). Same contract as
    /// <see cref="RenderIndex"/>.
    /// </summary>
    public int TranslucentRenderIndex = -1;

    /// <summary>
    /// The lowest terrain-surface height (<see cref="ColumnData.MinHeight"/>: ravines already
    /// cut in, caves not — carvers only remove blocks) anywhere in this chunk's 16x16 column.
    /// Nothing in a chunk whose top is below this can be lit by the sun, which is what the
    /// shadow pass uses it for. Recorded while the block data is generated, where the column
    /// data is already in hand, so no per-frame cache lookup is needed;
    /// <c>int.MinValue</c> on a chunk built without block data (never rendered).
    /// </summary>
    public int SurfaceMinY { get; private set; } = int.MinValue;

    /// <summary>
    /// The fluid cells inside this chunk that could already move when it loads (local block
    /// indices as <see cref="Idx"/> produces them), or <c>null</c> when there are none.
    /// Generated sea water is static source blocks, so a cave carved into the sea floor leaves
    /// water standing against worldgen air that nothing would ever schedule; this is the list
    /// <see cref="FluidManager.WakeLoadedChunks"/> uses to make it pour in the moment the chunk
    /// loads. Generated water keeps the carved rule: open air above the terrain is not a cavity
    /// (see <see cref="FluidManager.IsWakeTarget"/>), so a ravine, beach or hillside never wakes
    /// the sea. A restored chunk can hold fluid the player poured, so a non-source (mid-flow)
    /// cell always counts, and so does a source above both its column's terrain top and water
    /// level (only ever poured) wherever it has a replaceable neighbour. Written once by the
    /// generation worker before <see cref="IsBlockDataReady"/> goes true and read once on the
    /// main thread afterwards, so it needs no locking.
    /// </summary>
    internal ushort[]? FluidFrontier { get; private set; }

    /// <summary>
    /// Whether a block edit has changed this chunk since it was generated, restored or last
    /// snapshotted (<see cref="TakeSnapshot"/>). Set by the three raw writers every edit goes
    /// through — <see cref="SetBlockRaw"/> (which <see cref="PlaceBlockAt"/>,
    /// <see cref="SetStateAt"/> and the fluid tick use), <see cref="RemoveBlockAt"/> and
    /// <see cref="ClearBlockAt"/> — and never by generation, so an untouched chunk is never
    /// written to disk and simply regenerates. Main thread only, like the edits.
    /// </summary>
    public bool ModifiedSinceSave { get; private set; }

    /// <summary>
    /// Whether this chunk's blocks came from a save (<see cref="RestoreFrom"/>) instead of
    /// worldgen. A restored chunk can hold poured water anywhere, which is what makes the fluid
    /// wake scan its borders whatever height it sits at.
    /// </summary>
    public bool IsRestored { get; private set; }

    /// <summary>
    /// The player-placed cells (chunk-local <see cref="Idx"/> indices) a restored chunk brought
    /// back that the chunk manager has not yet adopted into its own player-placed set, or null.
    /// Set by <see cref="RestoreFrom"/> on the generation worker, cleared on the main thread
    /// once adopted.
    /// </summary>
    internal ushort[]? RestoredPlayerPlaced { get; set; }

    // Column data only depends on (x, z), so vertical chunk stacks share one entry. Sizing (capacity,
    // generations and the memory arithmetic behind both) lives in WorldGenerationConfig; the safety
    // argument - why an eviction, a race or a duplicate can only ever cost a recompute - is the
    // numbered invariant list on GenerationalCache. Neither is restated here.
    //
    // What the old wholesale Clear()-at-the-cap actually cost (item D2): chunk generation's own
    // working set is small - ChunkRadius = 16 is a disc of roughly 800 distinct columns, re-asked
    // constantly by the vertical stack - while ONE level-2 LOD ring sweeps roughly 51k columns
    // through LodRegion.BuildMeshData's 324-column prefetch. So the sweep tripped the 16384 cap
    // about three times per pass, and every trip threw away chunk-gen's permanently-hot columns
    // along with the sweep's one-shot ones. With four generations the sweep can only age out the
    // oldest quarter at a time, and anything read while it sits in that oldest quarter is carried
    // forward instead of dropped.
    //
    // Second, separate cost removed: the cap test was `ColumnCache.Count >= limit`, and
    // ConcurrentDictionary.Count acquires EVERY lock in the table. It was evaluated on every single
    // miss - i.e. on the exact path the four chunk-gen threads and the LOD builder share.
    // GenerationalCache counts its own inserts with Interlocked instead.
    private static readonly GenerationalCache<(int X, int Z), ColumnData> ColumnCache =
        new(WorldGenerationConfig.ColumnCacheCapacity, WorldGenerationConfig.ColumnCacheGenerations);

    // Static, so a miss on any of the four chunk-gen threads does not allocate a closure per call
    private static readonly Func<(int X, int Z), ColumnData> ComputeColumn =
        static key => TerrainGenerator.Compute(key.X, key.Z);

    /// <summary>
    /// Drops every cached column. Only <see cref="WorldSeed.Apply"/> calls it: the columns are a
    /// function of the seed, so a swap makes all of them wrong.
    /// </summary>
    internal static void ResetColumnCache() => ColumnCache.Clear();

    // Meshing lookup tables, flattened from CubeFaces/AONeighbors into padded-snapshot index deltas
    private static readonly int[] FaceNeighborDelta = new int[6];
    // Same face normals as light-volume index deltas, for the air cell in front of a face
    private static readonly int[] LightFaceDelta = new int[6];
    private static readonly int[][] AODeltas = new int[6][]; // 12 per face: (side1, side2, corner) x 4 corners
    // The same AO side/side/corner cells as light-volume index deltas (LightSize stride, not the
    // padded one), read by SmoothCornerLight
    private static readonly int[][] LightAODeltas = new int[6][];
    private const float SmoothLightDivisor = 60f; // 4 samples x level 15
    internal static readonly float[] FaceBrightness = [0.85f, 0.95f, 0.8f, 0.9f, 1.0f, 0.75f]; // matches CubeFaces order
    internal static readonly byte[] ShadeLut = new byte[6 * 4]; // [face * 4 + ao]

    static Chunk()
    {
        for (var i = 0; i < 6; i++)
        {
            FaceNeighborDelta[i] = PaddedDelta(CubeFaces[i].normal);
            LightFaceDelta[i] = LightDelta(CubeFaces[i].normal);

            var deltas = new int[12];
            var lightDeltas = new int[12];
            for (var j = 0; j < 4; j++)
            for (var k = 0; k < 3; k++)
            {
                deltas[j * 3 + k] = PaddedDelta(AONeighbors[i][j][k]);
                lightDeltas[j * 3 + k] = LightDelta(AONeighbors[i][j][k]);
            }
            AODeltas[i] = deltas;
            LightAODeltas[i] = lightDeltas;

            for (var ao = 0; ao <= 3; ao++)
                ShadeLut[(i << 2) | ao] = (byte)(FaceBrightness[i] * ao * MathUtils.ONE_THIRD * 255);
        }
    }

    private static int PaddedDelta(Vector3 offset) =>
        (int)offset.X * PaddedSq + (int)offset.Y * Padded + (int)offset.Z;

    private static int LightDelta(Vector3 o) => (int)o.X * LightSq + (int)o.Y * LightSize + (int)o.Z;

    private static int FloorDiv(int a, int b) => MathUtils.FloorDiv(a, b);

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
    /// substituted sample counts with the front cell's colour slot as well as its level.
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
    /// when the neighbour in front of it is open, otherwise the flat light of
    /// <paramref name="fallbackCell"/> - a face backed against an occluder must not average the
    /// occluder's zero light, or it renders black.
    /// </summary>
    private static FaceLight EmitterFaceLight(int face, byte[] padded, int pIdx, byte[] sunLight,
        byte[] blockLight, int lightBase, int fallbackCell) =>
        BlockType.Occludes(padded[pIdx + FaceNeighborDelta[face]])
            ? FaceLight.Flat(sunLight[fallbackCell], (byte)(blockLight[fallbackCell] & BlockLevelMask),
                (byte)(blockLight[fallbackCell] >> 4))
            : SmoothFaceLight(face, padded, pIdx, sunLight, blockLight, lightBase);

    /// <summary>
    /// Per-axis map from a light-volume coordinate (0..LightSize-1) to the neighbourhood chunk
    /// index (0..2) and the block index inside it (0..Size-1). The volume spans LightBorder cells
    /// into each neighbour: [1, LightBorder) of the previous chunk, all of this one, then
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
    /// The nine chunk-column heightmaps the light volume centred on chunk (chunkX, chunkZ) spans,
    /// written to columns[dx * 3 + dz] for chunk column (chunkX - 1 + dx, chunkZ - 1 + dz). Because
    /// LightBorder (15) is smaller than Size (16) the volume never reaches past those nine, so
    /// fetching them once replaces LightSize^2 cache probes (PERF_TODO C2) while resolving to the
    /// exact same ColumnData instances.
    /// </summary>
    internal static void FillLightColumnHeights(int chunkX, int chunkZ, int[]?[] columns)
    {
        for (var dx = 0; dx < 3; dx++)
        for (var dz = 0; dz < 3; dz++)
            columns[dx * 3 + dz] = GetColumnHeights(chunkX - 1 + dx, chunkZ - 1 + dz);
    }

    /// <summary>
    /// Copies the LightSize^3 block volume centered on this chunk straight from the 3x3x3
    /// neighboring chunks' block arrays, so the fill loop does zero dictionary lookups.
    /// <paramref name="neighbors"/> comes pre-resolved from <see cref="TryResolveNeighbors"/>,
    /// which guarantees the 6 face neighbors are present; the corner and edge slots may still
    /// be null and read as air, which only costs a little light accuracy in the diagonal
    /// corners of the wide light volume. Same thread-safety convention as FillPaddedSnapshot
    /// (block edits race the read; the edit's own remesh wins after).
    /// </summary>
    private void FillLightSnapshot(byte[] light, Chunk?[] neighbors)
    {
        // Per-axis map from volume coordinate to (chunk 0..2, local block 0..15). The volume
        // spans LightBorder cells into each neighbor: [1, LightBorder) of the previous
        // chunk, the full own chunk, then [0, LightBorder) of the next one.
        Span<int> mapChunk = stackalloc int[LightSize];
        Span<int> mapLocal = stackalloc int[LightSize];
        BuildLightAxisMap(mapChunk, mapLocal);

        for (var lx = 0; lx < LightSize; lx++)
        for (var ly = 0; ly < LightSize; ly++)
        {
            var rowBase = (lx * LightSize + ly) * LightSize;
            var neighborRow = (mapChunk[lx] * 3 + mapChunk[ly]) * 3;
            var blockRow = (mapLocal[lx] << (2 * SizeShift)) | (mapLocal[ly] << SizeShift);
            for (var lz = 0; lz < LightSize; lz++)
            {
                var neighbor = neighbors[neighborRow + mapChunk[lz]];
                light[rowBase + lz] = neighbor?._blocks[blockRow | mapLocal[lz]] ?? 0;
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
    /// <paramref name="sun"/> and <paramref name="block"/> must be zeroed and hold at
    /// least LightCount entries each, as must <paramref name="queue"/>.
    /// <para>
    /// <paramref name="block"/> stores <c>level | slot &lt;&lt; 4</c>: the lamp level in bits 0-3
    /// (<see cref="BlockLevelMask"/>) and the <see cref="BlockLightPalette"/> slot of the light's
    /// colour in bits 4-6, so every read of a level must mask. <paramref name="sun"/> is plain
    /// levels. Returns whether any lamp seed carries a non-default slot; when none does, every
    /// slot is 0 and the byte is exactly the old level.
    /// </para>
    /// </summary>
    private bool ComputeLight(byte[] light, byte[] sun, byte[] block, int[] queue)
    {
        var worldY0 = LowerBoundY - LightBorder;

        var head = 0;
        var tail = 0;

        // The LightSize^2 volume columns only ever land in the 3x3 chunk columns around this one
        // (LightBorder < Size), so their heightmaps are fetched once here instead of probing the
        // ColumnData cache per column (PERF_TODO C2). Same nine ColumnData instances, same heights -
        // only the number of dictionary probes changes.
        Span<int> mapChunk = stackalloc int[LightSize];
        Span<int> mapLocal = stackalloc int[LightSize];
        BuildLightAxisMap(mapChunk, mapLocal);

        var scratch = MeshScratch.Current;
        var columns = scratch.LightColumnHeights;
        FillLightColumnHeights((int)Position.X, (int)Position.Z, columns);

        // Sunlight seeds: air cells at or above the terrain height see the sky directly.
        // Leaves seed too: canopies pass light, so the sky reaches straight through them
        // (branch tops under leaves stay lit; only depth falloff dims deep cover). Tree
        // wood seeds as well (see PassesLight): a thin limb never casts a full shadow.
        for (var lx = 0; lx < LightSize; lx++)
        {
            var columnRow = mapChunk[lx] * 3;
            var heightRow = mapLocal[lx] << SizeShift;
            for (var lz = 0; lz < LightSize; lz++)
            {
                var height = columns[columnRow + mapChunk[lz]]![heightRow | mapLocal[lz]];
                var columnBase = (lx * LightSize) * LightSize + lz;
                for (var ly = 0; ly < LightSize; ly++)
                {
                    var i = columnBase + ly * LightSize;
                    if (!PassesLight(light[i])) continue;
                    if (worldY0 + ly >= height)
                    {
                        sun[i] = 15;
                        queue[tail++] = i;
                    }
                }
            }
        }

        scratch.ClearLightColumns();

        SpreadLight(light, sun, queue, ref head, ref tail);

        // Lamp seeds: emissive cells glow at full strength, then spread like sunlight, each
        // carrying its colour slot in bits 4-6.
        head = 0;
        tail = 0;
        var anyTinted = false;
        var emissionById = BlockType.Emission;
        var slotById = BlockType.LightSlot;
        for (var i = 0; i < LightCount; i++)
        {
            var id = light[i];
            var emission = emissionById[id];
            if (emission == 0) continue;
            var slot = slotById[id];
            block[i] = (byte)(emission | slot << 4);
            anyTinted |= slot != 0;
            queue[tail++] = i;
        }

        // All-default light keeps the plain pass: the values (slot bits all 0) and the cost are
        // exactly what they were before coloured light existed
        if (anyTinted)
            SpreadBlockLight(light, block, queue, ref head, ref tail);
        else
            SpreadLight(light, block, queue, ref head, ref tail);
        return anyTinted;
    }

    /// <summary>Lamp level bits of a <c>blockLight</c> byte (see <see cref="ComputeLight"/>).</summary>
    private const int BlockLevelMask = 15;

    // Colour slot bits of a blockLight byte
    private const int BlockSlotMask = 0x70;

    /// <summary>
    /// <see cref="SpreadLight"/> for lamp light that carries a colour slot (<c>level | slot
    /// &lt;&lt; 4</c>). The dominant-colour rule: a cell takes the slot of whichever source reaches
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
    private static void SpreadBlockLight(byte[] light, byte[] levels, int[] queue, ref int head, ref int tail)
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

    /// <summary>One neighbour step of <see cref="SpreadBlockLight"/>: a dimmer neighbour, or an
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
    private static bool PassesLight(byte blockId) => BlockType.PassesLightById[blockId];

    /// <summary>One BFS pass: spreads <paramref name="levels"/> through light-passing cells
    /// (see <see cref="PassesLight"/>) of the light snapshot, losing one level per block.
    /// Shared by the sun and lamp passes. Every cell is still enqueued at most once, since
    /// every traversal costs exactly one level.</summary>
    private static void SpreadLight(byte[] light, byte[] levels, int[] queue, ref int head, ref int tail)
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
    private static int LightIdx(int x, int y, int z) =>
        ((x + LightBorder) * LightSize + (y + LightBorder)) * LightSize + (z + LightBorder);

    /// <summary>Flat index of a chunk-local block; shared with the WorldGen carvers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Idx(int x, int y, int z) => (x << (2 * SizeShift)) | (y << SizeShift) | z;

    /// <summary>The inverse of <see cref="Idx"/>: the cell-local position a local block index names.</summary>
    internal static Vector3 LocalOfIdx(int idx) =>
        new(idx >> (2 * SizeShift), (idx >> SizeShift) & (Size - 1), idx & (Size - 1));

    /// <summary>
    /// Whether a fluid cell is a hot fluid open to the air above it — an ember and smoke spawn
    /// point. "Hot" is keyed off the block's <c>light.emission</c>, never its name, so any
    /// glowing fluid qualifies; the cell above must be neither fluid, solid nor occluding.
    /// </summary>
    internal static bool IsHotFluidVent(byte id, byte above) =>
        BlockType.IsFluid(id) && BlockType.Emission[id] > 0
        && !BlockType.IsFluid(above) && !BlockType.IsSolid(above) && !BlockType.Occludes(above);

    // gl is retained for signature stability only: every call site (and the headless tests)
    // still constructs a chunk with the context, but a chunk owns no GL object any more —
    // its geometry lives in the render manager's ChunkGeometryPools.
    public Chunk(GL gl, Vector3 position, bool generateBlockData = true)
    {
        _ = gl;
        Position = position;
        WorldMin = position * Size;
        WorldMax = WorldMin + new Vector3(Size);

        if (generateBlockData)
        {
            GenerateBlockData();
        }
    }

    /// <summary>
    /// Fills the terrain columns (grass / dirt / stone by depth), lets the cave carvers clear
    /// their air out of the result, floods everything below sea level, then stamps trees onto
    /// the surface. Runs on a generation worker thread.
    /// </summary>
    public void GenerateBlockData()
    {
        // Worldgen timing for the X/VOXEL_DEBUG console line (WorldgenStats, the `wgen` token)
        var genStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var column = GetColumnData((int)Position.X, (int)Position.Z);
        var lowerBoundY = LowerBoundY;
        SurfaceMinY = column.MinHeight;

        // Entirely above the terrain: nothing to fill, nothing to carve, nothing to stamp.
        // With trees enabled the band above the surface still needs generation (a tree reaches
        // TreeGenerator.ReachUp above its base), and overhanging limbs near cliffs can cross
        // from higher neighbouring columns, so the test uses the tallest column in reach.
        var maxHeight = column.MaxHeight;
        if (WorldGenerationConfig.TreesEnabled)
        {
            var cx = (int)Position.X;
            var cz = (int)Position.Z;
            var reach = TreeGenerator.ReachChunkRadius;
            for (var dx = -reach; dx <= reach; dx++)
            for (var dz = -reach; dz <= reach; dz++)
            {
                var neighbourMax = GetColumnData(cx + dx, cz + dz).MaxHeight;
                if (neighbourMax > maxHeight) maxHeight = neighbourMax;
            }
            maxHeight += TreeGenerator.ReachUp + 1;
        }

        // A chunk above the terrain still has water to fill when a body's surface reaches into it:
        // the sea reaches sea level, a lake its own level. A column that holds no water reports
        // NoWater, which no y is at or below.
        var needsWater = WorldGenerationConfig.WaterEnabled
                         && lowerBoundY <= column.MaxWaterLevel;
        if (lowerBoundY > maxHeight + 1 && !needsWater)
        {
            IsBlockDataReady = true;
            WorldgenStats.AddChunk(System.Diagnostics.Stopwatch.GetTimestamp() - genStart);
            return;
        }

        for (var x = 0; x < Size; x++)
        {
            for (var z = 0; z < Size; z++)
            {
                var i = (x << SizeShift) | z;
                GenerateColumn(x, z, column.Heights[i], column.Bed[i], column.DirtDepth[i],
                    lowerBoundY, column.Biomes[i]);
            }
        }

        var worldX0 = (int)Position.X * Size;
        var worldZ0 = (int)Position.Z * Size;

        // Both carvers only ever clear blocks at or below their own column's terrain top:
        // NoiseCaves breaks each column at `depth < 0` before it reads a block or a sample, and
        // WormCarver skips every y at `depth < margin`, margin = WormSurfaceMargin * (1 - EntranceWeight)
        // in [0, 6] (EntranceWeight is a clamped SmoothStep). Both read this chunk's own
        // column.Heights, so a chunk whose tallest column ends below its floor is untouchable by
        // either and skipping them is bit-identical. It saves the 1000-call cave lattice
        // (NoiseCaves.SampleLattice) on every sea chunk the needsWater clause above keeps alive
        // over its own sea floor.
        var carvable = column.MaxHeight >= lowerBoundY;

        if (carvable && WorldGenerationConfig.NoiseCavesEnabled)
            NoiseCaves.Carve(_blocks, column, worldX0, lowerBoundY, worldZ0);

        if (carvable && WorldGenerationConfig.WormCavesEnabled)
            WormCarver.Carve(_blocks, column, worldX0, lowerBoundY, worldZ0);

        // After the carvers, so veins rim cave walls instead of being eaten by them.
        if (WorldGenerationConfig.MalachiteEnabled)
            MalachiteGenerator.Stamp(_blocks, worldX0, lowerBoundY, worldZ0);

        // Same pass, same reason; coal's two host rocks let it also fill desert sandstone.
        if (WorldGenerationConfig.CoalEnabled)
            CoalGenerator.Stamp(_blocks, worldX0, lowerBoundY, worldZ0);

        // After the carvers (so caves under the sea floor flood) and before the trees and
        // pebbles, which skip submerged columns rather than growing underwater
        var hasWater = false;
        if (WorldGenerationConfig.WaterEnabled)
        {
            hasWater = FillWater(column, lowerBoundY, worldX0, worldZ0);
            StampMud(column, worldX0, lowerBoundY, worldZ0);
        }

        // After the sea: flooded cells are water, not air, so only dry cave floors pond lava
        if (WorldGenerationConfig.LavaEnabled)
            LavaGenerator.Stamp(_blocks, column, worldX0, lowerBoundY, worldZ0);

        // Every species (cacti included) stamps here, wood and leaves with their state bytes.
        // Through a local: a ref to the volatile field is refused, and nothing else can see the
        // chunk until IsBlockDataReady flips below.
        var treeStart = System.Diagnostics.Stopwatch.GetTimestamp();
        if (WorldGenerationConfig.TreesEnabled)
        {
            var state = _state;
            TreeGenerator.Stamp(_blocks, ref state, worldX0, lowerBoundY, worldZ0);
            _state = state;
        }
        WorldgenStats.AddTreeTicks(System.Diagnostics.Stopwatch.GetTimestamp() - treeStart);

        // Frozen-only ice spikes go down before the snow, so a spike keeps its base cell
        if (WorldGenerationConfig.SnowAndIceEnabled)
            IceSpikeGenerator.Stamp(_blocks, worldX0, lowerBoundY, worldZ0);

        // Last of the cold features: freezes cold surface water to ice, then lays snow on the dry
        // ground and the ice sheet that is left
        if (WorldGenerationConfig.SnowAndIceEnabled)
            SnowGenerator.Stamp(_blocks, column, worldX0, lowerBoundY, worldZ0);

        if (WorldGenerationConfig.PebblesEnabled)
            PebbleGenerator.Stamp(_blocks, column, worldX0, lowerBoundY, worldZ0);

        if (WorldGenerationConfig.FlowersEnabled)
            FlowerGenerator.Stamp(_blocks, column, worldX0, lowerBoundY, worldZ0);

        if (WorldGenerationConfig.FoliageEnabled)
            FoliageGenerator.Stamp(_blocks, column, worldX0, lowerBoundY, worldZ0);

        // Carving removed an unknown number of blocks; a recount is cheaper than bookkeeping.
        // Counting AIR and subtracting beats counting non-air: BlockType.Air is
        // `public const byte Air = 0`, so this binds to the vectorised
        // MemoryExtensions.Count<byte> with no cast, where a "!= Air" predicate would
        // have to run one comparison per cell.
        _solidCount = BlockCount - _blocks.AsSpan().Count(BlockType.Air);

        // Only a chunk that actually got flooded can have a frontier to report, plus any
        // chunk low enough to hold lava pockets (the scan itself is fluid-generic)
        if (hasWater || (WorldGenerationConfig.LavaEnabled && lowerBoundY <= WorldGenerationConfig.LavaMaxY))
            ScanFluidFrontier(column, lowerBoundY);

        IsBlockDataReady = true;
        WorldgenStats.AddChunk(System.Diagnostics.Stopwatch.GetTimestamp() - genStart);
    }

    /// <summary>
    /// Collects the fluid cells that could already move when this chunk loads into
    /// <see cref="FluidFrontier"/>. The sea is generated as static sources, so without this a
    /// cave or an entrance that breached it would leave a wall of water standing beside worldgen
    /// air forever — nothing schedules those cells, since only block edits do.
    ///
    /// Generated water keeps the carved rule: only neighbours at or below their column's natural
    /// terrain top count (see <see cref="FluidManager.IsWakeTarget"/>), since that is what a
    /// carver opened, and open air above the terrain is deliberately excluded so a generated sea
    /// never wakes across a ravine, a beach or a hillside. Minecraft's generated water is static
    /// the same way. A restored chunk can also hold fluid the player poured: a non-source
    /// (mid-flow) cell is always reported, and a source above both its own column's terrain top
    /// and water level — which generated water and lava never are — wakes against any
    /// replaceable neighbour. Generated fluid is always a source at or below one of those two,
    /// so a generated chunk's frontier is exactly the carved rule's.
    ///
    /// +Y is deliberately not tested: every sea-surface cell has air above it and water never
    /// flows upward, so including it would report a useless 256 cells per surface chunk.
    /// Neighbours across a chunk border are left to the main-thread pass, which can wait until
    /// both chunks are loaded, so this only ever reads this chunk's own blocks and stays as
    /// thread-safe as the rest of worldgen.
    /// </summary>
    private void ScanFluidFrontier(ColumnData column, int lowerBoundY)
    {
        List<ushort>? frontier = null;

        for (var x = 0; x < Size; x++)
        for (var y = 0; y < Size; y++)
        for (var z = 0; z < Size; z++)
        {
            var i = Idx(x, y, z);
            if (!BlockType.IsFluid(_blocks[i])) continue;

            var fluidY = lowerBoundY + y;
            var column2D = (x << SizeShift) | z;
            var fluidHeight = column.Heights[column2D];
            var fluidWaterLevel = column.WaterLevel[column2D];

            var exposed = !FluidState.IsSource(GetLocalState(x, y, z))
                          || (x > 0 && IsWakeTarget(column, lowerBoundY, x - 1, y, z, fluidY, fluidHeight, fluidWaterLevel))
                          || (x < SizeMask && IsWakeTarget(column, lowerBoundY, x + 1, y, z, fluidY, fluidHeight, fluidWaterLevel))
                          || (z > 0 && IsWakeTarget(column, lowerBoundY, x, y, z - 1, fluidY, fluidHeight, fluidWaterLevel))
                          || (z < SizeMask && IsWakeTarget(column, lowerBoundY, x, y, z + 1, fluidY, fluidHeight, fluidWaterLevel))
                          || (y > 0 && IsWakeTarget(column, lowerBoundY, x, y - 1, z, fluidY, fluidHeight, fluidWaterLevel));
            if (!exposed) continue;

            frontier ??= [];
            frontier.Add((ushort)i);
        }

        FluidFrontier = frontier?.ToArray();
    }

    /// <summary>Whether a chunk-local neighbour is somewhere the fluid beside it may wake into
    /// (<see cref="FluidManager.IsWakeTarget"/> with this chunk's column data).</summary>
    private bool IsWakeTarget(ColumnData column, int lowerBoundY, int x, int y, int z,
        int fluidY, int fluidColumnHeight, int fluidColumnWaterLevel) =>
        FluidManager.IsWakeTarget(_blocks[Idx(x, y, z)], lowerBoundY + y,
            column.Heights[(x << SizeShift) | z], fluidY, fluidColumnHeight, fluidColumnWaterLevel);

    /// <summary>
    /// Floods every air cell up to each column's own water level (see
    /// <see cref="ColumnData.WaterLevel"/>) in a wet column — an ocean floor, or a lake or river bed
    /// — so oceans, lakes, rivers and sea-floor caves all fill while the dry land basins stay dry.
    /// A lake's level is its own surface, so the pond rests in the ground it was dug into instead of
    /// reaching sea level. Wetness reads the column's <see cref="ColumnData.Bed"/>: a ravine that
    /// merely cut a dry column below its level holds no water, while a ravine under the ocean just
    /// deepens it (the cut is faded out across every other water body, see
    /// <c>TerrainGenerator.RavineCutFade</c>). Generated water is always a source (state 0), which
    /// is what an untouched
    /// state byte reads as, so nothing is written to <c>_state</c> here. A pure per-column function
    /// of world position like the rest of worldgen: neighbours agree at their borders with no shared
    /// state. Returns whether any cell was flooded, which is what decides if this chunk can have a
    /// fluid frontier to report.
    /// </summary>
    private bool FillWater(ColumnData column, int lowerBoundY, int worldX0, int worldZ0)
    {
        var filled = false;

        for (var x = 0; x < Size; x++)
        for (var z = 0; z < Size; z++)
        {
            var local = (x << SizeShift) | z;
            var level = column.WaterLevel[local];
            if (column.Bed[local] >= level) continue;

            var top = Math.Min(level - lowerBoundY, Size - 1);
            if (top < 0) continue;

            for (var y = 0; y <= top; y++)
            {
                var i = Idx(x, y, z);
                if (_blocks[i] != BlockType.Air) continue;
                _blocks[i] = TerrainGenerator.SeaBlock;
                filled = true;
            }
        }

        return filled;
    }

    /// <summary>
    /// Rots dirt touching the fresh sea to mud on the spot, so worldgen ground spawns muddy
    /// instead of waiting for the fluid tick's watch to convert it. In-chunk neighbours read
    /// straight off this chunk's blocks; across a chunk border the sea is a pure function of
    /// the neighbour column's height, so that is tested analytically. A cave flood breaching
    /// across a border is left to the fluid tick, the same split <see cref="ScanFluidFrontier"/>
    /// uses: worldgen stays a pure per-chunk function with no cross-chunk block reads.
    /// </summary>
    private void StampMud(ColumnData column, int worldX0, int lowerBoundY, int worldZ0)
    {
        // A chunk starting above every level this column can hold water at can neither hold any
        // nor touch any within one cell
        if (lowerBoundY > Math.Max(column.MaxWaterLevel, WorldGenerationConfig.SeaLevel) + 1) return;

        var dirt = TerrainGenerator.SoilBlock;
        var water = TerrainGenerator.SeaBlock;
        var mud = TerrainGenerator.MudBlock;

        for (var x = 0; x < Size; x++)
        for (var y = 0; y < Size; y++)
        for (var z = 0; z < Size; z++)
        {
            if (_blocks[Idx(x, y, z)] != dirt) continue;
            if (TouchesSeaWater(x, y, z, worldX0, lowerBoundY, worldZ0, water))
                _blocks[Idx(x, y, z)] = mud;
        }
    }

    /// <summary>
    /// Whether any of the 26 cells around a chunk-local cell holds water: a direct block read
    /// inside this chunk, or the same <see cref="TerrainGenerator.IsWaterColumn"/> test
    /// <see cref="FillWater"/> is built on outside it. (It was a plain height test before lakes
    /// and rivers, when any below-waterline column held water; that is no longer true.)
    /// </summary>
    private bool TouchesSeaWater(int x, int y, int z, int worldX0, int lowerBoundY, int worldZ0, byte water)
    {
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        {
            if (dx == 0 && dy == 0 && dz == 0) continue;

            var nx = x + dx;
            var ny = y + dy;
            var nz = z + dz;
            if (nx >= 0 && ny >= 0 && nz >= 0 && nx < Size && ny < Size && nz < Size)
            {
                if (_blocks[Idx(nx, ny, nz)] == water) return true;
                continue;
            }

            var wx = worldX0 + x + dx;
            var wy = lowerBoundY + y + dy;
            var wz = worldZ0 + z + dz;
            var neighbour = GetColumnData(wx >> SizeShift, wz >> SizeShift);
            var local = ((wx & SizeMask) << SizeShift) | (wz & SizeMask);
            if (wy > neighbour.Heights[local] && wy <= neighbour.WaterLevel[local])
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the 2D generation inputs for a chunk column. Nothing generated is ever solid
    /// above Heights[x, z]; the block at that height is solid unless a cave or entrance carved
    /// it. Cached, deterministic, and safe to call from any thread.
    /// </summary>
    public static ColumnData GetColumnData(int chunkX, int chunkZ) =>
        ColumnCache.GetOrAdd((chunkX, chunkZ), ComputeColumn);

    /// <summary>
    /// The terrain heightmap for a chunk column, indexed by (x &lt;&lt; SizeShift) | z. Ravines
    /// are already cut in; caves are not (they are 3D). See <see cref="GetColumnData"/>.
    /// </summary>
    public static int[] GetColumnHeights(int chunkX, int chunkZ) => GetColumnData(chunkX, chunkZ).Heights;

    /// <summary>Per-column biomes for a chunk column, indexed like
    /// <see cref="GetColumnHeights"/>. The far LOD reads this to pick biome surface tiles.</summary>
    public static Biome[] GetColumnBiomes(int chunkX, int chunkZ) => GetColumnData(chunkX, chunkZ).Biomes;

    // Solid from the chunk floor up to the terrain height: grass on top, dirt for dirtDepth
    // blocks under it, stone below that. dirtDepth == 0 marks a bare stone column
    // (see ColumnData.DirtDepth) - deep ravine walls, where the top block is stone as well.
    // A wet column tops out in dirt instead: grass does not grow on a sea, lake or river bed.
    // Shoreline columns (see TerrainGenerator.IsBeachHeight) build from sand instead, capped
    // with gravel where TerrainGenerator.IsGravelPatch holds. `terrainHeight` (ravines cut in)
    // decides how deep to fill; `bed` (ravines not cut) decides whether the column is water.
    private void GenerateColumn(int x, int z, int terrainHeight, int bed, int dirtDepth, int lowerBoundY,
        Biome biome)
    {
        var worldX = (int)Position.X * Size + x;
        var worldZ = (int)Position.Z * Size + z;

        var topBlock = TerrainGenerator.IsWaterColumn(worldX, worldZ, bed)
            ? TerrainGenerator.SoilBlock
            : TerrainGenerator.SurfaceBlock;
        var soilBlock = TerrainGenerator.SoilBlock;
        var soilDepth = dirtDepth;

        switch (biome)
        {
            case Biome.Desert:
                // Dune sand over sandstone. A ravine that stripped the soil (dirtDepth 0)
                // still reads as bare rock, the same rule the forest column uses.
                topBlock = TerrainGenerator.SandBlock;
                soilBlock = TerrainGenerator.SandstoneBlock;
                soilDepth = dirtDepth == 0 ? 0 : WorldGenerationConfig.SandstoneDepth;
                break;

            case Biome.Ocean:
                // Depth below the waterline picks the floor: shelf sand, slope gravel, deep
                // basin stone. Each material fills its own band, so there is no soil gradient.
                (topBlock, soilBlock) =
                    terrainHeight >= WorldGenerationConfig.SeaLevel - WorldGenerationConfig.OceanSandDepth
                        ? (TerrainGenerator.SandBlock, TerrainGenerator.SandBlock)
                        : terrainHeight >= WorldGenerationConfig.SeaLevel - WorldGenerationConfig.OceanGravelDepth
                            ? (TerrainGenerator.GravelBlock, TerrainGenerator.GravelBlock)
                            : (TerrainGenerator.RockBlock, TerrainGenerator.RockBlock);
                soilDepth = 2;
                break;

            case Biome.Frozen:
                // Barren rock rather than grass: the snow pass lays a drift on top, so the plains
                // read as rock-and-snow instead of a white field over soil.
                topBlock = TerrainGenerator.RockBlock;
                soilBlock = TerrainGenerator.RockBlock;
                soilDepth = 2;
                break;

            default:
                // Sand and gravel ring actual water only: the height band plus a lake or river rim.
                // Without the weight test a dry ravine floor in the band would wear sand in the
                // middle of the forest, which is what made random patches before.
                if (dirtDepth != 0 && TerrainGenerator.IsBeachHeight(terrainHeight)
                    && TerrainGenerator.SurfaceWaterWeight(worldX, worldZ) > 0f)
                {
                    topBlock = TerrainGenerator.IsGravelPatch(worldX, worldZ)
                        ? TerrainGenerator.GravelBlock
                        : TerrainGenerator.SandBlock;
                    soilBlock = TerrainGenerator.SandBlock;
                }
                break;
        }

        for (var y = 0; y < Size; y++)
        {
            var depth = terrainHeight - (lowerBoundY + y);
            if (depth < 0) break;

            _blocks[Idx(x, y, z)] = soilDepth == 0 ? TerrainGenerator.RockBlock
                : depth == 0 ? topBlock
                : depth <= soilDepth ? soilBlock
                : TerrainGenerator.RockBlock;
        }
    }

    public int LowerBoundY => (int)Position.Y * Size;
    public int UpperBoundY => (int)Position.Y * Size + Size;

    /// <summary>
    /// Reads a block using chunk-local coordinates. Coordinates must be in [0, Size).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetLocalBlock(int x, int y, int z) => _blocks[Idx(x, y, z)];

    /// <summary>
    /// Reads a cell's state byte using chunk-local coordinates; 0 when no state array exists
    /// yet. Coordinates must be in [0, Size). See <see cref="FluidState"/> for the encoding.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetLocalState(int x, int y, int z) => _state?[Idx(x, y, z)] ?? 0;

    /// <summary>Hands the light snapshot of the mesh just applied to entity queries (main thread).</summary>
    internal void PublishLight(byte[]? packed) => _light = packed;

    /// <summary>
    /// Sun and block light (0..15) of a chunk-local cell from the snapshot the current mesh
    /// was baked with. False, with both levels 0, when no snapshot has been published.
    /// </summary>
    public bool TryGetLocalLight(int x, int y, int z, out byte sun, out byte block) =>
        TryGetLocalLight(x, y, z, out sun, out block, out _);

    /// <summary>
    /// <see cref="TryGetLocalLight(int, int, int, out byte, out byte)"/> plus the block light's
    /// colour slot (<see cref="BlockLightPalette"/>). Only a chunk that had a tinted seed publishes
    /// the slot half (the snapshot is then twice as long), so every other chunk answers slot 0.
    /// </summary>
    public bool TryGetLocalLight(int x, int y, int z, out byte sun, out byte block, out byte slot)
    {
        // One read of the reference, so both halves come from the same published snapshot
        var light = _light;
        if (light == null)
        {
            sun = 0;
            block = 0;
            slot = 0;
            return false;
        }
        var i = Idx(x, y, z);
        var packed = light[i];
        sun = (byte)(packed >> 4);
        block = (byte)(packed & 0xF);
        slot = light.Length > BlockCount ? light[BlockCount + i] : (byte)0;
        return true;
    }

    /// <summary>
    /// Writes a block and its state with no remeshing, no player-placed bookkeeping and no
    /// scheduling, returning the previous id. The fluid simulation edits hundreds of cells per
    /// tick and batches the remeshes itself (<see cref="ChunkManager.MarkDirty"/>), so the
    /// per-edit work of <see cref="PlaceBlockAt"/> would hitch. Main thread only, like every
    /// other block edit. Allocates the state array on the first non-zero state.
    /// </summary>
    public byte SetBlockRaw(int x, int y, int z, byte id, byte state)
    {
        var i = Idx(x, y, z);
        var previous = _blocks[i];

        // _solidCount tracks non-air cells, so only an air <-> non-air transition moves it
        if (previous == 0 && id != 0) _solidCount++;
        else if (previous != 0 && id == 0) _solidCount--;

        _blocks[i] = id;

        if (state != 0)
        {
            var states = _state;
            if (states == null)
            {
                states = new byte[BlockCount];
                _state = states;
            }
            states[i] = state;
        }
        else if (_state != null)
        {
            _state[i] = 0;
        }

        MarkModified();
        return previous;
    }

    /// <summary>Flags the chunk as holding unsaved edits (see <see cref="ModifiedSinceSave"/>).
    /// The test keeps the hot fluid-tick path from rewriting a field it already set.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkModified()
    {
        if (!ModifiedSinceSave) ModifiedSinceSave = true;
    }

    /// <summary>
    /// Copies this chunk's blocks and state into a <see cref="ChunkSnapshot"/> for the save
    /// writer and clears <see cref="ModifiedSinceSave"/>. Copies only — no palette, no Deflate:
    /// the encoding happens on the writer thread. Main thread only, since that is the only
    /// thread that edits blocks, so the copy is consistent.
    /// </summary>
    /// <param name="playerPlaced">The chunk's player-placed cells as local <see cref="Idx"/>
    /// indices (the chunk manager owns that set), or null.</param>
    internal ChunkSnapshot TakeSnapshot(ushort[]? playerPlaced)
    {
        // The snapshot's constructor copies both arrays itself
        var snapshot = new ChunkSnapshot(_blocks, _state, playerPlaced);
        ModifiedSinceSave = false;
        return snapshot;
    }

    /// <summary>
    /// Fills this chunk from a save instead of generating it: the blocks, the state bytes and
    /// the player-placed cells (handed to the chunk manager through
    /// <see cref="RestoredPlayerPlaced"/>) come from <paramref name="snapshot"/>, and everything
    /// <see cref="GenerateBlockData"/> derives on the way — the non-air count, the surface
    /// floor and the fluid frontier — is recomputed from them. Runs on a generation worker on a
    /// chunk built with <c>generateBlockData: false</c>, before any other thread can see it;
    /// <see cref="IsBlockDataReady"/> goes true last, which is what publishes the rest.
    /// </summary>
    internal void RestoreFrom(ChunkSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Array.Copy(snapshot.Blocks, _blocks, BlockCount);
        _state = snapshot.State is { } state ? (byte[])state.Clone() : null;
        _solidCount = BlockCount - _blocks.AsSpan().Count(BlockType.Air);

        var column = GetColumnData((int)Position.X, (int)Position.Z);
        SurfaceMinY = column.MinHeight;
        IsRestored = true;
        RestoredPlayerPlaced = snapshot.PlayerPlaced is { } placed ? (ushort[])placed.Clone() : null;

        // A restored chunk can hold fluid anywhere, so it always scans (a chunk with no fluid
        // cell simply reports nothing)
        ScanFluidFrontier(column, LowerBoundY);

        IsBlockDataReady = true;
    }

    /// <summary>Clears a block. Returns false if the cell is outside this chunk or already air.</summary>
    public bool RemoveBlockAt(Vector3 worldPos, ChunkManager chunkManager)
    {
        var localX = (int)(worldPos.X - Position.X * Size);
        var localY = (int)(worldPos.Y - Position.Y * Size);
        var localZ = (int)(worldPos.Z - Position.Z * Size);

        if (!InBounds(localX, localY, localZ)) return false;
        if (_blocks[Idx(localX, localY, localZ)] == 0) return false;
        _blocks[Idx(localX, localY, localZ)] = 0;
        _solidCount--;
        ClearState(Idx(localX, localY, localZ));
        MarkModified();

        chunkManager.UnmarkPlayerPlaced((int)worldPos.X, (int)worldPos.Y, (int)worldPos.Z);
        chunkManager.ImmediateRemeshWithBorders(Position, worldPos);
        MarkNeighborsDirty(worldPos, chunkManager);
        return true;
    }

    /// <summary>
    /// Fills a block. Returns false if the cell is outside this chunk or already occupied, so
    /// callers can avoid spending an item on a placement that never happened.
    /// <paramref name="state"/> rides along into the state byte (a freshly grounded rock pile
    /// starts at 1); 0 clears it, which is what every ordinary placement wants.
    /// </summary>
    public bool PlaceBlockAt(Vector3 worldPos, byte blockId, ChunkManager chunkManager, byte state = 0)
    {
        var localX = (int)(worldPos.X - Position.X * Size);
        var localY = (int)(worldPos.Y - Position.Y * Size);
        var localZ = (int)(worldPos.Z - Position.Z * Size);

        if (!InBounds(localX, localY, localZ)) return false;
        // Water is displaced by a placement instead of blocking it, and a stack a placement may
        // take over (layered snow) is overwritten too; anything else occupies the cell and the
        // placement fails, so the caller keeps its item
        var existing = _blocks[Idx(localX, localY, localZ)];
        if (!Placement.CanOccupy(existing, GetLocalState(localX, localY, localZ))) return false;

        // One write does id, count and state together: SetBlockRaw moves _solidCount only
        // on air <-> non-air transitions, so displacing water keeps the count.
        SetBlockRaw(localX, localY, localZ, blockId, state);

        chunkManager.MarkPlayerPlaced((int)worldPos.X, (int)worldPos.Y, (int)worldPos.Z);
        chunkManager.ImmediateRemeshWithBorders(Position, worldPos);
        MarkNeighborsDirty(worldPos, chunkManager);
        return true;
    }

    /// <summary>
    /// Rewrites one cell's state byte with no id change — a rock pile growing by one — and
    /// remeshes like a placement so the new stone shows at once. Returns false outside this
    /// chunk or on air.
    /// </summary>
    public bool SetStateAt(Vector3 worldPos, byte state, ChunkManager chunkManager)
    {
        var localX = (int)(worldPos.X - Position.X * Size);
        var localY = (int)(worldPos.Y - Position.Y * Size);
        var localZ = (int)(worldPos.Z - Position.Z * Size);

        if (!InBounds(localX, localY, localZ)) return false;
        var i = Idx(localX, localY, localZ);
        if (_blocks[i] == 0) return false;
        SetBlockRaw(localX, localY, localZ, _blocks[i], state);

        chunkManager.MarkPlayerPlaced((int)worldPos.X, (int)worldPos.Y, (int)worldPos.Z);
        chunkManager.ImmediateRemeshWithBorders(Position, worldPos);
        MarkNeighborsDirty(worldPos, chunkManager);
        return true;
    }

    /// <summary>
    /// Clears a block without remeshing and returns the removed id (0 if the cell was outside
    /// this chunk or already air). The caller must remesh every touched chunk afterwards —
    /// explosions clear hundreds of blocks, where per-block remeshing would hitch.
    /// Main thread only, like the other block edits.
    /// </summary>
    public byte ClearBlockAt(Vector3 worldPos)
    {
        var localX = (int)(worldPos.X - Position.X * Size);
        var localY = (int)(worldPos.Y - Position.Y * Size);
        var localZ = (int)(worldPos.Z - Position.Z * Size);

        if (!InBounds(localX, localY, localZ)) return 0;
        var removed = _blocks[Idx(localX, localY, localZ)];
        if (removed == 0) return 0;
        _blocks[Idx(localX, localY, localZ)] = 0;
        _solidCount--;
        ClearState(Idx(localX, localY, localZ));
        MarkModified();
        return removed;
    }

    /// <summary>Zeroes one cell's state byte. Every write that is not a fluid write goes
    /// through here, so a stale level can never outlive the water that owned it.</summary>
    private void ClearState(int index)
    {
        if (_state != null) _state[index] = 0;
    }

    /// <summary>
    /// Schedules this chunk and everything in light radius for remeshing, so a batch edit
    /// that already cleared the blocks (see <see cref="ClearBlockAt"/>) becomes visible
    /// with correct light. Fully async through the dirty queue: a blast or a felling touches
    /// several chunks and building them all synchronously hitched the frame, while the dirty
    /// budget sorts closest-first and edited chunks sit at the camera, so they still rebuild
    /// within a frame or two. Single-block edits (<see cref="RemoveBlockAt"/>,
    /// <see cref="PlaceBlockAt"/>) stay synchronous: one build is cheap and instant.
    /// </summary>
    public void RemeshAfterBatchEdit(ChunkManager chunkManager)
    {
        var min = Position * Size;
        chunkManager.MarkAreaDirty(min, min + new Vector3(Size),
            WorldGenerationConfig.LightPropagationRadius);
    }

    /// <summary>
    /// Schedules every chunk in voxel-light radius of an edited block for remeshing, so
    /// lamp glow and skylight changes propagate their full 15 blocks instead of stopping
    /// at the edited chunk's border. The edited chunk itself was already remeshed
    /// synchronously by the caller and is skipped.
    /// </summary>
    private void MarkNeighborsDirty(Vector3 worldPos, ChunkManager chunkManager)
    {
        chunkManager.MarkAreaDirty(worldPos, worldPos + Vector3.One,
            WorldGenerationConfig.LightPropagationRadius, skip: Position);
    }

    private static bool InBounds(int x, int y, int z)
        => x >= 0 && y >= 0 && z >= 0 && x < Size && y < Size && z < Size;

    /// <summary>
    /// Builds this chunk's mesh, or returns null when a face neighbour is missing.
    /// The whole 3x3x3 neighbourhood is resolved once, up front: the caller checked for face
    /// neighbours before scheduling, but this runs on a pool thread behind a semaphore and can
    /// start seconds later, by which time an unload may have taken one away. Meshing against
    /// that hole reads solid ground as air and bakes bogus side faces, sunk fluid corners and
    /// sunlight leaks along the border, which then stay until something re-dirties the chunk —
    /// so the build is abandoned instead and the caller puts the chunk back on the dirty list.
    /// </summary>
    public ChunkMeshData? GenerateMeshData(ChunkManager chunkManager) => GenerateMeshData(chunkManager, greedy: true);

    /// <summary>
    /// <see cref="GenerateMeshData(ChunkManager)"/> with the binary greedy pass switchable: the
    /// differential test meshes the same chunk both ways and compares. A parameter rather than
    /// a static flag because test classes run in parallel and pool threads read it.
    /// <paramref name="tintFields"/> overrides the biome tint field: a build whose chunk is not
    /// where its blocks really stand (TopplingTree.BuildMeshData's scratch chunk at (256, 0, 256))
    /// passes the field of the real footprint; null means the chunk's own
    /// <see cref="BiomeTint.FieldsFor"/>.
    /// </summary>
    internal ChunkMeshData? GenerateMeshData(ChunkManager chunkManager, bool greedy,
        BiomeTintFields? tintFields = null)
    {
        // Resolve before renting or touching anything: an abandoned build should cost nothing.
        // The neighbourhood lives in the thread's mesh scratch, not in two fresh arrays.
        var scratch = MeshScratch.Current;
        var neighbors = scratch.Neighbors;
        var neighborStates = scratch.NeighborStates;
        if (!TryResolveNeighbors(chunkManager, neighbors, neighborStates))
        {
            scratch.ClearNeighbors();
            return null;
        }

        // Nothing to draw: either all air, or a solid block of occluders whose six neighbours
        // seal every boundary face. The second case is most of the deep terrain in the load
        // sphere, and skipping it saves the light flood and the per-voxel walk below.
        if (_solidCount == 0 || IsFullyEnclosed(neighbors))
            return new ChunkMeshData([], [], [], [], false);

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
            var anyTinted = ComputeLight(lightBlocks, sunLight, blockLight, queue);
            // Keep the chunk's own cells of the flood for entity lighting: a fresh array owned
            // by this build alone, published by UpdateMesh with the mesh it belongs to. A chunk
            // lit by coloured light appends a second half holding each cell's colour slot.
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
            return GenerateMeshDataFromSnapshot(padded, paddedState, sunLight, blockLight, chunkManager.MeshBuffers, greedy,
                    tintFields)
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
        }
    }

    // Slots of the six face-adjacent chunks in the 3x3x3 (cx*3+cy)*3+cz neighbourhood
    // (slot 13 is this chunk itself). Meshing cannot proceed without these; the twenty
    // edge and corner slots only feed light and fluid corner heights, so they may be null.
    private static readonly int[] FaceNeighborSlots = [4, 10, 12, 14, 16, 22];

    /// <summary>
    /// Resolves the 3x3x3 chunk neighbourhood once per mesh build, so both snapshot fills work
    /// from the same picture of the world instead of each racing the loader on its own. A
    /// neighbour that exists but has no block data yet counts as absent, exactly like one that
    /// was never loaded. Returns false when any of the six FACE neighbours is missing, which
    /// makes the mesh unbuildable (see <see cref="GenerateMeshData"/>); edge and corner
    /// neighbours are allowed to stay null and read as air.
    /// </summary>
    private bool TryResolveNeighbors(ChunkManager chunkManager, Chunk?[] neighbors, byte[]?[] neighborStates)
    {
        // Hoisted out of the 26 probes below, and FLOORED rather than truncated so this path
        // into the chunk key matches VoxelCoords.ChunkPosOf's convention on the negative side
        var px = MathUtils.FloorToInt(Position.X);
        var py = MathUtils.FloorToInt(Position.Y);
        var pz = MathUtils.FloorToInt(Position.Z);

        for (var cx = 0; cx < 3; cx++)
        for (var cy = 0; cy < 3; cy++)
        for (var cz = 0; cz < 3; cz++)
        {
            var ni = (cx * 3 + cy) * 3 + cz;
            if (ni == 13)
            {
                neighbors[ni] = this;
                neighborStates[ni] = _state;
                continue;
            }

            // Each neighbour's state array is read once into the array: the field is volatile
            // and may be allocated by a main-thread fluid edit while the snapshot is filled
            if (chunkManager.Chunks.TryGetValue(px + cx - 1, py + cy - 1, pz + cz - 1, out var neighbor)
                && neighbor.IsBlockDataReady)
            {
                neighbors[ni] = neighbor;
                neighborStates[ni] = neighbor._state;
            }
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
    /// neighbour that touches this chunk occludes too, so no face of any block can be seen.
    /// An air pocket anywhere inside (<c>_solidCount &lt; BlockCount</c>) has real faces around
    /// it and fails the test. Reads neighbour block arrays directly, the same tolerated race as
    /// the snapshot fills: an edit's own remesh of both chunks wins afterwards.
    /// </summary>
    private bool IsFullyEnclosed(Chunk?[] neighbors)
    {
        if (_solidCount != BlockCount) return false;

        var occludes = BlockType.OccludesById;
        for (var i = 0; i < BlockCount; i++)
        {
            if (!occludes[_blocks[i]]) return false;
        }

        // Face neighbours (slot = (cx*3+cy)*3+cz): the plane that faces this chunk is the
        // neighbour's far plane along that axis.
        var negX = neighbors[4]!._blocks;
        var posX = neighbors[22]!._blocks;
        var negY = neighbors[10]!._blocks;
        var posY = neighbors[16]!._blocks;
        var negZ = neighbors[12]!._blocks;
        var posZ = neighbors[14]!._blocks;
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
    /// neighbour chunk is missing or has no state array yet). The arrays come pre-resolved from
    /// <see cref="TryResolveNeighbors"/>, so the 6 face neighbours are always present; a null
    /// edge or corner slot reads as air, which only affects fluid corner heights.
    /// </summary>
    private void FillPaddedSnapshot(byte[] padded, byte[] paddedState, Chunk?[] neighbors, byte[]?[] neighborStates)
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
                    padded[rowBase + pz] = neighbor?._blocks[blockIndex] ?? 0;
                    paddedState[rowBase + pz] = neighborStates[ni]?[blockIndex] ?? 0;
                }
            }
        }
    }

    /// <summary>
    /// Turns the padded block snapshot into vertices. Plain full cubes (<see cref="BlockType.IsGreedy"/>)
    /// go through the binary greedy pass first (<see cref="EmitGreedyFaces"/>), which merges
    /// identical neighbouring faces into one quad. The per-voxel loop then handles the rest: each
    /// block picks an emitter from its <see cref="BlockType.Renderer"/> — the procedural ones
    /// (tree wood, pebbles, branch piles) build their geometry from tiles, everything else
    /// replays its baked model's quads.
    /// </summary>
    private ChunkMeshData GenerateMeshDataFromSnapshot(byte[] padded, byte[] paddedState,
        byte[] sunLight, byte[] blockLight, MeshBufferPool pool, bool greedy, BiomeTintFields? tintFields)
    {
        // Rented, not allocated: ChunkManager returns them once the mesh is on the GPU
        var vertices = pool.RentVertices();
        var indices = pool.RentIndices();
        ushort indexOffset = 0;

        // Fluids build a second, blended mesh drawn after the opaque world, so whatever sits
        // behind the water shows through it. Rented like the opaque lists; chunks without
        // fluid or glass return them untouched (see MeshBufferPool.Return).
        var tVertices = pool.RentTranslucentVertices();
        var tIndices = pool.RentTranslucentIndices();
        ushort tIndexOffset = 0;

        // Glass shares the translucent lists, so "has a translucent mesh" is not "has water";
        // the reflection and scene-copy passes need the narrower answer.
        var hasWater = false;

        // Exposed hot-fluid cells (lava open to the air), allocated only when one turns up
        List<ushort>? vents = null;

        // World origin of this chunk, for resolving a submerged face's water surface when its
        // column leaves the padded snapshot (SubmergedSurfaceCode)
        var chunkBaseY = LowerBoundY;
        var worldX0 = (int)Position.X * Size;
        var worldZ0 = (int)Position.Z * Size;

        // Biome tint of grass and leaves, both palettes from one climate evaluation per lattice
        // corner (8 noise evaluations per build). A scratch build away from its blocks' real
        // position (a felled tree) passes the field of the real footprint instead.
        var fields = tintFields ?? BiomeTint.FieldsFor((int)Position.X, (int)Position.Z);
        // Corner scratch of a biome-tinted face in the per-voxel loop below
        Span<byte> tShade = stackalloc byte[4];
        Span<float> tSun = stackalloc float[4];
        Span<float> tBlock = stackalloc float[4];
        Span<ushort> tTint = stackalloc ushort[4];

        if (greedy)
            EmitGreedyFaces(MeshScratch.Current, padded, paddedState, chunkBaseY, worldX0, worldZ0,
                sunLight, blockLight, vertices, indices, ref indexOffset, tVertices, tIndices, ref tIndexOffset, in fields);

        for (var x = 0; x < Size; x++)
        for (var y = 0; y < Size; y++)
        for (var z = 0; z < Size; z++)
        {
            var blockId = _blocks[Idx(x, y, z)];
            if (blockId == 0) continue;
            // Already emitted (merged) by the greedy pass above
            if (greedy && BlockType.IsGreedy(blockId)) continue;

            var type = BlockType.ById[blockId];
            if (type == null) continue;

            var basePos = new Vector3(x, y, z);
            var pIdx = (x + 1) * PaddedSq + (y + 1) * Padded + (z + 1);

            // Tree wood thinner than a full cube is a node box plus a tube toward every
            // connected neighbour (see EmitTreeWood); a full-thickness or detached cell falls
            // through to the plain model path below, which then also culls its faces against
            // a same-block full-thickness neighbour, since tree wood never occludes
            var cullSameFullWood = false;
            if (type.Renderer == BlockRenderer.TreeWood)
            {
                if (TreeWoodShape.IsThin(paddedState[pIdx]))
                {
                    EmitTreeWood(vertices, indices, ref indexOffset, padded, paddedState, pIdx, sunLight, blockLight,
                        x, y, z, type, basePos);
                    continue;
                }

                // A pixel-rim trunk shell with an exposed corner is bevelled from its four edge
                // neighbours (TreeWoodShape.RimCornerMask; the diagonals never change it, since a
                // cell only carves its own material); see EmitRimWood. Mask 0 is a plain cube.
                if (BlockType.HasPixelRim(blockId))
                {
                    var rimMask = RimMaskInSnapshot(padded, paddedState, pIdx, blockId);
                    if (rimMask != 0)
                    {
                        EmitRimWood(vertices, indices, ref indexOffset, padded, paddedState, pIdx, sunLight,
                            blockLight, x, y, z, type, basePos, rimMask);
                        continue;
                    }
                }

                cullSameFullWood = true;
            }

            // Pebbles render as 1-3 ankle-high boxes sharing the cell; see EmitPebbles
            if (type.Renderer == BlockRenderer.Pebbles)
            {
                EmitPebbles(vertices, indices, ref indexOffset, padded, paddedState, pIdx, sunLight, blockLight,
                    x, y, z, type, basePos);
                continue;
            }

            // Branch piles render as 1-4 crossed sticks sharing the cell; see EmitBranchPile
            if (type.Renderer == BlockRenderer.BranchPile)
            {
                EmitBranchPile(vertices, indices, ref indexOffset, padded, paddedState, pIdx, sunLight, blockLight,
                    x, y, z, type, basePos);
                continue;
            }

            // Layered snow renders as one box whose height is its stack depth; see EmitSnowLayers
            if (type.Renderer == BlockRenderer.SnowLayers)
            {
                EmitSnowLayers(vertices, indices, ref indexOffset, padded, paddedState, pIdx, sunLight, blockLight,
                    x, y, z, type, basePos);
                continue;
            }

            // A fluid's sloped surface is read out of paddedState and goes into the
            // translucent lists; see EmitFluid
            if (type.Renderer == BlockRenderer.Fluid)
            {
                EmitFluid(tVertices, tIndices, ref tIndexOffset, padded, paddedState, pIdx,
                    sunLight, blockLight, x, y, z, type, basePos);
                hasWater = true;
                if (IsHotFluidVent(padded[pIdx], padded[pIdx + FaceNeighborDelta[4]]))
                    (vents ??= []).Add((ushort)Idx(x, y, z));
                continue;
            }

            // A multiblock part is a solid, occluding cell that draws nothing: the anchor's
            // model already covers the whole 2x2x2 volume it belongs to.
            if (type.Renderer == BlockRenderer.MultiblockPart) continue;

            // ...and the anchor draws that whole model, pre-rotated to the facing its state
            // byte's low two bits carry (see MultiblockShape).
            var model = type.Renderer == BlockRenderer.Multiblock && type.RotatedModels != null
                ? type.RotatedModels[paddedState[pIdx] & 3]
                : type.BakedModel;
            if (model == null) continue;

            // Canopy leaves ride the wind in the vertex shader; everything else stays planted
            var sway = type.Sway ? 1f : 0f;
            var lightBase = LightIdx(x, y, z);
            var quads = model.Quads;

            // Translucent blocks (glass) replay the same baked quads into the blended
            // lists drawn after the opaque world; culling, AO and lighting below are shared.
            var dstVertices = type.Translucent ? tVertices : vertices;
            var dstIndices = type.Translucent ? tIndices : indices;
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
                // shader paints caustics on it. Interior sub-cell quads have no neighbour to
                // look into and stay false.
                var submerged = false;
                ushort waterSurface = 0;

                if (cullFace >= 0)
                {
                    var neighbour = padded[pIdx + FaceNeighborDelta[cullFace]];
                    // Only opaque neighbours hide a face. Leaves are cutout, so they never
                    // hide: a face culled behind one shows through its texture holes as an
                    // X-ray into the ground. Leaf-vs-leaf keeps both sides (Minecraft
                    // fancy-style) so the canopy still reads solid through the holes, while
                    // leaf-vs-rock draws the rock side only (the leaf side culls as before,
                    // so no coplanar double quad z-fights). CullSelf additionally hides a
                    // face against the same block, the way glass panes fuse.
                    if (BlockType.Occludes(neighbour) || (type.CullSelf && neighbour == blockId)) continue;
                    // Stacked full-thickness tree wood hides its shared faces itself - unless the
                    // neighbour is a bevelled pixel-rim shell whose face toward this cell is
                    // carved, which would open a hole (a core, a detached log or a mask-0 shell
                    // beside a rounded corner keeps its face). Non-rim species never read a mask.
                    if (cullSameFullWood && neighbour == blockId
                        && WoodState.IsFullCube(paddedState[pIdx + FaceNeighborDelta[cullFace]])
                        && (!BlockType.HasPixelRim(blockId)
                            || RimFaceFull(padded, paddedState, pIdx + FaceNeighborDelta[cullFace], blockId,
                                TreeWoodShape.Opposite(cullFace)))) continue;

                    // Light of the air cell in front of the face, from the wide light volume
                    // (so lamps across the chunk border still reach): the flat fallback for
                    // quads that do not smooth; a full face averages per corner below.
                    // A neighbour that would read as black light is exactly one that occludes,
                    // and that face is already gone, so no fallback is needed here.
                    lightCell = lightBase + LightFaceDelta[cullFace];
                    submerged = BlockType.IsFluid(neighbour);
                    // The water surface the caustics measure depth from: this face's own body
                    // of water, resolved exactly as the greedy pass keys it
                    if (submerged)
                        waterSurface = BakedWaterSurface(padded, paddedState, pIdx + FaceNeighborDelta[cullFace],
                            chunkBaseY, worldX0, worldZ0);
                }
                else
                {
                    // An interior quad of a sub-cell model has no neighbour to read: light it
                    // from its own cell, which passes light precisely because the model is not
                    // a full cube (the generalised form of the pebble trick).
                    lightCell = lightBase;
                }

                // Meshes are indexed with ushort (mirrors EmitFluid): stop emitting into a
                // list that is full instead of wrapping the offset
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
                        var vertex = new Vertex(basePos + positions[j], 255, 255, 255, 255, uvs[j], quad.Tile,
                            sun, block, normal, sway, submerged, waterSurface, flatTint);
                        dstVertices.Add(modelTint is { } palette ? ApplyModelTint(vertex, fields.Field(palette)) : vertex);
                    }
                }
                else if (quad.IsFullFace)
                {
                    // Only a quad that fills its cube face has meaningful corner neighbours,
                    // so per-corner AO applies exactly as it did for hand-written cubes. A face
                    // that culls against its own side (exactly the set the greedy pass takes,
                    // see ContentRegistry.BakesGreedy) also averages its light per corner.
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
                        EmitTintedCubeFace(dstVertices, dstIndices, ref dstOffset, face, basePos, Vector3.One,
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

                        var vertex = new Vertex(basePos + positions[j], shade, shade, shade, 255, uvs[j], quad.Tile,
                            vSun, vBlock, normal, sway, submerged, waterSurface, vTint);
                        dstVertices.Add(modelTint is { } palette ? ApplyModelTint(vertex, fields.Field(palette)) : vertex);
                    }
                }
                else
                {
                    // A sub-cell quad floats inside the cell, where corner sampling would
                    // darken it by neighbours it does not touch: shade it flat and unoccluded
                    var shade = ShadeLut[(face << 2) | 3];
                    for (var j = 0; j < 4; j++)
                    {
                        var vertex = new Vertex(basePos + positions[j], shade, shade, shade, 255, uvs[j], quad.Tile,
                            sun, block, normal, sway, submerged, waterSurface, flatTint);
                        dstVertices.Add(modelTint is { } palette ? ApplyModelTint(vertex, fields.Field(palette)) : vertex);
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

        return new ChunkMeshData(vertices, indices, tVertices, tIndices, hasWater)
            { HotFluidVents = vents?.ToArray() };
    }

    // Two-regime 7-bit code of a submerged face's water surface (SubmergedSurfaceCode):
    // 1..68 quarter blocks inside the padded snapshot, 69..127 whole blocks above its top.
    private const int SurfaceCodeInsideMax = 68;
    private const int SurfaceCodeAboveBase = 69;
    private const int SurfaceCodeMax = 127;

    /// <summary>
    /// The water surface above a submerged face, as the 7-bit code that joins the greedy key
    /// (bits 25-31), so a merge never crosses two different surfaces. It is the same surface
    /// <see cref="ChunkManager.GetFluidColumnTopGlobal"/> finds, resolved from the padded snapshot
    /// alone: the fluid column is walked up from <paramref name="fluidPIdx"/> (the fluid cell the
    /// face looks into). A column that tops out inside the snapshot codes its surface in quarter
    /// blocks (1..68). One that leaves the snapshot's top row still in fluid reads the generated
    /// column's <c>WaterLevel</c> (pure and worker-safe) and codes whole blocks above the snapshot
    /// top (69..127, clamped ~58 blocks above it, where the caustic fade is already ~0.01); a
    /// column whose generated level is lower than that (a player-built tower) caps at the
    /// snapshot top. <see cref="DecodeSurfaceCode"/> turns the code back into a world Y.
    /// </summary>
    internal static int SubmergedSurfaceCode(byte[] padded, byte[] paddedState, int fluidPIdx,
        int chunkBaseY, int worldX0, int worldZ0)
    {
        // FillPaddedSnapshot's layout: px * PaddedSq + py * Padded + pz (z innermost)
        var px = fluidPIdx / PaddedSq;
        var py = fluidPIdx % PaddedSq / Padded;
        var pz = fluidPIdx % Padded;

        var top = fluidPIdx;
        while (py < Padded - 1 && BlockType.IsFluid(padded[top + Padded]))
        {
            top += Padded;
            py++;
        }

        if (py < Padded - 1)
        {
            // The column's top cell lies inside the snapshot: its surface in chunk-local Y
            var ls = (py - 1) + FluidState.SurfaceHeight(paddedState[top]);
            return Math.Clamp((int)MathF.Round((ls + 1f) * 4f), 1, SurfaceCodeInsideMax);
        }

        // Still fluid in the snapshot's top row: the surface lies above what this build sees
        var wx = worldX0 + px - 1;
        var wz = worldZ0 + pz - 1;
        var level = GetColumnData(wx >> SizeShift, wz >> SizeShift)
            .WaterLevel[((wx & SizeMask) << SizeShift) | (wz & SizeMask)];
        var cap = chunkBaseY + Padded - 1;
        var surface = level != TerrainGenerator.NoWater && level + FluidState.SourceHeight > cap
            ? level + FluidState.SourceHeight
            : cap;
        return SurfaceCodeAboveBase
               + Math.Clamp((int)MathF.Round(surface - cap), 0, SurfaceCodeMax - SurfaceCodeAboveBase);
    }

    /// <summary>World Y of the water surface a <see cref="SubmergedSurfaceCode"/> code stands
    /// for, in a chunk whose lowest block row is <paramref name="chunkBaseY"/>.</summary>
    internal static float DecodeSurfaceCode(int chunkBaseY, int code) =>
        code <= SurfaceCodeInsideMax
            ? chunkBaseY + code / 4f - 1f
            : chunkBaseY + Padded - 1 + (code - SurfaceCodeAboveBase);

    /// <summary>The baked <see cref="Vertex.WaterSurface"/> of a face looking into the fluid
    /// cell at <paramref name="fluidPIdx"/>; the per-voxel twin of the greedy key's code.</summary>
    private static ushort BakedWaterSurface(byte[] padded, byte[] paddedState, int fluidPIdx,
        int chunkBaseY, int worldX0, int worldZ0) =>
        Vertex.EncodeWaterSurface(DecodeSurfaceCode(chunkBaseY,
            SubmergedSurfaceCode(padded, paddedState, fluidPIdx, chunkBaseY, worldX0, worldZ0)));

    // Axis each face's plane is perpendicular to (0 = X, 1 = Y, 2 = Z), in CubeFaces order,
    // and whether the face looks along +axis. Rows and columns of a face's bit plane are the two
    // other axes: X faces row = y, col = z; Y faces row = x, col = z; Z faces row = y, col = x.
    private static readonly int[] FaceAxis = [2, 2, 0, 0, 1, 1];
    private static readonly bool[] FacePositive = [false, true, false, true, true, false];

    // Bits 1..16 of an 18-bit padded column: the chunk's own cells (0 and 17 are the halo)
    private const uint ChunkBits = 0x1FFFE;

    /// <summary>
    /// Binary greedy pass over every <see cref="BlockType.IsGreedy"/> cell. Faces are found with
    /// bit columns (a cell shows a face when its greedy bit is set and the occluder bit one
    /// step along the face normal is not — the halo bits cull border faces exactly as the
    /// per-voxel neighbour read does), keyed by everything that would make two cells' quads
    /// differ (block, the four corner AO values, the four corner smooth sun/lamp light sums of
    /// <see cref="SmoothCornerLight"/> in a parallel 48-bit key, the submerged flag and, for a
    /// submerged face, its water surface code from <see cref="SubmergedSurfaceCode"/>),
    /// gathered per depth slice into one bit plane per key, and merged per plane into the widest,
    /// then tallest, rectangles. The output is pixel-identical
    /// to emitting each face on its own: a merge only happens along an axis where the key's AO
    /// and both lights are constant, so the big quad's interpolated shade and light equal the
    /// per-cell quads' (see
    /// <see cref="EmitGreedyRun"/>), and the UVs run 0..W / 0..H in tile units so the sampler's
    /// REPEAT tiles the texture per cell.
    /// </summary>
    private static void EmitGreedyFaces(MeshScratch scratch, byte[] padded, byte[] paddedState,
        int chunkBaseY, int worldX0, int worldZ0, byte[] sunLight, byte[] blockLight,
        List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        List<Vertex> tVertices, List<ushort> tIndices, ref ushort tIndexOffset, in BiomeTintFields fields)
    {
        var greedyCols = scratch.GreedyColumns;
        var occCols = scratch.OccluderColumns;
        for (var a = 0; a < 3; a++)
        {
            Array.Clear(greedyCols[a]);
            Array.Clear(occCols[a]);
        }

        // A. One pass over the padded snapshot fills the three column sets. Column index is
        // (row + 1) * Padded + (col + 1) in the face's row/col convention; the bit is the padded
        // coordinate along the axis.
        var greedyById = BlockType.GreedyById;
        var occludesById = BlockType.OccludesById;
        var gX = greedyCols[0]; var gY = greedyCols[1]; var gZ = greedyCols[2];
        var oX = occCols[0]; var oY = occCols[1]; var oZ = occCols[2];
        var anyGreedy = false;
        for (var px = 0; px < Padded; px++)
        for (var py = 0; py < Padded; py++)
        {
            var rowBase = px * PaddedSq + py * Padded;
            for (var pz = 0; pz < Padded; pz++)
            {
                var id = padded[rowBase + pz];
                if (id == 0) continue;
                if (greedyById[id])
                {
                    gX[py * Padded + pz] |= 1u << px;
                    gY[px * Padded + pz] |= 1u << py;
                    gZ[py * Padded + px] |= 1u << pz;
                    anyGreedy = true;
                }
                if (occludesById[id])
                {
                    oX[py * Padded + pz] |= 1u << px;
                    oY[px * Padded + pz] |= 1u << py;
                    oZ[py * Padded + px] |= 1u << pz;
                }
            }
        }
        if (!anyGreedy) return;

        var visible = scratch.Visible;
        var table = scratch.PlaneTable;
        var planeKeys = scratch.PlaneKeys;
        var planeLightKeys = scratch.PlaneLightKeys;
        var planeTintKeys = scratch.PlaneTintKeys;
        var planeRows = scratch.PlaneRows;

        for (var f = 0; f < 6; f++)
        {
            var axis = FaceAxis[f];
            var positive = FacePositive[f];
            var g = greedyCols[axis];
            var o = occCols[axis];
            var faceDelta = FaceNeighborDelta[f];
            var aoDeltas = AODeltas[f];

            // B. Face mask per column: a face shows where the cell is greedy and the cell one
            // step along the normal does not occlude
            var anyVisible = 0u;
            for (var row = 0; row < Size; row++)
            for (var col = 0; col < Size; col++)
            {
                var column = (row + 1) * Padded + (col + 1);
                var occ = o[column];
                var mask = g[column] & ~(positive ? occ >> 1 : occ << 1) & ChunkBits;
                visible[row * Size + col] = mask;
                anyVisible |= mask;
            }
            if (anyVisible == 0) continue;

            // C. Per depth slice: key every visible face and gather the faces of each key into
            // one 16x16 bit plane. Keys go through a small open-addressed table (at most 256
            // distinct keys per slice, 512 slots), which is cheaper than sorting the entries.
            for (var depth = 0; depth < Size; depth++)
            {
                var bit = 1u << (depth + 1);
                if ((anyVisible & bit) == 0) continue;
                var planeCount = 0;
                Array.Clear(table);

                for (var row = 0; row < Size; row++)
                for (var col = 0; col < Size; col++)
                {
                    if ((visible[row * Size + col] & bit) == 0) continue;

                    int x, y, z;
                    switch (axis)
                    {
                        case 0: x = depth; y = row; z = col; break;
                        case 1: x = row; y = depth; z = col; break;
                        default: x = col; y = row; z = depth; break;
                    }

                    var pIdx = (x + 1) * PaddedSq + (y + 1) * Padded + (z + 1);
                    var id = padded[pIdx];
                    var neighbour = padded[pIdx + faceDelta];
                    // Glass panes fuse: a face against the same block is hidden (the occluder
                    // mask already handled opaque neighbours)
                    if (neighbour == id && BlockType.ById[id]!.CullSelf) continue;

                    var lightBase = LightIdx(x, y, z);
                    var ao = 0;
                    var light = 0UL;
                    var tint = 0UL;
                    for (var j = 0; j < 4; j++)
                    {
                        var side1Id = padded[pIdx + aoDeltas[j * 3 + 0]];
                        var side2Id = padded[pIdx + aoDeltas[j * 3 + 1]];
                        var cornerId = padded[pIdx + aoDeltas[j * 3 + 2]];
                        ao |= VertexAO(BlockType.OccludesAO(side1Id), BlockType.OccludesAO(side2Id),
                            BlockType.OccludesAO(cornerId)) << (2 * j);
                        var code = SmoothCornerLight(f, j, sunLight, blockLight, lightBase,
                            side1Id, side2Id, cornerId);
                        light |= (ulong)(code & 0xFFF) << (12 * j);
                        tint |= (ulong)(code >> 12) << (10 * j);
                    }

                    // Key: id 0-7, AO 8-15, 16-23 unused, submerged 24, and for a submerged
                    // face its 7-bit water surface code in 25-31. Bit 31 can make the key
                    // negative; that is safe because the slot hash reads it as (uint)key and an
                    // empty slot is table entry 0, never a key value. The light travels in a
                    // parallel 48-bit key: four 12-bit SmoothCornerLight codes, corner j at 12j,
                    // and the colour in a second parallel 40-bit key: the four 10-bit LightTint
                    // codes, corner j at 10j (all zero wherever the lamp light is default-coloured).
                    var key = id | ao << 8;
                    if (BlockType.IsFluid(neighbour))
                        key |= 1 << 24 | SubmergedSurfaceCode(padded, paddedState, pIdx + faceDelta,
                            chunkBaseY, worldX0, worldZ0) << 25;

                    // Find or open the key's plane
                    var h = ((ulong)(uint)key ^ light * 0x9E3779B97F4A7C15UL ^ tint * 0xC2B2AE3D27D4EB4FUL)
                            * 0xBF58476D1CE4E5B9UL;
                    var slot = (int)(h >> (64 - MeshScratch.PlaneTableBits));
                    int plane;
                    while (true)
                    {
                        var entry = table[slot];
                        if (entry == 0)
                        {
                            plane = planeCount++;
                            table[slot] = plane + 1;
                            planeKeys[plane] = key;
                            planeLightKeys[plane] = light;
                            planeTintKeys[plane] = tint;
                            Array.Clear(planeRows, plane * Size, Size);
                            break;
                        }
                        if (planeKeys[entry - 1] == key && planeLightKeys[entry - 1] == light
                            && planeTintKeys[entry - 1] == tint)
                        {
                            plane = entry - 1;
                            break;
                        }
                        slot = (slot + 1) & (MeshScratch.PlaneTableSize - 1);
                    }
                    planeRows[plane * Size + row] |= (ushort)(1 << col);
                }

                // D + E. Merge and emit each key's plane
                for (var plane = 0; plane < planeCount; plane++)
                {
                    var key = planeKeys[plane];
                    var light = planeLightKeys[plane];
                    var tint = planeTintKeys[plane];
                    var type = BlockType.ById[key & 0xFF]!;
                    var rows = planeRows.AsSpan(plane * Size, Size);
                    if (type.Translucent)
                        EmitGreedyRun(f, axis, depth, chunkBaseY, key, light, tint, type, rows, tVertices, tIndices, ref tIndexOffset, in fields);
                    else
                        EmitGreedyRun(f, axis, depth, chunkBaseY, key, light, tint, type, rows, vertices, indices, ref indexOffset, in fields);
                }
            }
        }
    }

    /// <summary>
    /// Merges one key's bit plane into rectangles and emits them. Every cell in the plane has the
    /// same four corner AO values and the same four corner smooth sun/lamp light sums
    /// (<paramref name="light"/>), but that alone does not make them continuous across two cells:
    /// for ordinary occluders the shared-vertex geometry makes AO so, while a neighbour that
    /// casts AO without occluding (leaves) can leave a step at a shared edge, and
    /// light has a gradient wherever a lamp or a shadow edge is near. So a run only grows along an
    /// axis where the key's per-corner code (AO and both lights together) is the same at both
    /// ends of that axis — then the two cells' shared corners carry the same values, shade and
    /// light inside every cell are affine along it, and the merged quad interpolates to exactly
    /// the per-cell result. Uniformly lit terrain merges both ways; a strip beside a wall merges
    /// along the wall. The colour tint code (<paramref name="tint"/>) joins the per-corner code
    /// too: the shader decodes each corner's code to an RGB tint and lerps that linearly across the
    /// quad, so equal corner codes along a merged axis give the identical colour gradient.
    /// </summary>
    private static void EmitGreedyRun(int f, int axis, int depth, int chunkBaseY, int key, ulong light, ulong tint,
        BlockType type,
        Span<ushort> rows, List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset, in BiomeTintFields fields)
    {
        var ao = (key >> 8) & 0xFF;
        // Per-corner code: AO in bits 0-1, the 12-bit smooth light code in 2-13, the 10-bit
        // tint code in 14-23
        Span<int> code = stackalloc int[4];
        for (var j = 0; j < 4; j++)
            code[j] = ((ao >> (2 * j)) & 3) | (int)((light >> (12 * j)) & 0xFFF) << 2
                      | (int)((tint >> (10 * j)) & 0x3FF) << 14;
        // Corner pairs that differ along the col axis: (c0,c1),(c3,c2) on Y faces (c0-c1 runs
        // along z there), (c0,c3),(c1,c2) on X and Z faces; the row axis takes the other pair.
        var yFace = axis == 1;
        var alongCol = yFace ? code[0] == code[1] && code[3] == code[2] : code[0] == code[3] && code[1] == code[2];
        var alongRow = yFace ? code[0] == code[3] && code[1] == code[2] : code[0] == code[1] && code[3] == code[2];

        var submerged = ((key >> 24) & 1) != 0;
        var waterSurface = submerged
            ? Vertex.EncodeWaterSurface(DecodeSurfaceCode(chunkBaseY, (key >> 25) & 127))
            : (ushort)0;
        var layer = type.FaceTiles[f];
        var sway = type.Sway ? 1f : 0f;
        var normal = CubeFaces[f].normal;
        var corners = CubeFaces[f].face;

        // A biome-tinted block's corner values, decoded once per plane exactly as the plain
        // vertex loop below decodes them per quad (the key holds them constant over the plane)
        var biomeTint = type.BiomeTint;
        Span<byte> tShade = stackalloc byte[4];
        Span<float> tSun = stackalloc float[4];
        Span<float> tBlock = stackalloc float[4];
        Span<ushort> tTint = stackalloc ushort[4];
        if (biomeTint != null)
        {
            for (var j = 0; j < 4; j++)
            {
                tShade[j] = ShadeLut[(f << 2) | ((ao >> (2 * j)) & 3)];
                tSun[j] = (int)((light >> (12 * j)) & 63) / SmoothLightDivisor;
                tBlock[j] = (int)((light >> (12 * j + 6)) & 63) / SmoothLightDivisor;
                tTint[j] = (ushort)((tint >> (10 * j)) & 0x3FF);
            }
        }

        for (var row = 0; row < Size; row++)
        {
            var bits = (int)rows[row];
            while (bits != 0)
            {
                var start = BitOperations.TrailingZeroCount(bits);
                // Run of set bits from start; ~(bits >> start) has a zero above bit 15 at the
                // latest, so width never passes the plane's edge
                var width = alongCol ? BitOperations.TrailingZeroCount(~(bits >> start)) : 1;
                var mask = ((1 << width) - 1) << start;
                var height = 1;
                if (alongRow)
                {
                    while (row + height < Size && (rows[row + height] & mask) == mask)
                    {
                        rows[row + height] &= (ushort)~mask;
                        height++;
                    }
                }
                bits &= ~mask;

                // Meshes are indexed with ushort (mirrors EmitFluid): stop emitting into a full
                // list instead of wrapping the offset
                if (vertices.Count > ushort.MaxValue - 3) return;

                Vector3 origin, extent;
                switch (axis)
                {
                    case 0:
                        origin = new Vector3(depth, row, start);
                        extent = new Vector3(1f, height, width);
                        break;
                    case 1:
                        origin = new Vector3(row, depth, start);
                        extent = new Vector3(height, 1f, width);
                        break;
                    default:
                        origin = new Vector3(start, row, depth);
                        extent = new Vector3(width, height, 1f);
                        break;
                }

                // A biome-tinted block (grass, leaves) goes through the emitter the per-voxel
                // path shares, so both paths give identical output. The tint is a function of
                // vertex position, not of the cell, so it never splits a merge.
                if (biomeTint is { } bt)
                {
                    EmitTintedCubeFace(vertices, indices, ref indexOffset, f, origin, extent,
                        tShade, tSun, tBlock, tTint, bt, fields.Field(bt.Palette), submerged, waterSurface, sway);
                    continue;
                }

                // Corner j of the merged quad sits where corner j of a cell quad would, scaled
                // by the extent, so key AO and light corner j land on the matching corner
                for (var j = 0; j < 4; j++)
                {
                    var local = corners[j] * extent;
                    var shade = ShadeLut[(f << 2) | ((ao >> (2 * j)) & 3)];
                    var sun = (int)((light >> (12 * j)) & 63) / SmoothLightDivisor;
                    var block = (int)((light >> (12 * j + 6)) & 63) / SmoothLightDivisor;
                    vertices.Add(new Vertex(origin + local, shade, shade, shade, 255,
                        TiledUv(f, local, extent), layer, sun, block, normal, sway, submerged, waterSurface,
                        (ushort)((tint >> (10 * j)) & 0x3FF)));
                }

                indices.Add((ushort)(indexOffset + 0));
                indices.Add((ushort)(indexOffset + 1));
                indices.Add((ushort)(indexOffset + 2));
                indices.Add((ushort)(indexOffset + 0));
                indices.Add((ushort)(indexOffset + 2));
                indices.Add((ushort)(indexOffset + 3));
                indexOffset += 4;
            }
        }
    }

    /// <summary>
    /// Emits one face of a biome-tinted cube (<see cref="BlockType.BiomeTint"/>), merged over
    /// <paramref name="extent"/> or a single cell, for both the greedy pass and the per-voxel
    /// replay, so their output is identical by construction. Up to two coplanar quads: first the
    /// untinted base (grass sides: dirt) when the face keeps one, then the tinted quad, whose rgb
    /// is shade * the field's tint at each corner and whose packed normal sets bit 4 so the world
    /// fragment shader applies <see cref="BiomeTint.Gain"/>. The base goes first so the overlay,
    /// at identical depth, wins under Lequal. Corner values are in <see cref="CubeFaces"/> order;
    /// the field is sampled at the chunk-local vertex position, so neighbouring chunks agree.
    /// </summary>
    private static void EmitTintedCubeFace(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        int f, Vector3 origin, Vector3 extent, ReadOnlySpan<byte> shade, ReadOnlySpan<float> sun,
        ReadOnlySpan<float> block, ReadOnlySpan<ushort> lightTint, BiomeTintFaces bt, in BiomeTintField field,
        bool submerged, ushort waterSurface, float sway)
    {
        var normal = CubeFaces[f].normal;
        var corners = CubeFaces[f].face;

        if (bt.Base[f] != BiomeTintFaces.NoTile)
        {
            if (vertices.Count > ushort.MaxValue - 3) return;
            var layer = bt.Base[f];
            for (var j = 0; j < 4; j++)
            {
                var local = corners[j] * extent;
                vertices.Add(new Vertex(origin + local, shade[j], shade[j], shade[j], 255,
                    TiledUv(f, local, extent), layer, sun[j], block[j], normal, sway, submerged, waterSurface,
                    lightTint[j]));
            }
            AddQuadIndices(indices, ref indexOffset);
        }

        if (bt.Tinted[f] != BiomeTintFaces.NoTile)
        {
            if (vertices.Count > ushort.MaxValue - 3) return;
            var layer = bt.Tinted[f];
            for (var j = 0; j < 4; j++)
            {
                var local = corners[j] * extent;
                var position = origin + local;
                BiomeTint.Shade(shade[j], field.Sample(position.X, position.Z), out var r, out var g, out var b);
                vertices.Add(new Vertex(position, r, g, b, 255,
                    TiledUv(f, local, extent), layer, sun[j], block[j], normal, sway, submerged, waterSurface,
                    lightTint[j], biomeTint: true));
            }
            AddQuadIndices(indices, ref indexOffset);
        }

        static void AddQuadIndices(List<ushort> indices, ref ushort indexOffset)
        {
            indices.Add((ushort)(indexOffset + 0));
            indices.Add((ushort)(indexOffset + 1));
            indices.Add((ushort)(indexOffset + 2));
            indices.Add((ushort)(indexOffset + 0));
            indices.Add((ushort)(indexOffset + 2));
            indices.Add((ushort)(indexOffset + 3));
            indexOffset += 4;
        }
    }

    /// <summary>
    /// Recolours one quad vertex of a <c>modelTint</c> block: its rgb becomes the vertex's shade
    /// times the palette's field at the vertex, flagged so the world fragment shader applies
    /// <see cref="BiomeTint.Gain"/>. The vertex's rgb standing in for its shade (three equal
    /// channels, 255 on an unshaded quad) is what makes the shade recoverable here.
    /// </summary>
    private static Vertex ApplyModelTint(Vertex vertex, in BiomeTintField field)
    {
        var shade = (byte)(vertex.PackedColor & 0xFF);
        BiomeTint.Shade(shade, field.Sample(vertex.Position.X, vertex.Position.Z), out var r, out var g, out var b);
        return vertex.WithBiomeTint(r, g, b);
    }

    /// <summary>
    /// Emits one fluid cell's surface into the translucent lists. The top face is a quad whose
    /// four corners sit at <see cref="CornerHeight"/>, so a flow slopes away from its source
    /// and - the average being symmetric between neighbours - adjacent tops meet exactly with
    /// no crack. No face is ever emitted between two fluid cells, so a body of water reads as
    /// a single skin instead of a stack of blended cubes.
    /// </summary>
    private void EmitFluid(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        byte[] padded, byte[] paddedState, int pIdx, byte[] sunLight, byte[] blockLight,
        int x, int y, int z, BlockType type, Vector3 basePos)
    {
        // Smooth light per (X, Z) corner, averaged over the same 2x2 cells CornerHeight reads.
        // That set is identical for every fluid cell meeting at the corner and the fill of a
        // blocked sample is order-independent, so neighbours agree at shared corners exactly as
        // their heights do and a shoreline shows no light step along a cell edge. (Full cube
        // faces use SmoothCornerLight's asymmetric front-substitution rule instead.) Side and
        // bottom vertices sit on integer X/Z, so they take the corner values unchanged.
        var lightBase = LightIdx(x, y, z);
        var l00 = FluidCornerLight(padded, pIdx, sunLight, blockLight, lightBase, 0, 0);
        var l01 = FluidCornerLight(padded, pIdx, sunLight, blockLight, lightBase, 0, 1);
        var l10 = FluidCornerLight(padded, pIdx, sunLight, blockLight, lightBase, 1, 0);
        var l11 = FluidCornerLight(padded, pIdx, sunLight, blockLight, lightBase, 1, 1);
        var topCorners = CubeFaces[4].face;
        var light = FaceLight.FromCorners(4,
            FluidCornerPick(topCorners[0], l00, l01, l10, l11),
            FluidCornerPick(topCorners[1], l00, l01, l10, l11),
            FluidCornerPick(topCorners[2], l00, l01, l10, l11),
            FluidCornerPick(topCorners[3], l00, l01, l10, l11));

        // Corner heights indexed by the corner's own (X, Z) inside the cell: cornerX * 2 + cornerZ
        Span<float> corners = stackalloc float[4];
        var minCorner = float.MaxValue;
        for (var cx = 0; cx < 2; cx++)
        for (var cz = 0; cz < 2; cz++)
        {
            var h = CornerHeight(padded, paddedState, pIdx,
                cx == 0 ? -PaddedSq : PaddedSq, cz == 0 ? -1 : 1);
            corners[cx * 2 + cz] = h;
            if (h < minCorner) minCorner = h;
        }

        // A fluid's model only supplies its tile (see the block schema), so every face uses it
        var tile = type.Top;

        // Per-corner flow codes (Vertex.EncodeWaterFlow), indexed like `corners`. Computed on the
        // first face that survives the culls, so a fully enclosed sea cell pays nothing.
        Span<ushort> flowCodes = stackalloc ushort[4];
        var flowComputed = false;
        var anyFlow = false;

        for (var face = 0; face < 6; face++)
        {
            var neighbour = padded[pIdx + FaceNeighborDelta[face]];

            // Two fluid cells share no face: their sloped tops already meet at the corners
            if (BlockType.IsFluid(neighbour)) continue;
            if (face != 4 && BlockType.Occludes(neighbour)) continue;
            // A full column pressed against a ceiling has a coplanar top face - drop it
            // instead of z-fighting the block above
            if (face == 4 && BlockType.Occludes(neighbour) && minCorner >= 0.999f) continue;

            // Meshes are indexed with ushort (mirrors LodRegion.AddQuad): stop emitting
            // instead of wrapping the offset if a chunk somehow runs past the limit
            if (vertices.Count > ushort.MaxValue - 3) return;

            if (!flowComputed)
            {
                flowComputed = true;
                anyFlow = ComputeFluidFlowCodes(padded, paddedState, pIdx, flowCodes);
            }

            var faceCorners = CubeFaces[face].face;
            var a = LowerCorner(faceCorners[0], corners);
            var b = LowerCorner(faceCorners[1], corners);
            var c = LowerCorner(faceCorners[2], corners);
            var d = LowerCorner(faceCorners[3], corners);

            // A sloped top needs the normal of the quad it actually became, or the sun
            // diffuse ignores the slope; the other five faces stay axis-aligned
            var normal = face == 4
                ? Vector3.Normalize(Vector3.Cross(c - a, d - b))
                : CubeFaces[face].normal;

            // ponytail: alpha 254 flags water for the shader (glass stays 255); fColor.a is unused otherwise.
            EmitQuad(vertices, indices, ref indexOffset, basePos, a, b, c, d, normal,
                CropUv(face, a), CropUv(face, b), CropUv(face, c), CropUv(face, d), tile,
                ShadeLut[(face << 2) | 3], light, 254);

            // EmitQuad appended a, b, c, d in that order; each vertex takes its (X, Z) corner's
            // flow code (LowerCorner keeps X/Z at 0 or 1, so side and bottom vertices do too)
            if (anyFlow)
            {
                var span = CollectionsMarshal.AsSpan(vertices);
                span[^4].WaterSurface = flowCodes[(int)a.X * 2 + (int)a.Z];
                span[^3].WaterSurface = flowCodes[(int)b.X * 2 + (int)b.Z];
                span[^2].WaterSurface = flowCodes[(int)c.X * 2 + (int)c.Z];
                span[^1].WaterSurface = flowCodes[(int)d.X * 2 + (int)d.Z];
            }
        }
    }

    /// <summary>
    /// Fills the four per-corner flow codes of the fluid cell at <paramref name="pIdx"/>
    /// (<see cref="FluidFlow.Corner{T}"/> scaled by <see cref="WorldGenerationConfig.WaterFlowFullStrength"/>,
    /// plus the cell's falling flag) and reports whether any is nonzero. Falling is the cell's own
    /// state alone, never "fluid above": a stack of still sources is not a waterfall.
    /// </summary>
    private static bool ComputeFluidFlowCodes(byte[] padded, byte[] paddedState, int pIdx, Span<ushort> codes)
    {
        var cells = new PaddedFluidCells(padded, paddedState, pIdx);
        var falling = FluidState.IsFalling(paddedState[pIdx]);
        var any = false;
        for (var cx = 0; cx < 2; cx++)
        for (var cz = 0; cz < 2; cz++)
        {
            var v = FluidFlow.Corner(cells, 0, 0, 0, cx == 0 ? -1 : 1, cz == 0 ? -1 : 1);
            var dir = v.NormalizeSafe();
            var strength = dir == Vector3.Zero
                ? 0f
                : MathF.Min(v.Length() / WorldGenerationConfig.WaterFlowFullStrength, 1f);
            var code = Vertex.EncodeWaterFlow(new Vector2(dir.X, dir.Z) * strength, falling);
            codes[cx * 2 + cz] = code;
            any |= code != 0;
        }

        return any;
    }

    /// <summary>
    /// <see cref="IFluidCellReader"/> over the mesher's padded snapshot, in coordinates relative
    /// to the cell at <paramref name="pIdx"/>. Always succeeds: <see cref="FluidFlow.Corner{T}"/>
    /// reads only within ±1 horizontally at y and y - 1, which stays inside the 18³ snapshot for
    /// any own cell at padded 1..16, and an absent edge/corner chunk reads as air there - the same
    /// "no term" branch a live TryGetCell == false would take.
    /// </summary>
    private readonly struct PaddedFluidCells(byte[] padded, byte[] paddedState, int pIdx) : IFluidCellReader
    {
        public bool TryGetCell(int x, int y, int z, out byte id, out byte state)
        {
            var q = pIdx + x * PaddedSq + y * Padded + z;
            id = padded[q];
            state = paddedState[q];
            return true;
        }
    }

    private static int FluidCornerPick(Vector3 corner, int l00, int l01, int l10, int l11) =>
        corner.X == 0f ? (corner.Z == 0f ? l00 : l01) : (corner.Z == 0f ? l10 : l11);

    /// <summary>
    /// Smooth light code (<c>sunSum | blockSum &lt;&lt; 6 | tint &lt;&lt; 12</c>, the tint a
    /// <see cref="LightTint"/> code over the same four samples) of one top corner of a fluid cell,
    /// summed over the own, dx, dz and diagonal cells at the fluid's height (the cells
    /// <see cref="CornerHeight"/> reads). A cell that does not pass light stores 0, so it is
    /// replaced by the brightest open sample, per channel; the own fluid cell always passes. The
    /// rule never asks which of the four cells is the own one, so every fluid cell sharing the
    /// corner computes the same code.
    /// </summary>
    private static int FluidCornerLight(byte[] padded, int pIdx, byte[] sunLight, byte[] blockLight,
        int lightBase, int cx, int cz)
    {
        var pdx = cx == 0 ? -PaddedSq : PaddedSq;
        var ldx = cx == 0 ? -LightSq : LightSq;
        var dz = cz == 0 ? -1 : 1;
        var passes = BlockType.PassesLightById;
        var o1 = passes[padded[pIdx + pdx]];
        var o2 = passes[padded[pIdx + dz]];
        var o3 = passes[padded[pIdx + pdx + dz]];

        int s0 = sunLight[lightBase], s1 = sunLight[lightBase + ldx],
            s2 = sunLight[lightBase + dz], s3 = sunLight[lightBase + ldx + dz];
        // blockLight holds level | slot << 4 (see ComputeLight)
        int r0 = blockLight[lightBase], r1 = blockLight[lightBase + ldx],
            r2 = blockLight[lightBase + dz], r3 = blockLight[lightBase + ldx + dz];
        int b0 = r0 & BlockLevelMask, b1 = r1 & BlockLevelMask,
            b2 = r2 & BlockLevelMask, b3 = r3 & BlockLevelMask;

        var sMax = s0;
        var bMax = b0;
        if (o1) { sMax = Math.Max(sMax, s1); bMax = Math.Max(bMax, b1); }
        if (o2) { sMax = Math.Max(sMax, s2); bMax = Math.Max(bMax, b2); }
        if (o3) { sMax = Math.Max(sMax, s3); bMax = Math.Max(bMax, b3); }

        var sun = s0 + (o1 ? s1 : sMax) + (o2 ? s2 : sMax) + (o3 ? s3 : sMax);
        var block = b0 + (o1 ? b1 : bMax) + (o2 ? b2 : bMax) + (o3 ? b3 : bMax);

        // A blocked sample takes the brightest open sample's colour too; on a level tie the
        // lowest slot among the open samples at that level, so the fill never depends on which
        // of the four cells is the own one and neighbouring fluid cells agree at the corner
        int sl0 = (r0 >> 4) & 7, sl1 = (r1 >> 4) & 7, sl2 = (r2 >> 4) & 7, sl3 = (r3 >> 4) & 7;
        var slMax = 7;
        if (b0 == bMax) slMax = sl0;
        if (o1 && b1 == bMax && sl1 < slMax) slMax = sl1;
        if (o2 && b2 == bMax && sl2 < slMax) slMax = sl2;
        if (o3 && b3 == bMax && sl3 < slMax) slMax = sl3;
        var tint = LightTint.FromSamples(b0, sl0,
            o1 ? b1 : bMax, o1 ? sl1 : slMax,
            o2 ? b2 : bMax, o2 ? sl2 : slMax,
            o3 ? b3 : bMax, o3 ? sl3 : slMax);
        return sun | block << 6 | tint << 12;
    }

    /// <summary>Drops a cube corner's top vertex to the fluid surface, so side faces crop to
    /// the real height and the top face slopes. Bottom corners pass through unchanged.</summary>
    private static Vector3 LowerCorner(Vector3 corner, Span<float> cornerHeights) =>
        corner.Y == 0f
            ? corner
            : new Vector3(corner.X, cornerHeights[(int)corner.X * 2 + (int)corner.Z], corner.Z);

    /// <summary>Surface height inside one fluid cell of the padded snapshot, 0..1. Fluid
    /// directly above fills the cell completely, so stacked water shows no seam.</summary>
    private static float FluidHeight(byte[] padded, byte[] paddedState, int p) =>
        BlockType.IsFluid(padded[p + FaceNeighborDelta[4]])
            ? 1f
            : FluidState.SurfaceHeight(paddedState[p]);

    /// <summary>
    /// Height of the fluid surface at one corner of a cell, averaged over the cells that meet
    /// there (<paramref name="dx"/> and <paramref name="dz"/> are the padded index deltas
    /// toward the corner). Nearly full cells outweigh thin ones ten to one, so a source next
    /// to a skirt keeps its own level; air and cutouts pull the corner down toward 0; an
    /// occluding solid is skipped, because a wall must not lower the water beside it. The
    /// diagonal cell counts only when at least one of the two orthogonal neighbours is fluid
    /// (Minecraft's rule), so two bodies of water touching across a corner with solids in both
    /// orthogonal cells stay separate instead of visually joining. The cell itself is fluid, so
    /// the weight is never zero.
    /// </summary>
    private static float CornerHeight(byte[] padded, byte[] paddedState, int p, int dx, int dz)
    {
        var total = 0f;
        var weight = 0f;
        // The diagonal is only reachable through a fluid orthogonal neighbour; with neither of
        // them fluid it is ignored outright - no height, no full-column pin, no weight
        var orthogonalFluid = false;

        for (var i = 0; i < 4; i++)
        {
            // i: 0 = own cell, 1 = the dx neighbour, 2 = the dz neighbour, 3 = the diagonal
            if (i == 3 && !orthogonalFluid) break;

            var q = p + ((i & 1) != 0 ? dx : 0) + ((i & 2) != 0 ? dz : 0);
            var id = padded[q];

            if (BlockType.IsFluid(id))
            {
                if (i is 1 or 2) orthogonalFluid = true;

                // A full column touching this corner pins it to the cell top
                if (BlockType.IsFluid(padded[q + FaceNeighborDelta[4]])) return 1f;

                var h = FluidHeight(padded, paddedState, q);
                if (h >= 0.8f)
                {
                    total += h * 10f;
                    weight += 10f;
                }
                else
                {
                    total += h;
                    weight += 1f;
                }
            }
            else if (!BlockType.Occludes(id))
            {
                weight += 1f;
            }
        }

        return total / weight;
    }

    /// <summary>
    /// Emits the 1-3 ankle-high stones sharing a pebble cell: floor-sitting cubes sized and
    /// spun per stone (see PebbleGenerator), with the stone tile cropped to their size so
    /// texel density matches full cubes. UVs come from the unrotated corners, so the texture
    /// stays put while the geometry spins. The bottom face is skipped: it sits coplanar on
    /// the ground's top face, which still draws because pebbles never occlude.
    /// </summary>
    private void EmitPebbles(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        byte[] padded, byte[] paddedState, int pIdx, byte[] sunLight, byte[] blockLight,
        int x, int y, int z, BlockType type, Vector3 basePos)
    {
        var worldX = (int)Position.X * Size + x;
        var worldZ = (int)Position.Z * Size + z;
        var maxCount = type.StackMax >= 2 ? type.StackMax : 3;
        var count = PebbleGenerator.CountWithState(worldX, worldZ, paddedState[pIdx], maxCount);
        var obsidian = PebbleGenerator.ObsidianCountWithState(worldX, worldZ, paddedState[pIdx], maxCount);
        var explicitPile = PebbleGenerator.TryDecode(paddedState[pIdx], maxCount, out _, out _);
        var obsidianType = BlockType.ById[PebbleGenerator.ObsidianBlock];
        var variant = PebbleGenerator.VariantAt(worldX, worldZ);

        Span<FaceLight> lights = stackalloc FaceLight[6];
        var lightBase = LightIdx(x, y, z);
        for (var i = 0; i < 6; i++)
        {
            // A face backed against rock or wood (a slope, a trunk) must not sample the
            // neighbour's zero light, or it renders black: light it from the pebble's own
            // cell instead, which sits open to the sky above the ground it rests on.
            lights[i] = EmitterFaceLight(i, padded, pIdx, sunLight, blockLight, lightBase, lightBase);
        }

        for (var b = 0; b < count; b++)
        {
            PebbleGenerator.GetCenter(count, variant, b, out var cx, out var cz);
            var s = PebbleGenerator.SizeAt(worldX, worldZ, b);
            var angle = PebbleGenerator.AngleAt(worldX, worldZ, b);
            var cos = MathF.Cos(angle);
            var sin = MathF.Sin(angle);
            var half = s * 0.5f;
            var min = new Vector3(cx - half, 0f, cz - half);
            var max = new Vector3(cx + half, s, cz + half);
            for (var face = 0; face < 6; face++)
            {
                if (face == 5) continue;
                var isObsidian = explicitPile ? b < obsidian : PebbleGenerator.IsObsidianAt(worldX, worldZ, b);
                var tile = face == 4
                    ? (isObsidian ? obsidianType?.Top ?? type.Top : type.Top)
                    : (isObsidian ? obsidianType?.Side ?? type.Side : type.Side);
                BoxFaceCorners(min, max, face, out var p0, out var p1, out var p2, out var p3);
                var normal = CubeFaces[face].normal;
                EmitQuad(vertices, indices, ref indexOffset, basePos,
                    SpinY(p0, cx, cz, cos, sin), SpinY(p1, cx, cz, cos, sin),
                    SpinY(p2, cx, cz, cos, sin), SpinY(p3, cx, cz, cos, sin),
                    SpinY(normal, 0f, 0f, cos, sin),
                    CropUv(face, p0), CropUv(face, p1), CropUv(face, p2), CropUv(face, p3), tile,
                    ShadeLut[(face << 2) | 3], lights[face]);
            }
        }
    }

    /// <summary>
    /// Emits the 1-4 crossed sticks sharing a branch-pile cell: long thin logs laid across
    /// each other campfire-style (see BranchPileGenerator), bark on the long faces and the
    /// log-top rings on the cut ends, each spun about Y by its pile yaw. UVs come from the
    /// unrotated corners, so the texture stays put while the geometry spins. The bottom face
    /// is skipped: the lowest stick sits coplanar on the ground's top face, which still draws
    /// because branch piles never occlude.
    /// </summary>
    private void EmitBranchPile(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        byte[] padded, byte[] paddedState, int pIdx, byte[] sunLight, byte[] blockLight,
        int x, int y, int z, BlockType type, Vector3 basePos)
    {
        var worldX = (int)Position.X * Size + x;
        var worldZ = (int)Position.Z * Size + z;
        var maxCount = type.StackMax >= 2 ? type.StackMax : 4;
        var count = BranchPileGenerator.CountWithState(paddedState[pIdx], maxCount);
        var variant = BranchPileGenerator.VariantAt(worldX, worldZ);

        Span<FaceLight> lights = stackalloc FaceLight[6];
        var lightBase = LightIdx(x, y, z);
        for (var i = 0; i < 6; i++)
            lights[i] = EmitterFaceLight(i, padded, pIdx, sunLight, blockLight, lightBase, lightBase);

        for (var b = 0; b < count; b++)
        {
            BranchPileGenerator.GetCenter(worldX, worldZ, variant, b, out var cx, out var cz);
            var half = BranchPileGenerator.LengthAt(worldX, worldZ, b) * 0.5f;
            var halfThick = BranchPileGenerator.Thickness * 0.5f;
            var yaw = BranchPileGenerator.YawAt(worldX, worldZ, b);
            var cos = MathF.Cos(yaw);
            var sin = MathF.Sin(yaw);
            var y0 = BranchPileGenerator.LayerY(b);
            var min = new Vector3(cx - half, y0, cz - halfThick);
            var max = new Vector3(cx + half, y0 + BranchPileGenerator.Thickness, cz + halfThick);
            for (var face = 0; face < 6; face++)
            {
                if (face == 5) continue;
                var tile = face is 2 or 3 ? type.Top : type.Side;
                BoxFaceCorners(min, max, face, out var p0, out var p1, out var p2, out var p3);
                var normal = CubeFaces[face].normal;
                EmitQuad(vertices, indices, ref indexOffset, basePos,
                    SpinY(p0, cx, cz, cos, sin), SpinY(p1, cx, cz, cos, sin),
                    SpinY(p2, cx, cz, cos, sin), SpinY(p3, cx, cz, cos, sin),
                    SpinY(normal, 0f, 0f, cos, sin),
                    CropUv(face, p0), CropUv(face, p1), CropUv(face, p2), CropUv(face, p3), tile,
                    ShadeLut[(face << 2) | 3], lights[face]);
            }
        }
    }

    /// <summary>
    /// Emits a snow layer cell as one box from the floor up to its depth (see
    /// <see cref="SnowShape"/>): the state byte holds 1..StackMax layers and the box is that
    /// fraction of a block tall, so a full stack is a full cube. The bottom face is skipped unless
    /// the cell below cannot occlude, since a layer normally rests on ground that draws its own top.
    /// </summary>
    private void EmitSnowLayers(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        byte[] padded, byte[] paddedState, int pIdx, byte[] sunLight, byte[] blockLight,
        int x, int y, int z, BlockType type, Vector3 basePos)
    {
        var worldX = (int)Position.X * Size + x;
        var worldZ = (int)Position.Z * Size + z;
        var maxCount = type.StackMax >= 2 ? type.StackMax : 16;
        var count = SnowShape.CountWithState(worldX, worldZ, paddedState[pIdx], maxCount);
        var height = SnowShape.Height(count, maxCount);

        Span<FaceLight> lights = stackalloc FaceLight[6];
        var lightBase = LightIdx(x, y, z);
        for (var i = 0; i < 6; i++)
            lights[i] = EmitterFaceLight(i, padded, pIdx, sunLight, blockLight, lightBase, lightBase);

        var drawBottom = count >= maxCount && !BlockType.Occludes(padded[pIdx + FaceNeighborDelta[5]]);
        var min = new Vector3(0f, 0f, 0f);
        var max = new Vector3(1f, height, 1f);
        for (var face = 0; face < 6; face++)
        {
            if (face == 5 && !drawBottom) continue;
            var tile = face == 4 ? type.Top : face == 5 ? type.Bottom : type.Side;
            BoxFaceCorners(min, max, face, out var p0, out var p1, out var p2, out var p3);
            EmitQuad(vertices, indices, ref indexOffset, basePos, p0, p1, p2, p3,
                CubeFaces[face].normal,
                CropUv(face, p0), CropUv(face, p1), CropUv(face, p2), CropUv(face, p3), tile,
                ShadeLut[(face << 2) | 3], lights[face]);
        }
    }

    /// <summary>Rotates a point about (cx, cz) by a Y spin; normals pass through with cx = cz = 0.</summary>
    private static Vector3 SpinY(Vector3 p, float cx, float cz, float cos, float sin)
    {
        var dx = p.X - cx;
        var dz = p.Z - cz;
        return new Vector3(cx + dx * cos + dz * sin, p.Y, cz - dx * sin + dz * cos);
    }

    /// <summary>
    /// Emits a thin <see cref="BlockRenderer.TreeWood"/> cell: a centred node box sized by its
    /// <see cref="WoodState"/> thickness and one square tube toward every neighbour it connects
    /// to (<see cref="TreeWoodShape.Connects"/>), at the thinner of the two widths, plus a
    /// same-width plug toward every solid occluding neighbour (<see cref="TreeWoodShape.PlugMask"/>)
    /// so a root or trunk on the ground meets it. Tubes and plugs carry
    /// only their four walls - the neighbour emits the matching tube from its side, so the two
    /// meet flush with no cap between them - and a node face is dropped where a tube or plug of its
    /// own full width leaves it. The geometry is exactly <see cref="TreeWoodShape.GetBoxes"/>,
    /// so the hitboxes cover what is drawn. UVs are cropped (<see cref="CropUv"/>), so bark
    /// keeps the atlas texel density at every thickness.
    /// </summary>
    private static void EmitTreeWood(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        byte[] padded, byte[] paddedState, int pIdx, byte[] sunLight, byte[] blockLight,
        int x, int y, int z, BlockType type, Vector3 basePos)
    {
        var state = paddedState[pIdx];
        Span<byte> nbIds = stackalloc byte[6];
        Span<byte> nbStates = stackalloc byte[6];
        Span<FaceLight> lights = stackalloc FaceLight[6];
        var lightBase = LightIdx(x, y, z);
        for (var i = 0; i < 6; i++)
        {
            nbIds[i] = padded[pIdx + FaceNeighborDelta[i]];
            nbStates[i] = paddedState[pIdx + FaceNeighborDelta[i]];
            // Backed against an occluder, the cell's own light (like pebbles), so a limb
            // pressed against rock never goes black
            lights[i] = EmitterFaceLight(i, padded, pIdx, sunLight, blockLight, lightBase, lightBase);
        }

        var mask = TreeWoodShape.ConnectionMask(type.Id, state, nbIds, nbStates);
        var plugs = TreeWoodShape.PlugMask(state, nbIds);
        var half = TreeWoodShape.HalfWidth(WoodState.Thickness(state));
        TreeWoodShape.NodeBox(half, out var nodeMin, out var nodeMax);
        for (var face = 0; face < 6; face++)
        {
            // A tube or plug as wide as the node continues its face, so that face is its mouth
            if ((plugs & 1 << face) != 0) continue;
            if ((mask & 1 << face) != 0 && TreeWoodShape.TubeHalf(state, nbStates[face]) >= half) continue;
            var tile = face == 4 ? type.Top : face == 5 ? type.Bottom : type.Side;
            EmitBoxFace(vertices, indices, ref indexOffset, basePos, nodeMin, nodeMax, face,
                tile, ShadeLut[(face << 2) | 3], lights[face]);
        }

        for (var dir = 0; dir < 6; dir++)
        {
            var connected = (mask & 1 << dir) != 0;
            if (!connected && (plugs & 1 << dir) == 0) continue;
            var tubeHalf = connected ? TreeWoodShape.TubeHalf(state, nbStates[dir]) : half;
            TreeWoodShape.TubeBox(dir, half, tubeHalf, out var min, out var max);
            var opposite = TreeWoodShape.Opposite(dir);
            for (var face = 0; face < 6; face++)
            {
                if (face == dir || face == opposite) continue;
                EmitBoxFace(vertices, indices, ref indexOffset, basePos, min, max, face,
                    type.Side, ShadeLut[(face << 2) | 3], lights[face]);
            }
        }
    }

    private const int RimPx = WoodState.PixelsPerBlock;

    // Upper bound on the quads one bevelled shell emits: each cap is at most two segments per
    // pixel row, each box at most two X walls, and each Z boundary at most four wall segments
    private const int MaxRimQuads = 4 * RimPx + 6 * TreeWoodShape.MaxRimBoxes;

    /// <summary>
    /// <see cref="TreeWoodShape.RimCornerMask"/> of the cell at padded index
    /// <paramref name="idx"/>, read out of the snapshot. Only the four horizontal neighbours are
    /// read (the diagonals never change a cell's bevel, because a cell only ever carves its own
    /// material), so <paramref name="idx"/> must be a cell whose horizontal neighbours lie inside
    /// the 18^3 snapshot: the meshed cell itself or its vertical neighbours.
    /// </summary>
    private static int RimMaskInSnapshot(byte[] padded, byte[] paddedState, int idx, byte id)
    {
        if (!TreeWoodShape.CanBevel(id, paddedState[idx])) return 0;
        Span<byte> ids = stackalloc byte[6];
        Span<byte> states = stackalloc byte[6];
        for (var i = 0; i < 4; i++)
        {
            ids[i] = padded[idx + FaceNeighborDelta[i]];
            states[i] = paddedState[idx + FaceNeighborDelta[i]];
        }

        return TreeWoodShape.RimCornerMask(id, paddedState[idx], ids, states);
    }

    /// <summary>
    /// Whether the full-cube <paramref name="id"/> cell at padded index <paramref name="nbIdx"/>
    /// fills its whole face toward <paramref name="side"/> (the direction from it to the viewing
    /// cell): <see cref="TreeWoodShape.RimSideFull"/> of its rim mask, read without leaving the
    /// snapshot. A side face is carved only at a corner whose two edge neighbours are both
    /// missing, and the viewing cell is one of the two for both corners on that side, so the
    /// neighbour's far side is never read; a cap needs the whole mask, whose horizontal reads
    /// stay inside the snapshot for a vertical neighbour.
    /// </summary>
    private static bool RimFaceFull(byte[] padded, byte[] paddedState, int nbIdx, byte id, int side)
    {
        if (!TreeWoodShape.CanBevel(id, paddedState[nbIdx])) return true;
        if (side >= 4) return RimMaskInSnapshot(padded, paddedState, nbIdx, id) == 0;
        var toward = nbIdx + FaceNeighborDelta[side];
        if (TreeWoodShape.IsRimMass(id, padded[toward], paddedState[toward])) return true;
        // The two edge neighbours across the other axis: X for a Z side, Z for an X side
        var across = side < 2 ? 2 : 0;
        var a = nbIdx + FaceNeighborDelta[across];
        var b = nbIdx + FaceNeighborDelta[across + 1];
        return TreeWoodShape.IsRimMass(id, padded[a], paddedState[a])
               && TreeWoodShape.IsRimMass(id, padded[b], paddedState[b]);
    }

    /// <summary>
    /// Whether the rim cell at <paramref name="pIdx"/> hides its cell-boundary face toward
    /// side <paramref name="face"/>: an occluder does, and so does a same-wood full cube whose
    /// face toward this cell is whole (<see cref="RimFaceFull"/>; this cell is rim mass for it,
    /// so a horizontal one always is, and a detached log is never carved).
    /// </summary>
    private static bool RimBoundaryHidden(byte[] padded, byte[] paddedState, int pIdx, byte id, int face)
    {
        var nbIdx = pIdx + FaceNeighborDelta[face];
        var nb = padded[nbIdx];
        if (BlockType.Occludes(nb)) return true;
        return nb == id && WoodState.IsFullCube(paddedState[nbIdx])
               && RimFaceFull(padded, paddedState, nbIdx, id, TreeWoodShape.Opposite(face));
    }

    // Per Z pixel row, the covered X pixel span [lo, hi) of a rim cell's boxes
    private static void RimRows(ReadOnlySpan<BlockHitbox> boxes, Span<int> lo, Span<int> hi)
    {
        foreach (var box in boxes)
        {
            var z0 = (int)MathF.Round(box.Min.Z * RimPx);
            var z1 = (int)MathF.Round(box.Max.Z * RimPx);
            var x0 = (int)MathF.Round(box.Min.X * RimPx);
            var x1 = (int)MathF.Round(box.Max.X * RimPx);
            for (var z = z0; z < z1; z++)
            {
                lo[z] = x0;
                hi[z] = x1;
            }
        }
    }

    /// <summary>
    /// Emits a bevelled pixel-rim trunk shell (<see cref="TreeWoodShape.CanBevel"/> with a
    /// non-zero corner <paramref name="mask"/>): the full-height <see cref="TreeWoodShape.RimBoxes"/>
    /// stair-stepping each carved corner, which are exactly the cell's hitboxes, so the raycast,
    /// the outline and collision match what is drawn. Faces on the cell boundary cull like a
    /// cube's (<see cref="RimBoundaryHidden"/>); the X walls inside the cell and the Z steps
    /// between two boxes are always drawn, the steps only where one box sticks out past its
    /// neighbour. Top and bottom draw only the part of the footprint that the same-wood cell
    /// above or below does not cover, so a stack of equal bevels shows no buried caps and a
    /// shell under a narrower one keeps its exposed rim.
    /// </summary>
    private static void EmitRimWood(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        byte[] padded, byte[] paddedState, int pIdx, byte[] sunLight, byte[] blockLight,
        int x, int y, int z, BlockType type, Vector3 basePos, int mask)
    {
        if (vertices.Count > ushort.MaxValue - 4 * MaxRimQuads) return;
        var id = type.Id;
        Span<FaceLight> lights = stackalloc FaceLight[6];
        var lightBase = LightIdx(x, y, z);
        for (var i = 0; i < 6; i++)
            lights[i] = EmitterFaceLight(i, padded, pIdx, sunLight, blockLight, lightBase, lightBase);

        Span<BlockHitbox> boxes = stackalloc BlockHitbox[TreeWoodShape.MaxRimBoxes];
        var count = TreeWoodShape.RimBoxes(mask, boxes);
        boxes = boxes[..count];
        Span<int> rowLo = stackalloc int[RimPx];
        Span<int> rowHi = stackalloc int[RimPx];
        RimRows(boxes, rowLo, rowHi);

        // Top and bottom: this cell's rows minus the rows of the same-wood cell beyond the cap
        Span<BlockHitbox> coverBoxes = stackalloc BlockHitbox[TreeWoodShape.MaxRimBoxes];
        Span<int> coverLo = stackalloc int[RimPx];
        Span<int> coverHi = stackalloc int[RimPx];
        for (var face = 4; face <= 5; face++)
        {
            var nbIdx = pIdx + FaceNeighborDelta[face];
            var nb = padded[nbIdx];
            if (BlockType.Occludes(nb)) continue;
            var covered = false;
            if (nb == id && WoodState.IsFullCube(paddedState[nbIdx]))
            {
                var coverMask = RimMaskInSnapshot(padded, paddedState, nbIdx, id);
                if (coverMask == 0) continue; // a whole cube face buries the cap entirely
                RimRows(coverBoxes[..TreeWoodShape.RimBoxes(coverMask, coverBoxes)], coverLo, coverHi);
                covered = true;
            }

            var tile = face == 4 ? type.Top : type.Bottom;
            var shade = ShadeLut[(face << 2) | 3];
            var runStart = 0;
            int a0 = 0, a1 = 0, b0 = 0, b1 = 0;
            for (var row = 0; row <= RimPx; row++)
            {
                int s0 = 0, s1 = 0, t0 = 0, t1 = 0;
                if (row < RimPx)
                {
                    var lo = rowLo[row];
                    var hi = rowHi[row];
                    if (!covered)
                    {
                        s0 = lo;
                        s1 = hi;
                    }
                    else
                    {
                        // Up to two exposed segments: left of the cover and right of it
                        s0 = lo;
                        s1 = Math.Min(hi, coverLo[row]);
                        t0 = Math.Max(lo, coverHi[row]);
                        t1 = hi;
                    }

                    if (s1 <= s0) s0 = s1 = 0;
                    if (t1 <= t0) t0 = t1 = 0;
                    if (row > 0 && s0 == a0 && s1 == a1 && t0 == b0 && t1 == b1) continue;
                }

                if (row > 0)
                {
                    var z0 = runStart / (float)RimPx;
                    var z1 = row / (float)RimPx;
                    if (a1 > a0)
                        EmitBoxFace(vertices, indices, ref indexOffset, basePos,
                            new Vector3(a0 / (float)RimPx, 0f, z0), new Vector3(a1 / (float)RimPx, 1f, z1),
                            face, tile, shade, lights[face]);
                    if (b1 > b0)
                        EmitBoxFace(vertices, indices, ref indexOffset, basePos,
                            new Vector3(b0 / (float)RimPx, 0f, z0), new Vector3(b1 / (float)RimPx, 1f, z1),
                            face, tile, shade, lights[face]);
                }

                runStart = row;
                a0 = s0;
                a1 = s1;
                b0 = t0;
                b1 = t1;
            }
        }

        // X walls: a wall on the cell boundary culls like a cube face, one inside never does
        var hideMinX = RimBoundaryHidden(padded, paddedState, pIdx, id, 2);
        var hideMaxX = RimBoundaryHidden(padded, paddedState, pIdx, id, 3);
        var sideShadeMinX = ShadeLut[(2 << 2) | 3];
        var sideShadeMaxX = ShadeLut[(3 << 2) | 3];
        foreach (var box in boxes)
        {
            if (box.Min.X > 0f || !hideMinX)
                EmitBoxFace(vertices, indices, ref indexOffset, basePos, box.Min, box.Max, 2,
                    type.Side, sideShadeMinX, lights[2]);
            if (box.Max.X < 1f || !hideMaxX)
                EmitBoxFace(vertices, indices, ref indexOffset, basePos, box.Min, box.Max, 3,
                    type.Side, sideShadeMaxX, lights[3]);
        }

        // Z walls: the first and last are on the cell boundary; between two boxes only the part
        // of each box's wall that sticks out past the other is exposed
        var shadeMinZ = ShadeLut[(0 << 2) | 3];
        var shadeMaxZ = ShadeLut[(1 << 2) | 3];
        if (!RimBoundaryHidden(padded, paddedState, pIdx, id, 0))
            EmitBoxFace(vertices, indices, ref indexOffset, basePos, boxes[0].Min, boxes[0].Max, 0,
                type.Side, shadeMinZ, lights[0]);
        if (!RimBoundaryHidden(padded, paddedState, pIdx, id, 1))
            EmitBoxFace(vertices, indices, ref indexOffset, basePos, boxes[count - 1].Min, boxes[count - 1].Max, 1,
                type.Side, shadeMaxZ, lights[1]);
        for (var i = 0; i + 1 < count; i++)
        {
            var near = boxes[i];
            var far = boxes[i + 1];
            var zb = near.Max.Z;
            // The near box's +Z wall where the far box does not continue it, and vice versa
            EmitWallRemainder(vertices, indices, ref indexOffset, basePos, near.Min.X, near.Max.X,
                far.Min.X, far.Max.X, zb, 1, type.Side, shadeMaxZ, lights[1]);
            EmitWallRemainder(vertices, indices, ref indexOffset, basePos, far.Min.X, far.Max.X,
                near.Min.X, near.Max.X, zb, 0, type.Side, shadeMinZ, lights[0]);
        }
    }

    // A Z wall at depth zb spanning [x0, x1) minus the span [c0, c1) the adjacent box covers:
    // 0, 1 or 2 full-height segments
    private static void EmitWallRemainder(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        Vector3 basePos, float x0, float x1, float c0, float c1, float zb, int face,
        ushort tile, byte shade, in FaceLight light)
    {
        var leftHi = MathF.Min(x1, c0);
        if (leftHi > x0)
            EmitBoxFace(vertices, indices, ref indexOffset, basePos,
                new Vector3(x0, 0f, zb), new Vector3(leftHi, 1f, zb), face, tile, shade, light);
        var rightLo = MathF.Max(x0, c1);
        if (x1 > rightLo)
            EmitBoxFace(vertices, indices, ref indexOffset, basePos,
                new Vector3(rightLo, 0f, zb), new Vector3(x1, 1f, zb), face, tile, shade, light);
    }

    /// <summary>One axis-aligned face of the [min, max] box (cell-local), with flat shading
    /// and cropped UVs (see <see cref="CropUv"/>).</summary>
    private static void EmitBoxFace(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        Vector3 basePos, Vector3 min, Vector3 max, int face,
        ushort tile, byte shade, in FaceLight light)
    {
        BoxFaceCorners(min, max, face, out var a, out var b, out var c, out var d);
        EmitQuad(vertices, indices, ref indexOffset, basePos, a, b, c, d, CubeFaces[face].normal,
            CropUv(face, a), CropUv(face, b), CropUv(face, c), CropUv(face, d), tile,
            shade, light);
    }

    /// <summary>One arbitrary quad: four cell-local corners, one normal, one tile-local UV per
    /// corner, all on atlas slice <paramref name="tile"/>; each corner's light is sampled from
    /// <paramref name="light"/> at its cell-local position.</summary>
    private static void EmitQuad(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        Vector3 basePos, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal,
        Vector2 uva, Vector2 uvb, Vector2 uvc, Vector2 uvd, ushort tile,
        byte shade, in FaceLight light, byte alpha = 255)
    {
        light.Sample(a, out var sa, out var ba, out var ta);
        light.Sample(b, out var sb, out var bb, out var tb);
        light.Sample(c, out var sc, out var bc, out var tc);
        light.Sample(d, out var sd, out var bd, out var td);
        vertices.Add(new Vertex(basePos + a, shade, shade, shade, alpha, uva, tile, sa, ba, normal, tint: ta));
        vertices.Add(new Vertex(basePos + b, shade, shade, shade, alpha, uvb, tile, sb, bb, normal, tint: tb));
        vertices.Add(new Vertex(basePos + c, shade, shade, shade, alpha, uvc, tile, sc, bc, normal, tint: tc));
        vertices.Add(new Vertex(basePos + d, shade, shade, shade, alpha, uvd, tile, sd, bd, normal, tint: td));

        indices.Add((ushort)(indexOffset + 0));
        indices.Add((ushort)(indexOffset + 1));
        indices.Add((ushort)(indexOffset + 2));
        indices.Add((ushort)(indexOffset + 0));
        indices.Add((ushort)(indexOffset + 2));
        indices.Add((ushort)(indexOffset + 3));
        indexOffset += 4;
    }

    /// <summary>Corners of one axis-aligned face of the [min, max] box (cell-local).</summary>
    internal static void BoxFaceCorners(Vector3 min, Vector3 max, int face,
        out Vector3 a, out Vector3 b, out Vector3 c, out Vector3 d)
    {
        (a, b, c, d) = face switch
        {
            0 => (new Vector3(min.X, min.Y, min.Z), new Vector3(min.X, max.Y, min.Z),
                  new Vector3(max.X, max.Y, min.Z), new Vector3(max.X, min.Y, min.Z)),
            1 => (new Vector3(max.X, min.Y, max.Z), new Vector3(max.X, max.Y, max.Z),
                  new Vector3(min.X, max.Y, max.Z), new Vector3(min.X, min.Y, max.Z)),
            2 => (new Vector3(min.X, min.Y, max.Z), new Vector3(min.X, max.Y, max.Z),
                  new Vector3(min.X, max.Y, min.Z), new Vector3(min.X, min.Y, min.Z)),
            3 => (new Vector3(max.X, min.Y, min.Z), new Vector3(max.X, max.Y, min.Z),
                  new Vector3(max.X, max.Y, max.Z), new Vector3(max.X, min.Y, max.Z)),
            4 => (new Vector3(min.X, max.Y, min.Z), new Vector3(min.X, max.Y, max.Z),
                  new Vector3(max.X, max.Y, max.Z), new Vector3(max.X, max.Y, min.Z)),
            _ => (new Vector3(min.X, min.Y, max.Z), new Vector3(min.X, min.Y, min.Z),
                  new Vector3(max.X, min.Y, min.Z), new Vector3(max.X, min.Y, max.Z)),
        };
    }

    /// <summary>
    /// Tile-local UV of a point on an axis-aligned quad, in tile units. This is the one
    /// definition of how each face orients its texture: a full cube face maps its 0..1 cell to
    /// 0..1 on the tile, a sub-cell box (<see cref="CropUv"/>) samples the matching crop at the
    /// same texel density, and a quad merged over <paramref name="extent"/> cells runs 0..W and
    /// 0..H so the sampler's REPEAT tiles it. Per face, U runs along X or Z and V along Y (Z on
    /// top and bottom), mirrored where Minecraft's convention runs the other way — the same
    /// mirror <see cref="ModelBaker"/> applies, so baked cubes, procedural boxes and merged quads
    /// all sample identically and bark continues across log/branch joints at one scale.
    /// </summary>
    /// <param name="local">Point on the quad, relative to the quad's minimum corner.</param>
    /// <param name="extent">Size of the quad along each axis (1 on its plane axis).</param>
    internal static Vector2 TiledUv(int face, Vector3 local, Vector3 extent)
    {
        float u = face switch
        {
            0 => local.X,
            1 => extent.X - local.X,
            2 => extent.Z - local.Z,
            3 => local.Z,
            _ => local.X, // top and bottom run along X
        };
        float v = face switch
        {
            4 => extent.Z - local.Z, // top runs along Z
            5 => local.Z,            // bottom runs along Z
            _ => extent.Y - local.Y, // sides run bottom-to-top
        };
        return new Vector2(u, v);
    }

    /// <summary>Tile-local UV under a cell-local point of a sub-cell box (see <see cref="TiledUv"/>).</summary>
    internal static Vector2 CropUv(int face, Vector3 local) => TiledUv(face, local, Vector3.One);

    /// <summary>
    /// Local <see cref="Idx"/> indices of the exposed hot-fluid cells of the last applied mesh
    /// (ember/smoke spawn points), or null when there are none. Main thread only.
    /// </summary>
    public ushort[]? HotFluidVents { get; private set; }

    /// <summary>
    /// Uploads a finished mesh into the shared chunk geometry pools, releasing whatever this
    /// chunk held before. The upload is a synchronous <c>glBufferSubData</c>, so the caller may
    /// recycle <paramref name="meshData"/>'s lists the moment this returns. An empty list never
    /// touches a pool at all, which is what lets the headless tests pass <c>null</c> for both.
    /// Main thread only, like every other GL call.
    /// </summary>
    public void UpdateMesh(ChunkMeshData meshData, ChunkGeometryPool? opaquePool,
        ChunkGeometryPool? translucentPool)
    {
        IsMeshed = true;
        PublishLight(meshData.Light);
        ReleaseSlices();

        if (meshData.Vertices.Count > 0)
        {
            _opaquePool = opaquePool
                ?? throw new InvalidOperationException("non-empty chunk mesh without a geometry pool");
            OpaqueSlice = _opaquePool.Upload(CollectionsMarshal.AsSpan(meshData.Vertices),
                CollectionsMarshal.AsSpan(meshData.Indices));
        }

        if (meshData.TranslucentVertices.Count > 0)
        {
            _translucentPool = translucentPool
                ?? throw new InvalidOperationException("non-empty chunk mesh without a geometry pool");
            TranslucentSlice = _translucentPool.Upload(CollectionsMarshal.AsSpan(meshData.TranslucentVertices),
                CollectionsMarshal.AsSpan(meshData.TranslucentIndices));
        }

        HasWaterMesh = meshData.HasWater && meshData.TranslucentVertices.Count > 0;
        HotFluidVents = meshData.HotFluidVents;
    }

    /// <summary>
    /// Headless (no geometry pool) twin of <see cref="UpdateMesh"/>: marks the chunk meshed and
    /// publishes its light and fluid vents, but drops the vertex data instead of uploading it.
    /// Used only by a server world, whose chunks are never drawn, so the usual
    /// non-empty-mesh-without-a-pool throw never applies.
    /// </summary>
    public void MarkMeshedHeadless(ChunkMeshData meshData)
    {
        IsMeshed = true;
        PublishLight(meshData.Light);
        HasWaterMesh = meshData.HasWater && meshData.TranslucentVertices.Count > 0;
        HotFluidVents = meshData.HotFluidVents;
    }

    /// <summary>
    /// Hands both slices back to the pools they came from and forgets them. Freeing twice
    /// throws inside the allocator, so each slice is nulled the moment it is released.
    /// </summary>
    private void ReleaseSlices()
    {
        if (OpaqueSlice is { } opaque)
        {
            _opaquePool?.Free(opaque);
            OpaqueSlice = null;
        }
        _opaquePool = null;

        if (TranslucentSlice is { } translucent)
        {
            _translucentPool?.Free(translucent);
            TranslucentSlice = null;
        }
        _translucentPool = null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int VertexAO(bool side1, bool side2, bool corner)
    {
        if (side1 && side2) return 0;
        return 3 - ((side1 ? 1 : 0) + (side2 ? 1 : 0) + (corner ? 1 : 0));
    }

    public void Dispose()
    {
        ReleaseSlices();
        HasWaterMesh = false;
        GC.SuppressFinalize(this);
    }

    internal static readonly (Vector3 normal, Vector3[] face)[] CubeFaces =
    [
       (new Vector3(0,  0, -1), [CubeVertexPositions.BackLeftBottom, CubeVertexPositions.BackLeftTop, CubeVertexPositions.BackRightTop, CubeVertexPositions.BackRightBottom]),
       (new Vector3(0,  0,  1), [CubeVertexPositions.FrontRightBottom, CubeVertexPositions.FrontRightTop, CubeVertexPositions.FrontLeftTop, CubeVertexPositions.FrontLeftBottom]),
       (new Vector3(-1, 0,  0), [CubeVertexPositions.FrontLeftBottom, CubeVertexPositions.FrontLeftTop, CubeVertexPositions.BackLeftTop, CubeVertexPositions.BackLeftBottom]),
       (new Vector3(1,  0,  0), [CubeVertexPositions.BackRightBottom, CubeVertexPositions.BackRightTop, CubeVertexPositions.FrontRightTop, CubeVertexPositions.FrontRightBottom]),
       (new Vector3(0,  1,  0), [CubeVertexPositions.BackLeftTop, CubeVertexPositions.FrontLeftTop, CubeVertexPositions.FrontRightTop, CubeVertexPositions.BackRightTop]),
       (new Vector3(0, -1,  0), [CubeVertexPositions.FrontLeftBottom, CubeVertexPositions.BackLeftBottom, CubeVertexPositions.BackRightBottom, CubeVertexPositions.FrontRightBottom])
    ];

    private static readonly Vector3[][][] AONeighbors =
    [
        [
            [Vector3Directions.LeftBackward, Vector3Directions.DownBackward, Vector3Directions.DownLeftBackward],
            [Vector3Directions.LeftBackward, Vector3Directions.UpBackward, Vector3Directions.UpLeftBackward],
            [Vector3Directions.RightBackward, Vector3Directions.UpBackward, Vector3Directions.UpRightBackward],
            [Vector3Directions.RightBackward, Vector3Directions.DownBackward, Vector3Directions.DownRightBackward],
        ],
        [
            [Vector3Directions.RightForward, Vector3Directions.DownForward, Vector3Directions.DownRightForward],
            [Vector3Directions.RightForward, Vector3Directions.UpForward, Vector3Directions.UpRightForward],
            [Vector3Directions.LeftForward, Vector3Directions.UpForward, Vector3Directions.UpLeftForward],
            [Vector3Directions.LeftForward, Vector3Directions.DownForward, Vector3Directions.DownLeftForward],
        ],
        [
            [Vector3Directions.DownLeft, Vector3Directions.LeftForward, Vector3Directions.DownLeftForward],
            [Vector3Directions.UpLeft, Vector3Directions.LeftForward, Vector3Directions.UpLeftForward],
            [Vector3Directions.UpLeft, Vector3Directions.LeftBackward, Vector3Directions.UpLeftBackward],
            [Vector3Directions.DownLeft, Vector3Directions.LeftBackward, Vector3Directions.DownLeftBackward],
        ],
        [
            [Vector3Directions.DownRight, Vector3Directions.RightBackward, Vector3Directions.DownRightBackward],
            [Vector3Directions.UpRight, Vector3Directions.RightBackward, Vector3Directions.UpRightBackward],
            [Vector3Directions.UpRight, Vector3Directions.RightForward, Vector3Directions.UpRightForward],
            [Vector3Directions.DownRight, Vector3Directions.RightForward, Vector3Directions.DownRightForward],
        ],
        [
            [Vector3Directions.UpLeft, Vector3Directions.UpBackward, Vector3Directions.UpLeftBackward],
            [Vector3Directions.UpLeft, Vector3Directions.UpForward, Vector3Directions.UpLeftForward],
            [Vector3Directions.UpRight, Vector3Directions.UpForward, Vector3Directions.UpRightForward],
            [Vector3Directions.UpRight, Vector3Directions.UpBackward, Vector3Directions.UpRightBackward],
        ],
        [
            [Vector3Directions.DownLeft, Vector3Directions.DownForward, Vector3Directions.DownLeftForward],
            [Vector3Directions.DownLeft, Vector3Directions.DownBackward, Vector3Directions.DownLeftBackward],
            [Vector3Directions.DownRight, Vector3Directions.DownBackward, Vector3Directions.DownRightBackward],
            [Vector3Directions.DownRight, Vector3Directions.DownForward, Vector3Directions.DownRightForward],
        ]
    ];
}

/// <summary>
/// CPU-side mesh of one chunk, produced off the main thread. The opaque lists become the
/// chunk's normal mesh; the translucent ones its fluid mesh, drawn blended after everything
/// opaque so the sea floor shows through the water. <paramref name="HasWater"/> says whether
/// any of those translucent quads are fluid — glass lands in the same lists.
/// </summary>
public record ChunkMeshData(List<Vertex> Vertices, List<ushort> Indices,
    List<Vertex> TranslucentVertices, List<ushort> TranslucentIndices, bool HasWater)
{
    /// <summary>
    /// The chunk's own 16^3 cells of the light flood this mesh was built with, packed
    /// <c>sun &lt;&lt; 4 | block</c> (levels 0..15) in <see cref="Chunk.Idx"/> order, or null
    /// when the build skipped the light flood (all-air or fully enclosed).
    /// </summary>
    public byte[]? Light { get; init; }

    /// <summary>
    /// Local <see cref="Chunk.Idx"/> indices of the chunk's exposed hot-fluid cells
    /// (<see cref="Chunk.IsHotFluidVent"/>), or null when there are none.
    /// </summary>
    public ushort[]? HotFluidVents { get; init; }
}
