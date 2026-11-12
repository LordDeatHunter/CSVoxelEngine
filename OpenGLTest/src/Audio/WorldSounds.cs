using System.Collections.Generic;
using System.Numerics;

namespace VoxelEngine.Audio;

/// <summary>
/// A sound a world event made: its group, where it happened, and the player (a server's player id) who already
/// heard it through their own prediction and is left out, 0 for nobody.
/// </summary>
public readonly record struct SoundEvent(string Group, Vector3 Position, int ExceptPlayerId = 0);

/// <summary>
/// Where every <see cref="World"/> component sends its sounds. Singleplayer and a client play them
/// (<see cref="PlayAt"/> positionally through <see cref="SfxManager.PlayAt"/>, <see cref="Play"/> centered for
/// the local player's own actions); a headless server plays nothing and instead queues each positional sound as a
/// <see cref="SoundEvent"/> that its broadcast sends to the players within earshot, so a slime's hop or a mob's
/// death is heard by everyone near it. Main thread only.
/// </summary>
public sealed class WorldSounds
{
    private readonly SfxManager? _sfx;
    private readonly List<SoundEvent>? _pending;

    /// <summary>A server-mode instance (<paramref name="serverMode"/>) queues; any other plays through
    /// <paramref name="sfx"/> (null for silence: tests, scenario worlds without audio).</summary>
    public WorldSounds(SfxManager? sfx, bool serverMode)
    {
        _sfx = serverMode ? null : sfx;
        _pending = serverMode ? [] : null;
    }

    /// <summary>The sounds queued since the last <see cref="ClearPending"/> (always empty off a server).</summary>
    public IReadOnlyList<SoundEvent> Pending => (IReadOnlyList<SoundEvent>?)_pending ?? [];

    /// <summary>
    /// A sound at <paramref name="position"/> in the world: heard there by the local listener, or queued for
    /// the players near it on a server, leaving out the player <paramref name="exceptPlayerId"/> whose own
    /// client already played it. A server holds at most <see cref="NetworkConfig.NetSoundEventsPerBroadcast"/>
    /// between broadcasts and drops the rest.
    /// </summary>
    public void PlayAt(string group, Vector3 position, int exceptPlayerId = 0)
    {
        if (_pending != null)
        {
            if (_pending.Count < NetworkConfig.NetSoundEventsPerBroadcast)
                _pending.Add(new SoundEvent(group, position, exceptPlayerId));
            return;
        }
        _sfx?.PlayAt(group, position);
    }

    /// <summary>The local player's own sound (a swing, a pickup), centered at full volume; nothing on a server,
    /// whose players play their own.</summary>
    public void Play(string group) => _sfx?.Play(group);

    /// <summary>Forgets the queued sounds once the server has sent them.</summary>
    public void ClearPending() => _pending?.Clear();
}
