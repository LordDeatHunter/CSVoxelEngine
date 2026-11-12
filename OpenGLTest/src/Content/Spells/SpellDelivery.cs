namespace VoxelEngine.Content.Spells;

/// <summary>How a shape part delivers its spell's element (its <c>delivery</c> field in the shape part JSON).</summary>
public enum SpellDelivery
{
    /// <summary>A bolt flies from the caster's eye along the aim and applies the element where it lands.</summary>
    Projectile,

    /// <summary>The element is applied to the caster and around its feet.</summary>
    Self,
}
