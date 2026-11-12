namespace VoxelEngine.Entities.Crawlers;

/// <summary>Which of its attacks a <see cref="Scorpion"/> landed (<see cref="Scorpion.TryTakeStrike"/>).</summary>
public enum ScorpionStrike
{
    /// <summary>The claws snap shut: damage and a hard shove.</summary>
    Pinch,

    /// <summary>The tail whips over the head: more damage and venom.</summary>
    Sting,

    /// <summary>The stinger flicks a gob of venom at a target out of reach.</summary>
    Spit,
}
