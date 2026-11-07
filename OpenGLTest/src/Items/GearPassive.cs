namespace VoxelEngine.Items;

/// <summary>One compiled passive of a piece of gear: when it fires and the status effect it applies.</summary>
/// <param name="Trigger">When it fires.</param>
/// <param name="Effect">The status effect name; validated against the loaded effects.</param>
/// <param name="Chance">Chance per trigger, above 0 and at most 1.</param>
/// <param name="Scale">The effect's duration scale.</param>
/// <param name="Below">For <see cref="PassiveTrigger.LowHealth"/>: the health share under which it applies.</param>
public readonly record struct GearPassive(PassiveTrigger Trigger, string Effect, float Chance, float Scale, float Below);
