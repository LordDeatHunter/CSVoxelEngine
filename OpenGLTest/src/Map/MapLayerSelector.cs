namespace VoxelEngine.Map;

/// <summary>
/// The map's current layer with a hold on switching kinds: a different kind (surface, sky, cave) must be wanted for
/// <see cref="MapConfig.LayerSwitchSeconds"/> before the map changes to it, so a cave mouth, a doorway or a tree
/// trunk overhead never makes it flicker. A cave slice that only moved (the player climbed or dug down) follows at
/// once, since the kind is unchanged. The first update takes its layer at once. GL-free; main thread only.
/// </summary>
public sealed class MapLayerSelector
{
    private bool _started;
    private float _wantedFor;

    /// <summary>The layer the map shows.</summary>
    public MapLayer Current { get; private set; } = MapLayer.Surface;

    /// <summary>Advances the hold by <paramref name="deltaTime"/> toward <paramref name="wanted"/>; true when
    /// <see cref="Current"/> changed.</summary>
    public bool Update(MapLayer wanted, float deltaTime)
    {
        if (!_started || wanted.Kind == Current.Kind)
        {
            _started = true;
            _wantedFor = 0f;
            if (wanted == Current) return false;
            Current = wanted;
            return true;
        }

        _wantedFor += deltaTime;
        if (_wantedFor < MapConfig.LayerSwitchSeconds) return false;
        _wantedFor = 0f;
        Current = wanted;
        return true;
    }
}
