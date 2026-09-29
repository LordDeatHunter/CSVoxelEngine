using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Owns the live goblins and the stones they throw: ticks their AI and physics, spawns a stone
/// at each throw's release event, bills the club swing at each melee impact that still finds the
/// player in reach, shoves grounded goblins the player walks into, rolls death loot plus the
/// stones a goblin still carried, and spawns new ones on dry forest surface in a ring around the
/// player. A stone that misses shatters: it never becomes a pickup. Main thread only.
/// </summary>
public sealed class GoblinManager
{
    private readonly EntityType _type;
    private readonly List<Goblin> _goblins = [];
    private readonly List<ThrownProjectile> _stones = [];
    private readonly List<byte> _loot = [];
    private readonly byte _flintId;
    private float _spawnTimer;

    /// <summary>Validates the goblin content first, so a model missing a part, clip or event
    /// fails at startup naming its file.</summary>
    public GoblinManager(EntityType type)
    {
        Goblin.ValidateContent(type);
        _type = type;
        _flintId = ContentRegistry.RequireItem("flint").Id;
    }

    public IReadOnlyList<Goblin> Goblins => _goblins;

    /// <summary>Stones in flight; drawn and mirrored like the player's own throws.</summary>
    public IReadOnlyList<ThrownProjectile> Stones => _stones;

    /// <summary>Health the goblins took off the player during the most recent
    /// <see cref="Update"/>; the caller applies it to <see cref="PlayerHealth"/> and shakes.</summary>
    public int DamageDealtThisFrame { get; private set; }

    /// <summary>Adds a goblin at an exact body-centre position (used by spawns, scenarios and
    /// tests). A null <paramref name="ammo"/> rolls the stone count it carries.</summary>
    public Goblin Spawn(Vector3 position, int? ammo = null)
    {
        var stones = ammo ?? Random.Shared.Next(WorldGenerationConfig.GoblinStoneMin,
            WorldGenerationConfig.GoblinStoneMax + 1);
        var goblin = new Goblin(_type, position, stones);
        _goblins.Add(goblin);
        return goblin;
    }

    public void Update(float deltaTime, ChunkManager chunks, DropManager drops, Camera camera)
    {
        DamageDealtThisFrame = 0;
        if (chunks == null || camera == null) return;

        var playerPosition = camera.Position;
        var immune = camera.IsCreative || camera.IsFlying;
        var despawnSquared = WorldGenerationConfig.GoblinDespawnRadiusBlocks
            * WorldGenerationConfig.GoblinDespawnRadiusBlocks;

        for (var i = _goblins.Count - 1; i >= 0; i--)
        {
            var goblin = _goblins[i];
            goblin.Update(deltaTime, chunks, camera);

            if (goblin.IsDead)
            {
                DropLoot(goblin, drops);
                _goblins.RemoveAt(i);
                continue;
            }

            if (goblin.TryTakeThrowRelease(out var origin, out var velocity))
                _stones.Add(new ThrownProjectile(_flintId, origin, velocity, 0f, 1f));

            // The swing is decided at its impact frame, so a player who backed off during the
            // wind-up dodges it.
            if (goblin.TryTakeMeleeImpact() && !immune && Goblin.InMeleeReach(goblin.Position, playerPosition))
            {
                DamageDealtThisFrame += WorldGenerationConfig.GoblinMeleeDamage;
                var away = new Vector3(
                    playerPosition.X - goblin.Position.X, 0f, playerPosition.Z - goblin.Position.Z);
                camera.AddKnockback(away, WorldGenerationConfig.GoblinKnockbackSpeed,
                    WorldGenerationConfig.GoblinKnockbackLift);
            }

            if (goblin.IsGrounded && camera.BodyTouches(goblin.Position, Goblin.HalfExtent))
            {
                // Walking into a goblin is harmless: the player shoves it aside.
                var away = new Vector3(
                    goblin.Position.X - playerPosition.X, 0f, goblin.Position.Z - playerPosition.Z);
                goblin.Shove(away, WorldGenerationConfig.GoblinShoveSpeed);
            }

            var dx = goblin.Position.X - playerPosition.X;
            var dz = goblin.Position.Z - playerPosition.Z;
            if (dx * dx + dz * dz > despawnSquared)
                _goblins.RemoveAt(i);
        }

        UpdateStones(deltaTime, chunks, camera, immune);
        UpdateSpawning(deltaTime, chunks, playerPosition);
    }

    private void UpdateStones(float deltaTime, ChunkManager chunks, Camera camera, bool immune)
    {
        for (var i = _stones.Count - 1; i >= 0; i--)
        {
            var stone = _stones[i];
            // Stones pass through mobs: goblins never hit each other or a slime.
            var alive = stone.Update(deltaTime, chunks, null, out _);

            if (camera.BodyTouches(stone.Position, ThrownProjectile.HalfExtent))
            {
                if (!immune)
                {
                    DamageDealtThisFrame += WorldGenerationConfig.GoblinStoneDamage;
                    var playerPosition = camera.Position;
                    var away = new Vector3(
                        playerPosition.X - stone.Position.X, 0f, playerPosition.Z - stone.Position.Z);
                    camera.AddKnockback(away, WorldGenerationConfig.GoblinStoneKnockbackSpeed, 0f);
                }
                _stones.RemoveAt(i);
            }
            else if (!alive)
            {
                // A stone that hit the ground, a wall or its lifetime shatters: no drop, so
                // missed throws never pile up as pickups (the carried stones drop on death).
                _stones.RemoveAt(i);
            }
        }
    }

    private void DropLoot(Goblin goblin, DropManager drops)
    {
        if (_type.LootTable != null)
        {
            _loot.Clear();
            var (lootX, lootY, lootZ) = goblin.Position.BlockCell();
            _type.LootTable.Roll(new LootContext(lootX, lootY, lootZ, 0), _loot);

            foreach (var id in _loot)
                drops.SpawnAt(id, goblin.Position);
        }

        // The stones it never threw.
        for (var i = 0; i < goblin.Ammo; i++)
            drops.SpawnAt(_flintId, goblin.Position);
    }

    private void UpdateSpawning(float deltaTime, ChunkManager chunks, Vector3 playerPosition)
    {
        if (_goblins.Count >= WorldGenerationConfig.GoblinMaxAlive) return;

        _spawnTimer += deltaTime;
        if (_spawnTimer < WorldGenerationConfig.GoblinSpawnIntervalSeconds) return;
        _spawnTimer = 0f;

        if (TryFindSpawn(chunks, playerPosition, out var position))
            Spawn(position);
    }

    /// <summary>The goblin's spawn ring through <see cref="MobSpawner.TryFindSurfaceSpawn"/>,
    /// returning the body centre standing on the found ground cell.</summary>
    internal static bool TryFindSpawn(ChunkManager chunks, Vector3 playerPosition, out Vector3 position)
    {
        if (!MobSpawner.TryFindSurfaceSpawn(chunks, playerPosition,
                WorldGenerationConfig.GoblinSpawnMinRadiusBlocks, WorldGenerationConfig.GoblinSpawnMaxRadiusBlocks,
                WorldGenerationConfig.GoblinSpawnTries, out var x, out var groundY, out var z))
        {
            position = default;
            return false;
        }

        position = new Vector3(x + 0.5f, groundY + 1 + Goblin.Height * 0.5f, z + 0.5f);
        return true;
    }
}
