namespace VoxelEngine.Content.Quests;

/// <summary>
/// One compiled quest requirement: its kind, the block or item id it names (<see cref="Id"/>, for every
/// kind but <see cref="QuestRequirementKind.Kill"/>) or the entity file name (<see cref="Entity"/>, for a
/// kill), and how many (<see cref="Count"/>, at least 1). <see cref="TargetName"/> is the display name the
/// details panel lists.
/// </summary>
public sealed record QuestRequirement(QuestRequirementKind Kind, ushort Id, string? Entity, int Count, string TargetName)
{
    /// <summary>Whether a counted event of <paramref name="kind"/> on that block or item id advances this requirement.</summary>
    public bool Counts(QuestRequirementKind kind, ushort id) => Kind == kind && Entity == null && Id == id;

    /// <summary>Whether a kill of the entity named <paramref name="entity"/> advances this requirement.</summary>
    public bool CountsKill(string entity) => Kind == QuestRequirementKind.Kill && Entity == entity;
}
