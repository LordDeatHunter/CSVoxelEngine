using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using VoxelEngine.Saves;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Saves;

/// <summary>
/// The chunk save format (<see cref="ChunkSaveCodec"/>) and the chunk seams it feeds: an encoded
/// snapshot decodes to the same cells, an unknown block degrades to air, a damaged file surfaces as
/// <see cref="InvalidDataException"/>, every <see cref="ChunkBlocks"/> storage mode round-trips and
/// decodes compact, <see cref="Chunk.TakeSnapshot"/> and <see cref="Chunk.RestoreFrom(ChunkSnapshot)"/>
/// round-trip a chunk without sharing mutable storage, and a restored chunk's fluid frontier wakes
/// poured water while a generated-style sea source keeps the carved rule.
/// </summary>
public class ChunkSaveCodecTests
{
    // Content-free id <-> name mapping: id 7 is "b7", and back
    private static string? NameOf(ushort id) => "b" + id;
    private static ushort? IdOf(string name) => ushort.Parse(name[1..]);

    private static byte[] Encode(ChunkSnapshot snapshot, Func<ushort, string?> nameOf)
    {
        using var stream = new MemoryStream();
        ChunkSaveCodec.Write(stream, snapshot, nameOf);
        return stream.ToArray();
    }

    private static ChunkSnapshot Decode(byte[] bytes, Func<string, ushort?> idOf)
    {
        using var stream = new MemoryStream(bytes);
        return ChunkSaveCodec.Read(stream, idOf);
    }

    private static ChunkSnapshot RandomSnapshot(int seed, bool withState, bool withPlaced)
    {
        var random = new Random(seed);
        var blocks = new ushort[Chunk.BlockCount];
        for (var i = 0; i < blocks.Length; i++) blocks[i] = (ushort)random.Next(0, ushort.MaxValue + 1);
        // Plenty of air too, so the palette's air slot is exercised
        for (var i = 0; i < blocks.Length; i += 3) blocks[i] = BlockType.Air;

        byte[]? state = null;
        if (withState)
        {
            state = new byte[Chunk.BlockCount];
            random.NextBytes(state);
        }

        ushort[]? placed = withPlaced
            ? Enumerable.Range(0, 50).Select(_ => (ushort)random.Next(Chunk.BlockCount)).ToArray()
            : null;
        return new ChunkSnapshot(blocks, state, playerPlaced: placed);
    }

    // Air everywhere except cells 1..ids.Length, which hold the given ids in order
    private static ChunkSnapshot SnapshotHolding(ushort[] ids)
    {
        var blocks = new ushort[Chunk.BlockCount];
        ids.CopyTo(blocks, 1);
        return new ChunkSnapshot(blocks, state: null, playerPlaced: null);
    }

    private static void AssertRoundTripsCellExactly(ChunkSnapshot snapshot)
    {
        var back = Decode(Encode(snapshot, NameOf), IdOf);
        Assert.Equal(snapshot.Storage.ToDense(), back.Storage.ToDense());
    }

    [Fact]
    public void BlocksAndStateRoundTripThroughAnInMemoryStream()
    {
        var full = RandomSnapshot(11, withState: true, withPlaced: true);
        var back = Decode(Encode(full, NameOf), IdOf);

        Assert.Equal(full.Storage.ToDense(), back.Storage.ToDense());
        Assert.Equal(full.State, back.State);
        Assert.Equal(full.PlayerPlaced, back.PlayerPlaced);

        var bare = RandomSnapshot(12, withState: false, withPlaced: false);
        var bareBack = Decode(Encode(bare, NameOf), IdOf);

        Assert.Equal(bare.Storage.ToDense(), bareBack.Storage.ToDense());
        Assert.Null(bareBack.State);
        Assert.Null(bareBack.PlayerPlaced);
    }

    [Fact]
    public void UnknownOrNullBlockNameDegradesToAir()
    {
        var snapshot = RandomSnapshot(21, withState: false, withPlaced: false);
        var blocks = snapshot.Storage.ToDense();
        // Two ids the snapshot really holds: one the writer cannot name, one the reader cannot map
        var nameless = blocks.First(b => b != 0);
        var unmapped = blocks.First(b => b != 0 && b != nameless);

        var bytes = Encode(snapshot, id => id == nameless ? null : NameOf(id));
        var back = Decode(bytes, name => name == NameOf(unmapped) ? null : IdOf(name));

        for (var i = 0; i < blocks.Length; i++)
        {
            if (blocks[i] == nameless || blocks[i] == unmapped)
                Assert.Equal(0, back.Storage.Get(i));
            else
                Assert.Equal(blocks[i], back.Storage.Get(i));
        }
    }

    [Fact]
    public void BadMagicOrTruncatedBodyThrowsInvalidData()
    {
        var garbage = new byte[64];
        new Random(31).NextBytes(garbage);
        Assert.Throws<InvalidDataException>(() => Decode(garbage, IdOf));
        Assert.Throws<InvalidDataException>(() => Decode([], IdOf));

        var valid = Encode(RandomSnapshot(32, withState: true, withPlaced: true), NameOf);
        Assert.Throws<InvalidDataException>(() => Decode(valid[..(valid.Length / 2)], IdOf));

        // A future version this build cannot read is refused rather than misread
        var future = (byte[])valid.Clone();
        future[4] = unchecked((byte)(ChunkSaveCodec.Version + 1));
        Assert.Throws<InvalidDataException>(() => Decode(future, IdOf));
    }

    /// <summary>An older file is refused, not misread: its body has another layout, and the save
    /// store turns the refusal into a regenerated chunk.</summary>
    [Fact]
    public void AnOlderVersionIsRefusedAsInvalidData()
    {
        var older = Encode(RandomSnapshot(33, withState: true, withPlaced: true), NameOf);
        older[4] = ChunkSaveCodec.Version - 1;

        Assert.Throws<InvalidDataException>(() => Decode(older, IdOf));
    }

    /// <summary>A cell naming a palette entry the header never listed is refused.</summary>
    [Fact]
    public void AnIndexPastThePaletteIsRefused()
    {
        // Control: the forged file is otherwise well-formed, so naming the one entry decodes
        Assert.Equal(1, Decode(ForgeOneNameChunk(cell: 7, index: 1), IdOf).Storage.Get(7));
        Assert.Throws<InvalidDataException>(() => Decode(ForgeOneNameChunk(cell: 7, index: 2), IdOf));
    }

    // A chunk file written by hand: a header listing one name ("b1") and no state or placed
    // cells, then a byte-index body that is air except for one cell naming palette entry index
    private static byte[] ForgeOneNameChunk(int cell, byte index)
    {
        var indices = new byte[Chunk.BlockCount];
        indices[cell] = index;
        using var forged = new MemoryStream();
        using (var header = new BinaryWriter(forged, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            header.Write("CVXC"u8);
            header.Write(ChunkSaveCodec.Version);
            header.Write((byte)0);
            header.Write((ushort)1);
            header.Write(NameOf(1)!);
        }
        using (var deflate = new DeflateStream(forged, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(indices);
        return forged.ToArray();
    }

    /// <summary>
    /// As many distinct non-air ids as a chunk can hold or an id can name, spread over the whole
    /// id range up to its largest value, round-trip cell for cell: the palette count and the
    /// indices are wide enough for any chunk.
    /// </summary>
    [Fact]
    public void APaletteOfEveryDistinctIdRoundTrips()
    {
        var count = Math.Min(Chunk.BlockCount - 1, (int)ushort.MaxValue);
        var ids = new ushort[count];
        for (var i = 1; i <= count; i++)
            ids[i - 1] = (ushort)((long)i * ushort.MaxValue / count);
        Assert.Equal(count, ids.Distinct().Count());
        Assert.Contains(ushort.MaxValue, ids);

        AssertRoundTripsCellExactly(SnapshotHolding(ids));
    }

    /// <summary>Exactly as many distinct non-air ids as a one-byte index can name, taken from the
    /// top of the id range, round-trip cell for cell.</summary>
    [Fact]
    public void APaletteAtTheByteIndexBoundaryRoundTrips()
    {
        AssertRoundTripsCellExactly(SnapshotHolding(TopIds(byte.MaxValue)));
    }

    /// <summary>One distinct non-air id more than a one-byte index can name round-trips cell for
    /// cell, and its body switches to wide indices: it inflates to roughly one extra byte per cell
    /// over the byte-index boundary case.</summary>
    [Fact]
    public void APaletteJustPastTheByteIndexWidthRoundTrips()
    {
        var wide = SnapshotHolding(TopIds(byte.MaxValue + 1));
        AssertRoundTripsCellExactly(wide);

        var narrowBody = InflatedBodyLength(Encode(SnapshotHolding(TopIds(byte.MaxValue)), NameOf));
        var wideBody = InflatedBodyLength(Encode(wide, NameOf));
        Assert.True(wideBody >= narrowBody + Chunk.BlockCount / 2,
            $"a {byte.MaxValue + 1}-name palette's body inflates to {wideBody} bytes, the {byte.MaxValue}-name one to {narrowBody}");
    }

    // count distinct ids taken from the top of the id range
    private static ushort[] TopIds(int count)
    {
        var ids = new ushort[count];
        for (var k = 0; k < ids.Length; k++)
            ids[k] = (ushort)(ushort.MaxValue - k);
        return ids;
    }

    // The length of an encoded chunk's Deflate body once inflated: the header (magic, version,
    // flags, palette count and names) is skipped first
    private static long InflatedBodyLength(byte[] file)
    {
        using var input = new MemoryStream(file);
        using (var header = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            header.ReadBytes(4 + 1 + 1);
            var names = header.ReadUInt16();
            for (var k = 0; k < names; k++) header.ReadString();
        }
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var body = new MemoryStream();
        deflate.CopyTo(body);
        return body.Length;
    }

    /// <summary>
    /// Whatever storage a chunk's cells compact to (one id, a small palette, or more distinct ids
    /// than a palette holds), the file round-trips every cell, the state and the placed cells, and
    /// the decoded storage is the smallest that holds its ids and is compact (so a restore only
    /// clones it). Negative control: a decoder that kept a raw palette or skipped FromDense would
    /// fail the mode or compactness check.
    /// </summary>
    [Theory]
    [InlineData(1, (ushort)0)]
    [InlineData(1, (ushort)7)]
    [InlineData(5, (ushort)1)]
    [InlineData(ChunkBlocks.PaletteCapacity + 40, (ushort)1)]
    public void EveryStorageModeRoundTripsCellExactly(int distinct, ushort firstId)
    {
        var cells = StorageCells.WithDistinctIds(distinct, firstId, seed: 31);
        var random = new Random(32);
        var state = new byte[Chunk.BlockCount];
        random.NextBytes(state);
        ushort[] placed = [3, 77, (ushort)(Chunk.BlockCount - 1)];
        var snapshot = new ChunkSnapshot(cells, state, placed);

        var back = Decode(Encode(snapshot, NameOf), IdOf);

        Assert.Equal(cells, back.Storage.ToDense());
        Assert.Equal(state, back.State);
        Assert.Equal(placed, back.PlayerPlaced);
        Assert.Equal(StorageCells.SmallestModeFor(cells), back.Storage.Mode);
        Assert.True(back.Storage.IsCompact);
    }

    /// <summary>
    /// A storage that once held many ids and was overwritten back to two keeps stale entries in
    /// its in-memory palette, yet encodes to exactly the bytes of a snapshot built from its cells:
    /// the file depends only on the cells. Negative control: a writer that emitted the in-memory
    /// palette would list the stale names and differ.
    /// </summary>
    [Fact]
    public void AnEditedChunksStalePaletteEntriesNeverReachTheFile()
    {
        const ushort kept = 5;
        var storage = ChunkBlocks.Air;
        for (var i = 0; i < Chunk.BlockCount; i++)
            storage = storage.Set(i, (ushort)(1 + i % 100), out _);
        var oracle = new ushort[Chunk.BlockCount];
        for (var i = 0; i < Chunk.BlockCount; i++)
        {
            oracle[i] = i % 2 == 0 ? BlockType.Air : kept;
            storage = storage.Set(i, oracle[i], out _);
        }

        // Anti-vacuity: the palette really kept entries no cell uses any more
        Assert.True(storage.IdSpan.Length > oracle.Distinct().Count());
        Assert.Equal(oracle, storage.ToDense());

        var edited = Encode(new ChunkSnapshot(storage, state: null, playerPlaced: null), NameOf);
        var fromCells = Encode(new ChunkSnapshot(oracle, state: null, playerPlaced: null), NameOf);
        Assert.Equal(fromCells, edited);
    }

    // The ids no content assigns, so a storage built from them cannot collide with a real block
    private const ushort SyntheticIdBase = 40000;

    /// <summary>
    /// A snapshot is a copy: a write to the chunk after <see cref="Chunk.TakeSnapshot"/>, whether
    /// it stays in the storage's mode or upgrades it (Uniform to Indexed, Indexed to Direct), never
    /// shows in the snapshot. Negative control: a TakeSnapshot that shared the live storage would
    /// see the in-mode writes. The Indexed and Direct chunks are restored from synthetic-id
    /// snapshots so the case does not depend on how many blocks the content defines.
    /// </summary>
    [Theory]
    [InlineData(1)]                                // Uniform air, upgraded to Indexed by the write
    [InlineData(3)]                                // Indexed, written in place
    [InlineData(ChunkBlocks.PaletteCapacity)]      // a full palette, upgraded to Direct
    [InlineData(ChunkBlocks.PaletteCapacity + 20)] // Direct, written in place
    public void ASnapshotIsIsolatedFromLaterEdits(int distinct)
    {
        TestContent.EnsureLoaded();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var chunk = new Chunk(null!, MeshTestWorld.HighAbove, generateBlockData: false);
        if (distinct > 1)
            chunk.RestoreFrom(new ChunkSnapshot(StorageCells.WithDistinctIds(distinct, SyntheticIdBase, seed: 41), null, null),
                streamedColumns: true);

        var oracle = chunk.Storage.ToDense();
        var snapshot = chunk.TakeSnapshot(null);

        // Over a synthetic cell, so the write changes the cell in every case
        var cell = Array.FindIndex(oracle, id => id != stone);
        chunk.SetBlockRaw(cell >> (2 * Chunk.SizeShift), (cell >> Chunk.SizeShift) & Chunk.SizeMask, cell & Chunk.SizeMask,
            stone, 3);

        // The write adds one distinct id (stone), which decides whether the storage had to upgrade
        var modeAfterEdit = distinct + 1 <= ChunkBlocks.PaletteCapacity
            ? ChunkBlocks.StorageMode.Indexed
            : ChunkBlocks.StorageMode.Direct;
        Assert.Equal(modeAfterEdit, chunk.Storage.Mode);
        Assert.Equal(stone, chunk.Storage.Get(cell));
        Assert.Equal(oracle, snapshot.Storage.ToDense());
        Assert.Null(snapshot.State);
    }

    /// <summary>
    /// Two chunks restored from one snapshot, whether it is compact (a decoded file, which the
    /// restore clones) or not (a TakeSnapshot of an edited chunk, which the restore recompacts),
    /// never share mutable storage with it or with each other: an edit to one leaves the other and
    /// the snapshot as they were, and both restored chunks read every cell with the oracle's
    /// non-air count. Negative control: a restore that took the snapshot's storage as is would
    /// show the edit in the other chunk.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARestoredChunkNeverSharesMutableStorageWithItsSnapshot(bool decoded)
    {
        TestContent.EnsureLoaded();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var dirt = ContentRegistry.RequireBlock("dirt").Id;

        var source = new Chunk(null!, MeshTestWorld.HighAbove, generateBlockData: false);
        for (var i = 0; i < Chunk.BlockCount; i += 5)
            source.SetBlockRaw(i >> (2 * Chunk.SizeShift), (i >> Chunk.SizeShift) & Chunk.SizeMask, i & Chunk.SizeMask,
                i % 2 == 0 ? stone : dirt, 0);
        var snapshot = source.TakeSnapshot(null);
        if (decoded) snapshot = Decode(Encode(snapshot, NameOf), IdOf);
        Assert.Equal(decoded, snapshot.Storage.IsCompact);

        var oracle = snapshot.Storage.ToDense();
        var solid = oracle.Count(id => id != BlockType.Air);
        var first = new Chunk(null!, MeshTestWorld.HighAbove, generateBlockData: false);
        var second = new Chunk(null!, MeshTestWorld.HighAbove, generateBlockData: false);
        first.RestoreFrom(snapshot);
        second.RestoreFrom(snapshot);
        Assert.Equal(oracle, first.Storage.ToDense());
        Assert.Equal(solid, first.SolidCount);
        Assert.Equal(solid, second.SolidCount);

        // An in-mode write (dirt is already in the palette) over a stone cell
        first.SetBlockRaw(0, 0, 0, dirt, 0);
        Assert.Equal(dirt, first.GetLocalBlock(0, 0, 0));

        Assert.Equal(oracle, second.Storage.ToDense());
        Assert.Equal(oracle, snapshot.Storage.ToDense());
    }

    /// <summary>
    /// Two palette names that resolve to one id, plus one name nothing resolves (air), decode to
    /// the right cells with a duplicate-free palette: the decoded storage is compact whatever the
    /// file's name palette said. Negative control: a decoder that mapped the name palette straight
    /// into the storage palette would hold the merged id twice and air twice.
    /// </summary>
    [Fact]
    public void NamesThatResolveToTheSameIdRestoreAsOneCompactPalette()
    {
        const ushort merged = 9;
        var cells = StorageCells.WithDistinctIds(4, firstId: 1, seed: 51);
        var bytes = Encode(new ChunkSnapshot(cells, state: null, playerPlaced: null),
            id => id switch { 1 => "alias-a", 2 => "alias-b", _ => "gone" });

        var back = Decode(bytes, name => name.StartsWith("alias", StringComparison.Ordinal) ? merged : null);

        var expected = cells.Select(id => id is 1 or 2 ? merged : BlockType.Air).ToArray();
        Assert.Equal(expected, back.Storage.ToDense());
        var palette = back.Storage.IdSpan.ToArray();
        Assert.Equal(palette.Length, palette.Distinct().Count());
        Assert.Equal(expected.Distinct().Order(), palette.Order());
        Assert.True(back.Storage.IsCompact);
    }

    [Fact]
    public void TakeSnapshotAndRestoreRoundTripWithRealContent()
    {
        TestContent.EnsureLoaded();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var water = ContentRegistry.RequireBlock("water").Id;

        var source = new Chunk(null!, MeshTestWorld.HighAbove, generateBlockData: false);
        Assert.False(source.ModifiedSinceSave);

        source.SetBlockRaw(3, 4, 5, stone, 0);
        source.SetBlockRaw(6, 2, 6, water, FluidState.Flowing(3));
        Assert.True(source.ModifiedSinceSave);

        var snapshot = source.TakeSnapshot(null);
        Assert.False(source.ModifiedSinceSave);

        var restored = new Chunk(null!, MeshTestWorld.HighAbove, generateBlockData: false);
        restored.RestoreFrom(snapshot);

        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            Assert.Equal(source.GetLocalBlock(x, y, z), restored.GetLocalBlock(x, y, z));
            Assert.Equal(source.GetLocalState(x, y, z), restored.GetLocalState(x, y, z));
        }

        Assert.True(restored.IsBlockDataReady);
        Assert.True(restored.IsRestored);
        Assert.False(restored.ModifiedSinceSave);
        Assert.Equal(source.SolidCount, restored.SolidCount);
        Assert.NotNull(restored.FluidFrontier);
        Assert.Contains((ushort)Chunk.Idx(6, 2, 6), restored.FluidFrontier!);

        // The snapshot is a copy: editing the source afterwards cannot reach the restored chunk
        source.SetBlockRaw(3, 4, 5, 0, 0);
        Assert.Equal(stone, restored.GetLocalBlock(3, 4, 5));
    }

    /// <summary>
    /// A generated chunk holding tree wood keeps every block and state byte through the whole save
    /// path (snapshot, encode, decode, restore): the graph state of the wood and leaves is what the
    /// mesher, the hitboxes and felling read, so losing it would turn a reloaded tree into twigs.
    /// </summary>
    [Fact]
    public void WoodAndLeafStateSurviveTheRoundTrip()
    {
        TestContent.EnsureLoaded();
        Assert.True(TryGenerateChunkWithTreeWood(out var source),
            "no generated chunk near the origin holds tree wood with a graph state");

        // The same id <-> name mapping the save store's writer and reader use
        var bytes = Encode(source.TakeSnapshot(null), static id => BlockType.ById[id]?.Name);
        var snapshot = Decode(bytes, static name => ContentRegistry.TryResolveBlock(name, out var id) ? id : null);

        var restored = new Chunk(null!, source.Position, generateBlockData: false);
        restored.RestoreFrom(snapshot);

        var restoredWood = false;
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var block = restored.GetLocalBlock(x, y, z);
            var state = restored.GetLocalState(x, y, z);
            Assert.Equal(source.GetLocalBlock(x, y, z), block);
            Assert.Equal(source.GetLocalState(x, y, z), state);
            restoredWood |= BlockType.IsTreeWood(block) && state != 0;
        }

        Assert.True(restoredWood, "the restored chunk holds no tree wood with a non-zero state");
    }

    /// <summary>Generates the chunk holding a wood cell of some tree near the origin, if any has state.</summary>
    private static bool TryGenerateChunkWithTreeWood(out Chunk chunk)
    {
        for (var x = -96; x < 96; x++)
        for (var z = -96; z < 96; z++)
        {
            if (!TreeGenerator.TryGetTree(x, z, out _, out _)) continue;
            var shape = TreeBatches.ShapeOf(x, z);
            // A cell above the base, so the chunk is the one the trunk climbs through
            var cell = shape.Cells[Math.Min(1, shape.WoodCount - 1)];
            var position = new Vector3(cell.X >> Chunk.SizeShift, cell.Y >> Chunk.SizeShift, cell.Z >> Chunk.SizeShift);
            var candidate = new Chunk(null!, position, generateBlockData: true);

            for (var i = 0; i < Chunk.BlockCount; i++)
            {
                var (lx, ly, lz) = (i >> (2 * Chunk.SizeShift), (i >> Chunk.SizeShift) & Chunk.SizeMask, i & Chunk.SizeMask);
                if (BlockType.IsTreeWood(candidate.GetLocalBlock(lx, ly, lz)) && candidate.GetLocalState(lx, ly, lz) != 0)
                {
                    chunk = candidate;
                    return true;
                }
            }
        }

        chunk = null!;
        return false;
    }

    [Fact]
    public void GenerationNeverMarksAChunkModified()
    {
        TestContent.EnsureLoaded();
        var max = int.MinValue;
        foreach (var h in ColumnCache.GetHeights(0, 0)) max = Math.Max(max, h);

        // The surface chunk goes through every column fill, carver and surface stamp
        var surface = new Chunk(null!, new Vector3(0, max >> Chunk.SizeShift, 0), generateBlockData: true);
        Assert.True(surface.IsBlockDataReady);
        Assert.False(surface.ModifiedSinceSave);
        Assert.False(surface.IsRestored);
    }

    [Fact]
    public void RestoredFrontierWakesPouredWaterButNotAStillSeaSource()
    {
        TestContent.EnsureLoaded();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var water = ContentRegistry.RequireBlock("water").Id;

        // (a) High above the terrain: a source there can only have been poured
        var high = MeshTestWorld.HighAbove;
        var highColumn = ColumnCache.Get((int)high.X, (int)high.Z);
        var highFloor = (int)high.Y * Chunk.Size;
        Assert.True(highFloor > highColumn.MaxHeight && highFloor > highColumn.MaxWaterLevel,
            "HighAbove must sit above every terrain top and water level of its column");

        var openWest = BlocksWithSource(water, 5, 5, 5);
        EncloseFiveFaces(openWest, stone, 5, 5, 5, leaveOpen: (-1, 0, 0));
        Assert.Contains((ushort)Chunk.Idx(5, 5, 5), FrontierOf(high, openWest));

        var sealedBelow = BlocksWithSource(water, 5, 5, 5);
        EncloseFiveFaces(sealedBelow, stone, 5, 5, 5, leaveOpen: null);
        Assert.DoesNotContain((ushort)Chunk.Idx(5, 5, 5), FrontierOf(high, sealedBelow));

        // (b) A real wet column: a source above the terrain top but at or below the water level
        // is still generated-style water, so it keeps the carved rule
        Assert.True(TryFindWetColumn(out var cx, out var cz, out var lx, out var lz, out var nx, out var nz,
                out var y),
            "no wet column with a neighbor no higher than itself within the search radius");

        var column = ColumnCache.Get(cx, cz);
        var i = (lx << Chunk.SizeShift) | lz;
        var n = (nx << Chunk.SizeShift) | nz;
        Assert.True(column.Heights[i] < y && y <= column.WaterLevel[i]);
        Assert.True(column.Heights[n] < y);

        var pos = new Vector3(cx, y >> Chunk.SizeShift, cz);
        var ly = y & Chunk.SizeMask;

        // Air only beside it in the lower neighboring column: open air above that column's terrain
        var besideOpenAir = BlocksWithSource(water, lx, ly, lz);
        EncloseFiveFaces(besideOpenAir, stone, lx, ly, lz, leaveOpen: (nx - lx, 0, nz - lz));
        Assert.DoesNotContain((ushort)Chunk.Idx(lx, ly, lz), FrontierOf(pos, besideOpenAir));

        // Air only below it, at its own column's terrain top: a carved cavity
        var overCavity = BlocksWithSource(water, lx, ly, lz);
        EncloseFiveFaces(overCavity, stone, lx, ly, lz, leaveOpen: (0, -1, 0));
        Assert.Contains((ushort)Chunk.Idx(lx, ly, lz), FrontierOf(pos, overCavity));
    }

    private static ushort[] BlocksWithSource(ushort water, int x, int y, int z)
    {
        var blocks = new ushort[Chunk.BlockCount];
        blocks[Chunk.Idx(x, y, z)] = water;
        return blocks;
    }

    /// <summary>Stones the five faces the frontier scan tests (never +Y), except
    /// <paramref name="leaveOpen"/>, which stays air.</summary>
    private static void EncloseFiveFaces(ushort[] blocks, ushort stone, int x, int y, int z,
        (int X, int Y, int Z)? leaveOpen)
    {
        (int X, int Y, int Z)[] faces = [(-1, 0, 0), (1, 0, 0), (0, 0, -1), (0, 0, 1), (0, -1, 0)];
        foreach (var f in faces)
        {
            if (f == leaveOpen) continue;
            blocks[Chunk.Idx(x + f.X, y + f.Y, z + f.Z)] = stone;
        }
    }

    private static ushort[] FrontierOf(Vector3 pos, ushort[] blocks)
    {
        var chunk = new Chunk(null!, pos, generateBlockData: false);
        chunk.RestoreFrom(new ChunkSnapshot(blocks, null, playerPlaced: null));
        return chunk.FluidFrontier ?? [];
    }

    /// <summary>
    /// A chunk column holding a cell <c>i</c> whose water level stands above its terrain top, with
    /// an in-chunk horizontal neighbor <c>n</c> no higher than it, and a test height
    /// <c>y = Heights[i] + 1</c> that leaves room for all five tested faces inside one chunk.
    /// </summary>
    private static bool TryFindWetColumn(out int cx, out int cz, out int lx, out int lz,
        out int nx, out int nz, out int y)
    {
        (int X, int Z)[] sides = [(-1, 0), (1, 0), (0, -1), (0, 1)];
        const int radius = 64;
        for (var r = 0; r <= radius; r++)
        for (cx = -r; cx <= r; cx++)
        for (cz = -r; cz <= r; cz++)
        {
            if (Math.Max(Math.Abs(cx), Math.Abs(cz)) != r) continue;
            var column = ColumnCache.Get(cx, cz);
            if (column.MaxWaterLevel <= column.MinHeight) continue;

            for (lx = 1; lx < Chunk.SizeMask; lx++)
            for (lz = 1; lz < Chunk.SizeMask; lz++)
            {
                var i = (lx << Chunk.SizeShift) | lz;
                if (column.WaterLevel[i] <= column.Heights[i]) continue;
                y = column.Heights[i] + 1;
                var ly = y & Chunk.SizeMask;
                if (ly == 0) continue; // the -Y face would leave the chunk

                foreach (var (sx, sz) in sides)
                {
                    nx = lx + sx;
                    nz = lz + sz;
                    if (column.Heights[(nx << Chunk.SizeShift) | nz] < y) return true;
                }
            }
        }

        cx = cz = lx = lz = nx = nz = y = 0;
        return false;
    }
}
