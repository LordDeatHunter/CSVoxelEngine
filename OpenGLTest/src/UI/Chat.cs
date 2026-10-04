using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>Who a chat line came from: a player (shown "Name: text") or the game itself.</summary>
public enum ChatKind
{
    Player,
    System,
}

/// <summary>One line in the chat log.</summary>
public readonly record struct ChatMessage(ChatKind Kind, string Sender, string Text);

/// <summary>
/// GL-free state of the universal chat, shared by singleplayer and multiplayer: a bounded log of
/// the most recent lines, a single-line input field, a sent-message history walked with the arrow
/// keys, and the open/closed flag the HUD reads. <see cref="Program"/> feeds it typed characters
/// and keys; <see cref="UIRenderer"/> only reads it.
/// <para>
/// A sent line is returned by <see cref="Submit"/> rather than added: singleplayer echoes it
/// locally and multiplayer hands it to the server, which broadcasts it back to every client, so
/// the same message never appears twice.
/// </para>
/// </summary>
public sealed class ChatView
{
    /// <summary>How many lines the log keeps before the oldest is dropped.</summary>
    public const int MaxMessages = 100;

    /// <summary>How many sent lines the arrow-key history keeps.</summary>
    public const int MaxHistory = 50;

    /// <summary>Most characters one chat line accepts.</summary>
    public const int MaxInputLength = 200;

    private readonly List<ChatMessage> _messages = new();
    private readonly List<string> _history = new();

    public bool IsOpen { get; private set; }

    /// <summary>The local player's name, shown as the sender of a line they send.</summary>
    public string LocalName { get; set; } = "Player";

    /// <summary>Index into <see cref="_history"/> while browsing it; equal to the count means "new line".</summary>
    private int _historyCursor;

    public IReadOnlyList<ChatMessage> Messages => _messages;

    public TextFieldState Input { get; } = new(MaxInputLength);

    /// <summary>Opens the input line; a fresh open starts from an empty field.</summary>
    public void Open()
    {
        IsOpen = true;
        Input.Clear();
        _historyCursor = _history.Count;
    }

    /// <summary>Closes the input line and discards whatever was typed.</summary>
    public void Close()
    {
        IsOpen = false;
        Input.Clear();
        _historyCursor = _history.Count;
    }

    /// <summary>Appends a line, dropping the oldest once the log is full.</summary>
    public void AddMessage(ChatKind kind, string sender, string text)
    {
        _messages.Add(new ChatMessage(kind, sender, text));
        if (_messages.Count > MaxMessages)
            _messages.RemoveAt(0);
    }

    public void AddSystem(string text) => AddMessage(ChatKind.System, "", text);

    /// <summary>Types one character into the input; refused while closed or by the field's rules.</summary>
    public bool Type(char c) => IsOpen && Input.TryAppend(c);

    public bool Backspace() => IsOpen && Input.Backspace();

    /// <summary>
    /// Closes and returns the trimmed input, or null when closed or empty. A sent line is pushed
    /// onto the history, so it can be recalled.
    /// </summary>
    public string? Submit()
    {
        if (!IsOpen)
            return null;
        var text = Input.Text.Trim();
        Close();
        if (text.Length == 0)
            return null;
        _history.Add(text);
        if (_history.Count > MaxHistory)
            _history.RemoveAt(0);
        _historyCursor = _history.Count;
        return text;
    }

    /// <summary>Recalls an older sent line into the input (Up arrow).</summary>
    public void HistoryUp()
    {
        if (_history.Count == 0 || _historyCursor <= 0)
            return;
        _historyCursor--;
        Input.Set(_history[_historyCursor]);
    }

    /// <summary>Walks back toward the newest line, then an empty new line (Down arrow).</summary>
    public void HistoryDown()
    {
        if (_history.Count == 0 || _historyCursor >= _history.Count)
            return;
        _historyCursor++;
        Input.Set(_historyCursor >= _history.Count ? "" : _history[_historyCursor]);
    }
}

/// <summary>
/// Pure screen layout of the chat HUD in canvas units (see <see cref="UiCanvas"/>), shared by the
/// draw and any hit-testing so they cannot drift. Sits bottom-left, clear of the centred hotbar.
/// </summary>
public static class ChatLayout
{
    public const float Left = 16f;
    public const float MaxWidth = 760f;
    public const float LineHeight = 22f;
    public const int VisibleMessages = 10;
    public const float InputHeight = 40f;
    public const float InputWidth = 560f;

    /// <summary>Baseline the newest log line sits on, just above the hotbar (or the input when open).</summary>
    public static float LogBottom(Vector2 screen, bool inputOpen) =>
        inputOpen ? InputCentreY(screen) - InputHeight * 0.5f - 8f : HotbarLayout.Top(screen) - 10f;

    public static float InputCentreY(Vector2 screen) => HotbarLayout.Top(screen) - InputHeight * 0.5f - 8f;

    public static Vector2 InputCentre(Vector2 screen) => new(Left + InputWidth * 0.5f, InputCentreY(screen));

    public static Vector2 InputSize => new(InputWidth, InputHeight);

    /// <summary>Canvas Y of the top text line for a message drawn <paramref name="fromBottom"/> lines up.</summary>
    public static float LineTop(Vector2 screen, bool inputOpen, int fromBottom) =>
        LogBottom(screen, inputOpen) - (fromBottom + 1) * LineHeight;
}
