using System;
using VoxelEngine.Commands;
using VoxelEngine.UI.Hud;
using VoxelEngine.UI.Menus;
using Xunit;

namespace VoxelEngine.Tests.UI.Hud;

/// <summary>
/// The chat input's autocomplete (<see cref="ChatCompletion"/>): Tab walks one list (Shift+Tab back) instead of
/// completing its own completion, typing starts the list over, a completion replaces only the word being completed,
/// the ghost is the untyped rest of a completion that continues the word, and a plain line offers nothing.
/// </summary>
public sealed class ChatCompletionTests
{
    private readonly CommandDispatcher _commands = new();
    private readonly RecordingCommandHost _host = new();
    private readonly Func<CommandSource> _source;
    private readonly TextFieldState _field = new(ChatView.MaxInputLength);
    private readonly ChatCompletion _completion = new();

    public ChatCompletionTests()
    {
        _commands.Register(Cmd.Literal("pick")
            .Then(Arg.Word("what", "alpha", "alpine", "alto", "ball")
                .Then(Arg.Word("how", "fast", "slow"))));
        var self = _host.Add("Self");
        _source = () => _host.SourceFor(self);
    }

    private void Type(string text)
    {
        foreach (var c in text)
            _field.TryAppend(c);
        _completion.Sync(_field, _commands, _source);
    }

    [Fact]
    public void TabWalksOneListAndShiftTabWalksBack()
    {
        Type("/pick al");
        var items = _completion.Current.Items;
        Assert.True(items.Count > 1);

        Assert.True(_completion.Cycle(_field, 1));
        _completion.Sync(_field, _commands, _source);
        Assert.Equal("/pick " + items[0].Insert, _field.Text);

        Assert.True(_completion.Cycle(_field, 1));
        _completion.Sync(_field, _commands, _source);
        Assert.Equal("/pick " + items[1].Insert, _field.Text);
        Assert.Same(items, _completion.Current.Items);

        _completion.Cycle(_field, -1);
        Assert.Equal("/pick " + items[0].Insert, _field.Text);
    }

    [Fact]
    public void ShiftTabFirstTakesTheLastCompletion()
    {
        Type("/pick al");
        var items = _completion.Current.Items;

        _completion.Cycle(_field, -1);

        Assert.Equal("/pick " + items[^1].Insert, _field.Text);
    }

    [Fact]
    public void TypingAfterATabStartsTheListOver()
    {
        Type("/pick al");
        _completion.Cycle(_field, 1);

        Type(" ");

        Assert.Equal(-1, _completion.Selected);
        Assert.Contains(_completion.Current.Items, s => s.Insert == "fast");
    }

    [Fact]
    public void ACompletionReplacesOnlyTheWordBeingCompleted()
    {
        Type("/pick b fast");
        _field.SetCaret("/pick b".Length, extend: false);
        _completion.Sync(_field, _commands, _source);

        _completion.Cycle(_field, 1);

        Assert.Equal("/pick ball fast", _field.Text);
        Assert.Equal("/pick ball".Length, _field.Caret);
    }

    [Fact]
    public void TheGhostIsTheUntypedRestOfACompletionThatContinuesTheWord()
    {
        Type("/pick al");
        var best = _completion.Current.Items[0].Insert;
        Assert.Equal(best["al".Length..], _completion.Ghost(_field));

        // Not at the end of the line: no ghost
        _field.SetCaret(3, extend: false);
        _completion.Sync(_field, _commands, _source);
        Assert.Equal("", _completion.Ghost(_field));

        // A match further into the word does not continue it
        _field.Clear();
        Type("/pick lph");
        Assert.Contains(_completion.Current.Items, s => s.Insert == "alpha");
        Assert.Equal("", _completion.Ghost(_field));
    }

    [Fact]
    public void APlainLineOffersNothing()
    {
        Type("hello al");

        Assert.Empty(_completion.Current.Items);
        Assert.False(_completion.Cycle(_field, 1));
        Assert.Equal("hello al", _field.Text);
        Assert.Equal("", _completion.Ghost(_field));
    }

    [Fact]
    public void ClosingTheChatForgetsTheOffer()
    {
        var view = new ChatView();
        view.Open();
        foreach (var c in "/pick al")
            view.Type(c);
        view.Completion.Sync(view.Input, _commands, _source);
        Assert.NotEmpty(view.Completion.Current.Items);

        view.Close();

        Assert.Empty(view.Completion.Current.Items);
    }
}
