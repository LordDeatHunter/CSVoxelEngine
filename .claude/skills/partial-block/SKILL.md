---
name: partial-block
description: Checklist and traps for adding a block that is not a full cube in CSVoxelEngine (a stump, slab, hollow log, mold, anvil): model, hitbox, the mining pop and crack overlay, selection, AO and light, drops, and block-entity state riding on it. Use when adding any low, thin or multi-box block, or when such a block looks wrong while being hit, targeted or lit.
---

# Partial blocks (anything that is not a full cube)

The reference implementation is the **tree stump** (`stump.json` model, `<species>_stump.json` blocks, `TreeStump`, `StumpManager`); the older ones are `oak_planks_slab`, `hollow_oak_log` and the molds. Copy their JSON before inventing fields.

## Block JSON

- **Model stays inside its cell** (`from`/`to` 0..16). A "wider than a block" look is impossible; flare inside the cell instead.
- **Textures are cropped, never stretched**: a partial face samples the lower part of its tile. Name every face's texture explicitly.
- **`hitbox` must match the model** in cell-local 0..1 (a 5 px stump is `max y 0.3125`). The outline, the raycast and `collidesByHitbox` all read it. Several boxes merge into one outline.
- Set **`castsAO: false`** (a low block must not darken the floor around it) and keep `light.passes` at its default `true`.
- **`hasItem: false`** for a worldgen- or event-made block, and give it a **`loot/<block name>.json`** table (or `loot: none`); a block without one drops itself, which has no item. Use `pickAs` when a middle-click should pick another block.
- A block that follows the state byte (`facing`) needs its model and hitbox authored facing north; see `hollow_oak_log.json`.

## The traps that bit us

1. **Selection goes through the hitbox, so the ray misses a low block aimed at its cell middle** and hits whatever is behind it. Aim tests and scenarios at the block's *top* (`cell + (0.5, hitboxTop - 0.1, 0.5)`), never `Front + 0.5`.
2. **Tall grass is targetable.** A click on a stump through grass hits the grass cell. A scenario must aim from above (`pitch` about -60 or steeper) or clear the grass.
3. **The block-hit pop and the crack overlay are drawn by code, not the chunk mesh** (`PopBoxEmitter`, `SelectionPasses.RenderBlockPop`). With no model passed the pop draws each *hitbox* as a cube wearing `FaceTiles` (the first quad's tile per face), so a multi-element model pops as one slab with the wrong tile on its sides. A stump or other multi-box block must return its model from `WorldRenderer.FacingModelAt` (it does for `IsStump` and `HasHorizontalFacing`). If you add another partial block family, extend that one method and never a second path.
4. **The pop grows the model about its own bounds** (`PopBoxEmitter.EmitModel`). Growing about the cell center sinks a low block's top under the still-drawn chunk mesh, so only the sides seem to pop. Do not reintroduce a fixed `(0.5, 0.5, 0.5)` center.
5. **Check the pop with a survival scenario** (`visual-examples/stump-hit.json`): `creative` off, a `mine` of about 0.35 s, one `screenshot`. A creative `mine` breaks the block at once and shows no pop.
6. **Block-entity state keyed by the cell** (a log on a stump, a quern's top) must be swept when the block stops being that block (`StumpManager.Update`), and must ride the station snapshot and `stations.json`; see `StumpManager` for the full set (`StationNetCodec`, `StationSaveData`, `WorldPersistence`, a `NetFraming.ProtocolVersion` bump).
7. **Replacing a cell during a batch edit** (felling, blasts) goes through `Chunk.ReplaceBlockAt`/`ClearBlockAt` and the caller remeshes; run the `edit-path-checklist` hooks.

## Verify

`visual-verify` skill: a scenario that films the block at rest, being mined in survival (the pop) and in whatever interaction it has. Placeholder tiles go in `USER_TODO.md`.
