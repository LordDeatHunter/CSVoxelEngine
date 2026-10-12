using System;
using System.IO;
using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// A hosted world keeps each multiplayer player in its own file: a player saved and loaded again
/// comes back with its inventory, pose, health and respawn eye, two names never share a file,
/// creative only comes back where the server allows it, and the store accepts the players
/// subfolder but no other directory.
/// </summary>
public sealed class ServerPlayerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public ServerPlayerTests() => TestContent.EnsureLoaded();

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static byte Stone => ContentRegistry.RequireBlock("stone").Id;

    [Fact]
    public void APlayerComesBackWithItsInventoryAndPose()
    {
        var store = new ChunkSaveStore(_dir);
        var player = new ServerPlayer(1, "Alice", new Vector3(10.5f, 70f, -3.25f)) { Yaw = 30f, Pitch = -12f };
        player.Inventory.Add(Stone, 7);
        player.Inventory.Select(4);
        ServerPlayerSaves.Save(store, player);
        Assert.True(store.Shutdown(TimeSpan.FromSeconds(10)));

        // A new session over the same folder, the player joining under a fresh connection id
        var reopened = new ChunkSaveStore(_dir);
        try
        {
            var back = new ServerPlayer(9, "Alice", Vector3.Zero);
            Assert.True(back.Restore(ServerPlayerSaves.Load(reopened, "Alice"), allowCreative: false));
            Assert.Equal(player.Position, back.Position);
            Assert.Equal(player.Yaw, back.Yaw);
            Assert.Equal(player.Pitch, back.Pitch);
            Assert.Equal(player.Inventory.CountFor(Stone), back.Inventory.CountFor(Stone));
            Assert.Equal(player.Inventory.SelectedIndex, back.Inventory.SelectedIndex);

            // Somebody else's name loads nothing of Alice's
            var stranger = new ServerPlayer(10, "Bob", Vector3.Zero);
            Assert.False(stranger.Restore(ServerPlayerSaves.Load(reopened, "Bob"), allowCreative: false));
            Assert.Equal(0, stranger.Inventory.CountFor(Stone));
        }
        finally
        {
            reopened.Shutdown(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void DistinctNamesGetDistinctFilesInsideThePlayersFolder()
    {
        var alice = ServerPlayerSaves.FileNameFor("Alice");
        Assert.NotEqual(alice, ServerPlayerSaves.FileNameFor("Bob"));
        Assert.StartsWith(WorldStore.PlayersDirectoryName + "/", alice);

        var store = new ChunkSaveStore(_dir);
        try
        {
            // The one subfolder whole files may use; anything else still is not a plain name
            store.EnqueueFile(alice, [1, 2, 3]);
            Assert.Equal(new byte[] { 1, 2, 3 }, store.ReadFile(alice));
            Assert.Throws<ArgumentException>(() => store.EnqueueFile($"{WorldStore.ChunksDirectoryName}/x.json", [1]));
            Assert.Throws<ArgumentException>(() => store.EnqueueFile($"{WorldStore.PlayersDirectoryName}/../x.json", [1]));
            Assert.Throws<ArgumentException>(() => store.EnqueueFile($"{WorldStore.PlayersDirectoryName}/", [1]));
        }
        finally
        {
            Assert.True(store.Shutdown(TimeSpan.FromSeconds(10)));
        }

        Assert.True(File.Exists(Path.Combine(_dir, WorldStore.PlayersDirectoryName, Path.GetFileName(alice))));
    }

    [Fact]
    public void CreativeOnlyComesBackWhereTheServerAllowsIt()
    {
        var saved = new PlayerSaveData { Creative = true, Flying = true };

        var strict = new ServerPlayer(1, "Alice", Vector3.Zero);
        strict.Restore(saved, allowCreative: false);
        Assert.False(strict.Creative);
        Assert.False(strict.Flying);

        var lenient = new ServerPlayer(2, "Alice", Vector3.Zero);
        lenient.Restore(saved, allowCreative: true);
        Assert.True(lenient.Creative);
        Assert.True(lenient.Flying);
    }

    [Fact]
    public void HealthAndRespawnEyeRoundTripThroughTheSave()
    {
        var hurt = new ServerPlayer(1, "Alice", new Vector3(4.5f, 70f, 4.5f))
        {
            RespawnEye = new Vector3(-12.5f, 66.62f, 30.5f),
        };
        hurt.Status.TakeDamage(PlayerHealth.MaxHealth / 3);
        Assert.True(hurt.Health < PlayerHealth.MaxHealth);

        var back = new ServerPlayer(2, "Alice", Vector3.Zero);
        back.Restore(PlayerSaveCodec.TryParse(PlayerSaveCodec.ToBytes(hurt.Capture())), allowCreative: false);
        Assert.Equal(hurt.Health, back.Health);
        Assert.Equal(hurt.RespawnEye, back.RespawnEye);

        // A player who never had a respawn eye comes back without one, so it respawns at the spawn
        var fresh = new ServerPlayer(3, "Bob", Vector3.Zero);
        var freshBack = new ServerPlayer(4, "Bob", Vector3.Zero);
        freshBack.Restore(PlayerSaveCodec.TryParse(PlayerSaveCodec.ToBytes(fresh.Capture())), allowCreative: false);
        Assert.Null(freshBack.RespawnEye);
        Assert.Equal(fresh.Health, freshBack.Health);
    }

    [Fact]
    public void AnIntentIsOnlyTakenWhenItIsNewerThanEveryEarlierOne()
    {
        var player = new ServerPlayer(1, "Alice", Vector3.Zero);
        Assert.True(player.AcceptSeq(1));
        Assert.True(player.AcceptSeq(5));
        // A replay, or one overtaken by a newer id, is refused and leaves the newest in place
        Assert.False(player.AcceptSeq(5));
        Assert.False(player.AcceptSeq(3));
        Assert.Equal(5, player.LastSeq);
        Assert.True(player.AcceptSeq(6));
    }

    [Fact]
    public void AttacksLandNoFasterThanTheirPace()
    {
        var player = new ServerPlayer(1, "Alice", Vector3.Zero);
        var punchGap = AttackPacing.MinGapSeconds(NetAttackKind.Punch, null);
        Assert.True(player.TryAttack(NetAttackKind.Punch, null, 10.0, 0.0));
        // A second punch inside the held item's pace is refused, and the refusal records nothing
        Assert.False(player.TryAttack(NetAttackKind.Punch, null, 10.0 + punchGap * 0.5, 0.0));
        Assert.True(player.TryAttack(NetAttackKind.Punch, null, 10.0 + punchGap, 0.0));

        // A slice swing paces by the swing, which a buffered return swing cannot beat either
        var sliceGap = AttackPacing.MinGapSeconds(NetAttackKind.Slice, null);
        var start = 20.0;
        Assert.True(player.TryAttack(NetAttackKind.Slice, null, start, 0.0));
        Assert.False(player.TryAttack(NetAttackKind.SliceReturn, null, start + sliceGap * 0.5, 0.0));
        Assert.True(player.TryAttack(NetAttackKind.SliceReturn, null, start + sliceGap, 0.0));
    }

    [Fact]
    public void ARefusedModeRequestIsReportedOncePerRequest()
    {
        // Survival on a server that grants nothing: asking for creative is refused and reported,
        // the states that still carry the same request (sent before the client heard) are not
        var player = new ServerPlayer(1, "Alice", Vector3.Zero);
        Assert.True(player.ShouldReportModes(NetPlayerFlags.Creative));
        Assert.False(player.ShouldReportModes(NetPlayerFlags.Creative));
        // A different refused request is news again
        Assert.True(player.ShouldReportModes(NetPlayerFlags.Creative | NetPlayerFlags.Flying));

        // Once the client asks for what it has, a later refusal of the first request is news again
        Assert.False(player.ShouldReportModes(NetPlayerFlags.None));
        Assert.True(player.ShouldReportModes(NetPlayerFlags.Creative));

        // A granted request is never reported
        player.Creative = true;
        Assert.False(player.ShouldReportModes(NetPlayerFlags.Creative));
    }
}
