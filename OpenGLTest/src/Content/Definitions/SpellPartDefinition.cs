namespace VoxelEngine.Content.Definitions;

/// <summary>
/// JSON shape of <c>assets/data/spell/&lt;kind&gt;/&lt;name&gt;.json</c>. The kind comes from the folder,
/// never from a field.
/// </summary>
public sealed class SpellPartDefinition
{
    /// <summary>Display name; default the file name.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// The share of the starting mana pool (<see cref="VoxelEngine.Config.ManaConfig.StartMax"/>) the part
    /// adds to a spell's cost, in 0..1, exclusive of 0. Required.
    /// </summary>
    public float? CostShare { get; set; }

    /// <summary>How complex the part is, 0 or more; the recipe's summed complexity feeds its fizzle chance. Required.</summary>
    public float? Complexity { get; set; }

    /// <summary>The part's tags, at least one, lowercase: what a staff tier allows (a part passes when any tag is allowed).</summary>
    public string[]? Tags { get; set; }

    /// <summary>Modifier only: the factor the part multiplies a spell's damage by; default 1.</summary>
    public float? DamageFactor { get; set; }

    /// <summary>Modifier only: the factor the part multiplies a spell's range (projectile flight) by; default 1.</summary>
    public float? RangeFactor { get; set; }

    /// <summary>
    /// Element only: the name of the status effect (<c>assets/data/effects/&lt;name&gt;.json</c>) the element
    /// applies on hit; none when unset. Must name a loaded effect.
    /// </summary>
    public string? Effect { get; set; }
}
