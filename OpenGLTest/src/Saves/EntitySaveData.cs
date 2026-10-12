using System.Collections.Generic;
using VoxelEngine.Entities;

namespace VoxelEngine.Saves;

/// <summary>
/// The JSON shape of a world's <c>entities.json</c> (<see cref="EntitySaveCodec"/>): the live
/// mobs and the item drops lying in the world when it was saved. Every field is optional, so an
/// older or hand-edited file degrades field by field instead of failing the load; vectors travel
/// as <c>float[3]</c>.
/// <para>
/// Content is referenced by name, never by byte id: a mob by its <see cref="EntityType.Name"/>,
/// a drop by its <see cref="SaveNames"/> name (<c>block/...</c> or <c>item/...</c>). Whoever
/// restores the data drops an entry whose name no longer resolves.
/// </para>
/// </summary>
public sealed class EntitySaveData
{
    public List<MobData>? Mobs { get; set; }
    public List<DropData>? Drops { get; set; }

    /// <summary>One live mob. AI timers are not recorded: they restart on load.</summary>
    public sealed class MobData
    {
        /// <summary><see cref="EntityType.Name"/>, e.g. the entity JSON's file name.</summary>
        public string? Type { get; set; }

        /// <summary>The body centre (<see cref="Mob.Position"/>), not the feet.</summary>
        public float[]? Position { get; set; }

        public int? Health { get; set; }

        /// <summary>Yaw in radians (<see cref="Mob.Facing"/>).</summary>
        public float? Facing { get; set; }

        public float[]? Velocity { get; set; }

        /// <summary>Flint still carried; only meaningful for a goblin.</summary>
        public int? Ammo { get; set; }

        /// <summary>The body size (<see cref="Slime.Size"/>); only meaningful for a slime. Absent,
        /// as in a file saved before slimes had sizes, restores the smallest size.</summary>
        public int? Size { get; set; }

        /// <summary>Torches still carried (<see cref="Goblin.Torches"/>); only meaningful for a
        /// goblin. Absent, as in a file saved before goblins carried torches, restores none.</summary>
        public int? Torches { get; set; }
    }

    /// <summary>One item drop lying (or falling) in the world.</summary>
    public sealed class DropData
    {
        /// <summary>A <see cref="SaveNames"/> name of the block or item the drop collects as.</summary>
        public string? Item { get; set; }

        public float[]? Position { get; set; }
        public float[]? Velocity { get; set; }

        /// <summary>Seconds since the drop appeared, which carries its pickup delay.</summary>
        public float? Age { get; set; }
    }
}
