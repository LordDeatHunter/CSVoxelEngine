---
name: visual-verify
description: Drive and screenshot the CSVoxelEngine game with VOXEL_SCRIPT scenarios to verify a visible change (rendering, worldgen, entities, trees, water, lighting). Covers env vars, run recipe, pitfalls and which scenario to use.
---

# Visual verification

**Animations, held items and models: start in the item debugger, not a scenario** - see the `item-debugger`
skill (modes, timeline, env vars, how to tune a pose from the sliders). Use a `VOXEL_SCRIPT` scenario
(below) only to see them in the real world, with the camera, terrain and lighting around them. When you add
or change an animation, give it a debugger mode.

Build/test cannot prove a rendering or feel change. Use a scripted scenario, not ad-hoc input injection.

## Run recipe (PowerShell, Release)
```
~/.dotnet/dotnet.exe build OpenGLTest.sln -c Release
$env:VOXEL_SCRIPT = "<abs path to scenario.json>"   # e.g. OpenGLTest/visual-examples/water.json
$env:VOXEL_TIME = "147"                              # optional, see table
Start-Process OpenGLTest/bin/Release/net9.0/silkgltriangle.exe -WorkingDirectory OpenGLTest `
  -RedirectStandardOutput <scratchpad>/run.log
```
- The working directory MUST contain `assets/` (`OpenGLTest/` or the output dir). Shaders load from `OpenGLTest/assets/shaders` source, not bin.
- A scenario saves numbered PNGs to `visual/<name>/` (gitignored; `"output"` overrides the folder; an absolute scratchpad path works). The window may not close itself when done: sleep, then `Stop-Process silkgltriangle`.
- Do not run `dotnet build` while an instance is running (locked exe).
- Read the PNGs with the Read tool and say what you actually saw. Never claim a visual result you did not look at.
- Copy screenshot folders into the scratchpad instead of deleting them in-repo (the PowerShell layer blocks recursive deletes on repo paths).

## Env vars
`VOXEL_SCRIPT` (scenario), `VOXEL_SPAWN=x,y,z`, `VOXEL_LOOK=yaw,pitch` (yaw 0 = +X, default -90 = -Z), `VOXEL_TIME=<s>` (added to the lighting clock), `VOXEL_DEBUG=1` (FPS + profiler lines in the log). SCRIPT/SPAWN/LOOK skip the menu and play a storeless world; TIME alone still boots to the menu.

| VOXEL_TIME | look |
|---|---|
| 147 | noon (default for most scenarios) |
| 360 | water.json |
| 440 | dusk |
| 700 | night |

## Scenario ops
`teleport`, `look`, `wait`, `walk`, `mine`, `place`, `fly`, `give`, `spawn`, `grow`, `screenshot` (`hideHud` optional). Parsing is covered by `VisualScenarioTests`. Existing scenarios live in `OpenGLTest/visual-examples/` (basic, water, flow-water, ponds, lava, trees, goblins, grass-tint, ores, sky, clouds, cloud-shadows, linear-lighting, coloured-lights, night-lights, lod-horizon, cold, biomes, particles, ...). **Extend or copy a matching one before writing a new one.**

## Pitfalls (all hit before)
- Early waits: at `wait 5` the spawn is still embedded in unloaded terrain, so every `place` misses and shots are black. Wait ~12 s, `teleport` to a known open spot (`[0.5, 5.0, 0.5]`), wait ~2 s, then act.
- Gravity keeps acting after `teleport` (34 b/s^2). After an airborne teleport keep waits ~0.3 s, or `fly` first (`"op":"fly"`) to hover; hovering also makes the camera immune to mobs.
- `give {block, count}` selects the hotbar slot so `place` works without scrolling.
- `spawn` entity names and `grow` species are checked only when the step runs.
- Water spots (seed 1337): bay at `[228.5, 10.5, -138.5]` yaw 0 pitch -22; submerged at `[250.5, -3.5, -150.5]`.
- FPS: measure only after `chunks N/M` stops changing; see the `perf-capture` skill.
- Temporary debug output: tag it `// TEMP-DIAG` and verify `grep -rn TEMP-DIAG OpenGLTest/src` is empty before finishing; back up and restore any shader you patch (`git diff` must end clean).

## Reporting
State which scenario and VOXEL_TIME ran, which PNGs you read, and what they showed (including anything wrong).
