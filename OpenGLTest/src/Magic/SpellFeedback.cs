using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Spells;
using VoxelEngine.Networking.Client;
using VoxelEngine.Particles;

namespace VoxelEngine.Magic;

/// <summary>
/// The cosmetic side of a cast, a <see cref="World"/> component built only where frames are drawn
/// (singleplayer and a client, never a headless server); main thread only. <see cref="OnCast"/> is the
/// <see cref="SpellCaster.Feedback"/> hook: for the local player's own cast it plays the cast (or fizzle)
/// sound and bursts sparks in the element's color at the staff, and on a client it launches a cosmetic
/// preview of a projectile (<see cref="SpellBoltPreview"/>), since the server sends no bolt.
/// <see cref="Update"/> (a client's frame) flies those previews and bursts default-colored sparks where a
/// remote player cast (its element is not on the wire). Nothing here touches gameplay.
/// </summary>
internal sealed class SpellFeedback
{
    private readonly World _world;
    private readonly ChunkManager _chunks;
    private readonly BlockDebrisSystem _debris;
    private readonly SfxManager? _sfx;
    private readonly bool _clientMode;
    private readonly SpellBoltPreview _preview = new();
    private readonly List<Vector3> _landings = [];

    internal SpellFeedback(World world, ChunkManager chunks, BlockDebrisSystem debris, SfxManager? sfx, bool clientMode)
    {
        _world = world;
        _chunks = chunks;
        _debris = debris;
        _sfx = sfx;
        _clientMode = clientMode;
    }

    /// <summary>A client's cosmetic preview bolts in flight, oldest first (empty in singleplayer).</summary>
    internal IReadOnlyList<SpellProjectile> PreviewBolts => _preview.Bolts;

    /// <summary>
    /// The feedback of one accepted cast, <paramref name="fizzled"/> or not; only the local player's own
    /// casts (a context with a camera) show anything.
    /// </summary>
    internal void OnCast(SpellCast cast, bool fizzled)
    {
        if (cast.Caster.Camera == null) return;
        _sfx?.Play(fizzled ? SfxLibrary.SpellFizzleGroup : SfxLibrary.SpellCastGroup);

        var element = SpellImpactRules.ElementOf(cast.Parts);
        var color = fizzled ? SpellConfig.FizzleSparkColor
            : element != null ? SpellSparks.ColorOf(element)
            : SpellConfig.BoltDefaultColor;
        SpellSparks.Burst(_debris, SpellSparks.BurstOrigin(cast.Eye, cast.Direction), color);

        // Singleplayer flies the real bolt (SpellEffects); a client predicts one the server never sends
        if (!_clientMode || fizzled || element == null) return;
        if (SpellImpactRules.ShapeOf(cast.Parts) is not { Delivery: SpellDelivery.Projectile }) return;
        _preview.Launch(cast.Caster, element, cast.Stats, cast.Eye, cast.Direction, color);
    }

    /// <summary>
    /// Flies the preview bolts (the impact sound where one lands near the camera, at most once an update)
    /// and bursts sparks where a remote player just cast.
    /// </summary>
    internal void Update(float deltaTime, IReadOnlyList<RemotePlayer> remotes)
    {
        if (!_world.IsPlaying) return;

        _landings.Clear();
        _preview.Update(deltaTime, _chunks, _world.Mobs, _debris, _landings);
        for (var i = 0; i < _landings.Count; i++)
        {
            if (Vector3.Distance(_landings[i], _world.Camera.Position) > SpellConfig.ImpactSoundRangeBlocks) continue;
            _sfx?.Play(SfxLibrary.SpellImpactGroup);
            break;
        }

        for (var i = 0; i < remotes.Count; i++)
        {
            var remote = remotes[i];
            if (!remote.TakeCast()) continue;
            var aim = Vector3Utils.FromYawPitch(remote.Yaw, remote.Pitch);
            SpellSparks.Burst(_debris, SpellSparks.BurstOrigin(remote.Eye, aim), SpellConfig.BoltDefaultColor);
        }
    }
}
