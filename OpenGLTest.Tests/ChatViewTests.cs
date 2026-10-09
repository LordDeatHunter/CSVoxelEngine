using Xunit;

namespace silkgltriangle.Tests;

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
}
