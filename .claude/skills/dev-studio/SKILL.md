---
name: dev-studio
description: Preview and debug CSVoxelEngine's player animations, held items, entity (goblin, slime) models and clips, and tree species with the DevStudio tool - run recipe, every env var for scripted screenshots, the timeline (progress, speed, loop), how to tune a pose from its sliders, and how to add a mode for a new animation. Use for any animation, held-item or model work before a world scenario.
---

# DevStudio

`OpenGLTest.DevStudio` draws the real `PlayerModel`, `EntityAnimator`/`Goblin`/`Slime` and
`CellBatchMesher` meshes with the game's shader, without a world: orbit camera, a mode button per
animation, a timeline to scrub. **For any animation, held-item or model change look here first**; use a
`VOXEL_SCRIPT` scenario (`visual-verify` skill) only to see it in the real world with terrain, camera and
lighting around it. Full tab, mode and `DEV_STUDIO_*` reference: `docs/agents/dev-tools.md`.

## Run recipe (working directory `OpenGLTest/`, Release)
```
dotnet build OpenGLTest.DevStudio/DevStudio.csproj -c Release            # from the repo root, once
cd OpenGLTest
DEV_STUDIO_SELECT=obsidian_dagger DEV_STUDIO_PLAYER_ANIM=slice \
DEV_STUDIO_SHOT_AT=0.28 DEV_STUDIO_ORBIT=160,10 \
DEV_STUDIO_SHOT=visual/debugger/slice.png \
dotnet run --project ../OpenGLTest.DevStudio -c Release --no-build
```
- The working directory must hold `assets/` (`OpenGLTest/`); `AssetRoot.UseAssetsDirectory` (`OpenGLTest/src/Core/AssetRoot.cs`, in the game project) searches the working directory and then the base directory, preferring the engine source tree, and leaves the working directory unchanged when it finds nothing.
- `DEV_STUDIO_SHOT` saves one PNG (path relative to the cwd; `visual/` is gitignored) and closes;
  the process exits on its own. Without it a window opens and grabs the mouse like the game.
- Do not `dotnet build` while an instance is running (locked exe). Several captures in a row:
  build once, then `--no-build` per run (~5 s each).
- **Read every PNG you capture** and say what you saw. The default selection is the first grid item
  ("Ash"), so pass `DEV_STUDIO_SELECT` or you are judging the wrong item.

## Tabs and modes
- **ITEMS**: the held item on the player body (third person, or `VIEW: FIRST PERSON`) plus a button per
  player animation (`PlayerPreviewState.AllModes`): `idle walk run sneak jump "sprint jump" land swim
  "swim idle" punch "heavy punch" slice "slice back" throw hurt equip death`. The right panel's sliders are
  the item's `display` block (Rot pitch/yaw/roll, Trans, Display scale - what SAVE TO JSON writes) and the
  preview-only Held scale / Grip. RESET PREVIEW resets all sliders, RELOAD FROM JSON only the display ones;
  neither re-reads the file (display values are fixed at load).
- **ENTITIES**: one button per clip of the selected `.bbmodel` in file order plus the life-cycle modes.
  Goblin: `idle walk throw melee hurt death spawn`; slime (clipless, code-driven squash): `idle hop hurt
  spawn death`. Mobs are the real `Goblin`/`Slime` objects driven like a client's mirror
  (`Mob.AnimateMirror`), so the spawn pop, hit recoil, death and the goblin's `BodyEffect` show.
  The shared chest model is listed last as `Chest` (`DEV_STUDIO_SELECT=chest`): `open close wobble`, each clip
  alone on its own clock, as a chest's lid plays it in the game.
- **TREES**: a species grown pulse by pulse at a fixed seed (Step slider, STEP -/+, MATURE, REGROW).
- **Timeline** (ITEMS and ENTITIES, bottom strip): Progress slider (seconds; dragging scrubs and stops the
  clock), Speed (x0..x4 in 0.1 steps, 0 pauses), LOOP ON/OFF (off = play once and hold the last frame).
  `AnimationClock` steps the state in fixed 1/60 s slices and a scrub restarts the state and steps up to
  the time, so a scrubbed frame is exactly the played one. A one-shot's first play starts at once, so
  timeline time == seconds into the animation (a slice's strike is ~0.18-0.34 s).

## Env vars (scripted captures)
| var | meaning |
|---|---|
| `DEV_STUDIO_SELECT=<item\|entity\|species>` | item grid first, then entity by name/display name (switches to ENTITIES), then tree species (`tree/<name>` forces a species) |
| `DEV_STUDIO_PLAYER_ANIM=<mode>` | a player animation (selects ITEMS); unknown logs the list |
| `DEV_STUDIO_ANIM=<clip\|hurt\|hop\|spawn\|death>` | the entity mode |
| `DEV_STUDIO_TREE_STEP=<n\|mature>` | the TREES step |
| `DEV_STUDIO_SHOT=<png>` | capture and quit (an early frame unless SHOT_AT) |
| `DEV_STUDIO_SHOT_AT=<seconds>` | fixed-step the simulation and capture at that instant (deterministic) |
| `DEV_STUDIO_SPEED=<0..4>`, `DEV_STUDIO_LOOP=<0\|1>` | timeline playback options |
| `DEV_STUDIO_ORBIT=yaw,pitch` | orbit camera (default 30,12): yaw 0 behind the model, 90 its right side, 180 the front, 200-ish front-left |
| `DEV_STUDIO_VIEW=first`, `DEV_STUDIO_LOOK=yaw,pitch` | first-person ITEMS view and its look |

## Tuning a pose from the sliders
A mid-animation held-item look is tuned here: scrub to the frame, slide Rot yaw / Rot roll until it reads
right, and the **difference from the JSON values** goes into the pose as a display-space offset
(`PlayerModel.HeldItemRotationOffset`, consumed by `Item.DisplayMatrix(offset)` and
`ItemDisplayEditor.DisplayMatrix(offset)`), weighted by the animation's phase. Never turn such numbers into
hand-anchor rotations: the display rotation is applied before the hand anchor, so they do not transfer.
Mirror only the yaw for a mirrored swing (exact for a display yaw of +-90). The slice's mid-cut turn
(`SliceBladeDisplayYawDegrees`/`RollDegrees`) is the worked example.

## Adding an animation
- Player: a mode in `OpenGLTest.DevStudio/PlayerPreviewState.cs` (`AllModes`, `LabelOf`, `Duration`,
  `Update`, `Busy`/`Fire` for a one-shot) that only feeds the real `PlayerModel` the inputs and triggers the
  game does; extend `PlayerPreviewStateTests`/`AnimationClockTests` (every mode must seek reproducibly).
- Mob: a clip in the `.bbmodel` shows up as a button by itself (goblin clips come from
  `tools/generate-goblin-clips.py`); a code-driven mode goes in `EntityPreviewState.cs` plus
  `EntityPreviewStateTests`.
- Verify with a `SHOT_AT` capture from the angle where the problem shows, then the world scenario
  (`visual-examples/animations*.json`).

## Reporting
Say which item/entity, mode, time and orbit each PNG was captured with, what it showed, and which angles you
did not check.
