# Sandworm: the desert boss

> Guide: plans/README.md

## Status

All phases shipped: the model, content and chain/brain (`SandwormChain`, `SandwormBrain`), the mob, manager and
segment-aware hit testing, the thumper summon item, rendering and the sand/boom/earthquake effects, the client mirror,
protocol 23 and saves, and the scenario. How it works lives in AGENTS.md "Sandworm"; this file keeps only the
decisions and what is left for later.
Last updated 2026-11-07.

## In progress

Nothing.

## Open

- [ ] **`SW-1` · Play-tested balance** — P2, S.
  Every number in `SandwormConfig` is a first guess from the scenario (300 hp, 30 chomp damage, the dash turn rate and
  speed, the telegraph range, how long it stays exposed). Play a few fights with iron gear and retune.
- [ ] **`SW-2` · A real death** — P3, S.
  The worm only shrinks and sinks. A collapse (segments falling in turn, a long sand burst) would sell the kill.
- [ ] **`SW-3` · Body on the wire** — P3, M.
  Only the head is sent; a client drags its own chain, so a late joiner sees a straight body for a body length. Sending
  the segment positions (extra entries) would fix it at a protocol bump.
- [ ] **`SW-5` · Hurts more than players** — P3, M.
  The worm bites players only: mobs, blocks and built structures in its path are untouched (a deliberate first scope).

## Reference

Decisions made while planning:

- **Virtual burrow, no block edits.** Terrain is never touched, so no edit-path hook, save, LOD or journal cost; sand
  particles and a boom sell it.
- **Chain of 14 rigid segments, one health pool.** Head hits full damage, body hits `BodyDamageShare` of it; a segment
  is hittable when a quarter of it is above the sand.
- **Summoned, never spawned.** The thumper on desert sand, one worm at a time, no altar or worldgen.
- **Server-authoritative from the start**, saved in `entities.json`, mirrored on clients.
- **Chomp** about 30 damage plus an upward launch (the fall tracker bills the landing); creative, flying and dead players
  are immune like any mob's target.
