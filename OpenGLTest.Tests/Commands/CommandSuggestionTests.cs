using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.Commands;
using Xunit;

namespace VoxelEngine.Tests.Commands;

/// <summary>
/// Completing a line being typed (<see cref="CommandDispatcher.Suggest"/>): the first word offers the commands the
/// source may run, later words offer what the steps reached by the earlier words accept, a completion replaces the
/// word the caret is in, a prefix match ranks before a match further in, and the usage comes along once the command
/// is named.
/// </summary>
public sealed class CommandSuggestionTests
{
    private readonly CommandDispatcher _commands;
    private readonly RecordingCommandHost _host = new();
    private readonly RecordingCommandPlayer _self;

    public CommandSuggestionTests()
    {
        TestContent.EnsureLoaded();
        _commands = GameCommands.Create();
        _self = _host.Add("Self", new Vector3(10.5f, 20f, -3.5f));
    }

    private CommandSuggestions Suggest(string text, CommandPermission permission = CommandPermission.Operator) =>
        _commands.Suggest(text, text.Length, _host.SourceFor(_self, permission));

    private static string[] Inserts(CommandSuggestions suggestions) => suggestions.Items.Select(s => s.Insert).ToArray();

    [Fact]
    public void TheFirstWordOffersTheCommandsTheSourceMayRun()
    {
        var guest = _host.SourceFor(_self, CommandPermission.Everyone);

        var offered = Suggest("/", CommandPermission.Everyone);

        Assert.Equal(1, offered.ReplaceStart);
        Assert.Null(offered.UsageHint);
        Assert.Equal(_commands.Visible(guest).SelectMany(root => root.Names).Order(), Inserts(offered).Order());
        Assert.Contains("teleport", Inserts(Suggest("/tele")));
    }

    [Fact]
    public void ACommandTheSourceMayNotRunOffersNothing()
    {
        Assert.Same(CommandSuggestions.None, Suggest("/give ", CommandPermission.Everyone));
        Assert.Same(CommandSuggestions.None, Suggest("hello there"));
        Assert.Same(CommandSuggestions.None, Suggest(""));
    }

    [Fact]
    public void ACompletionReplacesTheWordTheCaretIsIn()
    {
        const string line = "/give sto 5";
        var caret = line.IndexOf(" 5", StringComparison.Ordinal);

        var offered = _commands.Suggest(line, caret, _host.SourceFor(_self));

        Assert.Equal(line.IndexOf("sto", StringComparison.Ordinal), offered.ReplaceStart);
        Assert.NotEmpty(offered.Items);
        Assert.All(offered.Items, s => Assert.Contains("sto", s.Insert, StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith("/give", offered.UsageHint);
    }

    [Fact]
    public void APrefixMatchRanksBeforeAMatchFurtherIn()
    {
        var dispatcher = new CommandDispatcher();
        dispatcher.Register(Cmd.Literal("pick").Then(Arg.Word("what", "bigstone", "stonewall", "stone", "dirt")));

        var offered = Inserts(dispatcher.Suggest("/pick stone", 11, _host.SourceFor(_self)));

        Assert.Equal("stone", offered[0]);
        Assert.True(Array.IndexOf(offered, "stonewall") < Array.IndexOf(offered, "bigstone"));
        Assert.DoesNotContain("dirt", offered);
        // With nothing typed, every option is offered
        Assert.Equal(4, dispatcher.Suggest("/pick ", 6, _host.SourceFor(_self)).Items.Count);
    }

    [Fact]
    public void ItemCompletionOffersThePinnedFormsOnlyOnceAPrefixIsTyped()
    {
        var plain = Inserts(Suggest("/give st"));
        var pinned = Inserts(Suggest("/give block/st"));

        Assert.NotEmpty(plain);
        Assert.DoesNotContain(plain, s => s.Contains('/'));
        Assert.NotEmpty(pinned);
        Assert.All(pinned, s => Assert.StartsWith("block/", s));
    }

    [Fact]
    public void WhatIsOfferedDependsOnTheWordsBeforeIt()
    {
        _host.Add("Bob");

        // After the command: either branch's first step, coordinates or a player
        var first = Inserts(Suggest("/tp "));
        Assert.Contains("~", first);
        Assert.Contains("Bob", first);

        // Inside the coordinates: the next coordinate, never a player
        var inside = Inserts(Suggest("/tp ~ "));
        Assert.Contains("~", inside);
        Assert.DoesNotContain("Bob", inside);
        Assert.Contains(((int)MathF.Floor(_self.Feet.Y)).ToString(), inside);

        // After a player: where to send them
        Assert.Contains("~ ~ ~", Inserts(Suggest("/tp Bob ")));

        // A number offers nothing to pick, but the usage still comes along
        var number = Suggest("/time set ");
        Assert.Empty(number.Items);
        Assert.StartsWith("/time", number.UsageHint);
    }

    [Fact]
    public void ALineTheTreeCannotFollowOffersNoCompletionButItsUsage()
    {
        var offered = Suggest("/give unobtainium ");

        Assert.Empty(offered.Items);
        Assert.StartsWith("/give", offered.UsageHint);
    }
}
