# Plan: Sandworm boss (desert)

> Guide: plans/README.md

## Status
Last updated 2026-11-07. Phase 1 is shipped: `tools/generate-sandworm-model.py` (model with Head/Jaw/Body/Tail groups, skin, fang sprite), `sandworm` entity/loot/`sandworm_fang` content, `SandwormConfig`, `SandwormChain` (follow-the-leader with a bend limit) and `SandwormBrain` (thump, dig, breach, air, dash, retreat) under `src/Entities/Sandworm/`, with tests. Phase 2 is shipped: `Sandworm : Mob` (head is the position, exposed segments by the sand surface), `SandwormManager` (one at a time, hunts desert targets, chomp and dash contact, loot, retreat), `SandwormGround`, the `Mob.OverlapsBox`/`AimPointFrom`/`ScaleDamage` hooks used by projectiles, melee and `MobStrikes`, and the World/MobDirector/WorldPersistence wiring. Known stopgaps until phase 5: the server broadcasts a worm as a goblin, and no client mirror exists. Phase 3 is shipped: `use.action: "summon"` (`use.entity`, `SummonItem`, `IItemUseWorld.SummonBoss`, `World.CanSummonBoss`/`TrySummonBoss`), `SandwormManager.CanSummonAt`/`Summon`, the `thumper` item and recipe (sprite from `tools/generate-sandworm-model.py`). Phase 4 is shipped: per-segment draws and shadows (`SandwormPose`, `MobPasses.RenderSandworms`), the jaw, hidden healthbar under the sand (`Mob.ShowsHealthbar`), and the cosmetic show (`SandwormEffects`/`SandwormEffectRules`: thump rumble, surfacing sand telegraph, breach burst and boom, dive splash, dash dust, the earthquake only while digging). `visual-examples/sandworm.json` films it. Phase 5 is shipped: protocol 23 (`NetEntityKind.Sandworm` for the head with its phase in the ammo field, `SandwormTarget` for the point it will surface at), the client mirror (`Sandworm.IsMirror`, dead-reckoned head, chain dragged locally, `SandwormManager.SpawnMirror`/`AnimateMirrors`), and saves (a restored worm resumes hunting). The body is deliberately not on the wire: the client drags its own chain after the head, so a late joiner sees a straight body until the head has travelled a body length. Phase 6 remains: the item debugger entry, AGENTS.md documentation and a balance pass.

## Context
A giant, chained, multi-segment sandworm boss for the desert. It is summoned (never spawns naturally), burrows *virtually* (no block edits), erupts under players to chomp and launch them, and can slide across the surface in a slow-turning dash. This plan file becomes `plans/SANDWORM.md` (plan-docs layout, row added to `plans/README.md`) when implementation starts; the work is then done one phase per prompt.

## Decisions (from the grilling)
- **Burrow**: virtual tunnel only. Terrain is never edited, so there are no edit-path hooks, saves, LOD or journal cost. Sand mound and particles sell it.
- **Body**: 14 rigid segments (head with fangs, 12 body, tail), about 3 blocks thick at the head tapering to the tail, about 35-40 blocks long. One hitbox per segment, one shared health pool (about 500 HP; head hits full damage, body hits reduced). Follow-the-leader chain: every segment is held a fixed link distance from the one ahead, so moving any segment tugs the rest.
- **Summon**: craftable item (the "thumper") right-clicked on sand/sandstone in the desert biome. A thump phase (a few seconds of rumbling and shake) precedes the eruption. One worm at a time. No altar or worldgen.
- **Netcode**: server-authoritative from the start. Only the head rides `EntityList`; clients drag their own chain after it. Saved in `entities.json`.
- **Combat**: chomp about 30 contact damage plus an upward launch (fall damage follows from the existing fall tracker). Dash deals less but is wide and constant. Creative, flying and dead players are immune (existing `IMobTarget` rules). The worm only burrows where the column surface is sand or sandstone; it retreats and despawns if no target stays in the desert for a while.
- **Loot**: a boss table (a fang item plus a crafting material). Strong but killable with iron-tier gear.

## Behavior (state machine, GL-free and tested)
States in `SandwormBrain` (pure, driven by dt, target, ground info):
1. **Thump**: summon item placed; shake and sand puffs at the spot; worm is underground and out of view.
2. **Dig** (burrowed hunt): the head travels underground toward the nearest non-immune target beneath the surface, at about burrow depth under the sand. The body follows through the chain. Earthquake shake scales with proximity and only applies in this state. Dust puffs rise around the *surfacing point* with count and intensity growing as the head approaches the surface (telegraph for dodging).
3. **Breach**: head surfaces under the target's position (sampled early, with a short lock-in so strafing dodges work). Contact box on the head deals chomp damage and a launch impulse via `IMobTarget.TakeHit(damage, away, speed, lift)`. Segments arc through the air behind it.
4. **Air**: partially airborne, steering toward the nearest target. At the apex it decides (weighted, avoiding repeating the same pick too often):
   - **Re-burrow**: dive straight down into the sand, then Dig again (restart the dig+launch loop), or
   - **Slide**: land flat on the surface, half the body in the ground and half above (clipped at the sand line), and enter Dash.
5. **Dash**: charges at the nearest target along the surface, turn rate deliberately low so it can be dodged. Ends on a range/time cap or when it runs out of sand; then it burrows (back to Dig).
6. **Retreat/Despawn**: no target in the desert for N seconds, or death (death animation: segments collapse, loot via the manager).

Chain physics (`SandwormChain`, GL-free): positions array `P[0..13]`; each update the head moves, then for i from 1: `P[i] = P[i-1] + normalize(P[i]-P[i-1]) * link(i)` (FABRIK-style follow). A gentle bend-angle limit and gravity on segments above the surface sell the weight. Segments below the sand line are skipped by rendering and by hit tests. Fully deterministic given head path.

## Implementation phases (one phase per prompt; each updates plans/SANDWORM.md)
6. **Tooling and verification**: item debugger entity factory entry (`EntityPreviewState`, modes per clip), `visual-examples/sandworm.json` (`VOXEL_TIME=147`: thump, dig telegraph, breach, air, re-burrow, slide/dash, death) and balance pass.

## Critical files
`OpenGLTest/src/Entities/Mob.cs`, `Entities/ThrownProjectile.cs`, `Interaction/LeftClickAction.cs`, `Interaction/MobStrikes.cs`, `Simulation/SkyMobManager.cs` (template), `Simulation/MobDirector.cs`, `World.cs`, `Saves/WorldPersistence.cs`, `Saves/EntitySaveData.cs`, `Networking/NetEntity.cs`, `Networking/NetEntityCodec.cs`, `Networking/NetProtocol.cs`, `Networking/Client/ClientMirror.cs`, `OpenGLTest.Server/ServerBroadcasts.cs`, `Rendering/Actors/MobPasses.cs`, `Rendering/ShadowPass.cs`, `Particles/BlockDebrisSystem.cs`, `Particles/AmbientEffects.cs`, `Juice/ShakeFalloff.cs`, `Items/ItemFactory.cs`, `Content/ItemLoader.cs`, `Simulation/MobSpawner.cs`, `OpenGLTest.ItemDebugger/EntityPreviewState.cs`.

## Open items to settle during phase 1-2
- Exact segment count/link lengths and HP split, damage numbers (tuning goes in config).
- Whether a segment-aware hit API belongs on `Mob` (preferred) or a worm-specific path.
- Mound visual: pure particles first; a temporary mesh only if particles read poorly.
- Content id budget: about 80 ids left; this adds a few items and zero blocks.

## Verification
- `dotnet test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release` (chain, brain, manager, codecs, loopback) with `DOTNET_ROLL_FORWARD=LatestMajor` as needed.
- Item debugger ENTITIES tab for model/clips; `visual-examples/sandworm.json` for the full encounter; run a dedicated server plus a client to confirm segments, shake and dodge feel; reload a save mid-fight.
