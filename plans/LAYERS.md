# Layers: Hell below, Sky above

> Guide: plans/README.md

## Status

All phases shipped (`L-1` to `L-17`): the barrier band, hell's cavern layers with their biomes, flora and
mobs, the solid clouds, the cloud sea, the sky islands with their biomes, flora and mobs, flight in the shared
mob layer, the netcode and the scenarios. How it all works lives in AGENTS.md "Layers: hell below, sky above"
and the sections it points to; this file keeps only the decisions and the ideas left for later.
Last updated 2026-11-07.

## Reference

### Decisions

- **Travel:** dig or build only. Barrier needs the iron pickaxe; the sky has no gate (skill
  progression through gaps and harder mobs).
- **LOD:** simplest possible for now (realms exist only in full-detail chunks, islands pop in).
  The real fix is `M-29` in `TODO.md`.
- **Hell light:** dark, lit by lava and emissive blocks, red fog.
- **Fall rules:** hell has no bottom, normal fall damage. Sky islands: just fall. The solid cloud
  layer negates fall damage and sinks the player to 50 %.

### Future ideas (discuss later)

Ores, structures/fortresses, loot, armor and tool tiers, iron smelting recipes (needs the iron
pickaxe recipe), heat, low gravity and void hazards, other realm mechanics, portals or natural
passages, a bottom to hell or deeper themes.
