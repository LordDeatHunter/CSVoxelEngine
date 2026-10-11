namespace silkgltriangle;

/// <summary>
/// Restores health (a bandage): heals the player the use runs for by
/// <see cref="WorldGenerationConfig.BandageHealAmount"/>, spending one unit from its own stack.
/// Refused at full health, so the stack is never spent for nothing. The pool is the local
/// player's in singleplayer and a client's mirrored one; on a server the Use intent heals the
/// sending player's own <see cref="PlayerHealth"/>.
/// </summary>
public class HealItem : Item
{
    internal HealItem(ItemFactory.ItemSpec spec) : base(spec)
    {
    }

    public override ItemUseResult OnUse(ItemUseContext ctx, ItemStack? stack)
    {
        var world = ctx.World;
        if (!world.Heal(WorldGenerationConfig.BandageHealAmount))
            return ItemUseResult.Ignored;
        if (!world.IsCreative)
            world.ConsumeSelected();
        return ItemUseResult.Succeed(null);
    }
}
