# Multiple players and chat

The dedicated server, world sources, the wire protocol, world intents, streaming, player status, the movement rubberband, the client, chat and the known gaps.

## Multiple players and chat

A dedicated **server** is a fourth project (`OpenGLTest.Server/`, `dotnet run --project
OpenGLTest.Server/VoxelEngine.Server.csproj -c Release`, working directory with `assets/` up the tree — the
program sets its own current directory to the assets parent so `FileUtils`' relative `assets/...`
paths resolve). It is a **CLI**: it loads content headlessly (`ContentRegistry.Load` +
`TextureAtlas.RegisterTilesWithoutUpload` + `ContentRegistry.BakeModels`, the `TestContent`
recipe), lists `WorldStore.List()` and asks for a world number or `N` for a new one (name and
optional seed), asks for a port (default `ServerAddress.DefaultPort`, 25566; `--allow-creative` on
the command line sets `GameServer.AllowCreative`, default `NetworkConfig.ServerAllowCreative`,
off, and every repeatable `--op <name>` joins `GameServer.Operators` with the names in the world
folder's `ops.json` — see the creative permission below — and the server prints one line saying
who may be creative), resolves the world source (`--source <name>`, else `world.json`'s
`source`; see "World sources" below), then builds a
**server-mode** `World` (`new World(..., store, serverMode: true, worldSource: source)`) over that world's
`ChunkSaveStore` and runs `World.Update(dt, null)` in a ~1 ms loop with `GameServer.Pump(dt)`.
The world list marks a world whose `WorldInfo.Version` is not `WorldStore.CurrentVersion` as
"(old format, cannot be hosted)" and refuses to select it (the menu's PLAY gate), and a world
folder another session still holds (the store's `session.lock`, an `IOException` from the
`ChunkSaveStore` constructor) is refused with a message before any World is built. A port already
in use (`SocketException` from `GameServer.Start`) prints a line, closes the world and exits.
Ctrl+C, a typed `stop`, `AppDomain.ProcessExit` and the `SIGTERM`/`SIGHUP` `PosixSignalRegistration`s
(each in a try/catch for `PlatformNotSupportedException`) all set the stop event; the loop's
`finally` runs `GameServer.Stop`, `SaveAndDispose` and `Log.Shutdown` on a clean stop and on an
unexpected exception alike, and the signal handlers wait for it (bounded by
`ServerShutdownWaitSeconds`) before letting the process end, so a server session persists its
edits however it is stopped. Server mode sets `ChunkManager.Headless` (finished meshes are marked and their light
published by `Chunk.MarkMeshedHeadless`, the geometry dropped — a headless world has no pools),
hovers the camera creative+flying so mobs cannot hurt it (and therefore never saves that camera
as the player: `WorldPersistence.CapturePlayer` writes back the `player.json` the world was opened with, kept as
`_hostedPlayer`, with only the lighting clock refreshed — its inventory too is written back as it
was opened, since every connected player carries their own — or for a world that had none no pose,
no creative or flight, no respawn eye and no inventory, so a survival world opened after hosting is
not in creative flight at the server camera's hover), and skips the LodGen threads; the
global `WorldSeed` still applies, and the thirteen `GenerationalCache` sites behave exactly as in the
game, which is what lets a headless World run `Update` over real terrain (the LOD builder already
drops real builds when its pool is null). `ServerWorldTests` locks this down.

**World sources.** Where a world's terrain comes from is one seam, `IWorldSource`
(`Chunks/Sources/IWorldSource.cs`). It extends `IColumnSource`, so the mesher's column reads and
the server's column answers read the same object, and adds three members: `GetColumn(cx, cz)`, the
complete `ColumnData` of a chunk column (heights, bed, water levels, biomes, dirt depth, corner
climate; never null); `Supplies(cx, cz)`, whether the source supplies that column and its whole
chunk stack itself or defers both to worldgen (cheap: no computation, no allocation); and
`Fill(chunk)`, which fills a chunk built with `generateBlockData: false` and leaves
`IsBlockDataReady`, the surface floor, the non-air count and the fluid frontier set. A source is
called on the generation workers, the pool mesh builds, the LodGen threads and the main thread, so
it must be thread-safe and deterministic, never touch GL and **never write into a
`GenerationalCache`**: those hold pure functions of their key, which a custom source's terrain is
not. A source is per world, never a static, so `ColumnCache` and `WorldSeed.Apply` keep their
contract and two in-process worlds (the loopback tests) cannot interfere.
`GeneratedWorldSource.Instance` is the default: `GetColumn` is `ColumnCache.Get` (the cached
instance, no copy), `Supplies` is always false and `Fill` is `Chunk.GenerateBlockData`, so
singleplayer and every World built without a source run exactly the old reads in the old order.
The source sits on `ChunkManager.WorldSource` (and `LodManager.WorldSource`, which `World` copies
before `Start`), set from `World`'s optional `worldSource:` constructor argument before any worker
starts (published by `Thread.Start`), which also installs it as `ChunkManager.ColumnSource`; null
leaves both defaults (`GeneratedWorldSource`, `WorldgenColumnSource`) untouched. The readers that
describe the live world go through it:
- `ChunkManager.CreateChunk`, the single seam for where a chunk's blocks come from: the save
  store still wins (its restore reads the source's column), otherwise `WorldSource.Fill`;
- `FluidWakeScanner`'s wake scans (`WakeLoadedChunks`, `WakeBorders`) and its generated-water test;
- the LOD edit hooks, `LodSurface.TopOrHeight(source, x, z)` and
  `LodColumnCaps.LowerColumnBelow(source, x, y, z)` (the source-less overload is worldgen's);
- `LodRegion.BuildMeshData(..., source:)` (its body is `LodMeshBuilder`), from `LodRegionClaims.TryBuildOneRegion` and the server's
  `LodRegionRequest` (`ChunkManager.WorldSource`). A supplied column's far LOD takes the biome and
  water level at the cell center from its `ColumnData`, snow from a Taiga or Frozen biome, sea ice
  from Frozen (it carries no cold weight, so this approximates the generated pack-ice band of
  `SnowGenerator.IsFrozenWater`), beaches from `TerrainGenerator.IsBeachHeight` in the Ocean biome, bare rock from a
  zero `DirtDepth` in a snowless grass biome (`BiomeTraits.ShowsBareRock`), a permafrost soil band
  and lake bed in the Frozen biome (stone at a zero `DirtDepth`) and the tint lattice from the
  stored corner climate, and it
  draws no canopy bumps (its trees are in its blocks) and no gravel caps; an unsupplied column keeps
  every worldgen read verbatim, so a generated region is byte-identical to the build without a
  source;
- `World.TryGroundHeight` and the spawn settle (`LocalPlayer.EnsureSpawnSettled`, whose water test is now the
  column's stored `WaterLevel`, the same value `TerrainGenerator.IsWaterColumn` evaluates for a
  generated column);
- `MobSpawner.TryFindSurfaceSpawn` (heights and water from the column; a supplied column's
  mob-biome test, `MobSpawner.SpawnsMobs`, reads its own `Biomes`, a generated one keeps the
  undithered `TerrainGenerator.BiomeAt`);
- the server's `ColumnRequest`, answered from `ChunkManager.ColumnSource`, exactly what its own
  mesher reads.

Worldgen's own pure functions deliberately stay static and never see the source:
`ChunkTerrainFill.GenerateBlockData`, the carvers (`NoiseCaves`, `WormCarver`), `TreeGenerator` (placement,
batches, the far-LOD canopy), `VirtualTreeSpace` and `SpawnOverrides.SpawnPosition` (a server's camera
starts at the generated origin surface, harmless since it is creative and flying, and the spawn
settle then re-settles it on the source's terrain). A generated chunk beside a supplied column
therefore generates as if the source were not there (see the known gaps).

**`ImportedMapSource`** (`Chunks/Sources/ImportedMapSource.cs`) is the non-generating source: a map
folder (`<world>/import/` on a server, `ImportedMapSource.DirectoryName`) holding
`columns/<cx>_<cz>.col` and `chunks/<cx>_<cy>_<cz>.bin`. A column file is `ColumnFileCodec`
(`Saves/ColumnFileCodec.cs`): the magic `CVXL`, a version byte (1), then a Deflate body of 256
int32 heights, 256 int32 bed, 256 int32 water levels, 256 biome bytes, 256 dirt-depth bytes and the
corner warmth and wetness floats, all little-endian; `EntranceWeight` and `CaveIntensity` are
worldgen-only carver inputs, not stored and decoded as zeros. A chunk file is the save store's own
`ChunkSaveCodec` (block-name paletted, so ids never leak into a map; player-placed cells in it are
ignored). A column with a file is **supplied**: its `ColumnData` comes from the file and every chunk
of its stack from its chunk file, or is **all air** when there is none; every other column, and its
chunks, comes from the fallback (worldgen by default). Only canonical file names count. Every
column file is loaded once at construction into a table never modified afterwards (a known limit:
about 3.5 KB of decoded arrays per column, all at once, plus a startup log line with the column and
chunk-file counts) and every chunk read opens its own stream, so the generation workers, the mesh
builds and the LodGen threads read it concurrently and deterministically. A damaged column file
refuses the whole map at construction (`InvalidDataException`/`IOException` naming the file); a
damaged chunk file is retried like a save read and then loads as air with a warning, so a position
is never left stuck in the queued set. Supplied chunks are restored through
`Chunk.RestoreFrom(snapshot, column)`: they are `IsRestored` (so the fluid wake always scans their
borders) and not `ModifiedSinceSave` until edited, so an untouched imported chunk is never saved
and an edited one is saved and wins over the map on reload. `ImportedMapWriter`
(`Saves/ImportedMapWriter.cs`, tool and test use only) writes the format (`WriteColumn`,
`WriteChunk`, each through a temp file) and `ExportGenerated(dir, source, cxMin, cxMax, cyMin,
cyMax, czMin, czMax)` seeds a map from any source (over `GeneratedWorldSource`, worldgen under the
current seed), writing every column in the box and every chunk that is not all air.

**Choosing a source.** The dedicated server reads `world.json`'s `source` (`WorldInfo.Source`:
`generated`, the default when the field is absent, or `imported`; `WorldSources.Names`), and
`--source <name>` on the command line overrides it (case-insensitive, never persisted; a bare
`--source` prints that it needs a name and exits). `WorldSources.Create(name, worldDirectory)`
(`Saves/WorldSources.cs`) builds it after the port prompt and the seed-lock check and **before**
the `ChunkSaveStore` takes `session.lock`, so an unknown name (an `ArgumentException` listing the
names), a missing `import/` or `import/columns/` (`DirectoryNotFoundException`) or a bad column
file prints `Cannot use world source '<name>': ...` and exits without locking the folder. After the
`Serving` line the server prints `World source: generated` or `World source: imported (<map dir>;
generated outside the imported columns)`. `WorldSeed.Apply(world.Seed)` still runs for an imported
world, since its fallback columns depend on it. Clients need nothing new: they never generate, and
the chunks, columns and far LOD they stream are the source's. Tests: `WorldSourceTests` (the
default source fills exactly like worldgen and hands back the cached column; a custom source
supplies its chunks and columns and the fallback the rest; the save store wins over the source; the
LOD edit hooks, the fluid wake and the ground height and spawn read the source; `FlatWorldSource`
is the test double), `WorldSourceLodTests` (a supplied column's far LOD follows its own heights,
biome, stored climate and dirt depth; an unsupplied one stays byte-identical),
`ColumnFileCodecTests`, `ImportedMapSourceTests` (an exported generated region fills exactly like
worldgen, an absent or damaged chunk is air, a corrupt column file or a missing folder is refused),
`WorldSourcesTests` and the `source` rule in `WorldStoreTests`, and `ImportedWorldStreamingTests`
(over a loopback server, an imported world is played and settles its spawn on the map, and a bare
client `ChunkManager` receives its chunk, column and far LOD without generating anything; an
edited imported chunk is saved and wins on reload while unsaved chunks still come from the map).

**The main menu** grew a fourth button, MULTIPLAYER (the main page no longer shares
`PauseMenuLayout`'s three-button stack; `MainMenuLayout.MainButtonCenter`/`MainTitleTop` own the
four-button page). It opens the multiplayer page, which is shaped exactly like the worlds page
(`MainMenuPage.Multiplayer`): a scrolled list of `ServerStore.List()` (name over address) with
CONNECT / DELETE per row, ADD SERVER / DIRECT CONNECT / BACK across the bottom, and an empty
state. ADD SERVER (`MainMenuPage.AddServer`) takes a NAME and an ADDRESS; DIRECT CONNECT
(`MainMenuPage.DirectConnect`) takes just an ADDRESS. `ServerAddress.TryParse` accepts
`host`, `host:port`, `[ipv6]:port` and a bare IPv6, defaulting the port; a bad address keeps the
page up with `MainMenuController.BadAddressNotice`. Servers live in one JSON file,
`%APPDATA%/CSVoxelEngine/servers.json`, owned by `ServerStore` (unique case-insensitive names via
`WorldNames.Unique`, most-recently-connected first, a temp-file write); a row CONNECT touches it
and returns a `ConnectTarget` through `MainMenuAction.Connect`, which Program turns into a real
session.

**The wire protocol** is in `OpenGLTest/src/Networking/NetProtocol.cs`: `NetFraming` writes a
4-byte little-endian length then the payload whose first byte is a `NetMessage`, and
`PacketWriter`/`PacketReader` carry the fields (`BinaryWriter`/`BinaryReader`, strings 7-bit
length prefixed, `Vector3` as three floats). Every block or item id field is two bytes (a ushort) and goes
through `PacketWriter.WriteId`/`PacketReader.ReadId`, the one place that fixes an id's wire width; a chunk payload
is the palette form (`ChunkNetCodec`, inside Deflate): a mode byte (0 Uniform, 1 Indexed, 2 Direct), then one
little-endian id, or a byte holding the palette count minus one, that many little-endian ids and 4096 index bytes, or
4096 little-endian ids, then the state flag (0 none, 1 followed by 4096 state bytes). The palette goes out as the chunk
holds it (stale entries cost a few bytes); the decoder refuses every malformed payload (an unknown mode, an index at or
past the palette count, a state flag other than 0 or 1, truncation anywhere) as `InvalidDataException` and compacts the
cells with `ChunkBlocks.FromDense`, so a decoded snapshot never carries a duplicate or unused id. Ids are handed out by file name at startup, so the client and the server
must load identical content: the client checks every server id that indexes a block table (`BlockTables.RequireAssigned`,
`AllInRange` over a decoded chunk's `Storage.IdSpan`), and an id outside its content throws `InvalidDataException`, which disconnects with "The
server sent bad data" instead of crashing. `NetFraming.ProtocolVersion` must match in both
hellos. Messages: `ClientHello` (version, name), `ServerHello` (version, player id, world name,
seed, day clock, spawn), `KeepAlive`, `ChatSend`/`ChatMessage` (kind, sender, text, optional link; `ChatMessageCodec`),
`PlayerState` (eye, yaw, pitch, flags, the newest correction it acks), `PlayerStatus` (the
player's server-owned status, `PlayerStatusCodec`: the creative/flight flags the server actually
granted, health, a dead flag, the knockback impulse and lift of the mob hits since the last
one, the air, the mana, the effect mask and the cave biome at the eye; sent at join, whenever it changed,
and once when a mode request was refused),
`PlayerCorrection` (a numbered eye the client must snap to: an illegal move undone, or a
respawn), `PlayerList` (id, name, eye, yaw, pitch, flags), `WorldIntent` (a client's world action, `WorldIntentCodec`), `IntentResult` (sequence id,
accepted), `BlockCorrection` (sequence id, then the authoritative cells around the intent's target),
`ChunkRequest`/`ChunkData` and `ColumnRequest`/`ColumnTerrain` (the streamed world,
`ChunkNetCodec`; a column answer is the whole `ColumnTerrain`, `EncodeColumn`), `LodRegionRequest`/`LodRegionData` (a streamed far region, `LodNetCodec`),
`EntityList` (the live mobs, drops, primed TNT and falling blocks, `NetEntity`), `StationState` (the campfires, querns, kilns and
cast molds, `StationNetCodec`), `BlockChanges` (a batch of server cell changes, `NetCellChange`)
and `InventoryState`/`InventoryRequest` (a player's server-owned inventory, `InventoryNetCodec`,
and its intents, `InventoryIntentCodec`), and the waystone messages `WaystoneRename` (a client's rename request),
`WaystoneRows` (a player's registry rows) and `WaystoneOpened` (what a waystone activation came to; `WaystoneNetCodec`,
see "Waystones on a server"). The intent protocol came with version 3: a client
never writes a cell (the old raw `BlockEdit` is retired and refused), every intent carries a
per-player sequence id, and `InventoryState` carries the id of the newest intent it reflects.
Version 4 added the status payload of `PlayerStatus`, the
`PlayerCorrection` message (id 25), the correction ack in `PlayerState` and the `Respawn` intent.
Version 5 made a column answer the whole `ColumnTerrain` (heights, water levels and the corner
climate) instead of the heights alone, so a client derives nothing from worldgen.
`ProtocolVersion` 6: `EntityList` also carries `NetEntityKind.PrimedTnt` (4;
`ItemId` the block that primed, `Health` the fuse remaining and `Ammo` the whole fuse, both in
milliseconds) and `NetEntityKind.FallingBlock` (5; `ItemId` the block), and an older client is
refused at hello. A slime entry's `Ammo` (a goblin's flint) carries the slime's size; the layout
did not change, so no version bump: an older server's 0 reads as the smallest size, and a mirror
whose reported size differs is replaced (`ClientMirror.KindMatches`). A hell, sky or tower mob's `Ammo` carries its
attack count the same way (`RealmMob.AttackCount`, see "A mirror's attack clip"), with no bump either. Every
entry is written and read by one codec, `NetEntityCodec`. Version 9 ends `PlayerStatus` with the player's air byte. Version 10 lets an `Attack` intent carry the sword's swings (`NetAttackKind.SwordCut`/`SwordBackCut`/`SwordChop`, mapped by `NetAttacks`) and the player list their `NetPlayerAction`s; an older peer would refuse the intent as malformed and drop the connection. Version 11 ends `PlayerStatus` with the player's mana and its max (two ints, clamped on read). Version 12 adds the `Cast` world intent (its slot byte) and the `SelectStaffSlot` inventory action; an older peer would refuse either as malformed. Version 14 adds the `BindStaffSlot` and `ClearStaffSlot` inventory actions (the spell table); an older peer would refuse either as malformed. Version 15 adds the known spell parts (`KnownParts`, by key) to the `InventoryState` snapshot. Versions 19, 20 and 21 add the gear equip actions, the per-player chest messages and the worn armor pieces at the end of each player list entry (see "Gear and equipment" and "Chests" above). Version 34 (current) sends the chunk payload in the palette form (a mode byte, then one id, or a palette of up to 256 ids with one index byte per cell, or one id per cell past 256 distinct ids, then the state as before; see "The wire protocol" above); an older peer would misread every `ChunkData` frame. Version 33 makes block and item ids two bytes (`PacketWriter.WriteId`) everywhere an id travels: the chunk payload (then a dense ushort per cell), cell changes and corrections, entity item ids, worn armor, inventory and world intents and the station frames; an older peer would misread every frame carrying an id. Version 32 ends each anvil of the `StationState` frame with its stacked metal's id and count and adds the `Forge(cell, recipe)` world intent (see "The bloomery and the anvil"); an older peer would misread the station frames and refuse the intent as malformed. Version 31 packs a skeleton's role and outfit above its tier in its `EntityList` `ItemId`
(`Skeleton.PackLook`/`UnpackLook`: tier in the low 3 bits, the role bit, the outfit code on top; a mirror whose look
differs is replaced, `ClientMirror.KindMatches`); an older client would read a warrior or an armored skeleton as another
tier. Version 30 ends every `ChatMessage` with an optional link (its suggested command, empty for none, then its start and length in the text; `ChatMessageCodec`, the frame's one writer and reader, which refuses a command the input could not hold or a span outside the text); an older peer would misread the frame. Version 29 lets `EntityList` carry the cave mobs (`NetEntityKind.Shardling` 15, `Sporeling` 16 and `Magmaborn` 17): the depth tier rides `ItemId` (clamped to 0..`MobConfig.CaveMaxTier` on read; a mirror whose tier differs is replaced, `ClientMirror.KindMatches`) and the attack count rides `Ammo` (see "Cave mobs"); an older client would not draw them. Version 28 appends the eye's cave biome byte to `PlayerStatus` (`PlayerStatusData.CaveBiome`, an undefined value reads as Plain); an older peer would misread the status. Version 27 appends each battle tower's altar state byte to its `StationState` record and lets `EntityList` carry the Skeleton King (`NetEntityKind.SkeletonKing` 14: its fight phase rides `ItemId`, clamped to 1..3 on read, and its attack count rides `Ammo`, slam, volley and summon by remainder; see "Battle tower"); an older peer would misread the station frames and an older client would not draw the king. Version 26 appends the battle tower locks at the end of `StationState` (`TowerManager.WriteNet`/`ReadNet`: each tower's cell, center and radius, and per floor its band, piles left, skeletons left and door bit, bounds-checked; see "Battle tower"); an older peer would misread the station frames. Version 25 lets `EntityList` carry the battle tower's skeleton (`NetEntityKind.Skeleton` 13): its tower tier rides `ItemId` (a goblin's torch byte, 0 for other mobs; clamped to 0..`MobConfig.TowerMaxTier` on read, since a mirror cannot derive it from where it stands, and a mirror whose tier differs is replaced, `ClientMirror.KindMatches`) and its attack count rides `Ammo` (melee even, shot odd, see "A mirror's attack clip"); an older client would not draw it. Version 24 adds waystones: the `Waystone(cell)` world intent and the `WaystoneRename`, `WaystoneRows` and `WaystoneOpened` messages (see "Waystones on a server"); an older peer would refuse the intent as malformed and never open a waystone. Version 23 adds the sandworm boss (`NetEntityKind.Sandworm` for its head, its phase in the ammo field, and `SandwormTarget` for the point it will surface at; see "Sandworm"); an older client would not draw it. Version 22 appends the bloomeries and anvils to the `StationState` frame and adds the `Hammer` world intent (see "The bloomery and the anvil"); an older peer would misread the station frames and refuse the intent as malformed. Version 18 lets `EntityList` carry the sky mobs (`NetEntityKind.Galeraptor` 9 and `Cloudgolem` 10, the
mob fields only: a flyer's whole position and velocity ride the entry and its flight follows from its kind, and a
mirror's zone tier, and so its pool, follows from the column it is first seen over); an older client would not draw
them. Version 17 lets `EntityList` carry the hell mobs (`NetEntityKind.Hellbrute` 7 and `Cinderwitch` 8, the
mob fields only: a mirror's layer, and so its pool, follows from where it is first seen); an older client would not
draw them. Version 16 makes a crucible carry one entry per metal (the alloy kiln): a `StationState` kiln entry writes a metal count (at most `CrucibleComponent.MaxMetals`; `StationNetCodec.ReadCrucible` refuses more, a material 0, a repeat or a negative amount) and each metal's id, molten and pending ml, and the `InventoryState` crucible a `Metals` list; an older peer would misread the station frames. Version 13 ends every `EntityList` entry, of every kind, with a status-effect byte mask (`NetEntity.Effects`: a mob's `StatusEffect.Bit`s, 0 for a drop, primed TNT, falling block or iceberg) and ends `PlayerStatus` with the player's own effect mask (`PlayerStatusData.EffectMask`); both codecs carry the mask raw and the client drops unknown bits when it mirrors them, and an older peer would misread every frame after the first entry. Version 8 added
`NetEntityKind.Iceberg` (6): `Position` the waterline center, `Yaw` the quarter turns times pi/2
(`NetEntity.YawFromTurns` / `TurnsFromYaw`), `Health` the seed, `Ammo` the scale in thousandths and
the sinking in its next byte (`NetEntity.PackIcebergAmmo` / `UnpackIcebergAmmo`), `ItemId` the
variant and `Velocity` the drift the client dead-reckons along; an older client is refused, since
it would neither draw nor collide with bergs while the server judges a rider's hover against
them. A
`WorldIntent` is the sequence id, a `NetIntentKind` byte, the eye and the unit aim the action was
made from, then that kind's fields (`WorldIntent`): `BreakStart`/`BreakComplete(cell)`,
`Place(hit cell, face normal)`, `Use(cell under the crosshair, optional)`, `Throw(charge 0..1)`,
`Attack(punch | slice | slice return)`, `Respawn` (nothing more; never rate-limited, edits no
cell), `Cast(slot)` (a held staff's spell slot as one byte; never edits a cell, rate-limited) , `Hammer(cell)` (one hammer hit on an anvil; never edits a cell, rate-limited), `Waystone(cell)` (a trip to the
discovered waystone at that cell from the one the player last activated; never edits a cell, rate-limited) and
`Forge(cell, recipe)` (a pick from an anvil's recipe list, the recipe named by its result id byte; never edits a cell,
rate-limited, answered with the inventory). Its
reader refuses an unknown kind or attack, a cast slot at or past `SpellConfig.StaffMaxSlots`, a
non-finite or far-out eye, an aim that is not close to a unit vector and a normal component
outside -1..1 as an `InvalidDataException`, renormalizes the aim and clamps the charge. An
`InventoryRequest` is the sequence id, then a `NetInventoryAction` byte and only that action's fields
(`InventoryIntent`): `SelectSlot(slot)`, `DropSelected`/`DropAllSelected`, `Equip(slot, isTool,
id)` (a stackable's id or a tool's instance id), `Craft(recipe file name, batch, fed member per
ingredient)`, `CreativeGrant(id, count)`, `CreativeClear(id)`, `PickBlock(id)`, `SelectStaffSlot(slot)` (the held staff's selected spell
slot, refused without a staff or for a slot it lacks), `BindStaffSlot(slot, part keys)` (binds a
recipe to the held staff's slot through `HeldStaff.TryBind`: refused without a staff, for keys that do
not resolve, for a part the player does not know (`KnownSpellParts`; creative knows all) or for a
recipe `StaffBinding.TryBind` refuses against the staff's tags) and `ClearStaffSlot(slot)` (refused
without a staff or for an unbound slot). The reader is
bounds-checked: an unknown action, an overlong recipe name, more than
`InventoryIntentCodec.MaxCraftMembers` members, more than `MaxSpellParts` part keys or a key
longer than `MaxPartKeyLength` is an `InvalidDataException` (the sender is
dropped like any malformed frame), while merely out-of-range values parse and are refused.

**The server** (`OpenGLTest.Server/GameServer.cs`, the composition root over `ServerTransport`, `PlayerSessions`, `IntentReplay`, `StreamRequests`, `ServerBroadcasts` and `ServerChat`, which share a `ServerState`) is a dual-mode `TcpListener.Create(port)`
(IPv6 any with IPv4 mapped, so `[::1]:port` works like `127.0.0.1:port`) with one reader thread per
connection that only frames inbound bytes onto a `ConcurrentQueue`; `Pump` (main thread) drains it
and owns every piece of `World` state, so the simulation stays single-threaded exactly like the
client. A transient `SocketException` from the accept is logged and retried rather than ending the
accept thread. Each `Handle` call in `Pump` runs inside a try/catch: a short or malformed frame
(`EndOfStreamException`, `IOException`, `InvalidDataException`, `FormatException`,
`ArgumentException`, `ObjectDisposedException`) logs, closes and removes only that client. Sends
are blocking writes on the main thread, so every socket gets a `NetSendTimeoutMs` write timeout,
and a failed or timed-out send faults the connection (no further sends) and queues its removal
once. The accept thread queues each new connection on `ServerTransport.Accepted` before its reader starts; `Pump`
moves the ones that have not said hello into `ServerState.Pending`, and once a second sweeps (`PlayerSessions.SweepIdleConnections`) every
connection, hello'd or pending, whose reader saw no frame for `NetIdleTimeoutSeconds`
(`ClientConnection.LastInboundAt`, `Volatile` ticks; clients send `KeepAlive` every
`NetKeepAliveSeconds` and their state at 20 Hz). `Stop` closes the pending ones too. Every
connected player is a **`ServerPlayer`** (`OpenGLTest/src/Networking/ServerSide/ServerPlayer.cs`): id, a name
unique among the players online by its save slug (a second "Player" joins as "Player 2"), its
**own** `Inventory` (`Inventory.Empty()` for a newcomer), server-owned `Creative`/`Flying` flags
(survival by default), its own health and respawn eye (see "Player status" below), the accepted
pose and `LastStateAt`. On `ClientHello` (`PlayerSessions.Join`) it assigns an
id, loads the player's file (`ServerPlayerSaves`: `players/<slug of the name>.json` in the world
folder, the `PlayerSaveCodec` shape — health and respawn eye included — written through the
world's `ChunkSaveStore` — `World.SaveStore` — which accepts that one subfolder), replies
`ServerHello` with the saved position or else the **join spawn** (the server camera's position
captured at the first hello; the camera never follows the players, so a later joiner never spawns
wherever the players online have wandered, possibly inside rock), teleports the player there
(`ServerPlayer.Teleport`, which starts its fall tracking; a player with no saved respawn eye gets
that spot as one), sends that player's inventory and status, and broadcasts a system chat and the
player list; players are saved again on leaving, in `Stop` (before the world's `SaveAndDispose`
shuts the store) and every `AutosaveIntervalSeconds`. Thereafter it relays chat, records
`PlayerState` into the player list (sent every `NetPlayerListSeconds`, 10 Hz) — its flags
(`NetPlayerFlags`, the client's `C` toggle) are only a request: `ServerPlayer.Creative` is set
only while `GameServer.MayBeCreative` holds for that player, and flight only with it, and its
position goes through the movement rubberband (both below) — syncs the day clock
(`NetTimeSyncSeconds`), serves `ChunkRequest`/`ColumnRequest` from the loaded world
(`ChunkManager.TryCaptureChunk`; a column through `WorldgenColumnSource`, exactly what its own
mesher reads, encoded by `ChunkNetCodec.EncodeColumn`), queues `LodRegionRequest`s (`StreamRequests`, deduped
per connection, `ClientConnection.LodQueued`) and builds at most `NetLodBuildsPerPump` of them a
`Pump` (`LodRegion.BuildMeshData`, skipping any whose client has gone), replays each
`InventoryRequest` intent (`IntentReplay.OnInventoryRequest`) against **the sender's** inventory only — `InventoryIntentRules.Apply`
(a slot must be on the hotbar; equip only what the player owns, or toggle off what the slot
already shows; craft through `Inventory.TryCraft` with exactly the fed members, so a refused craft
spends nothing; creative grants and clears only while `ServerPlayer.Creative`; pick-block through
`Inventory.PickBlock`, which never grants; a staff bind only with a staff in hand, keys that resolve,
parts the player knows and a recipe valid against the staff's tags, and a clear only on a bound slot,
both through `HeldStaff`) and a Q-drop through
`World.ThrowSelected(wholeStack, eye, forward, inventory)` from that player's eye and look and out
of that player's inventory, never the server camera — and **always** answers with a full
`InventoryState` snapshot to that player alone, accepted or not, so a refused intent rubberbands
the client's optimistic copy. Drops are picked up per player: a server-mode world's `DropPickup.Update` reads the
connected players from `World.ServerPlayers` (the server's list) and `DropManager.Update`'s
multi-collector overload (`DropCollector`) flies each drop in a loaded chunk to the nearest player
whose inventory has room (a drop in an unloaded chunk is neither collected nor simulated; one
nobody can take lets go and falls), crediting that player's inventory and flagging
`ServerPlayer.InventoryChanged`, which the next `Pump` answers with that player's snapshot; the
server camera and `World.Inventory` never collect.

**World intents.** A client never writes a cell: every world action is a `WorldIntent` the server
replays for the sending player through the singleplayer rules. Each `ServerPlayer` owns a
`PlayerContext` (`OpenGLTest/src/Player/PlayerContext.cs`: eye, aim, view direction, its inventory, its
server creative flag) that every interaction path (`BlockEditor`, `BlockPlacer`, `LeftClickAction`,
`RightClickAction`, `ThrowAction`, `WorldItemUse`) runs through instead of the camera
(singleplayer builds one from its camera, whose raycasts keep the third-person rules). An intent
is refused (and nothing applied) when its sequence id is not above the player's last
(`ServerPlayer.AcceptSeq`); a **dead** player's intent is judged right after that, before every
rule below — only a `Respawn` is accepted, however far its eye drifted, and anything else is
refused (see "Player status"), while a living player's `Respawn` is refused. Otherwise an intent
is refused when the world is not playing, its eye is more than
`NetMaxIntentEyeDriftBlocks` from the player's accepted position (`NetValidation.EyeWithin`: one
state interval of movement plus slack), or, for an edit (break completion, place, use, throw),
the player's `EditLimiter` (`RateLimiter`, `NetEditIntentsPerSecond`/`NetEditIntentBurst`) is
empty. Then, by kind:
- **Break** — `BreakStart` and `BreakComplete` both require the player's own first-person ray from
  the intent's eye along its aim (`World.RaycastFor`, `InteractionReach`, fluids excluded) to hit
  exactly the named cell, loaded and neither air nor fluid. The start records the target, the held
  item (id and tool instance) and the server time in `ServerPlayer.Breaking` (`BreakProgress`); the
  completion breaks only when the held item's hits since then reach the block's hardness the way
  the client counts them (first hit at the start, then one per `Mining.HitGapSeconds`, each dealing
  `Mining.BlockDamage`, summed in float — `BreakProgress.RequiredSeconds`), less
  `NetBreakSlackSeconds` of jitter, never with a tool `Mining.CanMine` refuses, and never after the
  target or the held item changed; a server-creative player breaks at once. The break itself is
  `World.BreakBlockFor` (forwarding to `BlockEditor.BreakBlockFor`), i.e. the full
  `BlockEditor.RemoveBlock`: felling (`FellWood`, which now runs on the
  server), multiblock revert, loot through `DropManager.SpawnLoot` (picked up by the nearest player
  with room), a station's hand-back, needs-support pops, toppling and the fluid, leaf-decay, LOD
  and canopy hooks.
- **Place** — the ray must hit the named cell, and the named face must be the ray's own or another
  face of that cell turned toward the eye (`NetValidation.FaceFacesEye`: a third-person aim reports
  the camera ray's face; the far side of a wall never qualifies). `World.TryPlaceSelectedBlockAt`
  then places the player's **selected** block (owned, placeable, one spent unless creative) under
  the placement rules (`Placement`, no live falling cube, fragile blocks on solid ground, and no
  connected player's body in the cell — `BlockPlacer.BodyBlocksPlacement` checks every `ServerPlayer`, the
  server camera is nobody), with the world's own state byte (a placed log `WoodState.Detached`, a
  placed leaf persistent).
- **Use** — `World.TryUseSelected(InteractionReach, context, out outcome, allowPlace: false)`: the
  right-click chain (campfire, quern, kiln, cast mold, then the held item's `OnUse` — bucket, pile,
  firestarter on TNT or a kiln, converter) aimed by the player's ray and spending from its
  inventory, short of the placement fallback. The item-use tail reads the dispatching player
  (`WorldItemUse._activeContext`), and a chance roll goes through `IItemUseWorld.NextUseRoll`
  (`World.UseRoll`, a test seam).
- **Throw** — `World.ThrowFor` launches the selected throwable at the intent's charge from the eye.
- **Attack** — a swing needs the weapon that makes it in hand (`NetAttacks.PatternOf`: a slice a
  slicing blade, a sword swing a sword, a punch anything else), each at most once per its pace
  (`ServerPlayer.TryAttack`, `AttackPacing`: the held item's hit gap, `PlayerModel.SliceDuration`,
  or for a sword swing the length of the swing before it, `PlayerModel.SwordDuration` of the
  player's last attack, less `NetAttackSlackSeconds`); `World.PunchFor`/`SliceFor`/`SwordFor` find
  the targets among the server's mobs from the intent's eye and aim with the `Combat` rules.
- **Cast** — `World.CastFor` (the `Magic/SpellCaster` seam, rules in `CastRules`) with the player's
  own `ServerPlayer.Casting` pacing: a spell staff in hand (`HeldStaff`), the intent's slot equal to
  the staff's selected one and bound, its parts resolving to a valid recipe with a finite cost, the
  cooldown (`SpellConfig.CastCooldownSeconds` less `NetCastSlackSeconds`) run out, and the mana billed
  last (`PlayerContext.Health.Mana.TrySpend`, free in creative), so every refusal spends nothing. A
  spend or a refusal for mana sets `StatusDirty`, so the pool reaches the client in the same `Pump`.
  An accepted cast fires `World.SpellEffect`, by default `SpellEffects.Cast`, which launches the bolt
  or applies the self cast on the server (bolts are not in `EntityList`, so other clients see only their
  consequences, while the caster's own client flies a cosmetic preview) and is rebroadcast as
  `NetPlayerAction.Cast` (8, no protocol bump: the player-list action byte is read unchecked, so an older
  client just shows no gesture). A client runs the same seam to predict only: it
  checks and paces, never spends and never fires the effect, then raises the intent.
- **Hammer** — `IntentReplay.HammerFor` -> `World.HammerFor`: a smithing hammer (the `hammer` tag) in hand, the player's own
  ray landing on the anvil's cell, and the anvil's own hit gap run down (the hammer's pace less
  `BloomeryConfig.HammerSlackSeconds`); the anvil manager counts the hit and, on the last, drops the result and slag.
- **Forge** — `IntentReplay.ForgeFor` -> `SmithingStations.Forge`: a smithing hammer in hand, the player's own ray landing
  on the anvil's cell, the recipe offered for the metal stacked there (`AnvilManager.Offered`: that metal, no more than
  the stack holds) and room for its result; the metal leaves the stack and the result goes into the player's inventory,
  which the snapshot answering the intent carries, and the stack rides the station broadcast. The client predicted the
  same call on its mirror. Stacking metal on an anvil and taking it back are plain `Use` intents (the right-click chain);
  `ServerPlayer.Aim` copies the player's reported sneaking flag into `PlayerContext.RemoteSneaking`, so a crouched
  right-click takes the whole stack back on the server too.
- **Waystone** — `WaystoneSessions.TripFor`: standing at the waystone the player last activated, no trip already waiting,
  then `WaystoneTeleport.TryPay` (discovered, not lost, mana over half unless creative or flying, drained); the player is
  held over the destination until it loads and then landed with a `PlayerCorrection` (see "Waystones on a server").

Every intent is answered, in order: an intent that can edit cells gets a `BlockCorrection` with
the authoritative state of the 3x3x3 cells around its named cell (within edit reach), so whatever
the client predicted there snaps to the server's truth whether it was accepted or not; an intent
whose prediction can change the inventory (place, use, throw), and any refused one, gets that
player's `InventoryState`; then its `IntentResult`. Consequences beyond the corrected cells (a
felled crown, a blast, a flow) reach everyone through the journal. A raw `BlockEdit` from an old
client is read, logged and refused, answered with the named cell's state when it is in reach. A
`PlayerState` whose creative or flight request the server does not grant is answered once per
request with a `PlayerStatus` (`ServerPlayer.ShouldReportModes`), and the client takes those modes
(`Camera.SetModes`). The server also broadcasts every station block entity it runs as a
`StationState` on the entity cadence (and one empty snapshot after the last one goes). It **journals every raw cell
write** (`Chunk.CellWritten` -> `ServerBroadcasts.OnCellWritten`, set in `Start`, cleared in `Stop`) and broadcasts the coalesced
batch as `BlockChanges` each `Pump`, so its own simulation — explosions, fluid flow, leaf decay,
felling, tree growth — reaches clients too. It broadcasts the live mobs, drops, primed TNT and
falling blocks every `NetEntitySyncSeconds` (`EntityList`, with stable ids by object identity). Client input is
validated by `NetValidation`: a `PlayerState` farther than `NetMaxMoveBlocksPerSecond` allows is
clamped, the authoritative position moving toward it by exactly the allowed distance (so a burst
of fast states converges at the cap instead of freezing at a stale point that would get every later
state rejected too); the cap is 130 b/s, the sprint-flight diagonal (`BaseSpeed *
FlyBoostMultiplier` = 80 b/s on each of the horizontal and vertical axes, ~113 b/s together) plus
slack. On top of that clamp the movement rubberband rejects an illegal move (below). The severed
player is removed on disconnect (its context marked absent, so a pour bound to it pauses).

**Streaming around every player.** Chunks load around **every** connected player, not around the
server camera: `World.Focus` (`Chunks/Streaming/StreamingFocus.cs`) publishes an immutable array of focus
chunks — every `ServerPlayer`'s chunk on a server with players online (players sharing a chunk
count once), the camera otherwise (singleplayer, a client, and a server while nobody is online) —
swapped with `Volatile.Write` only when a focus changes chunk or the set changes, so a still frame
allocates nothing. `World.UpdateFocus` refreshes it right before each `ChunkManager.Update(foci)`
(and once in the World constructor, before the generation workers start). `ChunkManager` unloads a
chunk only outside **all** foci's radii (`StreamingFocus.WithinRadius`), orders and drops mesh
uploads and dirty rebuilds by the distance to the nearest focus (`MinDistanceSquared`), and keys
its unload gate on the focus array's reference (plus the 120-frame fallback); an empty focus list
unloads, applies and builds nothing. `ChunkManager.Update(Camera)` stays as a thin overload over
its own single-focus `StreamingFocus` for the tests. `ChunkGenerationWorker` keeps one
`ChunkScanFrontier` per focus index (an array grown by compare-and-swap, since every worker
thread calls it) and round-robins the scans over the foci. `GameServer` no longer moves the
camera (`World.SetServerFocus` remains, for tests): the camera stays at the world spawn, is the
focus only while nobody is online and remains the join spawn. Two far-apart players therefore
cost roughly two load spheres of generation, meshing and memory, sharing the same per-frame
budgets (`MaxDirtyScanPerFrame`, `MaxMeshUploadsPerFrame`), which are not retuned for it. The far LOD stays
camera-bound: the server builds far regions per client request.

**Player status** is server-owned. Each `ServerPlayer` owns a `PlayerHealth` (`Status`, with
`Health`/`IsDead`), a `FallTracker` (`FallTracker.cs`, the GL-free peak/grounded chain lifted out
of singleplayer's `LocalPlayer.UpdateHealth`, which still uses it with `DefaultMaxStepBlocks`), a
`RespawnEye` and a mob-hit sink. The server never simulates a player's body; it probes its own
loaded chunks around the reported position (`Player/PlayerBodyProbe.cs`: `IsKnown` — the feet
cell's chunk and the one `NetGroundProbeBlocks` below are loaded —, `Grounded` — the body lowered
by `NetGroundProbeBlocks` collides, through the static `BodyCollision.BodyCollides` the client's
own collision now delegates to —, `InWater`, mirroring `UpdateWaterState`'s swim test, and
`InLava`, the one lava rule `UpdateWaterState` also calls; its cell test, `BurningFluid.AnyInBox`, is the mobs' too). The fall tracker is fed once per
**accepted** `PlayerState` (`ServerPlayer.ObserveAcceptedState`), with the state gap's allowed
distance (`NetValidation.AllowedDistance(gap, NetMaxMoveBlocksPerSecond)`) as its teleport bound —
exactly what the accepted position already obeys, so only the server's own teleports (join,
respawn, a correction: `Teleport`/`ResetFall`) reset it and lag errs toward no damage, never extra
damage; a body over unloaded chunks is not judged. Lava and the damage flash tick once per `Pump`
(`ServerPlayer.TickStatus` from `PlayerSessions.TickPlayers`), creative or flying being immune as in
singleplayer. Mob hits land through `IMobTarget.TakeHit` (below), which bills the player's own
pool and queues the knockback. Whatever changed the status marks `StatusDirty`, and `TickPlayers`
then sends that player a `PlayerStatus` (modes, health, dead, the queued knockback, handed
over and cleared by `TakePendingKnockback`, the air, the mana, the effect mask and the cave biome at the eye,
`ServerPlayer.EyeCaveBiome`, which marks the status dirty only when it changes; see "Cave biome atmosphere");
one more goes out at join, so a returning player's
health (or death) is mirrored at once. The status is saved with the player (health and respawn
eye in `players/<slug>.json`). A **dead** player stays where it died: its states still record
look, flags and modes but never move it, and every intent but `Respawn` is refused. The client
mirrors all of it: `ClientMirror.ApplyNetworkHealth` (client mode only; lower health is taken as damage
with the flash and a singleplayer-sized shake, higher is restored outright), `UpdateClient`
fading the flash, `Camera.AddKnockback` for the shove (the client's own physics applies it) and
`SetModes`; a client never bills itself damage. Once the status has read dead for
`RespawnPrompt.DelaySeconds` the client's **death screen** comes up (`ClientSession`'s
`RespawnPrompt`, the GL-free rule behind the screen: YOU DIED and a RESPAWN button, Enter works
too), and only when the player chooses RESPAWN does the client send a `Respawn` intent
(`ClientSession.RequestRespawn` -> `SendRespawn`, from its eye and aim), re-sent every
`NetRespawnRetrySeconds` while the status still reads dead; the server judges it before the
eye-drift and rate rules, so a corpse that kept falling on the client can always come back, brings
the player back at full health at its `RespawnEye` (else the join spawn, `ServerPlayer.Respawn`),
snaps the client there with a `PlayerCorrection` and sends the fresh status. Nothing server-side
changed: a dead player simply waits for the intent. Singleplayer works the same way
(`LocalPlayer.RespawnPrompt`: the death plays, then the player stays down until they respawn).

**The movement rubberband.** Every accepted-for-position `PlayerState` goes through
`MovementRules.Judge` (`Networking/ServerSide/MovementRules.cs`, GL-free, verdict `MoveVerdict` Accepted /
Clamped / Rejected, per-player memory in `ServerPlayer.Movement`, a `MovementState`): first the
converging speed clamp above (`Clamped`, never a correction), then, on the bounded point and only
without the flight flag and over loaded chunks (`PlayerBodyProbe.IsKnown`; unknown means accept):
a body that would collide with a solid cell while the previous position did not is rejected (a
player already inside a solid may move, so it can leave), and, when the state gap is at most
`NetMaxJudgedStateGapSeconds`, a climb faster than `NetMaxRiseBlocksPerSecond * gap +
NetRiseSlackBlocks` is rejected, and so is a hover: airborne, not in water and not sinking for
longer than `NetHoverGraceSeconds` in total, and so is a climb past one airtime's rise budget
(`MovementRules.AirRiseBudget`: one jump's apex per jump the player has, the ground jump plus `ServerPlayer.AirJumps` from the
cloud boots, plus `NetAirRiseSlackBlocks`), summed in `MovementState` over the upward steps since the body was last grounded or
in water; the sum is kept on a rejection, so every further rise of that airtime is refused until a landing. Three exemptions:
a descent turning into a rise with ground within reach below the low state (`PlayerBodyProbe.GroundedWithin`, a bunny hop
whose landing fell between two states) starts a fresh airtime; an airtime begun off a bounce block
(`PlayerBodyProbe.BounceWithin`) may climb a bounce's apex more; and a mob hit or blast lift (`ServerPlayer.TakeHit` ->
`MovementState.GrantLift`) widens the budget by its own climb for `NetLiftCreditSeconds`. A correction keeps the sum (being
corrected never refills it), a teleport resets it (`MovementState.Reset`). The numbers are generous on purpose — a jump, a sprint
jump, a sprint auto-step of a whole block inside one state, swimming up and out of water, a mob's
knockback lift, a fall and standing idle never trip them (`MovementRulesTests` asserts the
relations against `PlayerPhysics`' own constants, and that a wearer's one air hop is taken and a second rejected, a bunny hop,
a queued lift and a slime-block bounce are legal; `ServerPlayerJumpTests` that an air hop is never a bellows jump;
`GameServerTests.AWearersDoubleJumpIsNeverCorrectedButASecondAirHopIs` over loopback). Accepted gaps of the air budget: a
non-wearer may hop once after walking off a ledge (the budget is height-based), a hop within one sample step of the ground counts
as a landing, an air jump sent before the server applied the equip intent is corrected once, and iceberg mirror lag can still
mis-judge a rider's bunny hop. A rejection keeps the player where it stood,
resets its fall tracking and sends a numbered `PlayerCorrection(seq, eye)`
(`ServerPlayer.IssueCorrection`); the client snaps there (`Camera.TestTeleport`, motion reset),
echoes the number in every following `PlayerState`, and the server ignores state positions until
one acks the newest correction (`AcceptsStatePosition`), so the states the client sent before it
snapped can never trip a second correction. A respawn uses the same correction. The hover rule
(and the fall tracker's grounded probe) also counts the server's iceberg boxes as ground
(`World.BodySolids`, an `IBodySolids` passed to `MovementRules.Judge` and
`ServerPlayer.ObserveAcceptedState`), so a rider is never a hoverer; the endpoint-collision rule
stays block-only, since a client stands on a mirror up to a snapshot behind the server's berg.

**Creative permission** is per player: a client's creative bit is only a request, granted (with
flight only alongside it, and flight is noclip to the movement rules) while
`GameServer.MayBeCreative(name)` holds — everyone when `AllowCreative` (`--allow-creative`) is on,
otherwise only an operator. `OperatorList` (`Networking/ServerSide/OperatorList.cs`, GL-free) holds the
operators by save slug (`WorldNames.Slug`, the same normalization that names a player's file, so
a name matches exactly when it would load the same save file — case and punctuation fold away —
and a duplicate joiner's "Name 2" is not an operator); `ServerProgram` builds it from every repeatable `--op <name>` plus
`ops.json` in the hosted world's folder (a JSON array of names, read once at start through
`ChunkSaveStore.ReadFile`; no file means nobody, a broken one warns and means nobody), and nobody
is an operator by default. Both enforcement points use it — the restored modes at hello and every
`PlayerState` — creative grants and clears follow `ServerPlayer.Creative`, and a refused request
is answered once with a `PlayerStatus` carrying the modes the player really has.

**Mobs follow real players.** A mob chases and hurts an `IMobTarget` (`IMobTarget.cs`: `Eye`,
`IsImmune`, `TakeHit(damage, away, speed, lift)`), never the camera directly. `MobDirector.Update`
(World's mob component) rebuilds the target list each frame right before it runs the slime and goblin managers: singleplayer's is the
`Camera` (immune while creative or flying; its `TakeHit` applies only the knockback, since World
still bills the managers' `DamageDealtThisFrame` to its own pool, so singleplayer is unchanged),
and a server's is every `ServerPlayer` (immune while creative, flying or dead; `TakeHit` bills its
own pool and queues the knockback for its status), so a server with nobody online chases, spawns
and despawns nothing. `MobTargets` (GL-free) holds the rules: each mob chases the horizontally
nearest non-immune target (the nearest immune one only when every target is), contact and stones
hit whichever non-immune target they touch (a stone stops at any target) and a goblin's melee
impact every non-immune target still in reach, a mob despawns only when it is past its despawn radius from **every** target, and the one shared
spawn timer tries its ring around each target in turn (round-robin, `_spawnCursor`) under the
unchanged global caps. `Goblin.Update` takes the target, and a null one means no chase.

`GameServerTests` (loopback) and `ServerWorldTests` lock the frame, spawn, LOD-queue,
hosted-player, per-player inventory (independent inventories, refused equips corrected, creative
gating and its status report, crafting spends only the crafter's), nearest-player pickup and
intent rules down (a tool-less break refused and corrected, a break faster than its hits refused,
an unowned placement refused, a placement spending exactly one under the placement rules, a
forged overwrite refused, the server-computed state byte, a blast one player primed reaching
another client, its primed cube in the other client's `EntityList` until the blast, mined loot
landing with the breaker only, an intent from a far eye or a replayed
id refused, no placement into another connected player's body), the status rules (a joiner told
its health, death held until a respawn intent from a drifted eye brings the player home), the
rubberband (an illegal move corrected and ignored until acked, a legal walk never corrected), the
operator permission, per-player streaming (a moving player becomes a focus and the camera stays
put; two players 500 blocks apart both stream terrain and edit beside themselves, and a departed
player's chunks unload), with `InventoryIntentTests`,
`ServerPlayerTests`, `DropPickupTests`, `WorldIntentRuleTests` (codec, `BreakProgress`, edit rate,
visible face, the respawn intent), `ClientPredictionTests`, `StationNetCodecTests`,
`NetworkProtocolTests` (the status and correction payloads), `FallTrackerTests`,
`ServerPlayerStatusTests`, `MovementRulesTests`, `OperatorListTests`, `MobTargetTests`,
`StreamingFocusTests`, `ChunkManagerTests.UnloadKeepsAChunkInsideAnyFocusRadius` and
`ChunkGenerationWorkerTests.TwoFociAreBothServed` for the GL-free rules.

**The client** (`OpenGLTest/src/Networking/Client/NetworkClient.cs`) connects on a background thread from
`ClientSession.BeginConnect` (the CONNECT click records `PendingWork.Connect` and presents the
"Connecting..." loading screen, like PLAY), handshakes, then a reader thread queues frames. The
handshake read is bounded by `NetConnectTimeoutSeconds` (cleared before play), the socket is
disposed on every failure path, the connect timeout included, and `Status`/`Server`/`Error` are
published through volatile fields; `Handshaken` is set before the reader starts and the reader's
final `Disconnected` is unconditional, so a server that closes at once can never leave
`Handshaken` standing. `ClientSession.UpdateConnecting` aborts to the server list on `Failed` **or**
`Disconnected`, waits for `NetworkStatus.Handshaken`, then builds a **client-mode** `World`
(`clientMode: true`, `spawn: server.Spawn`, so the camera starts at the server's spawn — a client
camera never runs `SpawnOverrides.SpawnPosition`'s surface search, and stands at
`Camera.PlaceholderSpawn` when no spawn is given) with the renderer's pools and the window — no
worldgen, no LOD threads and no local simulation — and plays. The client derives nothing from the
seed: terrain, water levels and the biome tint all stream from the server, so it never calls
`WorldSeed.Apply` and a connect is never refused by the seed lock (the client world still calls
`WorldSeed.BeginWorld`, so a client teardown that times out keeps the lock like a singleplayer
one). A `ClientChunkStreamer` requests the chunks around the camera and, with every chunk it asks
for, the 3x3 chunk columns around it (`ChunkData`/`ColumnTerrain`; a chunk at the load sphere's rim
needs diagonal columns no chunk inside it would ask for). It is the client's `IColumnSource`
(installed as `ChunkManager.ColumnSource`, a `ConcurrentDictionary` because mesh builds on the
thread pool read it): a column already received is never asked again, an unanswered one is re-sent
after `ResendSeconds` like a chunk (a column is a hard dependency), and an arriving column
re-dirties the loaded chunks of the 3x3 chunk columns around it unless it equals the stored one in
heights, water levels and climate (`ColumnTerrain.ContentEquals`). A client chunk never meshes
until its nine columns are in (the dirty gate in the threading rules; all-air chunks exempt), and
the eye's water surface (`ChunkManager.GetFluidColumnTopGlobal`, `Camera.EyeWaterSurfaceY`) reads
the streamed water level too. The far LOD is the server's: a `ClientLodStreamer` requests far
regions (`LodRegionData`, at most `NetLodInFlightMax` outstanding, an unanswered one re-sent only
after `NetLodResendSeconds`), whose `LodNetCodec` payload is the server's finished vertices with
the tint and the canopy bumps already baked, so the client's `LodManager` never reads `ColumnCache`
or `TreeGenerator`; for the same reason `BlockEditor.NotifyLodEdit` and the inline
`TreeGenerator.PoisonCanopy`/`LodManager.MarkEditedColumn` sites in `RemoveBlock` and `FellWood`
return early in client mode, so a client edit never touches `LodSurface`, `LodColumnCaps`
or `TreeCanopy.CanopyDisturbed`. `WorldgenReads` (off in production) is the counter
`ClientWorldgenIsolationTests` turns on to prove a client world under a poisoned seed streams and
meshes with no worldgen call and edits without moving the far-LOD tables, and
`StreamedTerrainMeshTests` that a mesh built from streamed columns is byte-identical to the
worldgen one (a tinted surface and a submerged floor).
`ClientMirror.ApplyNetworkEntities`/`ApplyNetworkBlockChanges`/`Inventory.Import` apply the server's
entities, cell changes and inventory mirror. Only the mob kinds (`NetEntity.IsMob`) enter the mob
reconcile; primed TNT and falling blocks become **display-only mirrors**, and only in client mode
(`ApplyNetworkMirrors`, so a singleplayer world can never acquire a cube its `Update` would tick
and detonate): a primed TNT is reconciled by id (`PrimedTnt.Mirror` / `MirrorSetState`, so its
strobe clock survives snapshots, the fuse clamped so a bad pair cannot divide the flash by zero),
falling cubes are rebuilt wholesale (`FallingBlockManager.ReplaceMirrors`), and `WorldRenderer.Render`
draws both through the usual `_tntManager.Primed`/`_falling.Falling` lists. A client never calls
`TntManager.Update` or `FallingBlockManager.Update`, so a mirror never moves, lands or detonates
(the cells it leaves arrive as `BlockChanges`); `UpdateClient` only burns the TNT fuse down
locally (`TntManager.AnimateMirrors`) so the flash keeps its pace between snapshots, and a
mirrored falling cube still blocks the client's placement prediction (`OverlapsAny`).
`NetworkEntitySyncTests.PrimedTntAndFallingBlocksMirrorFromASnapshot` locks the mirrors down and
`GameServerTests.APrimedTntReachesTheClientsEntityList` the broadcast.
`ApplyNetworkBlockChanges` writes raw, then rebuilds
each touched chunk once with the light-radius `Chunk.RemeshAfterBatchEdit` (so a remote lamp or
roof edit relights the edge and corner chunks) and registers an arriving quern
(`QuernManager.Ensure`). `ChunkManager.ApplyStreamedChunk` does not queue streamed chunks on the
worldgen water-wake queue, and a client world disables `FluidManager` and `LeafDecayManager`
scheduling (`Enabled = false`), since it never ticks either. **The client predicts, then the
server answers.** Every action runs locally first as a prediction and raises
`World.IntentRaised` (`ClientSession.SendWorldIntent`): mining raises `BreakStart` when it starts a
target (or the held item changes) and `BreakComplete` when its own hits reach the hardness (a
creative client breaks once per `ClientCreativeBreakSeconds` rather than per frame, to stay
inside the edit rate); a claimed right-click raises `Place` (the hit cell and face its placement
used, `BlockPlacer.LastPlaceHit`) or `Use`; a released throw raises `Throw`; a landed punch or slice
raises `Attack`. Intents carry `Camera.Position` and the head ray the camera aimed along
(`Camera.LastAimDirection`), so the server's first-person ray lands on the same cell. Every cell
the prediction changed is reported first through `World.BlockEdited` — mining (`BlockEditor.RemoveBlock`, plus
the cells a multiblock revert gives back), placement, the `WorldItemUse` bucket wrappers
`ScoopFluidCell`/`PourFluidCell`, the stack writers `SetPileCount`/`SetPileComposition`, every
cell of a `BlockEditor.TryConvertMultiblock` and `RemoveCellForPrime` — and staged in `ClientPrediction`,
which tags them (and, for place/use/throw and every inventory intent, the inventory) with the
intent's sequence id. A server cell value for a predicted cell — a `BlockChanges` broadcast, or a
`BlockCorrection` answering an older intent — is held back until that prediction's
`IntentResult`, then applied (the newest one per cell); a correction answering the newest
prediction applies at once; and an `InventoryState` older than the newest inventory-predicting
intent is dropped, since the answer to that intent follows and includes it. A client fells
nothing, topples nothing, primes no TNT, rolls no loot, lets no station hand anything back and
damages no mob itself (`BlockEditor.RemoveBlock`, `BlockPlacer.PlaceAgainstFace`, `WorldItemUse`'s
`AfterPlaced` and `SpawnPrimedTnt`, `LeftClickAction.UpdatePunch`/`UpdateSlice` and
`ThrowAction.UpdateThrown` are guarded by `_clientMode`):
the server's broadcasts carry every consequence, its mobs' health arrives in `EntityList`, mined
loot arrives as drops it picks up into the player's server inventory, and a throw flies locally
only as a cosmetic copy. The purely cosmetic leaf particles and mining debris, the player model,
the station mirrors' quern animation and fire particles, the mirrored TNT fuses and the cosmetic throws tick in
`World.UpdateClient` so nothing hangs in mid-air. `UpdateNetwork` sends `PlayerState` every
`NetPlayerStateSeconds`, carrying the newest correction it applied (`netCorrectionAck`); chat
feeds `ChatView`, `TimeSync` calls `World.SetLightingTime`,
`PlayerList` reconciles `ClientMirror.SyncRemotePlayers`, `StationState` replaces the station mirrors
(`ClientMirror.ApplyNetworkStations`), `PlayerStatus` sets the camera's modes and mirrors health, death
and knockback (and starts the death screen's clock, see "Player status"), `PlayerCorrection` snaps
the camera and makes the next state go out at once with its ack, and `InventoryState` is
imported as the truth when it is current.
Inventory edits are predicted then sent (`ClientSession.SendInventoryIntent`): the hotbar keys and `Q`
are intents only, while the wheel (`GameplayInput.MirrorSelection`), a middle-click pick
(`BlockPlacer.TryPickTargetedBlock(inventory, out id)` -> `PickBlock`), an [E]-panel hotbar equip, a
craft (the fed members read before `CraftingSession.Craft` spends the reservation) and a creative
catalog grant or clear apply locally at once and send their intent, so the server's answer keeps
them or snaps them back. Each server message is handled inside a
try/catch, and a malformed one (`EndOfStreamException`, `IOException`, `InvalidDataException`,
`FormatException`, `ArgumentException`) leaves the server with "The server sent bad data" instead
of crashing the game. Remote players are `RemotePlayer`s whose
`PlayerModel` walk cycle runs off the distance they moved; `World` updates them each frame and
`PlayerPasses.RenderRemotePlayers` draws each as the player body at its broadcast eye. A lost
connection, or SAVE & QUIT while connected, disposes the client world and returns to the
multiplayer page.

**Chat** is universal: `ChatView`/`ChatLayout` (`OpenGLTest/src/UI/Hud/Chat.cs`, GL-free, tested) hold
a 100-line log, a 200-char input, arrow-key history and the open flag. `T` opens it from a clean
play state, `Enter` sends, `Escape` closes; while it is open the keyboard is withheld from the
camera exactly like a focused inventory field, and the log sits bottom-left above the hotbar
(`ChatRenderer.DrawChat`, its own `UiPasses.RenderChat` UI pass). In singleplayer `ClientSession.
SendChat` echoes locally; in multiplayer it sends `ChatSend` and the server's broadcast is the
only copy, so a line never appears twice. A line may carry a link (`ChatLink`: a span of its text and a
command, kept only when it fits the text): the GL-free `ChatLogLayout` lays the log out (which lines show, how
each wraps through `TextWrap.WrapRanges`, where a link lies on every line it spans) for both the draw, which
underlines link runs in `UiTheme.LinkBlue`, and the click hit-test (`ChatRenderer.LinkCommandAt`, rebuilt from
the current view), so the two cannot drift. While the chat is open the cursor is free and the look held
(`GameplayInput.SyncChatCursor`, on the open/close edge; a close recaptures only when no death screen, panel or
pause menu owns the cursor), a press in the input places its caret (`FieldPointer`) and a click on a link puts
its command into the input without sending it (`ChatView.Suggest`). `/locate` reads worldgen
(`ChatCommands.Locate` over `StructureLocator`), so singleplayer answers it locally while a connected client's
line runs on the server (`ServerChat`), from that player's eye, since a client holds no seed.

**Known gaps** (open): the airtime rise budget is height-based, so a player without the cloud boots may hop once after
walking off a ledge, a hop within one state step of the ground counts as a landing, a client that double jumps before the
server applied its equip intent is corrected once, and iceberg mirror lag can still mis-judge a rider's bunny hop; players are
keyed by name with no authentication, so whoever joins first
under a name gets that name's inventory, status and operator rights (`ops.json` is read once at
start, and there is no in-game op command); a slice's return-swing flag and a throw's charge are
taken from the client (both are paced, neither verified); a timed use (`use.seconds`) is held
locally and the completed `Use` intent is taken on trust, so a modified client can fire one
instantly; a third-person placement may land on a
face other than the one the client predicted (any face of the hit cell turned toward the eye is
accepted) and is then corrected; a correction covers the 3x3x3 cells around the named cell, so a
prediction outside it waits for the journal; projectiles and toppling trees are not in
`EntityList` (their cells still sync via `BlockChanges`, and a client never fakes them locally, so
a felled tree shows as cells changing rather than as a toppling entity; a client's own throw is
drawn as a cosmetic copy only); the primed TNT and falling-block mirrors update at the entity
cadence without interpolation (a falling cube steps at 10 Hz; only the TNT flash animates
locally) and never physically shove the client's player (`Camera.PushOutOf` runs only in the full
`Update`); station mirrors update at the entity cadence (10 Hz); every player is named "Player",
because `ChatView.LocalName` is never set from a setting; and there is no authentication or encryption. From the worldgen-free client: the
pebble, branch-pile and snow-layer emitters still read seed-keyed worldgen per cell for an
untouched cell (state byte 0: the `PebbleGenerator`/`BranchPileGenerator` position hashes and
noise keyed on `WorldGenerationConfig.Seed`, and `SnowShape.CountWithState` -> `SnowGenerator.DepthAt`, which
sets the drawn depth of a snow cell; collision reads the state byte alone), and since a client no longer applies the server's seed
it draws those with its own process seed (`WorldSeed.Default`, or the last singleplayer world's),
so untouched pebbles, branch piles and snow depths can differ from the server's
(`ClientWorldgenIsolationTests` picks chunks without them, and `WorldgenReads` does not count
those reads); and `ServerHello` still carries the seed although no client uses it. From the
server-owned status and movement:
a hovering cheater is frozen in place by repeated corrections rather than dropped to the ground;
collision is judged at a state's endpoint only, so one state may pass through a thin wall within
its speed allowance, a body already inside a solid may move anywhere, and a descent slower than
the hover rule's sink tolerance is never caught; knockback is applied by the client's own physics
(the server only queues it in the status), so a modified client can ignore it; a dead player
waits on the death screen and its model stays where it died for others until the respawn; a player's lava and fall state is judged from its 20 Hz positions, so a long fall
across a lag gap can go unbilled (lag errs toward no damage, never extra); two far-apart players
cost roughly two load spheres of generation and memory under the one set of per-frame budgets;
and while a store-backed server world is still loading, an early joiner is already the streaming
focus but `WorldLoadGate` still measures around the server camera, so a joiner who walks off
before the spawn settles makes the load fall back to its timeout. From the world sources: the
game's main menu ignores `world.json`'s `source`, so an `imported` world opened in singleplayer
regenerates (its saved chunks are restored into generated terrain); worldgen features in a
generated chunk beside a supplied column (trees, caves, pebbles, flowers) are computed from
worldgen's own heights, never the source's — **including** trees whose placement roots inside the
map's footprint but whose cells land in the generated chunk, which `TreeGenerator.TryGetCanopy`
also draws as far-LOD canopy bumps on the generated cells beside the map — so a map should be
padded by `TreeGenerator.ReachChunkRadius` chunks on every side or cover whole 64x64 tree cells;
an imported pebble or snow cell whose state byte is 0 takes its count (and a pebble its loot)
from worldgen's per-cell hash and noise like any untouched cell, so a map that means a specific
count should store it in the state byte; a supplied column's far LOD approximates snow, sea ice, beaches and bare rock
from its biome and dirt depth (its sea ice follows the stored Frozen biome, so it shows no ragged
pack-ice band) and draws no canopy bumps or gravel caps; the fluid wake treats
every imported chunk as restored (its border faces are always scanned at load), so a map whose
water disagrees with its columns costs wake work, never a crash; `ImportedMapSource` loads every
column file at construction, so a map of hundreds of thousands of columns costs hundreds of MB
and a long synchronous start (a lazy per-column load is the follow-up); and the server camera,
which `SpawnOverrides.SpawnPosition` still places on the generated origin surface, can start inside or
under the map's terrain until the spawn settle lifts it. From the status effects (MG-4b): other
players' effects are not shown (the `PlayerList` carries no mask), a client's slowdown is applied by
its own physics and is not judged (the rubberband only caps speed from above), and the server's attack
pacing checks a minimum gap only, so a slowed client stays legal but an unslowed one is not caught. From
`/locate`: it reads worldgen only, so on a server whose world source is an imported map it reports the generated
tower sites, which the map may not hold; its teleport lands on the generated heightmap ground beside the tower,
where a tree, a boulder or a ruin wall may stand; and a cold search builds the site verdicts of the candidates nearer
than the answer synchronously on the server's main thread, which can take a noticeable moment where no tower is near.
