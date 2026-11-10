# USER_TODO — placeholder art to replace

> Every stand-in asset an agent adds goes here: the file, what made it, and what a replacement has
> to keep. Tick an entry and delete it once the real art is in. If a `tools/` generator wrote the
> file, remove or repoint that generator in the same change, or a re-run puts the placeholder back.
> Current list as of 2026-11-09. Sound is not tracked here: features that need it wait in
> `plans/TODO.md` (`M-26`).

## Items

Paths are under `OpenGLTest/assets/textures/items/`. All sprites are 32x32 RGBA unless noted, with
transparency around the shape.

### Armor (all pieces)

- [ ] **Leather set** — `leather_cap.png`, `leather_tunic.png`, `leather_pants.png`,
  `leather_boots.png`, `leather_gloves.png`.
- [ ] **Bronze set** — `bronze_cap.png`, `bronze_tunic.png`, `bronze_pants.png`,
  `bronze_boots.png`, `bronze_gloves.png`.
- [ ] **Iron set** — `iron_cap.png`, `iron_tunic.png`, `iron_pants.png`, `iron_boots.png`,
  `iron_gloves.png`.
- [ ] **Cloud boots** — `cloud_boots.png`.

A set's five pieces share one silhouette and bevel, dressed per material (stitching on leather,
rivets on metal), so keep a replacement consistent with its set. A worn piece's shell is tinted by
the **mean color of its sprite** (`ArmorLook.ColorOf`), so replacing one also changes the color the
body wears.

### Accessories

- [ ] **Charms** — `bone_charm.png`, `ember_charm.png`, `feather_charm.png`, `fire_charm.png`,
  `frost_charm.png`, `gill_charm.png`, `magnet_charm.png`, `reserve_charm.png`. Each should read
  as its own ability (bone, ember, feather, fire, frost, gill, lodestone, reserves).
- [ ] **Amulet of vigor** — `amulet_of_vigor.png`.

### Staffs

- [ ] **Wooden staff** — `wooden_staff.png`.
- [ ] **Crystal staff** — `crystal_staff.png`. Keep it visually above the wooden staff (the
  `advanced` tier).

### Consumables

- [ ] **Mana draught** — `mana_draught.png`.
- [ ] **Teaching scrolls** — `scroll_fire_projectile.png`, `scroll_frost.png`,
  `scroll_modifiers.png`, `scroll_nature.png`, `scroll_self.png`. Keep them readable at hotbar
  size and distinct from one another.

### Tools and weapons

- [ ] **Bow** — `bow.png`. No shot yet; the two-handed draw pose (`UsePose.Bow`) already uses the
  held sprite.
- [ ] **Smithing hammer** — `smithing_hammer.png`.
- [ ] **Thumper** — `thumper.png`. Made by `tools/generate-sandworm-model.py`: re-running puts the
  placeholder back (`--check` compares).
- [ ] **Spear** — `spear.png`. `throwAligned`: the sprite's tip must sit on its (1, 1) diagonal
  (up-right), or the thrown copy does not fly point first.

### Drops

- [ ] **Bone** — `bone.png`. Made by `tools/generate-skeleton-model.py`.
- [ ] **Bloom** — `bloom.png`. The bloomery output.
- [ ] **Sandworm fang** — `sandworm_fang.png`. Made by `tools/generate-sandworm-model.py`.

### Tin (everything but the ring)

- [ ] **Tin ingot** — `tin_ingot.png`.
- [ ] **Tin ore (item)** — `tin_ore.png` (the raw ore chunk, not the block tile below).
- [ ] **Crushed tin** — uses `tin_ore.png` too (`data/items/crushed_tin.json` points at the same
  sprite), so replacing that one file covers both.
- [ ] **Tin tool heads** — `tin_axe_head.png`, `tin_pickaxe_head.png`, `tin_sword_head.png`
  (the sword head is 42x42).
- [ ] **Tin tools** — `tin_axe.png`, `tin_pickaxe.png`, `tin_sword.png`. The sword is 42x42 (the
  large-sword exception); everything else is 32x32. Keep the tool-family silhouettes so tin reads
  as the same tools in a weaker metal.

## Blocks

Paths are under `OpenGLTest/assets/textures/blocks/`. All tiles are 32x32; no `tools/` generator
writes them, so replace the PNG directly. Opaque `cube_all` tiles are greedy-merged and must stay
fully opaque and tileable on both axes.

### Hell and sky realms

- [ ] **Scorched hell rock** — `scorched_hellrock.png`.
- [ ] **Charred log** — `charred_log_side.png`, `charred_log_top.png`. The Ashlands' leafless tree;
  keep the side and end reads of a burnt log.
- [ ] **Fungal tree** — `fungal_log_side.png`, `fungal_log_top.png`, `fungal_cap.png`. The cap is
  the tree's leaves (opaque tile, teal glow). `fungal_soil.png` and `fungal_moss.png` are confirmed
  fine and are **not** placeholders.
- [ ] **Crystal tree** — `crystal_log_side.png`, `crystal_log_top.png`, `crystal_leaves.png`.
  The leaves are cutout and glow pale cyan.
- [ ] **Sky oak leaves** — `sky_oak_leaves.png`. Cutout and swaying; untinted (the sky has no
  climate of its own), so keep transparent holes and a canopy silhouette.

### Building

- [ ] **Oak planks tile** — `oak_planks.png`. Fully opaque and tileable on both axes (greedy walls
  repeat it). The slab, stairs and fence crop it with default UVs, so any region can show.
- [ ] **Oak door tiles** — `oak_door_bottom.png`, `oak_door_top.png`. Fully opaque; the top tile
  sits directly over the bottom one (no frame row where they meet). Keep the handle on the vertical
  center line, or move it only with mirrored explicit UVs on one broad face (the baker mirrors
  side-face UVs).
- [ ] **Oak trapdoor tile** — `oak_trapdoor.png`. Fully opaque (holes would need `cutout: true` on
  `oak_trapdoor.json`); the models crop it with default UVs.
- [ ] **Anvil** — `anvil.png`. The model crops it with default UVs across the base, waist, top and
  horn, so every part must read on its own. Keep it symmetric.
- [ ] **Cloud block** — `cloud_block.png`. A soft white mottle whose alpha is about 0.7; keep the
  alpha, since it is drawn in the translucent pass.
- [ ] **Slime block** — `slime_block.png`. Opaque, green gel in 4x4-texel cells with rims and
  glints; tileable on both axes (greedy-merged faces repeat it).

### Chests

One shared `block/chest` model, 32x32 tiles, front/side/top each.

- [ ] **Wooden chest** — `chest_wooden_front.png`, `chest_wooden_side.png`, `chest_wooden_top.png`.
- [ ] **Iron chest** — `chest_iron_front.png`, `chest_iron_side.png`, `chest_iron_top.png`.
- [ ] **Bronze chest** — `chest_bronze_front.png`, `chest_bronze_side.png`, `chest_bronze_top.png`.
- [ ] **Ruin chests** — `chest_ruin_surface_*`, `chest_ruin_underground_*`, `chest_ruin_sky_*`,
  `chest_ruin_hell_*` (front, side and top each).

The model crops each face with default UVs, and the front tile's lock plate lands across the seam
between lid and body, so keep the plate centered on that seam.

### Waystones

Each realm has a stone tile and a lower/upper rune tile.

- [ ] **Surface waystone** — `waystone_surface_stone.png`, `waystone_surface_runes.png`,
  `waystone_surface_runes_upper.png`.
- [ ] **Underground waystone** — `waystone_underground_stone.png`,
  `waystone_underground_runes.png`, `waystone_underground_runes_upper.png`.
- [ ] **Hell waystone** — `waystone_hell_stone.png`, `waystone_hell_runes.png`,
  `waystone_hell_runes_upper.png`.
- [ ] **Sky waystone** — `waystone_sky_stone.png`, `waystone_sky_runes.png`,
  `waystone_sky_runes_upper.png`.

The lower shaft crops the rune tile's columns 10..21, rows 0..21; the upper shaft crops rows 12..31,
so the upper tile must continue the lower one across the cell seam. The stone tile dresses the
plinth and cap; both models are symmetric.

### Other

- [ ] **Tin ore block** — `tin_ore.png` (the block tile; the item sprite above is a separate file
  with the same name). Opaque and tileable.
- [ ] **Lily pad** — `lily_pad.png`. Cutout; the model crops the whole tile on top and a one-texel
  edge on the sides.
- [ ] **Hollow log insides** — `hollow_oak_log_inner.png`, `hollow_oak_log_end.png`,
  `hollow_spruce_log_inner.png`, `hollow_spruce_log_end.png`,
  `hollow_stone_pine_log_inner.png`, `hollow_stone_pine_log_end.png`. The end tile's default UVs
  crop its outer 3/16 band, so keep a readable cross-section ring. The outside bark is the species'
  own log tile and is not listed here.

### Battle tower locks and altar

All three made by `tools/generate-tower-lock-textures.py`: re-running puts the placeholders back
(`--check` compares), so remove or repoint that generator with the replacement.

- [ ] **Tower gate** — `tower_door.png`. 32x32 RGBA, a cutout with binary alpha (fully clear gaps,
  fully opaque iron): a `cube_all` portcullis seen through from both sides, with two cells stacked in
  each stairwell, so its frame should still read where the cells meet.
- [ ] **Skeleton pile** — `skeleton_pile.png`. 32x32 RGBA, fully opaque. The `skeleton_pile` model
  crops it with default UVs: the base's sides take rows 26..31 across columns 4..27, its top the
  square rows/columns 4..27; the skull's sides take rows 18..25 across columns 10..21 (the sockets
  sit there) and its top the square rows/columns 10..21.
- [ ] **Tower altar top** — `tower_altar_top.png`. 32x32, fully opaque: the top face of the
  `tower_altar` (a `cube_bottom_top` whose sides and bottom are `stone_bricks`), so keep its rim in
  the stone-brick grays where it meets the sides. The block glows (light 7), so a crystal or rune
  center that reads as lit fits.
- [ ] **Tower forcefield** — `tower_forcefield.png`, made by `tools/generate-forcefield-texture.py`
  (its own script, so re-running it touches nothing else). 32x32 RGBA, mostly transparent (alpha
  ~10%, edges ~45%): the roof dome's `cube_all` block draws translucent, and in the game the world
  shader replaces its color and alpha with its own shimmer, so this tile only shows where that term
  is off (the ItemDebugger). Keep it faint and seamless; it needs no detail.

### Battle tower chest

- [ ] **Tower chest** — `chest_tower_front.png`, `chest_tower_side.png`, `chest_tower_top.png`. The
  worldgen-only `tower_chest` (BT-5) currently reuses the underground ruin chest's
  `chest_ruin_underground_*` tiles; same layout as the ruin chests (the shared `block/chest` model).
  Repoint `tower_chest.json`'s textures when they land.

### Cave biome ores

All three made by `tools/generate-cave-ore-textures.py` as palette swaps of `malachite_ore.png` (its
brown matrix kept, its six green mineral shades repainted by luminance rank, the way `tin_ore.png`
recolors it): re-running puts the placeholders back (`--check` compares), so remove or repoint that
generator with the replacement. Each must stay 32x32, fully opaque and tileable (a `cube_all`
block), and its own tile: the block's reflective `material` is keyed per tile, so a shared tile
would share it.

- [ ] **Amethyst ore** — `amethyst_ore.png`, the crystal caves' exclusive ore (violet).
- [ ] **Jade ore** — `jade_ore.png`, the fungal caves' exclusive ore (pale green).
- [ ] **Cinnabar ore** — `cinnabar_ore.png`, the magma-touched deep's exclusive ore (red).

## Entity models

Paths are under `OpenGLTest/assets/models/entity/`. Every model is a Blockbench `.bbmodel` with its
skin PNG beside it; the `tools/` generator writes both, so replace them by editing that generator
(or remove it in the same change), or a re-run puts the placeholder back (`--check` compares).

- [ ] **Skeleton Archer and Skeleton Warrior** — `skeleton.bbmodel`/`skeleton.png` (lanky and tall, 32 px, a bow
  under `ArmL`) and `skeleton_warrior.bbmodel`/`skeleton_warrior.png` (shorter and bulkier, 28 px, a copper dagger
  under `ArmR`), 64x64 skins in the bone palette. Made by `tools/generate-skeleton-model.py`. Keep the humanoid groups
  (head and arms under the torso), the clips (`idle`, `walk`, `melee` with `impact`, `shoot` with `release`, `hurt`,
  `death`, `spawn`) that `Skeleton.ValidateContent` checks, and whole `Head`/`Torso`/`LegL`/`LegR` parts: the armor
  shells (`SkeletonOutfit`) cover them.
- [ ] **Skeleton King** — `skeleton_king.bbmodel` and `skeleton_king.png` (128x128 skin, small
  palette: the skeleton's bone shades, gold, purple, red eyes). Made by
  `tools/generate-skeleton-king-model.py`. Keep the humanoid groups (`Torso`, `Head`, `ArmL`, `ArmR`,
  `LegL`, `LegR`, with the head and arms under the torso), the clip names (`idle`, `walk`, `slam`,
  `volley`, `summon`, `hurt`, `death`, `spawn`) and their events (`impact` on `slam`, `release` on
  `volley`, `summon` on `summon`), which `SkeletonKing.ValidateContent` checks at startup, and a
  height near 3 blocks (`MobConfig.SkeletonKingHeight`).
- [ ] **Dragonfly, Crow and Deer** — `dragonfly.bbmodel`/`.png` (man-sized, 128x128 skin: green thorax, banded blue
  abdomen, red eyes, cutout wing cells), `crow.bbmodel`/`.png` (64x64, blue-black, cutout feather tips) and
  `deer.bbmodel`/`.png` (a spotted stag, 128x128). Made by `tools/generate-critter-models.py`; models, rigs and clips
  only so far (an entity JSON each, no mob class). Keep the six humanoid group names each rig carries among its own
  bones (wings and forelegs are `ArmL`/`ArmR`), everything nested under `Torso`, the clip names listed in the
  generator's header, the `impact` event on `bite`, `peck`, `swoop` and `butt`, a `hurt` clip that starts and ends at
  the identity (it is added on top of other clips), and the `.bbmodel`'s UV layout.
- [ ] **Shardling** — `shardling.bbmodel` and `shardling.png` (64x64 skin, small palette: pale violet
  body, white-to-lilac faceted crystal spikes and shard hands, white eyes). Made by
  `tools/generate-cave-mob-models.py`. Keep the humanoid groups (`Torso`, `Head`, `ArmL`, `ArmR`, `LegL`,
  `LegR`, with the head and arms under the torso), the clip names (`idle`, `walk`, `throw`, `hurt`,
  `death`, `spawn`) and the `release` event on `throw`, which `Shardling.ValidateContent` checks at
  startup, and the `.bbmodel`'s UV layout (every cube face has its own rectangle of the skin).
- [ ] **Sporeling** — `sporeling.bbmodel` and `sporeling.png` (64x64 skin, small palette: brown stem
  body, a teal mushroom cap with pale spots, glowing teal eyes). Made by
  `tools/generate-cave-mob-models.py`. Keep the humanoid groups (as above), the clip names (`idle`,
  `walk`, `melee`, `hurt`, `death`, `spawn`) and the `impact` event on `melee` (its spore burst), which
  `Sporeling.ValidateContent` checks at startup, and the `.bbmodel`'s UV layout.
- [ ] **Magmaborn** — `magmaborn.bbmodel` and `magmaborn.png` (128x128 skin, small palette: black
  cooled rock split by orange glowing cracks, ember eyes and mouth). Made by
  `tools/generate-cave-mob-models.py`. Keep the humanoid groups (as above), the clip names (`idle`,
  `walk`, `melee`, `hurt`, `death`, `spawn`) and the `impact` event on `melee`, which
  `Magmaborn.ValidateContent` checks at startup, and the `.bbmodel`'s UV layout.
