namespace VoxelEngine.Entities;

/// <summary>What a thrown item that stays where it landed (<c>Item.ThrowSticks</c>) is stuck in. The values ride the
/// wire in a drop entry and are compared as numbers there, so they never change.</summary>
public enum DropStick
{
    /// <summary>A loose drop: it falls, spins and is pulled in.</summary>
    None = 0,
    /// <summary>In the terrain it hit: it stays until someone comes to take it, or the block is gone.</summary>
    Terrain = 1,
    /// <summary>In the mob it hit: it rides the body and nobody can take it until the mob dies.</summary>
    Body = 2,
}
