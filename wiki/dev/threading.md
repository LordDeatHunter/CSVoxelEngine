# Threading model

Chunk work is split across threads, and **OpenGL calls are only legal on the main thread**.

1. **Generation workers** (`ChunkGenerationWorker`) scan positions around each streaming focus, closest first, and generate chunk block data on the background thread.
2. **Meshing** is scheduled from the main thread (`ChunkMeshScheduler`) but runs as thread-pool work items. It produces CPU-side mesh data only.
3. **Upload** of completed meshes happens on the main thread, sorted closest-first and capped per frame.

## Rules that follow

- Never call GL (buffer uploads, `Mesh` creation or disposal, pool uploads, shader use) off the main thread. Background work only produces plain data handed over through concurrent queues.
- A chunk meshes only when its block data is ready **and all six face neighbors exist with ready data**; meshing re-resolves the neighborhood and returns `null` when a neighbor vanished, and the caller re-marks the chunk dirty.
- Shared state uses `ConcurrentDictionary` and `ConcurrentQueue`. `ChunkManager.Chunks` is a wrapper over a `ConcurrentDictionary`, never a `Dictionary` behind a lock.
- Editing a block on a chunk border must also remesh the neighbor chunk(s).
- Per-frame budgets and thread counts are constants in `Config/`, never hardcoded elsewhere.
- Worldgen must be deterministic and thread-safe: noise instances are configured once in static constructors.

## Block-edit hooks

Every path that writes or removes blocks must call the shared hooks: the fluid schedule, leaf decay, gravity (`TryTopple` above), the far LOD and canopy invalidation, saves, and networking. Read the edit-path checklist before adding any such path.

## Main-thread simulations

The fluid tick, leaf decay, falling blocks, TNT, stations (campfire, quern, kiln, casting), mob managers, drops and tree growth are all owned by `World` and are **main-thread only**.

## The per-block state byte

Each chunk has a lazily allocated state byte per block. Its meaning is decided entirely by the block that owns the cell: a horizontal facing, a fluid level, a stack count, a multiblock's facing and offset, tree wood thickness and parent direction, leaf hydration, or an openable's open bit. Every write that is not one of those zeroes it, so a stale value never outlives its block.
