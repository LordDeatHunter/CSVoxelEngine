// Writes the wiki's screenshot scenarios (OpenGLTest/visual-examples/wiki-*.json) and the manifest
// the wiki reads (wiki/data/screenshots.json) from one spec, so a shot's pose, caption and group
// live in one place. Run from the repo root:  node tools/generate-wiki-shots.mjs
//
// Capture (from OpenGLTest/, Release build; each scenario names the VOXEL_TIME it wants):
//   VOXEL_TIME=<t> VOXEL_SCRIPT=visual-examples/wiki-<name>.json  ->  visual/wiki-<name>/NN-<slug>.png
// then convert with `npm run shots` in wiki/.
//
// The lighting clock follows the wall clock, so each scenario is short enough (see the printed
// budgets) to stay in daylight from its start time. Poses come from the existing scenarios
// (biomes.json, cold.json, terrain-shape.json, hell.json, barrier.json, sky-*.json, aurora.json, moon.json).
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')

// [slug, title, caption, group, pos, yaw, pitch, settle seconds]
const day = (name, time, comment, shots) => ({ name, time, comment, shots })

const SCENARIOS = [
  day('surface', 30, 'The surface biomes around the spawn.', [
    ['forest', 'Forest', 'Rolling grass hills and oak woods, with the grass tinted by the temperate climate.', 'biome', [-64.5, 60.5, -40.5], -90, -18, 12],
    ['steppe', 'Steppe', 'A dry, open grass plain, yellower than the forest, with scattered dead trees.', 'biome', [40.5, 50.5, 144.5], 180, -14, 12],
    ['steppe_desert', 'Steppe meets desert', 'Borders are slopes and ragged patches rather than lines: sand creeps into the dry grass.', 'border', [64.5, 60.5, -16.5], 45, -18, 10],
    ['savanna_desert', 'Savanna meets desert', 'The warm, dry edge where savanna grass gives way to dunes.', 'border', [192.5, 70.5, -112.5], 90, -20, 12],
    ['taiga', 'Taiga', 'A snowy spruce wood.', 'biome', [-315.5, 40.5, 55.5], 90, -28, 14],
    ['frozen', 'Frozen tundra', 'Permafrost under snow, with sparse dead shrubs and leafless snags.', 'biome', [-720.5, 40.5, 415.5], 90, -28, 14],
    ['ocean_bay', 'Ocean', 'A sandy shore sloping into water with waves, foam and sun glitter.', 'biome', [-15.5, 10.5, 895.5], 180, -12, 14]
  ]),
  day('far-biomes', 30, 'Biomes found far from the spawn.', [
    ['desert', 'Desert', 'Dunes of sand over sandstone, with cacti and dead trees.', 'biome', [576.5, 50.5, 1184.5], 0, -15, 30],
    ['tundra', 'Frozen plain', 'A wide, white tundra under a low horizon.', 'biome', [-764.5, 19.5, 1318.5], 90, -10, 30],
    ['ragged_border', 'A ragged border', 'A coherent noise breaks material lines into patches, never single columns.', 'border', [480.5, 160.5, 1600.5], 45, -30, 20],
    ['steppe_tundra', 'Steppe meets tundra', 'Dry grass fades into frozen ground, with a wide band between sand and snow.', 'border', [-416.5, 80.5, 1040.5], 159, -18, 20],
    ['savanna', 'Savanna', 'Dry grassland with umbrella-crowned stone pines and wild cotton.', 'biome', [1392.5, 50.5, -1264.5], 90, -16, 30],
    ['savanna_ground', 'Savanna, ground level', 'Cotton clumps and stone pines on the warm, dry plain.', 'biome', [1392.5, 28.5, -1236.5], 90, -6, 6],
    ['pack_ice', 'Pack ice', 'In cold seas the water freezes into a walkable sheet, broken into floes.', 'biome', [-951.5, 120.5, -2535.5], 144, -35, 30],
    ['pack_ice_low', 'Pack ice, ground level', 'Icebergs drift where the sea is just cold enough.', 'biome', [-1055.5, 3.5, -2455.5], 144, -6, 10]
  ]),
  day('highlands', 30, 'Highland terrain and mountains.', [
    ['origin_valley', 'The spawn valley', 'Near the spawn the land is kept calm: gentle hills and a wide valley.', 'terrain', [0.5, 45.5, 60.5], -90, -14, 12],
    ['forest_highland', 'Highlands', 'A continent-scale lift raises some regions into highlands.', 'terrain', [-3450.5, 110.5, 2000.5], 45, -16, 25],
    ['range_along', 'A mountain range', 'Ranges are chains hundreds of blocks long, found only inside highlands.', 'terrain', [1017.5, 120.5, 3627.5], 54, -12, 25],
    ['range_across', 'Across a range', 'Looking across a range, with ridges and valleys running along it.', 'terrain', [1434.5, 110.5, 3696.5], 144, -10, 15],
    ['arid_plateau', 'An arid plateau', 'In dry land a highland becomes a lifted plateau instead of broken country.', 'terrain', [-4100.5, 70.5, -4200.5], 45, -14, 25],
    ['massif', 'A massif', 'Rare massifs rise to an absolute summit with a pointed, spurred body.', 'terrain', [631.5, 50.5, -4900.5], 0, 0, 25],
    ['massif_summit', 'A massif summit', 'The crown of a massif, with snow even in a warm biome.', 'terrain', [1119.5, 140.5, -4990.5], 90, -20, 15],
    ['lone_peak', 'A lone peak', 'A single peak standing on flat ground.', 'terrain', [-2871.5, 30.5, 3061.5], 0, 3, 25]
  ]),
  day('water-land', 30, 'Islands and great lakes.', [
    ['island', 'An island', 'Islands rise out of deep water, ringed by sand.', 'terrain', [4120.5, 40.5, -9.5], 0, -4, 25],
    ['island_beach', 'An island beach', 'The shore of an island with its beach.', 'terrain', [4325.5, 6.5, -75.5], 85, -8, 15],
    ['archipelago', 'An archipelago', 'Islets string out in chains.', 'terrain', [3158.5, 140.5, 2929.5], -28, -25, 25],
    ['great_lake_elevated', 'A great lake', 'Rare, lobed lakes a few hundred blocks across with islands and shores of their own. In the cold they freeze like any water.', 'terrain', [5780.5, 130.5, 4150.5], 90, -28, 25],
    ['great_lake_island', 'A lake island', 'A great lake with an island in its main lobe.', 'terrain', [-9230.5, 40.5, -9700.5], -141, -10, 15]
  ]),
  day('hell', 147, 'The hell realm.', [
    ['hell_layer0', 'Hell', 'Right under the barrier: a lava-lit cavern under a red haze.', 'hell', [-15.5, -330, -7.5], -90, -20, 24],
    ['hell_layer1', 'The second layer', 'Every layer is a cavern pocket over an unbroken slab of hellrock, and deeper ones are more open.', 'hell', [0.5, -430, 0.5], -90, -15, 20],
    ['hell_layer4', 'Deep hell', 'Deeper layers pond more lava.', 'hell', [4.5, -706, 8.5], -90, -30, 20],
    ['hell_ashlands', 'Ashlands', 'Ash over cinder soil, scorched walls, ember blooms and charred trees.', 'hell', [-7.5, -329.4, -7.5], -90, -15, 20],
    ['hell_magma', 'Magma Caverns', 'Basalt and magma floors with lava pools.', 'hell', [-23.5, -449.4, 8.5], 0, -15, 20],
    ['hell_fungal', 'Fungal Grove', 'Glowing teal fungi, glow ferns, thick vines and fungal trees.', 'hell', [-135.5, -424.4, -103.5], 0, -15, 20]
  ]),
  day('sky', 30, 'The sky realm.', [
    ['sky_cloud_patches', 'Solid clouds', 'Cloud slabs above every mountain that catch a fall.', 'sky', [-10.5, 201.5, 40.5], -40, 0, 20],
    ['sky_cloud_sea', 'The cloud sea', 'A thick sea of solid cloud under the islands, with holes down to the world.', 'sky', [-180.5, 330.5, 120.5], 60, -35, 20],
    ['sky_zone', 'A sky island zone', 'Islands float in zones with wide gaps of open sky between.', 'sky', [-30.5, 440.5, 100.5], 150, -25, 20],
    ['sky_island', 'A floating island', 'Domed grass tops over a tapering, stalactite-hung underside.', 'sky', [-250.5, 392.5, 202.5], 0, 0, 20],
    ['sky_meadow', 'Meadow island', 'Grass over dirt over stone, with tall grass, flowers and broad sky oaks.', 'sky', [172.5, 382.5, 763.5], 0, -5, 20],
    ['sky_meadow_above', 'A meadow from above', 'Looking down on a meadow island.', 'sky', [232.5, 420.5, 813.5], -90, -45, 12],
    ['sky_frost', 'Frost highland island', 'Frosted turf over permafrost, with crystal outcrops and glowing crystal trees.', 'sky', [-314.5, 368.5, 265.5], 0, -5, 20],
    ['sky_frost_above', 'A frost island from above', 'Looking down on a frost highland island.', 'sky', [-254.5, 405.5, 315.5], -90, -45, 12]
  ]),
  day('night', 700, 'The night sky.', [
    ['aurora', 'The aurora', 'Slow green and violet curtains in the night sky of cold lands.', 'night', [-1112.5, 40.5, 584.5], 270, 14, 24],
    ['aurora_up', 'The aurora overhead', 'Looking up into the curtains.', 'night', [-1112.5, 40.5, 584.5], 270, 40, 4],
    ['night_sky', 'Stars', 'The star field turns once a day; there is no aurora over the temperate lands.', 'night', [0.5, 140.5, 0.5], 270, 14, 20],
    ['moon', 'The moon', 'The moon goes through phases, and at night it is the world’s key light.', 'night', [0.5, 200.5, 0.5], -61, 67, 8]
  ])
]

const manifest = {}
for (const sc of SCENARIOS) {
  const steps = [{ op: 'creative', on: true }, { op: 'fly', on: true }]
  let budget = 0
  for (const [slug, title, caption, group, pos, yaw, pitch, settle] of sc.shots) {
    steps.push({ op: 'teleport', pos, yaw, pitch }, { op: 'wait', seconds: settle }, { op: 'teleport', pos, yaw, pitch },
      { op: 'screenshot', name: slug, hideHud: true })
    budget += settle
    manifest[slug] = { title, caption, group }
  }
  const file = path.join(root, 'OpenGLTest/visual-examples', `wiki-${sc.name}.json`)
  const header = `// ${sc.comment} Generated by tools/generate-wiki-shots.mjs. From OpenGLTest/:\n//   VOXEL_TIME=${sc.time} VOXEL_SCRIPT=visual-examples/wiki-${sc.name}.json\n// Shots land in visual/wiki-${sc.name}/ (about ${budget} s of settle time).\n`
  fs.writeFileSync(file, `${header}{\n  "steps": [\n${steps.map((s) => '    ' + JSON.stringify(s)).join(',\n')}\n  ]\n}\n`)
  console.log(`wiki-${sc.name}.json: ${sc.shots.length} shots, ~${budget} s settle, VOXEL_TIME=${sc.time}`)
}
fs.mkdirSync(path.join(root, 'wiki/data'), { recursive: true })
fs.writeFileSync(path.join(root, 'wiki/data/screenshots.json'), JSON.stringify(manifest, null, 2) + '\n')
console.log(`manifest: ${Object.keys(manifest).length} shots`)
