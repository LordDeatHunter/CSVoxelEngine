namespace VoxelEngine.Entities.TowerMobs;

/// <summary>
/// The Skeleton King's ranged attack as a burst: the volley's bones leave the hand one after another over a short window
/// instead of all at once, the first at the release and the last at its end, evenly spaced between. GL-free; the king
/// ticks it with its cooldowns and hands each due bone to its manager (<see cref="SkeletonKing.TryTakeBurstBone"/>).
/// </summary>
public sealed class BoneBurst
{
    private int _total;
    private int _left;
    private float _seconds;
    private float _clock;

    /// <summary>Whether bones are still due.</summary>
    public bool Active => _left > 0;

    /// <summary>Starts a burst of <paramref name="bones"/> over <paramref name="seconds"/>, replacing any burst still
    /// running.</summary>
    public void Start(int bones, float seconds)
    {
        _total = _left = bones > 0 ? bones : 0;
        _seconds = seconds > 0f ? seconds : 0f;
        _clock = 0f;
    }

    /// <summary>Advances the burst's clock while bones are due.</summary>
    public void Tick(float deltaTime)
    {
        if (_left > 0) _clock += deltaTime;
    }

    /// <summary>True once for each bone whose moment has come: bone i of n leaves at i / (n - 1) of the window, so
    /// several can be due in one long frame and none is ever skipped.</summary>
    public bool TryTake()
    {
        if (_left == 0) return false;
        var index = _total - _left;
        var due = _total == 1 ? 0f : _seconds * index / (_total - 1);
        if (_clock < due) return false;
        _left--;
        return true;
    }

    /// <summary>Drops the bones still due (the king died mid-burst).</summary>
    public void Stop() => _left = 0;
}
