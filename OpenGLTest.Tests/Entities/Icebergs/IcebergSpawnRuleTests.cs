using VoxelEngine.Entities.Icebergs;
using Xunit;

namespace VoxelEngine.Tests.Entities.Icebergs;

public sealed class IcebergSpawnRuleTests
{
    private static float[] Colds()
    {
        var colds = new float[101];
        for (var i = 0; i < colds.Length; i++)
            colds[i] = i / 100f;
        return colds;
    }

    [Fact]
    public void FrozenWaterNeverQualifies()
    {
        foreach (var cold in Colds())
            Assert.False(IcebergSpawnRule.InBand(cold, frozen: true));
    }

    [Fact]
    public void ColdBeyondTheSolidSheetNeverQualifies()
    {
        foreach (var cold in Colds())
            if (cold > WorldGenerationConfig.FreezeColdThreshold)
                Assert.False(IcebergSpawnRule.InBand(cold, frozen: false));
    }

    [Fact]
    public void TheBandReachesIntoThePackIceBand()
    {
        Assert.True(IcebergConfig.SpawnColdMin <= WorldGenerationConfig.PackIceColdStart);
        Assert.True(WorldGenerationConfig.PackIceColdStart < WorldGenerationConfig.FreezeColdThreshold);

        // The open leads inside the pack-ice band qualify.
        var inside = 0.5f * (WorldGenerationConfig.PackIceColdStart + WorldGenerationConfig.FreezeColdThreshold);
        Assert.True(IcebergSpawnRule.InBand(inside, frozen: false));
        // So does open sea at the start of the pack-ice band.
        Assert.True(IcebergSpawnRule.InBand(WorldGenerationConfig.PackIceColdStart, frozen: false));
    }

    [Fact]
    public void TheBandIsAnInterval()
    {
        var colds = Colds();
        for (var a = 0; a < colds.Length; a++)
        for (var c = a + 2; c < colds.Length; c++)
        {
            if (!IcebergSpawnRule.InBand(colds[a], false) || !IcebergSpawnRule.InBand(colds[c], false)) continue;
            for (var b = a + 1; b < c; b++)
                Assert.True(IcebergSpawnRule.InBand(colds[b], false));
        }
    }
}
