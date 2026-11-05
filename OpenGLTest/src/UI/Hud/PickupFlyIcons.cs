using System;
using System.Numerics;

namespace VoxelEngine.UI.Hud;

/// <summary>
/// The icons of just-collected drops arcing into their hotbar slots. GL-free: <see cref="DropPickup"/>
/// spawns one per local pickup with the drop's world position, the hotbar draw projects that to the
/// canvas each frame and places the icon with <see cref="Arc"/> and <see cref="ScaleAt"/>. A fixed
/// ring: when full, a new icon replaces the oldest. Main thread only.
/// </summary>
public sealed class PickupFlyIcons
{
    public const int Capacity = 16;

    /// <summary>One icon in flight.</summary>
    public struct Icon
    {
        public byte Id;
        public Vector3 WorldPosition;
        public int Slot;
        public float Elapsed;
    }

    private readonly Icon[] _icons = new Icon[Capacity];

    /// <summary>How many icons are in flight.</summary>
    public int Count { get; private set; }

    /// <summary>The <paramref name="index"/>th live icon.</summary>
    public Icon At(int index) => _icons[index];

    /// <summary>Flight progress of an icon, 0 at the drop and 1 at the slot.</summary>
    public static float Progress(Icon icon) => MathUtils.Clamp01(icon.Elapsed / HudConfig.PickupFlySeconds);

    /// <summary>Starts an icon of <paramref name="id"/> flying from <paramref name="worldPosition"/> to hotbar <paramref name="slot"/>.</summary>
    public void Spawn(byte id, Vector3 worldPosition, int slot)
    {
        var index = Count;
        if (Count == Capacity)
        {
            index = 0;
            for (var i = 1; i < Count; i++)
                if (_icons[i].Elapsed > _icons[index].Elapsed) index = i;
        }
        else
            Count++;
        _icons[index] = new Icon { Id = id, WorldPosition = worldPosition, Slot = slot };
    }

    /// <summary>Moves every icon on by <paramref name="deltaTime"/> and drops the finished ones.</summary>
    public void Advance(float deltaTime)
    {
        var step = MathF.Max(0f, deltaTime);
        for (var i = Count - 1; i >= 0; i--)
        {
            _icons[i].Elapsed += step;
            if (_icons[i].Elapsed < HudConfig.PickupFlySeconds) continue;
            _icons[i] = _icons[--Count];
        }
    }

    /// <summary>Empties the ring (a world opened or closed).</summary>
    public void Clear() => Count = 0;

    /// <summary>
    /// Canvas position at <paramref name="progress"/>: an accelerating straight run from
    /// <paramref name="start"/> to <paramref name="end"/> bowed upward by <see cref="HudConfig.PickupFlyArcPixels"/>
    /// at mid-flight. Exactly <paramref name="start"/> at 0 and <paramref name="end"/> at 1.
    /// </summary>
    public static Vector2 Arc(Vector2 start, Vector2 end, float progress)
    {
        var eased = MathUtils.EaseInQuad(progress);
        var lift = HudConfig.PickupFlyArcPixels * MathF.Sin(MathF.PI * eased);
        return Vector2.Lerp(start, end, eased) - new Vector2(0f, lift);
    }

    /// <summary>Icon size multiplier: full size at the drop, <see cref="HudConfig.PickupFlyEndScale"/> at the slot.</summary>
    public static float ScaleAt(float progress) =>
        MathUtils.Lerp(1f, HudConfig.PickupFlyEndScale, MathUtils.EaseInQuad(progress));
}
