using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// The block-contact damage rule (a cactus), GL-free: a body whose box touches a cell whose block
/// deals contact damage takes it once per <see cref="TickSeconds"/> while the touch lasts. A body
/// standing on top of the cell is not touching it (the cactus is its floor), and a body merely
/// beside it counts even though the collision keeps its box flush against the face, so the test
/// reaches <see cref="TouchMargin"/> past the box. The world bills the local pool
/// (<see cref="World"/>), a server bills each <see cref="ServerPlayer"/>.
/// </summary>
public static class ContactDamage
{
    /// <summary>Seconds between contact damage ticks.</summary>
    public const float TickSeconds = 0.5f;

    /// <summary>How far past the body box a cell still counts as touched, so standing flush
    /// against a cactus face (the collision keeps the box out of the cell) still pricks.</summary>
    public const float TouchMargin = 0.08f;

    /// <summary>
    /// Whether a body box (<paramref name="radius"/> around its centre, <paramref name="height"/>
    /// tall) with its feet at <paramref name="feet"/> touches a contact-damage cell it is not
    /// standing on top of, and the most damage one deals. False for an unloaded read (an unknown
    /// cell is air).
    /// </summary>
    public static bool Touches(ChunkManager chunks, Vector3 feet, float radius, float height, out int damage)
    {
        damage = 0;
        if (chunks == null) return false;
        var min = new Vector3(feet.X - radius - TouchMargin, feet.Y, feet.Z - radius - TouchMargin);
        var max = new Vector3(feet.X + radius + TouchMargin, feet.Y + height, feet.Z + radius + TouchMargin);
        VoxelCoords.CellRange(min, max, out var minX, out var minY, out var minZ, out var maxX, out var maxY, out var maxZ);
        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        {
            var cellDamage = BlockType.ContactDamageAt(chunks.GetBlockGlobal(x, y, z));
            if (cellDamage <= 0f) continue;
            // Standing on top of the cell: its top face is the floor under the feet
            if (feet.Y >= y + 1f - 1e-3f) continue;
            damage = Math.Max(damage, (int)cellDamage);
        }
        return damage > 0;
    }

    /// <summary>
    /// Advances a contact tick timer by <paramref name="deltaTime"/> and returns the damage this
    /// frame deals while <paramref name="touching"/> (0 on other frames); leaving the touch
    /// clears the partial tick. The world's local pool and a server player's status both tick
    /// through this one rule.
    /// </summary>
    public static int Tick(ref float timer, float deltaTime, int damage, bool touching)
    {
        if (!touching || damage <= 0)
        {
            timer = 0f;
            return 0;
        }

        timer += MathF.Max(0f, deltaTime);
        var total = 0;
        while (timer >= TickSeconds)
        {
            timer -= TickSeconds;
            total += damage;
        }
        return total;
    }
}
