using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Structures;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Hell;
using VoxelEngine.WorldGen.Sky;
using VoxelEngine.WorldGen.Structures;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Structures;

/// <summary>
/// The waystone satellites (<see cref="WaystonePlacement"/>): ruin picking never yields a satellite template and satellite
/// picking only does; every satellite holds its realm's waystone exactly once; and in every realm a found satellite repeats
/// exactly, keeps clear of its own ruin, stands on the ground its realm's rule asks for, and is stamped whole, its one waystone
/// included, by the chunks it crosses.
/// </summary>
[Collection("Worldgen seed")]
public sealed class WaystonePlacementTests
{
    private delegate bool CellFinder(int cellX, int cellZ, out PlacedStructure ruin, out PlacedStructure waystone);

    private delegate void ChunkStamp(byte[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0);

    private static readonly StructureKind[] Kinds =
        [StructureKind.Surface, StructureKind.Underground, StructureKind.Hell, StructureKind.Sky];

    public WaystonePlacementTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    private static byte WaystoneOf(StructureKind kind) =>
        ContentRegistry.RequireBlock($"waystone_{kind.ToString().ToLowerInvariant()}").Id;

    [Fact]
    public void RuinPickingNeverYieldsASatelliteAndSatellitePickingOnlySatellites()
    {
        var biomes = new List<Biome?> { null };
        foreach (var b in Enum.GetValues<Biome>()) biomes.Add(b);
        foreach (var kind in Kinds)
        {
            var pickedSatellite = false;
            foreach (var biome in biomes)
            for (var i = 0; i < 100; i++)
            {
                var roll = i / 100f;
                if (StructureRegistry.TryPick(kind, biome, roll, out var ruin))
                    Assert.True(ruin.Kind == kind && !ruin.Satellite, $"{ruin.Name} picked as a {kind} ruin");
                if (!StructureRegistry.TryPick(kind, biome, roll, out var satellite, satellite: true)) continue;
                Assert.True(satellite.Kind == kind && satellite.Satellite, $"{satellite.Name} picked as a {kind} satellite");
                pickedSatellite = true;
            }
            Assert.True(pickedSatellite, $"no {kind} satellite template is ever picked");
        }
    }

    [Fact]
    public void EverySatelliteHoldsExactlyOneWaystoneOfItsOwnRealm()
    {
        foreach (var kind in Kinds)
        {
            Assert.NotEmpty(StructureRegistry.SatellitesOf(kind));
            foreach (var template in StructureRegistry.SatellitesOf(kind))
            {
                var waystones = new List<int>();
                for (var y = 0; y < template.Height; y++)
                for (var z = 0; z < template.Size; z++)
                for (var x = 0; x < template.Size; x++)
                {
                    var cell = template.CellAt(0, x, y, z);
                    if (cell > 0 && BlockType.ById[cell & 0xFF]!.Waystone) waystones.Add(cell & 0xFF);
                }
                Assert.Equal(new List<int> { WaystoneOf(kind) }, waystones);
            }
        }
    }

    [Fact]
    public void SurfaceWaystonesStandOnLevelDryLandBesideTheirRuin()
    {
        var found = FindByCell(WaystonePlacement.TryFindSurface, 3);
        Assert.NotEmpty(found);
        foreach (var (ruin, waystone) in found)
        {
            Assert.True(WaystonePlacement.TryFindSurface(CellOf(ruin.X), CellOf(ruin.Z), out var r, out var w));
            Assert.Equal((ruin, waystone), (r, w));
            AssertClear(ruin, waystone);

            var ground = waystone.GroundY - 1;
            Assert.NotEqual(Biome.Ocean, TerrainGenerator.BiomeAt(waystone.X, waystone.Z));
            for (var x = waystone.MinX; x <= waystone.MaxX; x++)
            for (var z = waystone.MinZ; z <= waystone.MaxZ; z++)
            {
                var h = RuinPlacement.HeightAt(x, z);
                Assert.True(Math.Abs(h - ground) <= WorldFeatureConfig.RuinSurfaceMaxStep, $"step at {x},{z}");
                Assert.False(TerrainGenerator.IsWaterColumn(x, z, h), $"water at {x},{z}");
            }
            AssertStampedWhole(waystone, StructureKind.Surface, WaystonePlacement.StampSurface);
        }
    }

    [Fact]
    public void UndergroundWaystonesAreSealedUnderTheirCoverAndOverTheBarrier()
    {
        var found = FindByCell(WaystonePlacement.TryFindUnderground, 3);
        Assert.NotEmpty(found);
        foreach (var (ruin, waystone) in found)
        {
            Assert.True(WaystonePlacement.TryFindUnderground(CellOf(ruin.X), CellOf(ruin.Z), out var r, out var w));
            Assert.Equal((ruin, waystone), (r, w));
            AssertClear(ruin, waystone);

            Assert.True(waystone.OriginY > WorldGenerationConfig.BarrierTopY);
            Assert.True(waystone.OriginY + waystone.Template.Height - 1
                        <= RuinPlacement.HeightAt(waystone.X, waystone.Z) - WorldFeatureConfig.RuinUndergroundCover);
            AssertStampedWhole(waystone, StructureKind.Underground, WaystonePlacement.StampUnderground);
        }
    }

    [Fact]
    public void HellWaystonesStandOnAPocketFloorOfTheirRuinsLayer()
    {
        var found = 0;
        for (var layer = 0; layer < 8 && found < 2; layer++)
        {
            var l = layer;
            foreach (var (ruin, waystone) in FindByCell((int cx, int cz, out PlacedStructure r, out PlacedStructure w) =>
                         WaystonePlacement.TryFindHell(cx, cz, l, out r, out w), 2 - found))
            {
                found++;
                Assert.True(WaystonePlacement.TryFindHell(CellOf(ruin.X), CellOf(ruin.Z), layer, out var r, out var w));
                Assert.Equal((ruin, waystone), (r, w));
                AssertClear(ruin, waystone);

                Assert.Equal(layer, HellGenerator.LayerIndex(ruin.GroundY));
                Assert.Equal(layer, HellGenerator.LayerIndex(waystone.GroundY));
                Assert.False(HellGenerator.IsOpen(waystone.X, waystone.GroundY - 1, waystone.Z), "the waystone's floor is open");
                Assert.True(HellGenerator.IsCavernAir(waystone.X, waystone.GroundY, waystone.Z, new Dictionary<long, float>()));
                AssertStampedWhole(waystone, StructureKind.Hell, WaystonePlacement.StampHell, layer);
            }
        }
        Assert.True(found > 0, "no hell waystone found over a wide scan");
    }

    [Fact]
    public void SkyWaystonesStandOnTheirRuinsIslandTop()
    {
        var found = 0;
        for (var gx = -60; gx < 60 && found < 3; gx++)
        for (var gz = -60; gz < 60 && found < 3; gz++)
        {
            if (!SkyIslands.TryGetIsland(gx, gz, out var island)) continue;
            if (!WaystonePlacement.TryFindSky(island, out var ruin, out var waystone)) continue;
            found++;
            Assert.True(WaystonePlacement.TryFindSky(island, out var r, out var w));
            Assert.Equal((ruin, waystone), (r, w));
            AssertClear(ruin, waystone);

            for (var x = waystone.MinX; x <= waystone.MaxX; x++)
            for (var z = waystone.MinZ; z <= waystone.MaxZ; z++)
                Assert.True(SkyIslandGenerator.TryGetColumn(island, x, z, out _, out _), $"{x},{z} is off the island");
            Assert.True(SkyIslandGenerator.TryGetColumn(island, waystone.X, waystone.Z, out _, out var top));
            Assert.Equal(top + 1, waystone.GroundY);
            AssertStampedWhole(waystone, StructureKind.Sky, WaystonePlacement.StampSky);
        }
        Assert.True(found > 0, "no sky waystone found over a wide scan");
    }

    [Fact]
    public void TheWorldGeneratesTheWaystoneOfASurfaceAndAnUndergroundRuin()
    {
        foreach (var (kind, find) in new (StructureKind, CellFinder)[]
                 {
                     (StructureKind.Surface, WaystonePlacement.TryFindSurface),
                     (StructureKind.Underground, WaystonePlacement.TryFindUnderground),
                 })
        {
            var (_, waystone) = Assert.Single(FindByCell(find, 1));
            var (x, lowerY, z) = WaystoneCell(waystone);
            // Both halves stand, the upper one right over the waystone, whichever chunk holds each
            foreach (var (y, id) in new[] { (lowerY, WaystoneOf(kind)), (lowerY + 1, BlockType.ById[WaystoneOf(kind)]!.UpperHalfId) })
            {
                var chunk = new Chunk(null!, new Vector3(x >> Chunk.SizeShift, y >> Chunk.SizeShift, z >> Chunk.SizeShift),
                    generateBlockData: false);
                ChunkTerrainFill.GenerateBlockData(chunk);
                Assert.Equal(id, chunk.Blocks[Chunk.Idx(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask)]);
            }
        }
    }

    [Fact]
    public void NoLaterFeatureGrowsInsideAGeneratedWaystoneChamber()
    {
        var checkedChambers = 0;
        for (var cx = -4; cx <= 4; cx++)
        for (var cz = -4; cz <= 4; cz++)
        {
            if (!WaystonePlacement.TryFindUnderground(cx, cz, out _, out var chamber)) continue;
            checkedChambers++;
            var template = chamber.Template;
            var chunks = new Dictionary<(int, int, int), byte[]>();
            for (var y = 0; y < template.Height; y++)
            for (var z = 0; z < template.Size; z++)
            for (var x = 0; x < template.Size; x++)
            {
                var cell = template.CellAt(chamber.Turns, x, y, z);
                if (cell < 0) continue;
                var (wx, wy, wz) = (chamber.MinX + x, chamber.OriginY + y, chamber.MinZ + z);
                var key = (wx >> Chunk.SizeShift, wy >> Chunk.SizeShift, wz >> Chunk.SizeShift);
                if (!chunks.TryGetValue(key, out var blocks))
                {
                    var chunk = new Chunk(null!, new Vector3(key.Item1, key.Item2, key.Item3), generateBlockData: false);
                    ChunkTerrainFill.GenerateBlockData(chunk);
                    chunks[key] = blocks = chunk.Blocks;
                }
                var got = blocks[Chunk.Idx(wx & Chunk.SizeMask, wy & Chunk.SizeMask, wz & Chunk.SizeMask)];
                Assert.True(got == (cell & 0xFF),
                    $"{template.Name} cell {wx},{wy},{wz} holds {BlockType.ById[got]?.Name ?? "air"} after generation");
            }
        }
        Assert.True(checkedChambers > 0, "no underground waystone near the origin");
    }

    private static (int X, int Y, int Z) WaystoneCell(PlacedStructure placed)
    {
        var template = placed.Template;
        for (var y = 0; y < template.Height; y++)
        for (var z = 0; z < template.Size; z++)
        for (var x = 0; x < template.Size; x++)
        {
            var cell = template.CellAt(placed.Turns, x, y, z);
            if (cell > 0 && BlockType.ById[cell & 0xFF]!.Waystone) return (placed.MinX + x, placed.OriginY + y, placed.MinZ + z);
        }
        throw new InvalidOperationException($"{template.Name} holds no waystone");
    }

    private static int CellOf(int x) => x >> WorldFeatureConfig.RuinCellShift;

    private static List<(PlacedStructure Ruin, PlacedStructure Waystone)> FindByCell(CellFinder find, int want)
    {
        var found = new List<(PlacedStructure, PlacedStructure)>();
        for (var cx = -30; cx < 30 && found.Count < want; cx++)
        for (var cz = -30; cz < 30 && found.Count < want; cz++)
            if (find(cx, cz, out var ruin, out var waystone))
                found.Add((ruin, waystone));
        return found;
    }

    // The two footprints keep the configured gap of empty columns on at least one axis
    private static void AssertClear(PlacedStructure ruin, PlacedStructure waystone)
    {
        var gapX = Math.Max(waystone.MinX - ruin.MaxX, ruin.MinX - waystone.MaxX) - 1;
        var gapZ = Math.Max(waystone.MinZ - ruin.MaxZ, ruin.MinZ - waystone.MaxZ) - 1;
        Assert.True(Math.Max(gapX, gapZ) >= WorldFeatureConfig.WaystoneRuinGap,
            $"the waystone at {waystone.X},{waystone.Z} crowds its ruin at {ruin.X},{ruin.Z}");
    }

    /// <summary>Stamps every chunk the waystone's box crosses through the realm's own chunk stamp and checks that every
    /// template cell lands where it belongs, and that the box holds exactly one waystone, the realm's.</summary>
    private static void AssertStampedWhole(PlacedStructure waystone, StructureKind kind, ChunkStamp stamp, int layer = -1)
    {
        var template = waystone.Template;
        var maxY = waystone.OriginY + template.Height - 1;
        var written = new Dictionary<(int, int, int), int>();
        for (var cx = waystone.MinX >> Chunk.SizeShift; cx <= waystone.MaxX >> Chunk.SizeShift; cx++)
        for (var cy = waystone.OriginY >> Chunk.SizeShift; cy <= maxY >> Chunk.SizeShift; cy++)
        for (var cz = waystone.MinZ >> Chunk.SizeShift; cz <= waystone.MaxZ >> Chunk.SizeShift; cz++)
        {
            var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
            byte[]? state = null;
            stamp(blocks, ref state, cx * Chunk.Size, cy * Chunk.Size, cz * Chunk.Size);
            for (var lx = 0; lx < Chunk.Size; lx++)
            for (var ly = 0; ly < Chunk.Size; ly++)
            for (var lz = 0; lz < Chunk.Size; lz++)
            {
                var i = Chunk.Idx(lx, ly, lz);
                if (blocks[i] != 0) written[(cx * Chunk.Size + lx, cy * Chunk.Size + ly, cz * Chunk.Size + lz)] = blocks[i] | ((state?[i] ?? 0) << 8);
            }
        }

        var waystones = 0;
        for (var y = 0; y < template.Height; y++)
        for (var z = 0; z < template.Size; z++)
        for (var x = 0; x < template.Size; x++)
        {
            var at = (waystone.MinX + x, waystone.OriginY + y, waystone.MinZ + z);
            if (layer >= 0 && HellGenerator.LayerIndex(at.Item2) != layer) continue;
            var cell = template.CellAt(waystone.Turns, x, y, z);
            if (cell > 0)
            {
                Assert.True(written.TryGetValue(at, out var got), $"{template.Name} cell {x},{y},{z} missing");
                Assert.Equal(cell, got);
            }
            if (written.TryGetValue(at, out var block) && BlockType.ById[block & 0xFF]!.Waystone)
            {
                waystones++;
                Assert.Equal(WaystoneOf(kind), block & 0xFF);
            }
        }
        Assert.Equal(1, waystones);
    }
}
