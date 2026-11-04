using System;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Interaction;

namespace VoxelEngine.Networking;

/// <summary>
/// What each <see cref="NetAttackKind"/> stands for, in one place: the attack pattern the held item
/// must declare for it, the sword swing it names, and the cosmetic <see cref="NetPlayerAction"/>
/// the server rebroadcasts it as. GL-free, shared by the client's attack loop, the server's replay
/// and the tests.
/// </summary>
public static class NetAttacks
{
    /// <summary>The last kind a reader accepts; anything past it is unknown.</summary>
    public const NetAttackKind Last = NetAttackKind.SwordChop;

    /// <summary>The attack pattern a held item must declare for a swing of <paramref name="kind"/> to be legal.</summary>
    public static AttackPattern PatternOf(NetAttackKind kind) => kind switch
    {
        NetAttackKind.Slice or NetAttackKind.SliceReturn => AttackPattern.Slice,
        NetAttackKind.SwordCut or NetAttackKind.SwordBackCut or NetAttackKind.SwordChop => AttackPattern.Sword,
        _ => AttackPattern.Punch,
    };

    /// <summary>Whether <paramref name="kind"/> is one of the sword's swings.</summary>
    public static bool IsSword(NetAttackKind kind) => PatternOf(kind) == AttackPattern.Sword;

    /// <summary>The intent kind of a sword <paramref name="swing"/>.</summary>
    public static NetAttackKind OfSword(SwordSwing swing) => swing switch
    {
        SwordSwing.BackCut => NetAttackKind.SwordBackCut,
        SwordSwing.Chop => NetAttackKind.SwordChop,
        _ => NetAttackKind.SwordCut,
    };

    /// <summary>The sword swing <paramref name="kind"/> names; throws for a kind that is not a sword swing.</summary>
    public static SwordSwing SwordSwingOf(NetAttackKind kind) => kind switch
    {
        NetAttackKind.SwordCut => SwordSwing.Cut,
        NetAttackKind.SwordBackCut => SwordSwing.BackCut,
        NetAttackKind.SwordChop => SwordSwing.Chop,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a sword swing"),
    };

    /// <summary>The swing other clients see for an attack of <paramref name="kind"/>.</summary>
    public static NetPlayerAction ActionOf(NetAttackKind kind) => kind switch
    {
        NetAttackKind.Slice => NetPlayerAction.Slice,
        NetAttackKind.SliceReturn => NetPlayerAction.SliceReturn,
        NetAttackKind.SwordCut => NetPlayerAction.SwordCut,
        NetAttackKind.SwordBackCut => NetPlayerAction.SwordBackCut,
        NetAttackKind.SwordChop => NetPlayerAction.SwordChop,
        _ => NetPlayerAction.Punch,
    };
}
