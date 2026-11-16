namespace VoxelEngine.DevStudio;

/// <summary>Which grid DevStudio's left panel shows, and so what the scene previews:
/// the player holding an item, an entity playing its clips, or a tree species growing pulse by
/// pulse (<see cref="TreePreviewState"/>), or the quest graphs being edited (<see cref="QuestEditorPage"/>, whose
/// left panel is the item grid an icon is picked from).</summary>
public enum StudioTab
{
    Items,
    Entities,
    Trees,
    Quests,
}
