using VoxelEngine.UI.Menus;
using Xunit;

namespace VoxelEngine.Tests.UI.Menus;

public sealed class TextFieldStateTests
{
    [Fact]
    public void AcceptsOnlyPrintableFontCharacters()
    {
        var field = new TextFieldState(16);
        Assert.True(field.TryAppend('a'));
        Assert.True(field.TryAppend(' '));
        Assert.True(field.TryAppend('~'));
        var accepted = field.Text;
        Assert.Equal("a ~", accepted);

        foreach (var c in new[] { '\n', '\t', '\b', (char)31, (char)127, 'é' })
        {
            Assert.False(field.TryAppend(c));
            Assert.Equal(accepted, field.Text);
        }
    }

    [Fact]
    public void LengthIsCappedAtMaxLength()
    {
        var field = new TextFieldState(5);
        for (var i = 0; i < field.MaxLength; i++)
            Assert.True(field.TryAppend('x'));
        Assert.False(field.TryAppend('y'));
        Assert.Equal(field.MaxLength, field.Text.Length);
        Assert.DoesNotContain('y', field.Text);
    }

    [Fact]
    public void BackspaceRemovesTheLastCharacterAndIsANoOpWhenEmpty()
    {
        var field = new TextFieldState(8);
        field.Set("abc");
        Assert.True(field.Backspace());
        Assert.Equal("ab", field.Text);

        field.Clear();
        Assert.True(field.IsEmpty);
        Assert.False(field.Backspace());
        Assert.True(field.IsEmpty);
    }

    [Fact]
    public void TypingInsertsAtTheCaretAndAdvancesIt()
    {
        var field = new TextFieldState(16);
        field.Set("ac");
        field.MoveCaret(CaretMove.Left, extend: false);
        field.TryAppend('b');
        Assert.Equal("abc", field.Text);
        Assert.Equal(2, field.Caret);
    }

    [Fact]
    public void TypingAndPastingReplaceTheSelectionAndRespectTheCap()
    {
        var field = new TextFieldState(5);
        field.Set("abcde");
        Assert.False(field.TryAppend('x'));

        field.SelectAll();
        Assert.True(field.TryAppend('x'));
        Assert.Equal("x", field.Text);

        field.SelectAll();
        Assert.True(field.Insert("1\n23456789"));
        Assert.Equal("12345", field.Text);
        Assert.False(field.HasSelection);
    }

    [Fact]
    public void BackspaceAndDeleteWorkAroundTheCaretAndOnASelection()
    {
        var field = new TextFieldState(16);
        field.Set("abcd");
        field.MoveCaret(CaretMove.Left, false);
        Assert.True(field.Backspace());
        Assert.Equal("abd", field.Text);
        Assert.True(field.Delete());
        Assert.Equal("ab", field.Text);
        Assert.False(field.Delete());

        field.SelectAll();
        Assert.True(field.Delete());
        Assert.True(field.IsEmpty);
        Assert.False(field.Backspace());
    }

    [Fact]
    public void ArrowsCollapseASelectionToItsEdgeAndShiftExtendsAndShrinksIt()
    {
        var field = new TextFieldState(16);
        field.Set("hello");
        field.SetCaret(1, extend: false);
        field.MoveCaret(CaretMove.Right, extend: true);
        field.MoveCaret(CaretMove.Right, extend: true);
        Assert.Equal("el", field.SelectedText);
        field.MoveCaret(CaretMove.Left, extend: true);
        Assert.Equal("e", field.SelectedText);

        field.MoveCaret(CaretMove.Right, extend: false);
        Assert.False(field.HasSelection);
        Assert.Equal(field.SelectionEnd, field.Caret);
        field.SetCaret(4, extend: false);
        field.SetCaret(1, extend: true);
        field.MoveCaret(CaretMove.Left, extend: false);
        Assert.Equal(1, field.Caret);
        Assert.False(field.HasSelection);
    }

    [Fact]
    public void HomeEndAndSetCaretStayInBounds()
    {
        var field = new TextFieldState(16);
        field.Set("abc");
        field.MoveCaret(CaretMove.Start, extend: true);
        Assert.Equal("abc", field.SelectedText);
        field.MoveCaret(CaretMove.End, extend: false);
        Assert.Equal(3, field.Caret);
        field.SetCaret(99, extend: false);
        Assert.Equal(3, field.Caret);
        field.SetCaret(-4, extend: false);
        Assert.Equal(0, field.Caret);
        field.MoveCaret(CaretMove.Left, extend: false);
        Assert.Equal(0, field.Caret);
    }

    [Fact]
    public void WordMovesAndWordDeletesCrossWhitespaceAndPunctuationRuns()
    {
        var field = new TextFieldState(32);
        field.Set("foo  bar.baz");
        field.MoveCaret(CaretMove.WordLeft, false);
        Assert.Equal("foo  bar.", field.Text[..field.Caret]);
        field.MoveCaret(CaretMove.WordLeft, false);
        Assert.Equal("foo  bar", field.Text[..field.Caret]);
        field.MoveCaret(CaretMove.WordLeft, false);
        Assert.Equal("foo  ", field.Text[..field.Caret]);
        field.MoveCaret(CaretMove.WordRight, true);
        Assert.Equal("bar", field.SelectedText);

        Assert.True(field.DeleteWord(backward: true));
        Assert.Equal("foo  .baz", field.Text);
        field.MoveCaret(CaretMove.Start, false);
        Assert.True(field.DeleteWord(backward: false));
        Assert.Equal("  .baz", field.Text);
        Assert.False(new TextFieldState(4).DeleteWord(true));
    }

    [Theory]
    [InlineData("a b", 3, 2)]
    [InlineData("a b", 2, 0)]
    [InlineData("  ", 2, 0)]
    [InlineData("", 0, 0)]
    public void WordBoundaryLeftStopsAtTheStartOfTheWordBefore(string text, int from, int expected) =>
        Assert.Equal(expected, WordBoundary.Left(text, from));

    [Theory]
    [InlineData("a b", 0, 1)]
    [InlineData("a b", 1, 3)]
    [InlineData("a..b", 1, 3)]
    [InlineData("", 0, 0)]
    public void WordBoundaryRightStopsAtTheEndOfTheWordAfter(string text, int from, int expected) =>
        Assert.Equal(expected, WordBoundary.Right(text, from));

    private sealed class FakeClipboard : IClipboard
    {
        public string Text { get; set; } = "";
    }

    [Fact]
    public void EditingKeysCopyCutPasteAndLeaveUpDownToTheOwner()
    {
        var field = new TextFieldState(16);
        var clipboard = new FakeClipboard();
        field.Set("hello");

        TextFieldEditing.Handle(field, EditKey.A, ctrl: true, shift: false, clipboard, false, out _);
        TextFieldEditing.Handle(field, EditKey.C, true, false, clipboard, false, out _);
        Assert.Equal("hello", clipboard.Text);
        Assert.Equal("hello", field.Text);

        TextFieldEditing.Handle(field, EditKey.X, true, false, clipboard, false, out var changed);
        Assert.True(changed);
        Assert.True(field.IsEmpty);
        TextFieldEditing.Handle(field, EditKey.V, true, false, clipboard, false, out _);
        Assert.Equal("hello", field.Text);

        field.SetCaret(2, extend: false);
        Assert.False(TextFieldEditing.Handle(field, EditKey.Up, false, false, clipboard, upDownJump: false, out _));
        Assert.Equal(2, field.Caret);
        Assert.True(TextFieldEditing.Handle(field, EditKey.Up, false, false, clipboard, upDownJump: true, out _));
        Assert.Equal(0, field.Caret);
        Assert.True(TextFieldEditing.Handle(field, EditKey.Down, false, true, clipboard, upDownJump: true, out _));
        Assert.Equal("hello", field.SelectedText);
    }

    [Fact]
    public void SetObeysTheTypingRules()
    {
        const string input = "ab\ncd";
        var typed = new TextFieldState(16);
        foreach (var c in input) typed.TryAppend(c);

        var set = new TextFieldState(16);
        set.Set(input);
        Assert.Equal(typed.Text, set.Text);
        Assert.DoesNotContain('\n', set.Text);

        var short_ = new TextFieldState(3);
        short_.Set("abcdef");
        Assert.Equal(short_.MaxLength, short_.Text.Length);
        Assert.StartsWith(short_.Text, "abcdef");

        set.Set(null);
        Assert.True(set.IsEmpty);
    }
}
