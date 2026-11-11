using System.IO;
using VoxelEngine.Networking;
using VoxelEngine.UI.Hud;
using Xunit;

namespace VoxelEngine.Tests.Networking;

/// <summary>
/// A chat line on the wire (<see cref="ChatMessageCodec"/>): a plain line and a linked one round-trip every field, while a
/// link outside its text or a command the input could not hold is a malformed frame.
/// </summary>
public sealed class ChatMessageCodecTests
{
    private const string Text = "Nearest battle tower at [2875, 13, -1410], 3126 blocks away";

    private static PacketReader ReaderOf(PacketWriter writer)
    {
        var reader = new PacketReader(writer.ToArray());
        Assert.Equal(NetMessage.ChatMessage, reader.Message);
        return reader;
    }

    private static ChatMessage RoundTrip(ChatMessage message)
    {
        using var writer = new PacketWriter(NetMessage.ChatMessage);
        ChatMessageCodec.Write(writer, message);
        using var reader = ReaderOf(writer);
        return ChatMessageCodec.Read(reader);
    }

    // The frame written by hand, so a writer that would refuse it cannot hide what the reader does
    private static PacketReader RawFrame(string text, string command, int start, int length)
    {
        using var writer = new PacketWriter(NetMessage.ChatMessage);
        writer.Write((byte)ChatKind.System);
        writer.Write("");
        writer.Write(text);
        writer.Write(command);
        writer.Write(start);
        writer.Write(length);
        return ReaderOf(writer);
    }

    [Fact]
    public void APlainLineRoundTripsWithoutALink()
    {
        var message = new ChatMessage(ChatKind.Player, "Alice", "hello there");

        var back = RoundTrip(message);

        Assert.Equal(message, back);
        Assert.Null(back.Link);
    }

    [Fact]
    public void ALinkedLineRoundTripsItsSpanAndCommand()
    {
        var start = Text.IndexOf('[');
        var link = new ChatLink(start, Text.IndexOf(']') + 1 - start, "/tp 2849.5 72.62 -1409.5");
        var message = new ChatMessage(ChatKind.System, "", Text, link);

        Assert.Equal(message, RoundTrip(message));
    }

    [Fact]
    public void ALinkPastTheTextIsAMalformedFrame()
    {
        using var reader = RawFrame(Text, "/tp 1 2 3", Text.Length - 3, 4);

        Assert.Throws<InvalidDataException>(() => ChatMessageCodec.Read(reader));
    }

    [Fact]
    public void AnOverlongCommandIsAMalformedFrame()
    {
        using var reader = RawFrame(Text, new string('a', ChatView.MaxInputLength + 1), 0, 4);

        Assert.Throws<InvalidDataException>(() => ChatMessageCodec.Read(reader));
    }
}
