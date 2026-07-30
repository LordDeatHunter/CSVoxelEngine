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
    /// <summary>How close the player must get before a drop starts flying towards them (1.5 m).</summary>
    public const float PickupRange = 1.5f * WorldScale.BlocksPerMeter;

    // Drops aim below the eye so they fly to the player's chest rather than into the camera
    private const float CollectHeightOffset = 0.35f * WorldScale.BlocksPerMeter;

    private readonly List<ItemDrop> _drops = [];
    private readonly List<byte> _collected = [];

    public IReadOnlyList<ItemDrop> Drops => _drops;

    /// <summary>
    /// Block ids collected during the most recent <see cref="Update"/>. Valid until the next one.
    /// </summary>
    public ReadOnlySpan<byte> Collected => CollectionsMarshal.AsSpan(_collected);

    /// <summary>
    /// Drops a block at the centre of the cell it occupied. Air and unknown ids drop nothing.
    /// </summary>
    public void Spawn(byte blockId, Vector3 blockPosition)
    {
        if (BlockType.ById[blockId] == null) return;

        _drops.Add(new ItemDrop(blockId, blockPosition + new Vector3(0.5f)));
    }

    public void Update(float deltaTime, Vector3 playerPosition)
    {
        _collected.Clear();
        if (_drops.Count == 0) return;

        var target = playerPosition - Vector3.UnitY * CollectHeightOffset;
        var rangeSquared = PickupRange * PickupRange;

        // Back to front so removing a collected drop doesn't skip the next one
        for (var i = _drops.Count - 1; i >= 0; i--)
        {
            var drop = _drops[i];
            var inRange = Vector3.DistanceSquared(drop.Position, target) <= rangeSquared;

            if (!drop.Update(deltaTime, target, inRange)) continue;

            _collected.Add(drop.BlockId);
            _drops.RemoveAt(i);
        }
    }
}
