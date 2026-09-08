# XS — finish what's open (< 1 session each)
1. Tick off PERF_TODO 8 + 10. Measure the async edit-remesh + blast/fell visual check, then SustainedLowLatency + TieredPGO + real delta time for chunk.lifetime. Cheapest FPS left.
2. JSON-only content drop. New blocks need zero C# — ores, flowers/tall grass (cutout + needsSupport), torches (light.emission). Big visual payoff, tests already cover the pipeline.
3. Clouds + stars/moon. One translucent plane + points, noise offset by time. Same trick as the existing sun disc.

# S — quality of life (1 session each)
4. Pause menu / help / coords HUD. Esc currently quits, which is hostile for testing. Pause + key list + F3-style XYZ/biome readout via existing UI renderer.
5. Sound via Silk.NET audio. Break/place pop, splash, footsteps. No sound system exists at all, so even 5 synthesized blips feel huge.
6. Torch light + more point lights. Shader already supports 8 point lights — wire placed torches into it.

# M — real features (a few sessions)
 7. Save/load edits. Biggest missing QoL — all mining/building is lost on quit. Region files or SQLite, seed + deltas only, worldgen stays pure.
 8. Biomes. Desert/snow/swamp as 2D noise masks in TerrainGenerator — sand/snow tops, swamp water color, LOD already reads the same heights.
 9. Underground ores + surface structures. Deterministic placement like trees/pebbles: coal/iron veins, tiny huts, mineshaft scraps. Rolls through existing DropManager.
10. Weather: rain/snow. Particle pass + darkening + splash sound. Plays well with the day/night cycle.
11. Crafting + furnace + inventory UI. Drops and slots exist, no crafting. Wood → planks → tools, furnace = timed loot roll.

# L — project-scale
12. Mobs. Wandering animals by day, hostiles at night, simple voxel pathing, loot-table drops. Needs AI + spawning + health + entity rendering — the largest single gameplay gap.
13. Multiplayer (headless server + chunk packets). Threading model already isolates sim from GL, so a headless World without RenderManager is plausible — but auth, prediction, and edit conflicts make this weeks.

# XL — engine bets
14. Redstone-ish wiring / pistons / minecarts. Scheduled-tick logic on top of FluidManager's pattern, plus moving collision.
15. Second dimension (Nether-like) + portals. Separate seed/noise set, same chunk pipeline, new LOD palette.
