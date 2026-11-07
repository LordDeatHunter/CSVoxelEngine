using System;

namespace VoxelEngine.Entities;

/// <summary>
/// The checks a clip-animated mob kind runs on its model at startup, so a model missing a group,
/// a clip or a timeline event fails content load naming its file instead of misbehaving in play.
/// Each throws an <see cref="InvalidOperationException"/> that names the kind and the model file.
/// </summary>
public static class EntityContract
{
    /// <summary>The groups every humanoid rig (the goblin, the hell and sky mobs) must provide; the
    /// renderer draws each one and the shared walk and attack clips key them.</summary>
    public static readonly string[] HumanoidParts = ["Torso", "Head", "ArmL", "ArmR", "LegL", "LegR"];

    /// <summary>Throws unless the model has a non-empty group named after each of <paramref name="parts"/>.</summary>
    public static void RequireParts(EntityType type, string kind, string[] parts)
    {
        foreach (var part in parts)
        {
            if (!type.Model.Parts.TryGetValue(part, out var group) || group.Cubes.Count == 0)
                throw new InvalidOperationException(
                    $"{kind} model '{type.ModelPath}' needs a non-empty group named '{part}'.");
        }
    }

    /// <summary>The clip named <paramref name="name"/> (case-insensitive); throws when the model has none.</summary>
    public static AnimationClip RequireClip(EntityType type, string kind, string name)
    {
        foreach (var clip in type.Model.Animations)
        {
            if (string.Equals(clip.Name, name, StringComparison.OrdinalIgnoreCase))
                return clip;
        }

        throw new InvalidOperationException(
            $"{kind} model '{type.ModelPath}' has no animation named '{name}'.");
    }

    /// <summary>Throws unless <paramref name="clip"/> carries a timeline event named <paramref name="eventName"/>.</summary>
    public static void RequireEvent(EntityType type, string kind, AnimationClip clip, string eventName)
    {
        foreach (var e in clip.Events)
        {
            if (string.Equals(e.Name, eventName, StringComparison.OrdinalIgnoreCase))
                return;
        }

        throw new InvalidOperationException(
            $"{kind} model '{type.ModelPath}' animation '{clip.Name}' has no '{eventName}' timeline event.");
    }

    /// <summary>The humanoid contract with one attack: the <see cref="HumanoidParts"/> groups, the idle and
    /// walk clips, and <paramref name="attackClip"/> carrying <paramref name="attackEvent"/>.</summary>
    public static void RequireHumanoid(EntityType type, string kind, string attackClip, string attackEvent) =>
        RequireRig(type, kind, EntityAnimator.WalkClip, attackClip, attackEvent);

    /// <summary>The flyer's contract with one attack: the <see cref="HumanoidParts"/> groups (a bird's wings are
    /// its arms, its talons its legs), the idle and <see cref="EntityAnimator.FlyClip"/> clips, and
    /// <paramref name="attackClip"/> carrying <paramref name="attackEvent"/>.</summary>
    public static void RequireFlyer(EntityType type, string kind, string attackClip, string attackEvent) =>
        RequireRig(type, kind, EntityAnimator.FlyClip, attackClip, attackEvent);

    // The humanoid groups, the idle and moving clips, and the attack clip with its event
    private static void RequireRig(EntityType type, string kind, string moveClip, string attackClip, string attackEvent)
    {
        RequireParts(type, kind, HumanoidParts);
        RequireClip(type, kind, EntityAnimator.IdleClip);
        RequireClip(type, kind, moveClip);
        RequireEvent(type, kind, RequireClip(type, kind, attackClip), attackEvent);
    }
}
