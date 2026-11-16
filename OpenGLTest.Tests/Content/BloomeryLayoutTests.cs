using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Models;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Content.Stations;
using VoxelEngine.Particles;
using VoxelEngine.Simulation.Smithing;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>
/// The bloomery's prop layout against its model: the fuel bed and the blooms lie inside the tapping arch, the ore
/// charge inside the throat, each on a surface the model really has; the tuyere nozzles face the cells the
/// profile names; and the curves that drive the charge, the glow and the bellows blast only ever move one way.
/// </summary>
public sealed class BloomeryLayoutTests
{
    private const float Epsilon = 1e-4f;

    public BloomeryLayoutTests() => TestContent.EnsureLoaded();

    private static BlockType Bloomery => ContentRegistry.RequireBlock("bloomery");

    private static (float Min, float Max) Range(BakedQuad quad, Func<Vector3, float> axis) =>
        (quad.Positions.Min(axis), quad.Positions.Max(axis));

    private static bool StandsOn(BakedModel model, Vector3 foot) =>
        model.Quads.Any(q => q.Face == 4
            && MathF.Abs(Range(q, p => p.Y).Min - foot.Y) <= Epsilon
            && Range(q, p => p.X).Min <= foot.X + Epsilon && Range(q, p => p.X).Max >= foot.X - Epsilon
            && Range(q, p => p.Z).Min <= foot.Z + Epsilon && Range(q, p => p.Z).Max >= foot.Z - Epsilon);

    [Fact]
    public void TheLayoutIsDrawnOverTheBloomerysOwnVolume() =>
        Assert.Equal(BloomeryLayout.VolumeSize, Bloomery.MultiblockSize);

    [Fact]
    public void FuelAndBloomsLieInsideTheArchAndTheChargeInsideTheThroat()
    {
        var profile = Bloomery.Bloomery!;
        var model = Bloomery.RotatedModels![0];
        var half = KilnRenderer.FuelSize * 0.5f;

        void InArch(Vector3 foot, string what)
        {
            Assert.True(foot.X - half >= BloomeryLayout.ArchMinX - Epsilon && foot.X + half <= BloomeryLayout.ArchMaxX + Epsilon,
                $"{what} sticks out of the arch sideways");
            Assert.True(foot.Z - half >= BloomeryLayout.ArchFrontZ - Epsilon && foot.Z + half <= BloomeryLayout.ArchBackZ + Epsilon,
                $"{what} sticks out of the arch's depth");
            Assert.True(StandsOn(model, foot), $"{what} at {foot} stands on nothing");
        }

        // Every fill of the fuel bed, every unit of it, and no two units sharing a plane
        for (var count = 1; count <= profile.FuelCap; count++)
        for (var i = 0; i < count; i++)
        {
            var slot = BloomeryLayout.FuelSlotLocal(i, count);
            InArch(slot, $"fuel unit {i} of {count}");
            for (var j = 0; j < i; j++)
            {
                var apart = Vector3.Abs(slot - BloomeryLayout.FuelSlotLocal(j, count));
                Assert.True(apart.X >= KilnRenderer.FuelSize - Epsilon || apart.Z >= KilnRenderer.FuelSize - Epsilon);
            }
            // The blooms pile up in front of the bed, never on it
            Assert.True(slot.Z - half >= BloomeryLayout.BloomLocal.Z + half - Epsilon);
        }

        InArch(BloomeryLayout.BloomLocal, "the bloom pile");

        // The charge lies on the throat's floor, inside the well
        var charge = BloomeryLayout.ChargeLocal;
        Assert.True(charge.X - half >= BloomeryLayout.ThroatMin && charge.X + half <= BloomeryLayout.ThroatMax);
        Assert.True(charge.Z - half >= BloomeryLayout.ThroatMin && charge.Z + half <= BloomeryLayout.ThroatMax);
        Assert.True(StandsOn(model, charge), "the charge stands on nothing");

        // The flame burns inside the arch, under the lintel, and the throat smokes on top of the stack
        Assert.InRange(BloomeryLayout.FireLocal.X, BloomeryLayout.ArchMinX, BloomeryLayout.ArchMaxX);
        Assert.InRange(BloomeryLayout.FireLocal.Z, BloomeryLayout.ArchFrontZ, BloomeryLayout.ArchBackZ);
        Assert.InRange(BloomeryLayout.FireLocal.Y, BloomeryLayout.ArchFloorY, BloomeryLayout.ArchCeilingY);
        Assert.Equal(BloomeryLayout.VolumeSize, BloomeryLayout.ThroatLocal.Y, precision: 4);
        Assert.InRange(BloomeryLayout.ThroatLocal.X, BloomeryLayout.ThroatMin, BloomeryLayout.ThroatMax);
        Assert.InRange(BloomeryLayout.ThroatLocal.Z, BloomeryLayout.ThroatMin, BloomeryLayout.ThroatMax);
    }

    /// <summary>The model reaches the face of the volume beside every tuyere cell the profile names, inside
    /// that cell's own height and depth: a nozzle the bellows there can blow into.</summary>
    [Fact]
    public void ANozzleFacesEveryTuyereCell()
    {
        var size = Bloomery.MultiblockSize;
        var model = Bloomery.RotatedModels![0];
        foreach (var (x, y, z) in Bloomery.Bloomery!.Tuyeres)
        {
            // The tuyere cell lies beside the volume in x or in z; the nozzle's end is the model quad on that
            // face of the volume, looking at the cell
            var besideX = x < 0 || x >= size;
            var plane = besideX ? (x < 0 ? 0f : size) : (z < 0 ? 0f : size);
            var face = besideX ? (x < 0 ? 2 : 3) : (z < 0 ? 0 : 1);
            Assert.Contains(model.Quads, q =>
            {
                if (q.Face != face) return false;
                var across = besideX ? Range(q, p => p.Z) : Range(q, p => p.X);
                var along = besideX ? Range(q, p => p.X) : Range(q, p => p.Z);
                var height = Range(q, p => p.Y);
                var cellAcross = besideX ? z : x;
                return MathF.Abs(along.Min - plane) <= Epsilon
                       && across.Min >= cellAcross - Epsilon && across.Max <= cellAcross + 1 + Epsilon
                       && height.Min >= y - Epsilon && height.Max <= y + 1 + Epsilon;
            });
        }
    }

    /// <summary>The charge in the throat sinks without a jump: the top piece shrinks as its unit smelts and is
    /// gone exactly when the unit is done, the pieces under it stay whole, and nothing draws for no ore.</summary>
    [Fact]
    public void TheChargeSinksAsItsTopUnitSmelts()
    {
        Assert.Equal(0, BloomeryLayout.ChargeStack(0, 0.5f).Count);

        var previous = float.MaxValue;
        for (var step = 0; step <= 10; step++)
        {
            var (count, top) = BloomeryLayout.ChargeStack(3, step / 10f);
            Assert.Equal(3, count);
            Assert.InRange(top, 0f, 1f);
            Assert.True(top < previous);
            previous = top;
        }
        Assert.Equal(1f, BloomeryLayout.ChargeStack(3, 0f).TopScale, precision: 5);
        Assert.Equal(0f, BloomeryLayout.ChargeStack(3, 1f).TopScale, precision: 5);
    }

    /// <summary>The glow never dims as the fire heats, a dead fire still shows, a fire too cold to smelt is
    /// visibly duller than a well worked one, and the top of the scale is the full glow.</summary>
    [Fact]
    public void TheGlowFollowsTheHeat()
    {
        var previous = 0f;
        for (var step = 0; step <= 20; step++)
        {
            var glow = BloomeryRules.GlowAlpha(step / 20f);
            Assert.InRange(glow, 0f, 1f);
            Assert.True(glow >= previous);
            previous = glow;
        }
        Assert.True(BloomeryRules.GlowAlpha(0f) > 0f);
        Assert.True(BloomeryRules.GlowAlpha(BloomeryConfig.BandMin - 0.01f) < BloomeryRules.GlowAlpha(1f));
        Assert.Equal(1f, BloomeryRules.GlowAlpha(1f), precision: 5);
    }

    /// <summary>A bellows blast: the heat lost between two frames or a rounding wobble throws nothing, a real
    /// landing throws sparks, a harder one never fewer, and the burst is capped.</summary>
    [Fact]
    public void OnlyARealRiseInHeatThrowsSparksAndAHarderBlowThrowsMore()
    {
        Assert.Equal(0, FurnaceEffects.BlastSparks(-0.2f));
        Assert.Equal(0, FurnaceEffects.BlastSparks(0f));
        Assert.Equal(0, FurnaceEffects.BlastSparks(ParticleConfig.BellowsBlastMinRise * 0.5f));

        // The weakest landing that counts on the bellows is already a blast
        var weakest = BloomeryRules.LandingHeat(BloomeryConfig.BellowsMinFallBlocks);
        Assert.True(FurnaceEffects.BlastSparks(weakest) > 0);

        var previous = 0;
        for (var rise = ParticleConfig.BellowsBlastMinRise; rise <= 1f; rise += 0.05f)
        {
            var sparks = FurnaceEffects.BlastSparks(rise);
            Assert.True(sparks >= previous);
            Assert.True(sparks <= ParticleConfig.BellowsBlastMaxSparks);
            previous = sparks;
        }
    }
}
