using VoxelEngine.Content.Quests;

namespace VoxelEngine.DevStudio;

/// <summary>One entry of the QUESTS tab's icon grid: its kind, its block or item id (0 for an entity), its display name,
/// and <see cref="Key"/>, the name a quest file uses for it (a block's bare name, an item's <c>item/</c> name, an
/// entity's file name).</summary>
public readonly record struct IconEntry(IconKind Kind, ushort Id, string Name, string Key)
{
    /// <summary>What a quest file's <c>icon</c> holds for this entry: its key, behind <c>entity/</c> for an entity.</summary>
    public string Icon => Kind == IconKind.Entity ? QuestLoader.EntityIconPrefix + Key : Key;

    /// <summary>Whether this entry is <paramref name="quest"/>'s icon.</summary>
    public bool Is(QuestType quest) =>
        Kind == IconKind.Entity ? quest.IconEntity == Key : quest.IconEntity == null && quest.IconId == Id;
}
