using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Effects.Lighting;
using Xunit;

namespace VoxelEngine.Tests.Effects.Lighting;

/// <summary>The per-cell campfire flicker phase and curve (<c>LightFlicker</c>).</summary>
public sealed class LightFlickerTests
{
    [Fact]
    public void PhaseIsDeterministicPerCellAndInRange()
    {
        var rng = new Random(1234);
        for (var n = 0; n < 500; n++)
        {
            var cell = new Vector3(rng.Next(-5000, 5000), rng.Next(-200, 200), rng.Next(-5000, 5000));
            var phase = LightFlicker.CellPhase(cell);
            Assert.Equal(phase, LightFlicker.CellPhase(cell));
            // Any point inside the same block resolves to the same cell and so the same phase
            var inside = cell + new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble()) * 0.99f;
            Assert.Equal(phase, LightFlicker.CellPhase(inside));
            Assert.InRange(phase, 0f, MathF.Tau);
            Assert.True(phase < MathF.Tau);
        }
    }

    [Fact]
    public void AdjacentCellsDoNotShareOnePhase()
    {
        var phases = new HashSet<float>();
        for (var x = 0; x < 16; x++)
            phases.Add(LightFlicker.CellPhase(new Vector3(x, 10, -3)));
        Assert.True(phases.Count > 1);
    }

    [Fact]
    public void DifferentPhasesFlickerOutOfStep()
    {
        var a = LightFlicker.CellPhase(new Vector3(0, 0, 0));
        var b = LightFlicker.CellPhase(new Vector3(1, 0, 0));
        Assert.NotEqual(a, b);
        var differs = false;
        for (var t = 0f; t < 10f && !differs; t += 0.05f)
            differs = MathF.Abs(LightFlicker.Campfire(t, a) - LightFlicker.Campfire(t, b)) > 1e-3f;
        Assert.True(differs);
    }

    [Fact]
    public void FlickerNeverGoesNegative()
    {
        for (var phase = 0f; phase < MathF.Tau; phase += 0.1f)
            for (var t = 0f; t < 60f; t += 0.037f)
                Assert.True(LightFlicker.Campfire(t, phase) > 0f);
    }
}
