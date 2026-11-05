# Visual polish plan

## Status

Tracks particles, animations and other non-gameplay feel work. Last updated 2026-11-05. Nothing in progress (V-6 landed and filmed; V-11 and V-12 landed, not yet filmed: no scenario op opens the [E] panel or drops items). V-6 follow-up for V-5: rain should call `RippleSystem.Spawn` per drop that lands on water.

## In progress

(none)

## Open

### Player and tools

- [ ] **`V-1` Axe tree-chopping animation** - P1, L.
  An overhead chop for axes, with feedback on the tree itself:
  - Player swing: a chop mode for axe items, reusing `SwordMotion.Shape` and `BladeSwingFrame`, with a matching mode in `PlayerPreviewState`.
  - Wood chips on every hit.
  - Tree shudder with a leaf burst per hit.
  - A **progressive notch** carved into the trunk as damage builds, before the tree topples.

  **Hook**: `BlockDebrisSystem` (chips), `LeafParticleSystem` (leaves), `SelectionPasses.RenderBlockBreaking` (nearest hook for the crack; the notch needs a wood-cell mesh variant in `TreeWoodEmitter`), `TopplingTree` (the fell).
  **Verify**: item debugger `ITEM_DEBUGGER_PLAYER_ANIM=chop`; a new `visual-examples/animations-axe.json` (`VOXEL_TIME=147`).
- [ ] **`V-2` Dust puff on block place** - P2, S. A small puff tinted by the placed block. **Hook**: `BlockDebrisSystem.Puff`, `BlockPlacer`.
- [ ] **`V-3` Block-break camera nudge** - P2, S. A tiny camera kick on heavy hits (a chop, an ore break). **Hook**: `HeadMotion` hit kick.

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
