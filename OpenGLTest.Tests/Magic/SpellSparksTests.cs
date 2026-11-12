using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Spells;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Magic;
using VoxelEngine.Player;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Magic;

/// <summary>The shared spell sparks (<see cref="SpellSparks"/>): the element's color and where a cast bursts.</summary>
public sealed class SpellSparksTests
{
    public SpellSparksTests() => TestContent.EnsureLoaded();

    private static SpellPart[] Elements() =>
        SpellPartRegistry.All.Where(p => p.Kind == SpellPartKind.Element).ToArray();

    [Fact]
    public void ATintedElementsSparksWearItsEffectTint()
    {
        var tinted = Elements()
            .Where(e => e.Effect != null && StatusEffectRegistry.Require(e.Effect).TintLinear != null).ToArray();
        Assert.True(tinted.Length >= 2);

        foreach (var element in tinted)
            Assert.Equal(ColorSpace.LinearToSrgb(StatusEffectRegistry.Require(element.Effect!).TintLinear!.Value),
                SpellSparks.ColorOf(element));
        Assert.NotEqual(SpellSparks.ColorOf(tinted[0]), SpellSparks.ColorOf(tinted[1]));
    }

    [Fact]
    public void AnElementWithoutATintedEffectSparksInTheDefaultColor()
    {
        var untinted = Elements()
            .Where(e => e.Effect == null || StatusEffectRegistry.Require(e.Effect).TintLinear == null).ToArray();
        Assert.NotEmpty(untinted);
        Assert.All(untinted, e => Assert.Equal(SpellConfig.BoltDefaultColor, SpellSparks.ColorOf(e)));
    }

    [Theory]
    [InlineData(1f, 0f, 0f)]
    [InlineData(0.3f, -0.4f, 0.8f)]
    [InlineData(0f, 0f, 0f)]
    public void TheBurstOriginIsTheBoltsLaunchPointOnTheAimRay(float x, float y, float z)
    {
        var eye = new Vector3(3f, 70f, -2f);
        var direction = new Vector3(x, y, z);
        var bolt = new SpellProjectile(new PlayerContext(Inventory.Empty()), Elements()[0],
            new SpellRecipe([Elements()[0]]).Compile(), eye, direction);

        var origin = SpellSparks.BurstOrigin(eye, direction);

        Assert.True(Vector3.Distance(bolt.Origin, origin) < 1e-5f);
        // In front of the eye, along the aim
        var aim = direction.LengthSquared() > 0f ? Vector3.Normalize(direction) : -Vector3.UnitZ;
        Assert.True(Vector3.Dot(origin - eye, aim) > 0f);
    }
}
