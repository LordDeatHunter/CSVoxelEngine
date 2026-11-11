using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Interaction;
using VoxelEngine.Simulation.Fluids;

namespace VoxelEngine.Status;

/// <summary>
/// GL-free rule of what a status effect's block actions (<see cref="StatusEffectBlockAction"/>) do to
/// one cell, read entirely from block data (<c>flammable</c>, <c>freezesTo</c>, <c>meltsTo</c>), never
/// from an effect's name: <see cref="StatusEffectBlockAction.Ignite"/> burns a flammable block away to
/// air, <see cref="StatusEffectBlockAction.Freeze"/> turns a fluid source below the body into its
/// <c>freezesTo</c> block, and <see cref="StatusEffectBlockAction.Thaw"/> turns a meltable block into a
/// source of its <c>meltsTo</c> fluid. <see cref="StatusEffectBlockWriter"/> applies it to the world.
/// </summary>
public static class StatusEffectBlockRule
{
    /// <summary>
    /// What the cell holding <paramref name="id"/> with state <paramref name="state"/> becomes under
    /// <paramref name="actions"/>: true with the new id and state when one of the actions converts it,
    /// false when none does. <paramref name="belowFeet"/> says the cell lies wholly under the body's
    /// feet, the only place a freeze may set solid, so an effect never entombs its own body. An
    /// unbreakable block is never converted (the load rules already refuse it every status flag;
    /// the check keeps the writer and the spell impacts safe in this one place).
    /// </summary>
    public static bool TryConvert(ushort id, byte state, StatusEffectBlockAction actions, bool belowFeet,
        out ushort newId, out byte newState)
    {
        newId = 0;
        newState = 0;
        if (id == 0 || actions == StatusEffectBlockAction.None || BlockType.IsUnbreakable(id))
            return false;

        if ((actions & StatusEffectBlockAction.Ignite) != 0 && BlockType.FlammableById[id])
            return true;

        var frozen = BlockType.FreezesToById[id];
        if ((actions & StatusEffectBlockAction.Freeze) != 0 && frozen != 0 && belowFeet && FluidState.IsSource(state))
        {
            newId = frozen;
            return true;
        }

        var melted = BlockType.MeltsToById[id];
        if ((actions & StatusEffectBlockAction.Thaw) != 0 && melted != 0)
        {
            newId = melted;
            newState = FluidState.Source;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the cell (x, y, z) holds up a support-fragile neighbor (<see cref="BlockSupport"/>): a
    /// torch on its side, a flower on its top, a lantern under it. Emptying such a cell would leave the
    /// neighbor floating, so the writer leaves it alone rather than popping anything. An unloaded
    /// neighbor reads as nothing held.
    /// </summary>
    public static bool HoldsSomethingUp(ChunkManager chunks, int x, int y, int z)
    {
        var holder = new Vector3(x, y, z);
        for (var face = 0; face < 6; face++)
        {
            var cell = holder + Chunk.CubeFaces[face].normal;
            if (!chunks.TryGetCell((int)cell.X, (int)cell.Y, (int)cell.Z, out var id, out var state) || id == 0)
                continue;
            if (BlockSupport.IsHeldUpBy(id, state, cell, holder))
                return true;
        }
        return false;
    }
}
