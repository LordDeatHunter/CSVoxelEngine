using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The rules behind a TNT blast's side effects: the camera shake falls off with distance
/// (<see cref="ShakeFalloff"/>), and <see cref="TntManager"/> reports each detonation it set off.
/// </summary>
public sealed class BlastEffectTests
{
    public BlastEffectTests() => TestContent.EnsureLoaded();

    private const float Strength = 1f;
    private const float Near = 4f;
    private const float Range = 48f;

    [Fact]
    public void TheShakeNeverGrowsWithDistance()
    {
        var previous = float.MaxValue;
        for (var d = 0f; d <= Range + 8f; d += 0.25f)
        {
            var trauma = ShakeFalloff.Trauma(Strength, Near, Range, d);
            Assert.True(trauma <= previous, $"the shake grew at {d} blocks");
            previous = trauma;
        }
    }

    [Fact]
    public void TheShakeIsZeroFromItsRangeOnAndFullInsideTheNearRadius()
    {
        Assert.Equal(0f, ShakeFalloff.Trauma(Strength, Near, Range, Range));
        Assert.Equal(0f, ShakeFalloff.Trauma(Strength, Near, Range, Range * 3f));
        Assert.True(ShakeFalloff.Trauma(Strength, Near, Range, 0f) > ShakeFalloff.Trauma(Strength, Near, Range, Near * 3f));
        // A strength under the cap is honoured as is right at the event
        Assert.Equal(0.5f, ShakeFalloff.Trauma(0.5f, Near, Range, 0f), 3);
    }

    [Fact]
    public void ADetonationIsReportedForTheUpdateThatSetItOff()
    {
        var (manager, _) = MeshTestWorld.WalledArea();
        var tnt = new TntManager();
        tnt.Spawn(new Vector3(8, 8, 8), ContentRegistry.RequireBlock("tnt").Id, 0f);
        var lod = new LodManager(null!);
        var drops = new DropManager();
        var fluids = new FluidManager();
        var falling = new FallingBlockManager();

        tnt.Update(1e-4f, manager, lod, drops, fluids, falling);
        var blast = Assert.Single(tnt.Blasts);
        Assert.True(Vector3.Distance(blast, new Vector3(8.5f)) < 0.1f);

        tnt.Update(1e-4f, manager, lod, drops, fluids, falling);
        Assert.Empty(tnt.Blasts);
    }
}
