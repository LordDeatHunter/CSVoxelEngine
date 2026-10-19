using System;
using Silk.NET.OpenGL;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VoxelEngine.Chunks.Meshing;
using VoxelEngine.Chunks.Sources;
using VoxelEngine.Data;
using VoxelEngine.Effects.Climate;
using VoxelEngine.Effects.Lighting;
using VoxelEngine.Interaction;
using VoxelEngine.Rendering.Geometry;
using VoxelEngine.Saves;
using VoxelEngine.Simulation;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Chunks;

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
    internal const int PaddedCount = Padded * PaddedSq;

    // Voxel light travels up to LightPropagationRadius blocks through air, so it is
    // computed over a wider volume centered on this chunk: a lamp just across the
    // border must still light this chunk's faces. The 3x3x3 neighbor chunks cover it
    // (LightBorder < Size). A padded-snapshot index (px,py,pz) maps to light index
    // (px+LightOffset, py+LightOffset, pz+LightOffset).
    internal const int LightBorder = LightingConfig.LightPropagationRadius;
    internal const int LightSize = Size + LightBorder * 2;
    internal const int LightSq = LightSize * LightSize;
    internal const int LightCount = LightSize * LightSq;
    internal const int LightOffset = LightBorder - 1;

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

    /// <summary>The block ids in <see cref="Idx"/> order, read and written in place by the worldgen fill
    /// (<see cref="ChunkTerrainFill"/>) and read by the mesh build (<see cref="ChunkMeshBuilder"/>); an edit
    /// never writes through it but goes through <see cref="SetBlockRaw"/>.</summary>
    internal byte[] Blocks => _blocks;

    /// <summary>One plain read or write of the volatile state array (null = all zero), for the worldgen
    /// fill's round trip around the tree stamps and the mesh build's once-per-chunk read.</summary>
    internal byte[]? StateArray { get => _state; set => _state = value; }

    // Counts non-air cells, water included: its only consumer is the all-air mesh fast path,
    // and a chunk holding nothing but sea water still has a surface to draw.
    private int _solidCount;

    /// <summary>
    /// The non-air cell count (see <c>_solidCount</c>): recounted by the worldgen fill, read by
    /// the mesh build's all-air fast path and by ChunkManager, and pinned by a test against a
    /// scalar oracle walking <see cref="GetLocalBlock"/>.
    /// </summary>
    internal int SolidCount { get => _solidCount; set => _solidCount = value; }

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
    /// surfaces live apart from the opaque geometry so the terrain passes can draw them blended,
    /// after everything opaque (see TerrainPasses.RenderWorldTranslucent).
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
    /// planar reflection and the scene color/depth copies — keys on this instead.
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
    public int SurfaceMinY { get; internal set; } = int.MinValue;

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
    /// level (only ever poured) wherever it has a replaceable neighbor. Written once by the
    /// generation worker before <see cref="IsBlockDataReady"/> goes true and read once on the
    /// main thread afterwards, so it needs no locking.
    /// </summary>
    internal ushort[]? FluidFrontier { get; set; }

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
    /// Whether this chunk's blocks came from a snapshot (<see cref="RestoreFrom"/>) — a save, or
    /// a world source that supplies chunks itself (<see cref="IWorldSource.Supplies"/>) — instead
    /// of worldgen. A restored chunk can hold fluid anywhere (poured water, or an imported map's),
    /// which is what makes the fluid wake scan its borders whatever height it sits at, and its
    /// <see cref="RestoredPlayerPlaced"/> may be null. Restoring never sets
    /// <see cref="ModifiedSinceSave"/>; only the raw writers do.
    /// </summary>
    public bool IsRestored { get; private set; }

    /// <summary>
    /// The player-placed cells (chunk-local <see cref="Idx"/> indices) a restored chunk brought
    /// back that the chunk manager has not yet adopted into its own player-placed set, or null.
    /// Set by <see cref="RestoreFrom"/> on the generation worker, cleared on the main thread
    /// once adopted.
    /// </summary>
    internal ushort[]? RestoredPlayerPlaced { get; set; }

    /// <summary>Lamp level bits of a <c>blockLight</c> byte (see <see cref="ChunkLighting.ComputeLight"/>).</summary>
    internal const int BlockLevelMask = 15;



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
    /// Fills this fresh chunk's blocks from worldgen (<see cref="ChunkTerrainFill.GenerateBlockData"/>).
    /// Runs on a generation worker thread.
    /// </summary>
    public void GenerateBlockData() => ChunkTerrainFill.GenerateBlockData(this);

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
    /// color slot (<see cref="BlockLightPalette"/>). Only a chunk that had a tinted seed publishes
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
        if (CellWritten is { } written)
            written((int)Position.X * Size + x, (int)Position.Y * Size + y, (int)Position.Z * Size + z, id, state);
        return previous;
    }

    /// <summary>
    /// A server sets this to observe every raw cell write (an edit or a simulation step) in world
    /// coordinates, so it can broadcast the change to clients. Null on a client and in singleplayer;
    /// worldgen never fires it, because it writes the block array directly.
    /// </summary>
    public static Action<int, int, int, byte, byte>? CellWritten { get; set; }

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
    /// A snapshot of this chunk's blocks and state for the network, without
    /// <see cref="TakeSnapshot"/>'s save-dirty side effect: a server hands chunks to clients
    /// without pretending it just saved them. Main thread, like every read of the arrays.
    /// </summary>
    public ChunkSnapshot CaptureForNetwork() => new(_blocks, _state, playerPlaced: null);

    /// <summary>
    /// Fills this chunk from a save instead of generating it: the blocks, the state bytes and
    /// the player-placed cells (handed to the chunk manager through
    /// <see cref="RestoredPlayerPlaced"/>) come from <paramref name="snapshot"/>, and everything
    /// <see cref="GenerateBlockData"/> derives on the way — the non-air count, the surface
    /// floor and the fluid frontier — is recomputed from them. Runs on a generation worker on a
    /// chunk built with <c>generateBlockData: false</c>, before any other thread can see it;
    /// <see cref="IsBlockDataReady"/> goes true last, which is what publishes the rest.
    /// </summary>
    internal void RestoreFrom(ChunkSnapshot snapshot) => RestoreFrom(snapshot, streamedColumns: false);

    /// <summary>
    /// <see cref="RestoreFrom(ChunkSnapshot)"/> with the block source chosen: a streamed
    /// multiplayer chunk (<paramref name="streamedColumns"/> true) carries no worldgen column, so
    /// its surface floor is derived from its own cells and no fluid frontier is scanned (the server
    /// owns fluids). A save restore without a world source at hand uses the column cache as
    /// before, for the fluid frontier (<see cref="RestoreFrom(ChunkSnapshot, ColumnData)"/>).
    /// </summary>
    internal void RestoreFrom(ChunkSnapshot snapshot, bool streamedColumns)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!streamedColumns)
        {
            RestoreFrom(snapshot, ColumnCache.Get((int)Position.X, (int)Position.Z));
            return;
        }

        CopySnapshot(snapshot);
        SurfaceMinY = LowestSolidY();
        FinishRestore(snapshot);
    }

    /// <summary>
    /// <see cref="RestoreFrom(ChunkSnapshot)"/> against a known column: the surface floor comes
    /// from <paramref name="column"/> and the fluid frontier is scanned against its heights and
    /// water levels. This is the save store's restore (with the world source's column) and the
    /// way a world source that supplies chunks fills one (<see cref="IWorldSource.Fill"/>).
    /// </summary>
    internal void RestoreFrom(ChunkSnapshot snapshot, ColumnData column)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(column);

        CopySnapshot(snapshot);
        SurfaceMinY = column.MinHeight;
        // A restored chunk can hold fluid anywhere, so it always scans (a chunk with no fluid
        // cell simply reports nothing)
        ChunkTerrainFill.ScanFluidFrontier(this, column, LowerBoundY);
        FinishRestore(snapshot);
    }

    // The blocks, state bytes and non-air count of a restore.
    private void CopySnapshot(ChunkSnapshot snapshot)
    {
        Array.Copy(snapshot.Blocks, _blocks, BlockCount);
        _state = snapshot.State is { } state ? (byte[])state.Clone() : null;
        _solidCount = BlockCount - _blocks.AsSpan().Count(BlockType.Air);
    }

    // The tail every restore shares; IsBlockDataReady goes true last, which publishes the rest.
    private void FinishRestore(ChunkSnapshot snapshot)
    {
        IsRestored = true;
        RestoredPlayerPlaced = snapshot.PlayerPlaced is { } placed ? (ushort[])placed.Clone() : null;

        IsBlockDataReady = true;
    }

    // The world Y of this chunk's lowest non-air cell, or int.MinValue when it is all air.
    private int LowestSolidY()
    {
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        for (var z = 0; z < Size; z++)
            if (_blocks[Idx(x, y, z)] != 0)
                return LowerBoundY + y;
        return int.MinValue;
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
        CellWritten?.Invoke((int)worldPos.X, (int)worldPos.Y, (int)worldPos.Z, 0, 0);
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
        CellWritten?.Invoke((int)worldPos.X, (int)worldPos.Y, (int)worldPos.Z, 0, 0);
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
            LightingConfig.LightPropagationRadius);
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
            LightingConfig.LightPropagationRadius, skip: Position);
    }

    private static bool InBounds(int x, int y, int z)
        => x >= 0 && y >= 0 && z >= 0 && x < Size && y < Size && z < Size;

    /// <summary>
    /// Builds this chunk's mesh, or returns null when a face neighbor, or one of the nine chunk
    /// columns' terrain (<see cref="ChunkManager.ColumnSource"/>, a streamed column that has not
    /// arrived), is missing.
    /// The whole 3x3x3 neighborhood is resolved once, up front: the caller checked for face
    /// neighbors before scheduling, but this runs on a pool thread behind a semaphore and can
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
    /// passes the field of the real footprint; null means the chunk's own fields, from the stored
    /// corner climate of its lattice columns (<see cref="BiomeTint.FieldsFromNeighborhood"/>).
    /// </summary>
    internal ChunkMeshData? GenerateMeshData(ChunkManager chunkManager, bool greedy,
        BiomeTintFields? tintFields = null) => ChunkMeshBuilder.GenerateMeshData(this, chunkManager, greedy, tintFields);

    /// <summary>
    /// Local <see cref="Idx"/> indices of the exposed hot-fluid cells of the last applied mesh
    /// (ember/smoke spawn points), or null when there are none. Main thread only.
    /// </summary>
    public ushort[]? HotFluidVents { get; private set; }

    /// <summary>Local <see cref="Idx"/> indices of the chunk's hot-spring water tops (their vents
    /// steam), or null when there are none. A headless server never sets it, since it draws no
    /// steam.</summary>
    public ushort[]? SpringVents { get; private set; }

    /// <summary>
    /// Chunk-local points of the flames of the last applied mesh (torch heads, see
    /// <see cref="BlockType.FlamePoints"/>), or null when there are none. Main thread only. A headless
    /// server drops them (<see cref="MarkMeshedHeadless"/>), since it never draws a flame.
    /// </summary>
    public Vector3[]? FlamePoints { get; private set; }

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
        SpringVents = meshData.SpringVents;
        FlamePoints = meshData.FlamePoints;
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

    internal static readonly Vector3[][][] AONeighbors =
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
