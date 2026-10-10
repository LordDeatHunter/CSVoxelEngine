---
name: item-debugger
description: Preview and debug CSVoxelEngine's player animations, held items, entity (goblin, slime) models and clips, and tree species with the ItemDebugger tool - run recipe, every env var for scripted screenshots, the timeline (progress, speed, loop), how to tune a pose from its sliders, and how to add a mode for a new animation. Use for any animation, held-item or model work before a world scenario.
---

# Item debugger

`OpenGLTest.ItemDebugger` draws the real `PlayerModel`, `EntityAnimator`/`Goblin`/`Slime` and
`CellBatchMesher` meshes with the game's shader, without a world: orbit camera, a mode button per
animation, a timeline to scrub. **For any animation, held-item or model change look here first**; use a
`VOXEL_SCRIPT` scenario (`visual-verify` skill) only to see it in the real world with terrain, camera and
lighting around it.

## Run recipe (working directory `OpenGLTest/`, Release)
```
dotnet build OpenGLTest.ItemDebugger/ItemDebugger.csproj -c Release            # from the repo root, once
cd OpenGLTest
ITEM_DEBUGGER_SELECT=obsidian_blade ITEM_DEBUGGER_PLAYER_ANIM=slice \
ITEM_DEBUGGER_SHOT_AT=0.28 ITEM_DEBUGGER_ORBIT=160,10 \
ITEM_DEBUGGER_SHOT=visual/debugger/slice.png \
dotnet run --project ../OpenGLTest.ItemDebugger -c Release --no-build
```
- The working directory must hold `assets/` (`OpenGLTest/`); `AssetRoot` only walks up from there.
- `ITEM_DEBUGGER_SHOT` saves one PNG (path relative to the cwd; `visual/` is gitignored) and closes;
  the process exits on its own. Without it a window opens and grabs the mouse like the game.
- Do not `dotnet build` while an instance is running (locked exe). Several captures in a row:
  build once, then `--no-build` per run (~5 s each).
- **Read every PNG you capture** and say what you saw. The default selection is the first grid item
  ("Ash"), so pass `ITEM_DEBUGGER_SELECT` or you are judging the wrong item.

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
- **TREES**: a species grown pulse by pulse at a fixed seed (Step slider, STEP -/+, MATURE, REGROW).
- **Timeline** (ITEMS and ENTITIES, bottom strip): Progress slider (seconds; dragging scrubs and stops the
  clock), Speed (x0..x4 in 0.1 steps, 0 pauses), LOOP ON/OFF (off = play once and hold the last frame).
  `AnimationClock` steps the state in fixed 1/60 s slices and a scrub restarts the state and steps up to
  the time, so a scrubbed frame is exactly the played one. A one-shot's first play starts at once, so
  timeline time == seconds into the animation (a slice's strike is ~0.18-0.34 s).

## Env vars (scripted captures)
| var | meaning |
|---|---|
| `ITEM_DEBUGGER_SELECT=<item\|entity\|species>` | item grid first, then entity by name/display name (switches to ENTITIES), then tree species (`tree/<name>` forces a species) |
| `ITEM_DEBUGGER_PLAYER_ANIM=<mode>` | a player animation (selects ITEMS); unknown logs the list |
| `ITEM_DEBUGGER_ANIM=<clip\|hurt\|hop\|spawn\|death>` | the entity mode |
| `ITEM_DEBUGGER_TREE_STEP=<n\|mature>` | the TREES step |
| `ITEM_DEBUGGER_SHOT=<png>` | capture and quit (an early frame unless SHOT_AT) |
| `ITEM_DEBUGGER_SHOT_AT=<seconds>` | fixed-step the simulation and capture at that instant (deterministic) |
| `ITEM_DEBUGGER_SPEED=<0..4>`, `ITEM_DEBUGGER_LOOP=<0\|1>` | timeline playback options |
| `ITEM_DEBUGGER_ORBIT=yaw,pitch` | orbit camera (default 30,12): yaw 0 behind the model, 90 its right side, 180 the front, 200-ish front-left |
| `ITEM_DEBUGGER_VIEW=first`, `ITEM_DEBUGGER_LOOK=yaw,pitch` | first-person ITEMS view and its look |

## Tuning a pose from the sliders
A mid-animation held-item look is tuned here: scrub to the frame, slide Rot yaw / Rot roll until it reads
right, and the **difference from the JSON values** goes into the pose as a display-space offset
(`PlayerModel.HeldItemRotationOffset`, consumed by `Item.DisplayMatrix(offset)` and
`ItemDisplayEditor.DisplayMatrix(offset)`), weighted by the animation's phase. Never turn such numbers into
hand-anchor rotations: the display rotation is applied before the hand anchor, so they do not transfer.
Mirror only the yaw for a mirrored swing (exact for a display yaw of +-90). The slice's mid-cut turn
(`SliceBladeDisplayYawDegrees`/`RollDegrees`) is the worked example.

## Adding an animation
- Player: a mode in `OpenGLTest.ItemDebugger/PlayerPreviewState.cs` (`AllModes`, `LabelOf`, `Duration`,
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
