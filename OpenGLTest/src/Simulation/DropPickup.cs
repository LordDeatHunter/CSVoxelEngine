using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Chunks;
using VoxelEngine.Networking;
using VoxelEngine.Particles;
using VoxelEngine.Player;
using VoxelEngine.UI.Hud;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Simulation;

/// <summary>
/// The per-frame drop pickup: who may collect drops this tick (the local player, or every connected
/// player on a server, plus the goblins scavenging flint), the drop update itself, crediting what was
/// collected, the pickup sound and sparkle, and the "Inventory full" warning. Owned by
/// <see cref="World"/>, which runs <see cref="Update"/> inside its entity update; main thread only.
/// </summary>
internal sealed class DropPickup
{
    private readonly World _world;
    private readonly DropManager _dropManager;
    private readonly ChunkManager _chunkManager;
    private readonly GoblinManager? _goblins;
    private readonly FireParticleSystem _fire;
    private readonly SfxManager? _sfx;
    private readonly bool _serverMode;
    private readonly bool _clientMode;

    private Camera Camera => _world.Camera;
    private Inventory Inventory => _world.Inventory;
    private HudToast Toast => _world.Toast;
    private IReadOnlyList<ServerPlayer> ServerPlayers => _world.ServerPlayers;

    internal DropPickup(World world, DropManager dropManager, ChunkManager chunkManager, GoblinManager? goblins,
        FireParticleSystem fire, SfxManager? sfx, bool serverMode, bool clientMode)
    {
        _world = world;
        _dropManager = dropManager;
        _chunkManager = chunkManager;
        _goblins = goblins;
        _fire = fire;
        _sfx = sfx;
        _serverMode = serverMode;
        _clientMode = clientMode;
        _canAcceptDrop = Inventory.CanAccept;
    }

    // Inventory is assigned in its own initializer and never replaced, so the method group is the
    // same closed delegate every frame; passing `Inventory.CanAccept` inline at the DropManager call
    // allocated a fresh one per frame (PERF_TODO C7).
    private readonly Func<byte, bool> _canAcceptDrop;

    // Scratch for the server's per-player pickup, rebuilt each frame from ServerPlayers
    private readonly List<DropCollector> _dropCollectors = [];

    // Whether a drop in reach was refused last frame, so the "Inventory full" warning fires on
    // the transition into the blocked state only
    private bool _pickupBlocked;

    /// <summary>Runs this tick's drop pickup: gathers the collectors, updates the drops and credits what they collected.</summary>
    internal void Update(double deltaTime)
    {
        // Drops collected this tick land in the inventory pools. A full pool ignores the
        // drop (it is never attracted), and an id with no pool is discarded. The collectors are
        // the local player (singleplayer and a client), or every connected player on a server
        // (which has no player of its own), plus every goblin with ammo room, which scavenges
        // flint off the ground as its own ammo.
        _dropCollectors.Clear();
        if (_serverMode)
        {
            var players = ServerPlayers;
            foreach (var serverPlayer in players)
                _dropCollectors.Add(new DropCollector(serverPlayer.Position, serverPlayer.CanAcceptDrop));
        }
        else
        {
            _dropCollectors.Add(new DropCollector(Camera.Position, _canAcceptDrop));
        }

        // A client's goblins are mirrors whose ammo is the server's (EntityList): never scavenge
        var goblinCollectors = _dropCollectors.Count;
        if (!_clientMode)
            _goblins?.AppendCollectors(_dropCollectors);
        _dropManager.Update((float)deltaTime, _dropCollectors, _chunkManager);

        var pickedUp = false;
        var collected = _dropManager.Collected;
        var collectors = _dropManager.CollectedBy;
        for (var i = 0; i < collected.Length; i++)
        {
            if (collectors[i] >= goblinCollectors)
            {
                _goblins!.AwardScavenged(collectors[i] - goblinCollectors, collected[i]);
                continue;
            }

            if (_serverMode)
            {
                var player = ServerPlayers[collectors[i]];
                if (player.Inventory.Add(collected[i]))
                    player.InventoryChanged = true;
                continue;
            }

            var known = Inventory.Knows(collected[i]);
            pickedUp |= Inventory.Add(collected[i]);
            // A first find toasts its name: Add marks the id obtained, so read it before
            if (!known)
                Toast.Show($"New: {Inventory.DisplayName(collected[i])}");
        }

        if (pickedUp)
        {
            _sfx?.Play(SfxLibrary.PickupGroup);
            // Bright motes pop where the drop was collected, at the chest
            _fire.Puff(Camera.Position - new Vector3(0f, WorldGenerationConfig.PickupSparkleDrop, 0f),
                WorldGenerationConfig.PickupSparkleCount, WorldGenerationConfig.PickupSparkleSpread,
                FireParticleKind.Spark);
        }

        // A drop in reach the pool refuses warns "Inventory full" once, and stays silent until
        // there is room again (the transition, not the state)
        var blocked = !_serverMode && _dropManager.PickupBlocked;
        if (blocked && !_pickupBlocked)
            Toast.Show("Inventory full");
        _pickupBlocked = blocked;
    }
}
