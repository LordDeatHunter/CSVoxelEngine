# Saves and multiplayer

## Saves

A world lives in `%APPDATA%/CSVoxelEngine/saves/<slug>/`:

| File | Contents |
|---|---|
| `world.json` | Name, seed, timestamps, format version, optional `source` |
| `chunks/<cx>_<cy>_<cz>.bin` | One compressed, block-name-paletted snapshot per **modified** chunk |
| `lod-edits.bin` | Far-LOD column caps, canopy markers and surface overrides |
| `wood-damage.bin` | Permanent chop damage of natural trunk cells |
| `player.json` | Pose, modes, health, mana, inventory, known spell parts |
| `entities.json` | Live mobs, item drops and icebergs |
| `stations.json` | Campfires, querns, kilns and cast molds |

An untouched chunk is never written and simply regenerates. Writes are asynchronous: the main thread copies arrays, and one background thread builds the palette, compresses and writes a temp file that is then moved into place. A pending map guarantees a lookup always finds a chunk either in memory or on disk. A file that cannot be read is moved to `.bad` instead of being overwritten. A world folder is held by `session.lock`, so a second session cannot open it.

## Launch flow

The game boots to a main menu (worlds list with PLAY, EDIT, DELETE, CREATE; multiplayer; settings). PLAY and CREATE never block in the click: the work is deferred to the next update behind a loading screen, and the world streams in during a loading phase before it is ever rendered. SAVE & QUIT presents a saving frame and tears the world down on the next update.

## Multiplayer

A dedicated server (`OpenGLTest.Server`) loads content headlessly and runs a server-mode `World`. The wire protocol is length-prefixed frames over TCP; a protocol version in both hellos must match.

- **The client predicts, the server answers.** A client never writes a cell: every world action is an intent with a sequence id that the server replays through the singleplayer rules. Predictions are staged and then corrected by the authoritative cells.
- **Server-owned state**: each player's inventory, health, mana and status are owned by the server and mirrored to clients.
- **Validation**: movement is clamped to a speed cap, illegal moves are rubberbanded with numbered corrections, and edit, attack and cast rates are limited.
- **Streaming**: chunks load around every connected player. The far LOD is built by the server per request and streamed as finished vertices.
- **Creative** is a per-player permission, off by default.
- Known gaps include no authentication or encryption, and projectiles and toppling trees not being mirrored.

## World sources

Where a world's terrain comes from is one seam, `IWorldSource`: the default generates from the seed, and an imported source supplies pre-built columns and chunks from a map folder with worldgen as the fallback outside it.
