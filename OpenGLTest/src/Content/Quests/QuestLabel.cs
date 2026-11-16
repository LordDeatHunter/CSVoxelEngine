using System.Numerics;

namespace VoxelEngine.Content.Quests;

/// <summary>One compiled quest book label: free text centered on <see cref="Position"/> (grid steps), in a size and a
/// color; <see cref="Text"/> may hold line breaks. <see cref="Id"/> is its file name.</summary>
public sealed record QuestLabel(string Id, string Text, Vector2 Position, QuestLabelSize Size, QuestLabelColor Color);
