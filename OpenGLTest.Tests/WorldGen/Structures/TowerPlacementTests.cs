using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Structures;
using VoxelEngine.Interaction;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Structures;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Structures;

/// <summary>
/// Where battle towers stand and what they write (<see cref="TowerPlacement"/>, <see cref="TowerGenerator"/>): a tower stands
/// within a few cells of the origin on the default seed, sites are a pure function of (cell, seed), the rolled radius and floor
/// count stay in their ranges, every tower keeps the spawn clear on dry, level land, the point query equals the stamp in every
/// footprint chunk, the dressing fits the smallest room and every support-fragile dressing block has its holder, the altar cell
/// is free on an unbreakable roof, every floor holds chests of its own tier and the roof one of the treasure tier, and
/// every palette brick is unbreakable.
/// </summary>
[Collection("Worldgen seed")]
public sealed class TowerPlacementTests
{
    private const int Window = 4;
    private readonly ITestOutputHelper _output;

    public TowerPlacementTests(ITestOutputHelper output)
    {
        _output = output;
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    private static TowerSite Nearest()
    {
        Assert.True(TowerPlacement.TryFindNearest(0, 0, Window, out var site), "no tower within the window");
        return site;
    }

    /// <summary>Every tower of the cells within <see cref="Window"/> of the origin's cell.</summary>
    private static List<TowerSite> SitesNearOrigin()
    {
        var sites = new List<TowerSite>();
        for (var cx = -Window; cx <= Window; cx++)
        for (var cz = -Window; cz <= Window; cz++)
            if (TowerPlacement.TryGetSite(cx, cz, out var site))
                sites.Add(site);
        return sites;
    }

    private static int BlockOf(int cell) => cell & 0xFF;
    private static byte StateOf(int cell) => (byte)(cell >> 8);

    [Fact]
    public void ATowerStandsWithinAFewCellsOfTheOrigin()
    {
        var s = Nearest();
        _output.WriteLine($"Nearest tower: cell ({s.CellX}, {s.CellZ}), center ({s.CenterX}, {s.CenterZ}), ground {s.GroundY}, " +
                          $"radius {s.Radius}, floors {s.Floors}, turns {s.Turns}, roof y {s.RoofY}, altar {s.AltarCell}, " +
                          $"top y {s.TopY}, brick {BlockType.ById[s.Palette.Brick].Name}");
        for (var k = 0; k < s.Floors; k++) _output.WriteLine($"  floor {k}: slab y {s.FloorY(k)}");
    }

    [Fact]
    public void SitesRepeatPerCellAndFollowTheSeed()
    {
        var first = Signature(SitesNearOrigin());
        TowerPlacement.ResetCache();
        Assert.Equal(first, Signature(SitesNearOrigin()));

        try
        {
            WorldSeed.Apply(WorldSeed.Default + 4242);
            Assert.NotEqual(first, Signature(SitesNearOrigin()));
        }
        finally
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    private static string Signature(IEnumerable<TowerSite> sites) => string.Join(";", sites.Select(s =>
        $"{s.CellX},{s.CellZ},{s.CenterX},{s.CenterZ},{s.GroundY},{s.Radius},{s.Floors},{s.Turns},{s.Palette.Brick}," +
        $"{s.Altar.Name}/{s.AltarTurns}," + string.Join("|", s.FloorTemplates.Select((t, k) => $"{t.Name}/{s.FloorTurns[k]}"))));

    [Fact]
    public void RolledRadiusAndFloorsStayInTheirRanges()
    {
        var sites = SitesNearOrigin();
        Assert.NotEmpty(sites);
        foreach (var s in sites)
        {
            Assert.InRange(s.Radius, WorldGenerationConfig.TowerMinRadius, WorldGenerationConfig.TowerMaxRadius);
            Assert.InRange(s.Floors, WorldGenerationConfig.TowerMinFloors, WorldGenerationConfig.TowerMaxFloors);
            for (var k = 0; k < s.Floors; k++) Assert.True(s.FloorY(k + 1) > s.FloorY(k));
            Assert.Equal(s.FloorY(s.Floors), s.RoofY);
            Assert.True(s.BottomY < s.GroundY);
            Assert.True(s.GroundY < s.AltarCell.Y);
            Assert.True(s.AltarCell.Y <= s.TopY);
            Assert.True(s.TopY < WorldGenerationConfig.CloudSlabBottomY);
            // The footprint stays inside its own cell, so two towers never meet
            Assert.Equal(s.CellX, TowerPlacement.CellOf(s.MinX));
            Assert.Equal(s.CellX, TowerPlacement.CellOf(s.MaxX));
            Assert.Equal(s.CellZ, TowerPlacement.CellOf(s.MinZ));
            Assert.Equal(s.CellZ, TowerPlacement.CellOf(s.MaxZ));
        }
    }

    [Fact]
    public void TowersStandOutsideTheSpawnClearOnDryLevelLand()
    {
        var sites = SitesNearOrigin();
        Assert.NotEmpty(sites);
        const int r = TowerPlacement.Reach + WorldGenerationConfig.TowerSiteWaterRing;
        foreach (var s in sites)
        {
            var centerDistance = Math.Sqrt((double)s.CenterX * s.CenterX + (double)s.CenterZ * s.CenterZ);
            Assert.True(centerDistance - s.OuterRadius >= WorldGenerationConfig.TowerSpawnClearRadius,
                $"tower at ({s.CenterX}, {s.CenterZ}) reaches into the spawn clear");
            Assert.NotEqual(Biome.Ocean, TerrainGenerator.BiomeAt(s.CenterX, s.CenterZ));
            Assert.Equal(RuinPlacement.HeightAt(s.CenterX, s.CenterZ), s.GroundY);
            for (var dx = -r; dx <= r; dx += WorldGenerationConfig.TowerSiteSampleStep)
            for (var dz = -r; dz <= r; dz += WorldGenerationConfig.TowerSiteSampleStep)
            {
                if (dx * dx + dz * dz > r * r + r) continue;
                var h = RuinPlacement.HeightAt(s.CenterX + dx, s.CenterZ + dz);
                Assert.InRange(h, s.GroundY - WorldGenerationConfig.TowerMaxStep, s.GroundY + WorldGenerationConfig.TowerMaxStep);
                Assert.False(TerrainGenerator.IsWaterColumn(s.CenterX + dx, s.CenterZ + dz, h));
            }
        }
    }

    [Fact]
    public void ThePointQueryEqualsTheStampAcrossEveryFootprintChunk()
    {
        var s = Nearest();
        const int volume = Chunk.Size * Chunk.Size * Chunk.Size;
        var wrote = 0;
        var chests = 0;
        (int X, int Y, int Z)? seam = null;
        for (var cx = s.MinChunkX; cx <= s.MaxChunkX; cx++)
        for (var cy = s.MinChunkY; cy <= s.MaxChunkY; cy++)
        for (var cz = s.MinChunkZ; cz <= s.MaxChunkZ; cz++)
        {
            var blocks = new byte[volume];
            byte[]? state = null;
            int x0 = cx * Chunk.Size, y0 = cy * Chunk.Size, z0 = cz * Chunk.Size;
            TowerPlacement.Stamp(blocks, ref state, x0, y0, z0);
            var chunkWrote = 0;
            var chunkSkipped = 0;
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var cell = TowerGenerator.CellAt(x0 + x, y0 + y, z0 + z);
                var i = Chunk.Idx(x, y, z);
                var stamped = blocks[i];
                var stampedState = state?[i] ?? 0;
                if (cell < 0)
                {
                    chunkSkipped++;
                    Assert.Equal(0, stamped);
                    Assert.Equal(0, stampedState);
                    continue;
                }
                chunkWrote++;
                Assert.Equal(BlockOf(cell), stamped);
                Assert.Equal(StateOf(cell), stampedState);
                if (stamped != TowerPalette.ChestBlock) continue;
                // A chest carries the tier of the floor it stands on, or the roof's treasure tier
                chests++;
                var wy = y0 + y;
                var tier = wy < s.RoofY ? (wy - s.GroundY) / WorldGenerationConfig.TowerFloorHeight : TowerSite.TreasureTier;
                Assert.Equal(tier, ChestProfile.TierOf(stampedState));
            }
            wrote += chunkWrote;
            if (seam == null && chunkWrote > 0 && chunkSkipped > 0) seam = (x0, y0, z0);
        }
        Assert.True(wrote > 0);
        Assert.True(chests > 0, "the stamp wrote no chest, so no chest state was compared");
        Assert.NotNull(seam);

        // A seam chunk over junk: unwritten cells keep it, written cells get their exact state, zero included
        const byte junkId = 1;
        const byte junkState = 0x5A;
        var (sx0, sy0, sz0) = seam.Value;
        var junk = Enumerable.Repeat(junkId, volume).ToArray();
        byte[]? junkStates = Enumerable.Repeat(junkState, volume).ToArray();
        TowerPlacement.Stamp(junk, ref junkStates, sx0, sy0, sz0);
        var written = 0;
        var kept = 0;
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var cell = TowerGenerator.CellAt(sx0 + x, sy0 + y, sz0 + z);
            var i = Chunk.Idx(x, y, z);
            if (cell < 0)
            {
                kept++;
                Assert.Equal(junkId, junk[i]);
                Assert.Equal(junkState, junkStates![i]);
                continue;
            }
            written++;
            Assert.Equal(BlockOf(cell), junk[i]);
            Assert.Equal(StateOf(cell), junkStates![i]);
        }
        Assert.True(written > 0 && kept > 0, "the seam chunk should be partly covered");
    }

    /// <summary>
    /// The whole fill (gates on) keeps every tower cell: neither the above-the-terrain early-out nor the sky short path drops
    /// a tower chunk, its top chunks included, and no later stage writes over a tower cell. The entrance apron cuts a
    /// walkway through whatever ground stood there.
    /// </summary>
    [Fact]
    public void TheGeneratedChunksHoldEveryTowerCell()
    {
        var s = Nearest();
        var chunks = new Dictionary<(int, int, int), Chunk>();
        for (var cx = s.MinChunkX; cx <= s.MaxChunkX; cx++)
        for (var cy = s.MinChunkY; cy <= s.MaxChunkY; cy++)
        for (var cz = s.MinChunkZ; cz <= s.MaxChunkZ; cz++)
        {
            var chunk = new Chunk(null!, new Vector3(cx, cy, cz), generateBlockData: false);
            ChunkTerrainFill.GenerateBlockData(chunk);
            chunks[(cx, cy, cz)] = chunk;
        }

        int BlockAt(int x, int y, int z) =>
            chunks[(x >> Chunk.SizeShift, y >> Chunk.SizeShift, z >> Chunk.SizeShift)]
                .GetLocalBlock(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask);

        int StateAt(int x, int y, int z) =>
            chunks[(x >> Chunk.SizeShift, y >> Chunk.SizeShift, z >> Chunk.SizeShift)]
                .GetLocalState(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask);

        var checkedCells = 0;
        var aboveTerrain = 0;
        for (var x = s.MinX; x <= s.MaxX; x++)
        for (var z = s.MinZ; z <= s.MaxZ; z++)
        {
            var columnTop = ColumnCache.GetHeights(x >> Chunk.SizeShift, z >> Chunk.SizeShift)[((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask)];
            for (var y = s.BottomY; y <= s.TopY; y++)
            {
                var cell = TowerGenerator.CellAt(s, x, y, z);
                if (cell < 0) continue;
                checkedCells++;
                if (BlockOf(cell) != 0 && y > columnTop + Chunk.Size) aboveTerrain++;
                Assert.True(BlockOf(cell) == BlockAt(x, y, z) && StateOf(cell) == StateAt(x, y, z),
                    $"cell ({x}, {y}, {z}): the point query says {BlockOf(cell)}/{StateOf(cell)}, the fill holds {BlockAt(x, y, z)}/{StateAt(x, y, z)}");
            }
        }
        Assert.True(checkedCells > 0, "the tower wrote no cell");
        Assert.True(aboveTerrain > 0, "no tower block stands a chunk above its column's terrain, so the early-out was never tested");

        var apron = 0;
        for (var x = s.MinX; x <= s.MaxX; x++)
        for (var z = s.MinZ; z <= s.MaxZ; z++)
        {
            int dx = x - s.CenterX, dz = z - s.CenterZ;
            if (TowerShell.RingOf(s, dx, dz, TowerShell.StepOf(s, dx, dz)) != TowerRing.Apron) continue;
            if (BlockAt(x, s.GroundY + 1, z) == 0 && BlockAt(x, s.GroundY + 2, z) == 0) apron++;
        }
        Assert.True(apron > 0, "no entrance apron column is open at the ground floor");
    }

    /// <summary>
    /// Surface ruins and their waystone satellites keep off every tower's footprint: over each ruin cell whose ruin, or whose
    /// satellite a short walk off, could reach a tower near the origin, no accepted ruin footprint and no accepted waystone
    /// footprint overlaps the tower's box.
    /// </summary>
    [Fact]
    public void NoSurfaceRuinOrWaystoneStandsInATowerFootprint()
    {
        // A wide window, so some ruin rolls inside a tower and the rule has something to refuse
        var sites = new List<TowerSite>();
        for (var cx = -3 * Window; cx <= 3 * Window; cx++)
        for (var cz = -3 * Window; cz <= 3 * Window; cz++)
            if (TowerPlacement.TryGetSite(cx, cz, out var site))
                sites.Add(site);
        Assert.NotEmpty(sites);
        // A ruin center reaches its own footprint within Reach, its satellite's within the walk plus the satellite's Reach
        var grow = RuinPlacement.Reach + (int)Math.Ceiling(WorldFeatureConfig.WaystoneMaxDistance) + 2 * RuinPlacement.Reach;
        var shift = WorldFeatureConfig.RuinCellShift;
        var rolledInside = 0;
        foreach (var s in sites)
        {
            for (var cellX = (s.MinX - grow) >> shift; cellX <= (s.MaxX + grow) >> shift; cellX++)
            for (var cellZ = (s.MinZ - grow) >> shift; cellZ <= (s.MaxZ + grow) >> shift; cellZ++)
            {
                if (!RuinPlacement.TryRollRuin(StructureKind.Surface, cellX, cellZ, 0, out var hash, out var x, out var z)) continue;
                if (x >= s.MinX && x <= s.MaxX && z >= s.MinZ && z <= s.MaxZ) rolledInside++;
                if (RuinPlacement.TryFindSurfaceSite(x, z, hash, out var template, out var ground))
                {
                    var ruin = new PlacedStructure(template, RuinPlacement.TurnsOf(hash), x, ground + 1 - template.GroundLayer, z);
                    Assert.False(Overlaps(s, ruin), $"the ruin at ({x}, {z}) stands in the tower at ({s.CenterX}, {s.CenterZ})");
                }
                if (WaystonePlacement.TryFindSurface(cellX, cellZ, out _, out var waystone))
                    Assert.False(Overlaps(s, waystone),
                        $"the waystone at ({waystone.X}, {waystone.Z}) stands in the tower at ({s.CenterX}, {s.CenterZ})");
            }
        }
        _output.WriteLine($"{sites.Count} towers, {rolledInside} surface ruin rolls centered inside one");
        Assert.True(rolledInside > 0, "no surface ruin rolled inside any tower, so the rule was never exercised");
    }

    private static bool Overlaps(TowerSite s, PlacedStructure p) =>
        p.MinX <= s.MaxX && p.MaxX >= s.MinX && p.MinZ <= s.MaxZ && p.MaxZ >= s.MinZ;

    [Fact]
    public void DressingTemplatesFitTheSmallestRoomAtEveryTurn()
    {
        var site = TowerSiteFixture.SyntheticSite(StructureRegistry.Of(StructureKind.TowerFloor)[0], StructureRegistry.Of(StructureKind.TowerAltar)[0], 0);
        foreach (var template in StructureRegistry.Of(StructureKind.TowerFloor).Concat(StructureRegistry.Of(StructureKind.TowerAltar)))
        {
            if (template.Kind == StructureKind.TowerFloor)
                Assert.True(template.Height < WorldGenerationConfig.TowerFloorHeight, $"{template.Name} reaches the next slab");
            for (var turns = 0; turns < 4; turns++)
            for (var lx = 0; lx < template.Size; lx++)
            for (var lz = 0; lz < template.Size; lz++)
            for (var ly = 0; ly < template.Height; ly++)
            {
                if (template.CellAt(turns, lx, ly, lz) < 0) continue;
                var dx = StructureStamper.OriginOf(site.CenterX, template) + lx - site.CenterX;
                var dz = StructureStamper.OriginOf(site.CenterZ, template) + lz - site.CenterZ;
                var ring = TowerShell.RingOf(site, dx, dz, TowerShell.StepOf(site, dx, dz));
                Assert.True(ring == TowerRing.Room, $"{template.Name} turned {turns} writes ({lx}, {ly}, {lz}) outside the room");
            }
        }
    }

    [Fact]
    public void EverySupportedDressingBlockHasItsHolder()
    {
        var checkedBlocks = 0;
        foreach (var s in TowerSiteFixture.SyntheticSites())
        {
            var r = s.RoomRadius;
            for (var x = s.CenterX - r; x <= s.CenterX + r; x++)
            for (var z = s.CenterZ - r; z <= s.CenterZ + r; z++)
            for (var y = s.GroundY + 1; y <= s.TopY; y++)
            {
                var cell = TowerGenerator.CellAt(s, x, y, z);
                if (cell <= 0) continue;
                var id = (byte)BlockOf(cell);
                if (!BlockSupport.TryGetSupportCell(id, StateOf(cell), new Vector3(x, y, z), out var holder)) continue;
                checkedBlocks++;
                var held = TowerGenerator.CellAt(s, (int)holder.X, (int)holder.Y, (int)holder.Z);
                Assert.True(held >= 0 && BlockType.IsSolid((byte)BlockOf(held)),
                    $"{BlockType.ById[id].Name} at ({x}, {y}, {z}) has no solid holder");
            }
        }
        Assert.True(checkedBlocks > 0, "the dressing should hold some support-fragile block (a lantern or a torch)");
    }

    [Fact]
    public void TheAltarCellIsFreeOnAnUnbreakableRoof()
    {
        foreach (var s in TowerSiteFixture.SyntheticSites().Append(Nearest()))
        {
            var (x, y, z) = s.AltarCell;
            Assert.Equal(BlockType.Air, (byte)TowerGenerator.CellAt(s, x, y, z));
            var roof = TowerGenerator.CellAt(s, x, y - 1, z);
            Assert.True(roof >= 0 && BlockType.IsUnbreakable((byte)BlockOf(roof)));
        }
    }

    [Fact]
    public void EveryFloorHasPilesStandingOnItsSlabWhereThePointQuerySaysSo()
    {
        foreach (var s in TowerSiteFixture.SyntheticSites())
        for (var k = 0; k < s.Floors; k++)
        {
            var piles = s.PileCells(k);
            Assert.NotEmpty(piles);
            Assert.Equal(piles.Count, piles.Distinct().Count());
            foreach (var (x, y, z) in piles)
            {
                Assert.Equal(TowerPalette.PileBlock, (byte)BlockOf(TowerGenerator.CellAt(s, x, y, z)));
                Assert.Equal(s.FloorY(k) + 1, y);
            }
        }
    }

    [Fact]
    public void EveryFloorHoldsAChestOfItsOwnTierWhereThePointQuerySaysSo()
    {
        foreach (var s in TowerSiteFixture.SyntheticSites())
        {
            for (var k = 0; k < s.Floors; k++)
            {
                var chests = TowerGenerator.ChestCells(s, k);
                Assert.NotEmpty(chests);
                Assert.Equal(chests.Count, chests.Distinct().Count());
                var piles = s.PileCells(k);
                foreach (var (x, y, z) in chests)
                {
                    var cell = TowerGenerator.CellAt(s, x, y, z);
                    Assert.Equal(TowerPalette.ChestBlock, (byte)BlockOf(cell));
                    Assert.Equal(s.Tier(k), ChestProfile.TierOf(StateOf(cell)));
                    Assert.Equal(s.FloorY(k) + 1, y);
                    Assert.DoesNotContain((x, y, z), piles);
                }
            }

            var treasure = TowerGenerator.AltarChestCells(s);
            Assert.NotEmpty(treasure);
            foreach (var (x, y, z) in treasure)
            {
                var cell = TowerGenerator.CellAt(s, x, y, z);
                Assert.Equal(TowerPalette.ChestBlock, (byte)BlockOf(cell));
                Assert.Equal(TowerSite.TreasureTier, ChestProfile.TierOf(StateOf(cell)));
                Assert.Equal(s.RoofY + 1, y);
                Assert.NotEqual(s.AltarCell, (x, y, z));
            }
        }
    }

    [Fact]
    public void EveryPaletteBrickIsUnbreakable()
    {
        foreach (var palette in TowerPalette.All)
        {
            Assert.True(BlockType.IsUnbreakable(palette.Brick));
            Assert.False(BlockType.IsUnbreakable(palette.Step));
        }
        Assert.Equal(TowerPalette.DefaultBrick, TowerPalette.For(Biome.Forest).Brick);
        Assert.Equal(TowerPalette.For(Biome.Frozen), TowerPalette.For(Biome.Taiga));
        Assert.NotEqual(TowerPalette.For(Biome.Forest), TowerPalette.For(Biome.Taiga));
        Assert.NotEqual(TowerPalette.For(Biome.Forest), TowerPalette.For(Biome.Desert));
        Assert.NotEqual(TowerPalette.For(Biome.Desert), TowerPalette.For(Biome.Taiga));
        Assert.Equal(ContentRegistry.RequireBlock("tower_bricks_sandstone").Id, TowerPalette.For(Biome.Desert).Brick);
        Assert.Equal(ContentRegistry.RequireBlock("tower_bricks_frost").Id, TowerPalette.For(Biome.Frozen).Brick);
    }
}
