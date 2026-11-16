namespace VoxelEngine.Content.Definitions;

/// <summary>A definition whose id is the name of the file it lives in (a quest, a quest book label): set by whoever reads
/// or makes it, never read from or written to the file.</summary>
public interface IFileNamedDefinition
{
    string? Id { get; set; }
}
