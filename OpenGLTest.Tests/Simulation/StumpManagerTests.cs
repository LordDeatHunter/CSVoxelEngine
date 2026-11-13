using System.Numerics;
using VoxelEngine.Config;
using VoxelEngine.Networking;
using VoxelEngine.Saves;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Stumps;
using Xunit;

namespace VoxelEngine.Tests.Simulation;

/// <summary>
/// Rules of the log standing on a stump (<see cref="StumpManager"/>): only a log goes on an empty stump, where the
/// aim meets its top and never off the top; taking it gives the same log back; a stump that stops being a stump
/// hands its log over as a drop; and the saves and the station snapshot carry the log across unchanged.
/// </summary>
public sealed class StumpManagerTests
{
    private static readonly (int X, int Y, int Z) Cell = (4, 1, 4);

    public StumpManagerTests() => TestContent.EnsureLoaded();

    private static ushort OakLog => ContentRegistry.RequireBlock("oak_log").Id;
    private static ushort OakStump => ContentRegistry.RequireBlock("oak_stump").Id;

    private static ChunkManager World(ushort blockAtCell)
    {
        var chunks = new ChunkManager(null!);
        StampedTree.WriteCell(chunks, Cell, blockAtCell, 0);
        return chunks;
    }

    // Looking at the stump's top from above and to the side
    private static (Vector3 Eye, Vector3 Direction) Aim(float dx = 0f, float dz = 0f)
    {
        var target = new Vector3(Cell.X + 0.5f + dx, Cell.Y + StumpConfig.StumpTop, Cell.Z + 0.5f + dz);
        var eye = new Vector3(Cell.X + 0.5f, Cell.Y + 2f, Cell.Z - 2f);
        return (eye, Vector3.Normalize(target - eye));
    }

    [Fact]
    public void OnlyALogStandsOnAnEmptyStump()
    {
        var chunks = World(OakStump);
        var stumps = new StumpManager();
        var (eye, direction) = Aim();

        Assert.False(stumps.TryPlace(chunks, Cell.X, Cell.Y, Cell.Z, ContentRegistry.RequireBlock("oak_planks").Id, eye, direction));
        Assert.False(stumps.TryPlace(chunks, Cell.X, Cell.Y, Cell.Z, ContentRegistry.RequireBlock("cactus").Id, eye, direction));
        Assert.True(stumps.TryPlace(chunks, Cell.X, Cell.Y, Cell.Z, OakLog, eye, direction));
        Assert.False(stumps.TryPlace(chunks, Cell.X, Cell.Y, Cell.Z, OakLog, eye, direction));
    }

    [Fact]
    public void ALogDoesNotStandOnAnythingButAStump()
    {
        var chunks = World(OakLog);
        var (eye, direction) = Aim();

        Assert.False(new StumpManager().TryPlace(chunks, Cell.X, Cell.Y, Cell.Z, OakLog, eye, direction));
    }

    [Fact]
    public void TheLogStandsWhereTheAimMeetsTheTopAndStaysOnIt()
    {
        var chunks = World(OakStump);

        var near = new StumpManager();
        var (eye, direction) = Aim(dx: 0.1f);
        Assert.True(near.TryPlace(chunks, Cell.X, Cell.Y, Cell.Z, OakLog, eye, direction));
        Assert.Equal(0.1f, near.LogAt(Cell.X, Cell.Y, Cell.Z)!.OffsetX, 0.01f);

        var far = new StumpManager();
        (eye, direction) = Aim(dx: 3f, dz: -3f);
        Assert.True(far.TryPlace(chunks, Cell.X, Cell.Y, Cell.Z, OakLog, eye, direction));
        var log = far.LogAt(Cell.X, Cell.Y, Cell.Z)!;
        Assert.InRange(log.OffsetX, -StumpConfig.MaxOffset, StumpConfig.MaxOffset);
        Assert.InRange(log.OffsetZ, -StumpConfig.MaxOffset, StumpConfig.MaxOffset);
    }

    [Fact]
    public void TakingTheLogGivesTheSameLogBack()
    {
        var chunks = World(OakStump);
        var stumps = new StumpManager();
        var (eye, direction) = Aim();
        Assert.True(stumps.TryPlace(chunks, Cell.X, Cell.Y, Cell.Z, OakLog, eye, direction));

        Assert.Equal(OakLog, stumps.TryTake(Cell.X, Cell.Y, Cell.Z));
        Assert.Null(stumps.LogAt(Cell.X, Cell.Y, Cell.Z));
        Assert.Equal(0, stumps.TryTake(Cell.X, Cell.Y, Cell.Z));
    }

    [Fact]
    public void AWobbleRingsDownAndNeedsALog()
    {
        var chunks = World(OakStump);
        var stumps = new StumpManager();
        Assert.False(stumps.Wobble(Cell.X, Cell.Y, Cell.Z));

        var (eye, direction) = Aim();
        stumps.TryPlace(chunks, Cell.X, Cell.Y, Cell.Z, OakLog, eye, direction);
        Assert.True(stumps.Wobble(Cell.X, Cell.Y, Cell.Z));
        Assert.True(stumps.LogAt(Cell.X, Cell.Y, Cell.Z)!.WobbleLeft > 0f);

        stumps.Update(StumpConfig.WobbleSeconds + 0.1f, chunks, new DropManager());
        Assert.Equal(0f, stumps.LogAt(Cell.X, Cell.Y, Cell.Z)!.WobbleLeft);
    }

    [Fact]
    public void AStumpThatIsGoneDropsItsLog()
    {
        var chunks = World(OakStump);
        var stumps = new StumpManager();
        var drops = new DropManager();
        var (eye, direction) = Aim();
        stumps.TryPlace(chunks, Cell.X, Cell.Y, Cell.Z, OakLog, eye, direction);

        StampedTree.WriteCell(chunks, Cell, BlockType.Air, 0);
        stumps.Update(0.05f, chunks, drops);

        Assert.Null(stumps.LogAt(Cell.X, Cell.Y, Cell.Z));
        Assert.Contains(drops.Drops, d => d.BlockId == OakLog);
    }

    [Fact]
    public void TheSaveCarriesTheLogAndDropsOneThatIsNoLog()
    {
        var chunks = World(OakStump);
        var stumps = new StumpManager();
        var (eye, direction) = Aim(dx: 0.12f);
        stumps.TryPlace(chunks, Cell.X, Cell.Y, Cell.Z, OakLog, eye, direction);

        var restored = new StumpManager();
        Assert.Equal(0, restored.Restore(stumps.Capture()));
        var was = stumps.LogAt(Cell.X, Cell.Y, Cell.Z)!;
        var now = restored.LogAt(Cell.X, Cell.Y, Cell.Z)!;
        Assert.Equal((was.LogId, was.OffsetX, was.OffsetZ, was.Yaw), (now.LogId, now.OffsetX, now.OffsetZ, now.Yaw));

        var data = stumps.Capture();
        data[0].Log = "oak_planks";
        Assert.Equal(1, new StumpManager().Restore(data));
    }

    [Fact]
    public void TheSnapshotCarriesTheLogToAMirror()
    {
        var chunks = World(OakStump);
        var server = new StumpManager();
        var (eye, direction) = Aim(dx: -0.1f);
        server.TryPlace(chunks, Cell.X, Cell.Y, Cell.Z, OakLog, eye, direction);
        server.Wobble(Cell.X, Cell.Y, Cell.Z);

        using var writer = new PacketWriter(NetMessage.StationState);
        server.WriteNet(writer);
        var client = new StumpManager();
        using var reader = new PacketReader(writer.ToArray());
        Assert.Equal(NetMessage.StationState, reader.Message);
        client.ReadNet(reader);

        var sent = server.LogAt(Cell.X, Cell.Y, Cell.Z)!;
        var got = client.LogAt(Cell.X, Cell.Y, Cell.Z)!;
        Assert.Equal((sent.LogId, sent.OffsetX, sent.OffsetZ, sent.Yaw, sent.WobbleLeft),
            (got.LogId, got.OffsetX, got.OffsetZ, got.Yaw, got.WobbleLeft));

        // A snapshot without the log clears the mirror
        server.TryTake(Cell.X, Cell.Y, Cell.Z);
        using var empty = new PacketWriter(NetMessage.StationState);
        server.WriteNet(empty);
        using var emptyReader = new PacketReader(empty.ToArray());
        Assert.Equal(NetMessage.StationState, emptyReader.Message);
        client.ReadNet(emptyReader);
        Assert.False(client.HasAny);
    }
}
