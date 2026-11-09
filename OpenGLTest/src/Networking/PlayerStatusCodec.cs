using System;
using System.IO;
using System.Numerics;
using VoxelEngine.Player;
using VoxelEngine.WorldGen.Caves;

namespace VoxelEngine.Networking;

/// <summary>
/// One <see cref="NetMessage.PlayerStatus"/>: the creative and flight modes the server granted, the
/// player's health and whether it is dead, and a mob hit's shove for the client's own physics to
/// apply (<see cref="Knockback"/>, a horizontal impulse in blocks/s, and <see cref="Lift"/>; both
/// zero when no hit is pending), the player's air in <see cref="Breath.NetSteps"/> (a full lung
/// unless stated), and its mana and the mana max in whole units (<see cref="PlayerMana.NetMana"/>,
/// <see cref="PlayerMana.Max"/>; a fresh full pool unless stated), and its active status effects as a
/// bitmask of each effect's <c>StatusEffect.Bit</c> (<see cref="EffectMask"/>; none unless stated), and the cave
/// biome at its eye (<see cref="CaveBiome"/>, plans/CAVE_BIOMES.md CB-3; plain outside caves and unless stated).
/// </summary>
public readonly record struct PlayerStatusData(NetPlayerFlags Modes, int Health, bool Dead, Vector3 Knockback, float Lift,
    byte Air = Breath.NetSteps, int Mana = ManaConfig.StartMax, int MaxMana = ManaConfig.StartMax, byte EffectMask = 0,
    CaveBiome CaveBiome = CaveBiome.Plain);

/// <summary>
/// The wire form of a <see cref="PlayerStatusData"/>: <c>(byte flags, int health, bool dead,
/// Vector3 knockbackImpulse, float lift, byte air, int mana, int maxMana, byte effectMask, byte caveBiome)</c>. The reader is bounds-checked: mode bits other than
/// creative and flight are dropped, the health is clamped to 0..<see cref="PlayerHealth.MaxHealth"/> plus the most gear can add, the air to 0..<see cref="Breath.NetSteps"/>,
/// the mana max to 1..<see cref="PlayerMana.WireMaxCap"/> and the mana into that max,
/// the effect mask is taken raw (the client drops the bits no loaded effect owns when it mirrors it),
/// an undefined cave biome reads as <see cref="CaveBiome.Plain"/>,
/// and a non-finite knockback or lift is a malformed frame (<see cref="InvalidDataException"/>), which
/// the client answers by leaving the server; a short frame throws <see cref="EndOfStreamException"/>
/// the same way. GL-free and content-free.
/// </summary>
public static class PlayerStatusCodec
{
    public static void Write(PacketWriter writer, in PlayerStatusData status)
    {
        writer.Write((byte)status.Modes);
        writer.Write(status.Health);
        writer.Write(status.Dead);
        writer.Write(status.Knockback);
        writer.Write(status.Lift);
        writer.Write(status.Air);
        writer.Write(status.Mana);
        writer.Write(status.MaxMana);
        writer.Write(status.EffectMask);
        writer.Write((byte)status.CaveBiome);
    }

    /// <summary>Reads one status written by <see cref="Write"/>, starting after the message id.</summary>
    public static PlayerStatusData Read(PacketReader reader)
    {
        var modes = (NetPlayerFlags)reader.ReadByte() & (NetPlayerFlags.Creative | NetPlayerFlags.Flying);
        var health = Math.Clamp(reader.ReadInt(), 0, PlayerHealth.MaxHealth + GearConfig.MaxHealthBonusCap);
        var dead = reader.ReadBool();
        var knockback = reader.ReadVector3();
        if (!float.IsFinite(knockback.X) || !float.IsFinite(knockback.Y) || !float.IsFinite(knockback.Z))
            throw new InvalidDataException("player status knockback is not finite");
        var lift = reader.ReadFloat();
        if (!float.IsFinite(lift))
            throw new InvalidDataException("player status lift is not finite");
        var air = Math.Min(reader.ReadByte(), (byte)Breath.NetSteps);
        var mana = reader.ReadInt();
        var maxMana = Math.Clamp(reader.ReadInt(), 1, PlayerMana.WireMaxCap);
        mana = Math.Clamp(mana, 0, maxMana);
        var effectMask = reader.ReadByte();
        var rawCave = (CaveBiome)reader.ReadByte();
        var cave = Enum.IsDefined(rawCave) ? rawCave : CaveBiome.Plain;
        return new PlayerStatusData(modes, health, dead, knockback, lift, air, mana, maxMana, effectMask, cave);
    }
}
