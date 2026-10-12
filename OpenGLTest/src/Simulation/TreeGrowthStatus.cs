namespace silkgltriangle;

/// <summary>What one <see cref="TreeGrowthWriter.Grow"/> call did.</summary>
public enum TreeGrowthStatus
{
    /// <summary>An existing tree rooted in the target column was rebuilt from the world and grown.</summary>
    Grew,

    /// <summary>The column had no tree, so a sapling root was planted on its ground and grown.</summary>
    Planted,

    /// <summary>The column had no tree and no legal ground with a plantable cell above it; nothing was written.</summary>
    NoGround,

    /// <summary>The call was refused before writing anything: a read hit an unloaded chunk, the tree
    /// could not be rebuilt whole, the species is unknown or the world is not playing.</summary>
    NotGrowable,
}
