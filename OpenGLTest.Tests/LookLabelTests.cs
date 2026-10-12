using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The look-at label's rules (<see cref="LookLabel"/>): a block reads as "Unknown" until its item
/// has been obtained, a block with no item is always known, creative mode always knows everything,
/// the name is flagged red only when the held item cannot mine the block, and the label's
/// top-of-screen placement (<see cref="UIRenderer.LookLabelTop"/>) sits below the compass or clock
/// strip while one is equipped.
/// </summary>
public sealed class LookLabelTests
{
    public LookLabelTests() => TestContent.EnsureLoaded();

    private static byte Block(string name) => ContentRegistry.RequireBlock(name).Id;

    [Fact]
    public void ABlockIsUnknownUntilItsItemIsObtained()
    {
        var stone = Block("stone");
        var known = new System.Collections.Generic.HashSet<byte>();

        var unknown = LookLabel.For(stone, creative: false, known.Contains, held: null);
        Assert.Equal("Unknown", unknown.Text);
        Assert.False(unknown.Known);

        known.Add(stone);
        var knownState = LookLabel.For(stone, creative: false, known.Contains, held: null);
        Assert.Equal("Stone", knownState.Text);
        Assert.True(knownState.Known);

        // No target reads as an empty label
        Assert.Equal("", LookLabel.For(BlockType.Air, creative: false, known.Contains, held: null).Text);
    }

    [Fact]
    public void CreativeAndItemlessBlocksAreAlwaysKnown()
    {
        var stone = Block("stone");
        var creative = LookLabel.For(stone, creative: true, _ => false, held: null);
        Assert.True(creative.Known);
        Assert.Equal("Stone", creative.Text);

        // A block with no item at all (pebbles) is always known, even in survival
        var pebble = Block("pebble");
        Assert.False(BlockType.ById[pebble]!.HasItem);
        var state = LookLabel.For(pebble, creative: false, _ => false, held: null);
        Assert.True(state.Known);
    }

    [Fact]
    public void TheNameTurnsRedWhenTheHeldItemCannotMineIt()
    {
        var stone = Block("stone");
        var bare = LookLabel.For(stone, creative: false, _ => true, held: null);
        Assert.False(bare.Mineable);

        var pickaxe = ContentRegistry.RequireItem("primitive_pickaxe");
        var armed = LookLabel.For(stone, creative: false, _ => true, held: pickaxe);
        Assert.True(armed.Mineable);

        // Creative can always mine, whatever is held
        Assert.True(LookLabel.For(stone, creative: true, _ => true, held: null).Mineable);
    }

    [Fact]
    public void TheCompassStripPushesTheLabelBelowIt()
    {
        var free = UIRenderer.LookLabelTop(compass: false, clock: false, coordinates: false);
        var clock = UIRenderer.LookLabelTop(compass: false, clock: true, coordinates: false);
        var compass = UIRenderer.LookLabelTop(compass: true, clock: false, coordinates: false);
        var withCoords = UIRenderer.LookLabelTop(compass: true, clock: false, coordinates: true);

        // Equipping the compass, or the clock, which shares the strip, pushes the label down;
        // the compass's coordinates line pushes it further; with neither it takes the free top
        Assert.True(clock > free);
        Assert.True(compass > free);
        Assert.True(withCoords > compass);

        // The compass owns the strip when both are equipped, like the draw itself, and a clock
        // has no coordinates line to clear
        Assert.Equal(compass, UIRenderer.LookLabelTop(compass: true, clock: true, coordinates: false));
        Assert.Equal(clock, UIRenderer.LookLabelTop(compass: false, clock: true, coordinates: true));
    }
}
