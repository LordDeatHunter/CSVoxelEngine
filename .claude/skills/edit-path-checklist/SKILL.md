---
name: edit-path-checklist
description: Checklist of the hooks every block-edit path in CSVoxelEngine must call (fluid, leaf decay, gravity, LOD, canopy, saves, networking). Use when adding or changing any code that writes, removes, replaces or converts blocks, and when reviewing such a change.
---

# Block edit path checklist

Chunks are edited by many paths: mining, placement, TNT, felling, fluids/buckets, tree growth, leaf decay, multiblock convert/revert, network apply. Each must tell the side systems, or the world silently goes stale (water never flows, a canopy hangs forever, the LOD shows a buried shell). Work through this for **every** new or changed write path.

Existing call sites to mirror: `World.RemoveBlock`, `World.FellWood`, `World.TryPlaceBlockFromRaycast`, `World.PlaceBlockAtFace`, the `IItemUseWorld` bucket/TNT wrappers, `TntManager.Detonate`, `TreeGrowthWriter`, `FluidManager.SetCellImmediate`, and on a server `World.BreakBlockFor` / `World.TryPlaceSelectedBlockAt` (a client intent replayed).

## Always
- [ ] **Write through the raw writers.** `Chunk.SetBlockRaw` / `RemoveBlockAt` / `ClearBlockAt` set `ModifiedSinceSave`; a path that bypasses them is not saved. Every write that is not an owner of the state byte **zeroes it** (stale level/count/offset/thickness/hydration must never outlive the block). `PlaceBlockAt` with default state 0 also treats a fluid cell as replaceable.
- [ ] **Remesh the right chunks.** Border edits also dirty the face-adjacent neighbours. Single click edit: `ChunkManager.ImmediateRemesh*`. Batches (fell, blast, multiblock convert/revert): `Chunk.RemeshAfterBatchEdit` / `MarkDirty`, one immediate remesh on the clicked cell at most. Never a loop of immediate remeshes, never `MarkAreaDirty` for a fluid-only change (fluids pass light, cast no AO).
- [ ] **No GL calls off the main thread.**

## Side systems
- [ ] `FluidManager.ScheduleAround(cell)` (or `ScheduleBox(min, max)` for a batch), or water beside the edit never notices.
- [ ] `LeafDecayManager.ScheduleAround(cell)` / `ScheduleBox` next to it. It grows the box by `ScheduleReach` itself; callers pass only edited cells.
- [ ] **Gravity**: anything that can unsupport a `falls` block calls `FallingBlockManager.TryTopple` above it (`CheckColumn` bottom-up for batches). Fluids never count as support, so fluid edits need no hook.
- [ ] **Needs-support** blocks above a removed cell pop through `RemoveBlock`.
- [ ] **Far LOD**: removals call `LodManager.MarkEditedColumn` and cap the column (`LodRegion.LowerColumnBelow`, via `NotifyLodEdit`); placements need nothing.
- [ ] **Trees**: touching wood or leaves calls `TreeGenerator.PoisonCanopy(x, z, ...)` plus `MarkEditedColumn`.
- [ ] **Station block entities** (campfire, quern, kiln, casting): `NotifyBlockRemoved` when their block goes. Multiblocks: mining any of the 8 cells reverts the rest; a blast path relies on the `KilnManager.Update` integrity sweep.
- [ ] **TNT blasts skip fluid cells.** Mining never removes a fluid (only buckets do).
- [ ] **Loot**: drops roll through `DropManager.RollLoot` / `SpawnLoot` with the cell's state byte in `LootContext.State`, never a hand-built drop.
- [ ] **Multiplayer**: a client never writes a cell for real. A player-driven edit on a client is a prediction: it reports each changed cell through `World.BlockEdited` (staged in `ClientPrediction`) and then raises the action as a `World.IntentRaised` intent, which the server replays for that player through the same path with the player's `PlayerContext` (never the camera). A client world does not fell, topple, prime TNT, roll loot, hand back station contents or simulate fluids/decay itself (guarded by `_clientMode`). Server-side changes reach clients by journaling raw writes (`Chunk.CellWritten`), so raw writers are the path to use; a new player action needs an intent kind and a server handler in `GameServer.ApplyWorldIntent`.
- [ ] **Unloaded chunks**: read cells with `TryGetCell`/`ChunkAt`, never `GetBlockGlobal`, which reports unloaded chunks as air. An unloaded read freezes a simulation; it never decays or mangles.

## Reviewer use
For each write path in the diff, tick every row or write why it does not apply. A missing row is a blocker, not a nit.
