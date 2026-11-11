using VoxelEngine.UI.Hud;
using Xunit;

namespace VoxelEngine.Tests.UI.Hud;

public sealed class ChatViewTests
{
    [Fact]
    public void TypeOnlyLandsWhileOpen()
    {
        var chat = new ChatView();
        Assert.False(chat.Type('h'));
        Assert.True(chat.Input.IsEmpty);

        chat.Open();
        Assert.True(chat.Type('h'));
        Assert.True(chat.Type('i'));
        Assert.Equal("hi", chat.Input.Text);
    }

    [Fact]
    public void OpenStartsEmptyAndCloseDiscards()
    {
        var chat = new ChatView();
        chat.Open();
        chat.Type('x');
        chat.Close();
        Assert.False(chat.IsOpen);
        Assert.True(chat.Input.IsEmpty);

        chat.Open();
        Assert.True(chat.Input.IsEmpty);
    }

    [Fact]
    public void SubmitTrimsClosesAndReturnsTheLine()
    {
        var chat = new ChatView();
        chat.Open();
        foreach (var c in "  hello there  ")
            chat.Type(c);

        Assert.Equal("hello there", chat.Submit());
        Assert.False(chat.IsOpen);
        Assert.True(chat.Input.IsEmpty);
    }

    [Fact]
    public void SubmittingNothingReturnsNull()
    {
        var chat = new ChatView();
        chat.Open();
        chat.Type(' ');
        Assert.Null(chat.Submit());
    }

    [Fact]
    public void SubmitWhileClosedReturnsNull()
    {
        var chat = new ChatView();
        Assert.Null(chat.Submit());
    }

    [Fact]
    public void HistoryWalksSentLinesAndBackToEmpty()
    {
        var chat = new ChatView();
        chat.Open();
        chat.Type('a');
        chat.Submit();
        chat.Open();
        chat.Type('b');
        chat.Submit();

        chat.Open();
        chat.HistoryUp();
        Assert.Equal("b", chat.Input.Text);
        chat.HistoryUp();
        Assert.Equal("a", chat.Input.Text);
        chat.HistoryUp();
        Assert.Equal("a", chat.Input.Text);

        chat.HistoryDown();
        Assert.Equal("b", chat.Input.Text);
        chat.HistoryDown();
        Assert.True(chat.Input.IsEmpty);
        chat.HistoryDown();
        Assert.True(chat.Input.IsEmpty);
    }

    [Fact]
    public void LogDropsTheOldestLineWhenFull()
    {
        var chat = new ChatView();
        for (var i = 0; i < ChatView.MaxMessages + 5; i++)
            chat.AddSystem(i.ToString());

        Assert.Equal(ChatView.MaxMessages, chat.Messages.Count);
        Assert.Equal("5", chat.Messages[0].Text);
        Assert.Equal((ChatView.MaxMessages + 4).ToString(), chat.Messages[^1].Text);
    }

    [Fact]
    public void PlayerAndSystemLinesKeepTheirKind()
    {
        var chat = new ChatView();
        chat.AddMessage(ChatKind.Player, "Alice", "hi");
        chat.AddSystem("joined");

        Assert.Equal(ChatKind.Player, chat.Messages[0].Kind);
        Assert.Equal("Alice", chat.Messages[0].Sender);
        Assert.Equal(ChatKind.System, chat.Messages[1].Kind);
    }

    [Fact]
    public void ALinkThatDoesNotFitItsTextIsDropped()
    {
        const string text = "Nearest at [1, 2, 3]";
        var chat = new ChatView();
        ChatLink[] misfits =
        [
            new(text.Length, 1, "/tp 1 2 3"),
            new(3, 0, "/tp 1 2 3"),
            new(-1, 4, "/tp 1 2 3"),
            new(0, text.Length + 1, "/tp 1 2 3"),
            new(3, 4, ""),
            new(3, 4, new string('a', ChatView.MaxInputLength + 1)),
        ];
        foreach (var misfit in misfits)
        {
            chat.AddSystem(text, misfit);
            Assert.Null(chat.Messages[^1].Link);
        }

        var fitting = new ChatLink(11, 9, "/tp 1 2 3");
        chat.AddMessage(ChatKind.Player, "Alice", text, fitting);
        Assert.Equal(fitting, chat.Messages[^1].Link);
    }

    [Fact]
    public void SuggestPrefillsTheInputWithoutSendingIt()
    {
        const string command = "/tp 10.5 64 -3.5";
        var chat = new ChatView();
        chat.Open();
        chat.Type('a');
        chat.Submit();
        chat.AddSystem("Nearest at [1, 2, 3]", new ChatLink(11, 9, command));
        var count = chat.Messages.Count;

        chat.Suggest(command);

        Assert.True(chat.IsOpen);
        Assert.Equal(command, chat.Input.Text);
        Assert.Equal(count, chat.Messages.Count);

        // The suggestion is not in the history: Up recalls the line sent before it, Down comes back to a new line
        chat.HistoryUp();
        Assert.Equal("a", chat.Input.Text);
        chat.HistoryDown();
        Assert.True(chat.Input.IsEmpty);

        chat.Suggest(command);
        Assert.Equal(command, chat.Submit());
    }

    [Fact]
    public void ResetClosesAndForgetsTheLogAndHistory()
    {
        var chat = new ChatView();
        chat.AddSystem("welcome");
        chat.Open();
        foreach (var c in "hello") chat.Type(c);
        chat.Submit();
        chat.Open();

        chat.Reset();
        Assert.False(chat.IsOpen);
        Assert.Empty(chat.Messages);
        chat.Open();
        chat.HistoryUp();
        Assert.True(chat.Input.IsEmpty, "a sent line survived the reset");
    }

    [Fact]
    public void AClosedLogFadesItsLinesAfterTheHold()
    {
        var chat = new ChatView();
        chat.AddSystem("first");
        chat.Update(ChatView.HoldSeconds * 0.5f);
        chat.AddSystem("second");

        // Both are fully opaque through the hold, and while the log is open
        Assert.Equal(1f, chat.AlphaAt(0));
        Assert.Equal(1f, chat.AlphaAt(1));

        // Half way into the fade, the old line is dimmed but visible; the newer one still holds
        chat.Update(ChatView.HoldSeconds * 0.5f + ChatView.FadeSeconds * 0.5f);
        var dim = chat.AlphaAt(0);
        Assert.True(dim > 0f && dim < 1f, $"the old line read {dim}");
        Assert.Equal(1f, chat.AlphaAt(1));

        // The fade eases out: past it the old line is gone and the newer one has started fading
        chat.Update(ChatView.FadeSeconds * 2f);
        Assert.Equal(0f, chat.AlphaAt(0));
        var newer = chat.AlphaAt(1);
        Assert.True(newer > 0f && newer < 1f, $"the newer line read {newer}");

        // Opening the log makes every line opaque again
        chat.Open();
        Assert.Equal(1f, chat.AlphaAt(0));
        Assert.Equal(1f, chat.AlphaAt(1));
    }
}
