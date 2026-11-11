using VoxelEngine.Content.Spells;
using VoxelEngine.Items;
using VoxelEngine.Magic;
using VoxelEngine.Player;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Magic;

/// <summary>
/// The cast judgement: every refusal leaves the mana and the pacing untouched, an accepted cast bills exactly
/// the compiled cost (nothing in creative, nothing when only predicting) and starts the cooldown.
/// </summary>
public sealed class CastRulesTests
{
    private const double Now = 50.0;

    public CastRulesTests() => TestContent.EnsureLoaded();

    private static CastVerdict Cast(PlayerContext player, int slot, CastPacing pacing, double now = Now,
        bool spend = true) =>
        CastRules.TryCast(player, slot, pacing, now, 0.0, spend, out _);

    // Asserts the refusal and that it spent nothing, then that the fixed-up cast at the same moment goes
    // through, which it could not if the refusal had marked the pacing
    private static void AssertRefusedUntouched(PlayerContext player, int slot, CastPacing pacing,
        CastVerdict expected)
    {
        var mana = player.Health!.Mana.Current;
        Assert.Equal(expected, Cast(player, slot, pacing));
        Assert.Equal(mana, player.Health.Mana.Current);
    }

    private static PlayerContext BoundPlayer(out StaffItemSetup setup)
    {
        var inventory = StaffFixture.Holding("wooden_staff", out var staff, out var stack);
        var stats = StaffFixture.Bind(staff, stack, 0, out var parts);
        setup = new StaffItemSetup(staff, stack, stats, parts);
        return StaffFixture.Player(inventory);
    }

    private readonly record struct StaffItemSetup(StaffItem Staff, ItemStack Stack, SpellStats Stats,
        SpellPart[] Parts);

    [Fact]
    public void NoStaffInHandIsRefused()
    {
        var player = BoundPlayer(out _);
        var staffSlot = player.Inventory.SelectedIndex;
        player.Inventory.Select(staffSlot + 1);
        var pacing = new CastPacing();
        AssertRefusedUntouched(player, 0, pacing, CastVerdict.NoStaff);
        player.Inventory.Select(staffSlot);
        Assert.Equal(CastVerdict.Cast, Cast(player, 0, pacing));
    }

    [Fact]
    public void ASlotThatIsNotSelectedOrOutOfRangeIsRefused()
    {
        var player = BoundPlayer(out var setup);
        var pacing = new CastPacing();
        foreach (var slot in new[] { 1, -1, setup.Staff.Slots, SpellConfig.StaffMaxSlots })
            AssertRefusedUntouched(player, slot, pacing, CastVerdict.WrongSlot);

        Assert.Equal(CastVerdict.Cast, Cast(player, 0, pacing));
    }

    [Fact]
    public void AnUnboundSlotIsRefused()
    {
        var inventory = StaffFixture.Holding("wooden_staff", out var staff, out var stack);
        var player = StaffFixture.Player(inventory);
        var pacing = new CastPacing();
        AssertRefusedUntouched(player, 0, pacing, CastVerdict.Unbound);

        StaffFixture.Bind(staff, stack, 0, out _);
        Assert.Equal(CastVerdict.Cast, Cast(player, 0, pacing));
    }

    [Fact]
    public void AnUnknownPartIsRefused()
    {
        var player = BoundPlayer(out var setup);
        var good = setup.Stack.Get<StaffComponent>();
        setup.Stack.Set(new StaffComponent([["element/no_such_part", setup.Parts[1].Key]], 0));
        var pacing = new CastPacing();
        AssertRefusedUntouched(player, 0, pacing, CastVerdict.UnknownPart);

        setup.Stack.Set(good);
        Assert.Equal(CastVerdict.Cast, Cast(player, 0, pacing));
    }

    [Fact]
    public void ARecipeThatDoesNotCompileValidIsRefused()
    {
        var player = BoundPlayer(out var setup);
        var good = setup.Stack.Get<StaffComponent>();
        // An element alone, written straight into the component as an old save or a tampered one could hold it
        setup.Stack.Set(new StaffComponent([[setup.Parts[0].Key]], 0));
        var pacing = new CastPacing();
        AssertRefusedUntouched(player, 0, pacing, CastVerdict.InvalidRecipe);

        setup.Stack.Set(good);
        Assert.Equal(CastVerdict.Cast, Cast(player, 0, pacing));
    }

    [Fact]
    public void AnAcceptedCastBillsExactlyTheCompiledCostAndStartsTheCooldown()
    {
        var player = BoundPlayer(out var setup);
        var pacing = new CastPacing();
        var before = player.Health!.Mana.Current;
        Assert.True(setup.Stats.Cost > 0f);

        Assert.Equal(CastVerdict.Cast, CastRules.TryCast(player, 0, pacing, Now, 0.0, spend: true, out var cast));
        Assert.Equal(before - setup.Stats.Cost, player.Health.Mana.Current, 4);
        Assert.Equal(setup.Stats, cast.Stats);
        Assert.Same(setup.Staff, cast.Staff);
        Assert.Equal(0, cast.Slot);
        Assert.Equal(player.Eye, cast.Eye);
        Assert.Equal(player.Forward, cast.Direction);

        // The cooldown refuses at once and spends nothing; once it has passed the next cast goes through
        AssertRefusedUntouched(player, 0, pacing, CastVerdict.Cooldown);
        Assert.Equal(CastVerdict.Cast, Cast(player, 0, pacing, Now + SpellConfig.CastCooldownSeconds));
    }

    [Fact]
    public void ACastTheManaCannotCoverIsRefused()
    {
        var player = BoundPlayer(out _);
        var mana = player.Health!.Mana;
        Assert.True(mana.TrySpend(mana.Current, creative: false));
        var pacing = new CastPacing();
        AssertRefusedUntouched(player, 0, pacing, CastVerdict.NoMana);

        mana.Refill();
        Assert.Equal(CastVerdict.Cast, Cast(player, 0, pacing));
    }

    [Fact]
    public void ACasterWithoutAManaPoolIsRefused()
    {
        var player = BoundPlayer(out _);
        var poolless = new PlayerContext(player.Inventory);
        Assert.Equal(CastVerdict.NoMana, Cast(poolless, 0, new CastPacing()));
    }

    [Fact]
    public void ACreativeCastIsFree()
    {
        var bound = BoundPlayer(out _);
        var player = StaffFixture.Player(bound.Inventory, creative: true);
        var mana = player.Health!.Mana;
        mana.TrySpend(mana.Current, creative: false);

        Assert.Equal(CastVerdict.Cast, Cast(player, 0, new CastPacing()));
        Assert.Equal(0f, mana.Current);
    }

    [Fact]
    public void APredictionNeverSpendsButStillRefusesAShortPool()
    {
        var player = BoundPlayer(out _);
        var mana = player.Health!.Mana;
        var before = mana.Current;
        var pacing = new CastPacing();

        Assert.Equal(CastVerdict.Cast, Cast(player, 0, pacing, spend: false));
        Assert.Equal(before, mana.Current);
        Assert.Equal(CastVerdict.Cooldown, Cast(player, 0, pacing, spend: false));

        mana.TrySpend(mana.Current, creative: false);
        Assert.Equal(CastVerdict.NoMana, Cast(player, 0, new CastPacing(), spend: false));
        Assert.Equal(0f, mana.Current);
    }
}
