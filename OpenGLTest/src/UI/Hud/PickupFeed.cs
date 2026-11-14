using System.Collections.Generic;

namespace VoxelEngine.UI.Hud;

/// <summary>
/// The bottom-right pickup feed: one row per item recently collected, "+gained (total)". Picking up
/// the same item again while its row is up merges into it (the gain accumulates, the total updates,
/// the clock restarts); other items stack as their own rows. A row leaves
/// <see cref="HudConfig.PickupFeedSeconds"/> after its last pickup. GL-free; main thread only.
/// </summary>
public sealed class PickupFeed
{
    /// <summary>One feed row.</summary>
    public struct Row
    {
        public ushort Id;
        public int Gained;
        public int Total;
        public float Age;
    }

    private readonly List<Row> _rows = [];

    /// <summary>Rows shown, oldest first (the newest draws at the bottom).</summary>
    public int Count => _rows.Count;

    public Row At(int index) => _rows[index];

    /// <summary>Notes <paramref name="count"/> of <paramref name="id"/> collected, leaving <paramref name="total"/> held.</summary>
    public void Record(ushort id, int count, int total)
    {
        var gained = count;
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Id != id) continue;
            gained += _rows[i].Gained;
            _rows.RemoveAt(i);
            break;
        }

        _rows.Add(new Row { Id = id, Gained = gained, Total = total });
        if (_rows.Count > HudConfig.PickupFeedMaxRows)
            _rows.RemoveAt(0);
    }

    /// <summary>Ages the rows and drops the ones past their time.</summary>
    public void Update(float deltaTime)
    {
        var dt = System.MathF.Max(0f, deltaTime);
        for (var i = _rows.Count - 1; i >= 0; i--)
        {
            var row = _rows[i];
            row.Age += dt;
            if (row.Age >= HudConfig.PickupFeedSeconds) _rows.RemoveAt(i);
            else _rows[i] = row;
        }
    }

    /// <summary>A row's alpha: 1 until the last fade seconds of its life, then easing to 0.</summary>
    public static float Alpha(Row row)
    {
        var fadeStart = HudConfig.PickupFeedSeconds - HudConfig.PickupFeedFadeSeconds;
        return 1f - MathUtils.SmoothStep(MathUtils.InverseLerp(0f, HudConfig.PickupFeedFadeSeconds, row.Age - fadeStart));
    }
}
