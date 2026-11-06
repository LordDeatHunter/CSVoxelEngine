# Visual polish plan

## Status

Tracks particles, animations and other non-gameplay feel work. Last updated 2026-11-06. Nothing in progress. The player and tools section has landed and been filmed (V-1 axe side swing with its chips, canopy shake and the permanent eat-away of a natural trunk's hit face, V-2, V-3, V-13 flight, V-14 swim legs, V-15 bow, V-16 spear). V-6 landed and filmed; V-11 and V-12 landed, not yet filmed: no scenario op opens the [E] panel or drops items. Open follow-ups: rain should call `RippleSystem.Spawn` per drop that lands on water (for V-5); a remote player shows neither another player's axe chop nor bow draw (neither is on the wire); a thrown spear does not stick in the block it hits and the trunk itself never shudders (only its leaves shake).

## In progress

(none)

## Open

### World ambience

- [ ] **`V-4` Fireflies and pollen motes** - P2, M. Fireflies in the forest at night, pollen and dust drifting by day, carried by the wind. **Hook**: `Wind.cs`, a `FireParticleSystem`-style system owned by `AmbientEffects`.
- [ ] **`V-5` Rain and snow weather** - P2, XL. Precipitation particles plus a wet-surface look. **Hook**: `FrameLighting.WindDirection` (already reserved for this), a new weather clock.
- [ ] **`V-7` Footprints in snow and sand** - P3, L. Fading decals from the player and mobs.

### Combat and mobs

- [ ] **`V-8` Slime gel trail and drips** - P3, M. Scaled by slime size. **Hook**: `Slime` hop landing, `LandingDust.SplatCount`.
- [ ] **`V-9` Goblin idle fidgets** - P3, M. Scratch and look-around clips while unaware. **Hook**: `tools/generate-goblin-clips.py` (optional clips like `hurt`).
- [ ] **`V-10` Projectile trails** - P3, S. A faint streak behind goblin stones and player throws. **Hook**: `ThrownProjectile`, `FireParticleKind`.

## Reference

- Iterate on animations in the item debugger first (`item-debugger` skill), then film with a `VOXEL_SCRIPT` scenario (`visual-verify` skill).
- Cosmetic effects never run on a headless server; a client must reproduce them from mirrored state (see "Animation juice" in `AGENTS.md`).
- Time-driven curves belong in `MathUtils`, not inline.
