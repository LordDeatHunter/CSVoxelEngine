using System.Linq;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The selected item's name over the hotbar: a switch of slot or of the item in the selected slot
/// restarts it, the same selection again never does, an empty slot shows nothing and the name only
/// ever fades once shown.
/// </summary>
public sealed class HotbarNameFadeTests
{
    private const float Dt = 1f / 60f;

    private static float Lifetime =>
        WorldGenerationConfig.HotbarNameHoldSeconds + WorldGenerationConfig.HotbarNameFadeSeconds;

    [Fact]
    public void TheFirstObservationShowsNothing()
    {
        var fade = new HotbarNameFade();
        Assert.False(fade.Observe(0, 5, Dt));
        Assert.Equal(0f, fade.Alpha);
    }

    [Fact]
    public void ASlotChangeRestartsTheTimerAndARepeatDoesNot()
    {
        var fade = new HotbarNameFade();
        fade.Observe(0, 5, Dt);

        Assert.True(fade.Observe(1, 7, Dt));
        Assert.Equal(0f, fade.Elapsed);
        Assert.Equal(1f, fade.Alpha);

        // The same slot and item again: the timer keeps running instead of restarting
        for (var i = 0; i < 10; i++)
            Assert.False(fade.Observe(1, 7, Dt));
        Assert.True(fade.Elapsed > 0f);

        // Back to the first slot is a change again
        Assert.True(fade.Observe(0, 5, Dt));
        Assert.Equal(0f, fade.Elapsed);
    }

    [Fact]
    public void AnItemChangeInTheSelectedSlotRestartsTheTimer()
    {
        var fade = new HotbarNameFade();
        fade.Observe(3, 5, Dt);
        for (var i = 0; i < 200; i++)
            fade.Observe(3, 5, Dt);
        Assert.Equal(0f, fade.Alpha);

        Assert.True(fade.Observe(3, 9, Dt));
        Assert.Equal(1f, fade.Alpha);
    }

    [Fact]
    public void AnEmptySlotShowsNothing()
    {
        var fade = new HotbarNameFade();
        fade.Observe(0, 5, Dt);

        // Switching to an empty slot is a change, but there is no name to show
        Assert.True(fade.Observe(4, 0, Dt));
        Assert.Equal(0f, fade.Alpha);
    }

    [Fact]
    public void TheNameOnlyFadesAfterAChangeAndIsGoneAfterItsLifetime()
    {
        var fade = new HotbarNameFade();
        fade.Observe(0, 5, Dt);
        fade.Observe(2, 6, Dt);

        var previous = fade.Alpha;
        Assert.Equal(1f, previous);
        var frames = (int)(Lifetime / Dt) + 10;
        for (var i = 0; i < frames; i++)
        {
            fade.Observe(2, 6, Dt);
            Assert.True(fade.Alpha <= previous, $"alpha rose at frame {i}");
            previous = fade.Alpha;
        }
        Assert.Equal(0f, fade.Alpha);
    }

    [Fact]
    public void AnInventorySwitchNamesTheNewlySelectedItem()
    {
        TestContent.EnsureLoaded();
        var inventory = new Inventory();
        var blocks = BlockType.Types.Values.Where(t => t.HasItem && !t.Fluid).Take(2).ToArray();
        inventory.EquipStackable(0, blocks[0].Id);
        inventory.EquipStackable(1, blocks[1].Id);

        var fade = new HotbarNameFade();
        fade.Update(inventory, Dt);
        Assert.Equal(0f, fade.Alpha);

        inventory.Select(1);
        fade.Update(inventory, Dt);
        Assert.Equal(Inventory.DisplayName(blocks[1].Id), fade.Name);
        Assert.Equal(1f, fade.Alpha);

        // Re-selecting the slot already in hand is not a change
        fade.Update(inventory, 0.5f);
        inventory.Select(1);
        fade.Update(inventory, Dt);
        Assert.True(fade.Elapsed > 0.5f);
    }
}
