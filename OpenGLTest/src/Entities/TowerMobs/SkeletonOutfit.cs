using System;
using VoxelEngine.Content;

namespace VoxelEngine.Entities.TowerMobs;

/// <summary>
/// The armor a tower skeleton wears, fixed at its spawn: leather or copper over its head, chest or legs, drawn as tinted
/// shells over those parts (<see cref="RiggedMob.ArmorPieceOn"/>) and softening every blow it takes by a share per piece
/// (<see cref="DamageReduction"/>). One of <see cref="Count"/> outfits, so its <see cref="Code"/> fits four bits of the
/// skeleton's wire byte (<see cref="Skeleton.PackLook"/>) and its save. A warrior often rolls leather and rarely a copper
/// piece, an archer now and then a single leather piece (<see cref="Roll"/>). The pieces are a look, not items: they
/// never drop. GL-free.
/// </summary>
public readonly record struct SkeletonOutfit
{
    /// <summary>How many outfits there are (codes 0..Count - 1).</summary>
    public const int Count = 16;

    /// <summary>The armor slots a skeleton fills: head, chest, legs.</summary>
    public const int SlotCount = 3;

    // The outfits that are all leather (codes 1..7) come before the ones with a copper piece (8..15)
    private const int FirstCopper = 8;

    private const SkeletonArmor N = SkeletonArmor.None, L = SkeletonArmor.Leather, C = SkeletonArmor.Copper;

    // (head, chest, legs) per code
    private static readonly SkeletonArmor[][] Table =
    [
        [N, N, N], [L, N, N], [N, L, N], [N, N, L], [L, L, N], [N, L, L], [L, N, L], [L, L, L],
        [C, N, N], [N, C, N], [N, N, C], [C, L, N], [L, C, N], [N, C, L], [C, L, L], [L, C, L],
    ];

    // The items whose sprite colors tint the shells: the leather piece of each slot, then the copper ingot
    private static byte[]? _tintItems;

    private SkeletonOutfit(int code) => Code = code;

    /// <summary>Bare bones.</summary>
    public static readonly SkeletonOutfit None = new(0);

    /// <summary>The outfit's code, 0..<see cref="Count"/> - 1: what the save and the wire carry.</summary>
    public int Code { get; }

    /// <summary>The outfit of <paramref name="code"/>, clamped to the table, so a damaged save or a bad byte never
    /// throws.</summary>
    public static SkeletonOutfit FromCode(int code) => new(Math.Clamp(code, 0, Count - 1));

    /// <summary>What armor slot <paramref name="slot"/> (head 0, chest 1, legs 2) holds.</summary>
    public SkeletonArmor At(int slot) => (uint)slot < SlotCount ? Table[Code][slot] : SkeletonArmor.None;

    /// <summary>The share of every blow the outfit takes off: <see cref="MobConfig.SkeletonLeatherArmorShare"/> per
    /// leather piece and <see cref="MobConfig.SkeletonCopperArmorShare"/> per copper one.</summary>
    public float DamageReduction
    {
        get
        {
            var share = 0f;
            for (var slot = 0; slot < SlotCount; slot++)
                share += At(slot) switch
                {
                    SkeletonArmor.Leather => MobConfig.SkeletonLeatherArmorShare,
                    SkeletonArmor.Copper => MobConfig.SkeletonCopperArmorShare,
                    _ => 0f,
                };
            return share;
        }
    }

    /// <summary>
    /// A fresh skeleton's outfit from two uniform rolls in [0, 1): <paramref name="chance"/> picks bare bones, leather
    /// or copper by the role's chances, <paramref name="pick"/> one outfit of that kind. A warrior wears copper with
    /// <see cref="MobConfig.SkeletonWarriorCopperChance"/> and leather with <see cref="MobConfig.SkeletonWarriorLeatherChance"/>;
    /// an archer a single leather piece with <see cref="MobConfig.SkeletonArcherLeatherChance"/>.
    /// </summary>
    public static SkeletonOutfit Roll(SkeletonRole role, float chance, float pick)
    {
        if (role == SkeletonRole.Archer)
            return chance < MobConfig.SkeletonArcherLeatherChance ? new(1 + Pick(pick, SlotCount)) : None;
        if (chance < MobConfig.SkeletonWarriorCopperChance)
            return new(FirstCopper + Pick(pick, Count - FirstCopper));
        return chance < MobConfig.SkeletonWarriorCopperChance + MobConfig.SkeletonWarriorLeatherChance
            ? new(1 + Pick(pick, FirstCopper - 1))
            : None;
    }

    /// <summary>The armor slot that covers rig part <paramref name="part"/> (the head, the torso, either leg), or -1:
    /// the arms stay bare, so the bow and the dagger they hold are never wrapped.</summary>
    public static int SlotOf(string part) => part switch
    {
        "Head" => 0,
        "Torso" => 1,
        "LegL" or "LegR" => 2,
        _ => -1,
    };

    /// <summary>The item whose sprite color tints the shell over rig part <paramref name="part"/>, or 0 when no piece
    /// covers it.</summary>
    public byte TintItemOn(string part)
    {
        var slot = SlotOf(part);
        var tints = _tintItems ??=
        [
            ContentRegistry.RequireItem("leather_cap").Id, ContentRegistry.RequireItem("leather_tunic").Id,
            ContentRegistry.RequireItem("leather_pants").Id, ContentRegistry.RequireItem("copper_ingot").Id,
        ];
        return At(slot) switch
        {
            SkeletonArmor.Leather => tints[slot],
            SkeletonArmor.Copper => tints[SlotCount],
            _ => 0,
        };
    }

    private static int Pick(float unit, int count) => Math.Min(count - 1, (int)(unit * count));
}
