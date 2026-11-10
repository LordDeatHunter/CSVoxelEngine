# Player systems: HUD, water, health, mana, effects, gear, chests, waystones

HUD and feedback, swimming and water depth states, drowning, lava, regeneration, mana, status effects (player side), gear and armor, bone shrapnel, chests and waystones.

**HUD and feedback.** The crosshair is drawn as five pieces cropped out of its 16x16 texture (a
center dot and four pills): the arms slide outward with the eased `Camera.ThrowCharge` while a throw
is charged, a ring of dots around it fills with `LeftClickAction.Crosshair.BreakProgress` (0 in creative or
with no target), and the hit marker flashes an X at the connecting-hit sites the hit-stop uses (a
landed punch, slice or sword swing), fading over `HitMarker.Seconds` (`HitMarkerTests`). At the top of the
screen the **look-at label** shows the targeted block: its name and a small spinning 3D render of
it, pushed below the compass or clock strip while one is equipped (`TopStripRenderer.DrawLookLabel` and
its `LookLabelTop` over `BlockCubeCache`), the name in red while the held item cannot mine
it, and "Unknown" with a flat dark silhouette until its item has been obtained; creative mode and a
block with no item at all always know, and `X` adds id, state, sun/block light and cell debug lines
(`LookLabel`, `LeftClickAction.LookAt`, `LookLabelTests`, `look-label.json`). "Obtained" is the inventory's own
set (`Inventory.Knows`, marked in `Add` and by `MarkKnown` when the player finishes mining the block, never by TNT or other non-player breaks, saved as names in `player.json` and riding the
`InventoryNetCodec` snapshot, so a server's player and a client agree), which also drives the
"New: <name>" toast on a first pickup or `/give`. `HudToast` draws one line above the item-name row
for the day toast ("Day N" as the sun rises through dawn), the inventory-full warning, the
first-find toast and the spell table's hold-a-staff hint; the spell builder panel hides the crosshair
and the hearts row like the [E] panel does; `HotbarNameFade` shows the selected item's name after a slot or item switch and
`HotbarPickupPop` bumps a slot whose count rises, and a collected drop's icon arcs from where it lay into its hotbar slot (`PickupFlyIcons`, owned by the pop, spawned by `DropPickup` for local pickups only, drawn by `HudRenderer.DrawFlyingPickups`; a client sees none since its drops arrive as snapshots). The [E] panel fades and slides up on open and fades on close (`InventoryView.Transition`, `PanelTransition`; `UIRenderer.SetOverlay` shifts the pass's projection and sets `uFade` in `ui-fragment.glsl`, and panel scissors add `OverlayOffset`). The player's active status effects show as a row of icons left of the hearts (`HudRenderer.DrawEffects`, see "Status effects"). `ItemTooltip.Hover` builds the hover panel over the
[E] panel's rows and the hotbar as a `HoverTooltip` (its lines, their colors and the hovered item's rarity via
`Rarities.Of`), and the box wears that rarity's chrome (see "Rarity chrome"); holding Tab draws the player list (a
server session only). F2
saves the finished frame through `VisualCapture`; F1 toggles `GameplayInput`'s `hudVisible`, the gate the scenario
screenshot's `hideHud` drives. The chat commands are `/help`, `/tp x y z`, `/give <item> [count]`
(the last two needing rights), `/time day|night|<seconds>` and `/locate tower`, which any player may use: it finds the
nearest battle tower from worldgen (`StructureLocator.TryFindTower`, within `TowerLocateRings` tower cells; a tower the `TowerManager` records as defeated is skipped) and answers
with a line whose coordinates are a link (`ChatLink`), whose click puts a `/tp` to the ground beside the tower into the
chat input without sending it (`ChatView.Suggest`; the `/tp` itself still needs rights); the death line is
`DeathMessages.For` over the blow `PlayerHealth` recorded, drained into the chat (`LocalPlayer.TakeDeathMessage`).

Standing in water switches the player to **swimming** (`Camera.IsInWater`, recomputed every frame by `PlayerPhysics.UpdateWaterState` from the real fluid surface heights under the body): movement is a series of **burst strokes** rather than a steady paddle: a stroke (ready at once on entering the water, then every `SwimStrokeSeconds`) *sets* the horizontal speed to `SwimBurstSpeed` along the WASD direction (0.6x backing up), which then glides off under `SwimGlideDrag` until the next one; Space and Ctrl stroke straight up (`SwimBurstUp`) or down (`SwimBurstDown`), the vertical speed gliding toward the gentle `SinkSpeed` through `MathUtils.Damp`, and no input means no stroke (`Camera.SwimStrokeCount` moves on each, which is how the body starts its stroke animation on the same frame; see "Animation juice"; with the eye under water, not in lava, each one also lets out a burst of `SwimStrokeBubble*` bubbles just in front of the head through `BubbleParticleSystem.TrackSwimStrokes`, run by `AmbientEffects.UpdateBubbles` in singleplayer and on a client, never on a server). Swimming into a bank while holding Space adds `SwimExitBoost` so the player can climb out without a ramp. Flight ignores water entirely, and a player standing on the bottom of a shallow puddle still jumps normally. Flowing water also pushes the player and floating item drops along its flow direction (`ChunkManager.GetFluidFlowGlobal`, Minecraft's `FlowingFluid.getFlow`: a horizontal vector summed from how much lower each neighboring cell's surface sits, zero in a still pool). `Camera.IsEyeInWater` is the separate eye-only test and is the source of truth for the underwater tint.

**Water depth states.** Beside `IsInWater` (which keeps its meaning) the camera reports how deep the
body stands: `Camera.ImmersionShare`, the share of `PlayerPhysics.PlayerHeight` under the highest fluid
surface over the cells the body overlaps (gathered in the same `UpdateWaterState` pass), and
`Camera.WaterPhase` from it and the head's eye (`WaterImmersion.PhaseOf`, GL-free): `Dry`, `Wade` (under
`WaterFloatShare` of the body, e.g. a one-block source pool whose surface sits 8/9 up), `Float` (half or
more under with the eye above the surface) and `Swim` (the eye under). `WaterImmersionTests` locks the
bands down. The phase drives the movement (`PlayerPhysics.Update`, `WaterFloatTests`): `Wade` *walks*
(ground acceleration, gravity, auto-step and the land jump) at `WadeSpeedFactor` of the walk, still
carried by a flow, so the burst strokes above apply only from `Float` up. A swimmer is latched onto the
swim physics until it lands on ground, so a stroke that bobs it out above the float band is caught on
the way back down instead of falling through the surface. In the water the vertical speed no longer
glides toward `SinkSpeed` for everyone: it eases at `FloatSettleRate` toward
`WaterImmersion.BuoyancySpeed` (`FloatBuoyancy` times the height error, capped by the sink and rise
speeds), which holds a floating body where `WaterFloatRestShare` (about 70 %) of it is under with the
eye just out, and floats a body that went under without diving (a fall in) back up. A down stroke
(Ctrl) *dives*: the diver keeps the old slow glide toward `SinkSpeed` until its eye surfaces again, and
Space strokes up as before. The server needs nothing new: a body resting at the float is
`PlayerBodyProbe.InWater`, so the hover rule never trips (`MovementRulesTests` feeds it the client's own
simulated float and stroke hop).

**Drowning.** With the eye under water (`PlayerBodyProbe.EyeInFluid`, the one eye rule the physics, the
shoulder camera and the server share; lava never counts) the player's air (`Breath`, owned by `PlayerHealth`,
`PlayerConfig.AirSeconds` 15) drains; out of the water it refills at `AirRefillPerSecond`, and at zero a
`DamageSource.Drowning` blow of `DrownDamagePerTick` lands every `DrownTickSeconds` (`PlayerHealth.UpdateBreath`;
creative and flight are immune, `DeathMessages` says "drowned"). Singleplayer ticks it in
`LocalPlayer.UpdateHealth`; a server ticks each `ServerPlayer` in `TickStatus` (an unloaded body is not judged) and
sends the air in `PlayerStatus` as a `Breath.NetSteps` byte (protocol 9; the status goes out whenever the step
changes), which a client mirrors (`ClientMirror.ApplyNetworkAir`). `HudRenderer.DrawAir` draws ten bubbles
(`assets/textures/ui/bubble.png`) over the hearts, right-aligned, emptying from the left,
shown only below a full lung. `BreathTests`, `visual-examples/drowning.json` (`VOXEL_TIME=147`).

**Lava.** A body touching lava burns for `PlayerHealth.LavaDamagePerTick` (10 HP) every
`LavaTickSeconds` (0.5 s), i.e. 20 HP/s, and catches the `burning` status effect while it is in the
pool, so the fire rides out with it for the effect's own duration; creative and flight are immune.
Singleplayer ticks it in `LocalPlayer.UpdateHealth`, a server in `ServerPlayer.TickStatus` (an unloaded
body is not judged); `HealthTests`. Lava also drags movement: `PlayerConfig.LavaSpeedFactor` scales the
walk and the swim stroke while any part of the body is in it (flight ignores it), so a sprint cannot
carry through a shallow dip (`LavaMovementTests`).

**Regeneration.** Out of combat the player heals (`PlayerHealth.UpdateRegen`): once
`PlayerConfig.RegenDelaySeconds` have passed with no damage taken (`TakeDamage` restarts the clock)
and no blow dealt (`NoteCombat`, called by `MobStrikes.StrikeMob` through `PlayerContext.Health` for
punches and slices; a thrown item does not count yet) it refills at `RegenPerSecond`, scaled by
`RegenSprintFactor` while sprinting (0 would halt it). Creative and flying players, the dead and a
full pool are left alone. Singleplayer ticks it in `LocalPlayer.UpdateHealth`, a server in
`ServerPlayer.TickStatus` (the sprint bit is the client's claim and only slows its own healing),
and the heal reaches a client through the usual `PlayerStatus` health. `RegenTests`.

**Mana (MG-1).** The magic pool is `PlayerHealth.Mana` (`PlayerMana`, GL-free, tuning in
`ManaConfig`): it regenerates at `Max / ManaConfig.RegenFullSeconds` per second
(`PlayerHealth.UpdateMana`), never while dead, and refills on a respawn. `TrySpend(cost, creative)`
is free in creative and refused, changing nothing, when the pool is short. The max starts at
`ManaConfig.StartMax` and grows through `MaxFor`/`SetKnownParts` with the player's known spell parts
(`KnownSpellParts.SizeMana`, called whenever they are learned or restored; MG-8), up to `ManaConfig.MaxCap`. Singleplayer ticks it in
`LocalPlayer.UpdateHealth`, a server in `ServerPlayer.TickStatus`, which marks the status dirty only
when the whole-unit `NetMana` or the `Max` moves (about one status a second while it refills, none
when full). It rides the end of `PlayerStatus` as two ints (protocol 11, the max clamped to
1..`MaxCap` and the mana into it on read) and a client only mirrors it
(`ClientMirror.ApplyNetworkMana`). It is saved as `Mana` in `player.json` (absent reads as full; the
max is not saved, since it follows progression). `HudRenderer.DrawMana` draws it as a thin blue bar
as wide as the hotbar row in the margin under it, hidden while dead or in creative. The cost a cast
spends comes from its spell parts (`SpellRecipe`, see "Spell part schema"). `ManaTests`.

**Status effects (MG-4b).** The player's effects are `PlayerHealth.Effects` (a `StatusEffectSet`, see
"Status effect schema"), ticked by `PlayerHealth.UpdateEffects` in `LocalPlayer.UpdateHealth`
(singleplayer) and `ServerPlayer.TickStatus` (server); a creative, flying or dead player has them
cleared, and a respawn or a restore clears them too. Their damage is a `DamageSource.Magic` blow whose
death line is the effect's own `deathMessage` phrase ("burned to death"), else "was killed by magic". A
server marks the status dirty whenever the set of active effects changes, and the mask rides the end of
`PlayerStatus` (protocol 13), which a client only mirrors (`ClientMirror.ApplyNetworkEffects`; a mirror
never ticks). `HudRenderer.DrawEffects` draws one icon per active effect that has an `icon`, in a row
ending just left of the hearts, hidden while dead. `StatusEffectSetTests`, `StatusEffectApplyTests`.

**Gear and equipment (I-1).** A player wears up to `GearConfig.EquipmentSlotCount` pieces (`Inventory.Equipment`, an
`Equipment`: one instance id per slot, 5 armor slots in head/chest/legs/feet/gloves order, then 4 accessory slots; a
slot takes a `GearItem` of its own `GearSlot`, `GearSlots.Fits`). Gear is an item with an `equip` object in its JSON
(`slot`, `armor`, `modifiers` by `GearStat` name, `set`, `visible`, `immune`, `passives`, `abilities`; `rarity` beside it),
compiled by `ItemLoader.ParseEquip` into a `GearInfo`. `equip.abilities` (`air_jump`, `no_fall_damage`, `extra_air`,
`magnet`, `bone_shrapnel`; an unknown name refuses the item) compiles into `GearInfo.Abilities` (`GearAbility` flags), and
`Equipment.Abilities`/`Has` caches their union with the stats on `Version` (the `no_fall_damage`, `extra_air` and `magnet`
consumers are under "Armor, modifiers and sets", `bone_shrapnel` under "Bone shrapnel"). Every gear
sprite is 32x32 committed pixel art (the same width every item sprite is authored at; the large
swords are the 42x42 exception; several are still placeholders, listed in `USER_TODO.md`): the five-piece silhouette
of each set is drawn once, shaded by a shared bevel and recolored per material, then dressed per material (stitching on
leather, rivets on metal), and the rings, charms and belt are drawn the same way. **Cloud boots** (`cloud_boots`, feet, from iron boots and cloud shards) carry `air_jump`: one free air jump per airtime
(`PlayerPhysics`), on a fresh Space press while airborne, never in water or flight nor on the frame a creative double tap toggles
flight; it rises as high as a ground jump, keeps the horizontal speed (sprint momentum), refills on the ground, in water and in
flight, and has its own `AirJumpCount`, never moving `JumpCount`, so it never works a bloomery's bellows. `LocalPlayer.ApplyGear`
feeds the flag into `Camera.GearAirJump` every frame (singleplayer and a client's mirrored equipment), and the server judges the
hop by `ServerPlayer.AirJumps`: the server's airtime rise budget (`MovementRules.AirRiseBudget`: one jump apex per jump the
player has plus `NetAirRiseSlackBlocks`, reset by support or a descent-then-rise near ground, widened by a bounce block and a
queued knockback lift) is what accepts the legal hop and corrects a second (see "The movement rubberband"); no wire change,
protocol 24. `AirJumpTests`. **Accessories**: the stat accessories are the Ring of Focus (`focus_ring`, mana regen) and the
Charm of Reserves (`reserve_charm`, max mana), both crafted and looted, plus `amulet_of_vigor`, `miners_belt` and
`ember_charm`; the **metal ring ladder** is `tin_ring` and `copper_ring` (+1 armor) and `bronze_ring` and `iron_ring` (+2),
armor only (no mana, no set), never looted, the tin, copper and bronze ones cast in the ring mold (200 ml, see "Cast molds") and the iron one forged on the anvil from two `wrought_iron` (`recipes/forge/iron_ring.json`, see "The bloomery and the anvil"), their armor adding to the
body armor's in `GearStatsCalculator`; the ability charms are under "Armor, modifiers and sets". Its instances live in the same `Inventory.Tools` list as tool
instances (so every instance lookup, the save and the wire carry them) but count against their own pool
(`InventoryCategory.Gear`, `GearConfig.GearCapacity`), never take a hotbar slot and come off when removed from the
pool. `Equipment.Stats` (`GearStatsCalculator`, clamped by `GearStats.Clamped`) is cached until the worn set changes;
`PlayerHealth.ApplyGear` turns it into the health pool (`PlayerHealth.Max`, the hearts scale to it) and, where the pool
is simulated, the mana max and regen (`PlayerMana.SetGear`); `Camera.GearMoveFactor` scales walk and swim speed,
mining and attack pace and `Combat.*Damage` read the mining, attack-speed and damage factors on the client
(`LeftClickAction`) and the server (`BreakProgress.Start`, `AttackPacing`). Wearing is a `NetInventoryAction.EquipGear` /
`UnequipGear` intent (protocol 19), judged by `Equipment.TryEquip`; the worn ids save as `InventoryData.Equipped`. The
[E] panel's Gear tab lists the pieces over `EquipmentCardLayout`/`EquipmentCardRenderer`: pick a row, then
click a lit slot to wear it, drag the row onto a slot, or right-click the row (or a gear search hit) to wear it or take it
off in one click (`RowEquip`; a second left click only picks again). Hovering a worn piece's row shows its tooltip (`InventoryPanelInput.HoveredTooltip`, the `EquipmentCardLayout.SlotAt` hit), and a worn name too wide for its row is never cut with an ellipsis: it scrolls back and forth inside the row (`UIRenderer.DrawTextMarqueeY`, the timing in `TextMarquee` and `HudConfig.Marquee*`). Every list of the panel (a tab's rows, the search
results and the creative catalog) marks an equipped row the same way (`EquippedMark`): a green accent bar, "worn" in
place of the count for a worn piece, and the hotbar key on a chip over the icon for an item on the hotbar. A left click
never puts a row on the hotbar: a pick goes there by a click on a slot, a drag onto it (`RowDragInput`, `RowDrag`: a press
that lifts once the pointer leaves it by `RowDrag.LiftDistance`, then `RowEquip.DropOnHotbar`/`DropOnEquipment`, never a
toggle; a creative catalog row is granted one first), the right-click or a number key over the row (a plain row or a search hit the player holds); a creative catalog
grant is picked but stays off the hotbar (`Inventory.Add(..., autoEquip: false)`, on the server's replay too), while a
world pickup still lands in the first empty slot. `GearTests`, `RowEquipTests`; `visual-examples/gear.json` and `accessories.json` (the double jump
and a feather-charm fall; both `VOXEL_TIME=147`).
**Worn armor (I-3).** A body is drawn wearing its visible armor (`equip.visible`, slots head, chest, legs, feet, gloves) as flat
tinted shells: `ArmorLook` (GL-free) is the item id per armor slot, `ArmorLook.From(Equipment)` for the local player and
`FromWire` for a peer (a piece in the wrong slot or not visible armor is dropped). `PlayerPasses.DrawPart` draws a covered
part's mesh and then its shell (`PlayerRig.BuildArmorMesh`: the part's cuboids grown `ArmorInflate` blocks, vertex-colored by
`ArmorLook.ColorOf`, the mean of the item sprite, over a 1x1 white texture), built on first use per (part, piece); the
local look is set once a frame from `Equipment.Version` (`PlayerPasses.LocalArmor`, also read by the mirror), a remote's
rides `PlayerList` (protocol 21, one byte per armor slot at the end of each entry, `RemotePlayer.Armor`). The shadow pass
draws no armor. `ArmorLookTests`; `visual-examples/gear-worn.json` (`VOXEL_TIME=147`).

**Armor, modifiers and sets (I-2).** `PlayerHealth.TakeDamage(amount, source, ...)` runs the blow through
`ArmorRules.Reduce` (`max(min(hit, minDamage(source)), hit - armor)`, minimums in `PlayerConfig.ArmorMinDamage*`; drowning,
magic over time and the source-less overload, which the save restore and the client's mirror use, ignore armor). An
item's `equip.immune` blocks effects at `StatusEffectSet.ImmunityCheck`, and `equip.passives` (`on_hit`,
`on_damaged`, `low_health`) fire through `GearPassives`: on-hit from `MobStrikes.StrikeMob`, the other two on the mobs
near the wearer from `MobDirector.FireWearerPassives`, authoritative side only. Loot gear rolls `ModifiersComponent`
(rarity plus modifiers, saved as `ToolData.Rolled`) from the pure `GearRoller.Roll(gear, rarity, seed)`
(`Inventory.AddRolledGear`), crafted gear never; a piece's rarity is its roll's or its item's. Sets are
`assets/data/sets/<name>.json` (`GearSetLoader`), bonuses by piece count summed in `GearStatsCalculator`; only head, chest and
legs count toward a set (`GearSlots.CountsForSets`; boots and gloves are free-standing armor). The
`undeadDamage` stat is a fraction applied in `MobStrikes.StrikeMob` through `Combat.VersusUndead` to a mob whose
`IsUndead` is true (the battle tower's skeleton, see "Skeleton"), never rolled by `GearRoller`. The `equip.abilities` consumers read the cached
`Equipment.Has`: `no_fall_damage` skips fall billing in `PlayerHealth.TakeDamage` and the server's `ObserveAcceptedState`,
`extra_air` widens `Breath.MaxAir` through `ApplyGear`, `magnet` widens `DropCollector.Range` in `DropPickup`. The ability
charms wear them: Feather (`feather_charm`, `no_fall_damage`), Gill (`gill_charm`, `extra_air`, `GearConfig.ExtraAirFactor`),
Frostward and Fireward (`frost_charm`, `fire_charm`: immune to frostburn / burning) and Lodestone (`magnet_charm`, `magnet`,
`GearConfig.MagnetRangeFactor`), all rare and loot-only (ruin chests, rare mob drops; a mob-table charm is an unrolled instance,
`CharmLootTests`). Accepted gaps: a magnet-latched drop is pulled through walls, and a client's bubble row can lag an equip by
one status. Any item's tooltip title takes its rarity's color (`RarityStyle`; the shown rarity is `Rarities.Of`: a stack's
roll, else its item's JSON `rarity`, Common for a block), and a non-gear item above Common names its rarity on its kind line
("Rare Item"). [E] list rows and chest rows wear the rarity chrome and name color (see "Rarity chrome") while the accent bar
stays the equipped/ready status. The tooltip then shows rolled lines, set progress and abilities. `GearRulesTests`.

**Bone shrapnel (the battle tower's bone pair).** A blade's own `shrapnel` object and the Bone Charm's `bone_shrapnel`
ability feed one effect: a landed melee hit sprays bone fragments (`Simulation/BoneShrapnel`, authoritative worlds only,
main thread) out of the struck body, and each fragment wounds the first *other* enemy it touches through the ordinary
`MobStrikes.StrikeMob` path (so its shove, sparks, damage number and undead bonus are the weapon path's own, and a
fragment's own hit never sprays again). `LeftClickAction` resolves the payload at its strike sites
(`ShrapnelProfile.Resolve`) and hands it to `MobStrikes.StrikeMob` as a `ShrapnelProfile`: the blade's profile alone, the
charm's weaker default one (`ShrapnelConfig.Charm*`) when only the charm is worn, or the blade's count and damage raised
by `ShrapnelConfig.TogetherFactor` (about 20 %) when both are present, so the blade's own effect always stays the
stronger. A thrown item, a spell bolt and a fragment's own hit pass no payload, so shrapnel never cascades. The struck
mob is passed as the fragment's `ignore`, so a
burst never doubles a blow on its source. Fragments are ordinary `ThrownProjectile`s of the `bone` item with
`ShrapnelConfig.GravityScale` of gravity and a short `LifeSeconds`, so the existing thrown-object draw and the water mirror
render them; `World.Shrapnel.AppendTo` adds them to the frame's projectile list after `Throws.RefreshProjectiles`. The
`bone_breaker_blade` (a `sword` with `shrapnel: { count: 6, damage: 3 }`) and the `bone_charm` (accessory: `bone_shrapnel`,
slight `manaRegen`, +10 % `undeadDamage`) are crafted; `BoneShrapnelTests`, and `visual-examples/bone-shrapnel.json`
(`VOXEL_TIME=147`) films the sharpened burst.

**Chests (I-4).** A block with a `chest` object (`slots`, optional `loot`; `BlockType.Chest`) is a chest block entity kept by
`ChestManager` (`Simulation/Chests/`, owned by `World`, saved in `stations.json` as `Chests`). Its `ChestState` is created on
first use (`ChestManager.Open`, a break): a ruin chest (`ruin_chest_*`, worldgen-only; the underground one rolls the table of its cave biome, see "Structure templates and ruins") then rolls its loot table with
`ChestLoot.Materialize` from the hash of its cell and the world seed, so a chest nobody touched holds no state and is never
saved. An optional `chest.tiers` list of loot tables instead of `loot` (validated at load; the two are exclusive) makes a
**tiered chest** (the battle tower's `tower_chest`): its tier is state bits 2-7 (`ChestProfile.TierShift`/`TierState`/`TierOf`,
bits 0-1 being the facing) and `ChestProfile.TableFor(state)` picks the table, clamped to the last entry.
`ChestLoot.Materialize(profile, state, cell, seed)` and `ChestManager.NotifyBlockRemoved(..., removedState, ...)` take the
cell's state (`BlockEditor` passes `brokenState`, `Open` reads it), so opening and breaking unopened agree. A slot is a
stackable or one tool/gear instance with every component (`ChestSlot`, `ItemStack.CloneAs`); moves are
`ChestTransfer` (conserving, merging up to `ChestConfig.MaxStack`) behind `ChestAccess.Apply`, the same in singleplayer and on
the server. A break and the stale sweep spill the contents as drops (instances lose their components there). Over the wire
(protocol 20) the client sends `ChestOpen`/`ChestMove`/`ChestClose` and gets a per-player `ChestContents`; `ChestSessions`
(server) judges each move against that player's open chest and reach, tells every viewer, and closes the chest when it is
broken or out of reach. Right-clicking a chest is a `RightClickOutcome.Container` claim (sneak places against it instead);
`ChestInput` (owned by `InventoryPanelInput`) opens the `ChestView` card (`UI/Chest/`, `ChestCardRenderer`) beside the [E] list,
whose rows then deposit (one unit, all with shift) while the card's rows withdraw; the card's rows wear the same
`RarityStyle` chrome, accent bar and name color as the [E] rows (see "Rarity chrome"). The chest card takes the right side
of the panel, so the preview, equipment and crafting cards are neither drawn nor clicked while it is up. `ChestTests`, `TowerChestTests`, `ChestServerTests`;
`visual-examples/chests.json` (`VOXEL_TIME=147`, with the `setblock`, `chest` and `chestrow` ops).

**Waystones (W-1..W-3, plans/WAYSTONES.md).** A block with `"waystone": true` (`BlockType.Waystone`; the four
`waystone_<realm>` blocks: surface, underground, hell, sky, on the shared `block/waystone` model) is a teleport point. A waystone stands two cells tall: the `waystone_<realm>` block is
its lower half (plinth and the foot of the rune shaft) and names `waystone_<realm>_upper` (the shaft's top, cap and point, on
`block/waystone_upper`, no `waystone` flag) as its `upperHalf`, so it is a `TallBlock` like the door: worldgen writes both,
mining either half takes both down and rolls the lower half's loot once. Each half's `hitbox` is its model's own boxes with
`collidesByHitbox`, so the outline and the raycast hug the obelisk, the body steps onto the plinth and stops at the shaft
(`BuildingBlockTests.EveryShapeCollidingModelsBoxesHugItsParts`). The registry, the screens, the trip and the wire all key a
waystone by its lower cell: `WaystoneManager.TryGetWaystoneCell` maps either half to it (a right-click on either half opens it,
a break of either marks it lost once), and `WaystoneManager.IsWaystoneAt` holds only while both halves stand, so a lone half (a
blast's leftover, or a waystone saved before they were two cells tall: it reads as a remnant, lost to its discoverers, and
breaks like a door's orphan) is no waystone; `WaystoneArrival.StoneHeight` is where a traveler held on top stands. Worldgen-placed, and in the creative catalog (the lower half has an item, so a creative player places one anywhere and it works like a generated one; survival has no source for it)
Only a pickaxe breaks it and it crumbles into stone bricks (`loot/waystone.json`). `WaystoneManager` (`Simulation/Waystones/`, owned by `World` as
`World.Waystones`, authoritative worlds only, saved in `stations.json` as `Waystones`) keeps one `WaystoneRecord` per cell,
created on the first activation (`Discover`/`DiscoverFor`, `World.DiscoverWaystoneFor`): a random `WaystoneNames.Generate`
name and the activator as first discoverer (`WaystoneManager.DiscovererOf`: the name slug, or `@local` for the local
player), the only one who may rename it (`TryRename`/`TryRenameFor`, `World.RenameWaystoneFor`, sanitized to printable
ASCII within `WaystoneNames.MaxLength`). A break of either half (`BlockEditor.RemoveBlock`) or the sweep in `Update` (a blast
took either half) marks the record lost instead of forgetting it. Each player's discovered list is `Inventory.Waystones` (`DiscoveredWaystones`: cell plus
the name at discovery, with a `Version`), saved as `InventoryData.Waystones` (null in an older save) and carried by the
`InventoryState` snapshot; a server flags `InventoryChanged` when a discovery or rename changed it. `PlayerMana.Drain` and
`MoreThanHalf` are the teleport cost's halves. `WaystoneManagerTests`, `WaystoneNamesTests`, `WaystoneContentTests`.
Worldgen stands them only beside ruins, one realm template each (W-4, `WaystonePlacement`, see "Waystone satellites" under
"Structure templates and ruins").
**The waystone screen and the trip (W-5, singleplayer).** A right-click on a waystone (`BlockInteractions.TryOpenWaystone`,
`RightClickOutcome.Waystone`) reaches `App/WaystoneInput`, which discovers it (`World.DiscoverWaystoneFor`) and opens the
GL-free `WaystoneView` (`UI/Waystone/`, drawn by `WaystoneRenderer` in `UiPasses.RenderUI`, geometry and hit-tests in
`WaystoneLayout`): a waystone new to the player shows the **discovery popup** ("{name} has been discovered", CONFIRM, and
RENAME only for its first discoverer), RENAME opens the **rename dialog** (a `TextFieldState` capped at
`WaystoneNames.MaxLength`, Enter or RENAME saves through `World.RenameWaystoneFor`, Escape or CANCEL steps back to the popup),
and CONFIRM, or any later visit, shows the **teleport list**: the player's discovered waystones (`WaystoneRows.Build`: the
current one first and not selectable, then the standing ones nearest first, the lost ones last and greyed), each with its
realm tag (`WaystoneRealms.Classify`: hell under `HellTopY`, sky from `SkyIslandBottomY`, between them underground when it lies
`UndergroundDepthBlocks` under its column's surface from `ChunkManager.ColumnSource`) and its distance, a search field filtering
by name, a scroll list and a footer with the mana requirement (`WaystoneText.ManaLine`, red when unmet). The cursor is free and
the look frozen while it is up; E (when no field has focus) or Escape close it, and so do the pause menu, the death screen, a
world switch and leaving the waystone's reach or its block (`WaystoneTeleport.IsAtWaystone`, checked every frame). A row click
runs `World.TeleportViaWaystone`: `WaystoneTeleport.TryPay` (the pure rule: not the current waystone, discovered, a registry row
that is not lost, mana strictly over half unless creative or flying, then `PlayerMana.Drain`), and on success
`WaystoneTravel.Begin` moves the camera over the destination at once (so the streaming focus follows) and holds it there each
frame, like the spawn settle, until the destination is loaded and meshed, then lands it on `WaystoneArrival.Find`'s spot (pure
over an `IArrivalCells` seam, `ChunkArrivalCells` on the live chunks: the nearest free two-cell standing spot within
`SearchRadius` columns and `SearchHeight` cells, a level spot beating a climb, facing away; on top of the waystone when nothing is
free or after `StreamingConfig.WaystoneArrivalTimeoutSeconds`); a landing never bills a fall (`LocalPlayer.ResetFall`).
`WaystoneTeleportTests`, `WaystoneArrivalTests`, `WaystoneRealmsTests`, `WaystoneTravelTests`, `WaystoneViewTests`,
`WaystoneLayoutTests`; `visual-examples/waystones-ui.json` (`VOXEL_TIME=147`).

**Waystones on a server (W-6, protocol 24).** The server is authoritative and the screens stay the client's. A connected
client's right-click on a waystone only raises its usual `Use` intent (`WaystoneInput.OpenFromStation` does nothing then): the
server's replay claims `RightClickOutcome.Waystone` and `WaystoneSessions.OnActivated` (`OpenGLTest.Server`) discovers it for
that player (`World.DiscoverWaystoneFor` at `World.LastWaystoneCell`), remembers it as the waystone the player stands at
(`ServerPlayer.AtWaystone`), and answers with the player's `WaystoneRows` and a `WaystoneOpened` (cell, name, may rename, new
to the player), which the client's `WaystoneMirror` (`ClientSession.Waystones`) hands to `WaystoneInput.Update` to open the
popup or the list. Each player's rows (`WaystoneNetRow`: the registry row of every waystone it discovered, with whether it may
rename it) are resent by `WaystoneSessions.Tick` whenever `WaystoneManager.Version` or its `DiscoveredWaystones.Version` moved
(`ServerPlayer.WaystoneRowsSent`), so they go out at join and a rename or a loss reaches everyone who knows the waystone; the
list reads the mirror instead of the client's empty registry. RENAME sends a `WaystoneRename(cell, name)`, which
`World.RenameWaystoneFor` takes from the first discoverer only (sanitized), answered with the rows either way. A row click is
judged on the mirrored rows and pool first (`WaystoneTeleport.Judge`, so a refusal toasts its reason at once), then sent as a
`Waystone(cell)` world intent and the screen closes. The server (`WaystoneSessions.TripFor`; not operator gated, rate-limited,
editing no cell) refuses unless the player stands at the waystone it last activated (`IsAtWaystone` with its eye and
`World.InteractionReach`) and no trip is already waiting, then runs `WaystoneTeleport.TryPay` with the player's discovered list,
the registry row, `IsFree(creative, flying)` and its `PlayerHealth.Mana` (a spend marks `StatusDirty`, so the drained pool
reaches the client). An accepted trip moves the `ServerPlayer` over the destination (`WaystoneArrivalHold.HoldEye`, so its
streaming focus loads it) and issues a correction it never sends, so the client's states move nothing and are never judged
meanwhile; the held player is immune (`IsImmune`). Each pump `WaystoneArrivalHold.Step` retries `WaystoneArrival.Find` until no
cell it reads is unloaded (on top of the waystone past the timeout), then lands it: `ServerPlayer.Teleport` (the fall
forgotten), a fresh `PlayerCorrection` and the status. The client's `WaystoneMirror` remembers the trip's intent and, on the
correction that follows its accepted result, holds the camera there (`WaystoneTravel.BeginLanding`, facing away from the
waystone, ticked in `World.UpdateClient`) until its own streamed chunk there is meshed. `/tp` ends a waiting trip.
`WaystoneServerTests` (loopback), `WaystoneNetCodecTests`, `WaystoneArrivalHoldTests`.
