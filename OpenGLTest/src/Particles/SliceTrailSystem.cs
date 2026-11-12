using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Items;
using VoxelEngine.Networking.Client;
using VoxelEngine.Player;

namespace VoxelEngine.Particles;

/// <summary>
/// The slice trails of every swinging blade in view: the local player's and one per remote player.
/// A cosmetic <see cref="World"/> component, GL-free and main thread only; World feeds it once per
/// update outside server mode, and <c>WorldRenderer</c> draws <see cref="Active"/>. Each trail is fed
/// the exact matrix its blade sprite is drawn with (<see cref="HeldItemPose.SpriteMatrix"/>), in its
/// player's owner frame (the eye plus <see cref="PlayerModel.DrawnBodyYaw"/>).
/// </summary>
public sealed class SliceTrailSystem
{
    // The blades a remote is assumed to hold while it swings: its held item is not on the wire
    private static Item? _assumedSlice;
    private static Item? _assumedSword;

    private readonly SliceTrail _local = new();
    private readonly Dictionary<int, SliceTrail> _remote = [];
    private readonly List<int> _stale = [];
    private readonly List<SliceTrail> _active = [];

    /// <summary>The trails with anything to draw, rebuilt in place on every <see cref="Update"/>.</summary>
    public IReadOnlyList<SliceTrail> Active => _active;

    /// <summary>
    /// The blade <paramref name="model"/> is assumed to swing: the first registered item with the
    /// matching attack pattern (<see cref="ItemRegistry.FindByAttack"/>) while it slices or swings a
    /// sword, null otherwise. A remote player's held item is not on the wire, so its trail and its drawn
    /// blade both come from this item.
    /// </summary>
    public static Item? AssumedBlade(PlayerModel model)
    {
        if (model.IsSlicing)
            return _assumedSlice ??= ItemRegistry.FindByAttack(AttackPattern.Slice);
        if (model.IsSwordSwinging)
            return _assumedSword ??= ItemRegistry.FindByAttack(AttackPattern.Sword);
        return null;
    }

    /// <summary>
    /// Feeds one update at world clock <paramref name="now"/>: the local trail from
    /// <paramref name="local"/> holding <paramref name="held"/> (null for an empty hand) at the given eye,
    /// look and first-person back offset, then one trail per remote in <paramref name="remotes"/>, whose
    /// departed players' trails are dropped.
    /// </summary>
    public void Update(double now, PlayerModel local, Item? held, Vector3 eye, float yawDeg, float pitchDeg,
        float backOffset, IReadOnlyList<RemotePlayer> remotes)
    {
        UpdateLocal(now, local, held, eye, yawDeg, pitchDeg, backOffset);
        for (var i = 0; i < remotes.Count; i++)
            UpdateRemote(now, remotes[i]);
        DropDeparted(remotes);
        RebuildActive();
    }

    private void UpdateLocal(double now, PlayerModel local, Item? held, Vector3 eye, float yawDeg, float pitchDeg,
        float backOffset)
    {
        var swinging = SliceTrail.IsBladeSwing(local) && held is { Attack: AttackPattern.Slice or AttackPattern.Sword };
        var blade = Matrix4x4.Identity;
        if (swinging)
        {
            _local.Color = held!.TrailColorOrDefault;
            blade = HeldItemPose.SpriteMatrix(local, held, eye, yawDeg, pitchDeg, backOffset);
        }
        var owner = SliceTrail.OwnerFrameAt(eye, local.DrawnBodyYaw(yawDeg));
        _local.Update(now, swinging, SliceTrail.SwingProgress(local), local.IsHitStopped, blade, owner);
    }

    // A remote draws no back offset (RenderRemotePlayers poses it at 0)
    private void UpdateRemote(double now, RemotePlayer remote)
    {
        if (!_remote.TryGetValue(remote.Id, out var trail))
        {
            trail = new SliceTrail();
            _remote[remote.Id] = trail;
        }

        var model = remote.Model;
        var item = AssumedBlade(model);
        var blade = Matrix4x4.Identity;
        if (item != null)
        {
            trail.Color = item.TrailColorOrDefault;
            blade = HeldItemPose.SpriteMatrix(model, item, remote.Eye, remote.Yaw, remote.Pitch, 0f);
        }
        var owner = SliceTrail.OwnerFrameAt(remote.Eye, model.DrawnBodyYaw(remote.Yaw));
        trail.Update(now, item != null, SliceTrail.SwingProgress(model), model.IsHitStopped, blade, owner);
    }

    // Forgets the trails of players no longer in the list, through a reused scratch list
    private void DropDeparted(IReadOnlyList<RemotePlayer> remotes)
    {
        if (_remote.Count == 0)
            return;
        _stale.Clear();
        foreach (var id in _remote.Keys)
            if (!Contains(remotes, id))
                _stale.Add(id);
        foreach (var id in _stale)
            _remote.Remove(id);
    }

    private static bool Contains(IReadOnlyList<RemotePlayer> remotes, int id)
    {
        for (var i = 0; i < remotes.Count; i++)
            if (remotes[i].Id == id)
                return true;
        return false;
    }

    private void RebuildActive()
    {
        _active.Clear();
        if (_local.Count > 0)
            _active.Add(_local);
        foreach (var trail in _remote.Values)
            if (trail.Count > 0)
                _active.Add(trail);
    }
}
