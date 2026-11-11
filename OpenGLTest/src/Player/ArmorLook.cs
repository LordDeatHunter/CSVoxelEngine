using System;
using System.Collections.Concurrent;
using System.IO;
using StbImageSharp;
using VoxelEngine.Config;
using VoxelEngine.Items;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Player;

/// <summary>
/// What armor a body wears, as the renderer and the wire see it: the item id of each visible armor piece (head, chest, legs,
/// feet, gloves; 0 for none or for a piece whose <c>equip.visible</c> is off). Accessories are never drawn. Immutable and
/// GL-free; the color a piece is drawn in is the average of its item sprite (<see cref="ColorOf"/>).
/// </summary>
public sealed class ArmorLook : IEquatable<ArmorLook>
{
    /// <summary>The body parts each armor slot covers, in the player rig's part names.</summary>
    private static readonly string[][] Covered =
    [
        ["Head"],
        ["Torso", "UpperArmL", "UpperArmR"],
        ["ThighL", "ThighR"],
        ["ShinL", "ShinR"],
        ["ForearmL", "ForearmR"],
    ];

    private static readonly ConcurrentDictionary<ushort, (byte R, byte G, byte B)> Colors = new();

    private readonly ushort[] _pieces;

    private ArmorLook(ushort[] pieces) => _pieces = pieces;

    /// <summary>Wearing nothing visible.</summary>
    public static readonly ArmorLook None = new(new ushort[GearConfig.ArmorSlotCount]);

    /// <summary>Whether no piece shows.</summary>
    public bool IsNone => Array.TrueForAll(_pieces, p => p == 0);

    /// <summary>The item id drawn in armor slot <paramref name="slot"/> or 0 for none.</summary>
    public ushort PieceAt(int slot) => (uint)slot < _pieces.Length ? _pieces[slot] : (ushort)0;

    /// <summary>The look of what <paramref name="equipment"/> wears: its visible armor pieces.</summary>
    public static ArmorLook From(Equipment equipment)
    {
        var pieces = new ushort[GearConfig.ArmorSlotCount];
        for (var slot = 0; slot < pieces.Length; slot++)
            if (equipment.StackAt(slot) is { } stack && ItemRegistry.TryGet(stack.DefinitionId) is GearItem { Info.Visible: true })
                pieces[slot] = stack.DefinitionId;
        return Of(pieces);
    }

    /// <summary>The look of the given piece ids (one per armor slot); an id that is not visible armor of its slot is dropped,
    /// so a peer can never make a body wear something it cannot.</summary>
    public static ArmorLook FromWire(ReadOnlySpan<ushort> ids)
    {
        var pieces = new ushort[GearConfig.ArmorSlotCount];
        for (var slot = 0; slot < pieces.Length && slot < ids.Length; slot++)
            if (ItemRegistry.TryGet(ids[slot]) is GearItem { Info.Visible: true } gear && (int)gear.Info.Slot == slot)
                pieces[slot] = ids[slot];
        return Of(pieces);
    }

    private static ArmorLook Of(ushort[] pieces) => Array.TrueForAll(pieces, p => p == 0) ? None : new ArmorLook(pieces);

    /// <summary>Copies the piece ids, one per armor slot, for the wire.</summary>
    public void CopyTo(Span<ushort> destination) => _pieces.AsSpan().CopyTo(destination);

    /// <summary>The body parts armor slot <paramref name="slot"/> covers.</summary>
    public static string[] PartsOf(int slot) => Covered[slot];

    /// <summary>The color item <paramref name="itemId"/> is drawn in: the mean of its opaque sprite pixels, darkened by half, since entity light brightens a vertex color, so
    /// armor reads as material rather than as a glow. Gray when the sprite cannot be read.</summary>
    public static (byte R, byte G, byte B) ColorOf(ushort itemId) => Colors.GetOrAdd(itemId, Average);

    private static (byte, byte, byte) Average(ushort itemId)
    {
        try
        {
            var path = ItemRegistry.TryGet(itemId)?.TexturePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return (140, 140, 140);
            using var stream = File.OpenRead(path);
            var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            long r = 0, g = 0, b = 0, n = 0;
            for (var i = 0; i + 3 < image.Data.Length; i += 4)
            {
                if (image.Data[i + 3] < 128) continue;
                r += image.Data[i];
                g += image.Data[i + 1];
                b += image.Data[i + 2];
                n++;
            }
            if (n == 0) return (140, 140, 140);
            return ((byte)(r * 0.5 / n), (byte)(g * 0.5 / n), (byte)(b * 0.5 / n));
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            return (140, 140, 140);
        }
    }

    /// <inheritdoc />
    public bool Equals(ArmorLook? other) => other != null && _pieces.AsSpan().SequenceEqual(other._pieces);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ArmorLook);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_pieces[0], _pieces[1], _pieces[2], _pieces[3], _pieces[4]);
}
