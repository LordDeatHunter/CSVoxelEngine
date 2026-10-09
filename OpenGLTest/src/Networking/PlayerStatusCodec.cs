using System;
using System.IO;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// One <see cref="NetMessage.PlayerStatus"/>: the creative and flight modes the server granted, the
/// player's health and whether it is dead, and a mob hit's shove for the client's own physics to
/// apply (<see cref="Knockback"/>, a horizontal impulse in blocks/s, and <see cref="Lift"/>; both
/// zero when no hit is pending).
/// </summary>
public readonly record struct PlayerStatusData(NetPlayerFlags Modes, int Health, bool Dead, Vector3 Knockback, float Lift);

/// <summary>
/// The wire form of a <see cref="PlayerStatusData"/>: <c>(byte flags, int health, bool dead,
/// Vector3 knockbackImpulse, float lift)</c>. The reader is bounds-checked: mode bits other than
/// creative and flight are dropped, the health is clamped to 0..<see cref="PlayerHealth.MaxHealth"/>,
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
    }

    /// <summary>Reads one status written by <see cref="Write"/>, starting after the message id.</summary>
    public static PlayerStatusData Read(PacketReader reader)
    {
        var modes = (NetPlayerFlags)reader.ReadByte() & (NetPlayerFlags.Creative | NetPlayerFlags.Flying);
        var health = Math.Clamp(reader.ReadInt(), 0, PlayerHealth.MaxHealth);
        var dead = reader.ReadBool();
        var knockback = reader.ReadVector3();
        if (!float.IsFinite(knockback.X) || !float.IsFinite(knockback.Y) || !float.IsFinite(knockback.Z))
            throw new InvalidDataException("player status knockback is not finite");
        var lift = reader.ReadFloat();
        if (!float.IsFinite(lift))
            throw new InvalidDataException("player status lift is not finite");
        return new PlayerStatusData(modes, health, dead, knockback, lift);
    }
}
