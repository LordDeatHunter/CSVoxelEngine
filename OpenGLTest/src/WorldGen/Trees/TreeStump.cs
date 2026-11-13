using VoxelEngine.Chunks;

namespace VoxelEngine.WorldGen.Trees;

/// <summary>
/// The rule for what a felled trunk leaves behind: break the lowest log of a natural trunk (its root) and the root cell
/// stays, as the wood's stump block (<c>wood.stump</c>), while everything it held up falls. A cut higher up leaves the
/// root as the log it is, and a shell of a wide trunk never leaves one. GL-free.
/// </summary>
public static class TreeStump
{
    /// <summary>
    /// Whether breaking the wood cell (<paramref name="id"/>, <paramref name="state"/>) leaves a stump: it is a trunk's root
    /// whose wood names a stump block, returned in <paramref name="stumpId"/>.
    /// </summary>
    public static bool TryFind(ushort id, byte state, out ushort stumpId)
    {
        stumpId = BlockType.ById[id]?.StumpId ?? 0;
        return stumpId != 0 && !WoodState.IsShell(state) && WoodState.IsRoot(state);
    }
}
