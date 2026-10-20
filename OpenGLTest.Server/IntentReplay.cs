using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Networking;
using VoxelEngine.Networking.ServerSide;

namespace VoxelEngine.Server;

/// <summary>Replays a player's intents through the singleplayer rules and answers each one in order: the authoritative cells around a world intent's target, the inventory when it could change or was refused, then the result. Also refuses the retired raw block edit. Main thread only.</summary>
internal sealed class IntentReplay
{
    private readonly ServerState _state;

    // Scratch for one correction's cells
    private readonly List<NetCellChange> _correctionScratch = [];

    internal IntentReplay(ServerState state)
    {
        _state = state;
    }

    internal void OnBlockEdit(ClientConnection client, PacketReader reader)
    {
        // Retired: a client never writes a cell. The edit is refused whatever it names, and
        // a cell in reach is answered with its authoritative state.
        var x = reader.ReadInt();
        var y = reader.ReadInt();
        var z = reader.ReadInt();
        reader.ReadByte();
        reader.ReadByte();
        Log.Warn($"net: refused a raw block edit from {client.Name} (#{client.Id})");
        if (_state.Players.TryGetValue(client.Id, out var player)
            && NetValidation.InEditReach(player.Position, x, y, z, NetworkConfig.NetMaxEditReachBlocks))
            SendCellCorrection(client, player.LastSeq, x, y, z, radius: 0);
    }

    internal void OnWorldIntent(ClientConnection client, PacketReader reader)
    {
        var intent = WorldIntentCodec.Read(reader, out var seq);
        if (_state.Players.TryGetValue(client.Id, out var player))
            HandleWorldIntent(client, player, seq, intent);
    }

    internal void OnInventoryRequest(ClientConnection client, PacketReader reader)
    {
        var seq = reader.ReadInt();
        var intent = InventoryIntentCodec.Read(reader);
        if (!_state.Players.TryGetValue(client.Id, out var player))
            return;
        // Replayed against this player's own inventory only, and only when it is newer than
        // everything the player sent before. A Q-drop leaves from the player's eye along
        // their look, never from the server's own camera (which stands at the world spawn).
        var accepted = player.AcceptSeq(seq);
        if (accepted)
        {
            switch (intent.Action)
            {
                case NetInventoryAction.DropSelected:
                case NetInventoryAction.DropAllSelected:
                    accepted = _state.World.ThrowSelected(intent.Action == NetInventoryAction.DropAllSelected,
                        player.Position, player.Forward, player.Inventory);
                    break;
                default:
                    accepted = InventoryIntentRules.Apply(player.Inventory, intent, player.Creative);
                    break;
            }
        }

        // Always, accepted or not: the snapshot is the truth the client's optimistic copy
        // snaps back to when the server refused what it did.
        PlayerSessions.SendInventory(client, player);
        SendIntentResult(client, seq, accepted);
    }

    /// <summary>
    /// Judges and applies one world intent for <paramref name="player"/>, then answers it: the
    /// authoritative cells around its target when it could edit cells (in reach of the player),
    /// the inventory when it could change it or was refused, and its result, in that order.
    /// A dead player is judged before every other rule: only a respawn is accepted, however far
    /// its eye drifted, and anything else is refused.
    /// </summary>
    private void HandleWorldIntent(ClientConnection client, ServerPlayer player, int seq, in WorldIntent intent)
    {
        var accepted = player.AcceptSeq(seq)
                       && (player.IsDead
                           ? intent.Kind == NetIntentKind.Respawn && RespawnFor(client, player)
                           : ApplyWorldIntent(player, intent));
        if (intent.EditsCells && intent.HasCell
            && NetValidation.InEditReach(player.Position, intent.X, intent.Y, intent.Z, NetworkConfig.NetMaxEditReachBlocks))
            SendCellCorrection(client, seq, intent.X, intent.Y, intent.Z, radius: 1);
        if (intent.PredictsInventory || !accepted)
            PlayerSessions.SendInventory(client, player);
        SendIntentResult(client, seq, accepted);
    }

    /// <summary>
    /// The rules every world intent of a living player passes before the world replays it: the
    /// world is playing, the intent was aimed from (near) where the player is, and an edit fits the
    /// player's edit rate. A living player has nothing to respawn from, so its respawn is refused.
    /// </summary>
    private bool ApplyWorldIntent(ServerPlayer player, in WorldIntent intent)
    {
        if (intent.Kind == NetIntentKind.Respawn)
            return false;
        if (!_state.World.IsPlaying)
            return false;
        if (!NetValidation.EyeWithin(player.Position, intent.Eye, NetworkConfig.NetMaxIntentEyeDriftBlocks))
            return false;
        if (intent.IsRateLimited && !player.EditLimiter.TryTake(_state.Elapsed))
            return false;

        player.Aim(intent.Eye, intent.Direction);
        var context = player.Context;
        // Other clients see the swing even when the hit itself is refused.
        switch (intent.Kind)
        {
            case NetIntentKind.BreakStart:
            case NetIntentKind.BreakComplete:
                player.NoteAction(NetPlayerAction.Punch);
                break;
            case NetIntentKind.Throw:
                player.NoteAction(NetPlayerAction.Throw);
                break;
            case NetIntentKind.Attack:
                player.NoteAction(intent.Attack switch
                {
                    NetAttackKind.Slice => NetPlayerAction.Slice,
                    NetAttackKind.SliceReturn => NetPlayerAction.SliceReturn,
                    _ => NetPlayerAction.Punch,
                });
                break;
        }
        switch (intent.Kind)
        {
            case NetIntentKind.BreakStart:
                return StartBreak(player, intent);
            case NetIntentKind.BreakComplete:
                return CompleteBreak(player, intent);
            case NetIntentKind.Place:
                return PlaceFor(player, intent);
            case NetIntentKind.Use:
                // The right-click chain short of its placement fallback: a click the client did
                // not place with never places here either
                return _state.World.TryUseSelected(World.InteractionReach, context, out _, allowPlace: false);
            case NetIntentKind.Throw:
                return _state.World.ThrowFor(context, intent.Charge);
            case NetIntentKind.Attack:
                return AttackFor(player, intent);
            default:
                return false;
        }
    }

    // The player's own ray from the intent's eye along its aim lands on the cell the intent names:
    // within reach, with nothing in the way
    private bool AimsAt(ServerPlayer player, in WorldIntent intent, out Vector3 normal) =>
        _state.World.RaycastFor(player.Context, includeFluids: false, out var hit, out normal) && hit == intent.Cell;

    // A loaded cell holding a block that can be mined: not air, not a fluid
    private bool TryMineable(in WorldIntent intent, out byte id, out byte state) =>
        _state.World.GetChunkManager().TryGetCell(intent.X, intent.Y, intent.Z, out id, out state)
        && id != 0 && !BlockType.IsFluid(id);

    private bool StartBreak(ServerPlayer player, in WorldIntent intent)
    {
        player.Breaking.Reset();
        if (!AimsAt(player, intent, out _) || !TryMineable(intent, out var id, out var state))
            return false;
        var inventory = player.Inventory;
        player.Breaking.Start(intent.X, intent.Y, intent.Z, id, state, inventory.HeldItem,
            inventory.SelectedBlockId, inventory.SelectedToolInstance, _state.Elapsed);
        return true;
    }

    private bool CompleteBreak(ServerPlayer player, in WorldIntent intent)
    {
        if (!AimsAt(player, intent, out _) || !TryMineable(intent, out var id, out _))
        {
            player.Breaking.Reset();
            return false;
        }

        var inventory = player.Inventory;
        var verdict = player.Breaking.Complete(intent.X, intent.Y, intent.Z, id, inventory.SelectedBlockId,
            inventory.SelectedToolInstance, player.Creative, _state.Elapsed, NetworkConfig.NetBreakSlackSeconds);
        return verdict == BreakVerdict.Break && _state.World.BreakBlockFor(player.Context, intent.Cell);
    }

    private bool PlaceFor(ServerPlayer player, in WorldIntent intent)
    {
        // The face placed against: the server ray's own, or another face of the same cell turned
        // toward the eye (a third-person aim reports the camera ray's face). Never a face the eye
        // cannot see, which would place through the block.
        if (!AimsAt(player, intent, out var normal))
            return false;
        var face = intent.Normal;
        if (face != normal && !NetValidation.FaceFacesEye(intent.Cell, face, intent.Eye))
            return false;
        return _state.World.TryPlaceSelectedBlockAt(player.Context, intent.Cell, face);
    }

    private bool AttackFor(ServerPlayer player, in WorldIntent intent)
    {
        // A slice needs a slicing weapon in hand, a punch anything else, and either one only at its
        // own pace. A swing that strikes nothing is still a legal swing.
        var held = player.Inventory.HeldItem;
        var slicing = held is { Attack: AttackPattern.Slice };
        if (slicing != (intent.Attack != NetAttackKind.Punch))
            return false;
        if (!player.TryAttack(intent.Attack, held, _state.Elapsed, NetworkConfig.NetAttackSlackSeconds))
            return false;
        if (intent.Attack == NetAttackKind.Punch)
            _state.World.PunchFor(player.Context);
        else
            _state.World.SliceFor(player.Context, intent.Attack == NetAttackKind.SliceReturn);
        return true;
    }

    /// <summary>
    /// Brings a dead <paramref name="player"/> back (full health, at its respawn eye or else the
    /// join spawn), snaps its client there with a <see cref="NetMessage.PlayerCorrection"/> whose
    /// ack its states must carry before they move it again, and tells it its fresh status.
    /// </summary>
    private bool RespawnFor(ClientConnection client, ServerPlayer player)
    {
        player.Respawn(_state.JoinSpawn ?? _state.World.Camera.Position);
        PlayerSessions.SendPlayerCorrection(client, player.IssueCorrection(), player.Position);
        PlayerSessions.SendPlayerStatus(client, player);
        Log.WriteLine($"net: {client.Name} (#{client.Id}) respawned");
        return true;
    }

    private static void SendIntentResult(ClientConnection client, int seq, bool accepted)
    {
        using var writer = new PacketWriter(NetMessage.IntentResult);
        writer.Write(seq);
        writer.Write(accepted);
        client.Send(writer.ToArray());
    }

    /// <summary>
    /// Sends the authoritative state of every loaded cell within <paramref name="radius"/> (on each
    /// axis) of (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>), tagged with the
    /// intent <paramref name="seq"/> it answers: whatever the client predicted around that target
    /// (the cell itself, the face neighbor it placed into, a popped pebble above, a structure's
    /// cells) snaps to what the server has, accepted or not.
    /// </summary>
    private void SendCellCorrection(ClientConnection client, int seq, int x, int y, int z, int radius)
    {
        var manager = _state.World.GetChunkManager();
        _correctionScratch.Clear();
        for (var dx = -radius; dx <= radius; dx++)
        for (var dy = -radius; dy <= radius; dy++)
        for (var dz = -radius; dz <= radius; dz++)
        {
            if (manager.TryGetCell(x + dx, y + dy, z + dz, out var id, out var state))
                _correctionScratch.Add(new NetCellChange(x + dx, y + dy, z + dz, id, state));
        }

        using var writer = new PacketWriter(NetMessage.BlockCorrection);
        writer.Write(seq);
        writer.Write(_correctionScratch.Count);
        foreach (var cell in _correctionScratch)
        {
            writer.Write(cell.X);
            writer.Write(cell.Y);
            writer.Write(cell.Z);
            writer.Write(cell.Id);
            writer.Write(cell.State);
        }
        client.Send(writer.ToArray());
    }
}
