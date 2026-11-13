using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Commands;
using VoxelEngine.Effects.Sky;
using VoxelEngine.UI.Hud;
using Xunit;

namespace VoxelEngine.Tests.Commands;

/// <summary>
/// Running a line on the command trees (<see cref="CommandDispatcher.Execute"/>) and the game's commands
/// (<see cref="GameCommands"/>): a token goes to the first branch that parses it, rights hold for a whole subtree,
/// every failure is answered (unknown, refused, a bad token, or the usage), and the commands do what they say to the
/// source or the player they name.
/// </summary>
public sealed class CommandDispatcherTests
{
    private readonly CommandDispatcher _commands;
    private readonly RecordingCommandHost _host = new();
    private readonly RecordingCommandPlayer _self;

    public CommandDispatcherTests()
    {
        TestContent.EnsureLoaded();
        _commands = GameCommands.Create();
        _self = _host.Add("Self", new Vector3(10f, 20f, 30f));
    }

    private void Run(string line, CommandPermission permission = CommandPermission.Operator) =>
        _commands.Execute(line, _host.SourceFor(_self, permission));

    [Fact]
    public void ATokenGoesToTheFirstBranchThatParsesIt()
    {
        var taken = new List<string>();
        var dispatcher = new CommandDispatcher();
        dispatcher.Register(Cmd.Literal("go")
            .Then(Arg.Coordinates("pos").Executes(_ => taken.Add("pos")))
            .Then(Arg.Player("who").Executes(_ => taken.Add("who"))));
        _host.Add("Bob");

        dispatcher.Execute("/go 1 2 3", _host.SourceFor(_self));
        dispatcher.Execute("/go Bob", _host.SourceFor(_self));

        Assert.Equal(["pos", "who"], taken);
    }

    [Fact]
    public void TildeCoordinatesAreRelativeToTheSourcesFeet()
    {
        Run("/TP ~1 ~ ~-2.5");

        Assert.Equal(new Vector3(11f, 20f, 27.5f), _self.Feet);
    }

    [Fact]
    public void ATeleportCanMoveTheSourceToAPlayerOrAPlayerElsewhere()
    {
        var bob = _host.Add("Bob", new Vector3(-5f, 70f, 8f));
        var eve = _host.Add("Eve", new Vector3(100f, 64f, 100f));

        Run("/tp Bob");
        Assert.Equal(bob.Feet, _self.Feet);

        Run("/tp Eve 1 2 3");
        Assert.Equal(new Vector3(1f, 2f, 3f), eve.Feet);

        Run("/tp Eve Bob");
        Assert.Equal(bob.Feet, eve.Feet);
    }

    [Fact]
    public void AnAliasRunsLikeItsCommand()
    {
        Run("/teleport 0 5 0");

        Assert.Equal(new Vector3(0f, 5f, 0f), _self.Feet);
    }

    [Fact]
    public void APlayerIsNamedByAnyCaseOrAUniquePrefix()
    {
        var bob = _host.Add("Bob", new Vector3(-5f, 70f, 8f));
        Run("/tp bo");
        Assert.Equal(bob.Feet, _self.Feet);

        // With a second match the prefix names nobody, but an exact name still wins
        var bobby = _host.Add("Bobby", new Vector3(3f, 90f, 3f));
        var home = new Vector3(1f, 1f, 1f);
        _self.Teleport(home);
        Run("/tp bo");
        Assert.Equal(home, _self.Feet);
        Run("/tp BOB");
        Assert.Equal(bob.Feet, _self.Feet);
        Run("/tp bobb");
        Assert.Equal(bobby.Feet, _self.Feet);

        _self.Teleport(home);
        Run("/tp xyz");
        Assert.Equal(home, _self.Feet);
        Assert.Contains("xyz", _host.LastReply);
    }

    [Fact]
    public void ALineThatStopsShortOrRunsOnIsAnsweredWithItsUsage()
    {
        var usage = "Usage: " + _commands.Usage(_commands.FindRoot("give")!, _host.SourceFor(_self));

        Run("/give");
        Assert.Equal(usage, _host.LastReply);

        Run("/give stone 2 Self extra");
        Assert.Equal(usage, _host.LastReply);
        Assert.Empty(_self.Given);

        // Two of the three coordinates: the usage, not an error about a player named "1"
        Run("/tp 1 2");
        Assert.StartsWith("Usage: /tp", _host.LastReply);
    }

    [Fact]
    public void ABadTokenIsAnsweredByName()
    {
        Run("/give stone many");
        Assert.Contains("'many'", _host.LastReply);

        Run("/give unobtainium");
        Assert.Contains("unobtainium", _host.LastReply);
        Assert.Empty(_self.Given);

        Run("/dance");
        Assert.Contains("/help", _host.LastReply);
    }

    [Fact]
    public void RightsHoldForAWholeSubtree()
    {
        var ran = new List<string>();
        var dispatcher = new CommandDispatcher();
        dispatcher.Register(Cmd.Literal("door")
            .Then(Cmd.Literal("knock").Executes(_ => ran.Add("knock")))
            .Then(Cmd.Literal("unlock").Requires(CommandPermission.Operator)
                .Then(Cmd.Literal("now").Executes(_ => ran.Add("now")))));
        var guest = _host.SourceFor(_self, CommandPermission.Everyone);

        dispatcher.Execute("/door unlock now", guest);
        dispatcher.Execute("/door knock", guest);
        Assert.Equal(["knock"], ran);
        Assert.DoesNotContain("unlock", dispatcher.Usage(dispatcher.FindRoot("door")!, guest));

        dispatcher.Execute("/door unlock now", _host.SourceFor(_self));
        Assert.Equal(["knock", "now"], ran);
        Assert.Contains("unlock", dispatcher.Usage(dispatcher.FindRoot("door")!, _host.SourceFor(_self)));
    }

    [Fact]
    public void AnOperatorCommandIsRefusedToEveryoneElse()
    {
        Run("/give stone 5", CommandPermission.Everyone);

        Assert.Equal(CommandDispatcher.OperatorsOnly, _host.LastReply);
        Assert.Empty(_self.Given);
    }

    [Fact]
    public void HelpListsExactlyTheCommandsTheSourceMayRun()
    {
        foreach (var permission in new[] { CommandPermission.Everyone, CommandPermission.Operator })
        {
            _host.Replies.Clear();
            var source = _host.SourceFor(_self, permission);
            _commands.Execute("/help", source);

            var listed = _host.Replies.Select(r => r.Text.Split(' ')[0].TrimStart('/')).ToHashSet();
            var visible = _commands.Visible(source).Select(root => root.Name).ToHashSet();
            Assert.Equal(visible, listed);
        }
        Assert.Contains(_commands.Roots, root => root.Permission == CommandPermission.Operator
            && !_commands.Visible(_host.SourceFor(_self, CommandPermission.Everyone)).Contains(root));
    }

    [Fact]
    public void GiveGrantsOneUnlessCountedAndCanGrantAnotherPlayer()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var bob = _host.Add("Bob");

        Run("/give stone");
        Run("/give block/stone 3");
        Run("/give stone 4 Bob");

        Assert.Equal([(stone, 1), (stone, 3)], _self.Given);
        Assert.Equal([(stone, 4)], bob.Given);
    }

    [Fact]
    public void FlightComesWithCreativeAndSurvivalEndsBoth()
    {
        Run("/fly");
        Assert.True(_self.IsFlying);
        Assert.True(_self.IsCreative);

        Run("/fly off");
        Assert.False(_self.IsFlying);
        Assert.True(_self.IsCreative);

        Run("/fly on");
        Run("/gamemode survival");
        Assert.False(_self.IsFlying);
        Assert.False(_self.IsCreative);
    }

    [Fact]
    public void AModeAPlayerMayNotHaveIsRefusedWithTheReason()
    {
        var guest = _host.Add("Guest", mayBeCreative: false);

        Run("/gamemode creative Guest");

        Assert.False(guest.IsCreative);
        Assert.Contains("may not be creative", _host.LastReply);
    }

    [Fact]
    public void TimeSetsNamedPointsAndAddsToTheClock()
    {
        string[] lines = ["/time set 100", "/time add 50.5"];
        foreach (var line in lines)
            Run(line);
        Assert.Equal(150.5, _host.LightingTime);
        // Everyone hears about each change to the shared clock
        Assert.Equal(lines.Length, _host.Announced.Count);

        Run("/time NIGHT");
        Assert.Equal(Clock.MidnightTime, _host.LightingTime);
        Run("/time day");
        Assert.Equal(DayCycle.NoonTime, _host.LightingTime);
    }

    [Fact]
    public void SummonSpawnsWhereTheSourceStandsOrAtTheCoordinates()
    {
        var entity = VoxelEngine.Entities.EntityRegistry.ByName.Keys.First();

        Run($"/summon {entity}");
        Run($"/summon {entity.ToUpperInvariant()} ~ ~5 ~");
        Run("/summon unicorn");

        Assert.Equal([(entity, _self.Feet), (entity, _self.Feet + new Vector3(0f, 5f, 0f))], _host.Spawned);
        Assert.Contains("unicorn", _host.LastReply);
    }

    [Fact]
    public void KillKillsOnceAndThenSaysSo()
    {
        var bob = _host.Add("Bob");

        Run("/kill Bob");
        Assert.True(bob.IsDead);
        Assert.False(_self.IsDead);

        _host.Replies.Clear();
        Run("/kill Bob");
        Assert.Contains("already dead", _host.LastReply);
    }

    [Fact]
    public void ACommandThatNeedsAPlayerRefusesASourceWithNone()
    {
        var replies = new List<string>();
        var nobody = new CommandSource("Console", CommandPermission.Operator, Vector3.Zero, null, _host,
            (text, _) => replies.Add(text));

        _commands.Execute("/kill", nobody);

        Assert.Single(replies);
        Assert.False(_self.IsDead);
    }
}
