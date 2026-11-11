using System.Linq;
using VoxelEngine.Content.Spells;
using VoxelEngine.Magic;
using VoxelEngine.Player;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Magic;

/// <summary>The known-part rule: a fresh player knows nothing but creative knows all, and learning grows the
/// mana pool with the owned count only when something is new.</summary>
public sealed class KnownSpellPartsTests
{
    public KnownSpellPartsTests() => TestContent.EnsureLoaded();

    [Fact]
    public void AFreshInventoryKnowsNoPartUnlessCreative()
    {
        var inventory = Inventory.Empty();

        Assert.NotEmpty(SpellPartRegistry.All);
        foreach (var part in SpellPartRegistry.All)
        {
            Assert.False(KnownSpellParts.Knows(inventory, part, creative: false));
            Assert.True(KnownSpellParts.Knows(inventory, part, creative: true));
        }
    }

    [Fact]
    public void LearningGrowsTheManaMaxWithTheKnownCount()
    {
        var inventory = Inventory.Empty();
        var mana = new PlayerMana();
        var keys = SpellPartRegistry.All.Take(2).Select(p => p.Key).ToArray();
        var before = mana.Max;

        Assert.True(KnownSpellParts.Learn(inventory, keys, mana));

        Assert.All(keys, k => Assert.True(inventory.KnowsPart(k)));
        Assert.Equal(PlayerMana.MaxFor(inventory.KnownPartCount), mana.Max);
        Assert.True(mana.Max >= before);
    }

    [Fact]
    public void LearningOnlyKnownPartsChangesNothing()
    {
        var inventory = Inventory.Empty();
        var mana = new PlayerMana();
        var keys = SpellPartRegistry.All.Take(2).Select(p => p.Key).ToArray();
        KnownSpellParts.Learn(inventory, keys, mana);
        var version = inventory.KnownPartsVersion;
        var max = mana.Max;

        Assert.False(KnownSpellParts.Learn(inventory, keys.Take(1), mana));

        Assert.Equal(version, inventory.KnownPartsVersion);
        Assert.Equal(max, mana.Max);
    }

    [Fact]
    public void LearningWithoutAPoolStillTeaches()
    {
        var inventory = Inventory.Empty();
        var key = SpellPartRegistry.All.First().Key;

        Assert.True(KnownSpellParts.Learn(inventory, [key], mana: null));
        Assert.True(inventory.KnowsPart(key));
    }
}
