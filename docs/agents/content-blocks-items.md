# Content: blocks, items, models, loot

The data-driven content pipeline, the block, item, loot and model schemas, multiblocks and the building blocks. Recipe for adding content: the `add-content` skill.

## Content: blocks, items, models and loot are data

Nothing about a block is hardcoded any more. `ContentRegistry` reads `assets/data` and
`assets/models` at startup, hands out ids and publishes read-only tables that the rest of
the engine indexes by id. **Adding a block means adding JSON, not editing C#.**

**Placeholder art.** Any asset an agent adds as a stand-in — a texture, sprite, model or skin — is
recorded in `USER_TODO.md` at the repo root in the same change: one checkbox per asset or group,
with its path, what generated it (a `tools/` script, if any) and what a replacement must keep
(canvas size, alpha or cutout, the UV regions its model crops, palette rules). Tick and delete the
entry once real art is in. If a `tools/` generator wrote the file, remove or repoint that generator
in the same change, or a re-run puts the placeholder back. Sound is not tracked there: a feature
that needs audio waits in `plans/TODO.md` (`M-26`).

Ids are `ushort` and chunk storage is palette-compressed (`Chunks/ChunkBlocks`: one id, a palette of up
to 256 ids with an index byte per cell, or a `ushort` per cell; the per-block state byte stays a `byte`): id 0 is air (it has no definition file, and a block may not be named `air`), then blocks
sorted by file name, then items sorted by file name. Blocks and items share one id space of up to
65,535 ids; the registry throws if they overflow, and `BlockTables.Allocate` sizes every per-id
table on `BlockType` to exactly the loaded id count. Code that has
to name a specific block or item asks for it by file name — `ContentRegistry.RequireBlock("stone")`,
`ContentRegistry.RequireItem("bucket")` — which throws naming the missing content at startup
instead of silently mis-indexing. Hot paths must not do that per voxel: worldgen and the LOD
mesher resolve their palette once in the owning feature's static constructor
(`TerrainGenerator.SurfaceBlock/SoilBlock/RockBlock/SeaBlock`, `TreeGenerator.LeavesBlock` — the
far LOD's fallback canopy leaf —, `PebbleGenerator.PebbleBlock`), a tree species resolves its
blocks once at load (`TreeSpecies.WoodId`/`LeavesId`), and everything else reads those ids. Best of all is not naming
a block: when the question is "what does this block do", add a data flag and read it (see the
convention below). Every content error names the file it came from.

### Startup order (`RenderManager.LoadResources`)

1. `ContentRegistry.Load("assets")` - parses items, loot tables, blocks and every model they
   reference, and assigns ids. No GL, no atlas.
2. `TextureAtlas.Build(gl, "assets/textures/blocks")` - loads the tiles into the array texture.
3. `ContentRegistry.BakeModels()` - bakes each block's model into atlas-ready quads and settles
   the defaults that need to know whether the result is a full cube (`occludes`, `light.passes`).
4. Shaders, `BlockCubeCache`, `ItemMeshCache`, `UIRenderer`, then the `World`.

The `World` (and with it every worker thread) is created last, so all content tables are
immutable by the time anything but the main thread can see them. Keep it that way.

### Block schema (`assets/data/blocks/<name>.json`)

Every field is optional except `model`. Parsing is case-insensitive and tolerates comments and
trailing commas.

| field | default | meaning |
|---|---|---|
| `model` | required | a `"block/cube_all"`-style path under `assets/models/`, **or** an inline model object with the same schema |
| `name` | file name | display name for inventory rows and preview headings (`BlockType.DisplayName`) |
| `hardness` | 5 | float damage the block absorbs before breaking; each hit deals the held tool's power (`Item.ToolPower`, bare hand 1), so 5 with a power-1 tool is five hits; `0` breaks on first contact (pebble). On a `tree_wood` block it is the hardness of a full-thickness cell, and a thinner cell scales down linearly with the thickness in its state byte to `wood.tipHardness` of it (`Mining.HardnessOf(id, state)`: `hardness * (tipHardness + (1 - tipHardness) * thickness / 15)`). See `Mining` |
| `requiresTool` | false | when true, a hit only damages the block if the held item belongs to one of the `tools` tags at or above the required power; the wrong kind or a lower tier deals 0 damage and no progress. When false, any tool or bare hand mines it and `tools` is ignored |
| `tools` | none | tag name to minimum tool power, e.g. `{"pickaxe": 1.0}` admits any item listed in the `pickaxe` tag (`assets/data/tags/pickaxe.json`) whose `tool.power` is at least 1.0; `0` admits any member. Only read while `requiresTool` is true; a tag no item provides warns yellow at startup |
| `solid` | true | collides with bodies (`BlockType.IsSolid`) |
| `movementSpeed` | 0 | how fast the player moves through the block, 0..1: `0` (default) is a solid collider, a positive value makes it non-blocking for the player and scales their speed while the body overlaps it (`1` = full speed), so leaves at `0.75` are waded through at 75% speed; never set on fluids |
| `friction` | 0.99 | grip of the block's top face, 0..1: `1` is instant response, lower slides more (ground response time grows with `1 - friction`); ice sets `0.95`. Only read from the solid block under the feet |
| `pebbleWeight` | 0 | density multiplier (0..1) for the natural pebble scatter (`PebbleGenerator`) when this block is the exposed surface under the pebble cell: `1` keeps the patch's full density (stone, gravel), a lower value thins the scatter (grass `0.3`), and `0` (the default, e.g. sand and sandstone) means the surface grows no rock piles |
| `softLanding` | false | a body landing on the block takes no fall damage (no spring back, unlike `bounce`); read over the footprint under a landing player's feet (`PlayerBodyProbe.LandingCushioned`), in singleplayer and on the server. Illegal on a fluid |
| `sink` | 0 | the top share of the cell, 0..0.5, a player body sinks into slowly (`PlayerConfig.SinkBlockSpeed`) until it stands on the solid part below (`SinkLayer`); the body counts as grounded in the layer. Only on a plain solid full-cell collider: refused on a fluid, a non-solid or pass-through (`movementSpeed`) block, a stack, tree wood, a `collidesByHitbox` block and a `bounce` block. Player body only |
| `footstepDust` | false | loose ground: a player sprinting on foot over it kicks up a tiny puff of its chips at every footfall (`FootstepDust`, `GroundDust`; sand, snow, dirt and gravel set it, grass does not). Illegal on a fluid |
| `occludes` | full cube && !cutout | hides a neighboring full cube's face (`OccludesNeighbors`) |
| `cullSelf` | false | also cull faces against a neighbor of the same block (glass) |
| `castsAO` | `solid` | darkens neighboring faces (`CastsAmbientOcclusion`) |
| `cutout` | false | alpha-tested texture; forces `occludes` off whatever the file says |
| `translucent` | false | drawn blended in the sorted translucent pass with the fluids (glass); forces occludes off; never casts a shadow |
| `sway` | false | leaf-wind sway in the vertex shader. A full cube (leaves) sways uniformly; a non-full-cube `model` (a plant) is rooted: its sway eases from 0 at the model's lowest vertex to full at its highest (`BlockType.SwaysFromRoot`/`SwayWeightAt`, carried in the vertex alpha) |
| `biomeTint` | none | color the block by climate (see "Biome-tinted grass and leaves"): `{"palette": p, "faces": {face: entry}}`, where `palette` is `grass` (default) or `foliage`, a face key is `north`..`down`, `side` or `all`, and an entry is `{"tile": t}` or `{"overlay": t, "base": b}`. `tile` replaces the face with a tinted tile, `overlay` draws a tinted cutout tile over it (`base` optionally swapping the tile beneath). Only on a block that bakes to a plain full cube and is not translucent; `overlay` not on a cutout block. `FaceTiles` stay the default tiles for icons, drops and the held block |
| `modelTint` | none | the sub-cell sibling of `biomeTint`: a palette name (`grass` default, or `foliage`) that tints **every** quad of the block's own baked model (`shade * the field`, bit 4 set) instead of per cube face. Only on a non-full-cube `model`-renderer block that is not translucent (a cross-shaped plant), and the model must wear a gray `_textureless` tile, since the tint multiplies the texel. The `tall_grass` / `dead_shrub` plants use it |
| `material` | none | reflective surface tuning for the ores: `metallic` (0..1, default 0 - an ordinary diffuse block), `roughness` (0..1, default 1, lower being a tighter highlight) and `normalStrength` (0..4, default 1, scaling the tile's normal map). Omitting the object costs nothing at render time. Keyed by *texture*, not by block - see the material atlas below |
| `light.emission` | 0 | 0..15 (`LightEmission`) |
| `light.color` | warm torch color | sRGB `[r, g, b]`, 0..255 each, of the block's light; only legal with `emission` > 0. Colors within 0.5/255 share a `BlockLightPalette` slot, and at most 8 distinct colors exist (the default included) - a ninth throws naming the file. `blue_lamp` is the example; five are in use (default, blue lamp, glow mushroom, the hell ember and fungal teal) |
| `light.passes` | `!fullCube` | whether voxel light travels through the cell (`PassesLight`) |
| `flame` | none | `[x, y, z]` in 0..16 model units: where a small flame burns (a torch's head, half a pixel above its stick). Authored facing north and pre-turned with a `facing` block (`BlockType.FlamePoints`, `FlamePointAt(state)`); the mesher records each cell's point in `Chunk.FlamePoints` and the world burns a flicker of embers and a wisp of smoke there (see "Animation juice"). Only on the `model` renderer |
| `renderer` | `"model"` | `model` (baked quads), `tree_wood` (a grown tree's wood: its shape, thickness and tree-graph links come from the state byte, see `### Trees`; requires a `wood` object, forces `occludes` and `castsAO` off and `light.passes` on, never meshes greedy, and refuses `cutout`, `translucent`, `fluid`, `falls`, `needsSupport`, `stackMax` and `hitbox`), `pebbles`, `branch_pile`, `snow_layers`, `fluid` or `quern` (procedural emitters), `multiblock` (the anchor cell of a structure, carrying the whole volume's pre-rotated model), `multiblock_part` (one of its other cells, solid and occluding but never meshed) or `pane` (a 2 px post plus an arm toward every horizontal neighbor that is a pane or a solid full cube, derived at mesh time by `PaneEmitter` from `PaneShape`, never stored; a lone pane is just the post, as in Minecraft; the raycast, outline and body collide with the same arms through `BlockShape`; refuses `hitbox`, `fluid`, `falls`, `stackMax` and `needsSupport`, and a solid pane always collides by its arms); a fluid's, layer block's or pane's model only supplies its tile (and the pane's icon) |
| `multiblock` | none | required on, and only legal with, `renderer: "multiblock"`: `{"part": "block/<name>", "source": "block/<name>", "size": n}` — the block filling the other cells (which must use `multiblock_part`), the plain block the volume is built from and reverts to, and the volume's edge length in cells, `size` 2..4 (default 2, the kiln; `BlockType.MultiblockSize`, `BlockSchemaParser.ParseMultiblockSize`; the state byte holds each offset in two bits, so no more). The anchor and its part must both set `hasItem: false` (a structure is built, never held), and `cutout`/`translucent`/`fluid` are rejected, since both halves are ordinary opaque solids to everything but the mesher. Both default `occludes` and `light.passes` to **true**: the structure's surface really does cover its neighbors, and its quads carry no cullface and are lit from the anchor's own cell, so light has to reach that cell or the whole thing renders black. The model is authored in 0..16 * size model units (0..32 for a 2x2x2) with the opening on **north**, no `cullface` on any face and no face covering a whole cell plane — see `### Multiblocks` below |
| `kiln` | none | marks a multiblock anchor as a kiln, which is what gives it `KilnManager`'s fuel, ignition, crucible slot and integrity sweep, and tunes it: `{"fuelCap": n, "meltSpeed": s, "canAlloy": b}`, every field optional (`{}` is the plain kiln). `fuelCap` 1..64 (default 4) is how many fuel units the hearth holds, `meltSpeed` above 0 and at most 8 (default 1) divides the melt interval (`KilnProfile.MeltSeconds` = `BaseMeltSeconds / meltSpeed`), `canAlloy` (default false) lets the crucible hold two metals and alloy them (see "Recipes, cast molds and metalworks"). Compiled into `BlockType.Kiln` (a `KilnProfile`, null on a non-kiln) together with the `KilnLayout` of the anchor's `multiblock.size` (`KilnLayout.ForSize`: 2 is the mud brick kiln's model, 3 the alloy furnace's), so a kiln's model must follow the layout of its size and a size with no kiln layout is refused; an out-of-range value throws naming the file. Only legal on the `multiblock` renderer |
| `bloomery` | none | marks a multiblock anchor as a bloomery (see "The bloomery and the anvil"): `{"oreCap", "fuelCap", "ore", "bloom", "slag"}`, caps 1..64 (default 4), the three names required and resolved at load (`BloomeryProfile`, `BlockType.Bloomery`). Only on the `multiblock` renderer, never with `kiln` |
| `anvil` | none | marks a plain model block as an anvil: `{"hits", "input", "result", "slag"}`, hits 1..32 (default 3), the three names required (`AnvilProfile`, `BlockType.Anvil`). Not on a multiblock |
| `spellBuilder` | false | the block is a spell table: a right-click with a staff in hand opens the spell builder (`BlockType.SpellBuilder`, `BlockInteractions.TryOpenSpellTable`, after the other stations and before the staff cast; a block in hand places against it instead). No block entity, no state; legal on any block. `spell_table` |
| `waystone` | false | the block is a waystone (`BlockType.Waystone`): a right-click discovers it and a discovered one teleports to the others. Its name, first discoverer and lost flag live in `WaystoneManager` (see "Waystones"), so the block keeps no state; legal on any block. With an `upperHalf` (the shipped ones) the upper half carries no flag: a click on it opens the waystone below, and the waystone stands only while both halves do. `waystone_surface`, `_underground`, `_hell`, `_sky` |
| `pane` | glass pane | only legal with `renderer: "pane"`: the post and arm dimensions in pixels, `{"post": p, "arm": a, "rails": [[bottom, top], ...]}` (`PaneProfile`): a full-height post `p` (1..16) wide, arms `a` (1..`p`, default `p`) wide filling each `rails` height band (ascending, apart, inside 0..16, default `[[0, 16]]`). Omitted is the glass pane's 2 px post and arms over the full height, drawn byte-identically to before. Panes join only panes of the same dimensions (interned, so equal dimensions are one `PaneProfile`) and solid full cubes, so a fence never reaches into a glass pane. `oak_fence` is `{"post": 4, "arm": 2, "rails": [[6, 9], [12, 15]]}` |
| `wood` | none | required on, and only legal with, `renderer: "tree_wood"`: how hardness and loot follow the thickness stored in each cell's `WoodState` (code 0 = 1 px across .. 15 = full cube) |
| `wood.tipHardness` | 0.25 | the share of `hardness` the thinnest (1 px) wood keeps, 0..1, rising linearly to the full `hardness` at a full cube (`BlockType.TipHardness`, read by `Mining.HardnessOf`). Player-placed tree wood is a full detached cube and therefore as hard as a trunk |
| `wood.handMaxThickness` | none | the highest thickness code (0..15) at which the block ignores `requiresTool`/`tools` and mines bare-handed (`BlockType.HandMaxThickness`, read by `Mining.CanMine`/`HitDamage` with the cell's state byte; callers with no state assume a full cube). The logs set 6, the same cells that drop sticks, so a baby tree or thin branch is punchable and its whole limb falls and drops only sticks |
| `wood.stump` | none | `tree_wood` only: the stump block (one with `stump: true`) a natural trunk of this wood leaves at its root when cut above it (`BlockType.StumpId`, resolved by `BlockLinkResolver`; see `### Trees`) |
| `stump` | false | the block is a tree stump (`BlockType.IsStump`): what a felled trunk leaves at its root, named by a log's `wood.stump`; `hasItem: false`, loot drops its log |
| `wood.loot` | always drop self | thickness bands `[{"minThickness": n, "loot": l}, ...]`, strictly ascending by `minThickness` (0..15) and starting at exactly 0, so every thickness has loot; `loot` is `self` (drop the block, the default), `none` or a loot table name. The band with the largest `minThickness` at or below the cell's thickness is rolled (`WoodLootBand.Select`, through `LootContext.State` on every destruction path): `oak_log` gives the `wood` table (a stick half the time) under 8 px and itself from there, `cactus` always itself |
| `stackMax` | 0 | units that stack in one cell (pebble piles 3, branch piles 4, snow layers 16): the count lives in the chunk state byte, 0 = untouched worldgen (the generator's own default), 1..`stackMax` an explicit stack. 2..16 |
| `stackSolidAt` | 0 | stack count at which a stack block turns solid, for a block whose partial stacks are walked through (layered snow at 16). Only legal from 1..`stackMax` on a `solid: false` stack block |
| `hasItem` | true | enters the blocks pool and can be hotbarred; false for pebbles, branch piles, layered snow and the kiln's anchor and part (fluids force it off). Every `tree_wood` log and every leaf block keeps its item: placing one writes a detached full cube (`WoodState.Detached`) or a persistent leaf (`LeafState`), which never fells with a tree or decays |
| `needsSupport` | false | pops, rolling its loot, when the block holding it up is emptied (the block below, or behind it with `support: "attached"`; `BlockSupport`), and is only placed against a solid holder; also makes the block replaceable, so a flow washes it away. Mining, blasts, felling and leaf decay pop it; a toppling falls block does not yet |
| `flowResistant` | false | a `needsSupport` block a flow may not wash away (a cave formation is rock, not vegetation): the fluid tick treats the cell as a wall and only mining the block under it pops it. Only legal with `needsSupport` |
| `support` | `"below"` | which neighbor holds a `needsSupport` block up: `below`, `above` (the cell over it: a hanging lantern, so it is placed against a block's underside, or wherever a solid block stands over the target cell; `BlockType.SupportAbove`; any non-air holder holds it, so a generated `hell_vine` hangs cell under cell, and emptying any cell pops everything hanging below it, by mining, a blast or leaf decay) or `attached` (the cell behind its front, opposite the `facing` in its state byte: a wall torch). Only with `needsSupport`; `attached` also needs `facing` |
| `facing` | none | `"horizontal"`: the state byte's low two bits are a facing (north -Z 0, south +Z 1, west -X 2, east +X 3); the model (no full-face quads) and `hitbox` are authored facing north and pre-turned to all four (`BlockType.RotatedModels`/`RotatedHitboxes`, `HorizontalFacing`). Only on the `model` renderer, never with `fluid`, `falls` or `stackMax` |
| `supportFullFace` | false | a `needsSupport` block that only a full block face holds (`BlockSupport.CanHold`, `BlockType.HoldsFullFace`): a solid block whose model fills its cell (`BlockType.FullCubeById`), or tree wood at full thickness, so a placement against a branch, a waystone, stairs or a slab is refused and spends nothing. Only with `needsSupport`. `torch`, `wall_torch` (`TorchTests.ATorchMountsOnlyOnAFullBlockFace`) |
| `facingFromLook` | false | a placement faces the block along the placing player's horizontal look (`Placement.LookFacing`: the dominant horizontal axis of `PlayerContext.ViewDirection`, so a server replays a `Place` intent by that player's own aim), whatever face was hit: stairs placed looking north climb north. Only with `facing`, never with `support: "attached"` |
| `open` | none | makes the block openable: `{"model": m, "hitbox": h}`, both required, authored facing north like the closed model and hitbox. Right-click (`BlockInteractions.TryToggleOpenable`, in the right-click chain after the stations) flips `DoorState.OpenBit` in the state byte whatever is in hand, and the block is then drawn with `m` and hit (and, with `collidesByHitbox`, collided) with `h`; `RotatedModels`/`RotatedHitboxes` hold the four closed facings then the four open ones. Only with `facing`. `oak_trapdoor` |
| `upperHalf` | none | makes this block the lower half of a block two cells tall (`TallBlock`): the named block fills the cell above it. Placement writes both halves with one state byte and is refused, spending nothing, unless the upper cell is free too; breaking either half takes the other down through the hooks mining runs and drops this block once; an openable pair opens and shuts together. The upper half needs `hasItem: false` (it is picked as this block, `BlockType.PickId`; the lower half may have no item either, a worldgen-only pair such as a waystone), the same `facing` and `open` as this block, and neither half may be a fluid, fall, need support or stack. A structure template must stand a pair whole (`StructureLoader`). `oak_door` -> `oak_door_upper`, `waystone_<realm>` -> `waystone_<realm>_upper` |
| `wallBlock` | none | the block a placement against a block's side becomes, facing the hit normal (leaning away from the wall); on top the block places as itself, under a ceiling it is refused (`Placement.TryMount`). The wall block needs `facing` and `support: "attached"`, and is picked as this block (`BlockType.PickId`). `torch` -> `wall_torch` |
| `pickAs` | none | the block a middle-click picks instead of this one (`BlockType.PickId`, through the same `ItemBlockId` a `wallBlock` and an `upperHalf` set): a worldgen-only block picked as its plain counterpart. Requires `hasItem: false` on this block and names another registered block that has an item; a block that is already some block's `wallBlock` or an `upperHalf` may not set it (`BlockLoader.ResolvePickAs` refuses each case naming the file). A misspelled field name is ignored silently, like every unknown field. `hollow_oak_log` -> `oak_log` |
| `replaceableByPlacement` | false | a block placement aimed at this block fills the block's own cell and discards it, instead of filling the cell across the hit face, so a block placed against a partial snow drift rests on the ground instead of hovering a layer above it. Only fires while the cell is not solid, so a full snow stack stays an ordinary block, and the displaced block drops nothing. Independent of `needsSupport`, which is what a flow or a bucket may overwrite. Illegal on an always-solid block, which could never honor it |
| `falls` | false | gravity-affected (sand, gravel): topples into a `FallingBlock` entity when the cell below stops being solid, sinks through water, slides off lone supports; never combines with `fluid`/`needsSupport` |
| `fluid` | false | the block is a flowing fluid (water, lava): forces `solid`, `castsAO`, `hasItem`, `needsSupport` off, `occludes` off and `light.passes` on, and drops nothing whatever the file says |
| `flow` | water behavior | per-fluid tuning, only legal with `fluid: true`: `tickDivisor` (ticks between updates, lava `4` = one quarter water speed (half its previous speed)), `range` (sideways spread 1..7, lava `3`), `infinite` (adjacent sources birth new ones, lava `false`), and the `hardensTo` + `hardensTouching` pair (lava hardens to `cobblestone` on touching `block/water`) |
| `flammable` | false | a status effect with `blocks.ignite` burns the block away to air (no loot, no fire block) when it ticks beside it (`BlockType.FlammableById`). Illegal on a fluid and on a multiblock anchor or part. The leaves and the ground plants set it; planks, logs, TNT and stations do not |
| `unbreakable` | false | no gameplay path removes the block (`BlockType.IsUnbreakable`): mining whiffs with no crack progress (`Mining.CanMine`/`HitDamage`), creative swings but never breaks it, the server refuses its break (`BreakVerdict.Unbreakable`, before the creative shortcut), `BlockEditor.BreakBlockFor`/`RemoveBlock` return early, blasts skip it, `Chunk.RemoveBlockAt`/`ClearBlockAt` and placement (`Placement.CanOccupy`, so a falling cube's landing too) refuse it, and buckets (`FluidManager.SetCellImmediate`) and status or spell block actions (`StatusEffectBlockRule`) leave it. Only `Chunk.CollapseWriteAt`, through `TowerBlockWriter` (the battle tower's opened doors and depleted piles, and BT-8's collapse into a ruin through `TryReplace`, which also spills a replaced chest), writes over it; `Chunk.SetBlockRaw` stays raw. Refused with `fluid`, `falls`, `needsSupport`, `replaceableByPlacement`, `flammable`, `meltsTo`, the `tree_wood`/`multiblock`/`multiblock_part` renderers, `upperHalf` and `onBreak: "prime_tnt"` (`BlockFieldRules.ValidateUnbreakable`), and as a `multiblock.source`, since each would hand the cell to a raw-write path. The look-at label and outline still target it (the name in red, never mineable). `tower_bricks`, `tower_bricks_sandstone`, `tower_bricks_frost`, `tower_door`, `skeleton_pile`, `tower_altar`, `tower_forcefield` (translucent, the roof dome) |
| `coversSky` | false | the block hides the sky from every cell under it in a chunk's light volume: sun seeds start one above the highest covering cell of their volume column (`BlockType.CoversSkyById`, see "Sky cover (BT-10)" under "Chunk meshing"). Only legal on a solid, opaque (not `cutout` or `translucent`), `model`-renderer block that is not a fluid and does not set `light.passes: true` (`BlockFieldRules.ValidateCoversSky`), so the cells under it really are shaded. `tower_bricks`, `tower_bricks_sandstone`, `tower_bricks_frost` |
| `freezesTo` | none | the solid block a fluid **source** freezes into under `blocks.freeze` and a spell's `freeze` impact (`BlockType.FreezesToById`; water -> `ice`, lava -> `cobblestone`). Only legal on a fluid, and the target must be a registered non-fluid block |
| `meltsTo` | none | the fluid a block thaws into, as a source, under `blocks.thaw` (`BlockType.MeltsToById`; ice -> `water`). Only legal on a non-fluid, and the target must be a fluid other than the block itself |
| `onBreak` | `"drop"` | `drop` (roll loot) or `prime_tnt` (no drop, spawns a `PrimedTnt`) |
| `loot` | the table sharing this block's name, else drop self | loot table name; `"none"` drops nothing |
| `sfx` | `hit_default` | sound group played while the block is mined, e.g. `hit_rock` (the stone blocks) or `hit_wood` (the logs and cactus). A group is every `<group>.wav` / `<group>_N.wav` directly under `assets/sfx`, and one variant is picked at random per hit (`SfxManager`). An unknown group warns yellow and uses the default |
| `hitbox` | full cube | selection boxes in cell-local 0..1 space: one `{"min": [x,y,z], "max": [x,y,z]}` object or an array of them; the outline draws all, the raycast only targets through one |
| `collidesByHitbox` | false | the body collides with the declared `hitbox` boxes instead of the full cell, the way tree wood collides with its drawn node and tubes (a 0.25 slab is stepped onto and stood on; `BodyCollision.BodyCollides` -> `TreeWoodShape.OverlapsShape`, which tests the registered hitboxes for every block that is not thin or beveled tree wood); requires `hitbox`, `solid: true`, no `stackMax`, not a fluid. Player body only: drops, slimes and falling blocks still see the full cell |

Two traps worth repeating: a **model that is not a full cube needs `light.passes: true`** (which
is its default) or its own cell stays dark and the interior quads render black; and a **cutout
block must not occlude**, which the registry enforces rather than trusting the file.

Partial-block textures are **cropped, never stretched**: the chunk mesher samples `FaceTiles`
crops (`ChunkFaceTables.TiledUv`/`CropUv`), and the mining pop draws the hitboxes the same way
(`PopBoxEmitter`), so a new partial block needs no pop work as long as its `FaceTiles` are
sensible (the baker falls back to the first quad's tile).

### Multiblocks

A multiblock is a **cubic volume of ordinary blocks**, `size` cells along every edge (the anchor
block's `multiblock.size`, 2..4, `BlockType.MultiblockSize`; the kiln is 2x2x2, the alloy furnace
3x3x3), not an entity:
the **anchor** — always the volume's minimum corner on every axis — holds the `multiblock` block
and draws the whole structure, and every other cell holds its `multiblock_part`, which collides,
occludes and raycasts but is never meshed. Everything else in the engine keeps seeing plain solid
cells. Every structure rule takes the size from the anchor's type: `Multiblock.Offsets(size)` (the
cells as offsets, the anchor first) drives `Convert`, `Revert`, `IsIntact` and the editor's and
kiln sweep's per-cell hooks, and `MultiblockPlacement.TryFindVolume` and `MultiblockShape.Rotate`
take it as an argument.

The structure lives in the **per-block state byte**: `Multiblock.EncodeState(facing, dx, dy, dz)`
packs the facing into bits 0-1 and each offset (0..3) into two bits, the low bits of dx, dy, dz
in bits 2, 3, 4 and their high bits in bits 5, 6, 7 — so a 2x2x2 cell reads exactly
`facing | dx<<2 | dy<<3 | dz<<4`, the kiln's bytes from before sizes existed. The facing sits in
the low two bits of *every* cell (the anchor's offset is 0,0,0, leaving just the facing) and one
state read resolves any cell to its anchor — `Multiblock.TryResolveAnchor` subtracts the offset
and then checks the candidate really is an anchor that owns this part id, agrees on the facing
and is big enough to hold that offset, so an orphaned part left by a blast reports false instead
of pointing at a stranger. `facing` is the engine face index of the
opening's outward normal (north -Z = 0, south +Z = 1, west -X = 2, east +X = 3), and the anchor's
model is baked **four times** at startup into `BlockType.RotatedModels` (`MultiblockShape.Rotate`,
a quarter turn about the vertical axis through `(size / 2, y, size / 2)`, which maps the volume
onto itself),
so the mesher picks a pre-rotated model instead of rotating anything per frame. That rotation is
also why a multiblock face may carry no `cullface` and may not cover a whole cell plane
(`ModelBakeSteps.ValidateMultiblockBake`): its quads are culled against nothing and the
per-corner AO path indexes neighbor deltas by `Chunk.CubeFaces` corner order, which rotation
breaks, so a multiblock shades flat on every facing.

**Conversion** is a right-click with an item whose `use.action` is `convert_multiblock`
(`ConvertMultiblockItem` -> `BlockEditor.TryConvertMultiblock` -> `MultiblockPlacement.TryFindVolume`).
The converter names no structure: it tries every multiblock anchor in id order with that anchor's
own source block and size, and the first whose volume the clicked cell belongs to is converted
(2x2x2 `mud_bricks` into the kiln, 3x3x3 `alloy_bricks` into the alloy furnace), so a new structure
needs no converter edit, and no two structures may share a source block. The clicked cell lies
on the volume's front face and the volume always extends **into** the face that was hit, away
from the player; which of the volumes whose front face holds that cell is taken is decided in
screen terms, where `right = Cross(screenUp, hitNormal)`: the one reaching farthest right wins,
then the one reaching farthest down — for a 2x2x2 the order **right-down, right-up, left-down,
left-up** over the four volumes the clicked corner belongs to. The first candidate whose cells are
all the source block wins, so building a neat cube and clicking any of its front cells (a corner,
an edge or the center) converts that cube. A cell **inside** the volume, touching none of its faces
(`Multiblock.IsInterior`: the 3x3x3's center, nothing in a 2x2x2), may be air or the source block,
so a hollow shell converts as well as a solid cube; it becomes a part cell either way, a source
block built there is handed back as a drop in front of the clicked face (never on a client, whose
convert is a prediction), and a revert leaves the interior empty, so a convert and a revert make
and lose nothing. The
opening ends up facing the player: on a side face that is the hit normal itself, and on a top or
bottom face (where the normal says nothing about where the player stands) it is the player's own
horizontal view direction, reversed.

**Mining any cell** reverts the rest (`BlockEditor.RemoveBlock` -> `RevertMultiblock` ->
`Multiblock.Revert`): the mined cell is cleared and rolls the structure's own loot table (the
kiln's yields the one `mud_bricks` that cell was built from, the furnace's one `alloy_bricks`), and
every other shell cell still holding the anchor or part id becomes the source block again with a
**zeroed** state byte, the interior air.
Both `Convert` and `Revert` write through `Chunk.SetBlockRaw` and only collect the chunk
positions they touched, because the remesh follows the `BlockEditor.FellWood` batch idiom — one
`ChunkManager.ImmediateRemeshWithBorders` on the clicked or mined cell's chunk so the click frame
already shows the change, and `Chunk.RemeshAfterBatchEdit` (the asynchronous `MarkAreaDirty`
rebuild a felled tree and a TNT crater already use) on the rest — never a loop of immediate
remeshes.

Nothing but mining goes through `BlockEditor.RemoveBlock`, so a **blast** would otherwise leave the rest of a
structure standing as invisible solid blocks. `KilnManager.Update` doubles as the integrity sweep
for that: it re-checks `Multiblock.IsIntact` every frame, freezes while any cell is in an unloaded
chunk (`anyUnloaded`, so a half-loaded structure is never mangled) and otherwise reverts,
including when the *anchor* cell itself is gone — which is what `KilnState.AnchorId` remembers.
**Only kilns get that sweep**: a future multiblock that is not a kiln needs its own registry, or a
generic one.

### Building blocks

Plain construction pieces built from the schema above. `oak_planks` is the first building wood:
four from one `oak_log` (`recipes/oak_planks.json`), a plain `cube_all` block with `sfx: hit_wood`
that is mined by hand. Its tile is hand-authored and is what the wooden pieces below crop their faces from.
`visual-examples/planks.json` (`VOXEL_TIME=147`) builds and shoots them.

**Slabs** (`cobblestone_slab`, `oak_planks_slab`, six from three of their block) are bottom half
blocks: the `block/slab` parent (one 16x8x16 box, `#bottom`/`#top`/`#side`, default UVs so each
side crops the lower half of its tile) with a `0..0.5` `hitbox` and `collidesByHitbox`, the molds'
idiom, so the body steps onto and stands on them. There is no top-half or double-slab placement.
`BuildingBlockTests` holds the rules every shape-colliding model block obeys: it is drawn exactly
as far as its boxes reach on every facing, and a floor slab is stood on at its top.

**Stairs** (`cobblestone_stairs`, `oak_planks_stairs`, four from six of their block) are a
`facing: "horizontal"` block with `facingFromLook`: `Placement.TryMount`'s look overload writes
`Placement.LookFacing` of the placing `PlayerContext.ViewDirection` into the state byte's low two
bits (`BlockPlacer.PlaceBlockAtFace` takes the view from the placing player, the item-use path from
`WorldItemUse.ActiveContext`), so the step rises the way the player looked. The `block/stairs` parent is
authored facing north (tall step over the north half) and split into three boxes so no face
covers a whole cell side (`ModelBakeSteps.ValidateFacingBake`); the two `hitbox` boxes turn with it
(`RotatedHitboxes`) and the body collides with them, so the walk step climbs them. On a client
in third person the predicted facing reads the camera's view while the server reads the aim,
so a placement near a 45-degree look may be corrected. `StairTests` covers the rules.

The **lantern** (`lantern: true`) is a block item carried as an individual Tools instance. It crafts from
six copper rings and four glass panes. It hangs under a ceiling (historical state 0) or stands on the floor
(state bit 0 set); `Placement.TryMount` records the chosen support and every `BlockSupport` pop path honors it.
It is non-solid and has no unconditional block emission: only a fuelled held or loaded placed lantern contributes
to the existing warm dynamic point lights. The model crops a cage, lid and chain from one opaque tile; its held
transform keeps it upright below the raised hand. `HangingBlockTests`, `LanternTests` and
`visual-examples/held-lanterns.json` cover support, progression and visuals (`VOXEL_TIME=700`).

The **fence** (`oak_fence`, three from four planks and two sticks) is the `pane` renderer with
its dimensions as block data: the `pane` object (schema table) becomes the block's `PaneProfile`
(`BlockType.Pane`, flat `BlockType.PaneProfileById`), a 4 px post plus two 2 px rails per arm.
`PaneShape` reads the cell's own profile (its profile-less members describe the default glass
pane, so the pane is byte-identical), `PaneEmitter` draws each arm's rails with their caps (a
rail short of the cell's top or bottom always keeps them) and keeps the post's side wherever an
arm does not cover it whole, and `PaneProfile.Boxes` gives the post plus one box per arm and
rail to the raycast, outline and body. Panes join only their own shape. No gate and no 1.5-high
collision. Its icon model is `block/fence_inventory`. `PaneProfileTests` holds the rules;
`visual-examples/fences.json` (`VOXEL_TIME=147`) shoots fences, a dirt block and glass panes
side by side.

The **cobblestone wall** (six from six cobblestone) is the same renderer with a wall's JSON:
`"pane": {"post": 8, "arm": 6}`, an 8 px pillar with 6 px arms over the full height, so stacked
walls meet seamlessly (`cullSelf` hides the caps between them); its icon is
`block/wall_inventory`. `fences.json` builds a short wall run behind the fences, one wall stacked
and one turning away from the fence in front of it.

The **trapdoor** (`oak_trapdoor`, two from six planks) is a 3 px hatch on the cell floor:
`facing` with `facingFromLook` plus an `open` object (schema table), so it opens by right-click
into a panel standing against the side of its cell nearest whoever placed it. The open bit is the
`DoorState` meaning of the state byte (see the state-byte invariant), the toggle is
`BlockInteractions.TryToggleOpenable`, a raw state write through `Chunk.SetStateAt` (saved, journaled to
clients, remeshed at once) with the fluid and leaf-decay schedules and `World.BlockEdited`; a
client predicts it and the server replays the `Use` intent through the same chain. Both models
(`block/trapdoor_bottom`, `block/trapdoor_open`) are split in half-width boxes so no face spans a
cell side. `OpenableTests` covers the
rules; `visual-examples/doors.json` (`VOXEL_TIME=147`) shoots trapdoors shut and open.

The **door** (`oak_door`, three from six planks) is two blocks, `oak_door` (the lower half, the
item and the drop) naming `oak_door_upper` as its `upperHalf` (schema table, `TallBlock`), both
openable facing blocks on the `block/door_closed` / `block/door_open` parents: shut, a 3 px panel
across the side of the cell nearest the placer; open, swung a quarter turn about its left-hand
hinge into the cell. `BlockPlacer.PlaceBlockAtFace` checks the upper cell (`CanPlaceUpperHalf`: loaded,
free for a placement, no falling cube or player body) before writing the lower half, then writes
the upper half with the same state (`PlaceUpperHalf`, with the fluid, leaf-decay and
`BlockEdited` hooks); `BlockEditor.RemoveBlock` hands either half to `RemoveTallBlock`, which drops the
lower half once and clears the partner through the hooks mining runs (fluid, leaf decay, far LOD,
`PopUnsupportedAround`, a falls block above it, `BlockEdited`), the multiblock revert in
miniature; `BlockInteractions.TryToggleOpenable` writes the clicked half's new state to its partner too. A blast
clears cells without these hooks, so it can leave one half standing (like a multiblock without
a sweep); breaking that orphan drops the door as usual. The door's pull sits on the center line because of the mirrored uv.
`DoorTests` covers placement, the two-cell refusal, breaking either half and the shared toggle;
`doors.json` shoots two doors, one clicked open.

**Stone bricks** (`stone_bricks`, four from four stone) are a plain `cube_all` building block on
`stone_bricks`' own tile.

**Slime block** (nine slimeballs) carries the `bounce` flag: a body that lands on it is sprung back
up at `SlimeBlockBounceRestitution` of its impact and bills no fall damage (`FallTracker`'s cushioned
landing) (`SlimeBlockTests`).

**Sink and soft-landing flags** (the solid `cloud_block` of the sky's cloud slab uses both, see "Solid clouds").
`softLanding` makes a landing on the block free, like the bounce but with no spring. `sink` (0..0.5)
cuts a soft layer off the top of a plain solid cell: the player body collides only with the part
below it (`SinkLayer.SolidHeight`, inside `BodyCollision.BodyCollides`, so the server's endpoint rule
accepts a sunk body), and a body whose feet stand in the layer (`PlayerBodyProbe.SinkLayerTop`) is
grounded while `PlayerPhysics.HoldInSinkLayer` holds its descent to `PlayerConfig.SinkBlockSpeed`
(`SinkLayer.HoldFeet`: a fall is caught at the top) until it rests on the solid part; it jumps, steps
and walks out as from any ground. `PlayerBodyProbe.Grounded` counts the layer too, so the server's
hover rule never corrects a sinking body, and the one cushion rule (`PlayerBodyProbe.LandingCushioned`:
a bounce or soft-landing block under the footprint) feeds the fall tracker in singleplayer and on the
server alike, so a server no longer bills a slime-block landing either. Player body only: mobs, drops
and falling blocks see the whole cell (`SinkBlockTests`, `MovementRulesTests.ABodySinkingIntoASinkBlockIsNeverCorrectedNorAHoverer`,
both driven through the real `cloud_block`).

**Solid clouds** (`cloud_block`, `WorldGen/Sky/CloudPatchGenerator.cs`, plans/LAYERS.md) are real blocks
in the sky band `CloudSlabBottomY..CloudSlabTopY` (198..203), above every peak (`ReliefCeilingHeight`
148) and the drawn cloud layer (160). The block is a `translucent`, `cullSelf` glass-like cube (drawn in
the sorted translucent pass, no shadow, no AO, light passes) with `softLanding` and `sink: 0.5`, so a
fall onto it is free and the player sinks half a block into its top cell and stands there; hardness 1,
it breaks by hand and drops itself, so mining through a patch drops the player out of it. Its tile is a soft white mottle whose alpha is about 0.7. Worldgen lays patches where a 2D
OpenSimplex noise (seed offset 166, two octaves, `CloudPatchFrequency`) is above `CloudPatchThreshold`
(about a quarter of the sky, patches tens of blocks across): the further above, the thicker the column
(one cell at a rim, the whole band from `CloudPatchCoreValue`), roughened by a detail noise (offset 167)
that also lifts the top, so a patch is a flat-ish slab with an uneven domed top.
`CloudPatchGenerator.TryGetColumn` is the one rule `IsCloud` and the stamp share, and cloud only
replaces air. A sky chunk in the band (above the terrain and any water) takes a short path that runs
only the cloud stamp, the solid recount and the frontier scan (`Stage.Clouds`); a chunk the full
pipeline reaches stamps it after the foliage, before the barrier. The far LOD draws no clouds (the
realms exist only in full-detail chunks), so they show within the load sphere (256 blocks from the
eye). `CloudPatchTests`, `StampGateTests`; `visual-examples/clouds-solid.json` (`VOXEL_TIME=147`).
The shape is `WorldGen/Sky/CloudLayer.cs`, which the cloud sea below shares.

**Sky islands** (`WorldGen/Sky/`, plans/LAYERS.md `L-12`, tuning in `SkyIslandConfig`) float everywhere in
the band `SkyIslandBottomY..SkyIslandTopY` (320..420) and crowd closer together inside **zones**: one hashed
candidate per `ZoneCellBlocks` cell (`TerrainSites.TryRoll`, salt "SKYZ"), its radius
(`ZoneMinRadius..ZoneMaxRadius`) rolled with a size skewed small, kept `ZoneGapBlocks` clear of every
lower-hash raw neighbor roll (`TerrainSites.YieldsToSizedNeighbor`, radius plus radius plus gap). The
outline wobbles inward (noise offset 168) and the weight is 1 in the core (`ZoneCoreShare`) easing to 0 at
the outline. `SkyIslands.ZoneAt(x, z)` is the pure query the sky spawner of `L-15` reads (sky mobs spawn
only inside zones): a `SkyZone` with that weight and a `Tier` (0 small, 1 medium, 2 big, from the rolled
size by `ZoneMediumSize` / `ZoneLargeSize`; bigger is rarer, and the big zones hold the hardest mobs).
**Islands** are one hashed candidate per `IslandCellBlocks` cell (salt "SKYI") anywhere in the band,
accepted when max(`IslandOpenSkyWeight`, the zone weight at its center) beats a roll (the zone is only
asked when the roll is above the floor), so a flying player always has several within the 256-block load
radius (about ten on average, more in a zone core; `SkyIslandTests.AFlyingPlayerAlwaysHasSeveralIslandsInView`)
and a zone is a denser cluster on top. An island is kept `IslandGapBlocks` clear of lower-hash neighbors, so
two islands never touch and at most one holds a column (`SkyIslands.TryGetIsland`). Each rolls a
radius, an underside depth per radius and a base height chosen so its whole body stays in the band.
`SkyIslandGenerator.TryGetColumn(island, x, z)` is the column rule: inside the wobbled outline
(offset 169) the top domes up from the base (`IslandDomePerRadius`, roughened by offset 170, so the
top reads as gentle grass terraces) and the underside hangs from just below the base, deepest under
the center and tapering as `(1 - t)^IslandTaperExponent` to a point at the rim, with stalactites
where offset 171 is high. The blocks come from the island's sky biome's `SkyIslandPalette` (see "Sky
biomes" below), read once per island in the stamp. The meadow's grass is biome-tinted by the climate of
the ground column below it. An island's base is rolled to leave `IslandFloraHeadroom` over its top for
its plants and trees and `SkyVineMaxLength` under its stalactites for its vines, all inside the band. The stamp collects the
few islands reaching the chunk once (`SkyIslands.CollectIslands`: the landmasses, then the 2 x 2 island
cells at most that reach any box up to the sky ruin box, so every caller's stack buffer is
`SkyIslands.MaxIslandsPerBox` long; `IslandCellBlocks` must stay above 2 * (`IslandMaxRadius` + 1) plus that
box's 34 blocks), then asks each column the same rule `BlockAt` asks: the point query equals the stamp
across seams, a chunk reads no neighbor and nothing is cached. Islands write into air only.

**Sky landmasses** (`WorldGen/Sky/SkyLandmasses.cs`, `SkyIslandConfig.Landmass*`, seed offsets 175-178) are whole
floating continents beside the ordinary islands: one hashed candidate per `LandmassCellBlocks` (2100) cell
(`TerrainSites`, salt "SKYL"), a bounding radius spread evenly from `LandmassMinRadius` (300) to
`LandmassMaxRadius` (625), kept `LandmassGapBlocks` clear of lower-hash neighbors. A landmass is a `SkyIsland` with
an `Aspect` (`IsLandmass`; `Radius` is its bounding disc, `Dome` its hill height, `Depth` its keel), so `CollectIslands`
hands it out first and the stamp, flora, sky trees, ruins and mob spawner read it unchanged. Its outline is a long
ellipse of a hashed turn and aspect (`LandmassMinAspect`..1) whose point is first domain-warped (`LandmassWarp`, noise 175/176
sampled so one wave spans its length), so the coast bends and cuts bays, then roughened `LandmassRimWobble` blocks inward;
the top rolls over low hills (noise 177) easing down to the coast over `LandmassTopRamp`, the underside is a shallow keel
(noise 178) that thickens over `LandmassKeelRamp` blocks with the islands' stalactites, always inside the band (the base roll
leaves the flora headroom and vine room). Its biome changes along its length (`SkyBiomes.OfColumn`), an ordinary island stays
whole. No ordinary island touches a landmass's land (`SkyLandmasses.TouchesLand`: a conservative closed-form reject
against the ellipse grown by the warp and the island's reach first, then the landmass column rule sampled at the island's
center and round its perimeter every `LandmassClearanceSampleBlocks`), so islands fill its bays and the open sky of its
bounding disc; `ZoneAt` reports a landmass as its own zone (tier by radius). Around it lies a **fringe**
(`LandmassFringeShare` of its radius, at most `LandmassFringeMaxBlocks`) whose zone weight eases from 1 to 0, so the
ordinary island roll scatters broken-off islands there more densely (`SkyLandmasses.TryFindInfluence`). Archipelago zones
are three times the old size (cell 1200, radius 510-1320). A ruin sits only where an island's center column is land, so
most landmasses hold none.
`SkyLandmassTests`; `visual-examples/sky-landmass.json` (`VOXEL_TIME=147`).

**The cloud sea** (`CloudSeaGenerator`, offsets 172-173) is a second, thick `CloudLayer` of the same
`cloud_block` in `CloudSeaBottomY..CloudSeaTopY` (290..301, from `SkyConfig.SkyCloudSeaHeight` and
`SkyCloudSeaThickness`): a broad, low-threshold cover noise lays it over about three quarters of the
sky with wide holes down to the world below, full thickness over most of it with an uneven top.
Falling off an island lands in it without damage (the block's `softLanding`/`sink`); through a hole the
fall is an ordinary one. Like the patches and the islands it exists only in full-detail chunks (the far
LOD draws none, so it pops in within the load sphere), and the ground's sun light is unchanged (the sun
seeds from the heightmap, so neither casts voxel shade). Mobs never spawn on either: the surface ring
search reads the heightmap, and the cave search keeps to cells under their column's surface; the spawn
settle and `World.TryGroundHeight` read the heightmap too. Measured with `WorldgenCostProbe`'s
`skyIsland` and `cloudSea` classes: a sky-island chunk about 0.14 ms warm with its flora, trees and ruins (the
island stamp 0.10 ms), a cloud-sea chunk 0.05-0.1 ms warm; cold both are dominated by the column-cache
neighborhood scan every chunk pays (plans/PERF.md). `SkyIslandTests`, `StampGateTests`;
`visual-examples/sky-islands.json` and `sky-density.json` (both `VOXEL_TIME=147`; the second films the island
density over the origin, open sky, a zone core and rim and a continent's coast).

**Sky biomes** (`L-13`; `WorldGen/Sky/SkyBiome.cs`, `SkyBiomes.cs`, `SkyFlora.cs`, `SkyTrees.cs`,
`SkyTreeSpace.cs`) are two, `SkyBiome.Meadow` and `SkyBiome.FrostHighland`, an enum of their own like
`HellBiome` (never the surface `Biome`). `SkyBiomes.At(x, z)` cuts a coherent two-octave 2D noise (seed
offset 174, `SkyBiomeFrequency`, regions about a zone across) at `SkyFrostMinValue`; an island takes the
biome at its center (`SkyBiomes.OfIsland`), so every island wears one palette and a zone's islands mostly
share it. Each biome's `SkyIslandPalette` (`SkyBiomes.Palette`) names its top, soil, core, a sparkle top,
its plants, vine rate and tree species with its chance: the **meadow** is grass over dirt over stone with
tall grass and flowers (`MeadowPlantRate`), sparse vines and the broad `sky_oak` (`sky_oak_log` wearing the
oak's bark, `sky_oak_leaves`); the **frost highland** is `frost_grass` (frosted turf) over `permafrost`
over `frost_stone`, one top cell in `FrostCrystalRate` a `crystal_block` outcrop, `frost_fern`s, thicker
vines and the `crystal_tree` (`crystal_log` under a cone of `crystal_leaves`, cutout, glowing pale cyan at
7 in a sixth palette slot). The small flora is part of the island's one column rule (`SkyFlora.BlockAt`,
read by `SkyIslandGenerator.BlockAt` and the stamp alike, so the point query still equals the stamp): the
sparkle top, a plant over a plain top cell (one in `PlantRate`, picked by a salted column hash,
`SkyBiomes.Hash`), and a `sky_vine` of 1..`SkyVineMaxLength` cells straight down from the column's bottom
cell (one column in `VineRate`), so every vine cell hangs from the island or the vine over it. `sky_vine`
is `support: "above"` like the hell vine, so a vine cut anywhere drops what hangs below, and the plants
`needsSupport`. **Trees** do not go through `TreeGenerator` (the sky has no heightmap): `SkyTrees` cuts the
sky into `SkyTreeCellBlocks` (16, one chunk column) cells, each rooting at most one tree, a pure function of
(cell, seed): a hash picks the root column `SkyTreeCellMargin` inside the cell, which must be an island
column whose top is its palette's plain `Top` (not a sparkle); the island's biome picks the species and the
chance; the tree roots over the top and grows to maturity in a `SkyTreeSpace` (free only in its own cell's
columns, inside the band and outside every island's cells, memoized per column), so trees never meet,
never enter an island and never leave the band. The shape is cached per cell (`SkyTrees`'
`GenerationalCache`, `SkyTreeCacheCapacity`, cleared by `WorldSeed.Apply`) and `SkyTrees.Stamp`
(`Stage.SkyTrees`, right after the islands in `ChunkTerrainFill.StampSky`) writes each tree's cells with
their state bytes over air and flora only, never over a solid cell. **Pixel art uses a small hand-picked palette, never noise or smooth gradients, so a new tile is a palette swap of the regular tile it mirrors** (the frost turf and stone are the regular grass and stone in an icy palette). Cost (`WorldgenCostProbe` `skyIsland` class): a warm sky-island chunk
0.065 ms (0.034 before the flora), cold dominated by the column scan as before plus about 0.3 ms of
trees grown on a cache miss (plans/PERF.md). `SkyBiomeTests`, `SkyIslandTests`; `visual-examples/sky-biomes.json` (`VOXEL_TIME=147`) frames a
meadow and a frost island from the side, from above, on top and from below.

**Bandage** is the first `use.action: "heal"` item (`HealItem` -> `PlayerHealth.Heal`, refused at
full health), its sprite is committed art (`HealItemTests`). A heal item restores its own `heals`
(`ItemLoader.ParseHeals`, `HealItem.Heals`), or `BandageHealAmount` when it declares none; **cooked venison**
and **cooked boar meat** are the food that uses it, cooked on a lit campfire from the raw meat the deer and
the boars drop (`recipes/cook/raw_*.json`; see "Wildlife" for the drops, `WildlifeDropTests`).

**Ash fertilizer** (`use.action: "grow_tree"`) grows the tree of the column it is used on, resolving
the species from the targeted block (`TreeSpeciesRegistry.ForBlock`, which resolves a wood two
species share to the leafy owner) and refusing, spending nothing, where the cell holds no tree
(`GrowTreeItemTests`). The **acorn** plants an oak sapling the same way (`AcornTests`), and
`World.TryGrowTree` is the reactive growth path behind both (`### Trees`).

**Spell staffs** (`staff` in the item schema): the **wooden staff** (branches and a crystal block) and
the **crystal staff** it is upgraded into (the wooden staff, crystal blocks and a copper ring), a
higher tier with more slots and the `advanced` part tag beside `basic`. Their sprites are placeholders recorded in `USER_TODO.md` (`StaffContentTests`, `StaffBindingTests`).

**Spell table** (`spell_table`, four oak planks and a crystal block) is a plain `cube_bottom_top` block
wearing the crystal block's tile on top and planks elsewhere, made a station by the `spellBuilder`
flag alone (no block entity). A right-click with a staff in hand opens the spell builder
(`App/SpellBuilderInput`, `UI/SpellBuilder/`, `SpellBuilderRenderer`), client-side only; without a
staff a toast asks for one. The builder lists the parts the player knows by kind (`KnownSpellParts`:
the parts its scrolls taught, creative always all), keeps one element, one shape and one trigger with any
modifiers, shows the live cost, verdict, fizzle chance and damage and range factors (`SpellRecipe.Compile`
against the staff's tags), and binds or clears the chosen slot through the `BindStaffSlot` /
`ClearStaffSlot` inventory actions, which the client predicts and the server's `InventoryState` keeps or
reverts. E or Escape closes it, and so do the pause menu, the death screen and a change of the held
staff (`SpellTableTests`, `SpellBuilderViewTests`, `SpellBuilderLayoutTests`).

**Flint from gravel**: the gravel loot table yields the flint inside it sometimes, so a gravel patch
is worth digging (`GravelPocketTests` covers the pocket side).

Two small interaction rules round the list out: **a cactus prick** hurts any body standing against
the cactus' side (a `contact` block flag, billed like a mob's contact damage; `CactusPrickTests`),
and **attacks pass through plants**: the punch and slice cones ignore non-solid plants (tall grass,
flowers), so a mob behind one is still reachable. **A placement takes the plant's own cell** when
the targeted block is `replaceableByPlacement` (the layered snow idiom), so building against a plant
fills where it stands instead of hovering beside it (`PlacementTests`).

### Item schema (`assets/data/items/<name>.json`)

```json
{ "name": "Flint", "texture": "assets/textures/items/flint.png" }
```

`name` and `texture` are all a plain pickup needs. An optional `use` object gives the item a
right-click behavior (`RightClickAction.TryUseSelected` -> `WorldItemUse.DispatchUse`):

```json
{ "name": "Bucket", "texture": "assets/textures/items/bucket.png",
  "use": { "action": "pickup_fluid", "fluid": "block/water", "result": "item/water_bucket" } }
```

| field | meaning |
|---|---|
| `use.action` | `pickup_fluid` (scoop the targeted fluid source into the item), `place_fluid` (pour one into the targeted cell), `place_stack` (build a stack block up), `ignite_campfire` (strike a branch pile, or a kiln that has fuel), `convert_multiblock` (turn a volume of the anchor's size into a structure), `heal` (restore health to the user, refused at full health), `grow_tree` (grow the tree the aimed cell belongs to, `AshGrowthPulses` pulses), `restore_mana` (refill the `restoresMana` share of the mana pool, one unit spent, refused at a full pool) `learn_parts` (a teaching scroll: learn the spell parts in `teaches`, spending one; creative spends none, and a scroll that teaches nothing new is refused and kept) or `summon` (wake the boss `use.entity` names at the aimed cell, the dragonfly wing's sandworm; refused, and not spent, off desert sand or with one alive) |
| `use.pose` | the pose held while a `use.seconds` charge runs: `raise` (default, the hand presenting the item) or `bow` (`UsePose`, `BowPose`): the item arm out forward, the other arm drawn to the cheek as the charge rises, the shoulders turned side-on, holding at full draw until the button is let go (the use never runs by itself; `RightClickAction.UpdateUseCharge`), then the string arm snaps back and the body settles. Needs `use.seconds`. The `bow` item has no shot yet (`F-4`), and a remote player is not shown drawing one (the draw is not on the wire) |
| `use.seconds` | seconds holding right-click takes before the use runs: the world charges a use pose (`PlayerModel.StartUseCharge`/`ThrowPose.WithUse`) and only runs the use at completion, so letting go early cancels it and spends nothing (`RightClickAction.TryBeginUseCharge`/`UpdateUseCharge`/`CancelUseCharge`, the twin of `ThrowAction`'s throw charge). 0 (omitted) keeps the old instant use. The firestarter's `useDelay` stays the cooldown after a use |
| `use.block` | for `place_stack`, the stack block it builds; for `ignite_campfire`, the firepit block |
| `convert_multiblock` | takes no `use.block`, `use.result` or `use.fluid` (refused naming the file): the converter presses whichever multiblock the clicked cube is the source of (each anchor's `multiblock.source`). It is spent by nothing and never morphs |
| `use.fluid` | the fluid block the action works on; must be a block with `fluid: true` || `use.result` | the item this one becomes on success; must be an item, and the two normally point at each other |
| `display` | how the item sits in the right hand: `rotation` as `[pitch, yaw, roll]` degrees (composed yaw, then pitch, then roll), `translation` in blocks of hand space, `scale` as a multiplier; omitted means `{"rotation": [-30, 10, 0]}`, i.e. upright over the hand with a slight sideways cant — the pickaxe yaws to 60 so a tip faces forward, the compass uses rotation `[-60, 0, 0]`, scale `0.67` and a lift to face back up at the holder |
| `blockDamage` | damage one left-click hit removes from a block's `hardness`; default 0.5 (the bare-hand baseline). Independent of `tool`, which only gates *whether* the block may be mined |
| `throwBlock` | A block (`block/pebble`, named like `use.block`; needs `throwDamage`) whose small cube a thrown copy is drawn as instead of the item's sprite (`ThrownProjectile.RenderId`, `ObjectPasses.DrawThrown`): `flint` flies as the pebble's stone cube and `obsidian` as the obsidian cube, the same stones they rest as on the ground |
| `throwDamage` / `throwAligned` | `throwDamage` makes the item throwable (a charged right-click throw, damage at full charge, 0 for a harmless snowball); `throwAligned: true` (needs `throwDamage`) makes the thrown copy fly point first along its velocity (`ThrownProjectile.AlignedRotation`, the sprite's tip on its (1, 1) diagonal along the heading) instead of tumbling about the vertical, which is how the `spear` flies. `visual-examples/spear.json` films it (the `throw` player action launches the selected throwable at full charge). Gameplay beyond the throw (pickup, durability, a recipe) is `F-4` |
| `entityDamage` | damage one punch or slice deals to an enemy (any `Mob`: a slime or a goblin); default 1 (the bare-hand baseline). A weapon raises it without needing a `tool` block (the obsidian blade is `6`) |
| `attack` | left-click attack behavior, an `AttackPattern`: `punch` (default; forward cone, hits the nearest one), `slice` (forward hemisphere, hits the nearest two, two-phase swing — the obsidian blade) or `sword` (a three-swing chain: a diagonal cut and a rising back cut over the hemisphere, hitting the nearest three, then an overhead chop on the one target in the cone at the highest power with a heavier shove — the iron sword). See `Combat`/`SliceCombo`/`SwordCombo`/`SwordSwings` |
| `trail` | optional `{"color": [r, g, b]}`, sRGB 0..255: the color of the glowing slice trail the item's swings leave (`Item.TrailColor`; omitted, `TrailColorOrDefault` falls back to `ParticleConfig.SliceTrailDefaultColor`). Legal only on a `slice` or `sword` item; a trail on a punch item, or a color that is not three ints in 0..255, throws naming the file (`ItemLoader.ParseTrail`). The obsidian blade is violet, the iron sword pale blue-white |
| `shrapnel` | optional `{"count": n, "damage": d}`: a landed hit with this blade sprays `n` bone fragments (1..32) that each deal `d` damage (1..100) to the first other enemy they touch (`Item.Shrapnel`, a `ShrapnelProfile`). Legal only on a `slice` or `sword` item (`ItemLoader.ParseShrapnel` refuses it on a punch item and refuses a missing or out-of-range field, naming the file). A worn `bone_shrapnel` gear ability adds its own weaker burst to any weapon and raises this blade's by `ShrapnelConfig.TogetherFactor`; see "Bone shrapnel". The bone-breaker blade is `{"count": 6, "damage": 3}` |
| `onHit` | optional `{"trigger": "on_hit", "effect": name, "chance"?, "scale"?}`, a gear-passive shape: a landed hit with this blade puts that status effect on the enemy (`Item.OnHit`, applied in `MobStrikes.StrikeMob`). Legal only on a `slice` or `sword` item (`ItemLoader.ParseOnHit`); the effect name is checked at load (`ValidateGear`). The cinder sword is `burning` |
| `fuel.burnSeconds` | seconds one unit of this item keeps a kiln lit (coal 60; 1..3600). Writing the object is what makes an item kiln fuel — a kiln takes up to its `kiln.fuelCap` units of **one** kind at a time |
| `crucible.capacityMl` | marks the item a **crucible**: a vessel holding this many milliliters of metal, melted and pending together (100..10000). A crucible stores in the **Tools** pool, one instance at a time, because its contents ride on that instance's `ItemStack` as a `CrucibleComponent`: one `CrucibleMetal(MaterialId, MoltenMl, PendingMl)` per molten material it holds (copper and tin side by side in an alloy kiln, see "The kiln"), the capacity counted over all of them, plus `PendingInputId`, display-only, the last melt input charged in, so the kiln can draw what is waiting to melt; while the vessel stands in a kiln the same struct lives in `KilnState` instead, and taking it out writes it back onto a fresh instance (`CrucibleInventory.Add`). The stack carries no melt timer, so putting a crucible back into a kiln restarts the partial chunk it was melting |
| `staff` | marks the item a **spell staff**: `{"slots": n, "tier": t, "tags": [..]}`, slots 1..`SpellConfig.StaffMaxSlots`, tier >= 1, non-blank distinct spell part tags (`ItemLoader.ParseStaff`, shape only: spell parts load after items). Builds a `StaffItem`, stored in the **Tools** pool one instance at a time, because each slot's bound spell recipe rides that instance's `ItemStack` as a `StaffComponent` (the part keys `<kind>/<name>` per slot plus the selected slot, immutable). `StaffBinding.TryBind` binds a recipe only when MG-2's `SpellRecipe.Compile` finds it valid against the staff's tags (a part passes when any of its tags is allowed), so a tier gates its parts by tag; `Clear`, `Cycle` (wrapping) and `Select` exist; Left Ctrl + wheel cycles the held staff's slot through `HeldStaff` and the server-owned `SelectStaffSlot` inventory action, and the spell table binds and clears a slot (`HeldStaff.TryBind`/`TryClear`, through the server-owned `BindStaffSlot`/`ClearStaffSlot` inventory actions, see "Spell table"). Right-click casts the selected slot (see the **Cast** intent). Saved in `player.json` and carried in the inventory snapshot as `ToolData.Staff` by part key, never id, and re-bound on load against this run's parts (what no longer binds is dropped with a warning). Crafting consumes the least-bound instance first. A Q-dropped instance keeps its components in the drop payload, including spells and crucible metal. May not declare `use` or `crucible`; the tier is shown in the tooltip |
| `teaches` | the spell part keys (`<kind>/<name>`) a scroll teaches, a non-empty list of distinct keys; required with, and only legal with, `use.action: "learn_parts"` (`ItemLoader.ParseTeaches` checks the shape, and `ValidateLearnParts` checks every key against `SpellPartRegistry` once the spell parts load, naming the file). Builds a `LearnPartsItem`, a plain Misc stackable; a scroll may not declare `use.result`, `use.fluid` or `use.block`. Its tooltip lists the parts it teaches |
| `restoresMana` | the share of the mana pool one use restores, in (0, 1]; required with, and only legal with, `use.action: "restore_mana"` (`ItemLoader.ParseRestoresMana`; `RestoreManaItem`, `PlayerMana.TryRestore`; the `mana_draught`) |
| `heals` | the health one use restores, 1 or more; only legal with `use.action: "heal"`, where leaving it out heals `PlayerConfig.BandageHealAmount` (`ItemLoader.ParseHeals`, `HealItem.Heals`) |
| `model` | optional `block/<name>` model under `assets/models/`, baked into `Item.BakedModel` so the item can be drawn as real 3D geometry while it sits inside a station (the crucible standing in a kiln's hearth). The sprite stays the inventory icon; this only adds a mesh |
| `tool` | optional mining numbers: `power` (the item's mining *tier* — the number a block's `tools` requirement is compared against, default 0.5, the bare-hand baseline; it is **not** a damage number, damage is `blockDamage`) and `hitDelayMs` (milliseconds between hits, for both punches and block hits; omitted, a punch takes 600 ms and a block hit 300 ms). Writing the object marks the item as a tool; which block `tools` tags it satisfies comes from the tag files, not the item — `assets/data/tags/pickaxe.json` listing `item/primitive_pickaxe` is what makes a block gating on `"pickaxe"` accept it (`Mining.CanMine`). Bare hand and non-tool items punch at the 600 ms default and mine at the 300 ms block default, and creative ignores the gate |

Both names resolve like a loot entry (`block/` / `item/` prefixes pin the namespace).
`ItemLoader.ValidateItemUses` runs after the blocks are loaded and throws, naming the file,
if the fluid is not a fluid block or the result is not an item. A successful use goes through
`Inventory.MorphTool`, which turns the tool instance into its result in place, so every hotbar
slot holding that bucket flips with it and scooping and pouring alternate on the same button.
A `pickup_fluid` item scoops the fluid it names into its `result`, but an empty bucket also
scoops any other fluid that has its own full bucket (lava becomes `item/lava_bucket`) — the
pour side is what decides the pairing, so new fluids only need a `place_fluid` item to be scoopable.
`Item.TargetsFluids` (true on `BucketPickupItem`) makes the selection
raycast include fluid sources only while a `pickup_fluid` item is held — otherwise water is
aimed straight through, and mining refuses fluid cells regardless.

### Loot schema (`assets/data/loot/<name>.json`)

```jsonc
{
  "pools": [
    {
      "rolls": 1,                    // int, {"min":1,"max":3}, or {"provider":"pebble_count"}
      "chance": 0.15,                // the whole pool is skipped otherwise; default 1
      "entries": [
        { "item": "cobblestone", "weight": 1, "count": 1 },  // count: int or {min,max}; weight default 1
        { "empty": true, "weight": 3 }                       // weighted nothing
      ]
    }
  ]
}
```

An `item` name resolves against blocks first, then items; a `block/` or `item/` prefix pins it to
one of the two, so a table that means the item says so whenever a block of the same name could
exist: `leaves.json` (shared by every `*_leaves` block) and `wood.json` (the thin-wood band of the
`tree_wood` logs) both write `"item/branch"`, which keeps the stick an item even beside the
`branch_pile` block. The only roll-count provider is `pebble_count`
(`PebbleGenerator.CountAt(x, z)` at the loot context's cell).

Every destruction path rolls through one place: `DropManager.RollLoot` (a block with no table
drops itself, `LootTable.None` drops nothing) and its spawning wrapper `SpawnLoot`. Mining
(`BlockEditor.RemoveBlock`), blasts (`TntManager.Detonate`), felled trees (`TopplingTree.TakeDrops`) and
decaying leaves and crumbling orphaned wood (`LeafDecayManager`) all go through it, with the
cell's state byte in `LootContext.State` so tree wood picks its thickness band, so a block's
yield never depends on how it came down.

### Model schema (`assets/models/block/<name>.json`)

Minecraft Java block-model format, i.e. what BlockBench exports: `parent` (chained, cycles
rejected), `textures` (`#var` refs, child entries overriding the parent's) and `elements[]` with
`from`/`to` in 0..16, optional `rotation {origin, axis, angle, rescale}`, `shade`, and `faces`
carrying `uv`, `texture`, `cullface` and `rotation`. `tintindex`, `display`, `ambientocclusion`
and `gui_light` are ignored. Texture refs are normalized by stripping `minecraft:` and any
leading `block/` or `blocks/`, then looked up as an atlas tile.

Minecraft's face names map onto the engine's face indices (`Chunk.CubeFaces`) as
`north` = -Z = 0, `south` = +Z = 1, `west` = -X = 2, `east` = +X = 3, `up` = +Y = 4,
`down` = -Y = 5.

Four built-in parents mirror Minecraft's byte for byte, so a BlockBench export referencing them
works unchanged: `cube` (six faces, `#north #south #east #west #up #down`), `cube_all` (all six
`#all`), `cube_column` (`#end` on up/down, `#side` around) and `cube_bottom_top` (`#top`,
`#bottom`, `#side`). `ContentRegistry.BakeModels` self-checks at startup that all four still bake
to six full faces.
