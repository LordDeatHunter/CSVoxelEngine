using System.Numerics;
using VoxelEngine.Content.Stations;
using VoxelEngine.Networking;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Smithing;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Smithing;

/// <summary>
/// The anvil's rules: it takes only its own input, one piece at a time; a hit needs the gap since the last one to
/// have run down, the last hit finishes the piece into its result and slag, and a gone block hands the piece back.
/// </summary>
public sealed class AnvilManagerTests
{
    public AnvilManagerTests() => TestContent.EnsureLoaded();

    private static readonly Vector3 Cell = new(4, 2, 4);
    private const float Gap = 2.5f;

    private static BlockType Anvil => ContentRegistry.RequireBlock("anvil");
    private static AnvilProfile Profile => Anvil.Anvil!;

    private static AnvilManager Ready()
    {
        var anvils = new AnvilManager();
        anvils.Ensure(Cell, Anvil.Id);
        return anvils;
    }

    [Fact]
    public void ItTakesOnlyItsInputAndOnlyOnePiece()
    {
        var anvils = Ready();
        Assert.False(anvils.TryPlace(Cell, ContentRegistry.RequireItem("coal").Id));
        Assert.True(anvils.TryPlace(Cell, Profile.InputId));
        Assert.False(anvils.TryPlace(Cell, Profile.InputId));
    }

    [Fact]
    public void AHitWithNothingOnTheAnvilDoesNothing()
    {
        Assert.Equal(AnvilHitKind.Refused, Ready().TryHit(Cell, Gap, 0f).Kind);
    }

    [Fact]
    public void HitsAreGatedByTheHammersGapAndTheLastOneFinishesThePiece()
    {
        var anvils = Ready();
        Assert.True(anvils.TryPlace(Cell, Profile.InputId));
        var state = anvils.StateAt(4, 2, 4)!;

        for (var hit = 1; hit < Profile.Hits; hit++)
        {
            Assert.Equal(AnvilHitKind.Struck, anvils.TryHit(Cell, Gap, 0f).Kind);
            // Right away again: refused, nothing counted
            Assert.Equal(AnvilHitKind.Refused, anvils.TryHit(Cell, Gap, 0f).Kind);
            Assert.Equal(hit, state.Hits);
            state.CooldownLeft = 0f;
        }

        var done = anvils.TryHit(Cell, Gap, 0f);
        Assert.Equal(AnvilHitKind.Finished, done.Kind);
        Assert.Equal(Profile.ResultId, done.ResultId);
        Assert.True(done.ResultCount > 0);
        Assert.Equal(Profile.SlagId, done.SlagId);
        Assert.False(state.HasPiece);
        Assert.Equal(0, state.Hits);
    }

    [Fact]
    public void TheGapRunsDownWithTimeAndSlackLetsAHitThroughEarly()
    {
        var anvils = Ready();
        Assert.True(anvils.TryPlace(Cell, Profile.InputId));
        Assert.Equal(AnvilHitKind.Struck, anvils.TryHit(Cell, Gap, 0f).Kind);

        // Half the gap passes, with the anvil block standing in its cell
        var (manager, _) = MeshTestWorld.WalledArea();
        var (lx, ly, lz) = VoxelCoords.LocalOf((int)Cell.X, (int)Cell.Y, (int)Cell.Z);
        manager.ChunkAt((int)Cell.X, (int)Cell.Y, (int)Cell.Z)!.SetBlockRaw(lx, ly, lz, Anvil.Id, 0);
        anvils.Update(Gap * 0.5f, manager, new DropManager());

        Assert.Equal(AnvilHitKind.Refused, anvils.TryHit(Cell, Gap, 0f).Kind);
        // Within the slack of the end of the gap a late packet still counts
        Assert.Equal(AnvilHitKind.Struck, anvils.TryHit(Cell, Gap, Gap * 0.5f + 0.01f).Kind);
    }

    [Fact]
    public void ThePieceCanBeTakenBackAndHitsAreLost()
    {
        var anvils = Ready();
        Assert.True(anvils.TryPlace(Cell, Profile.InputId));
        anvils.TryHit(Cell, Gap, 0f);

        Assert.True(anvils.TryTake(Cell, out var item));
        Assert.Equal(Profile.InputId, item);
        Assert.False(anvils.TryTake(Cell, out _));
        Assert.True(anvils.TryPlace(Cell, Profile.InputId));
        Assert.Equal(0, anvils.StateAt(4, 2, 4)!.Hits);
    }

    [Fact]
    public void AnAnvilWhoseBlockIsGoneDropsItsPiece()
    {
        var (manager, _) = MeshTestWorld.WalledArea();
        var drops = new DropManager();
        var anvils = Ready();
        Assert.True(anvils.TryPlace(Cell, Profile.InputId));

        // The cell holds air, not the anvil
        anvils.Update(0.1f, manager, drops);

        Assert.Null(anvils.StateAt(4, 2, 4));
        Assert.Single(drops.Drops);
        Assert.Equal(Profile.InputId, drops.Drops[0].BlockId);
    }

    [Fact]
    public void APieceAndItsHitsSurviveASaveAndTheWire()
    {
        var anvils = Ready();
        Assert.True(anvils.TryPlace(Cell, Profile.InputId));
        anvils.TryHit(Cell, Gap, 0f);

        var saved = new AnvilManager();
        Assert.Equal(0, saved.Restore(anvils.Capture()));
        Assert.True(saved.StateAt(4, 2, 4)!.HasPiece);
        Assert.Equal(1, saved.StateAt(4, 2, 4)!.Hits);

        using var writer = new PacketWriter(NetMessage.StationState);
        anvils.WriteNet(writer);
        using var reader = new PacketReader(writer.ToArray());
        Assert.Equal(NetMessage.StationState, reader.Message);
        var mirror = new AnvilManager();
        mirror.ReadNet(reader);
        Assert.True(mirror.StateAt(4, 2, 4)!.HasPiece);
        Assert.Equal(1, mirror.StateAt(4, 2, 4)!.Hits);
    }

    [Fact]
    public void AnEmptyAnvilIsNotSaved()
    {
        Assert.Empty(Ready().Capture());
    }
}
