# Waystones

> Guide: plans/README.md

## Status

Port of the Fabric Waystones mod (`C:\Users\DH\projects\fabric\FabricWaystones`): rare natural waystone structures beside
ruins in all four realms, discovered by right-click, teleporting between discovered ones for mana, in singleplayer and on
a server (protocol 24). W-1..W-7 are shipped; the design lives in `AGENTS.md` ("Waystones", "The waystone screen and the
trip", "Waystones on a server", "Waystone satellites") and the player guide in `wiki/game/waystones.md`. What is left
here is future work. Last updated 2026-11-08 (the two-cell waystone, its model hitboxes and the 5x5 pavilion).

## In progress

Nothing.

## Open

### Future (not planned now)

- [ ] **`W-8` Craftable waystones** - P3, M. A recipe, `hasItem`, ownership and a random name on first use; the blocks
  then drop themselves instead of `loot/waystone.json`. **Hook:** `assets/data/blocks/waystone_*.json`,
  `WaystoneManager.Discover`, `WaystoneContentTests`.
- [ ] **`W-9` Dungeons as a proximity POI** - P3, S. Raise the satellite chance near dungeons once the engine has them.
  **Hook:** `WaystonePlacement.TryRollSatellite`.
- [ ] **`W-10` Portable pocket-wormhole item** - P3, M. Open the teleport list away from a waystone. **Hook:**
  `WaystoneTeleport.IsAtWaystone` (the "from" rule), `WaystoneSessions.TripFor`, `WaystoneInput`.
- [ ] **`W-11` Global waystones, owner rename, break permissions, cooldowns, forget entry** - P3, M. **Hook:**
  `WaystoneRecord`, `WaystoneManager.MayRename`, `DiscoveredWaystones`.
- [ ] **`W-12` A refused trip's reason on the wire** - P3, S. The client pre-judges with the same rule, so only a race
  gets the generic `WaystoneText.TripRefused` toast. **Hook:** `NetMessage.IntentResult` or a waystone message.
- [ ] **`W-13` Hide a held traveler from the player list** - P3, S. Other players see a traveler jump to the destination
  at once while the server still holds it over an unloaded destination. **Hook:** `ServerPlayer.WaystoneTrip`,
  `PlayerSessions` (the player list).

## Reference

### Decisions (settled)

- Natural waystones only for now (W-8 is the craftable follow-up).
- Teleport needs mana strictly above 50% of max (`PlayerMana.MoreThanHalf`, on the whole-unit `NetMana` the server uses)
  and drains it to 0; creative and flying players are free.
- Structures in all four realms (surface, underground, hell, sky), each with its own layout, generated only beside ruins
  (no stand-alone waystones). The underground one is a sealed chamber; the player digs in.
- Any distance, any realm; the player must stand at a waystone. Arrival is beside the destination on the nearest free
  two-cell standing spot, facing away.
- Names are the mod's algorithm (4 x consonant + vowel, first letter capitalized, e.g. "Kafuziro"; no easter egg), kept in
  a world-level registry. Only the first player ever to activate a waystone may rename it; the discoverer id is the
  player name slug (`@local` in singleplayer).
- Mineable; a broken waystone stays in its discoverers' lists grayed out as "lost" and cannot be traveled to.
- A waystone is two cells tall (after the first in-game review): `waystone_<realm>` is the lower half and the registry cell,
  `waystone_<realm>_upper` its `upperHalf` (a `TallBlock`, like the door, with no item on either half). Either half opens it
  and breaking either takes both down; it stands only while both halves do. Each half collides by its model's own boxes.
- The surface pavilion is 5x5 and ten layers (it was 7x7 and nine); the other three layouts only made room for the taller
  stone (the crypt gained a layer, the ember ring and the sky arch hold the upper half where there was air).

### Deviations from the original plan

- "Drops itself" is impossible without an item (a drop of a `hasItem: false` block can never be picked up), so all four
  blocks roll `loot/waystone.json` (stone bricks); W-8 gives them an item and a self drop.
- Placement takes several tries (`WaystoneSiteTries`): one roll rarely found a site in hell or on an island. A hell
  satellite scans its pocket from the top and is level within its `groundLayer` (a foundation of that depth), since
  strictly level 7x7 hell floors are rare; a sky satellite's distance is rolled across its island's room; an underground
  chamber's depth is hashed independently of its ruin's, so it is usually tens of blocks above or below it.
- The realm tag reads the column's surface from `ChunkManager.ColumnSource` (the Y bands alone cannot tell surface from
  underground); the list footer states the requirement and whether it is met; a waystone claims the right-click unless
  sneaking or holding a block.
- Underground rooms and their chambers are stamped after the cave life (W-7 found a glow mushroom inside a crypt);
  `WaystonePlacementTests.NoLaterFeatureGrowsInsideAGeneratedWaystoneChamber` holds it.

### Measured (default seed, 3600 ruin cells a kind, 2026-11-07, with the old 7x7 pavilion)

Satellites beside 24% of surface ruins (60 of 246), 27% of underground rooms (292 of 1095), ~35% of hell ruins (7 of 19 over
four layers; hell ruins are rare, ~5 per layer per 3600 cells) and 11% of sky ruins (176 of 1653); mean center distance
20-22 blocks, 10 on islands. `WaystoneSatelliteChance` 0.25 stays; revisit hell and sky if they feel too rare in play.
The nearest of each realm (rechecked after the 5x5 pavilion and the two-cell stone: unchanged), filmed by
`visual-examples/waystones.json`: the pavilion at (29, 12, 58), the crypt at
(-204, -162, 122), the sky arch at (-130, 380, -35) and the ember ring at (-2607, -346, -411) (hell layer 0).

### Known gaps

- A world saved while waystones were one cell tall keeps them as lone lower halves wherever its chunks were saved: no
  waystone any more (the sweep marks a discovered one lost) and broken like a door's orphan. Unsaved chunks regenerate whole.

- The discovery popup's own name is set optimistically on a connected rename (the rows bring the truth to the list).
- The list's realm tags are cached with its rows, so a tag read before a streamed column arrived (sea level stands in
  for the unknown surface) stays until the next registry or list change.

### How the mod works

- Name: `Utils.generateUniqueId()`: 4 x (consonant, vowel), 21 consonants, 5 vowels, first letter capitalized.
- Each player has a set of discovered waystone ids; a server-wide registry holds name and owner; the cost is a level,
  health or item price (replaced here by mana).
- Seven structure variants, all one open pavilion (about 5x5 plinth, four pillars, a 3-step roof, the waystone in the
  middle) in different materials and rune colors; villages get one through a template pool. We use distinct layouts per
  realm instead and place by ruin.

### Verify

`dotnet test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release` (names, mana rule, registry round trip, stamp gates,
site rules, intent rules, loopback), `visual-examples/waystones.json` and `waystones-ui.json` at `VOXEL_TIME=147`; then a
Release run: teleport across realms above and below 50% mana, mine a waystone, repeat on the dedicated server with two
clients.
