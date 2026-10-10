# QUICK_WINS — one-thing features

> Plug-n-play ideas: each entry is **one self-contained feature** you can start and finish in a
> sitting. Not a category, not a system, and nothing that drags in sub-features.
> The big roadmap lives in **TODO.md**, performance in **PERF_TODO.md**.

## What qualifies

- One sentence says what it is. If it needs "and also", split it or drop it.
- It reuses what exists (a block flag, an existing manager, an existing animation hook).
- No new station, no new protocol message, no new save file, no new shader pass unless that
  *is* the feature.
- It can be verified with one scenario or one small test.
- A new block, item or enemy is fine, as long as it is a single one that needs no machinery
  around it. (The kiln was not: multiblock, fuel, crucible, molds and casting.)

Mark done with `[x]` and a short note, or delete the line. Add a new idea as one line plus a
**Hook** (where it plugs in) so the next person can start at once.

## Feel and feedback

- [ ] **Landing dust puffs.** An entity landing from a fall of more than ~3 blocks kicks up a
  small burst of particles tinted by the block landed on. Both the particle count and their
  launch velocity scale with the entity's fall velocity (equivalently its fall distance), so a
  long drop throws more, faster dust than a short one.
  *Hook:* `FallTracker` gives the fall height (the impact speed follows from it);
  `BlockDebrisSystem.Burst` already makes tinted debris. Tuning constants (minimum fall, count
  and velocity curves) go in `WorldGenerationConfig`, with the curve in `MathUtils`.
  Test: a fall under the minimum spawns nothing; count and speed never decrease as the fall grows.
  - *Follow-up tweak (slimes):* a landing slime should throw slime-coloured particles
    (its green, not the ground's tint), ideally a small splat that matches its landing squash
    (`Slime._squash` landing kick). Do this after the generic puffs exist: it is only a different
    colour/count source fed into the same burst.

- [ ] **Sprint footstep puffs.** While sprinting on a dry, loose surface (sand, snow, dirt,
  gravel) each footfall kicks up a tiny puff of particles tinted by the block underfoot.
  *Hook:* the same `BlockDebrisSystem.Burst` the landing puffs use, fired from the gait's
  footfall (`PlayerModel`'s walk cycle phase). A slice of `V-3` in TODO.md; build it right after
  the landing puffs so the surface-tint lookup is shared. Test: no puff while standing or
  sneaking, none on a non-loose surface.
- [x] **Sprint FOV widening.** *Done:* `SprintFov` term added to the base FOV and the punch pinch, capped at the on-foot sprint speed (`camera-feel.json`). The view eases out a few degrees while sprinting and back when
  the sprint ends.
  *Hook:* `Camera._currentFov` is already part of the view-cache key (see "Camera" in AGENTS.md),
  so add a target FOV driven through `MathUtils.Damp`; the knob (extra degrees, ease speed) goes
  in `WorldGenerationConfig`. A slice of `V-17`. Must not stack wrongly with the punch FOV pinch
  (`PunchFov`): add them, do not overwrite. Flight sprint (10x) should not blow the FOV out, so
  clamp it.

- [x] **Hit-stop on a landed punch.** *Done:* `PlayerModel.HitStop` + `SwingHitStop`; the rest of the swing replays faster so it still ends on time (bare-hand punch holds ~80 ms, slice 150 ms); a client predicts the connect (`hit-stop.json`). When a punch or slice connects with a mob, the player's
  swing freezes for about 100-200 ms so hits feel heavier (start near 150 ms and tune by feel).
  *Hook:* the swing clock in `PlayerModel` (`Punch`/`SlicePhase`) and the hit landing in
  `World.UpdatePunch`/`UpdateSlice`. Freeze only the swing animation and camera pinch, never the
  world or the mob's own recoil, or it reads as lag. Duration in `WorldGenerationConfig`. A heavier
  tool may hold longer (`Punch(weight)`). Cosmetic and client-side: on a server the stall is
  local to the animation, so nothing may be paced by it (attack pacing stays `AttackPacing`).
  Test: the freeze is bounded and never extends past the max, a miss never freezes.
- [x] **Swim stroke bubbles.** *Done:* `BubbleParticleSystem.TrackSwimStrokes`, one burst from the head per stroke while the eye is in water (`swim-bubbles.json`). Each swim stroke lets out a few bubbles from the head.
  *Hook:* `Camera.SwimStrokeCount` moves on every stroke, which is already how the body starts
  its stroke animation; spawn the bubbles on that change. The bubble texture and renderer exist
  (the HUD/world bubbles). Only while the head is actually in water (`IsEyeInWater`).

- [x] **Strafe camera tilt.** *Done:* `StrafeTilt`, an eased roll of at most 1.5° into the strafe, added to the other roll terms, off while flying or dead. The view rolls 1-2 degrees toward the strafe direction and eases
  back when the strafe stops.
  *Hook:* `Camera._roll` already folds into the view cache key (see "Camera" in AGENTS.md), so
  this is one damped target through `MathUtils.Damp` from the sideways input or velocity; the
  angle and ease speed go in `WorldGenerationConfig`. Add to the existing roll (the hit kick and
  death view use it), never overwrite. Off while flying and while dead. Test: no tilt without
  strafe input, the roll is bounded by the constant, and it returns to zero.
- [ ] **Pickup pop on the hotbar.** When a drop is collected, the hotbar slot it lands in does a
  quick scale bump.
  *Hook:* `UIRenderer`'s hotbar draw plus a per-slot timer set where a pickup adds to the
  inventory (`DropManager`'s collector crediting `Inventory`). The bump curve belongs in
  `MathUtils` (`EaseOutBack`). A slice of `V-5`. On a server the inventory snapshot arrives
  instead, so detect it from the slot's count rising, not from the pickup call. Test: a rising
  count starts the timer, a falling count (a drop or craft) does not.
- [ ] **Low-health vignette pulse.** Below about 30% health the screen edge pulses red slowly,
  stronger the lower it goes.
  *Hook:* a full-screen term in the UI pass or the post pass (`post-fragment.glsl`; if the post
  pass, mind that it is display-space and shared with the ItemDebugger). Driven by the health
  value that already feeds the hearts; the pulse is a `MathUtils` curve and the threshold and
  rate live in `WorldGenerationConfig`. Off while dead or in creative. Test: no effect above the
  threshold, intensity never decreases as health falls.

## Shaders

- [ ] **Tall grass roots stay put.** Today the whole `tall_grass` cross sways like a leaf, which
  looks odd: the roots at the bottom of the block should be static and only the upper part
  should move, growing from nothing at the base to full sway at the tip.
  *Hook:* the sway is the `vSway` block in `test-vertex.glsl` and `shadow-vertex.glsl` (the two
  must stay byte-identical, see the `shader-sync` skill and `ChunkDrawShaderTests`). Scale the
  displacement by the vertex's height within its cell (`fract(position.y)`, 0 at the base, 1 at
  the top), ideally eased (e.g. squared) so the lower half barely moves. Leaves must keep their
  current uniform sway, so either the weight applies only to a new kind of sway vertex or
  leaves are weighted to 1; `tall_grass` and `dead_shrub` are the plants to check. Watch the
  case of a non-cell-aligned `y` (a vertex exactly on the cell's top is `fract` 0), so derive the
  weight from the model's own base rather than a bare `fract`. Verify with a `VOXEL_SCRIPT`
  close-up of a grass patch at two `VOXEL_TIME`s.

- [ ] **Leaf particles follow the wind.** Ambient and burst leaf particles should drift along
  `Wind` like smoke, embers and the canopy sway already do.
  *Hook:* `LeafParticle`/`LeafParticleSystem` (see `FireParticle.Update`'s `wind` argument for the
  pattern; `RenderManager.WindDirection` exposes the unit direction). Check first how much of it
  is already wired. Verify with `visual-examples/trees.json` at two `VOXEL_TIME`s, where the
  heading differs.

- [ ] **Water edge foam.** A thin pale line where the water surface meets a block or the shore.
  *Hook:* the water fragment in `test-fragment.glsl` already reads the scene depth copy and
  measures the water's thickness (the Beer-Lambert path); foam is a term that rises as that
  thickness goes to zero. Animate it with the existing wave/flow phase so it moves, and keep the
  Snell-window view from below foam-free. One term, no new pass; verify with
  `visual-examples/water.json` (`VOXEL_TIME=360`).
- [ ] **Stars.** A field of stars on the night sky, fading in at dusk and out at dawn.
  *Hook:* `SkyRenderer` / `sky-fragment.glsl`, where the dome currently samples one flat colour
  (see "Water rendering" in AGENTS.md: `SkySample` is duplicated in four shaders, so a star term
  in the dome only should not touch those copies; confirm before editing and use the
  `shader-sync` skill). Fade by the sun's height from the lighting clock; hide stars behind the sun
  disc and under clouds. Stars must stay out of the reflection and Snell-window views in a way
  that still looks right. A slice of `V-6`.
  - *Follow-up (twinkle):* each star's brightness flickers on its own phase and rate. Needs the
    stars first; keep the phase a hash of the star's cell so it is stable per star.

- [ ] **Sun lens glare.** A soft radial glow when you look toward the sun, scaled by how
  directly you face it and fading with the sun's height and with cloud cover over it.
  *Hook:* the sun disc is already drawn additively by `RenderSun`; this is a second, larger and
  fainter additive quad or a term in the sky pass, driven by the dot of view direction and sun
  direction. Skip it underwater and in lava (the dome and sun stay dry underwater, so check which
  you want) and when the sun is below the horizon. Strength constants in
  `WorldGenerationConfig`. Test (GL-free twin, like `Tonemap`): intensity is monotone in the
  alignment and zero when facing away.
- [ ] **Snow sparkle.** Tiny glints on snow in direct sun that shift as you move.
  *Hook:* the ore highlight already exists (`material.metallic`, `OreSpecLinearScale`), but the
  AGENTS.md notes that material is keyed per tile and ore-only. Prefer a small separate
  fragment term for the snow tiles over turning on a general material, so this stays one
  feature. A hashed per-texel threshold gated by the sun term and the view angle gives the glints.
  Off in shadow and cloud shadow. Verify with a `VOXEL_SCRIPT` shot on the frozen plains at
  `VOXEL_TIME=147`.

## Blocks and items

- [x] **Natural cotton.** *Done:* `cotton_plant` + `cotton` item, `CottonGenerator` over the
  reusable `PlantClumps` rule (64-block cells, warm/dry forest fringe), `visual-examples/cotton.json`.
  A cotton plant block that generates in clumps and drops cotton.
  One cross-model block (like `flower` / `tall_grass`, `needsSupport`, hardness 0), one `cotton`
  item and its loot table, all JSON (see the `add-content` skill).
  **Plants generate clumped, not scattered**: a cluster of a few blocks every X blocks, never
  one block every X. Do it by hashing a cluster centre per coarse cell (the way tree cells work:
  `TreeGenerator.CellOf`) and stamping the plants within a small radius of it with a density
  falloff, instead of the per-column patch-noise-plus-density roll `FlowerGenerator` and
  `FoliageGenerator` use. Keep it a pure function of (x, z) so chunks agree, and read the
  neighbouring cells' centres so a clump on a cell border is not cut in half. Biome: pick one
  (e.g. dry forest edge) and leave the rest bare.
  *Hook:* a new `CottonGenerator` beside `FoliageGenerator`, called from the same stamp step;
  its cell size, radius, count and density go in `WorldGenerationConfig`, and a `Reseed` hook
  if it owns noise. Write the clump rule so a later plant can reuse it (put it in a small shared
  helper rather than inline). Test: every clump has at least the minimum members, members are
  within the radius, clump centres are at least a cell apart, and the same seed gives the same
  cells.
- [ ] **Torch.** A placeable light source: one block with `light.emission` and a warm
  `light.color`, a small hitbox, floor placement only at first (`needsSupport`, so it pops when
  the ground goes).
  *Hook:* `add-content`; `V-4` (torch particles) is the follow-up for the flame, not part of this.
  Test: it emits light, and breaking the block under it pops it.
  - *Sub-step (wall-mounted):* a torch placed against the side of a block leans out from the
    wall. Store the facing in the state byte (`facing` in the low bits, like `Multiblock`), bake
    the four wall models (and rotate or pick by facing in the mesher), and make support follow
    the **attached** face instead of always the block below (`needsSupport` is below-only today,
    so this needs a per-state support rule). Placement picks wall vs floor from the hit face.
    Do this only after the floor torch ships.


- [ ] **Glass pane.** A thin pane block (a thin slab-shaped model with a matching `hitbox`) on
  the existing translucent path. Single block, no connecting at first.
  *Hook:* `add-content`; glass already ships as `translucent` + `cullSelf`, so copy its flags
  and make the model a thin slab. Check that it passes light and does not occlude.
  - *Side-step (neighbour connectivity):* the pane grows arms toward adjacent panes and solid
    blocks, like Minecraft's. The connection is derived from neighbours at mesh time (not stored),
    so it needs a procedural emitter in `ChunkMesher` (a new `renderer`) rather than a baked
    model, plus the matching selection boxes and a remesh of the neighbours when one changes.
    Do it only after the plain pane ships.

## Enemies

- [x] **Slime size variants.** *Done:* sizes 1-3 via `SlimeSize` multipliers (body, health, damage, loot rolls), saved in `MobData.Size`, sent in `NetEntity.Ammo` (`slime-sizes.json`). Slimes come in sizes. Each size scales the body, health, damage
  and loot, on top of the existing hop AI and squash spring.
  *Hook:* `Slime` / `SlimeManager` (spawn roll, `WorldGenerationConfig.Slime*`), the model scale
  through the squash path, and the size must be saved and sent (`MobSaveCodec`, `NetEntity`), so
  look at both before starting. Per-size stats are data-like constants, not a branch per size.
  Test: stats grow monotonically with size; a size survives save and reload.
  - [x] *Sub-step (one mob cap, do this first):* *Done:* `MobCaps` (natural `MobNaturalCap`, hard `MobHardCap` = 15x) shared by both managers; `MobsMaxAlive` is the hard cap. replace the per-kind caps (`SlimeMaxAlive`,
    `GoblinMaxAlive`) with **two global mob caps** shared by every kind. The **natural spawn cap**
    limits how many mobs the ring spawner may have alive; the **hard cap** (about 15x the natural
    one, one constant in `WorldGenerationConfig`) limits how many may exist at all. A natural
    spawn is refused at the natural cap. A forced spawn (a slime splitting, a spawner later) may
    go past the natural cap, but **nothing ever spawns past the hard cap**: a split at the hard
    cap just produces no children. `MobsMaxAlive` (which sizes the healthbar buffers) becomes the
    hard cap, so it stays the one number those buffers are sized from. The managers already count
    `LivingCount` (dying mobs excluded); count across both managers, so this touches
    `SlimeManager`, `GoblinManager` and the `MobSpawner` callers, and a mob kind added later needs
    no cap of its own. Test: natural spawns stop at the natural cap, a forced spawn exceeds it, no
    spawn of either kind exceeds the hard cap, and dying mobs do not count.
  - [x] *Sub-step (spawn rules):* *Done:* `SlimeSize.Roll` from `DayCycle.NightFactor` and the underground flag. by day the slimes are mostly small, with a small chance of a
    larger one. At night, or underground, the chance of a larger one is higher. Reads the
    lighting clock and the spawn height or sky exposure at the chosen cell; this is the
    light/time-keyed spawning `F-3` in TODO.md lists as open, but done only for slime size, not
    a general system. Test: the large-size share is higher at night and below ground than by day
    on the surface, with the same rolls.
  - [x] *Sub-step (splitting):* *Done:* `SlimeSize.SplitsOnHit` on the half-line transition, children forced under the hard cap (`slime-split.json`). a slime that is large enough splits into smaller slimes when a hit
    brings it to half health or below while it survives. A hit that kills it from above half
    does **not** split it (no children), and neither does a slime too small to split. So the rule
    is on the transition, not on being low: the health before the hit is above half, the health
    after is at or below half and above zero. The children spawn at the parent's position with a
    small outward shove, forced past the natural cap but never past the hard cap (see the cap
    sub-step above). Test: a killing blow from full health never splits, a hit across the half
    line splits once, a second hit below half does not split again, and no split exceeds the
    hard cap.

## World

<!-- One generator or one worldgen feature, e.g. a single rare surface structure. -->

## UI

- [ ] **Hotbar item name fade.** Switching the selected hotbar slot shows that item's name
  above the hotbar for about 1.5 s, then fades out.
  *Hook:* a draw in `UIRenderer` next to the hotbar and a small timer reset on selection change
  (singleplayer and the server's `SelectSlot` intent both change it). The fade curve belongs in
  `MathUtils`. Empty slot shows nothing; re-selecting the same slot does not restart it.
  Test: the timer resets on a change and not on a repeat.
- [ ] **Floating damage numbers.** A small number appears above a mob when it is hit, rises and
  fades.
  *Hook:* the healthbar billboard approach (`EntityHealthbarRenderer`, buffers sized by the mob
  cap) and the damage amount from `Mob.TakeDamage`. Pool them in a fixed buffer (no per-hit
  allocation); the rise and fade curve goes in `MathUtils`. On a client the damage is not known
  locally (a client never damages a mob), so derive it from the health drop between two
  `EntityList` snapshots, or leave it singleplayer-only at first. Pairs with the hit-stop entry
  above. Test: a hit spawns one number of that amount, the pool never exceeds its size.
