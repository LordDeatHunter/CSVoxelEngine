using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Chunks;
using VoxelEngine.WorldGen.Sky;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Sky;

/// <summary>
/// The great sky landmasses: they exist at every seed, vary in size up to the config's reach, stay inside the
/// island band with room for their flora, are irregular rather than round, keep ordinary islands out of their
/// footprint, and read like any island through the shared point query and the stamp.
/// </summary>
[Collection("Worldgen seed")]
public sealed class SkyLandmassTests
{
    public SkyLandmassTests() => TestContent.EnsureLoaded();

    private static List<SkyIsland> Landmasses(int cells)
    {
        var found = new List<SkyIsland>();
        for (var gx = -cells; gx <= cells; gx++)
        for (var gz = -cells; gz <= cells; gz++)
            if (SkyLandmasses.TryGetLandmass(gx, gz, out var landmass)) found.Add(landmass);
        return found;
    }

    private static SkyIsland Biggest()
    {
        var best = default(SkyIsland);
        foreach (var landmass in Landmasses(6))
            if (landmass.Radius > best.Radius) best = landmass;
        Assert.True(best.IsLandmass, "no landmass near the origin");
        return best;
    }

    [Fact]
    public void LandmassesExistAndTheirSizesSpanTheConfiguredRange()
    {
        var all = Landmasses(12);
        Assert.True(all.Count >= 4, $"{all.Count} landmasses");
        Assert.All(all, l => Assert.InRange(l.Radius, SkyIslandConfig.LandmassMinRadius, SkyIslandConfig.LandmassMaxRadius));
        Assert.True(all.Max(l => l.Radius) > 2f * all.Min(l => l.Radius), "the sizes barely differ");
    }

    [Fact]
    public void ALandmassStaysInTheBandWithRoomForItsFlora()
    {
        var landmass = Biggest();
        var held = 0;
        for (var x = -landmass.Radius; x <= landmass.Radius; x += 17f)
        for (var z = -landmass.Radius; z <= landmass.Radius; z += 17f)
        {
            var wx = (int)(landmass.CenterX + x);
            var wz = (int)(landmass.CenterZ + z);
            if (!SkyIslandGenerator.TryGetColumn(landmass, wx, wz, out var bottom, out var top)) continue;

            held++;
            Assert.True(top - bottom >= 1, $"a column {bottom}..{top} is thinner than two cells");
            Assert.True(bottom - SkyIslandConfig.SkyVineMaxLength >= WorldGenerationConfig.SkyIslandBottomY,
                $"the vines under {bottom} leave the band");
            Assert.True(top + SkyIslandConfig.IslandFloraHeadroom <= WorldGenerationConfig.SkyIslandTopY,
                $"the flora over {top} leaves the band");
        }

        Assert.True(held > 200, $"only {held} sampled columns hold land");
    }

    [Fact]
    public void ALandmassOutlineIsIrregularNotARoundDisc()
    {
        var landmass = Biggest();
        float min = float.MaxValue, max = 0f;
        for (var a = 0; a < 48; a++)
        {
            var angle = a * MathF.Tau / 48f;
            var dirX = MathF.Cos(angle);
            var dirZ = MathF.Sin(angle);
            // The farthest land along the ray from the center, found by stepping out
            var reach = 0f;
            for (var d = 0f; d < landmass.Radius; d += 8f)
                if (SkyIslandGenerator.TryGetColumn(landmass, (int)(landmass.CenterX + dirX * d),
                        (int)(landmass.CenterZ + dirZ * d), out _, out _))
                    reach = d;
            min = MathF.Min(min, reach);
            max = MathF.Max(max, reach);
        }

        Assert.True(max > 1.4f * MathF.Max(min, 1f), $"the reach only varies {min}..{max}");
    }

    [Fact]
    public void NoOrdinaryIslandStandsWithinALandmass()
    {
        var landmass = Biggest();
        var cell = SkyIslandConfig.IslandCellBlocks;
        var cx = (int)MathF.Floor(landmass.CenterX / cell);
        var cz = (int)MathF.Floor(landmass.CenterZ / cell);
        var span = (int)(landmass.Radius / cell) + 2;
        for (var gx = cx - span; gx <= cx + span; gx++)
        for (var gz = cz - span; gz <= cz + span; gz++)
        {
            if (!SkyIslands.TryGetIsland(gx, gz, out var island)) continue;
            var dx = island.CenterX - landmass.CenterX;
            var dz = island.CenterZ - landmass.CenterZ;
            Assert.True(dx * dx + dz * dz >= landmass.Radius * landmass.Radius,
                $"island {island} stands inside the landmass {landmass}");
        }
    }

    [Fact]
    public void BrokenOffIslandsDriftInTheFringeAroundALandmass()
    {
        var landmass = Biggest();
        var fringe = SkyLandmasses.FringeOf(landmass);
        var cell = SkyIslandConfig.IslandCellBlocks;
        var cx = (int)MathF.Floor(landmass.CenterX / cell);
        var cz = (int)MathF.Floor(landmass.CenterZ / cell);
        var span = (int)((landmass.Radius + fringe) / cell) + 2;
        var inFringe = 0;
        for (var gx = cx - span; gx <= cx + span; gx++)
        for (var gz = cz - span; gz <= cz + span; gz++)
        {
            if (!SkyIslands.TryGetIsland(gx, gz, out var island)) continue;
            var dx = island.CenterX - landmass.CenterX;
            var dz = island.CenterZ - landmass.CenterZ;
            if (dx * dx + dz * dz < (landmass.Radius + fringe) * (landmass.Radius + fringe)
                && dx * dx + dz * dz >= landmass.Radius * landmass.Radius)
                inFringe++;
        }

        Assert.True(inFringe > 0, "no island drifts around the landmass");
    }

    [Fact]
    public void TheStampOfTwoNeighboringChunksEqualsThePointQuery()
    {
        var landmass = Biggest();
        var x0 = ((int)MathF.Floor(landmass.CenterX) >> Chunk.SizeShift) * Chunk.Size;
        var z0 = ((int)MathF.Floor(landmass.CenterZ) >> Chunk.SizeShift) * Chunk.Size;
        var y0 = (landmass.BaseY >> Chunk.SizeShift) * Chunk.Size;

        var written = 0;
        for (var half = -1; half <= 0; half++)
        {
            var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
            var chunkX0 = x0 + half * Chunk.Size;
            SkyIslandGenerator.Stamp(blocks, chunkX0, y0, z0);
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var expected = SkyIslandGenerator.BlockAt(chunkX0 + x, y0 + y, z0 + z);
                Assert.Equal(expected, blocks[Chunk.Idx(x, y, z)]);
                if (expected != BlockType.Air) written++;
            }
        }

        Assert.True(written > 0, "the sampled chunks hold no landmass");
    }

    [Fact]
    public void ALandmassIsItsOwnZoneAndAGreaterOneIsHarder()
    {
        var all = Landmasses(12);
        var small = all.OrderBy(l => l.Radius).First();
        var big = all.OrderBy(l => l.Radius).Last();
        var smallZone = SkyIslands.ZoneAt(small.CenterX, small.CenterZ);
        var bigZone = SkyIslands.ZoneAt(big.CenterX, big.CenterZ);
        Assert.True(smallZone.InZone && bigZone.InZone);
        Assert.True(bigZone.Tier >= smallZone.Tier);
    }

    [Fact]
    public void ALandmassChangesBiomeAcrossItsLengthOnlyThroughTheNoise()
    {
        var landmass = Biggest();
        foreach (var (x, z) in new[] { (landmass.CenterX + 50f, landmass.CenterZ), (landmass.CenterX - 400f, landmass.CenterZ + 90f) })
            Assert.Equal(SkyBiomes.At(x, z), SkyBiomes.OfColumn(landmass, x, z));
    }
}
