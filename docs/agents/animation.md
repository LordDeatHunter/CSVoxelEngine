# Entity animation and animation juice

Blockbench clips and the `EntityAnimator`, springs, mob life cycle, player secondary motion, camera and HUD feedback.

### Entity animation

Entity rigs come from Blockbench. **Groups may nest**: `BbmodelPart.Parent` names the enclosing group
(null at the top level), and the goblin keeps `Head`, `ArmL` and `ArmR` under `Torso`. Group origins
stay absolute, as Blockbench stores them. Rest rotations on groups and cubes are still rejected. The
project's top-level `animations` array is parsed by `BbmodelLoader.ReadAnimations` into GL-free
`AnimationClip`s (`BbmodelData.Animations`, in file order):
- **Loop modes:** `loop`, `once` and `hold`.
- **Tracks:** per-bone `rotation`/`position`/`scale` tracks keyed by group uuid, or by the animator's
  `name`.
- **Interpolation:** `linear`, `catmullrom` and `step`, using Blockbench's segment rule: catmull-rom
  when either end key is catmull-rom, step when the earlier key is step, otherwise linear. Components
  are interpolated as raw Euler values, as Blockbench does.
- **Events:** the `effects` animator's `timeline` keyframes become the clip's sorted events. The name
  is the trimmed `script`; sound and particle keys are ignored.
- **Values:** a data point may be a number or a numeric string. A Molang expression, an unknown
  channel, `bezier` or any other interpolation, and an animator naming a group the outliner lacks all
  throw, naming the file and the animation.
- **Unanimated files:** a file without `animations` loads exactly as before.
- **Cleared faces:** a cube face whose texture the author cleared (`"texture": null`, a wall's side that stands
  against another cube) is left out of the mesh, like a disabled one.
The **sign and order convention** lives in one place, the comment above `BbmodelLoader.ReadAnimations`,
taken from Blockbench's `BoneAnimator.displayRotation`/`displayPosition`:
- A rotation `(x, y, z)` in degrees becomes engine Euler `(-x, -y, z)`, applied X first, then Y, then
  Z (Blockbench's 'ZYX' order, `AnimationClip.RotationFromDegrees`).
- A position in pixels becomes `(-x, y, z) / 16` blocks, in the parent bone's space.
- Scale is unchanged.
- +X is the model's right (`ArmR`) and -Z its front, so a Blockbench author types a **negative** x to
  raise a hanging arm forward.
- **Blockbench 5 files.** The lines above are how a file up to format 4.x stores its keys (every generated
  model). Blockbench 5 applies a Generic model's keys as stored: opening an older file it negates rotation
  x and y and position x, and it saves `meta.format_version` 5.0 with those values, which are the engine's
  own, so `StoresPreviewSigns` reads such a file without the flip (there a **positive** x raises a hanging
  arm forward). A 5.x file also keeps each group's name, origin and rotation in a root `groups` array and
  only its uuid and children in the outliner (`GroupData` reads either layout), and embeds its skin as a
  data URL. `boar.bbmodel` and `dire_boar.bbmodel` are such files, saved from Blockbench itself.
If a clip ever plays mirrored, fix the loader, never the clips.

**`EntityAnimator`** (one per entity, GL-free, no per-frame allocation) has two layers:
- **Locomotion** crossfades the `idle` and `walk` clips by horizontal speed through `MathUtils.Damp`
  (a flyer's animator crossfades `idle` and `fly` by its 3D speed, see "Flying mobs").
  The walk plays back at a rate that scales with speed against the animator's reference speed.
- **Action** runs a one-shot clip (`Play("throw")`). Replaying the clip that is already running is
  ignored, and a different one replaces it. The action overrides only the bones its clip keys, with an
  `ActionBlendSeconds` smoothstep in and out, so it returns exactly to the locomotion pose at its end.
`SetGait(idle, move, referenceSpeed)` swaps the locomotion pair (a deer's walk for its run), fading from the old
pair to the new over `GaitBlendSeconds`, and `CutAction()` ends the running action early, blending it out without
firing the events it skipped (see "Wildlife").
`ConsumeEvent(name)` returns true exactly once per play, when playback crosses the event time.
`GetPartMatrix(part, rig, feet, facingYaw)` composes the parent chain in System.Numerics row-vector
order: scale, rotation, then pivot delta plus animated translation, then the parent, down to the root
`RotY(facing) * T(feet)`. `HandAnchor(part, ...)` is that matrix dropped to the part's lowest cube; it
is where the goblin's held flint is drawn (`Goblin.HandPart` = `ArmR`, `Goblin.HeldStoneScale`). The
player's `PlayerModel` still poses procedurally and ignores `Parent`.
The chain composition itself is `RigPose.PartMatrix(pose, part, rig, root)` (`Entities/Posing/`), generic over an
`IBonePose` (a bone's animated local transform): the animator is one, and `ClipPose`, one clip sampled at one time with
no layers and no blending, is the other. A chest's lid is posed that way (`ChestModel`, see "Chest model and lid").

The goblin's **content contract** is enforced at startup by `Goblin.ValidateContent`, which the
`GoblinManager` constructor calls. The model must provide:
- the groups `Torso`, `Head`, `ArmL`, `ArmR`, `LegL` and `LegR` (`Goblin.RequiredParts`);
- the clips `idle`, `walk`, `throw` and `melee`;
- a `release` event on `throw` and an `impact` event on `melee`.
A missing piece throws, naming the model file. The clips' keyframes may be retuned freely in
Blockbench; the clip and event names may not change. Hurt is not a clip: it is the hit-flash tint.
`goblin.png` is committed art whose UV layout must match the `.bbmodel`'s face UVs.

### Animation juice (springs, mob life-cycle, player secondary motion)

`Spring.cs` is the one velocity-carrying damped spring (`Impulse`, `Step(target, dt)`; frequency in
Hz, damping ratio below 1 rings); `MathUtils` also has `EaseOutBack`, `EaseOutElastic`,
`EaseOutBounce` and `EaseInOutCubic`. Use them instead of a linear fade wherever something should
overshoot or jiggle.

- **Mob life-cycle** (`Mob.cs`): every mob has a spawn pop (`SpawnProgress`), a death
  (`BeginDying`/`IsDying`/`DeathProgress`/`IsRemovable`, `DeathSeconds` per kind), a hit recoil spring
  (`HitKick`) and an eased `HitFlash`; an active status effect tints it through `uStatusTint`, mixed
  under the flash (see "Status effect schema"). A killed mob **rolls its loot at once**, then stays in the
  manager's list (still drawn, no longer a target, a threat, a saved or broadcast mob or part of the
  live count) until its death has played; `MobDirector.RefreshMobs` skips dying mobs, the spawn caps use
  `LivingCount`, and a client's `Remove` (the server stopped reporting it) starts the death instead of
  popping it. `AnimateMirror` advances the clocks of mobs a client never simulates.
- **Slime**: squash and stretch ride one under-damped spring (`Slime._squash`): a 0.2 s crouch before
  each hop (the launch waits for it), a stretch kick on take-off, a landing kick in proportion to the
  fall, an idle breath, a ballooning pop on death and an overshooting pop-in.
- **Goblin**: its facing eases (`Mob.TurnToward`); `RiggedMob.BodyEffect(feet)` (the goblin's and the hell mobs') is the pose the clips
  cannot give (a lean that follows the walk, the hit flinch, the death fall and shrink, the spawn pop)
  and the renderer multiplies it onto every part and the held flint. The clips are generated by
  `tools/generate-goblin-clips.py` (re-running is byte-identical, `--check` verifies): idle, walk,
  throw, melee as before (the `release` at 0.42 s and `impact` at 0.30 s are pinned by gameplay
  pacing) plus the optional `hurt`, `death` and `spawn`. `EntityAnimator.PlayOverlay` plays `hurt` as
  an **additive overlay** (translation added, rotation composed) so a hit never cancels a throw or
  swing; `death` and `spawn` are ordinary actions.
- **Arm frames** (`ArmOverlays.ApplyArmFrames`): the base arm is a forward flex plus a yaw, so both
  bones stay in one plane and it cannot reach out sideways. Poses that need that are given as an upper
  arm *roll* (abduction about the front axis, then flexion about X, then yaw; the left arm mirrors the
  roll and yaw) and a forearm *flexion plus bend*, slerped over the base frames by a weight
  (`LimbFrames.UpperFrame`/`ForeFrame`/`Blend`). With the arm held out the elbow's X hinge is the vertical axis, so
  a plain forearm flexion swings it forward in the horizontal plane and a bend about Z lifts it up. In
  use: the **jump** (arms out to the sides, a soft elbow, both knees folded under; a sprint jump trails
  them back), the **punch** (the elbow flares out and forward at the chamber with the forearm folded to
  the front, the weight `Retract * (1 - Extend) * (1 - Recover)` fades it to nothing at full extension
  where the IK frame on the aim line takes over, so the elbow swings in and forward through the thrust),
  the **throw charge** (the upper arm out to the right and a little behind the body, the forearm bent up
  at about a right angle, the shoulders wound back toward the throwing side, then a whip forward and a
  settle) and the **swim stroke**. The hand anchor follows the forearm frame, so the held item does too.
- **Bow draw** (`UsePose.Bow`, `BowPose`): an item whose `use.pose` is `bow` (the `bow`) holds a two-handed draw while the timed use charges: the right (item) arm goes out forward through `ArmOverlays`, the left arm is folded back to the cheek, and the torso twists side-on and leans back a little (`SecondaryMotion`), all weighted by the use charge (`BowPose.BodyWeight`/`DrawWeight`). It holds at full draw and, when the button is let go (`PlayerModel.CancelUseCharge` from a real draw), the string arm snaps back within `BowPose.SnapShare` of `ReleaseSeconds` and the rest follows; the camera narrows with the draw like a throw's charge (`Camera.ThrowCharge` is fed `Player.BowCharge`). The item debugger's `BOW` mode and the `draw`/`loose` player ops of `animations.json` show it.
- **Axe split** (`SwordSwing.AxeSplit`, `PlayerModel.StartAxeSplit`): an axe blow on a log standing on a stump plays the finisher's overhead shape (`SwordMotion.AxeSplit`, the chop's pose at `StumpConfig.SplitSwingSeconds`, a slow blow) and the split lands at its impact share (`LeftClickAction.UpdateSplit`). The item debugger's `AXE SPLIT` mode and the `player` op's `axesplit` action show it; `visual-examples/stump.json` films the whole split.
- **Cast feedback** (MG-10, cosmetic only): a staff cast plays `PlayerModel.Cast`, `PlayerModel.CastDuration` (0.45 s) of `CastMotion` (GL-free pure functions of the cast clock): the right arm raises the staff over the head-front, thrusts it straight forward with a snap (an ease-out) and a small forward lean (`CastMotion.Lean` into `SecondaryMotion`'s torso pitch), recoils a little and eases back, its weight 0 at both ends so the arm leaves and returns to the base pose without a jump; `PoseBuilder` lays it after the throw, and `BodyPartMatrices` counts it as an authored right arm. `LocalPlayer.CastSwing` (the right-click chain's staff branch, and the `player` op's `cast` action, gesture only) adds a light view pinch (`SpellConfig.CastFovKickDegrees`). The server rebroadcasts an accepted cast as `NetPlayerAction.Cast`, which `RemotePlayer` replays. The rest is `Magic/SpellFeedback`, the `SpellCaster.Feedback` hook (never built on a headless server): for the local player's own accepted cast the `spell_cast` sound (`spell_fizzle` on a fizzle, singleplayer only, since a client never learns of one) and a burst of `SpellSparks` at the bolt's launch point in the element's status tint (`SpellSparks.ColorOf`, else `BoltDefaultColor`; gray on a fizzle); on a client a cosmetic preview of the bolt (`SpellBoltPreview`: `SpellProjectile`'s flight and stop test, trail and impact sparks and the impact sound, never a strike, effect or cell write) and a default-colored burst where a remote player cast (its `spell_cast` sound plays at that player from `ClientMirror.SyncRemotePlayers`). `SpellEffects` plays `spell_impact` where a real bolt lands, positionally, at most once an update; on a server it goes to the players near it except the caster (see "World sounds"). The wavs come from `tools/generate-spell-sfx.py`. The item debugger's `CAST` mode (`ITEM_DEBUGGER_PLAYER_ANIM=cast`, with `ITEM_DEBUGGER_SELECT=wooden_staff`) and `visual-examples/animations-cast.json` (`VOXEL_TIME=147`) show it. Remote players' bolts stay invisible.
- **Axe chop** (`SwordSwing.AxeChop`, a fourth swing that is not in the sword's chain): an item in the `axe`
  tag (`Mining.IsAxe`) swings a chop from the side (a flat sweep across the front from the right, the elbow straightening into the blow) instead of a punch whenever `LocalPlayer.PunchSwing` runs, through
  `PlayerModel.StartAxeChop(seconds)` over the same `SwordMotion` shape machinery and `BladeSwingFrame`. Its length
  is the axe's hit gap (`LocalPlayer.AxeChopSeconds`, held within `PlayerConfig.AxeChopMin/MaxSeconds`), and while
  mining `LeftClickAction.UpdateBlockBreaking` starts the swing ahead of each hit by the shape's impact share of that
  gap, so the blade comes down as the hit lands (the first hit of a break lands at once, so that one chops a beat
  late). A hit on tree wood also shakes leaves loose from the canopy above it (`TreeShudder` into
  `LeafParticleSystem.BurstCells`, `ParticleConfig.TreeShudder*`), and a heavy hit nudges the camera (`BreakKick`).
  Chopping a **natural** tree's wood (tree wood whose state is not `WoodState.Detached`; a placed log never) keeps its
  damage **for good**, in singleplayer: `LeftClickAction.RecordChop` stores the damage and the side face of the first
  hit in the world's `ChunkManager.ChopDamage` (`WoodDamage`), a break of that cell seeds its damage from it, so
  releasing, retargeting or switching tools never heals it, and it is saved with the world (`wood-damage.bin`,
  `WoodDamageCodec`, written by `WorldPersistence` when its version moved, read in the `World` constructor before any
  chunk meshes). The cell is cleared when it is mined (`Chunk.RemoveBlockAt`) or felled (`BlockEditor.FellWood`); a
  client and a server keep none (the server counts a break from zero). A full-cube trunk cell shows that damage as its
  hit face cut by a **slit**: the whole width of the face from the first hit, a few pixels high (it never grows
  taller), and deeper with every hit (`WoodDamage.BiteDepthPx`, at least 1 px after any damage and `MaxDepthPx` only
  at the break): `RecordChop` remeshes the chunk when the slit gets deeper (`ImmediateRemesh` takes a chunk position) and
  `TreeWoodEmitter.EmitEaten` draws the cube minus that box (each cell face as the bars around the hole, the box's
  walls in the log's end-grain tile, a cap where the neighbor hides a face, so it is never see-through; a cut that
  reaches the cell's side also opens the neighboring face). It is cosmetic (the hitbox stays the whole cube), it
  replaces the crack overlay and the mining pop cube for that cell (`LeftClickAction.ShowsEatenFace`; the pop box is
  a whole cube and would cover the bite), and a hit on a top or bottom face still keeps the damage but shows the
  usual crack overlay. A **thin** cell (node and tubes) shows the same slit in its own shape: `ThinWoodBite` cuts a
  slab across the node on the hit side, perpendicular to the wood's run (`ThinWoodBite.RunAxis`, the axis of the
  cell's parent direction: horizontal across a trunk or root, vertical across a horizontal limb; as wide as the cell,
  deeper with the damage but scaled to the node's width so a twig is never severed, always a core left), subtracts it from every box of the cell and draws the pieces
  whole with the walls in end grain; `RecordChop` remeshes for it exactly as for a full cube.
  The item debugger's `AXE` mode and `visual-examples/animations-axe.json` (`VOXEL_TIME=147`) show it.
- **Slice** (`SliceMotion.SlicePhase`): a whole-body motion in uneven phases of the same 0.7 s: a slow
  wind-up that coils the body (to 0.26), a strike about twice as fast as before centered on the impact
  (0.26..0.49, a steeper `EaseInOutCubic` through the middle) and a long eased recovery, so the contrast
  reads as a snap. The arm sweep is only part of it: the whole body turns about the feet
  (`SliceMotion.SliceBodyYaw`, the legs counter-turning by `SliceLegCounter` so the feet stay planted), the shoulders
  turn further (`SliceMotion.SliceTorsoYaw`), the knees and hips sink into the strike and the torso dips. The hand
  also travels a diagonal on every slice, from `SliceStartRaiseBlocks` above the chest line (so the cut
  starts high, not horizontal) down to just `SliceEndDropBlocks` below it (`SliceMotion.SliceDiagonal`, through the
  same re-solved arm as the chest-height drop), whichever way the swing goes. The slicing **arm is held out in front,
  nearly straight, at chest height** (`SliceMotion.SliceArmShoulder`, `SliceArmElbow`): the carrying pose (elbow bent 75
  degrees, forearm up, which made the hand look held up by the elbow) is eased into it as the stroke ramps in. The
  **blade is carried by the wrist**: its tip points along the forearm (so it follows the arm's sweep and is
  always across the direction the hand travels, never turned up mid-cut), rolled about its length by
  `SliceBladeRoll` (cocked on edge, not laid flat) and cocked up from the forearm by `SliceBladeCant`, and through the main part of the cut (`SliceMotion.SliceMainWeight`: eased in at the end of the wind-up,
  full through the strike, out over the start of the recovery) turned about the vertical by `SliceBladeYawOffset`
  (+45 degrees in the engine's yaw convention, so the tip leads ahead of the hand's travel; mirrored for the
  return); in
  `GetHandAnchorMatrix` the wrist orientation takes over from the body-space one by the stroke. The tip therefore
  sweeps out to the wind-up side, forward as the hand crosses the middle and out to the far side (mirrored for the
  return).
  On top of that the pose adds a **display-space offset** to the held blade through the main part of the cut
  (`PlayerModel.HeldItemRotationOffset`, from `SliceBladeDisplayYawDegrees` / `SliceBladeDisplayRollDegrees`):
  degrees in the item JSON's `display.rotation` order `[pitch, yaw, roll]`, which `Item.DisplayMatrix(offset)` and
  the debugger's `ItemDisplayEditor.DisplayMatrix(offset)` add to the item's own rotation, so the blade is drawn
  at yaw -145 / roll 70 mid-cut and at its JSON yaw -90 / roll 45 at rest. **That is how a mid-animation held-item
  pose is tuned**: in the item debugger scrub the timeline to the frame, slide Rot yaw / Rot roll until it reads
  right, and put the difference from the JSON values into those constants; the sliders keep showing the JSON
  values (what SAVE TO JSON writes), since the offset is the pose's, not the item's. The yaw part mirrors for the
  return swing and the roll does not: for a display yaw of -90 that makes the return's blade the exact mirror image
  of the first swing's (`PlayerModelTests.TheDrawnBladeLeadsAheadOfTheForearmThroughTheCutAndTheReturnSwingIsItsMirrorImage`,
  which measures the item as drawn: the blade sprite's tip is its (1, 1, 0) diagonal and its edge the (1, -1, 0) one,
  both through `DisplayMatrix(offset)` and the hand anchor).
  The **shoulder socket** (`LimbFrames.ShoulderSocketInward`, 0.09) slides the joint in toward the torso as an arm lifts off
  hanging (`LimbFrames.SocketWeight`), so a raised arm stays seated on the body instead of hinging on a point 0.1 outside it
  with a gap; the punch IK includes it. `SliceImpactProgress` (where the damage lands) is the middle of the strike. Filmed by
  `visual-examples/animations-slice.json`.
- **Blade swing frames** (`Posing/BladeSwingFrame`): everything the body reads of a blade swing in progress (the
  arm's sweep yaw, the shoulder and elbow it is eased to by the stroke, a hand lift re-solved through the arm, the
  body's turn, twist, roll, dip and lean, the shoulder pull, the wrist carry's roll, cant and yaw and the display
  offset) is one struct, `PlayerModel.Blade`, built by `SliceMotion.FrameAt` for a slice and `SwordMotion.FrameAt`
  for a sword swing. `BodyPartMatrices`, `SecondaryMotion`, `ShoulderReach` and `HeldItemRotationOffset` read only
  that, so a new swing is a new shape, never a new branch in the body code, and only one blade swing plays at a
  time (`StartSlice` cuts a sword swing short and `StartSword` a slice).
- **Sword** (`SwordMotion`, one `Shape` record per `SwordSwing`): a long blade is swung from the shoulder, not
  carried across at chest height, so each swing drives the shoulder flexion and the elbow themselves (a wound
  value through the wind-up, carried to a far value through the strike) instead of an extended arm plus a hand
  drop. The **cut** (0.85 s) winds the arm high over the right shoulder, the elbow folded so the blade lies back,
  the body coiled toward it, then sweeps across and down to low on the left with the body turning hard (a
  `BodyYaw` wider than the slice's) and dipping at the impact; the **back cut** (0.75 s) is its mirror in side and
  height, low on the left rising to high on the right, a little quicker; the **chop** (1.1 s) raises the arm
  straight overhead with the blade cocked behind the head, reaching the top three quarters through its wind-up
  and holding there (`WindupReach`, the anticipation), then whips the arm straight down the front while the torso
  folds forward (`Lean`) and the weight drops to full depth by the impact (`HeldDip`, deeper than any cut), rests
  at the bottom (`HoldEnd`) and unwinds slowly. The wrist carries the blade as in the slice: the cuts keep the
  slice's edge roll, cant, leading yaw and display offset (mirrored for the back cut), the chop carries the blade
  straight along the forearm, so it points down and forward at the bottom of the blow. The far poses are placed
  for the first-person view as much as the third (the comment above the shapes has the two rules: a forward fold
  tips the arm down with the torso, a turn toward the sword side swings that shoulder behind the eye), so every
  impact is on screen while a wind-up may rise out of the frame and a follow-through may leave it. Each
  swing's damage lands at its own `Impact`, which the hit-stop holds at. The item debugger's CUT, B-CUT and CHOP
  modes and `visual-examples/animations-sword.json` (`VOXEL_TIME=147`, the three swings from the shoulder camera,
  then the cut and the chop in first person) show them; `PlayerModelTests` holds the shapes (the cut comes down
  from high right to low left, the back cut rises the other way, the chop goes over the head and straight down
  the middle with the blade pointed down at the end, the chop sinks and folds the body further than a cut, no
  swing snaps at either end, every shoulder stays on the torso) and `HitStopTests` the hold.
- **Attachment** (`PlayerModel.GetPartMatrix`, its body in `Posing/BodyPartMatrices`): the neck and both shoulders are children of the **torso
  frame** (hip-origin, turning with the torso's lean, twist and roll), so a limb can never come off the body
  or sink into it; only the legs and the pelvis hang off the root. While a tuned pose is showing (the slice
  sweep, the cocked throw, the punch) the torso's twist is taken back out of that arm's yaw so its hand path is
  the one authored in body space; an idle arm just follows the torso round. The punch IK is solved from where
  the torso actually carries the shoulder (`PunchPose.PunchTargetFromShoulder`), not from its rest place. On top of that a
  reaching arm **pulls its shoulder** a little toward where it reaches (`ShoulderReach.PullTarget`, spring-smoothed so a
  thrust or release trails and settles, at most `MaxShoulderPullBlocks` = 0.08) and the torso leans toward the
  pulled side. `PlayerModelJuiceTests.TheShouldersAndTheNeckStayOnTheTorsoThroughEveryAnimation` runs every
  debugger mode and asserts that no shoulder or neck is ever farther off its place on the torso than the pull bound.
- **Swim stroke animation**: `PlayerModel` runs the physics' stroke clock (`SwimStrokeProgress`, started
  by `TriggerSwimStroke`; a remote model with `AutoSwimStroke` starts its own while it moves): the arms
  reach forward together, thrust out to the sides (slightly behind the shoulders) in the first 20 %,
  hold for a beat, then creep back to the front over the rest, where the cycle rests until the next
  stroke. The legs snap straight with the thrust and draw up during the recovery, and the whole body
  tips about the hips toward the heading (`Update(..., swimForward:, swimSide:)`: forward when
  swimming ahead, back when backing off, rolled into a strafe, plus a lunge at the burst).
- **Surface float** (`PlayerModel.Update(..., floating:)`, `FloatBlend`, the `SwimPose.Float*` constants): a body at
  rest in `WaterPhase.Float` is upright and straight (`SecondaryMotion.ComputeMods` drops the swim tilt and roll by the
  float's share), its arms scull slowly beside the hips (upper arms a little out and forward, forearms
  bent forward, the hands sweeping out and in together) and its legs hang with a faint flutter. It eases
  in over the swim pose through `MathUtils.Damp` and back out during a stroke (a floater swimming off
  strokes in the swim pose) or once the body stops floating. `LocalPlayer.UpdatePlayerModel` feeds the swim
  pose only from `Float` up (a wader walks with no water pose) and `floating` in `Float`. The item
  debugger's `SURFACE` mode (`ITEM_DEBUGGER_PLAYER_ANIM=surface`) previews it, `SurfaceFloatPoseTests`
  locks it down and `visual-examples/water-depth.json` (`VOXEL_TIME=147`) films a poured puddle (wade),
  the deep sea's float, a stroke hop and a dive (swim) from the shoulder camera.
- **Player model** (`PlayerModel.cs`): the existing `Pose` is post-processed by `SwimPose.ApplySwim` and
  `SecondaryMotion.ApplyMods` (`ComputeMods`): a landing dip spring (knees, hips, torso, root drop), crouch
  (`Update(..., sneaking:)`), a swim stroke (`swimming:`), the hit flinch (`Hurt(fromFront)`), torso
  twist and sway in the gait, the slice twist and weight shift, the throw lean, the off-hand counter
  on a punch, a heavier swing for a slower tool (`Punch(weight)`), the first-person arm lag behind
  the look (`SetLook`), the equip dip (`NotifyEquip`), the body-yaw lag with a head that leads
  (`BodyYawLags`, third person and remote players only) and the death fall (`DeathFall`). The limbs
  are still not parented to the torso, so torso-only motion stays small.
- **Camera** (`Camera.cs`, its view juice in `HeadMotion.cs`): the view effects fold into `HeadMotion`'s `_bobHeight`, `_roll` and `CurrentFov`, which
  the view cache is already keyed on, so none needs a key of its own: a landing dip, a directional
  hit kick (`AddHitKick`), an FOV pinch on a swing (`PunchFov`), a crouch eye drop and the death view
  (`DeathProgress`). The **sprint FOV** widening (`SprintFov`, knobs `SprintFov*` in
  `PlayerConfig`) is its own eased term *added* to the base FOV and the pinch: the sprint key
  with movement on foot or flying, never sneaking or swimming, scaled by the speed and clamped at the
  on-foot sprint, so the 10x flight sprint widens no further. The **strafe tilt** (`StrafeTilt`,
  `StrafeTilt*` knobs) is likewise an eased term *added* to `_roll`: positive roll tips the view's up
  axis toward the camera's right, so a strafe right leans the head right, into the move; off while
  flying or dead. **Sneak** is Left Ctrl on foot (`Camera.IsSneaking`): slower, never sprinting, a
  lowered *view and model only* — the body and its collision keep their size, so the server's
  movement rules (which derive the feet from the eye) are unaffected.
  The **throw charge zoom** (`ThrowZoom`, `ThrowZoom*` knobs) is a third FOV term *added* beside the
  sprint widening and the pinch: `LocalPlayer.UpdatePlayerModel` copies the arm's `PlayerModel.ThrowCharge`
  into `Camera.ThrowCharge` (like `HoldPunchFov`), and the next head-motion update springs the view
  toward `ThrowZoom.TargetDegrees` (narrower with the eased charge, 0 with none) through an
  under-damped `Spring`, so a release or cancel springs back with a small wide overshoot, clamped at
  half the zoom. Singleplayer and a client alike (it reads only the local arm);
  `visual-examples/throw-zoom.json` films it, `ThrowZoomTests` locks it down.
- **Hit-stop** (`SwingHitStop`, `HitStop*` knobs): a punch or slice that connects
  (`LeftClickAction.UpdatePunch`/`UpdateSlice`; on a client the *predicted* connect, a mob in the cone or
  hemisphere with line of sight, since the server's verdict comes too late) calls
  `PlayerModel.HitStop`, which holds that swing at its impact pose (full extension, or
  `SliceImpactProgress`) and then replays the rest faster, so the swing still ends when it would have:
  the slice combo, the jab pace and `AttackPacing` never see it, and the world, mobs and movement
  never stop. `Camera.HoldPunchFov` holds the pinch while `PlayerModel.IsHitStopped`. The item
  debugger never requests one. `visual-examples/hit-stop.json` films it.
- **Death** plays for `RespawnPrompt.DelaySeconds` (the view tips over, the body falls), then the
  **death screen** (`MenuRenderer.DrawDeathScreen`, `DeathScreenLayout`, drawn by
  `UiPasses.RenderDeathScreen`) covers the world with the death line and a RESPAWN button, in
  singleplayer and on a client alike. While it is up `GameplayInput` stands every game input down
  (keys, mining, use, the wheel, ESC, the chat and the [E] panel; `SyncDeathScreen` frees the cursor
  and the look) and a click on RESPAWN or Enter asks for the respawn (`ClientSession.RequestRespawn`).
  There are no death drops and no bed: the respawn point stays where the world first settled.
- **Mob death poof.** A mob bursts into skin-colored chips when its death animation ends
  (`BlockDebrisSystem` + `DebrisLook.Color`, `LandingDust.SkinColor`), the same pool the slime
  splat uses.
- **Placement pop.** A placed block pops in a little too big and settles with one small overshoot
  (`SelectionPasses.RenderBlockPop`, `PopBoxEmitter`).
- **Mob hit sparks.** A punch, slice or throw that connects throws pale chips from the wound point
  on top of the floating damage number.
  The wound also spurts **gore** (`BlockDebrisSystem.Gore`, `JuiceConfig.Gore*`): droplets of the kind's `blood` (its entity file:
  omitted is red, `"none"` sprays nothing (skeletons, the Skeleton King), `{"color": [r, g, b]}` sRGB 0..255 (bluish for the
  spiders and scorpion, yellow-green for the dragonfly, and the slime's green with `"size": 2.5`, an optional multiplier on each droplet (`EntityType.BloodSizeScale`, `EntityLoader.ParseBloodSize`; default 1)); `EntityType.BloodColor`, parsed by `EntityLoader.ParseBlood`,
  `EntityBloodTests`) out the far side of the blow, more on a kill.
- **Drop landing bounce.** A drop that lands hops back up once or twice, damped by `BounceDamping`,
  so a pickup reads as physical.
- **Tool break shatter.** A tool whose durability runs out shatters into chips of its own sprite
  colors instead of just vanishing.
- **Pickup sparkle.** A few bright `FireParticleKind.Spark` motes pop at the chest where a drop is
  collected.
- **Ground dust** (`GroundDust`, owned by `World`; cosmetic, so never on a server): a landing kicks
  up chips of the block under the feet (`GroundDust.TryFindSurface`: the feet's own cell if it holds
  a stack cover like a snow drift, else the cell below), sized by `LandingDust` (nothing under
  `LandingDustMinFallBlocks`, then count and launch speed grow with the drop along
  `MathUtils.ImpactStrength`, both capped). They go through `BlockDebrisSystem.Puff` as
  `DebrisLook.Surface` chips, which wear the block's top on every face (`BlockCubeCache.GetSurface`), so
  grass dust is tinted green. The local player's landings come from `GroundDust`'s own
  `FallTracker.LandedDrop` (singleplayer and a client); a mob reports its landing with
  `Mob.NoteLanding` (the goblin's and slime's physics do) and `World` takes them after `RefreshMobs`
  (singleplayer only: a client's mobs are mirrors and never land). A slime splats its own goo instead,
  on every hop (`LandingDust.SplatCount`/`SplatSpeed`, linear in the impact like its squash kick, scaled
  by the slime's body scale `SlimeSize.Scale(size)`): `DebrisLook.Color` chips with an air id, one gray cube
  (`BlockCubeCache.GetColorChip`, `JuiceConfig.ColorChipTile`) painted through `uBiomeTint`
  with `BiomeTint.VertexTint` of the mean opaque skin color (`LandingDust.SkinColor`). Sprinting
  on foot over a `footstepDust` block (grounded, not sneaking, not in water, really moving) puffs once
  per foot strike of the gait: `PlayerModel.WalkPhase` crossing π/2 + kπ (`FootstepDust.FootfallsBetween`).
- **World-event effects** (cosmetic: singleplayer and a client, never a headless server). A TNT blast
  shakes the local camera (`AmbientEffects.ShakeForBlast`): `ShakeFalloff.Trauma` is `BlastShakeStrength` inside
  `TntManager.BlastRadius`, falls off as radius / distance and eases to zero at `BlastShakeRangeBlocks`,
  never growing with distance. Singleplayer reads the centers `TntManager.Blasts` reports for the frame;
  a client, which never ticks TNT, takes a primed-TNT mirror leaving the server's snapshot with its
  locally burnt fuse within `BlastMirrorFuseSlackSeconds` as the blast (the server drops a cube only when
  it detonates). Tests: `BlastEffectTests`. A sand or gravel cube that settles throws a skirt of its
  own chips (`GroundDust.LandBlock` -> `BlockDebrisSystem.Skirt`, which starts each chip just outside
  the cell's sides so none spawns inside the solid block), sized by `LandingDust.FallingBlockCount` /
  `FallingBlockSpeed` from the fall (`FallingBlock.FallBlocks`, from the highest point the cube
  reached; none under `FallingBlockDustMinFallBlocks`, none under water): singleplayer reads
  `FallingBlockManager.Landings` (each cube written back as a block that frame), and a client puffs
  where a `BlockChanges` entry turns a cell into a `falls` block beside a falling-cube mirror of that
  block in the newest or previous snapshot, as if it fell `FallingBlockDustClientFallBlocks`
  (`FallingBlockDustTests`). Lava hardening gives off steam: `FluidManager.HardenedCells` lists every
  cell contact hardening turned solid in the frame (the touching rule and a flow arriving hardened),
  and `AmbientEffects.PuffSteam` puffs `SteamPuffSmoke` pale `FireParticleKind.Steam` particles (drawn blended
  like smoke, lighter, quicker and briefer) off the top of each through `FireParticleSystem.Puff` on
  the campfire instance, at most `SteamPuffMaxCellsPerFrame` cells within `FireParticleRadius` a frame,
  since a waking cave pocket hardens dozens a frame far below; a client puffs where a `BlockChanges`
  entry turns a fluid into its own `hardensTo` block (a flow crusting against lava is missed there)
  (`SteamPuffTests`). A felled tree hitting the ground thuds: `TopplingTree.TryTakeLanding` is true
  exactly once, on the update its first hard impact lands (or it comes to rest), and `BlockEditor.ThudTree` then takes the cells the blocks
  are in (`TopplingTree.GatherLanded`, the cells `TakeDrops` drops at), bursts
  `LeafParticleBurstThud` leaves off the landed canopy, kicks the ground's dust up under at most
  `TreeThudDustPoints` points of the landed trunk (`GroundDust.TreeThud`) and shakes the camera by its
  distance to the trunk (`ShakeFalloff`, `TreeThudShakeStrength` scaled by the wood that fell over
  `TreeThudFullWoodCells`). Singleplayer only: a server skips it, and a client sees no toppling tree
  at all (`TreeThudTests`). Anything that enters or leaves water splashes: every body keeps a
  `WaterSplashTracker` (the player's in `AmbientEffects`, a field on `Mob`, `ItemDrop`, `ThrownProjectile`,
  `FallingBlock` and `PrimedTnt`), which `AmbientEffects.UpdateSplashes` steps each frame through
  `WaterSplash.Track` from the body's lowest point (the player by `Camera.IsInWater`, the rest by that
  point being under its cell's fluid surface). The tracker measures the vertical speed from the body's
  own height between frames (so a client mirror's 10 Hz jumps read as the speed over the snapshot),
  seeds on its first call, ignores a teleport-sized step and treats a body still for
  `WaterSplashStaleSeconds` as still; on a crossing it reports the entry's downward or the exit's upward
  speed, and `WaterSplash.Count` / `Speed` (none under `WaterSplashMinSpeed`, growing along
  `MathUtils.ImpactStrength` to `WaterSplashFullSpeed`, the count scaled by the body: a mob by its width,
  drops and projectiles `WaterSplashSmallScale`, cubes `WaterSplashCubeScale`) size a ring of pale
  `WaterSplashColor` `DebrisLook.Color` droplets (`BlockDebrisSystem.Splash`) thrown up from the water
  top of the column crossed (`ChunkManager.GetFluidColumnTopGlobal`), never off lava. Every crossing (even one too gentle for droplets) also starts a **ripple** (`RippleSystem`, owned by `AmbientEffects.Ripples`; rule `WaterRipple`, GL-free: the leading edge runs out at a near-steady speed (`RippleSteadyShare`) over a life, a reach and a peak slope that grow with `WaterSplash.Strength`, the slope thinning with the square root of the spread (`SpreadShare`) and fading to nothing at the end, the wavelength shorter for a small body). Nothing is drawn for it: the water shader tilts its own normal by the ripple's slope, so the mirror, the refraction, the fresnel and the sun glints bend in rings (see "Water rendering": `RippleField` packs the `RippleShaded` ripples nearest the eye into `uRippleShape`/`uRippleWave`, `RippleWave` is the GL-free twin of the wave packet: calm ahead of the edge, a train of `RippleTrainWaves` waves behind it that shorten toward the back and whose crests run forward through it, calm again in the middle); only water tops within `RippleLevelTolerance` of the crossing's level take it, so a ring stops at the shore and never climbs a ledge; `ParticleConfig.Ripple*`; `WaterRippleTests`, `visual-examples/water-ripples.json` at `VOXEL_TIME=147`). A client splashes
  its own player, the mob mirrors and the primed-TNT mirrors (all persist across snapshots); its drops and
  falling cubes are rebuilt every snapshot, so they never splash there (`WaterSplashTests`). Torch
  heads burn: the mesher records the chunk-local point of every block with a JSON `flame` (turned to the
  cell's facing) in `ChunkMeshData.FlamePoints` beside `HotFluidVents`, kept on `Chunk.FlamePoints` by
  `UpdateMesh` and dropped by `Chunk.MarkMeshedHeadless`, since a server never draws one; each frame
  `AmbientEffects.UpdateTorchFlames` gathers the points within `TorchFlameRadius` of the eye from the
  `Renderable` chunks (nearest first, at most `TorchFlameMaxEmitters`) into a third
  `FireParticleSystem` (`TorchEmberPerSecond`, `TorchSmokePerSecond`, no spawn offset, the spawn scatter
  `TorchFlameJitter` and the particle scale `TorchFlameScale`, both constructor parameters, the scale
  shrinking a `FireParticle`'s size, life, speed and sway), drawn after the lava batch. No point lights:
  the torch's own block light lights it (`TorchFlameTests`; `visual-examples/torches.json` at
  `VOXEL_TIME=700` ends on close shots of a floor and a wall torch).
  `visual-examples/world-events.json` (`VOXEL_TIME=147`) films
  these effects; its blasts use the scenario op `prime` (`{ "op": "prime", "pos": [x, y, z] }`,
  `WorldItemUse.PrimeTnt`), a firestarter strike on the TNT block in that cell that never misses, since
  `give` cannot equip a tool instance and the strike's chance roll would make the scenario flaky.
- **Remote players** (protocol 7): the `PlayerState` flags carry cosmetic `Sneaking`, `Swimming`,
  `Sprinting` and `Airborne` bits (rebroadcast untrusted, never acted on) and every `PlayerList`
  entry ends with the player's last `NetPlayerAction` and a counter that moves per swing, so
  `RemotePlayer` poses the model and replays punches, slices, throws and staff casts (`NetPlayerAction.Cast`,
  added without a version bump; `TakeCast` hands the client's `SpellFeedback` one default-colored burst per cast).
  A seventh cosmetic bit, `NetPlayerFlags.Floating` (64), rides beside `Swimming` while the sender
  is in `WaterPhase.Float`, so others see the surface float (`PlayerModel.Update`'s `floating`); the
  client now sets `Swimming` only from `Float` up, so a wader walks for everyone. No protocol version
  moved: the server stores the flags byte raw and rebroadcasts it, and no reader rejects an unknown
  bit, so an older client just poses the swim (`GameServerTests.TheCosmeticFloatingFlagReachesTheOtherClientsUntouched`,
  `RemotePlayerAnimationTests`).
- **HUD feedback** (GL-free state fed once a frame from what the HUD already shows, so singleplayer and a
  client's mirrored inventory and health behave alike): `HotbarNameFade` shows the selected item's name
  over the hearts after a slot or item switch, `HotbarPickupPop` bumps a slot whose count rises (never a
  fall or an item change), and `LowHealthVignette.Intensity` is a slow red edge heartbeat below
  `LowHealthThreshold`, off while dead or creative, drawn as a second term (`uLowHealth`) of the damage
  vignette's one full-screen pass. `visual-examples/hotbar.json` and `low-health.json` film them.
  The **damage direction arc** is that pass's third term (`uHitArc`, `uHitArcDir`): a red glow on the
  screen edge toward where the last blow came from, held then faded (`DamageArc*` knobs). The camera
  owns a GL-free `DamageDirection` that keeps the blow's *world* direction and recomputes the screen
  direction from the current view yaw every frame (`Camera.DamageArcScreenDirection`), so the arc keeps
  pointing at the attacker while the player turns. It is fed by `Camera.TakeHit` (the mob's real
  knockback direction, so a goblin stone from 12 blocks points right, which the nearest-mob direction
  `LocalPlayer.NotifyPlayerHurt` gives `AddHitKick` would not) and, on a client, by `Camera.AddDamageDirection` from
  a `PlayerStatus` knockback that came with lost health; a fall or lava tick has no direction and shows
  none. `visual-examples/damage-arc.json` films it, `DamageDirectionTests` locks it down.
  **Surfacing droplets** are a fourth term of the same pass (`uDroplets`, `uDropletTime`,
  `uDropletSeed`): when the eye comes up out of water (not lava), hashed drops with a dark rim and a
  glint dot the screen, the bigger ones slide down leaving thin trails, and all fade out over
  `SurfacingDropletSeconds`. The camera's GL-free `SurfacingDroplets` carries the timing: it is told
  the frame's final eye state in `Camera.RefreshEyeWaterState` (the one the underwater tint reads, so
  the shoulder camera's eye counts), aged in `UpdateHeadMotion`, cleared the moment the eye is back
  under, and given a new seed per surfacing; the drop positions, sizes and slides are hashed in the
  shader. `visual-examples/surfacing-droplets.json` films it, `SurfacingDropletsTests` locks it down.
- **Slice trail** (`Particles/SliceTrail*`): a blade swing (the obsidian blade's slice and its return,
  the iron sword's cut, back cut and chop) leaves a glowing, curved crescent behind the blade tip that
  fades out within `SliceTrailLifeSeconds`, in first person and in the shoulder view. The tip and an
  inner point are read along the held sprite's (1, 1, 0) diagonal (`SliceTrailTipReach`,
  `SliceTrailInnerReach`) through `HeldItemPose.SpriteMatrix`, the very matrix the held item is drawn
  with. `SliceTrail` stores its samples in the swinging player's **owner frame** (the eye plus
  `PlayerModel.DrawnBodyYaw`, so a lagging body keeps the arc on the blade; pitch is not in it) and turns
  them back at build time, so the arc rides with a walking or turning player and camera motion alone adds
  nothing. A sample is committed at most once every `SliceTrailMinSampleSeconds` (the life divided over
  the ring, so the whole life fits `SliceTrailMaxSamples` at any frame rate, since VSync is off) after
  `SliceTrailMinStepBlocks` of travel, while a live head moves every update and keeps the newest end on
  the drawn tip. Ages run on `World.Now`: a hit-stop (`PlayerModel.IsHitStopped`) commits nothing while
  the trail keeps fading, the tip speed counts only the time the tip was really moving, and a paused
  world keeps fading at render time. A restarted swing progress starts a new segment, so a slice and its
  return are never bridged. `SliceTrailRibbon` builds the ribbon (Catmull-Rom, `SliceTrailSubdivisions`
  per gap): the tail tapers by position along the segment (`SliceTrailTailTaper`), the head by tip
  speed (`SliceTrailMinSpeed..FullSpeed`), the width shrinks and the alpha falls with age, and UV.x is
  the stable arc length the streaks are keyed on, so the wisps never crawl as the tail expires.
  `slice-trail-fragment.glsl` (twin `SliceTrailGlow`) draws a soft colored body brightening toward the
  tip edge, a white core band near it and hashed streak lanes (the `SliceTrailCore*`, `GlowPower` and
  `Streak*` knobs in `ParticleConfig`); the color is the item's `trail.color` (else
  `SliceTrailDefaultColor`), decoded from sRGB into the linear HDR target. `SliceTrailRenderer` draws
  it additively (`SrcAlpha, One`, depth test on, no depth write, no cull) in `WorldRenderer`'s late
  particle block after the clouds (`BillboardPasses.RenderSliceTrails`), never in the mirror or the
  shadow pass. `SliceTrailSystem` (World's component, fed right after the remote players update, never
  in server mode) runs the local trail and one per remote player: a remote's held item is not on the
  wire, so while it swings the client assumes, and `PlayerPasses.RenderRemotePlayers` draws, the first
  item with that attack pattern (`SliceTrailSystem.AssumedBlade` over `ItemRegistry.FindByAttack`). The
  item debugger's trail is fed per fixed step, so a scrub rebuilds it (see "Build and run").
  `visual-examples/slice-trail.json` (`VOXEL_TIME=147`, using the `player` op's `slicereturn` action)
  films every swing; `SliceTrailTests`, `SliceTrailRibbonTests`, `SliceTrailGlowTests`,
  `SliceTrailSystemTests`, `SliceTrailContentTests` and `ServerWorldTests.AServerModeWorldFeedsNoSliceTrail`
  lock it down.
- **Floating damage numbers**: a landed hit floats its damage over the mob (`World.DamageNumbers`, a
  fixed `DamageNumberPool` of `DamageNumberCapacity` that replaces its oldest number when full),
  drawn by `DamageNumberRenderer` right after the healthbars with their billboard shader and the HUD's
  font atlas. Singleplayer sees every accepted `Mob.TakeDamage` through `Mob.HitsTaken` /
  `LastHitDamage` (compared around `MobStrikes.StrikeMob`, which punch, slice and throws all use); a client,
  which never damages a mob, floats the health drop between two `EntityList` snapshots, plus the health
  left on a mob that leaves the snapshot within `DamageNumberDeathRangeBlocks` of the camera (the
  server stops listing a mob the moment it dies, so that is the killing blow). A headless server keeps
  none. `visual-examples/damage-numbers.json` films them.
- **Verification**: `visual-examples/animations.json` (run with `VOXEL_TIME=147`) films the player's
  poses from the shoulder camera and the mobs' spawn, hit and death; its `damage`, `view` and
  `player` ops (`hurt`, `punch`, `slice`, `slicereturn`, `cut`, `backcut`, `chop`, `axechop`, `equip`, `charge`, `release`, `throw`, `draw`, `loose`, `cast`, `die`, `revive`) exist for it. For
  iterating on a single animation prefer the **item debugger** (see "Build and run"): its ITEMS tab has a
  button for every player animation and its ENTITIES tab previews the goblin and slime life-cycles,
  including the slime's code-driven squash, with `ITEM_DEBUGGER_PLAYER_ANIM` / `ITEM_DEBUGGER_ANIM` and
  `ITEM_DEBUGGER_SHOT_AT` for scripted captures. A new player animation gets a mode in
  `PlayerPreviewState`, a new mob animation a mode in `EntityPreviewState`.
