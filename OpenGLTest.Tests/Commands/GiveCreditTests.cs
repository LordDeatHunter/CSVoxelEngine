using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.App;
using VoxelEngine.Commands;
using VoxelEngine.Content;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Simulation.Tower;
using VoxelEngine.UI.Hud;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Commands;

[Collection("Worldgen seed")]
public sealed class GiveCreditTests : IDisposable
{
    private World? _world;

    public GiveCreditTests() => TestContent.EnsureLoaded();

    [Fact]
    public void LocalGiveReportsTheFullRequestedCountWhenItFits()
    {
        var world = NewWorld();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var player = new LocalCommandPlayer(world, () => "Local");
        var host = new CommandTestHost(player);
        RunGive(host, player, "/give stone 5");

        Assert.Equal(5, world.Inventory.CountFor(stone));
        Assert.Equal("Gave 5 x stone to Local", host.LastReply);
        Assert.Equal(1, world.PickupFeed.Count);
        var row = world.PickupFeed.At(0);
        Assert.Equal(5, row.Gained);
        Assert.Equal(5, row.Total);
    }

    [Fact]
    public void LocalGiveReportsTheActualPartialBlockCreditAndFeed()
    {
        var world = NewWorld();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        world.Inventory.Add(stone, Inventory.BlockCapacity - 1);
        var player = new LocalCommandPlayer(world, () => "Local");
        var host = new CommandTestHost(player);
        RunGive(host, player, "/give stone 5");

        Assert.Equal(1, world.PickupFeed.Count);
        var row = world.PickupFeed.At(0);
        var problems = new List<string>();
        if (world.Inventory.CountFor(stone) != Inventory.BlockCapacity)
            problems.Add("the block pool did not stop at capacity");
        if (host.LastReply != "Gave 1 x stone to Local")
            problems.Add($"reply was '{host.LastReply}'");
        if (row.Gained != 1 || row.Total != Inventory.BlockCapacity)
            problems.Add($"pickup feed was +{row.Gained} ({row.Total})");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [Fact]
    public void LocalGiveWithNoRoomReturnsAnErrorAndAddsNoFeedGain()
    {
        var world = NewWorld();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        world.Inventory.Add(stone, Inventory.BlockCapacity);
        var player = new LocalCommandPlayer(world, () => "Local");
        var host = new CommandTestHost(player);
        RunGive(host, player, "/give stone 5");

        var problems = new List<string>();
        if (!host.LastReply.Contains("No room")) problems.Add($"reply was '{host.LastReply}'");
        if (host.LastReply.Contains("Gave ")) problems.Add("command reported a successful grant");
        if (world.PickupFeed.Count != 0) problems.Add("a pickup gain was recorded");
        if (world.Inventory.CountFor(stone) != Inventory.BlockCapacity) problems.Add("the full pool changed");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [Fact]
    public void LocalGiveReportsOnlyTheToolInstancesThatFit()
    {
        var world = NewWorld();
        var bucket = ContentRegistry.RequireItem("bucket").Id;
        world.Inventory.Add(bucket, Inventory.ToolCapacity - 1);
        var player = new LocalCommandPlayer(world, () => "Local");
        var host = new CommandTestHost(player);
        RunGive(host, player, "/give bucket 3");

        Assert.Equal(1, world.PickupFeed.Count);
        var row = world.PickupFeed.At(0);
        var problems = new List<string>();
        if (world.Inventory.CountFor(bucket) != Inventory.ToolCapacity)
            problems.Add("the tool pool did not stop at capacity");
        if (host.LastReply != "Gave 1 x bucket to Local")
            problems.Add($"reply was '{host.LastReply}'");
        if (row.Gained != 1 || row.Total != Inventory.ToolCapacity)
            problems.Add($"pickup feed was +{row.Gained} ({row.Total})");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [Fact]
    public void NamedServerGiveReportsPartialAndZeroCredit()
    {
        var sender = new ServerCommandPlayer(null!, new ServerPlayer(1, "Admin", Vector3.Zero), null!);
        var targetState = new ServerPlayer(2, "Bob", Vector3.Zero);
        var target = new ServerCommandPlayer(null!, targetState, null!);
        var host = new CommandTestHost(sender, target);
        var commands = GameCommands.Create();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        targetState.Inventory.Add(stone, Inventory.BlockCapacity - 1);
        var replies = new List<(string Text, ChatLink? Link)>();
        var source = new CommandSource(sender.Name, CommandPermission.Operator, sender.Feet, sender, host,
            (text, link) => replies.Add((text, link)));

        commands.Execute("/give stone 5 Bob", source);
        var partialReply = replies[^1].Text;
        var partialCount = targetState.Inventory.CountFor(stone);
        targetState.Inventory.Add(stone, Inventory.BlockCapacity - partialCount);
        commands.Execute("/give stone 5 Bob", source);

        var problems = new List<string>();
        if (partialCount != Inventory.BlockCapacity) problems.Add("partial request did not fill the remaining slot");
        if (partialReply != "Gave 1 x stone to Bob") problems.Add($"partial reply was '{partialReply}'");
        if (!replies[^1].Text.Contains("No room")) problems.Add($"full reply was '{replies[^1].Text}'");
        if (replies[^1].Text.Contains("Gave ")) problems.Add("full request reported a successful grant");
        if (targetState.Inventory.CountFor(stone) != Inventory.BlockCapacity) problems.Add("full pool changed");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    public void Dispose()
    {
        _world?.SaveAndDispose();
        try { WorldSeed.Apply(WorldSeed.Default); }
        catch (System.InvalidOperationException) { }
    }

    private World NewWorld()
    {
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, null,
            spawn: new Vector3(0f, 1e6f, 0f));
        return _world;
    }

    private static void RunGive(CommandTestHost host, ICommandPlayer player, string line)
    {
        var replies = new List<(string Text, ChatLink? Link)>();
        var source = new CommandSource(player.Name, CommandPermission.Operator, player.Feet, player, host,
            (text, link) => replies.Add((text, link)));
        GameCommands.Create().Execute(line, source);
        host.LastReply = replies[^1].Text;
    }

    private sealed class CommandTestHost(params ICommandPlayer[] players) : ICommandHost
    {
        private readonly ICommandPlayer[] _players = players;
        public string LastReply { get; set; } = "";
        public IReadOnlyList<string> PlayerNames => _players.Select(player => player.Name).ToArray();
        public ICommandPlayer? FindPlayer(string name) =>
            _players.FirstOrDefault(player => string.Equals(player.Name, name, System.StringComparison.OrdinalIgnoreCase));
        public double LightingTime => 0;
        public void SetLightingTime(double seconds) { }
        public bool SpawnEntity(string name, Vector3 feet) => false;
        public TowerManager? Towers => null;
        public void Announce(string text) { }
        public IOperatorAdmin? Operators => null;
    }
}
