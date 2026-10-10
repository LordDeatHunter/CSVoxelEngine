using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Owns the live slimes: ticks their hop AI and physics, shoves the ones a player walks into,
/// bills contact damage while they are airborne, rolls death loot, and spawns new ones on dry
/// forest surface in a ring around a player. Every rule runs against a list of
/// <see cref="IMobTarget"/>s (the local camera, or a server's connected players), each slime
/// chasing its nearest one. Every spawn is judged by the global <see cref="MobCaps"/> it shares
/// with the other mob kinds. Main thread only.
/// </summary>
public sealed class SlimeManager : IMobPopulation
{
    private readonly EntityType _type;
    private readonly MobCaps _caps;
    private readonly List<Slime> _slimes = [];
    private readonly List<byte> _loot = [];
    // The camera overload's one-element target list, reused so it allocates nothing per frame.
    private readonly List<IMobTarget> _singleTarget = new(1);
    private float _spawnTimer;
    private int _spawnCursor;

    /// <summary>Joins <paramref name="caps"/> (the world's, shared with every other mob kind), or
    /// caps itself alone when none is given (tests, previews).</summary>
    public SlimeManager(EntityType type, MobCaps? caps = null)
    {
        _type = type;
        _caps = caps ?? new MobCaps();
        _caps.Register(this);
    }

    /// <summary>Every slime still drawn: the living ones plus any playing their death
    /// (<see cref="Mob.IsDying"/>), which are no longer targets and do not count toward the cap.</summary>
    public IReadOnlyList<Slime> Slimes => _slimes;

    /// <summary>Slimes that are alive (not yet dying), what the global caps count.</summary>
    public int LivingCount
    {
        get
        {
            var count = 0;
            for (var i = 0; i < _slimes.Count; i++)
                if (!_slimes[i].IsDying) count++;
            return count;
        }
    }

    /// <summary>A client's network reconcile: the server no longer reports this slime, so it plays
    /// its death where it stands and is then dropped.</summary>
    public bool Remove(Slime slime)
    {
        if (!_slimes.Contains(slime)) return false;
        slime.BeginDying();
        return true;
    }

    /// <summary>Advances the death and spawn clocks of the slimes without simulating them (a
    /// client never ticks the AI), dropping the ones whose death is over.</summary>
    public void AnimateMirrors(float deltaTime)
    {
        for (var i = _slimes.Count - 1; i >= 0; i--)
        {
            _slimes[i].AnimateMirror(deltaTime);
            if (_slimes[i].IsRemovable) _slimes.RemoveAt(i);
        }
    }

    /// <summary>Health the slimes took off every target together during the most recent
    /// <c>Update</c>; each hit also reached its target through <see cref="IMobTarget.TakeHit"/>.
    /// The singleplayer caller applies it to its <see cref="PlayerHealth"/> and shakes.</summary>
    public int DamageDealtThisFrame { get; private set; }

    /// <summary>
    /// Adds a slime at an exact body-centre position: a forced spawn (scenarios, save restores, a
    /// client's mirror, tests), which may go past the natural cap but never the hard cap. Null, and
    /// nothing added, once the hard cap is full. The ring spawner adds through here too, after its
    /// own natural-cap check. <paramref name="size"/> is clamped into the slime sizes.
    /// </summary>
    public Slime? Spawn(Vector3 position, int size = SlimeSize.Min)
    {
        if (_caps.ForcedRoom <= 0) return null;
        var slime = new Slime(_type, position, size);
        _slimes.Add(slime);
        return slime;
    }

    /// <summary>Ticks the slimes against the one local player (singleplayer and the tests); see
    /// <see cref="Update(float, ChunkManager, DropManager, IReadOnlyList{IMobTarget}, float)"/>.</summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops, Camera camera, float night = 0f)
    {
        if (camera == null)
        {
            DamageDealtThisFrame = 0;
            return;
        }

        _singleTarget.Clear();
        _singleTarget.Add(camera);
        Update(deltaTime, chunks, drops, _singleTarget, night);
    }

    /// <summary>
    /// Ticks every slime against <paramref name="targets"/>: each hops at its nearest target
    /// (<see cref="MobTargets.Nearest"/>), an airborne touch hurts whichever non-immune target it
    /// touches (<see cref="IMobTarget.TakeHit"/>, also summed into <see cref="DamageDealtThisFrame"/>),
    /// a grounded touch is shoved off by that target, a slime past the despawn radius of every
    /// target is culled, and the spawn ring cycles over the targets under the one shared cap and
    /// timer. With no targets nothing chases, despawns or spawns. <paramref name="night"/> is the
    /// lighting clock's <see cref="DayCycle.NightFactor"/>, which the ring spawn's size roll reads.
    /// </summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops, IReadOnlyList<IMobTarget> targets,
        float night = 0f)
    {
        DamageDealtThisFrame = 0;
        if (chunks == null || targets == null) return;

        var despawnSquared = WorldGenerationConfig.SlimeDespawnRadiusBlocks
            * WorldGenerationConfig.SlimeDespawnRadiusBlocks;

        for (var i = _slimes.Count - 1; i >= 0; i--)
        {
            var slime = _slimes[i];
            var chased = MobTargets.Nearest(targets, slime.Position);
            slime.Update(deltaTime, chunks, chased >= 0 && !slime.IsDying ? targets[chased].Eye : null);

            if (slime.IsDying)
            {
                if (slime.IsRemovable) _slimes.RemoveAt(i);
                continue;
            }

            if (slime.IsDead)
            {
                DropLoot(slime, drops);
                slime.BeginDying();
                continue;
            }

            for (var t = 0; t < targets.Count; t++)
            {
                var target = targets[t];
                var eye = target.Eye;
                if (!MobTargets.BodyTouches(eye, slime.Position, slime.BodyHalfExtent)) continue;

                if (!target.IsImmune && slime.CanDealContactDamage)
                {
                    slime.NotifyContactDamage();
                    var damage = slime.ContactDamage;
                    DamageDealtThisFrame += damage;
                    var away = new Vector3(eye.X - slime.Position.X, 0f, eye.Z - slime.Position.Z);
                    target.TakeHit(damage, away, WorldGenerationConfig.SlimeKnockbackSpeed,
                        WorldGenerationConfig.SlimeKnockbackLift);
                }
                else if (slime.IsGrounded)
                {
                    // Grounded contact is harmless: the player shoves the slime off instead.
                    var away = new Vector3(slime.Position.X - eye.X, 0f, slime.Position.Z - eye.Z);
                    slime.Shove(away, WorldGenerationConfig.SlimeShoveSpeed);
                }
            }

            if (targets.Count > 0 && MobTargets.MinHorizontalDistanceSquared(targets, slime.Position) > despawnSquared)
                _slimes.RemoveAt(i);
        }

        UpdateSpawning(deltaTime, chunks, targets, night);
    }

    // A larger slime rolls its kind's loot table once per LootRolls, so its yield grows with it.
    private void DropLoot(Slime slime, DropManager drops)
    {
        if (_type.LootTable == null) return;

        _loot.Clear();
        var (lootX, lootY, lootZ) = slime.Position.BlockCell();
        var context = new LootContext(lootX, lootY, lootZ, 0);
        for (var roll = 0; roll < slime.LootRolls; roll++)
            _type.LootTable.Roll(context, _loot);

        foreach (var id in _loot)
            drops.SpawnAt(id, slime.Position);
    }

    // One shared timer, under the natural cap every mob kind shares; each expiry tries the ring
    // round the next target in turn.
    private void UpdateSpawning(float deltaTime, ChunkManager chunks, IReadOnlyList<IMobTarget> targets, float night)
    {
        if (!_caps.AllowsNaturalSpawn) return;

        _spawnTimer += deltaTime;
        if (_spawnTimer < WorldGenerationConfig.SlimeSpawnIntervalSeconds) return;
        _spawnTimer = 0f;

        if (targets.Count == 0) return;
        var index = _spawnCursor % targets.Count;
        _spawnCursor = index + 1;

        if (TryFindSpawn(chunks, targets[index].Eye, night, Random.Shared.NextSingle(), out var position, out var size))
            Spawn(position, size);
    }

    /// <summary>The smallest slime's spawn ring (a size roll that never grows); see
    /// <see cref="TryFindSpawn(ChunkManager, Vector3, float, float, out Vector3, out int)"/>.</summary>
    internal static bool TryFindSpawn(ChunkManager chunks, Vector3 playerPosition, out Vector3 position) =>
        TryFindSpawn(chunks, playerPosition, 0f, 1f, out position, out _);

    /// <summary>
    /// The slime's spawn ring through <see cref="MobSpawner.TryFindSurfaceSpawn"/>: the size comes
    /// from <see cref="SlimeSize.Roll"/> with <paramref name="night"/>, whether the ground cell is
    /// below its column's surface (<see cref="SlimeSize.IsUnderground"/>) and
    /// <paramref name="sizeRoll"/> (uniform in [0, 1)), and <paramref name="position"/> is that
    /// size's body centre resting on the found ground cell. The ring only cleared the two cells above
    /// one ground cell, so a larger body that would overlap a solid cell beside them steps down a
    /// size at a time until it fits; the smallest body sits inside that column and always does.
    /// </summary>
    internal static bool TryFindSpawn(ChunkManager chunks, Vector3 playerPosition, float night, float sizeRoll,
        out Vector3 position, out int size)
    {
        if (!MobSpawner.TryFindSurfaceSpawn(chunks, playerPosition,
                WorldGenerationConfig.SlimeSpawnMinRadiusBlocks, WorldGenerationConfig.SlimeSpawnMaxRadiusBlocks,
                WorldGenerationConfig.SlimeSpawnTries, out var x, out var groundY, out var z))
        {
            position = default;
            size = SlimeSize.Min;
            return false;
        }

        var column = chunks.WorldSource.GetColumn(VoxelCoords.WorldToChunk(x), VoxelCoords.WorldToChunk(z));
        var surface = column.Heights[VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(x), VoxelCoords.WorldToLocal(z))];
        size = SlimeSize.Roll(night, SlimeSize.IsUnderground(groundY, surface), sizeRoll);

        while (true)
        {
            var half = Slime.HalfExtentFor(size);
            position = new Vector3(x + 0.5f, groundY + 1 + half, z + 0.5f);
            if (size <= SlimeSize.Min || !Slime.IsColliding(position, half, chunks))
                return true;
            size--;
        }
    }
}
