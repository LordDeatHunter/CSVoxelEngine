using System.Numerics;
using VoxelEngine.Interaction;
using VoxelEngine.Player;

namespace VoxelEngine.Simulation.Lanterns;

/// <summary>The shared right-click precedence for lanterns; all mutations run on the authoritative world.</summary>
internal sealed class LanternInteraction(World world, bool clientMode)
{
    internal int Instance { get; private set; }
    internal Vector3 Cell { get; private set; }

    internal bool TryUse(PlayerContext player, float reach, out RightClickOutcome outcome)
    {
        outcome = RightClickOutcome.None;
        var chunks = world.GetChunkManager();
        if (!LanternAccess.TryTarget(player, chunks, reach, out var target, out var pickup)) return false;
        var hit = new Vector3(target.Cell.X, target.Cell.Y, target.Cell.Z);
        if (pickup)
        {
            outcome = RightClickOutcome.LanternPickup;
            if (clientMode) return true;
            var stack = world.Lanterns.Open(chunks, hit.BlockCell());
            // A full pool claims the click but changes nothing; it must not open the held lantern or place through it.
            if (stack == null || !player.Inventory.AddLifted(stack)) return true;
            world.Edits.RemoveBlock(hit, player, pickedUp: true);
            return true;
        }
        Instance = target.Instance;
        Cell = hit;
        outcome = RightClickOutcome.Lantern;
        return true;
    }
}
