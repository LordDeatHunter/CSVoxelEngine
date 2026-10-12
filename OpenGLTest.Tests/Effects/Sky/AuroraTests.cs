using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Effects.Climate;
using VoxelEngine.Effects.Sky;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Effects.Sky;

/// <summary>
/// The aurora's fade and drift (<see cref="Aurora"/>, the twin of sky-fragment.glsl's), and the eye
/// climate it is keyed to (<see cref="EyeClimate"/>): only from cold lands, only at night, never
/// guessed for an unknown column, and drifting with no jump.
/// </summary>
public sealed class AuroraTests
{
    // TerrainGenerator's palette resolves blocks in its static constructor
    public AuroraTests() => TestContent.EnsureLoaded();

    [Fact]
    public void ThereIsNoAuroraByDay()
    {
        for (var e = WorldGenerationConfig.StarsGoneAboveSunElevation; e <= 1f; e += 0.01f)
            foreach (var cold in new[] { 0f, 0.7f, 1f })
                Assert.Equal(0f, Aurora.Visibility(cold, StarField.Visibility(e)));
    }

    [Fact]
    public void ThereIsNoAuroraFromTemperateOrWarmLands()
    {
        // The climate's warmth is exactly 0.5 across the temperate band and above it in the warm lands
        for (var warmth = 0.5f; warmth <= 1f; warmth += 0.01f)
            Assert.Equal(0f, Aurora.Visibility(Aurora.TargetColdness(true, warmth), 1f));
        for (var cold = 0f; cold <= WorldGenerationConfig.AuroraColdStart; cold += 0.01f)
            Assert.Equal(0f, Aurora.Visibility(cold, 1f));
    }

    [Fact]
    public void TheAuroraStrengthensWithTheColdAndTheNight()
    {
        var previous = 0f;
        for (var cold = 0f; cold <= 1f; cold += 0.01f)
        {
            var v = Aurora.Visibility(cold, 1f);
            Assert.True(v >= previous, $"the aurora weakened as it got colder ({cold})");
            previous = v;
        }

        Assert.Equal(1f, Aurora.Visibility(WorldGenerationConfig.AuroraColdFull, 1f), 5);
        previous = 0f;
        for (var stars = 0f; stars <= 1f; stars += 0.01f)
        {
            var v = Aurora.Visibility(1f, stars);
            Assert.True(v >= previous, $"the aurora weakened as the night deepened ({stars})");
            previous = v;
        }
    }

    [Fact]
    public void AnUnknownColumnFadesTheAuroraOutRatherThanGuessing()
    {
        Assert.Equal(0f, Aurora.TargetColdness(false, 0f));
        var cold = 1f;
        var previous = cold;
        for (var i = 0; i < 600; i++)
        {
            cold = Aurora.Ease(cold, Aurora.TargetColdness(false, 0f), 1f / 60f);
            Assert.True(cold <= previous && cold >= 0f, "the eased coldness overshot or turned back");
            previous = cold;
        }

        Assert.True(cold < 0.2f, "ten seconds over an unknown column left the aurora nearly up");
    }

    [Fact]
    public void TheDriftStaysInsideEachPeriodAndNeverJumpsExceptByAWholePeriod()
    {
        var periods = Aurora.Periods;
        var previous = Aurora.Drift(0.0);
        for (var t = 0.0; t < 5000.0; t += 0.25)
        {
            var d = Aurora.Drift(t);
            for (var k = 0; k < 4; k++)
            {
                var p = periods[k];
                Assert.InRange(d[k], 0f, p);
                // A step is tiny, or tiny plus or minus one whole period (the noise repeats there)
                var step = MathF.Abs(d[k] - previous[k]);
                Assert.True(step < 0.1f || MathF.Abs(step - p) < 0.1f, $"noise {k} jumped by {step} cells at t = {t}");
            }

            previous = d;
        }
    }

    [Fact]
    public void TheColdnessAndWarmnessReadBackOutOfTheClimateAreTheBiomeRamps()
    {
        for (var t = -1f; t <= 1f; t += 0.005f)
        {
            // TerrainGenerator.Climate's warmth for a raw temperature t
            var warmth = 0.5f * (1f - TerrainGenerator.ColdOf(t)) + 0.5f * TerrainGenerator.WarmOf(t);
            Assert.Equal(TerrainGenerator.ColdOf(t), EyeClimate.Coldness(warmth), 4);
            Assert.Equal(TerrainGenerator.WarmOf(t), EyeClimate.Warmness(warmth), 4);
        }
    }

    [Fact]
    public void TheEyeClimateIsTheStoredCornerClimateBlendedAcrossItsColumn()
    {
        var source = new CornerSource();
        source.Set(0, 0, 0.1f, 0.9f);
        source.Set(1, 0, 0.3f, 0.7f);
        source.Set(0, 1, 0.2f, 0.4f);
        source.Set(1, 1, 0.6f, 0.2f);

        Assert.True(EyeClimate.TryAt(source, 0f, 0f, out var w, out var h));
        Assert.Equal(0.1f, w, 5);
        Assert.Equal(0.9f, h, 5);
        for (var x = 0.5f; x < Chunk.Size; x += 1.5f)
        for (var z = 0.5f; z < Chunk.Size; z += 1.5f)
        {
            Assert.True(EyeClimate.TryAt(source, x, z, out w, out h));
            Assert.InRange(w, 0.1f - 1e-5f, 0.6f + 1e-5f);
            Assert.InRange(h, 0.2f - 1e-5f, 0.9f + 1e-5f);
        }

        // A column whose corner has not streamed in is unknown, not guessed
        Assert.False(EyeClimate.TryAt(source, Chunk.Size + 1f, 1f, out _, out _));
    }

    private sealed class CornerSource : IColumnSource
    {
        private readonly Dictionary<(int, int), ColumnTerrain> _columns = new();

        public void Set(int cx, int cz, float warmth, float wetness) =>
            _columns[(cx, cz)] = new ColumnTerrain(new int[Chunk.Size * Chunk.Size], new int[Chunk.Size * Chunk.Size], warmth, wetness);

        public bool IsReady(int chunkX, int chunkZ) => _columns.ContainsKey((chunkX, chunkZ));

        public bool TryGet(int chunkX, int chunkZ, out ColumnTerrain terrain) =>
            _columns.TryGetValue((chunkX, chunkZ), out terrain);
    }
}
