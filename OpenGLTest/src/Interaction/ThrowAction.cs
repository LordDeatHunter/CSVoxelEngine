using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities;
using VoxelEngine.Items;
using VoxelEngine.Networking;
using VoxelEngine.Player;
using VoxelEngine.Simulation;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Interaction;

/// <summary>
/// Throwing: the Q-drop of the selected slot onto the ground, the local player's throw charge and
/// release, a server player's throw, and the flying thrown items (which strike a mob or land as a
/// drop) together with the per-frame projectile list the renderer and the mirror read. Owned by
/// <see cref="World"/>; main thread only.
/// </summary>
internal sealed class ThrowAction
{
    private readonly World _world;
    private readonly ChunkManager _chunkManager;
    private readonly DropManager _dropManager;
    private readonly GoblinManager? _goblins;
    private readonly List<Mob> _mobs;
    private readonly List<ThrownProjectile> _projectiles;
    private readonly bool _clientMode;

    private bool IsPlaying => _world.IsPlaying;
    private Camera Camera => _world.Camera;
    private PlayerModel Player => _world.Player;
    private Inventory Inventory => _world.Inventory;
    private MobStrikes Strikes => _world.Strikes;

    internal ThrowAction(World world, ChunkManager chunkManager, DropManager dropManager, GoblinManager? goblins,
        List<Mob> mobs, List<ThrownProjectile> projectiles, bool clientMode)
    {
        _world = world;
        _chunkManager = chunkManager;
        _dropManager = dropManager;
        _goblins = goblins;
        _mobs = mobs;
        _projectiles = projectiles;
        _clientMode = clientMode;
    }

    private readonly List<ThrownProjectile> _thrown = [];

    private float _throwChargeSeconds;
    private byte _throwItemId;
    private float _throwBaseDamage;

    internal bool IsThrowCharging { get; private set; }

    internal float ThrowCharge01 => MathUtils.Clamp01(_throwChargeSeconds / WorldGenerationConfig.ThrowChargeSeconds);

    /// <summary>Rebuilds the projectile list (<c>_projectiles</c>) from the player's throws and the
    /// goblins' stones, with indexed loops for the same reason as <see cref="World.RefreshMobs"/>.</summary>
    internal void RefreshProjectiles()
    {
        _projectiles.Clear();
        for (var i = 0; i < _thrown.Count; i++)
            _projectiles.Add(_thrown[i]);
        if (_goblins != null)
        {
            var stones = _goblins.Stones;
            for (var i = 0; i < stones.Count; i++)
                _projectiles.Add(stones[i]);
        }
    }

    /// <summary>
    /// Releases a throw of <paramref name="player"/>'s selected throwable at
    /// <paramref name="charge01"/> from the player's eye along its view, spending the unit unless
    /// the player is creative. False, spending nothing, when the selected slot holds nothing
    /// throwable.
    /// </summary>
    internal bool ThrowFor(PlayerContext player, float charge01)
    {
        if (!IsPlaying) return false;
        var inventory = player.Inventory;
        var id = inventory.SelectedBlockId;
        if (id == 0 || inventory.SelectedCount <= 0) return false;
        if (ItemRegistry.TryGet(id) is not { IsThrowable: true } item) return false;
        return TryLaunchThrow(inventory, id, item.ThrowDamage ?? 0f, MathUtils.Clamp01(charge01), player.Eye,
            player.ViewDirection, player.IsCreative);
    }

    /// <summary>
    /// Throws the selected slot onto the ground in front of the player: one unit, or the whole
    /// stack when <paramref name="wholeStack"/> is set. Works for blocks and pure items alike;
    /// does nothing when the slot is empty. Each unit spawns its own drop, so a thrown stack
    /// scatters (every drop rolls its own random pop) and picks back up one unit at a time.
    /// Does nothing unless the world is playing: nothing may edit an unloaded world.
    /// </summary>
    internal void ThrowSelected(bool wholeStack = false) =>
        ThrowSelected(wholeStack, Camera.Position, Camera.ViewDirection, Inventory);

    /// <summary>
    /// <see cref="ThrowSelected(bool)"/> out of <paramref name="inventory"/>, from an explicit
    /// <paramref name="eye"/> along <paramref name="forward"/> (a unit vector): what a server runs
    /// for a client's Q-drop, which must leave from that player and out of that player's own
    /// inventory, not from the server's camera. Returns whether anything was thrown.
    /// </summary>
    internal bool ThrowSelected(bool wholeStack, Vector3 eye, Vector3 forward, Inventory inventory)
    {
        if (!IsPlaying) return false;

        // A held tool is one instance: it leaves the pool and every hotbar slot holding it
        if (inventory.SelectedToolInstance is var instance and > 0)
        {
            var id = inventory.SelectedBlockId;
            if (id == 0) return false;
            if (!inventory.RemoveTool(instance)) return false;
            _dropManager.SpawnAt(id, eye + forward * 0.6f, forward * 4f + Vector3.UnitY * 1.5f);
            return true;
        }

        var stackId = inventory.SelectedBlockId;
        if (stackId == 0 || inventory.SelectedCount <= 0) return false;

        var units = wholeStack ? inventory.SelectedCount : 1;
        var thrown = false;
        for (var i = 0; i < units; i++)
        {
            // Spend first so a failed spend never duplicates the drop
            if (!inventory.ConsumeSelected()) break;

            _dropManager.SpawnAt(stackId, eye + forward * 0.6f, forward * 4f + Vector3.UnitY * 1.5f);
            thrown = true;
        }
        return thrown;
    }

    /// <summary>Starts charging a throw of the selected throwable. False unless the world is
    /// playing: nothing may edit an unloaded world.</summary>
    internal bool TryBeginThrowCharge()
    {
        if (!IsPlaying) return false;
        if (IsThrowCharging) return true;
        var id = Inventory.SelectedBlockId;
        if (id == 0 || Inventory.SelectedCount <= 0) return false;
        if (ItemRegistry.TryGet(id) is not { IsThrowable: true } item) return false;
        IsThrowCharging = true;
        _throwChargeSeconds = 0f;
        _throwItemId = id;
        _throwBaseDamage = item.ThrowDamage ?? 0f;
        Player.StartThrowCharge();
        return true;
    }

    /// <summary>Advances a held throw charge. Does nothing unless the world is playing: nothing
    /// may edit an unloaded world.</summary>
    internal void UpdateThrowCharge(float deltaTime)
    {
        if (!IsPlaying) return;
        if (!IsThrowCharging) return;
        _throwChargeSeconds = Math.Min(_throwChargeSeconds + Math.Max(0f, deltaTime), WorldGenerationConfig.ThrowChargeSeconds);
        if (Inventory.SelectedBlockId != _throwItemId || Inventory.SelectedCount <= 0)
        {
            IsThrowCharging = false;
            Player.CancelThrowCharge();
            return;
        }
        Player.UpdateThrowCharge(ThrowCharge01);
    }

    /// <summary>Cancels a held throw charge. Does nothing unless the world is playing: nothing
    /// may touch an unloaded world's player state.</summary>
    internal void CancelThrowCharge()
    {
        if (!IsPlaying) return;
        IsThrowCharging = false;
        Player.CancelThrowCharge();
    }

    /// <summary>Releases a charged throw. False unless the world is playing: nothing may edit an
    /// unloaded world. On a connected client the projectile is a cosmetic copy: the throw goes
    /// out as an intent and the server launches the one that deals damage and lands as a drop.</summary>
    internal bool ReleaseThrow()
    {
        if (!IsPlaying) return false;
        if (!IsThrowCharging) return false;
        IsThrowCharging = false;
        var charge01 = MathUtils.Clamp01(_throwChargeSeconds / WorldGenerationConfig.ThrowChargeSeconds);
        if (!TryLaunchThrow(Inventory, _throwItemId, _throwBaseDamage, charge01, Camera.Position,
                Camera.ViewDirection, Camera.IsCreative))
        {
            Player.CancelThrowCharge();
            return false;
        }
        _world.RaiseIntent(WorldIntent.Throw(Camera.Position, Camera.ViewDirection, charge01));
        Camera.NotifyPlayerAction();
        Player.ReleaseThrow(charge01);
        return true;
    }

    /// <summary>
    /// Launches one <paramref name="id"/> out of <paramref name="inventory"/>'s selected slot from
    /// <paramref name="eye"/> along <paramref name="forward"/> at <paramref name="charge01"/>,
    /// spending the unit unless <paramref name="creative"/>. False, spending nothing, when the slot
    /// no longer holds that item. The release half of a throw, apart from the local player's
    /// charge and arm pose.
    /// </summary>
    private bool TryLaunchThrow(Inventory inventory, byte id, float baseDamage, float charge01,
        Vector3 eye, Vector3 forward, bool creative)
    {
        if (id == 0 || inventory.SelectedBlockId != id || inventory.SelectedCount <= 0)
            return false;
        if (!creative && !inventory.ConsumeSelected())
            return false;
        var origin = eye + forward * 0.6f;
        var velocity = ThrownProjectile.LaunchVelocity(forward, charge01);
        _thrown.Add(new ThrownProjectile(id, origin, velocity, baseDamage, charge01));
        return true;
    }

    /// <summary>
    /// Flies every thrown item: one that hits a mob deals its damage and shoves it away from where
    /// it was thrown from, one that comes to rest becomes a drop. A client's throws are cosmetic
    /// copies of the server's, so they only fly and vanish.
    /// </summary>
    internal void UpdateThrown(float deltaTime)
    {
        if (_thrown.Count == 0) return;
        for (var i = _thrown.Count - 1; i >= 0; i--)
        {
            var projectile = _thrown[i];
            var alive = projectile.Update(deltaTime, _chunkManager, _mobs, out var hit);
            if (hit != null)
            {
                if (!_clientMode)
                    Strikes.StrikeMob(hit, projectile.Damage, projectile.Origin);
                else
                    // The cosmetic copy's connect sparks where it stops on the mirrored mob
                    Strikes.SpawnHitSparks(hit, projectile.Origin);
                _thrown.RemoveAt(i);
                continue;
            }

            if (alive) continue;
            if (!_clientMode)
                _dropManager.SpawnAt(projectile.ItemId, projectile.Position);
            _thrown.RemoveAt(i);
        }
    }
}
