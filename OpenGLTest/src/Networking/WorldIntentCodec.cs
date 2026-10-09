using System;
using System.IO;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// The wire form of a <see cref="WorldIntent"/>: the sequence id, the kind, the eye and the aim,
/// then only the fields that kind reads. The reader is bounds-checked: an unknown kind or attack, a
/// non-finite or far-out eye, an aim that is not (close to) a unit vector, a face normal component
/// outside -1..1 or a non-finite charge is a malformed frame (<see cref="InvalidDataException"/>),
/// which the server answers by dropping the sender; a short frame throws
/// <see cref="EndOfStreamException"/> the same way. The aim is renormalised and the charge clamped
/// to 0..1. Values that merely fail a rule (a cell out of reach, a refused tool) parse fine and are
/// refused by the server instead. GL-free and content-free.
/// </summary>
public static class WorldIntentCodec
{
    /// <summary>Largest eye coordinate accepted: well past any reachable world position.</summary>
    public const float MaxCoordinate = 3.0e7f;

    public static void Write(PacketWriter writer, int seq, in WorldIntent intent)
    {
        writer.Write(seq);
        writer.Write((byte)intent.Kind);
        writer.Write(intent.Eye);
        writer.Write(intent.Direction);
        switch (intent.Kind)
        {
            case NetIntentKind.BreakStart:
            case NetIntentKind.BreakComplete:
                WriteCell(writer, intent);
                break;
            case NetIntentKind.Place:
                WriteCell(writer, intent);
                writer.Write((byte)intent.NormalX);
                writer.Write((byte)intent.NormalY);
                writer.Write((byte)intent.NormalZ);
                break;
            case NetIntentKind.Use:
                writer.Write(intent.HasCell);
                if (intent.HasCell)
                    WriteCell(writer, intent);
                break;
            case NetIntentKind.Throw:
                writer.Write(intent.Charge);
                break;
            case NetIntentKind.Attack:
                writer.Write((byte)intent.Attack);
                break;
            case NetIntentKind.Respawn:
                break;
            default:
                throw new ArgumentException($"Unknown world intent {intent.Kind}.", nameof(intent));
        }
    }

    /// <summary>Reads one intent written by <see cref="Write"/>, starting at its sequence id.</summary>
    public static WorldIntent Read(PacketReader reader, out int seq)
    {
        seq = reader.ReadInt();
        var kind = (NetIntentKind)reader.ReadByte();
        if (kind is < NetIntentKind.BreakStart or > NetIntentKind.Respawn)
            throw new InvalidDataException($"unknown world intent {(byte)kind}");

        var eye = reader.ReadVector3();
        if (!IsFinite(eye) || MathF.Abs(eye.X) > MaxCoordinate || MathF.Abs(eye.Y) > MaxCoordinate
            || MathF.Abs(eye.Z) > MaxCoordinate)
            throw new InvalidDataException("world intent eye out of range");

        var direction = reader.ReadVector3();
        var length = IsFinite(direction) ? direction.Length() : float.NaN;
        if (!(length is >= 0.5f and <= 2f))
            throw new InvalidDataException("world intent aim is not a unit vector");
        direction /= length;

        switch (kind)
        {
            case NetIntentKind.BreakStart:
            case NetIntentKind.BreakComplete:
            {
                var (x, y, z) = ReadCell(reader);
                return new WorldIntent(kind, eye, direction, x, y, z, HasCell: true);
            }
            case NetIntentKind.Place:
            {
                var (x, y, z) = ReadCell(reader);
                var nx = ReadUnitComponent(reader);
                var ny = ReadUnitComponent(reader);
                var nz = ReadUnitComponent(reader);
                return new WorldIntent(kind, eye, direction, x, y, z, HasCell: true,
                    NormalX: nx, NormalY: ny, NormalZ: nz);
            }
            case NetIntentKind.Use:
            {
                if (!reader.ReadBool())
                    return new WorldIntent(kind, eye, direction);
                var (x, y, z) = ReadCell(reader);
                return new WorldIntent(kind, eye, direction, x, y, z, HasCell: true);
            }
            case NetIntentKind.Throw:
            {
                var charge = reader.ReadFloat();
                if (!float.IsFinite(charge))
                    throw new InvalidDataException("world intent charge is not finite");
                return new WorldIntent(kind, eye, direction, Charge: Math.Clamp(charge, 0f, 1f));
            }
            case NetIntentKind.Attack:
            {
                var attack = (NetAttackKind)reader.ReadByte();
                if (attack is < NetAttackKind.Punch or > NetAttackKind.SliceReturn)
                    throw new InvalidDataException($"unknown attack {(byte)attack}");
                return new WorldIntent(kind, eye, direction, Attack: attack);
            }
            case NetIntentKind.Respawn:
                return new WorldIntent(kind, eye, direction);
            default:
                throw new InvalidDataException($"unknown world intent {(byte)kind}");
        }
    }

    private static void WriteCell(PacketWriter writer, in WorldIntent intent)
    {
        writer.Write(intent.X);
        writer.Write(intent.Y);
        writer.Write(intent.Z);
    }

    private static (int X, int Y, int Z) ReadCell(PacketReader reader) =>
        (reader.ReadInt(), reader.ReadInt(), reader.ReadInt());

    private static sbyte ReadUnitComponent(PacketReader reader)
    {
        var value = (sbyte)reader.ReadByte();
        if (value is < -1 or > 1)
            throw new InvalidDataException($"world intent normal component {value}");
        return value;
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
