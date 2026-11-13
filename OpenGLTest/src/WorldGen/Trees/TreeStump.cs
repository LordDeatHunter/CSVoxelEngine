using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.WorldGen.Trees;

/// <summary>
/// The rule for what a felled trunk leaves behind: cut a natural trunk above its root and only the
/// root cell stays, as the wood's stump block (<c>wood.stump</c>); the cells between the root and
/// the cut are no longer worth a standing column and go with the fell. Only the trunk chain counts:
/// from the cut cell every parent link must point straight down, so a branch (whose chain turns
/// sideways before it reaches the trunk) never leaves a stump, and neither does a cut of the root
/// itself (the whole tree falls). GL-free, reads the world only.
/// </summary>
public static class TreeStump
{
    /// <summary>The longest trunk chain walked: a trunk taller than this leaves no stump.</summary>
    private const int MaxChain = 128;

    /// <summary>
    /// Looks for the root of the trunk a cut at <paramref name="cut"/> belongs to. Returns false when
    /// the cut cell is not on a trunk chain, is the root itself, or its wood has no stump. On success
    /// <paramref name="root"/> is the root cell, <paramref name="stumpId"/> its stump block and
    /// <paramref name="between"/> the cells strictly between the root and the cut, with their ids and states.
    /// </summary>
    public static bool TryFind(ChunkManager chunks, Vector3 cut, ushort cutId, byte cutState,
        out Vector3 root, out ushort stumpId, out List<(Vector3 Cell, ushort Id, byte State)> between)
    {
        root = default;
        between = [];
        stumpId = BlockType.ById[cutId]?.StumpId ?? 0;
        if (stumpId == 0 || WoodState.IsShell(cutState)) return false;

        var cell = cut;
        var state = cutState;
        for (var i = 0; i < MaxChain; i++)
        {
            if (WoodState.IsRoot(state))
            {
                // Cutting the root itself leaves nothing standing to be a stump
                if (i == 0) return false;
                root = cell;
                return true;
            }

            if (!WoodState.TryParentOffset(state, out var dx, out var dy, out var dz) || dx != 0 || dz != 0 || dy != -1)
                return false;

            cell += new Vector3(0, -1, 0);
            if (!chunks.TryGetCell((int)cell.X, (int)cell.Y, (int)cell.Z, out var id, out state)
                || id != cutId || WoodState.IsShell(state))
                return false;

            if (!WoodState.IsRoot(state))
                between.Add((cell, id, state));
        }

        return false;
    }
}
