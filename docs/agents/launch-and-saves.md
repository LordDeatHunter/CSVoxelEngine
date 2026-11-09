# Launch flow and saves

The main menu / loading / playing flow, the save folder layout, and the persistence rules (chunk save ordering, autosave, teardown). Moved out of AGENTS.md; text unchanged.

**Launch flow.** The game boots to the **main menu** with the cursor free (`Program` owns an
explicit `AppScreen` of Menu / Loading / Playing / Saving; every page rule, key rule and store
call lives in the GL-free, tested `MainMenuController`, drawn by `MenuRenderer.DrawMainMenu`
through `UiPasses.RenderMenuFrame`, which writes framebuffer 0 directly with no HDR pass,
lighting or GPU timer). START opens the worlds page, a scrollable list of `WorldStore.List()`
(most recently played first) with PLAY / EDIT (rename) / DELETE (a confirm popup, DELETE /
CANCEL) on every row, plus CREATE and BACK; an empty list says so. CREATE opens a name field
(placeholder `Unnamed World`, which is what an empty field becomes; the store makes a taken name
unique with the smallest free numeric suffix, `WorldNames.Unique`) and a seed field (empty =
random, an integer is used verbatim, any other text is FNV-1a-hashed by `WorldSeedParser`).
Enter confirms the create and edit pages (never the delete popup), Tab switches field, and ESC
steps back one page and does nothing on the main page. SETTINGS and EXIT are the pause menu's
(the settings page is shared: the one `MenuInput.SettingsPageOpen` predicate drives the slider drag
and its save on both menus). PLAY / CREATE never block inside the click: the click records the
pending work, `OnRender` presents one loading-screen frame, and the next update runs
`WorldSeed.Apply(seed)` -> `new ChunkSaveStore(dir)` -> `new World(..., store)`. The world then
streams in `WorldPhase.Loading` behind an animated loading screen (`LoadingScreenView` /
`LoadingScreenRenderer.DrawLoadingScreen`: a progress bar eased monotonically toward `World.LoadProgress`, a
wall-clock spinner, the world name and `Generating terrain...` for a new world vs `Loading
world...` for a saved one) and is **never rendered** until `IsLoaded`; after that the overlay
fades out over `HudConfig.LoadingFadeSeconds` (`UiPasses.RenderLoadingOverlay`).
While `!World.IsPlaying` Program runs no gameplay input, no pause menu and no inventory, and the
cursor stays captured from the World constructor on, so the Loading -> Playing flip never jumps
the look. A menu-launched world's lighting clock is rebased at construction, so a new world
starts at the mid-morning default (+ `VOXEL_TIME`) however long the menu stayed open; a saved
clock overrides that. The pause menu's third button is SAVE & QUIT: it presents a `Saving...`
frame, calls `World.SaveAndDispose(renderManager)` on the next update and returns to the worlds
page, and a second PLAY with another seed works in the same process; closing the window while in
a store-backed world saves it the same way (`OnClosing`). The dev overrides `VOXEL_SCRIPT`,
`VOXEL_SPAWN` and `VOXEL_LOOK` (`LaunchMode.BypassesMenu`) skip the menu entirely and build a
**storeless** World exactly as before — no persistence, no seed change, no loading phase — which
is why a dev override can never reach a saved world; `VOXEL_TIME` alone does not bypass.

Saves live in `%APPDATA%/CSVoxelEngine/saves/<slug>/` (`WorldStore.DefaultRoot`; the slug is
fixed at creation and never follows a rename): `world.json` (name, seed, created and last-played
timestamps, format version, and an optional `source` only the dedicated server reads — see "World
sources"), `chunks/<cx>_<cy>_<cz>.bin` (one Deflate-compressed,
block-name-paletted snapshot per **modified** chunk, `ChunkSaveCodec`; an untouched chunk is
never written and simply regenerates), `lod-edits.bin` (`LodEditState`: column caps,
felled-canopy markers, grown crowns, surface overrides), `wood-damage.bin` (`WoodDamageCodec`: the permanent chop damage of natural trunk cells, singleplayer only), `player.json` (`PlayerSaveCodec`: position and look,
creative/flight, health, mana, respawn eye, lighting clock, the full inventory and hotbar (a staff's bound spells by part key), the known spell parts by key and the discovered waystones; no position
while the spawn has not settled) and `entities.json` (`EntitySaveCodec`: the live mobs
with health, facing, velocity, ammo, a slime's size and a skeleton's tower tier and tower floor plus still-pending entries, up to
`MaxSavedDrops` item drops, nearest first, and the drifting icebergs) and `stations.json` (`StationSaveCodec`:
the campfires, querns, kilns and cast molds with their fuel, fire, crucible, cooking and cast progress, the used chests,
the waystone registry and the battle towers' floor locks). See "Saves" below for the rules.

## Saves

A world opened from the main menu owns a `ChunkSaveStore` over its save folder (layout under
"Launch flow" above); a World built without one (the tests, and every `VOXEL_SCRIPT` /
`VOXEL_SPAWN` / `VOXEL_LOOK` run) regenerates and forgets exactly as before. Worldgen stays a
pure function of the seed: only **deltas** and the seed are written.

- **Chunk persistence ordering** (`ChunkSaveStore`'s class doc is the reference). Writes are
  asynchronous: the main thread only copies arrays (`Chunk.TakeSnapshot`) and one background
  thread (`ChunkSaveWriter`) builds the palette, deflates and writes. `Enqueue` publishes the
  snapshot in the pending map **before** the job is queued, and `ChunkManager` enqueues
  **before** it forgets the unloading chunk, so a chunk that streams straight back in is
  restored from memory. The writer writes `.bin.tmp`, moves it over the final file (retried
  `SaveMoveRetries` times), adds the key to the on-disk index and only **then** removes the
  pending entry — and only if it is still the snapshot it wrote, so a newer enqueue survives. A
  lookup therefore always finds the chunk in one of the two, and a move that finally fails keeps
  its pending entry and is remembered as failed, so the chunk stays readable for the session,
  the next flush of it retries, and `Shutdown` re-queues every failed write once more
  (`ChunkSaveStore.RetryFailed`) before the writer stops. A file a read gave up on (an
  `IOException` after a few retries, or a corrupt file) is moved to `.bad` before the writer
  first replaces it, so what the player had is never overwritten by a regenerated chunk or an
  empty player file, and the temp file is flushed to disk before the rename. The store holds
  `session.lock` (`FileShare.None`) from construction until its writer thread exits, so a second
  store over the same folder — this process after a writer overran its shutdown timeout, or the
  dedicated server beside the game — throws `IOException` from the constructor: `Program` shows
  the could-not-open notice and the server prints a line and exits.
  The index is built once at open; leftover temp files from a crash are deleted then.
- **What marks a chunk dirty.** `Chunk.ModifiedSinceSave` is set by the three raw writers every
  edit goes through (`SetBlockRaw` — which placement, state writes and the fluid tick use —
  `RemoveBlockAt` and `ClearBlockAt`) and never by generation, so a fluid settling after an edit
  is saved too, while an untouched chunk is never written.
- **Restored chunks** (`Chunk.RestoreFrom`, on the generation worker) skip worldgen: blocks,
  state bytes and the player-placed cells come from the snapshot, the solid count and surface
  floor are recomputed, the fluid frontier is always rescanned, and `FluidManager` wakes the
  borders of a restored chunk (and of a generated chunk beside one), since a save can hold
  fluid anywhere. A chunk an imported map supplies goes through the same restore
  (`Chunk.RestoreFrom(snapshot, column)`, with the source's column) and is `IsRestored` too;
  like any restore it is not `ModifiedSinceSave` until edited, so the save only ever holds the
  edited imported chunks, and a saved chunk wins over the map (see "World sources").
- **`world.json`'s `source` and the `import/` folder.** `source` (`WorldInfo.SourceName`, kept
  raw) names the world source a dedicated server builds the world from; `WorldStore` writes it
  back on every rewrite (rename, touch) only when it is set, so a world the game creates never
  gains the field, and `CurrentVersion` did not move (an older file reads as `generated`). The
  map an `imported` world reads lives in the world folder's `import/` (`ImportedMapSource`);
  `ChunkSaveStore` never reads or writes it (it indexes `chunks/*.bin` and clears leftover temp
  files in the world folder, `chunks/` and `players/` only), but deleting the world from the menu
  deletes it with the folder.
- **The far LOD always reflects the current terrain.** `LodSurface` holds per-column surface
  overrides (the real top height and the block wearing it, REPLACE semantics), beside
  `LodColumnCaps` (MIN semantics, only ever falls, so a dug hole never exposes the buried
  shell) and `TreeCanopy.CanopyDisturbed` (the edited-canopy markers `PoisonCanopy` writes: one bit per
  fine column, kept as four 64-bit words per chunk column and never evicted or cleared; the file still
  lists them column by column, TODO `M-10`) and `TreeCanopy.CanopyAdded` (the grown crowns a
  reactive growth added back over those markers); a fine column's LOD height is
  `min(override ?? generated, cap)`, fed in before the erosion window and bias so the nesting and
  burial invariants hold by construction. `LodEditState` exports all four to `lod-edits.bin`
  (only when their combined version moved) and imports them **before** `LodManager.Start`, so
  the first region build already sees them.
- **`WorldSeed.Apply(seed)`** is how a world gets its seed (`WorldGenerationConfig.Seed` reads
  `WorldSeed.Value`; `WorldSeed.Default` is what tests and scripted runs use). It is legal only
  while no World is live (`WorldSeed.BeginWorld` in the World constructor, `EndWorld` at the very
  end of a clean teardown; `WorldIsLive` reports it), and it rebuilds every worldgen noise, clears
  the thirteen `GenerationalCache` sites and resets the process-global LOD edit state — always, even
  for the same seed, since another world sharing it must not inherit the previous one's edits.
- **The loading phase.** A World built with a store starts in `WorldPhase.Loading`. The saved
  player is applied before the workers start, so they stream around the restored position;
  `WorldLoadGate` flips the world to Playing once every chunk within `LoadReadyRadiusChunks` of
  the camera is generated and meshed (`Chunk.IsMeshed`), or after `LoadTimeoutSeconds` of update
  time (which still reports `IsLoaded`). While loading, the raised `Loading*PerFrame` budgets
  apply (still bounded, so no frame spikes), every input entry point (throw charge and its
  cancel, use/place, mining, ...) is refused through `IsPlaying`, and World holds
  `Camera.SuppressLook` until the flip — which is why Program opens no menu and runs no input
  while `!IsPlaying`. The far LOD never gates loading.
- **`World.SaveAndDispose(renderManager)`** is the teardown, main thread only and blocking: the
  camera detaches, the generation and LOD workers are stopped and joined
  (`WorkerStopJoinTimeoutMs` each) and the mesh runners drained (`MeshDrainTimeoutMs`) first, then
  `WorldPersistence.FlushAll` hands every modified chunk, the LOD edit state, the player and the entities to the
  store, then the store shuts down (`SaveWriterShutdownTimeoutSeconds`). Only when every join and
  the drain succeeded are the chunk and LOD slices released to the shared pools, the toppling-tree
  meshes disposed and `WorldSeed.EndWorld` run; `RenderManager.ForgetWorld` drops its references
  (and the eased underwater/lava eye state). A timed-out join leaves `WorldSeed.WorldIsLive` true,
  so the seed stays locked for the rest of the process and the menu refuses PLAY/CREATE with a
  notice; a false return with the seed free (a writer timeout) is only a warning, but the folder
  stays locked until that writer finishes, so a PLAY on the same world before then is refused
  with the could-not-open notice. The World owns
  the store's `Shutdown`: never shut it down again, and give the next World a **new**
  `ChunkSaveStore` over the same folder.
- **Autosave** (`WorldPersistence.Autosave`) runs every `AutosaveIntervalSeconds` (at most `MaxAutosaveSnapshotsPerCall`
  snapshots a call, the rest on following frames) after the fluid tick, under the one `save`
  profiler row, which is entered nowhere else.
- **Player and entities.** `player.json` never stores a position before the spawn settled, so a
  world closed mid-load searches the surface again next time. Saved mobs and drops are spawned
  once loading finishes, each only when its chunk is loaded; a restore is a forced spawn, so it
  may pass the natural mob cap but a mob waits while the hard cap (`MobHardCap`, which sizes the
  healthbar buffers) is full; an entry that waits stays pending and is saved again, and is
  discarded only after `EntityRestoreTimeoutSeconds` of play. A saved iceberg (`Icebergs` in
  `entities.json`) waits until every chunk under its placed bounds is loaded, and a sinking berg
  is not saved.
- **Stations** (`stations.json`, `StationSaveCodec`/`StationSaveData`): restored into the empty
  managers when the world is built, with no wait for chunks, because every manager already freezes a
  cell in an unloaded chunk and sweeps an entry whose block is gone (dropping what it held) once the
  chunk loads. An entry whose block, item or molten material name no longer resolves is dropped with
  one aggregated warning, and a cast pour is restored paused (its crucible binding is transient). A
  corrupt file parses to null and is replaced at the next autosave, like `entities.json`.
- **Not persisted** (tracked as `M-12` in `plans/TODO.md`): primed TNT, falling blocks, projectiles
  in flight, spell bolts in flight, toppling trees and the sapling clock (a half-grown sapling stands where it is until it
  is grown again).
