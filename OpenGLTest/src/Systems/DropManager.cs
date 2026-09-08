using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace silkgltriangle;

/// <summary>
/// Owns the live item drops: spawns them where blocks break, pulls nearby ones towards the
/// player, and reports what was collected each tick for the caller to credit to an inventory.
/// </summary>
public class DropManager
{
    /// <summary>How close the player must get before a drop starts flying towards them (1.5 blocks).</summary>
    public const float PickupRange = 1.5f;

    // Drops aim below the eye so they fly to the player's chest rather than into the camera
    private const float CollectHeightOffset = 0.35f;

    private readonly List<ItemDrop> _drops = [];
    private readonly List<byte> _collected = [];
    // Reused by SpawnLoot so a break does not allocate; main thread only, like the rest
    private readonly List<byte> _loot = [];

    public IReadOnlyList<ItemDrop> Drops => _drops;

    /// <summary>
    /// Block ids collected during the most recent <see cref="Update"/>. Valid until the next one.
    /// </summary>
    public ReadOnlySpan<byte> Collected => CollectionsMarshal.AsSpan(_collected);

    /// <summary>
    /// Drops a block or item at the centre of the cell it occupied. Air and unknown ids drop
    /// nothing.
    /// </summary>
    public void Spawn(byte blockId, Vector3 blockPosition)
    {
        if (BlockType.ById[blockId] == null && !ItemType.IsItem(blockId)) return;

        _drops.Add(new ItemDrop(blockId, blockPosition + new Vector3(0.5f)));
    }

    /// <summary>
    /// Drops a block or item at an exact world position (no cell-centre snap), for throws.
    /// Air and unknown ids drop nothing.
    /// </summary>
    public void SpawnAt(byte blockId, Vector3 position, Vector3? toss = null)
    {
        if (BlockType.ById[blockId] == null && !ItemType.IsItem(blockId)) return;

        _drops.Add(new ItemDrop(blockId, position, toss));
    }

    /// <summary>
    /// Rolls what a destroyed block leaves behind into <paramref name="into"/>: its loot table,
    /// or the block itself when it declares none (<see cref="LootTable.None"/> yields nothing).
    /// Every destruction path — mining, TNT, felling — rolls through here, so the yield never
    /// depends on how the block came down. Unknown ids (air, items) roll nothing.
    /// </summary>
    public static void RollLoot(byte blockId, LootContext context, List<byte> into)
    {
        var type = BlockType.ById[blockId];
        if (type == null) return;

        if (type.LootTable == null)
        {
            into.Add(blockId);
            return;
        }

        type.LootTable.Roll(context, into);
    }

    /// <summary>
    /// Rolls a destroyed block's loot and drops every rolled id at the centre of the cell the
    /// block occupied. The cell doubles as the loot context, so position-dependent rolls (the
    /// pebble's flint count) see where the block stood.
    /// </summary>
    public void SpawnLoot(byte blockId, Vector3 blockPosition)
    {
        _loot.Clear();
        RollLoot(blockId,
            new LootContext((int)blockPosition.X, (int)blockPosition.Y, (int)blockPosition.Z),
            _loot);

        foreach (var id in _loot)
            Spawn(id, blockPosition);
    }

    /// <summary>
    /// Advances the drops, collecting what reaches the player. A drop whose id
    /// <paramref name="canCollect"/> refuses is never attracted and stays on the ground,
    /// so a full inventory pool ignores pickups instead of sucking them up.
    /// </summary>
    public void Update(float deltaTime, Vector3 playerPosition, ChunkManager chunks,
        Func<byte, bool>? canCollect = null)
    {
        _collected.Clear();
        if (_drops.Count == 0) return;

        var target = playerPosition - Vector3.UnitY * CollectHeightOffset;
        var rangeSquared = PickupRange * PickupRange;

        // Back to front so removing a collected drop doesn't skip the next one
        for (var i = _drops.Count - 1; i >= 0; i--)
        {
            var drop = _drops[i];
            var wanted = canCollect?.Invoke(drop.BlockId) ?? true;
            var inRange = wanted && Vector3.DistanceSquared(drop.Position, target) <= rangeSquared;

            if (!drop.Update(deltaTime, target, inRange, chunks)) continue;

            // Belt and braces: a drop that latched before its pool filled still needs room
            if (!wanted && !(canCollect?.Invoke(drop.BlockId) ?? true)) continue;

            _collected.Add(drop.BlockId);
            _drops.RemoveAt(i);
        }
    }
}
