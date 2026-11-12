using System.IO;
using VoxelEngine.UI.Hud;

namespace VoxelEngine.Networking;

/// <summary>
/// One <see cref="NetMessage.ChatMessage"/> on the wire, the only writer and reader of its fields:
/// <c>(byte kind, string sender, string text, string command)</c>, then <c>(int start, int length)</c> only when the
/// command is not empty. The command and the span are the line's optional <see cref="ChatLink"/>; an empty command is a
/// line with none. The kind, sender and text are taken as they come; the link is bounds-checked, so a command the input
/// could not hold or a span outside the text is a malformed frame.
/// </summary>
public static class ChatMessageCodec
{
    public static void Write(PacketWriter writer, in ChatMessage message)
    {
        writer.Write((byte)message.Kind);
        writer.Write(message.Sender);
        writer.Write(message.Text);
        if (message.Link is not { } link)
        {
            writer.Write("");
            return;
        }
        writer.Write(link.Command);
        writer.Write(link.Start);
        writer.Write(link.Length);
    }

    /// <summary>Reads one chat line; throws <see cref="InvalidDataException"/> on a link that does not fit it.</summary>
    public static ChatMessage Read(PacketReader reader)
    {
        var kind = (ChatKind)reader.ReadByte();
        var sender = reader.ReadString();
        var text = reader.ReadString();
        var command = reader.ReadString();
        if (command.Length == 0) return new ChatMessage(kind, sender, text);
        if (command.Length > ChatView.MaxInputLength)
            throw new InvalidDataException($"chat link command of {command.Length} characters");
        var link = new ChatLink(reader.ReadInt(), reader.ReadInt(), command);
        if (!link.FitsIn(text))
            throw new InvalidDataException($"chat link {link.Start}+{link.Length} outside a line of {text.Length} characters");
        return new ChatMessage(kind, sender, text, link);
    }
}
