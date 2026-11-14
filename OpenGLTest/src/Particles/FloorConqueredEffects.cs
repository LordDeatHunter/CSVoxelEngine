using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Effects.Post;
using VoxelEngine.Player;
using VoxelEngine.Simulation.Tower;

namespace VoxelEngine.Particles;

/// <summary>
/// The cosmetic show of a battle tower's floor being conquered (<see cref="TowerManager.FloorConquered"/>): the clear
/// jingle (when the camera is near) and a few shockwave shells pulsing outward from the floor's axis across its whole
/// height until they reach the wall, each a <see cref="Shockwave"/> the post pass bends the screen with. Never played
/// for the boss: the roof altar's fight has no floor door. Singleplayer and a client, never a server. Main thread only.
/// </summary>
internal sealed class FloorConqueredEffects
{
    private readonly Camera _camera;
    private readonly WorldSounds _sounds;
    private readonly List<Show> _shows = [];
    private readonly List<Wave> _waves = [];
    private readonly List<Shockwave> _shockwaves = [];

    // A floor's show: where it pulses and how many cylinders are still to come
    private struct Show
    {
        public Vector3 Center;
        public float BottomY;
        public float TopY;
        public float Radius;
        public float Timer;
        public int Left;
    }

    // One cylinder in flight
    private struct Wave
    {
        public Vector3 Center;
        public float BottomY;
        public float TopY;
        public float Radius;
        public float Age;
    }

    internal FloorConqueredEffects(Camera camera, TowerManager towers, WorldSounds sounds)
    {
        _camera = camera;
        _sounds = sounds;
        towers.FloorConquered += OnConquered;
    }

    /// <summary>The shells in flight, newest first (the shader's few slots go to the freshest), for the post pass to bend the screen with.</summary>
    internal IReadOnlyList<Shockwave> Shockwaves => _shockwaves;

    /// <summary>How far out a floor's pulse reaches: the inside of its wall (the tower's radius less the apron and the wall).</summary>
    internal static float InteriorRadius(TowerRecord tower) =>
        tower.OuterRadius - WorldGenerationConfig.TowerApronDepth - WorldGenerationConfig.TowerWallThickness;

    private void OnConquered(TowerRecord tower, int floor)
    {
        var state = tower.Floors[floor];
        var center = new Vector3(tower.CenterX + 0.5f, state.BottomY, tower.CenterZ + 0.5f);
        if (Vector3.Distance(_camera.Position, center) < JuiceConfig.FloorConqueredSoundRangeBlocks)
            _sounds.Play(SfxLibrary.FloorClearGroup);
        _shows.Add(new Show
        {
            Center = center, BottomY = state.BottomY + 1f, TopY = state.TopY, Radius = InteriorRadius(tower),
            Timer = 0f, Left = JuiceConfig.FloorConqueredPulseCount,
        });
    }

    /// <summary>Launches each running show's next cylinder when its timer runs out and ages the cylinders in flight,
    /// dropping the ones that reached the wall.</summary>
    internal void Update(float deltaTime)
    {
        for (var i = _shows.Count - 1; i >= 0; i--)
        {
            var show = _shows[i];
            show.Timer -= deltaTime;
            if (show.Timer <= 0f)
            {
                Launch(show);
                show.Timer += JuiceConfig.FloorConqueredPulseIntervalSeconds;
                show.Left--;
            }
            if (show.Left <= 0) _shows.RemoveAt(i);
            else _shows[i] = show;
        }

        _shockwaves.Clear();
        for (var i = _waves.Count - 1; i >= 0; i--)
        {
            var wave = _waves[i];
            wave.Age += deltaTime;
            var radius = wave.Age * JuiceConfig.FloorConqueredWaveSpeed;
            if (radius >= wave.Radius)
            {
                _waves.RemoveAt(i);
                continue;
            }
            _waves[i] = wave;
            if (Vector3.Distance(_camera.Position, wave.Center) > JuiceConfig.FloorConqueredShockwaveRangeBlocks) continue;
            // The lens fades as the shell nears the wall
            _shockwaves.Add(new Shockwave(wave.Center, radius, wave.BottomY, wave.TopY, 1f - radius / wave.Radius,
                JuiceConfig.FloorConqueredShockwaveHalfThickness));
        }
    }

    private void Launch(Show show)
    {
        _waves.Add(new Wave { Center = show.Center, BottomY = show.BottomY, TopY = show.TopY, Radius = show.Radius });
    }
}
