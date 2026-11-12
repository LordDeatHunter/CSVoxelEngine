using System.Numerics;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Player;
using Xunit;

namespace VoxelEngine.Tests.Networking.ServerSide;

/// <summary>
/// The server counts a jump from accepted states alone (it never simulates a body): a step off the ground that rises.
/// That is what works a bloomery's bellows for a connected player, so it must not fire for a fall, a hover, a state
/// it cannot judge or the very first one, and it must fire once per take-off.
/// </summary>
public sealed class ServerPlayerJumpTests
{
    public ServerPlayerJumpTests() => TestContent.EnsureLoaded();

    private static ChunkManager StoneFloor() => MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);

    private static Vector3 EyeAtFeet(float feet) => new(8.5f, feet + Camera.EyeHeight, 8.5f);

    private static bool Observe(ServerPlayer player, ChunkManager chunks, float feet)
    {
        player.Position = EyeAtFeet(feet);
        return player.ObserveAcceptedState(chunks, NetworkConfig.NetPlayerStateSeconds);
    }

    [Fact]
    public void ARisingTakeOffFromTheGroundIsOneJump()
    {
        var chunks = StoneFloor();
        var player = new ServerPlayer(1, "Jumper", EyeAtFeet(1f));

        Assert.False(Observe(player, chunks, 1f));
        Assert.True(Observe(player, chunks, 1f + NetworkConfig.NetJumpRiseBlocks + 0.3f));
        // Still in the air, higher: the same jump, not another
        Assert.False(Observe(player, chunks, 2.2f));
        Assert.False(Observe(player, chunks, 2.0f));
    }

    [Fact]
    public void TheFirstStateAndAFallNeverCountAsAJump()
    {
        var chunks = StoneFloor();
        var player = new ServerPlayer(1, "Faller", EyeAtFeet(6f));

        // Airborne from the start, falling: no ground under it to take off from
        Assert.False(Observe(player, chunks, 6f));
        Assert.False(Observe(player, chunks, 4f));
        Assert.False(Observe(player, chunks, 2f));
        Assert.False(Observe(player, chunks, 1f));
    }

    [Fact]
    public void ARiseSmallerThanATakeOffIsNotAJump()
    {
        var chunks = StoneFloor();
        var player = new ServerPlayer(1, "Stepper", EyeAtFeet(1f));

        Assert.False(Observe(player, chunks, 1f));
        Assert.False(Observe(player, chunks, 1f + NetworkConfig.NetJumpRiseBlocks * 0.5f));
    }

    [Fact]
    public void ATeleportForgetsWhereThePlayerWasSoItCannotBeAJump()
    {
        var chunks = StoneFloor();
        var player = new ServerPlayer(1, "Warped", EyeAtFeet(1f));
        Assert.False(Observe(player, chunks, 1f));

        player.Teleport(EyeAtFeet(1f));
        Assert.False(Observe(player, chunks, 3f));
    }

    [Fact]
    public void AnAirHopIsNeverABellowsJump()
    {
        var chunks = StoneFloor();
        var player = new ServerPlayer(1, "Hopper", EyeAtFeet(1f));
        var rise = NetworkConfig.NetJumpRiseBlocks + 0.3f;

        Assert.False(Observe(player, chunks, 1f));
        Assert.True(Observe(player, chunks, 1f + rise));
        // Up to the apex, a little way down, then the cloud boots' air jump climbs again from no ground
        Assert.False(Observe(player, chunks, 1f + 2f * rise));
        Assert.False(Observe(player, chunks, 1f + 1.5f * rise));
        Assert.False(Observe(player, chunks, 1f + 2.5f * rise));
        Assert.False(Observe(player, chunks, 1f + 3.5f * rise));
    }

    [Fact]
    public void AFlyingPlayerNeverJumps()
    {
        var chunks = StoneFloor();
        var player = new ServerPlayer(1, "Flier", EyeAtFeet(1f)) { Creative = true, Flying = true };

        Assert.False(Observe(player, chunks, 1f));
        Assert.False(Observe(player, chunks, 2f));
    }
}
