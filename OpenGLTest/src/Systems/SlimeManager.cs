using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Owns the live slimes: ticks their hop AI and physics, shoves the ones a player walks into,
/// bills contact damage while they are airborne, rolls death loot, and spawns new ones on dry
/// forest surface in a ring around a player. Every rule runs against a list of
/// <see cref="IMobTarget"/>s (the local camera, or a server's connected players), each slime
/// chasing its nearest one. Main thread only.
/// </summary>
public sealed class SlimeManager
{
    private readonly EntityType _type;
    private readonly List<Slime> _slimes = [];
    private readonly List<byte> _loot = [];
    // The camera overload's one-element target list, reused so it allocates nothing per frame.
    private readonly List<IMobTarget> _singleTarget = new(1);
    private float _spawnTimer;
    private int _spawnCursor;

    public SlimeManager(EntityType type) => _type = type;

    public IReadOnlyList<Slime> Slimes => _slimes;

    /// <summary>Removes a slime the server no longer reports (a client's network reconcile).</summary>
    public bool Remove(Slime slime) => _slimes.Remove(slime);

    /// <summary>Health the slimes took off every target together during the most recent
    /// <c>Update</c>; each hit also reached its target through <see cref="IMobTarget.TakeHit"/>.
    /// The singleplayer caller applies it to its <see cref="PlayerHealth"/> and shakes.</summary>
    public int DamageDealtThisFrame { get; private set; }

    /// <summary>Adds a slime at an exact body-centre position (used by spawns and tests).</summary>
    public Slime Spawn(Vector3 position)
    {
        var slime = new Slime(_type, position);
        _slimes.Add(slime);
        return slime;
    }

    /// <summary>Ticks the slimes against the one local player (singleplayer and the tests); see
    /// <see cref="Update(float, ChunkManager, DropManager, IReadOnlyList{IMobTarget})"/>.</summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops, Camera camera)
    {
        if (camera == null)
        {
            DamageDealtThisFrame = 0;
            return;
        }

        _singleTarget.Clear();
        _singleTarget.Add(camera);
        Update(deltaTime, chunks, drops, _singleTarget);
    }

    /// <summary>
    /// Ticks every slime against <paramref name="targets"/>: each hops at its nearest target
    /// (<see cref="MobTargets.Nearest"/>), an airborne touch hurts whichever non-immune target it
    /// touches (<see cref="IMobTarget.TakeHit"/>, also summed into <see cref="DamageDealtThisFrame"/>),
    /// a grounded touch is shoved off by that target, a slime past the despawn radius of every
    /// target is culled, and the spawn ring cycles over the targets under the one shared cap and
    /// timer. With no targets nothing chases, despawns or spawns.
    /// </summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops, IReadOnlyList<IMobTarget> targets)
    {
        DamageDealtThisFrame = 0;
        if (chunks == null || targets == null) return;

        var despawnSquared = WorldGenerationConfig.SlimeDespawnRadiusBlocks
            * WorldGenerationConfig.SlimeDespawnRadiusBlocks;

        for (var i = _slimes.Count - 1; i >= 0; i--)
        {
            var slime = _slimes[i];
            var chased = MobTargets.Nearest(targets, slime.Position);
            slime.Update(deltaTime, chunks, chased >= 0 ? targets[chased].Eye : null);

            if (slime.IsDead)
            {
                DropLoot(slime, drops);
                _slimes.RemoveAt(i);
                continue;
            }

            for (var t = 0; t < targets.Count; t++)
            {
                var target = targets[t];
                var eye = target.Eye;
                if (!MobTargets.BodyTouches(eye, slime.Position, Slime.HalfExtent)) continue;

                if (!target.IsImmune && slime.CanDealContactDamage)
                {
                    slime.NotifyContactDamage();
                    var damage = WorldGenerationConfig.SlimeContactDamage;
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

        UpdateSpawning(deltaTime, chunks, targets);
    }

    private void DropLoot(Slime slime, DropManager drops)
    {
        if (_type.LootTable == null) return;

        _loot.Clear();
        var (lootX, lootY, lootZ) = slime.Position.BlockCell();
        _type.LootTable.Roll(new LootContext(lootX, lootY, lootZ, 0), _loot);

        foreach (var id in _loot)
            drops.SpawnAt(id, slime.Position);
    }

    // One shared timer and cap; each expiry tries the ring round the next target in turn.
    private void UpdateSpawning(float deltaTime, ChunkManager chunks, IReadOnlyList<IMobTarget> targets)
    {
        if (_slimes.Count >= WorldGenerationConfig.SlimeMaxAlive) return;

        _spawnTimer += deltaTime;
        if (_spawnTimer < WorldGenerationConfig.SlimeSpawnIntervalSeconds) return;
        _spawnTimer = 0f;

        if (targets.Count == 0) return;
        var index = _spawnCursor % targets.Count;
        _spawnCursor = index + 1;

        if (TryFindSpawn(chunks, targets[index].Eye, out var position))
            Spawn(position);
    }

    /// <summary>The slime's spawn ring through <see cref="MobSpawner.TryFindSurfaceSpawn"/>,
    /// returning the body centre resting on the found ground cell.</summary>
    internal static bool TryFindSpawn(ChunkManager chunks, Vector3 playerPosition, out Vector3 position)
    {
        if (!MobSpawner.TryFindSurfaceSpawn(chunks, playerPosition,
                WorldGenerationConfig.SlimeSpawnMinRadiusBlocks, WorldGenerationConfig.SlimeSpawnMaxRadiusBlocks,
                WorldGenerationConfig.SlimeSpawnTries, out var x, out var groundY, out var z))
        {
            position = default;
            return false;
        }

        position = new Vector3(x + 0.5f, groundY + 1 + Slime.HalfExtent, z + 0.5f);
        return true;
    }
}
