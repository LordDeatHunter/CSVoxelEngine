using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Effects.Lighting;
using VoxelEngine.WorldGen.Hell;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Hell;

/// <summary>
/// Hell's biomes and palette (plans/LAYERS.md L-7, L-8): the biome is a pure function of position and
/// seed that varies across layers and columns and holds down a column of one layer; every biome's
/// blocks are solid, steady stone and its lights glow in colors of their own; the stamp dresses each
/// pocket in the biome of every cell (floors under air, ceilings over it, nothing foreign), ponds its
/// pools by the biome's lava share and never lets a lava cell touch air at its sides or bottom; and
/// the stamp still equals the point query in every biome.
/// </summary>
[Collection("Worldgen seed")]
public sealed class HellBiomeTests
{
    private const int Volume = Chunk.Size * Chunk.Size * Chunk.Size;
    private const int Period = WorldGenerationConfig.HellLayerPeriodBlocks;

    private static readonly HellBiome[] AllBiomes = Enum.GetValues<HellBiome>();

    public HellBiomeTests() => TestContent.EnsureLoaded();

    private static int LayerTop(int layer) => WorldGenerationConfig.HellTopY - layer * Period;

    private static IEnumerable<ushort> Blocks(HellBiomePalette p) =>
        [p.Surface, p.Soil, p.Wall, p.Ceiling, p.PoolBed, p.CeilingLight, p.FloorLight];

    /// <summary>Air, or the flora (HellFlora) that stands in an air cell.</summary>
    private static bool IsCavernAir(ushort block) =>
        block == BlockType.Air || block == HellFlora.VineBlock
        || AllBiomes.Any(b => HellBiomes.Palette(b).GroundPlant == block);

    [Fact]
    public void TheBiomeIsAPureFunctionOfPositionAndSeed()
    {
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
            var first = Sample();
            Assert.Equal(first, Sample());
            WorldSeed.Apply(WorldSeed.Default + 7);
            Assert.NotEqual(first, Sample());
            WorldSeed.Apply(WorldSeed.Default);
            Assert.Equal(first, Sample());
        }
        finally { WorldSeed.Apply(WorldSeed.Default); }

        static List<HellBiome> Sample()
        {
            var list = new List<HellBiome>();
            for (var layer = 0; layer < 3; layer++)
            for (var x = -2000; x <= 2000; x += 250)
            for (var z = -2000; z <= 2000; z += 250)
                list.Add(HellBiomes.At(x, LayerTop(layer) - 40, z));
            return list;
        }
    }

    [Fact]
    public void EveryBiomeAppearsAndTheyMixWithinAndAcrossLayersButHoldDownAColumn()
    {
        var seen = new HashSet<HellBiome>();
        var mixedLayers = 0;
        var columnsChangingWithLayer = 0;
        for (var x = -3000; x <= 3000; x += 150)
        for (var z = -3000; z <= 3000; z += 150)
        {
            var first = HellBiomes.At(x, LayerTop(0), z);
            for (var layer = 1; layer < 6; layer++)
                if (HellBiomes.At(x, LayerTop(layer), z) != first) { columnsChangingWithLayer++; break; }
        }

        for (var layer = 0; layer < 6; layer++)
        {
            var inLayer = new HashSet<HellBiome>();
            for (var x = -3000; x <= 3000; x += 150)
            for (var z = -3000; z <= 3000; z += 150)
            {
                var biome = HellBiomes.At(x, LayerTop(layer), z);
                // A pocket's ceiling and floor over the same spot share a biome
                Assert.Equal(biome, HellBiomes.At(x, LayerTop(layer) - HellGenerator.PocketHeight + 1, z));
                inLayer.Add(biome);
            }

            seen.UnionWith(inLayer);
            if (inLayer.Count > 1) mixedLayers++;
        }

        Assert.Equal(AllBiomes.Length, seen.Count);
        Assert.True(mixedLayers > 0, "no layer mixes biomes");
        Assert.True(columnsChangingWithLayer > 0, "every column keeps one biome through all layers");
    }

    [Fact]
    public void EveryBiomeIsMadeOfSolidSteadyBlocksAndLitInColorsOfItsOwn()
    {
        var slotsByBiome = new Dictionary<HellBiome, byte>();
        foreach (var biome in AllBiomes)
        {
            var palette = HellBiomes.Palette(biome);
            foreach (var id in Blocks(palette).Where(id => id != BlockType.Air))
            {
                // Solid and steady, so a lava cell beside it never flows and it never falls away
                var type = BlockType.ById[id]!;
                Assert.True(BlockType.IsSolid(id), $"{type.Name} in {biome} is not solid");
                Assert.False(BlockType.IsFluid(id), $"{type.Name} in {biome} is a fluid");
                Assert.False(type.Falls, $"{type.Name} in {biome} falls");
            }

            foreach (var light in new[] { palette.CeilingLight, palette.FloorLight }.Where(id => id != BlockType.Air))
            {
                Assert.True(BlockType.Emission[light] > 0, $"{biome}'s light block emits nothing");
                Assert.NotEqual(0, BlockType.LightSlot[light]);
            }

            slotsByBiome[biome] = BlockType.LightSlot[palette.CeilingLight];
            Assert.True(palette.CeilingLightRate >= 1 && palette.FloorLightRate >= 1);
        }

        // The fungal light is a color apart from the embers, and the palette stays within its cap
        Assert.NotEqual(slotsByBiome[HellBiome.Ashlands], slotsByBiome[HellBiome.FungalGrove]);
        Assert.True(BlockLightPalette.Count <= BlockLightPalette.MaxSlots);
    }

    [Fact]
    public void PoolsHoldLavaInTheOrderOfTheirBiomesLavaShare()
    {
        var pools = AllBiomes.ToDictionary(b => b, _ => 0);
        var lava = AllBiomes.ToDictionary(b => b, _ => 0);
        for (var layer = 0; layer < 4; layer++)
        for (var x = -3000; x <= 3000; x += 37)
        for (var z = -3000; z <= 3000; z += 41)
        {
            var biome = HellBiomes.AtLayer(x, layer, z);
            pools[biome]++;
            if (HellBiomes.PoolIsLava(x, layer, z, biome)) lava[biome]++;
        }

        var ordered = AllBiomes.OrderBy(b => HellBiomes.Palette(b).LavaShare).ToArray();
        for (var i = 1; i < ordered.Length; i++)
        {
            double Share(HellBiome b) => (double)lava[b] / pools[b];
            Assert.True(Share(ordered[i]) > Share(ordered[i - 1]),
                $"{ordered[i]} ponds {Share(ordered[i]):F3} of its pools, {ordered[i - 1]} {Share(ordered[i - 1]):F3}");
        }

        foreach (var biome in AllBiomes)
        {
            var share = HellBiomes.Palette(biome).LavaShare;
            if (share >= 1f) Assert.Equal(pools[biome], lava[biome]);
            if (share <= 0f) Assert.Equal(0, lava[biome]);
        }
    }

    [Fact]
    public void TheStampDressesEachPocketInTheBiomeOfEveryCell()
    {
        var hellrock = HellGenerator.HellrockBlock;
        var lavaId = HellGenerator.LavaBlock;
        var surfacesSeen = new HashSet<HellBiome>();
        var lightsSeen = new HashSet<ushort>();
        var blocks = new ushort[Volume];
        var checkedPoint = new HashSet<HellBiome>();

        foreach (var (layer, cx, cz) in ChunkColumnsPerBiome())
        {
            var top = LayerTop(layer);
            var bottom = top - HellGenerator.PocketHeight + 1;
            for (var cy = bottom >> Chunk.SizeShift; cy <= top >> Chunk.SizeShift; cy++)
            {
                int x0 = cx * Chunk.Size, y0 = cy * Chunk.Size, z0 = cz * Chunk.Size;
                HellGenerator.Stamp(blocks, x0, y0, z0);
                var biomeHere = HellBiomes.AtLayer(x0, layer, z0);
                var comparePoint = checkedPoint.Add(biomeHere);

                for (var x = 0; x < Chunk.Size; x++)
                for (var y = 0; y < Chunk.Size; y++)
                for (var z = 0; z < Chunk.Size; z++)
                {
                    int wx = x0 + x, wy = y0 + y, wz = z0 + z;
                    if (!HellGenerator.InHell(wy)) continue;
                    var block = blocks[Chunk.Idx(x, y, z)];
                    if (comparePoint) Assert.Equal(HellGenerator.BlockAt(wx, wy, wz), block);

                    var biome = HellBiomes.At(wx, wy, wz);
                    var palette = HellBiomes.Palette(biome);
                    if (IsCavernAir(block))
                    {
                        Assert.False(HellGenerator.InPool(wy), "an air cell under the lava level");
                        CheckAirNeighbors(blocks, x, y, z, wx, wy, wz, surfacesSeen);
                        continue;
                    }

                    if (block == lavaId)
                    {
                        Assert.True(HellGenerator.InPool(wy));
                        Assert.True(HellBiomes.PoolIsLava(wx, HellGenerator.LayerIndex(wy), wz, biome));
                        CheckLavaContained(blocks, x, y, z);
                        continue;
                    }

                    if (HellGenerator.IsOpen(wx, wy, wz))
                        Assert.False(HellBiomes.PoolIsLava(wx, HellGenerator.LayerIndex(wy), wz, biome),
                            "a lava column's pool cell holds a solid block");
                    if (block == hellrock) continue;
                    Assert.Contains(block, Blocks(palette));
                    if (BlockType.Emission[block] > 0) lightsSeen.Add(block);
                }
            }
        }

        Assert.Equal(AllBiomes.Length, surfacesSeen.Count);
        foreach (var biome in AllBiomes)
            Assert.Contains(HellBiomes.Palette(biome).CeilingLight, lightsSeen);
    }

    /// <summary>The floor under an air cell is its biome's surface or floor light (or lava), and a
    /// solid cell over it that is no floor itself is its biome's ceiling or ceiling light.</summary>
    private static void CheckAirNeighbors(ushort[] blocks, int x, int y, int z, int wx, int wy, int wz,
        HashSet<HellBiome> surfacesSeen)
    {
        if (y > 0)
        {
            var below = blocks[Chunk.Idx(x, y - 1, z)];
            if (!IsCavernAir(below) && below != HellGenerator.LavaBlock)
            {
                var biome = HellBiomes.At(wx, wy - 1, wz);
                var palette = HellBiomes.Palette(biome);
                Assert.True(below == palette.Surface || below == palette.FloorLight,
                    $"the floor under air at ({wx}, {wy}, {wz}) is {BlockType.ById[below]!.Name}, not {biome}'s surface");
                if (below == palette.Surface) surfacesSeen.Add(biome);
            }
        }

        if (y + 1 >= Chunk.Size) return;
        var above = blocks[Chunk.Idx(x, y + 1, z)];
        if (IsCavernAir(above) || above == HellGenerator.LavaBlock) return;
        for (var d = 1; d <= WorldGenerationConfig.HellFloorSkinBlocks; d++)
            if (HellGenerator.IsOpen(wx, wy + 1 + d, wz)) return;

        var ceiling = HellBiomes.Palette(HellBiomes.At(wx, wy + 1, wz));
        Assert.True(above == ceiling.Ceiling || above == ceiling.CeilingLight,
            $"the ceiling over air at ({wx}, {wy}, {wz}) is {BlockType.ById[above]!.Name}");
    }

    /// <summary>A lava cell's sides and bottom, inside the chunk, are lava or solid, so it never flows.</summary>
    private static void CheckLavaContained(ushort[] blocks, int x, int y, int z)
    {
        Span<(int, int, int)> sides = [(x - 1, y, z), (x + 1, y, z), (x, y, z - 1), (x, y, z + 1), (x, y - 1, z)];
        foreach (var (nx, ny, nz) in sides)
        {
            if (nx < 0 || nx >= Chunk.Size || ny < 0 || nz < 0 || nz >= Chunk.Size) continue;
            var neighbor = blocks[Chunk.Idx(nx, ny, nz)];
            Assert.True(neighbor == HellGenerator.LavaBlock || BlockType.IsSolid(neighbor),
                "a lava cell touches a non-solid cell at its side or bottom");
        }
    }

    /// <summary>Two chunk columns per biome, picked by scanning the first layers, each wholly in its
    /// biome at its corners.</summary>
    private static IEnumerable<(int Layer, int Cx, int Cz)> ChunkColumnsPerBiome()
    {
        var found = AllBiomes.ToDictionary(b => b, _ => 0);
        for (var layer = 0; layer < 3; layer++)
        for (var cx = -160; cx <= 160; cx += 7)
        for (var cz = -160; cz <= 160; cz += 11)
        {
            var biome = HellBiomes.AtLayer(cx * Chunk.Size, layer, cz * Chunk.Size);
            if (found[biome] >= 2) continue;
            if (HellBiomes.AtLayer(cx * Chunk.Size + Chunk.Size - 1, layer, cz * Chunk.Size + Chunk.Size - 1) != biome)
                continue;
            found[biome]++;
            yield return (layer, cx, cz);
        }

        Assert.All(found.Values, count => Assert.Equal(2, count));
    }
}
