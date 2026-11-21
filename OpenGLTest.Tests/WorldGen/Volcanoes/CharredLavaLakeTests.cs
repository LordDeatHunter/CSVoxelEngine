using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Linq;
using VoxelEngine.Commands;
using VoxelEngine.Commands.Definitions;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Structures;
using VoxelEngine.WorldGen.Volcanoes;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Volcanoes;

/// <summary>
/// The Charred province's fluids, obsidian and <c>/locate volcano</c>: a lake centered in Charred land floods with lava
/// over its whole footprint (never split into water and lava) and its bed and banks cool to obsidian, no river water
/// flows in Charred land, obsidian craters and pebbles are more common there than elsewhere, and the volcano locate
/// answers the nearest accepted site with a teleport onto ground outside its caldera.
/// </summary>
[Collection("Worldgen seed")]
public sealed class CharredLavaLakeTests : IDisposable
{
    private const int Seed = 1337;
    private readonly RecordingCommandHost _host = new();
    private readonly int _previousSeed;
    private readonly WorldGenProfile _previousProfile;

    public CharredLavaLakeTests()
    {
        TestContent.EnsureLoaded();
        _previousSeed = WorldSeed.Value;
        _previousProfile = WorldSeed.Profile;
        WorldSeed.Apply(Seed, WorldGenProfile.Dynamic);
    }

    // Back to whatever seed and profile the collection held, so the next class sees the state it would have without us
    public void Dispose() => WorldSeed.Apply(_previousSeed, _previousProfile);

    private static VolcanoSite NearestVolcano()
    {
        Assert.True(VolcanoSites.TryFindNearest(0, 0, WorldGenerationConfig.VolcanoLocateRings, out var site),
            "no volcano within the locate rings of the origin");
        return site;
    }

    [Fact]
    public void AVolcanoLocateIsTheNearestSiteAndLandsOutsideItsCaldera()
    {
        Assert.True(StructureLocator.TryFindVolcano(0, 0, WorldGenerationConfig.VolcanoLocateRings, out var found));
        var site = NearestVolcano();

        // Nothing accepted in the scanned window is nearer than the answer
        const int cell = WorldGenerationConfig.VolcanoCellBlocks;
        const int rings = WorldGenerationConfig.VolcanoLocateRings;
        for (var gx = -rings; gx <= rings; gx++)
        for (var gz = -rings; gz <= rings; gz++)
        {
            var other = VolcanoSites.SiteAt(gx, gz);
            if (!other.Exists) continue;
            Assert.True(MathF.Sqrt(other.X * other.X + other.Z * other.Z) >= (float)found.Distance - 0.5f,
                $"cell ({gx}, {gz}) holds a nearer volcano than the answer");
            Assert.True(Math.Abs(gx * cell) < (rings + 1) * cell);
        }

        // The landing is on the cone, outside the owned caldera, standing on the generated ground
        Assert.False(VolcanoShape.InGuard(site, found.LandX + 0.5f, found.LandZ + 0.5f));
        Assert.True(VolcanoShape.Distance(site, found.LandX + 0.5f, found.LandZ + 0.5f) < site.Radius);
        Assert.Equal(RuinPlacement.HeightAt(found.LandX, found.LandZ) + 1, found.LandFeetY);

        var (text, link) = LocateCommand.Volcano(new Vector3(0.5f, 80f, 0.5f));
        Assert.NotNull(link);
        var shown = text.Substring(link!.Value.Start, link.Value.Length);
        var numbers = Regex.Matches(shown, @"-?\d+").Select(m => int.Parse(m.Value)).ToArray();
        Assert.Equal([found.X, found.Y, found.Z], numbers);
        var player = _host.Add("Self", new Vector3(0.5f, 80f, 0.5f));
        GameCommands.Create().Execute(link.Value.Command, _host.SourceFor(player));
        Assert.Equal(found.LandX, (int)MathF.Floor(player.Feet.X));
        Assert.Equal(found.LandZ, (int)MathF.Floor(player.Feet.Z));
    }

    [Fact]
    public void ALegacyWorldLocatesNoVolcano()
    {
        WorldSeed.Apply(Seed, WorldGenProfile.Legacy);
        var (_, link) = LocateCommand.Volcano(new Vector3(0.5f, 80f, 0.5f));
        Assert.Null(link);
    }

    /// <summary>Accepted ponds near the nearest volcano, each with whether its center is Charred land.</summary>
    private static List<(float X, float Z, int Surface, bool Charred)> PondsAround(VolcanoSite volcano, float reach)
    {
        const int cell = WorldGenerationConfig.LakeCellBlocks;
        var ponds = new List<(float, float, int, bool)>();
        for (var gx = (int)MathF.Floor((volcano.X - reach) / cell); gx <= (int)MathF.Floor((volcano.X + reach) / cell); gx++)
        for (var gz = (int)MathF.Floor((volcano.Z - reach) / cell); gz <= (int)MathF.Floor((volcano.Z + reach) / cell); gz++)
        {
            if (!TerrainSites.TryRoll(gx, gz, cell, WaterBodies.LakeSalt, WorldGenerationConfig.LakeChance,
                    WorldGenerationConfig.LakeRadiusSkew, out _, out var cx, out var cz, out _)) continue;
            var site = WaterBodies.PondSiteAt(gx, gz);
            if (!site.Accepted) continue;
            ponds.Add((cx, cz, site.Surface, WaterBodies.IsCharredPool(cx, cz)));
        }
        return ponds;
    }

    [Fact]
    public void ACharredPondFloodsWithLavaAcrossItsWholeFootprint()
    {
        var volcano = NearestVolcano();
        var ponds = PondsAround(volcano, 3000f);
        var lava = ponds.Where(p => p.Charred).ToList();
        Assert.NotEmpty(lava);

        foreach (var pond in lava.Take(3))
        {
            var flooded = 0;
            for (var dx = -24; dx <= 24; dx += 2)
            for (var dz = -24; dz <= 24; dz += 2)
            {
                var x = (int)MathF.Floor(pond.X) + dx;
                var z = (int)MathF.Floor(pond.Z) + dz;
                var column = ColumnCache.Get(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
                var local = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
                if (column.WaterLevel[local] != pond.Surface || column.Bed[local] >= pond.Surface) continue;
                flooded++;
                Assert.True(column.LavaPool[local], $"a column of the Charred pond at ({pond.X}, {pond.Z}) floods with water");
                Assert.True(WaterBodies.WaterLevelAt(x, z, out var pure) == pond.Surface && pure,
                    "the pure level (the far LOD's) disagrees with the stored lava mark");
            }
            Assert.True(flooded > 0, $"the Charred pond at ({pond.X}, {pond.Z}) floods no sampled column");
        }
    }

    [Fact]
    public void ALavaLakesBedAndBanksAreObsidian()
    {
        var pond = PondsAround(NearestVolcano(), 3000f).First(p => p.Charred);
        int beds = 0, shores = 0;
        for (var dx = -40; dx <= 40; dx++)
        {
            var x = (int)MathF.Floor(pond.X) + dx;
            var z = (int)MathF.Floor(pond.Z);
            var column = ColumnCache.Get(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
            var lx = x & Chunk.SizeMask;
            var lz = z & Chunk.SizeMask;
            var local = (lx << Chunk.SizeShift) | lz;
            var bed = column.LavaPool[local];
            var shore = LavaPoolCrust.IsShore(column, lx, lz, x - lx, z - lz);
            if (!bed && !shore) continue;
            var top = column.Heights[local];
            var chunk = new Chunk(null!, new Vector3(x >> Chunk.SizeShift, top >> Chunk.SizeShift, z >> Chunk.SizeShift),
                generateBlockData: false);
            ChunkTerrainFill.GenerateBlockData(chunk);
            var id = chunk.GetLocalBlock(lx, top & Chunk.SizeMask, lz);
            if (id == BlockType.Air || BlockType.IsFluid(id)) continue;
            Assert.Equal(LavaPoolCrust.CrustBlock, id);
            if (bed) beds++;
            else shores++;
        }
        Assert.True(beds > 0 && shores > 0, $"the pond's row crossed {beds} bed and {shores} shore cells");
    }

    [Fact]
    public void ObsidianCratersAndPebblesAreCommonerInCharredLand()
    {
        var volcano = NearestVolcano();
        int charredCells = 0, charredCraters = 0, otherCells = 0, otherCraters = 0;
        int charredStones = 0, charredObsidian = 0, otherStones = 0, otherObsidian = 0;
        var shift = WorldFeatureConfig.MeteorCellShift;
        for (var cx = ((int)volcano.X - 6000) >> shift; cx <= ((int)volcano.X + 6000) >> shift; cx += 2)
        for (var cz = ((int)volcano.Z - 6000) >> shift; cz <= ((int)volcano.Z + 6000) >> shift; cz += 2)
        {
            var centerX = (cx << shift) + (1 << (shift - 1));
            var centerZ = (cz << shift) + (1 << (shift - 1));
            var charred = BiomeClassifier.BiomeWeights(centerX, centerZ, out _, out _) == Biome.Charred;
            var crater = MeteorGenerator.TryGetCrater(cx, cz, out _, out _, out _);
            if (charred) { charredCells++; if (crater) charredCraters++; }
            else { otherCells++; if (crater) otherCraters++; }

            for (var i = 0; i < 3; i++)
            {
                var obsidian = PebbleGenerator.IsObsidianAt(centerX, centerZ, i);
                if (charred) { charredStones++; if (obsidian) charredObsidian++; }
                else { otherStones++; if (obsidian) otherObsidian++; }
            }
        }
        Assert.True(charredCells > 50 && otherCells > 50, $"{charredCells} Charred and {otherCells} other cells sampled");
        Assert.True((double)charredCraters / charredCells > 2.0 * otherCraters / otherCells,
            $"craters: {charredCraters}/{charredCells} in Charred land vs {otherCraters}/{otherCells} elsewhere");
        Assert.True((double)charredObsidian / charredStones > 5.0 * otherObsidian / Math.Max(1, otherStones),
            $"obsidian pebbles: {charredObsidian}/{charredStones} in Charred land vs {otherObsidian}/{otherStones} elsewhere");
    }

    [Fact]
    public void NoRiverWaterFlowsInCharredLand()
    {
        var volcano = NearestVolcano();
        var checkedCharred = 0;
        for (var dx = -2500; dx <= 2500; dx += 37)
        for (var dz = -2500; dz <= 2500; dz += 37)
        {
            var x = volcano.X + dx;
            var z = volcano.Z + dz;
            var biome = BiomeClassifier.BiomeWeights(x, z, out var ocean, out _);
            if (biome != Biome.Charred) continue;
            checkedCharred++;
            WaterBodies.SampleSurfaceWater(x, z, ocean, out _, out _, out _, out var river, out _, out _, out _, out _);
            Assert.Equal(0f, river);
        }
        Assert.True(checkedCharred > 0, "no Charred land sampled around the volcano");
    }
}
