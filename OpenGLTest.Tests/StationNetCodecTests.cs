using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The stations a server runs reach its clients whole: a snapshot read back into fresh managers
/// shows exactly the fires, cooking, quern tops, kilns and pours the server has, a later snapshot
/// replaces what an earlier one left (a station that is gone disappears), a malformed snapshot is
/// refused, and a pour bound to a connected player drains that player's crucible and pauses once
/// the player left.
/// </summary>
public sealed class StationNetCodecTests
{
    public StationNetCodecTests() => TestContent.EnsureLoaded();

    private static byte Mudball => ContentRegistry.RequireItem("mudball").Id;
    private static byte MalachiteChunk => ContentRegistry.RequireItem("malachite_ore").Id;
    private static Item Coal => ContentRegistry.RequireItem("coal");
    private static BlockType Kiln => ContentRegistry.RequireBlock("mud_brick_kiln");
    private static byte Copper => ContentRegistry.MoltenMaterialByName("copper")!.Id;

    private sealed record Stations(CampfireManager Campfires, QuernManager Querns, KilnManager Kilns,
        CastingManager Casting)
    {
        public static Stations Empty() => new(new CampfireManager(), new QuernManager(), new KilnManager(),
            new CastingManager());
    }

    private static Stations RoundTrip(Stations from, Stations into)
    {
        using var writer = new PacketWriter(NetMessage.StationState);
        StationNetCodec.Write(writer, from.Campfires, from.Querns, from.Kilns, from.Casting);
        using var reader = new PacketReader(writer.ToArray());
        Assert.Equal(NetMessage.StationState, reader.Message);
        StationNetCodec.Read(reader, into.Campfires, into.Querns, into.Kilns, into.Casting);
        return into;
    }

    private static Stations Running()
    {
        var server = Stations.Empty();
        Assert.True(server.Campfires.Ignite(1, 2, 3));
        Assert.True(server.Campfires.TryPlaceFood(1, 2, 3, Mudball));
        Assert.True(server.Querns.TryInsert(5, 2, 3, MalachiteChunk));
        server.Querns.StartSpin(5, 2, 3);
        server.Kilns.Ensure(new Vector3(8, 2, 3), 2, Kiln.Id);
        Assert.True(server.Kilns.TryAddFuel(new Vector3(8, 2, 3), Coal));
        Assert.True(server.Kilns.Ignite(new Vector3(8, 2, 3)));

        var moldId = ContentRegistry.RequireBlock("fired_mold_pickaxe").Id;
        var inventory = Inventory.Empty();
        var stack = CrucibleInventory.Add(inventory, ContentRegistry.RequireItem("crucible").Id,
            new CrucibleComponent(Copper, 500, 0));
        Assert.NotNull(stack);
        Assert.True(server.Casting.TryStartPour(new Vector3(12, 2, 3), moldId, stack!));
        return server;
    }

    [Fact]
    public void AMirrorShowsExactlyTheStationsTheServerRuns()
    {
        var server = Running();
        var client = RoundTrip(server, Stations.Empty());

        Assert.Equal(server.Campfires.LitCells, client.Campfires.LitCells);
        Assert.Equal(server.Campfires.CookingView().ToList(), client.Campfires.CookingView().ToList());
        Assert.Equal(server.Querns.QuernView().ToList(), client.Querns.QuernView().ToList());
        Assert.True(client.Querns.IsSpinning(5, 2, 3));
        Assert.Equal(server.Kilns.LitCells, client.Kilns.LitCells);
        var kiln = client.Kilns.StateAt(8, 2, 3);
        var original = server.Kilns.StateAt(8, 2, 3)!;
        Assert.NotNull(kiln);
        Assert.Equal(original.Facing, kiln!.Facing);
        Assert.Equal(original.Lit, kiln.Lit);
        Assert.Equal(original.Fuel, kiln.Fuel);
        Assert.Equal(original.Contents, kiln.Contents);
        Assert.Equal(server.Casting.CastingView().ToList(), client.Casting.CastingView().ToList());
    }

    [Fact]
    public void ALaterSnapshotReplacesWhatAnEarlierOneLeft()
    {
        var client = RoundTrip(Running(), Stations.Empty());
        Assert.True(StationNetCodec.AnyStations(client.Campfires, client.Querns, client.Kilns, client.Casting));

        // Every station on the server went away: the next snapshot clears the mirror
        RoundTrip(Stations.Empty(), client);
        Assert.False(StationNetCodec.AnyStations(client.Campfires, client.Querns, client.Kilns, client.Casting));
        Assert.Empty(client.Campfires.LitCells);
        Assert.Empty(client.Kilns.LitCells);
    }

    [Fact]
    public void AMirroredQuernKeepsTurningBetweenSnapshotsAndComesToRest()
    {
        var client = RoundTrip(Running(), Stations.Empty());
        var before = client.Querns.QuernView().Single().Angle;
        client.Querns.Animate(QuernManager.FullRotationSeconds * 0.25f);
        Assert.NotEqual(before, client.Querns.QuernView().Single().Angle);
        Assert.True(client.Querns.IsSpinning(5, 2, 3));

        // One full rotation and it rests, holding its input: only the server crushes
        client.Querns.Animate(QuernManager.FullRotationSeconds);
        Assert.False(client.Querns.IsSpinning(5, 2, 3));
        Assert.Equal(MalachiteChunk, client.Querns.QuernView().Single().InputId);
    }

    [Fact]
    public void AMalformedSnapshotIsRefused()
    {
        using var writer = new PacketWriter(NetMessage.StationState);
        writer.Write(StationNetCodec.MaxEntries + 1);
        using var reader = new PacketReader(writer.ToArray());
        _ = reader.Message;
        var client = Stations.Empty();
        Assert.Throws<InvalidDataException>(() =>
            StationNetCodec.Read(reader, client.Campfires, client.Querns, client.Kilns, client.Casting));
    }

    [Fact]
    public void APourBoundToAPlayerDrainsThatPlayersCrucibleAndPausesWhenItLeaves()
    {
        var moldId = ContentRegistry.RequireBlock("fired_mold_pickaxe").Id;
        var recipe = ContentRegistry.CastRecipeFor(moldId, Copper)!;
        var manager = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false) { IsBlockDataReady = true };
        manager.Chunks[Vector3.Zero] = chunk;
        chunk.SetBlockRaw(0, 0, 0, moldId, 0);

        var pourer = new PlayerContext(Inventory.Empty()) { Eye = new Vector3(0.5f, 1.5f, 0.5f) };
        var stack = CrucibleInventory.Add(pourer.Inventory, ContentRegistry.RequireItem("crucible").Id,
            new CrucibleComponent(Copper, recipe.AmountMl * 2, 0));
        Assert.NotNull(stack);
        Assert.Equal(stack!.InstanceId, pourer.Inventory.SelectedToolInstance);

        var casting = new CastingManager();
        Assert.True(casting.TryStartPour(Vector3.Zero, moldId, stack, pourer));

        // The world's own inventory and camera (the server's, standing nowhere near) do not matter
        var nobody = Inventory.Empty();
        var far = new Vector3(500f, 0f, 500f);
        var drops = new DropManager();
        var step = recipe.AmountMl / CastingManager.PourMlPerSecond / 4f;
        casting.Update(step, manager, drops, nobody, 0, far, 10f);
        var poured = casting.StateAt(0, 0, 0)!.FilledMl;
        Assert.True(poured > 0, "the bound player's pour did not run");
        Assert.Equal(recipe.AmountMl * 2 - poured, stack.Get<CrucibleComponent>().MoltenMl);

        // Gone: the pour pauses with what it already holds
        pourer.Present = false;
        casting.Update(step, manager, drops, nobody, 0, far, 10f);
        Assert.Equal(poured, casting.StateAt(0, 0, 0)!.FilledMl);
        Assert.False(casting.StateAt(0, 0, 0)!.Pouring);
    }
}
