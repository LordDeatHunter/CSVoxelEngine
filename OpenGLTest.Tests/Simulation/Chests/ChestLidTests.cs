using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Models;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Entities.Posing;
using VoxelEngine.Networking;
using VoxelEngine.Simulation.Chests;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Chests;

/// <summary>
/// The chest's lid: it opens when a chest's viewers go above zero and shuts when they drop back to zero, the locked
/// rattle only plays on a shut chest, the shared model stands where the static block model stands on every facing,
/// a chest is drawn by the chest pass instead of its chunk's mesh, and the open chests survive the wire.
/// </summary>
public sealed class ChestLidTests
{
    private static readonly (int X, int Y, int Z) Cell = (3, 70, -5);
    private static readonly (int X, int Y, int Z) Other = (9, 64, 2);

    public ChestLidTests() => TestContent.EnsureLoaded();

    private static ChestLids Lids() => new(ChestModel.Open, ChestModel.Close, ChestModel.Wobble);

    private static ClipPose PoseOf(ChestLids lids) => lids.PoseAt(Cell.X, Cell.Y, Cell.Z);

    // How high the lid's front edge (its lowest, most northern corner, where the latch hangs) stands over the
    // chest's floor in a pose, in blocks
    private static float FrontEdgeHeight(ClipPose pose)
    {
        var root = ChestModel.Root(Vector3.Zero, 0);
        var lid = ChestModel.Rig.Parts[ChestModel.LidPart];
        var front = lid.Cubes.Min(c => c.Min.Z);
        var bottom = lid.Cubes.Where(c => c.Min.Z == front).Min(c => c.Min.Y);
        return Vector3.Transform(new Vector3(0f, bottom, front), ChestModel.PartMatrix(ChestModel.LidPart, root, pose)).Y;
    }

    [Fact]
    public void ALidOpensWithItsFirstViewerHoldsWhileAnyoneLooksAndShutsWithTheLast()
    {
        var lids = Lids();
        Assert.Null(PoseOf(lids).Clip);

        // The first viewer: the open clip starts
        lids.SetViewed([Cell]);
        lids.Update(0.01f);
        Assert.True(lids.IsOpen(Cell.X, Cell.Y, Cell.Z));
        Assert.Same(ChestModel.Open, PoseOf(lids).Clip);

        // A second viewer (the local player's own panel) restarts nothing, and the lid holds its last frame
        lids.Update(ChestModel.Open.Length * 3f, localViewer: Cell);
        Assert.Same(ChestModel.Open, PoseOf(lids).Clip);
        Assert.Equal(ChestModel.Open.Length, PoseOf(lids).Time, 3);

        // One of the two leaves: still open, still held
        lids.SetViewed([]);
        lids.Update(0.05f, localViewer: Cell);
        Assert.True(lids.IsOpen(Cell.X, Cell.Y, Cell.Z));
        Assert.Same(ChestModel.Open, PoseOf(lids).Clip);

        // The last one leaves: the close clip plays, then the chest is at rest again
        lids.Update(0.01f);
        Assert.False(lids.IsOpen(Cell.X, Cell.Y, Cell.Z));
        Assert.Same(ChestModel.Close, PoseOf(lids).Clip);
        lids.Update(ChestModel.Close.Length + 0.01f);
        Assert.Null(PoseOf(lids).Clip);
    }

    [Fact]
    public void EachChestFollowsItsOwnViewers()
    {
        var lids = Lids();
        lids.SetViewed([Cell, Other]);
        lids.Update(1f);
        lids.SetViewed([Other]);
        lids.Update(0.01f);

        Assert.False(lids.IsOpen(Cell.X, Cell.Y, Cell.Z));
        Assert.True(lids.IsOpen(Other.X, Other.Y, Other.Z));
        Assert.Same(ChestModel.Open, lids.PoseAt(Other.X, Other.Y, Other.Z).Clip);
    }

    [Fact]
    public void AnOpenLidStandsRaisedOnItsHingeAndAShutOneLiesOnTheBase()
    {
        var lids = Lids();
        var shut = FrontEdgeHeight(PoseOf(lids));
        lids.Update(5f, localViewer: Cell);
        var open = FrontEdgeHeight(PoseOf(lids));
        // The front edge swings up by more than the lid is deep at half its swing: well over a quarter block
        Assert.True(open > shut + 0.25f, $"the open lid's front edge is at {open}, the shut one's at {shut}");

        // The hinge (the lid's pivot) and the base do not move
        var root = ChestModel.Root(Vector3.Zero, 0);
        var hingeShut = Vector3.Transform(Vector3.Zero, ChestModel.PartMatrix(ChestModel.LidPart, root, default));
        var hingeOpen = Vector3.Transform(Vector3.Zero, ChestModel.PartMatrix(ChestModel.LidPart, root, PoseOf(lids)));
        Assert.True(Vector3.Distance(hingeShut, hingeOpen) < 1e-4f);
        Assert.Equal(ChestModel.PartMatrix(ChestModel.BasePart, root, default),
            ChestModel.PartMatrix(ChestModel.BasePart, root, PoseOf(lids)));

        // Shut again, the lid is back exactly where it started
        lids.Update(0.01f);
        lids.Update(ChestModel.Close.Length + 0.01f);
        Assert.Equal(shut, FrontEdgeHeight(PoseOf(lids)), 4);
    }

    [Fact]
    public void TheCloseClipStartsWhereTheOpenClipEnds()
    {
        // Or the lid would jump the moment the last viewer leaves
        var held = FrontEdgeHeight(new ClipPose(ChestModel.Open, ChestModel.Open.Length));
        var closing = FrontEdgeHeight(new ClipPose(ChestModel.Close, 0f));
        Assert.Equal(held, closing, 3);
    }

    [Fact]
    public void AClosingLidThatGetsAViewerBackDoesNotStartOverFromShut()
    {
        var lids = Lids();
        lids.Update(5f, localViewer: Cell);
        lids.Update(0.01f);
        lids.Update(ChestModel.Close.Length * 0.3f);
        Assert.Same(ChestModel.Close, PoseOf(lids).Clip);

        lids.Update(0.001f, localViewer: Cell);
        Assert.Same(ChestModel.Open, PoseOf(lids).Clip);
        Assert.True(PoseOf(lids).Time > 0.1f * ChestModel.Open.Length,
            "a lid that had barely started to close restarted its open clip from the shut pose");
    }

    [Fact]
    public void TheLockedRattleOnlyPlaysOnAShutRestingChestAndLeavesItAtRest()
    {
        var lids = Lids();
        Assert.True(lids.Wobble(Cell.X, Cell.Y, Cell.Z));
        Assert.Same(ChestModel.Wobble, PoseOf(lids).Clip);
        // A second knock while it rattles does not restart it
        lids.Update(0.1f);
        Assert.False(lids.Wobble(Cell.X, Cell.Y, Cell.Z));
        lids.Update(ChestModel.Wobble.Length);
        Assert.Null(PoseOf(lids).Clip);

        // An open chest, and one still closing, ignore it
        lids.Update(5f, localViewer: Cell);
        Assert.False(lids.Wobble(Cell.X, Cell.Y, Cell.Z));
        lids.Update(0.01f);
        Assert.False(lids.Wobble(Cell.X, Cell.Y, Cell.Z));
        Assert.Same(ChestModel.Close, PoseOf(lids).Clip);
    }

    [Fact]
    public void TheRattleMovesTheChestAndEndsExactlyAtRest()
    {
        var root = ChestModel.Root(Vector3.Zero, 0);
        var rest = ChestModel.PartMatrix(ChestModel.BasePart, root, default);
        var mid = ChestModel.PartMatrix(ChestModel.BasePart, root,
            new ClipPose(ChestModel.Wobble, ChestModel.Wobble.Length * 0.1f));
        var end = ChestModel.PartMatrix(ChestModel.BasePart, root, new ClipPose(ChestModel.Wobble, ChestModel.Wobble.Length));
        Assert.NotEqual(rest, mid);
        Assert.Equal(rest, end);
    }

    [Fact]
    public void TheModelStandsWhereTheStaticBlockModelStandsOnEveryFacing()
    {
        // The middle of the latch's front, in the cell: the shared model's, and the static twin's turned the way a
        // facing block's model is turned
        var lid = ChestModel.Rig.Parts[ChestModel.LidPart];
        var latch = lid.Cubes.MinBy(c => c.Min.Z)!;
        var front = new Vector3((latch.Min.X + latch.Max.X) / 2f, (latch.Min.Y + latch.Max.Y) / 2f, latch.Min.Z);
        var cell = new Vector3(12f, 40f, -7f);
        var north = Vector3.Transform(front, ChestModel.PartMatrix(ChestModel.LidPart, ChestModel.Root(cell, 0), default)) - cell;

        for (byte facing = 0; facing < 4; facing++)
        {
            var world = Vector3.Transform(front, ChestModel.PartMatrix(ChestModel.LidPart, ChestModel.Root(cell, facing), default));
            var expected = cell + HorizontalFacing.RotatePoint(north, facing, 1f);
            Assert.True(Vector3.Distance(expected, world) < 1e-4f, $"facing {facing}: the latch is at {world}, the block model's at {expected}");
        }

        // Facing north, the latch is on the cell's north side, where the block model has it
        Assert.True(north.Z < 0.1f && MathF.Abs(north.X - 0.5f) < 1e-4f, $"the latch stands at {north} in its cell");
    }

    [Fact]
    public void TheFacingIgnoresATieredChestsTierBits()
    {
        var cell = new Vector3(1f, 2f, 3f);
        for (byte facing = 0; facing < 4; facing++)
            Assert.Equal(ChestModel.Root(cell, facing),
                ChestModel.Root(cell, (byte)(facing | VoxelEngine.Content.Blocks.ChestProfile.TierState(5))));
    }

    [Fact]
    public void AChestLeavesNoQuadsInItsChunkMeshAndIsRecordedForTheChestPass()
    {
        var chests = BlockType.Types.Values.Where(t => t.Chest != null).ToList();
        Assert.NotEmpty(chests);
        foreach (var type in chests)
        {
            var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
            center.SetBlockRaw(4, 4, 4, type.Id, 1);
            var mesh = center.GenerateMeshData(manager)!;
            Assert.Empty(mesh.Vertices);
            Assert.Empty(mesh.TranslucentVertices);
            Assert.Equal([(ushort)Chunk.Idx(4, 4, 4)], mesh.ChestCells);
        }

        var (bareManager, bare) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        bare.SetBlockRaw(4, 4, 4, ContentRegistry.RequireBlock("stone").Id, 0);
        Assert.Null(bare.GenerateMeshData(bareManager)!.ChestCells);
    }

    [Fact]
    public void TheViewedChestsSurviveTheWire()
    {
        var server = Lids();
        server.SetViewed([Cell, Other]);
        Assert.True(server.HasViewed);
        using var writer = new PacketWriter(NetMessage.StationState);
        server.WriteNet(writer);

        var client = Lids();
        using var reader = new PacketReader(writer.ToArray());
        Assert.Equal(NetMessage.StationState, reader.Message);
        client.ReadNet(reader);
        client.Update(0.01f);
        Assert.True(client.IsOpen(Cell.X, Cell.Y, Cell.Z));
        Assert.True(client.IsOpen(Other.X, Other.Y, Other.Z));

        // The next snapshot, with one of them shut, shuts it here too
        server.SetViewed([Other]);
        using var second = new PacketWriter(NetMessage.StationState);
        server.WriteNet(second);
        using var again = new PacketReader(second.ToArray());
        _ = again.Message;
        client.ReadNet(again);
        client.Update(0.01f);
        Assert.False(client.IsOpen(Cell.X, Cell.Y, Cell.Z));
        Assert.True(client.IsOpen(Other.X, Other.Y, Other.Z));
    }

    [Fact]
    public void AnOverlongViewerListIsRefused()
    {
        using var writer = new PacketWriter(NetMessage.StationState);
        writer.Write(StationNetCodec.MaxEntries + 1);
        using var reader = new PacketReader(writer.ToArray());
        _ = reader.Message;
        Assert.Throws<System.IO.InvalidDataException>(() => Lids().ReadNet(reader));
    }
}
