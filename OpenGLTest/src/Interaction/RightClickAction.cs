using System;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Items;
using VoxelEngine.Magic;
using VoxelEngine.Networking;
using VoxelEngine.Player;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Interaction;

/// <summary>
/// The right-click action: the chain that hands a right-click to the first thing that claims it (a
/// station or an openable block, then a spell table, then a held staff's cast, then the held item's own use, otherwise
/// a placement), for the
/// local player or a server's player, plus the local player's timed use charge. Owned by
/// <see cref="World"/>; main thread only.
/// </summary>
internal sealed class RightClickAction
{
    private readonly World _world;
    private readonly bool _clientMode;

    private bool IsPlaying => _world.IsPlaying;
    private Camera Camera => _world.Camera;
    private PlayerModel Player => _world.Player;
    private Inventory Inventory => _world.Inventory;
    private LocalPlayer LocalPlayer => _world.LocalPlayer;
    private LeftClickAction LeftClick => _world.LeftClick;
    private BlockPlacer Placer => _world.Placer;
    private ThrowAction Throws => _world.Throws;
    private BlockInteractions Interactions => _world.Interactions;
    private WorldItemUse ItemUse => _world.ItemUse;
    private SpellCaster Caster => _world.Caster;

    internal RightClickAction(World world, bool clientMode)
    {
        _world = world;
        _clientMode = clientMode;
    }

    private float _useChargeSeconds;
    private byte _useItemId;

    // The slot the last local cast went out on, which a client's Cast intent names
    private int _castSlot;

    /// <summary>
    /// Right-click: runs the first thing that claims the click - a campfire, quern, kiln or cast
    /// mold interaction or an openable block, then a spell table (claimed with no side effect unless
    /// a block is in hand), then a held staff's cast of its selected spell slot (through
    /// <see cref="SpellCaster"/>; a staff never places, and a client raises a
    /// <see cref="NetIntentKind.Cast"/> intent for its server to bill and run), then the selected
    /// slot's own <see cref="Item.OnUse"/> (a bucket scoops
    /// or pours, flint grounds a pile, a firestarter strikes), otherwise places the selected
    /// block. A successful tool use morphs the instance in place, so every hotbar slot holding
    /// that bucket flips with it and scooping and pouring alternate on one button. Returns
    /// whether the click was claimed, so a plain throwable only starts charging when nothing
    /// here owned it - a mudball aimed at a lit campfire cooks instead of being thrown.
    /// False unless the world is playing: nothing may edit an unloaded world.
    /// </summary>
    internal bool TryUseSelected(float maxDistance) => TryUseSelected(maxDistance, out _);

    /// <summary>
    /// <see cref="TryUseSelected(float)"/> that also hands back what claimed the click, so the
    /// caller can open the spell builder on a <see cref="RightClickOutcome.SpellTable"/> claim (a
    /// client raises a plain Use intent for it, like any other non-placement claim).
    /// </summary>
    internal bool TryUseSelected(float maxDistance, out RightClickOutcome outcome)
    {
        var claimed = TryUseSelected(maxDistance, LocalPlayer.LocalContext(), out outcome);
        // A connected client predicted the click; the server replays it for this player. A
        // placement names the face it went against (the server checks the player can see it), any
        // other claim only the cell under the crosshair, which scopes the correction sent back.
        if (!claimed || !_clientMode) return claimed;

        if (outcome == RightClickOutcome.Place)
            _world.RaiseIntent(WorldIntent.Place(Camera.Position, Camera.LastAimDirection, Placer.LastPlaceHit, Placer.LastPlaceNormal));
        else if (outcome == RightClickOutcome.Cast)
            _world.RaiseIntent(WorldIntent.Cast(Camera.Position, Camera.LastAimDirection, _castSlot));
        else
            _world.RaiseIntent(WorldIntent.Use(Camera.Position, Camera.LastAimDirection,
                LeftClick.HasSelectedBlock ? LeftClick.SelectedBlockPosition : null));

        return claimed;
    }

    /// <summary>
    /// <see cref="TryUseSelected(float)"/> for <paramref name="player"/>: every station
    /// interaction, the staff's cast (local players only: a server takes casts as their own intent),
    /// the item's own use (its tool morph, durability and cooldown) and the placement
    /// cost run against that player's inventory, aim and creative flag. Single player passes its
    /// own camera's context; a server passes the acting player's. <paramref name="outcome"/> says
    /// what claimed the click; <paramref name="allowPlace"/> false stops the chain before the
    /// placement fallback (a server replaying a click the client said was not a placement).
    /// </summary>
    internal bool TryUseSelected(float maxDistance, PlayerContext player, out RightClickOutcome outcome,
        bool allowPlace = true)
    {
        outcome = RightClickOutcome.None;
        if (!IsPlaying) return false;
        var local = player.Camera != null;
        switch (local)
        {
            case true when Throws.IsThrowCharging:
                return false;
            case true:
                Camera.NotifyPlayerAction();
                break;
        }

        if (Interactions.TryCookOrCollect(maxDistance, player) || Interactions.TryQuernInteract(maxDistance, player)
                                                  || Interactions.TryKilnInteract(maxDistance, player) || Interactions.TryCastInteract(maxDistance, player)
                                                  || Interactions.TryToggleOpenable(maxDistance, player))
        {
            LocalPlayer.PunchSwing();
            outcome = RightClickOutcome.Station;
            return true;
        }
        // A spell table claims the click before the staff casts; opening the builder is not a strike
        if (Interactions.TryOpenSpellTable(maxDistance, player))
        {
            outcome = RightClickOutcome.SpellTable;
            return true;
        }
        var item = player.Inventory.HeldItem;
        if (item is StaffItem)
        {
            // A staff never places; a server's Use replay (no camera) claims nothing, since casts come as Cast intents
            if (!local) return false;
            var slot = HeldStaff.SelectedSlot(player.Inventory);
            if (Caster.CastFor(player, slot, Caster.LocalPacing, _world.Now, 0) != CastVerdict.Cast) return false;
            _castSlot = slot;
            LocalPlayer.PunchSwing();
            outcome = RightClickOutcome.Cast;
            return true;
        }
        if (item == null || item is BlockItem || item.UseAction == ItemUseAction.None)
        {
            if (!allowPlace || !Placer.TryPlaceSelectedBlock(maxDistance, player)) return false;
            outcome = RightClickOutcome.Place;
            return true;
        }
        if (!ItemUse.DispatchUse(item, maxDistance, player)) return false;
        outcome = RightClickOutcome.ItemUse;
        return true;
    }

    /// <summary>Whether a timed use is charging (see <see cref="TryBeginUseCharge"/>).</summary>
    internal bool IsUseCharging { get; private set; }

    /// <summary>
    /// Starts the timed use of the selected item when it declares <see cref="Item.UseSeconds"/>:
    /// holding the button plays a use pose and the use runs only at completion, so letting go
    /// early (<see cref="CancelUseCharge"/>) spends nothing. False for anything else, which uses
    /// on the click or charges a throw. False unless the world is playing.
    /// </summary>
    internal bool TryBeginUseCharge()
    {
        if (!IsPlaying) return false;
        if (IsUseCharging) return true;
        var id = Inventory.SelectedBlockId;
        if (id == 0 || Inventory.SelectedCount <= 0) return false;
        if (ItemRegistry.TryGet(id) is not { } item || item.UseSeconds <= 0f) return false;
        IsUseCharging = true;
        _useChargeSeconds = 0f;
        _useItemId = id;
        Player.StartUseCharge(item.UsePose);
        return true;
    }

    /// <summary>
    /// Advances a held timed use; at the selected item's <see cref="Item.UseSeconds"/> the use
    /// runs exactly as a click would have run it. A changed selection cancels the charge.
    /// </summary>
    internal void UpdateUseCharge(float deltaTime)
    {
        if (!IsPlaying) return;
        if (!IsUseCharging) return;
        var seconds = ItemRegistry.TryGet(_useItemId)?.UseSeconds ?? 0f;
        if (Inventory.SelectedBlockId != _useItemId || Inventory.SelectedCount <= 0 || seconds <= 0f)
        {
            CancelUseCharge();
            return;
        }

        _useChargeSeconds += Math.Max(0f, deltaTime);
        // A drawn bow holds at full draw until the button is let go (CancelUseCharge): its use never runs by itself
        var bow = ItemRegistry.TryGet(_useItemId)?.UsePose == UsePose.Bow;
        if (bow || _useChargeSeconds < seconds)
        {
            Player.UpdateUseCharge(MathUtils.Clamp01(_useChargeSeconds / seconds));
            return;
        }

        // Completed: the use runs now, on the same path a click would have taken
        IsUseCharging = false;
        Player.CancelUseCharge();
        TryUseSelected(World.InteractionReach);
    }

    /// <summary>Cancels a held timed use; the item spends nothing.</summary>
    internal void CancelUseCharge()
    {
        if (!IsPlaying) return;
        IsUseCharging = false;
        Player.CancelUseCharge();
    }
}
