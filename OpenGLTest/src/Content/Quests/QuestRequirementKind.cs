namespace VoxelEngine.Content.Quests;

/// <summary>What a <see cref="QuestRequirement"/> counts.</summary>
public enum QuestRequirementKind
{
    /// <summary>Hold a stack of an item, checked when an item is gained (and once when the quest unlocks).</summary>
    Obtain,

    /// <summary>Craft an item (the shapeless crafting list or the anvil), counted per result.</summary>
    Craft,

    /// <summary>Break a block with your own hands or tools.</summary>
    Break,

    /// <summary>Place a block.</summary>
    Place,

    /// <summary>Land the killing blow on an entity.</summary>
    Kill
}
