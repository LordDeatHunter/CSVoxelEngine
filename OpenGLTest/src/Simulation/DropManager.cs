using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Loot;
using VoxelEngine.Entities;
using VoxelEngine.Items;
using VoxelEngine.Juice;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Trees;

namespace VoxelEngine.Simulation;

/// <summary>
/// Owns the live item drops: spawns them where blocks break, pulls nearby ones toward the
/// player, and reports what was collected each tick for the caller to credit to an inventory.
/// </summary>
public class DropManager
{
    /// <summary>How close the player must get before a drop starts flying toward them (3 blocks).</summary>
    public const float PickupRange = 3f;

    // Drops aim below the eye so they fly to the player's chest rather than into the camera
    internal const float CollectHeightOffset = 0.35f;

    private readonly List<ItemDrop> _drops = [];
    private readonly List<byte> _collected = [];
    // Parallel to _collected after a multi-collector Update: the index of the collector each went to
    private readonly List<int> _collectedBy = [];
    // Reused by SpawnLoot so a break does not allocate; main thread only, like the rest
    private readonly List<byte> _loot = [];

    public IReadOnlyList<ItemDrop> Drops => _drops;

    /// <summary>Drops every tracked drop (a client rebuilds its list from a server snapshot).</summary>
    public void Clear() => _drops.Clear();

    /// <summary>
    /// A TNT blast of <paramref name="radius"/> at <paramref name="center"/> shoves every idle drop in
    /// range away from it (<see cref="BlastImpulse"/>), so loot flies out of a crater.
    /// </summary>
    public void ApplyBlastImpulse(Vector3 center, float radius)
    {
        for (var i = 0; i < _drops.Count; i++)
            _drops[i].Push(BlastImpulse.At(center, _drops[i].Position, radius));
    }

    /// <summary>
    /// Block ids collected during the most recent <see cref="Update"/>. Valid until the next one.
    /// </summary>
    public ReadOnlySpan<byte> Collected => CollectionsMarshal.AsSpan(_collected);

    /// <summary>
    /// After the multi-collector <see cref="Update(float, IReadOnlyList{DropCollector}, ChunkManager)"/>:
    /// for each entry of <see cref="Collected"/>, the index of the collector it went to. Empty after
    /// the single-player overload. Valid until the next update.
    /// </summary>
    public ReadOnlySpan<int> CollectedBy => CollectionsMarshal.AsSpan(_collectedBy);

    /// <summary>
    /// Drops a block or item at the center of the cell it occupied. Air and unknown ids drop
    /// nothing.
    /// </summary>
    public void Spawn(byte blockId, Vector3 blockPosition)
    {
        if (BlockType.ById[blockId] == null && !ItemRegistry.IsItem(blockId)) return;

        _drops.Add(new ItemDrop(blockId, blockPosition + new Vector3(0.5f)));
    }

    /// <summary>
    /// Drops a block or item at an exact world position (no cell-center snap), for throws.
    /// Air and unknown ids drop nothing.
    /// </summary>
    public void SpawnAt(byte blockId, Vector3 position, Vector3? toss = null)
    {
        if (BlockType.ById[blockId] == null && !ItemRegistry.IsItem(blockId)) return;

        _drops.Add(new ItemDrop(blockId, position, toss));
    }

    /// <summary>
    /// Puts back a drop a save recorded, with its exact position, velocity and age and no pop.
    /// False (and nothing added) for an id that is neither a block nor an item, the same guard
    /// as <see cref="SpawnAt"/>.
    /// </summary>
    public bool SpawnRestored(byte blockId, Vector3 position, Vector3 velocity, float age)
    {
        if (BlockType.ById[blockId] == null && !ItemRegistry.IsItem(blockId)) return false;

        _drops.Add(new ItemDrop(blockId, position, velocity, age));
        return true;
    }

    /// <summary>
    /// Rolls what a destroyed block leaves behind into <paramref name="into"/>: its loot table,
    /// or the block itself when it declares none (<see cref="LootTable.None"/> yields nothing).
    /// Every destruction path — mining, TNT, felling — rolls through here, so the yield never
    /// depends on how the block came down. Unknown ids (air, items) roll nothing. Tree wood
    /// rolls the band its thickness (read from <see cref="LootContext.State"/>) falls in.
    /// </summary>
    public static void RollLoot(byte blockId, LootContext context, List<byte> into)
    {
        var type = BlockType.ById[blockId];
        if (type == null) return;

        if (type.Renderer == BlockRenderer.TreeWood)
        {
            var band = WoodLootBand.Select(type.WoodLootBands, WoodState.Thickness(context.State));
            if (band.Self) into.Add(blockId);
            else band.Table?.Roll(context, into);
            return;
        }

        if (type.LootTable == null)
        {
            into.Add(blockId);
            return;
        }

        type.LootTable.Roll(context, into);
    }

    /// <summary>
    /// Rolls a destroyed block's loot and drops every rolled id at the center of the cell the
    /// block occupied. The cell doubles as the loot context, so position-dependent rolls (the
    /// pebble's yield) see where the block stood. <paramref name="count"/> carries an
    /// explicit pile size for a player-built stack (see <see cref="PileCount"/>); 0 derives it.
    /// <paramref name="secondary"/> carries the obsidian share of a player-built pebble pile,
    /// <paramref name="state"/> the destroyed cell's state byte (tree wood's thickness band).
    /// </summary>
    public void SpawnLoot(byte blockId, Vector3 blockPosition, int count = 0, int secondary = 0, byte state = 0)
    {
        _loot.Clear();
        RollLoot(blockId,
            new LootContext((int)blockPosition.X, (int)blockPosition.Y, (int)blockPosition.Z, count, secondary, state),
            _loot);

        foreach (var id in _loot)
            Spawn(id, blockPosition);
    }

    /// <summary>
    /// Explicit loot count for a destroyed stackable block: its state byte when it holds a
    /// player-built pile, else 0 meaning "derive from the world". Callers must read the state
    /// before the block is cleared, and pass 0 straight through for ordinary blocks.
    /// </summary>
    public static int PileCount(byte blockId, int x, int z, byte state)
    {
        var max = BlockType.ById[blockId]?.StackMax ?? 0;
        if (BlockType.ById[blockId]?.Renderer == BlockRenderer.Pebbles)
            return PebbleGenerator.TryDecode(state, max, out var total, out _) ? total : 0;
        return max >= 2 && state >= 1 && state <= max ? state : 0;
    }

    /// <summary>
    /// Obsidian share for a destroyed pebble pile: the packed count for a player pile, else 0
    /// meaning "derive it from the world hash". Other blocks always read 0.
    /// </summary>
    public static int PileSecondary(byte blockId, int x, int z, byte state)
    {
        if (BlockType.ById[blockId]?.Renderer != BlockRenderer.Pebbles)
            return 0;
        var max = BlockType.ById[blockId]?.StackMax ?? 0;
        return PebbleGenerator.TryDecode(state, max, out _, out var obsidian) ? obsidian : 0;
    }

    /// <summary>
    /// Whether the last <see cref="Update(float, Vector3, ChunkManager, Func{byte, bool})"/> saw a
    /// drop within pickup range that the pool refused (a full inventory). The HUD's "Inventory
    /// full" warning fires on this turning true and stays silent while it stays true; a drop the
    /// pool has room for again clears it.
    /// </summary>
    public bool PickupBlocked => _blockedCollectors.Contains(0);

    // Collector indices with a drop in range they refused this update (see BlockedCollectors)
    private readonly List<int> _blockedCollectors = [];

    /// <summary>
    /// The collector indices that had a drop within their own pickup range refused this update (a
    /// full pool): the local player's index drives the HUD's "Inventory full" warning, so a goblin
    /// refusing a stone at its feet never warns anyone. Empty when nothing was refused.
    /// </summary>
    public IReadOnlyList<int> BlockedCollectors => _blockedCollectors;

    /// <summary>
    /// Advances the drops, collecting what reaches the player. A drop whose id
    /// <paramref name="canCollect"/> refuses is never attracted and stays on the ground,
    /// so a full inventory pool ignores pickups instead of sucking them up.
    /// </summary>
    public void Update(float deltaTime, Vector3 playerPosition, ChunkManager chunks,
        Func<byte, bool>? canCollect = null)
    {
        _collected.Clear();
        _collectedBy.Clear();
        _blockedCollectors.Clear();
        if (_drops.Count == 0) return;

        var target = playerPosition - Vector3.UnitY * CollectHeightOffset;
        var rangeSquared = PickupRange * PickupRange;

        // Back to front so removing a collected drop doesn't skip the next one
        for (var i = _drops.Count - 1; i >= 0; i--)
        {
            var drop = _drops[i];
            var wanted = canCollect?.Invoke(drop.BlockId) ?? true;
            var inRange = Vector3.DistanceSquared(drop.Position, target) <= rangeSquared;
            // A drop within reach that the pool refuses is what the HUD's "Inventory full"
            // warning reads; it is still not attracted, so the drop stays on the ground
            if (!wanted && inRange && !_blockedCollectors.Contains(0)) _blockedCollectors.Add(0);

            if (!drop.Update(deltaTime, target, wanted && inRange, chunks)) continue;

            // Belt and braces: a drop that latched before its pool filled still needs room
            if (!wanted && !(canCollect?.Invoke(drop.BlockId) ?? true)) continue;

            _collected.Add(drop.BlockId);
            _drops.RemoveAt(i);
        }
    }

    /// <summary>
    /// The multiplayer server's pickup: every drop flies to the nearest collector whose pool has
    /// room for it (attracted once within <see cref="PickupRange"/>, as in single player), and a
    /// collected drop is reported in <see cref="Collected"/> with its collector's index in
    /// <see cref="CollectedBy"/>, so each player's own inventory is credited. A drop whose chunk is
    /// not loaded is left untouched (neither collected nor simulated, so it cannot fall through the
    /// unloaded air); one nobody can take lets go of any player it was flying at and falls back to
    /// the ground.
    /// </summary>
    public void Update(float deltaTime, IReadOnlyList<DropCollector> collectors, ChunkManager chunks)
    {
        _collected.Clear();
        _collectedBy.Clear();
        _blockedCollectors.Clear();
        if (_drops.Count == 0) return;

        var rangeSquared = PickupRange * PickupRange;
        var lift = Vector3.UnitY * CollectHeightOffset;

        // Back to front so removing a collected drop doesn't skip the next one
        for (var i = _drops.Count - 1; i >= 0; i--)
        {
            var drop = _drops[i];
            var (cx, cy, cz) = drop.Position.BlockCell();
            if (chunks.ChunkAt(cx, cy, cz) == null) continue;

            var nearest = -1;
            var nearestSquared = float.MaxValue;
            var target = Vector3.Zero;
            for (var c = 0; c < collectors.Count; c++)
            {
                var collector = collectors[c];
                var aim = collector.Eye - lift;
                var distanceSquared = Vector3.DistanceSquared(drop.Position, aim);
                // A collector within reach that refuses the drop is a blocked pool (see
                // BlockedCollectors), and is not a candidate to fly it in
                if (!(collector.CanCollect?.Invoke(drop.BlockId) ?? true))
                {
                    if (distanceSquared <= rangeSquared && !_blockedCollectors.Contains(c))
                        _blockedCollectors.Add(c);
                    continue;
                }
                if (distanceSquared >= nearestSquared) continue;
                nearest = c;
                nearestSquared = distanceSquared;
                target = aim;
            }

            if (nearest < 0)
            {
                drop.Release();
                drop.Update(deltaTime, drop.Position, false, chunks);
                continue;
            }

            if (!drop.Update(deltaTime, target, nearestSquared <= rangeSquared, chunks)) continue;

            _collected.Add(drop.BlockId);
            _collectedBy.Add(nearest);
            _drops.RemoveAt(i);
        }
    }
}
