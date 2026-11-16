using System.Numerics;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Player;
using Xunit;

namespace VoxelEngine.Tests.Networking.ServerSide;

/// <summary>
/// The server reads a landing from accepted states alone (it never simulates a body): the drop from the highest point
/// since the body left the ground. That is what works a bloomery's bellows for a connected player, so taking off,
/// rising, a teleport and flight must never report one, and a landing must report its drop once.
/// </summary>
public sealed class ServerPlayerLandingTests
{
    public ServerPlayerLandingTests() => TestContent.EnsureLoaded();

    private static ChunkManager StoneFloor() => MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);

    private static Vector3 EyeAtFeet(float feet) => new(8.5f, feet + Camera.EyeHeight, 8.5f);

    private static float Observe(ServerPlayer player, ChunkManager chunks, float feet)
    {
        player.Position = EyeAtFeet(feet);
        return player.ObserveAcceptedState(chunks, NetworkConfig.NetPlayerStateSeconds);
    }

    [Fact]
    public void AJumpReportsItsDropOnceWhenItLandsAndNotBefore()
    {
        var chunks = StoneFloor();
        var player = new ServerPlayer(1, "Jumper", EyeAtFeet(1f));

        Assert.Equal(0f, Observe(player, chunks, 1f));
        Assert.Equal(0f, Observe(player, chunks, 1.6f));
        Assert.Equal(0f, Observe(player, chunks, 2.1f));
        Assert.Equal(0f, Observe(player, chunks, 1.5f));

        Assert.Equal(1.1f, Observe(player, chunks, 1f), 3);
        // Standing on: nothing more
        Assert.Equal(0f, Observe(player, chunks, 1f));
    }

    [Fact]
    public void AHigherFallReportsALargerDrop()
    {
        var chunks = StoneFloor();
        var low = new ServerPlayer(1, "Low", EyeAtFeet(2f));
        var high = new ServerPlayer(2, "High", EyeAtFeet(4f));
        Observe(low, chunks, 2f);
        Observe(high, chunks, 4f);
        Observe(high, chunks, 2.5f);

        Assert.True(Observe(high, chunks, 1f) > Observe(low, chunks, 1f));
    }

    [Fact]
    public void ATeleportForgetsTheFallSoItLandsNothing()
    {
        var chunks = StoneFloor();
        var player = new ServerPlayer(1, "Warped", EyeAtFeet(3f));
        Assert.Equal(0f, Observe(player, chunks, 3f));

        player.Teleport(EyeAtFeet(1f));
        Assert.Equal(0f, Observe(player, chunks, 1f));
    }

    [Fact]
    public void AFlyingPlayerNeverLands()
    {
        var chunks = StoneFloor();
        var player = new ServerPlayer(1, "Flier", EyeAtFeet(2f)) { Creative = true, Flying = true };

        Assert.Equal(0f, Observe(player, chunks, 2f));
        Assert.Equal(0f, Observe(player, chunks, 1.5f));
        Assert.Equal(0f, Observe(player, chunks, 1f));
    }
}
