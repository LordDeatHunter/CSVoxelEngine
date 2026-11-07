using System.Collections.Generic;

namespace VoxelEngine.Content.Spells;

/// <summary>
/// One compiled spell part from <c>assets/data/spell/&lt;kind&gt;/&lt;name&gt;.json</c>, immutable. Parts are
/// keyed by <see cref="Key"/> (<c>&lt;kind&gt;/&lt;name&gt;</c>), so parts of different kinds may share a file
/// name; they take no byte ids.
/// </summary>
public sealed class SpellPart
{
    internal SpellPart(SpellPartKind kind, string name, string displayName, float costShare, float complexity,
        IReadOnlySet<string> tags, float damageFactor, float rangeFactor, string? effect,
        SpellImpact impact, string? plant, SpellDelivery delivery)
    {
        Effect = effect;
        Impact = impact;
        Plant = plant;
        Delivery = delivery;
        Kind = kind;
        Name = name;
        DisplayName = displayName;
        CostShare = costShare;
        Complexity = complexity;
        Tags = tags;
        DamageFactor = damageFactor;
        RangeFactor = rangeFactor;
        Key = KeyOf(kind, name);
    }

    /// <summary>The part's kind, the folder it was loaded from.</summary>
    public SpellPartKind Kind { get; }

    /// <summary>The file name the part was loaded from, without extension.</summary>
    public string Name { get; }

    /// <summary>The name shown to the player.</summary>
    public string DisplayName { get; }

    /// <summary>The share of the starting mana pool the part adds to a spell's cost, in 0..1, exclusive of 0.</summary>
    public float CostShare { get; }

    /// <summary>The part's complexity, 0 or more; summed over a recipe it feeds the fizzle chance.</summary>
    public float Complexity { get; }

    /// <summary>The part's tags; a staff allows the part when it allows any of them.</summary>
    public IReadOnlySet<string> Tags { get; }

    /// <summary>The factor the part multiplies a spell's damage by (1 for every part but a modifier that sets it).</summary>
    public float DamageFactor { get; }

    /// <summary>The factor the part multiplies a spell's range by (1 for every part but a modifier that sets it).</summary>
    public float RangeFactor { get; }

    /// <summary>
    /// The name of the status effect an element applies to what it hits (a key of
    /// <see cref="VoxelEngine.Content.StatusEffects.StatusEffectRegistry"/>), read by the spell cast's hit
    /// (MG-5); null for an element that applies none and for every other kind.
    /// </summary>
    public string? Effect { get; }

    /// <summary>What an element does to the cells it lands on; <see cref="SpellImpact.None"/> for every other kind.</summary>
    public SpellImpact Impact { get; }

    /// <summary>
    /// The tree species an element with <see cref="SpellImpact.Grow"/> plants on bare ground (a key of
    /// <see cref="VoxelEngine.WorldGen.Trees.TreeSpeciesRegistry"/>); null when it plants none and for every other kind.
    /// </summary>
    public string? Plant { get; }

    /// <summary>
    /// How a shape delivers its element. Meaningful only on a shape; every other kind holds
    /// <see cref="SpellDelivery.Projectile"/>.
    /// </summary>
    public SpellDelivery Delivery { get; }

    /// <summary>The registry key, <c>&lt;kind&gt;/&lt;name&gt;</c> (<see cref="KeyOf"/>).</summary>
    public string Key { get; }

    /// <summary>The folder name of a kind under <c>assets/data/spell</c>: the lowercase kind name.</summary>
    public static string FolderOf(SpellPartKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>The registry key of the part of that kind and file name.</summary>
    public static string KeyOf(SpellPartKind kind, string name) => FolderOf(kind) + "/" + name;

    /// <summary>Whether a staff allowing <paramref name="allowedTags"/> allows this part: true when any of its tags is in the set.</summary>
    public bool IsAllowedBy(IReadOnlySet<string> allowedTags)
    {
        foreach (var tag in Tags)
        {
            if (allowedTags.Contains(tag)) return true;
        }
        return false;
    }
}
