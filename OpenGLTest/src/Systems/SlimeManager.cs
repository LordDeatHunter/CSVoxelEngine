using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Owns the live slimes: ticks their hop AI and physics, shoves the ones the player walks into,
/// bills contact damage while they are airborne, rolls death loot, and spawns new ones on dry
/// forest surface in a ring around the player. Main thread only.
/// </summary>
public sealed class SlimeManager
{
    private readonly EntityType _type;
    private readonly List<Slime> _slimes = [];
    private readonly List<byte> _loot = [];
    private float _spawnTimer;

    public SlimeManager(EntityType type) => _type = type;

    public IReadOnlyList<Slime> Slimes => _slimes;

    /// <summary>Health the slimes took off the player during the most recent
    /// <see cref="Update"/>; the caller applies it to <see cref="PlayerHealth"/> and shakes.</summary>
    public int DamageDealtThisFrame { get; private set; }

    /// <summary>Adds a slime at an exact body-centre position (used by spawns and tests).</summary>
    public Slime Spawn(Vector3 position)
    {
        var slime = new Slime(_type, position);
        _slimes.Add(slime);
        return slime;
    }

    public void Update(float deltaTime, ChunkManager chunks, DropManager drops, Camera camera)
    {
        DamageDealtThisFrame = 0;
        if (chunks == null || camera == null) return;

        var playerPosition = camera.Position;
        var immune = camera.IsCreative || camera.IsFlying;
        var despawnSquared = WorldGenerationConfig.SlimeDespawnRadiusBlocks
            * WorldGenerationConfig.SlimeDespawnRadiusBlocks;

        for (var i = _slimes.Count - 1; i >= 0; i--)
        {
            var slime = _slimes[i];
            slime.Update(deltaTime, chunks, playerPosition);

            if (slime.IsDead)
            {
                DropLoot(slime, drops);
                _slimes.RemoveAt(i);
                continue;
            }

            if (camera.BodyTouches(slime.Position, Slime.HalfExtent))
            {
                if (!immune && slime.CanDealContactDamage)
                {
                    slime.NotifyContactDamage();
                    DamageDealtThisFrame += WorldGenerationConfig.SlimeContactDamage;
                    var away = new Vector3(
                        playerPosition.X - slime.Position.X, 0f, playerPosition.Z - slime.Position.Z);
                    camera.AddKnockback(away, WorldGenerationConfig.SlimeKnockbackSpeed,
                        WorldGenerationConfig.SlimeKnockbackLift);
                }
                else if (slime.IsGrounded)
                {
                    // Grounded contact is harmless: the player shoves the slime off instead.
                    var away = new Vector3(
                        slime.Position.X - playerPosition.X, 0f, slime.Position.Z - playerPosition.Z);
                    slime.Shove(away, WorldGenerationConfig.SlimeShoveSpeed);
                }
            }

            var dx = slime.Position.X - playerPosition.X;
            var dz = slime.Position.Z - playerPosition.Z;
            if (dx * dx + dz * dz > despawnSquared)
                _slimes.RemoveAt(i);
        }

        UpdateSpawning(deltaTime, chunks, playerPosition);
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

    private void UpdateSpawning(float deltaTime, ChunkManager chunks, Vector3 playerPosition)
    {
        if (_slimes.Count >= WorldGenerationConfig.SlimeMaxAlive) return;

        _spawnTimer += deltaTime;
        if (_spawnTimer < WorldGenerationConfig.SlimeSpawnIntervalSeconds) return;
        _spawnTimer = 0f;

        if (TryFindSpawn(chunks, playerPosition, out var position))
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
