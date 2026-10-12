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
