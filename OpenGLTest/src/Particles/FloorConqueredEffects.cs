using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Player;
using VoxelEngine.Simulation.Tower;

namespace VoxelEngine.Particles;

/// <summary>
/// The cosmetic show of a battle tower's floor being conquered (<see cref="TowerManager.FloorConquered"/>): the clear
/// jingle (when the camera is near) and a few rings of glowing motes pulsing outward from the middle of the floor
/// (<see cref="FireParticleSystem.PulseRing"/>). Never played for the boss: the roof altar's fight has no floor door.
/// Singleplayer and a client, never a server. Main thread only.
/// </summary>
internal sealed class FloorConqueredEffects
{
    private readonly Camera _camera;
    private readonly FireParticleSystem _fire;
    private readonly SfxManager? _sfx;
    private readonly Random _random;
    private readonly List<Pulse> _pulses = [];

    private struct Pulse
    {
        public Vector3 Center;
        public float Timer;
        public int Left;
    }

    internal FloorConqueredEffects(Camera camera, FireParticleSystem fire, TowerManager towers, SfxManager? sfx,
        Random? random = null)
    {
        _camera = camera;
        _fire = fire;
        _sfx = sfx;
        _random = random ?? Random.Shared;
        towers.FloorConquered += OnConquered;
    }

    private void OnConquered(TowerRecord tower, int floor)
    {
        var center = new Vector3(tower.CenterX + 0.5f, tower.Floors[floor].BottomY + JuiceConfig.FloorConqueredRiseBlocks,
            tower.CenterZ + 0.5f);
        if (Vector3.Distance(_camera.Position, center) < JuiceConfig.FloorConqueredSoundRangeBlocks)
            _sfx?.Play(SfxLibrary.FloorClearGroup);
        _pulses.Add(new Pulse { Center = center, Timer = 0f, Left = JuiceConfig.FloorConqueredPulseCount });
    }

    /// <summary>Emits each running show's next ring when its timer runs out, dropping the shows that are done.</summary>
    internal void Update(float deltaTime)
    {
        for (var i = _pulses.Count - 1; i >= 0; i--)
        {
            var pulse = _pulses[i];
            pulse.Timer -= deltaTime;
            if (pulse.Timer <= 0f)
            {
                _fire.PulseRing(pulse.Center, JuiceConfig.FloorConqueredMoteCount, JuiceConfig.FloorConqueredMoteSpeed,
                    JuiceConfig.FloorConqueredMoteLifetime, JuiceConfig.FloorConqueredMoteSize, _random);
                pulse.Timer += JuiceConfig.FloorConqueredPulseIntervalSeconds;
                pulse.Left--;
            }
            if (pulse.Left <= 0) _pulses.RemoveAt(i);
            else _pulses[i] = pulse;
        }
    }
}
