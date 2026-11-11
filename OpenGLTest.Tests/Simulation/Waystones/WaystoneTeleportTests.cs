using VoxelEngine.Player;
using VoxelEngine.Simulation.Waystones;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Waystones;

/// <summary>
/// The waystone teleport rule: only to another waystone this player discovered whose block still stands, only with
/// mana strictly over half unless creative or flying, and an allowed trip drains the pool while a refused one changes
/// nothing.
/// </summary>
public sealed class WaystoneTeleportTests
{
    private static readonly (int X, int Y, int Z) Here = (0, 10, 0);
    private static readonly (int X, int Y, int Z) There = (500, -350, 40);

    private static DiscoveredWaystones Known()
    {
        var known = new DiscoveredWaystones();
        known.Add(Here.X, Here.Y, Here.Z, "Home");
        known.Add(There.X, There.Y, There.Z, "Far");
        return known;
    }

    [Fact]
    public void AFullPoolTravelsToADiscoveredStandingWaystoneAndPaysItAll()
    {
        var mana = new PlayerMana();

        var verdict = WaystoneTeleport.TryPay(Known(), new WaystoneRecord("Far", "@local"), Here, There, free: false, mana);

        Assert.Equal(WaystoneTeleportVerdict.Teleport, verdict);
        Assert.Equal(0f, mana.Current);
    }

    [Fact]
    public void AFreeTravelerKeepsItsMana()
    {
        var mana = new PlayerMana();
        mana.Restore(1f);

        var verdict = WaystoneTeleport.TryPay(Known(), new WaystoneRecord("Far", "@local"), Here, There, free: true, mana);

        Assert.Equal(WaystoneTeleportVerdict.Teleport, verdict);
        Assert.Equal(1f, mana.Current);
        Assert.True(WaystoneTeleport.IsFree(creative: true, flying: false));
        Assert.True(WaystoneTeleport.IsFree(creative: false, flying: true));
        Assert.False(WaystoneTeleport.IsFree(creative: false, flying: false));
    }

    [Fact]
    public void HalfAPoolIsNotEnoughAndARefusalSpendsNothing()
    {
        var mana = new PlayerMana();
        mana.Restore(mana.Max / 2);
        var before = mana.Current;

        var verdict = WaystoneTeleport.TryPay(Known(), new WaystoneRecord("Far", "@local"), Here, There, free: false, mana);

        Assert.Equal(WaystoneTeleportVerdict.NeedsMana, verdict);
        Assert.Equal(before, mana.Current);
    }

    [Fact]
    public void TheDestinationMustBeAnotherDiscoveredStandingWaystone()
    {
        var mana = new PlayerMana();
        var record = new WaystoneRecord("Far", "@local");

        Assert.Equal(WaystoneTeleportVerdict.Current,
            WaystoneTeleport.TryPay(Known(), record, Here, Here, free: false, mana));
        Assert.Equal(WaystoneTeleportVerdict.NotDiscovered,
            WaystoneTeleport.TryPay(Known(), record, Here, (7, 7, 7), free: false, mana));
        Assert.Equal(WaystoneTeleportVerdict.Unknown,
            WaystoneTeleport.TryPay(Known(), null, Here, There, free: false, mana));
        Assert.Equal(WaystoneTeleportVerdict.Lost,
            WaystoneTeleport.TryPay(Known(), new WaystoneRecord("Far", "@local", lost: true), Here, There, free: true, mana));
        Assert.True(mana.MoreThanHalf);
    }
}
