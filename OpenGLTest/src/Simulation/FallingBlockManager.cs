using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Owns gravity-affected blocks: topples a <c>falls</c> block into a <see cref="FallingBlock"/>
/// entity when its support goes away, ticks the live cubes, and writes rested ones back as
/// blocks (or drops them as items when the landing cell is taken). Main thread only.
/// </summary>
public class FallingBlockManager
{
    private readonly List<FallingBlock> _falling = [];
    private readonly List<FallingBlockLanding> _landings = [];

    public IReadOnlyList<FallingBlock> Falling => _falling;

    /// <summary>
    /// Every cube the last <see cref="Update"/> wrote back as a block, with how far it fell and whether
    /// it settled under water; valid until the next one. The world raises the settling dust from it
    /// (<see cref="GroundDust.LandBlock"/>), except on a headless server.
    /// </summary>
    public IReadOnlyList<FallingBlockLanding> Landings => _landings;

    /// <summary>Whether any live cube's body touches the world cell (x, y, z).</summary>
    public bool OverlapsAny(int x, int y, int z)
    {
        foreach (var block in _falling)
            if (block.OverlapsCell(x, y, z))
                return true;
        return false;
    }

    /// <summary>
    /// Starts a cube at the centre of the cell the block occupied. The cube carries
    /// <paramref name="blockId"/> so it renders as the block that toppled. Returns the cube
    /// so <see cref="TryTopple"/> can flag chain collapses.
    /// </summary>
    public FallingBlock Spawn(Vector3 blockPosition, byte blockId)
    {
        var block = new FallingBlock(blockPosition + new Vector3(0.5f), blockId);
        _falling.Add(block);
        return block;
    }

    /// <summary>
    /// A multiplayer client replaces its cubes with the server's, each at the exact centre the
    /// server reported. Only a client calls this, and it never calls <see cref="Update"/>, so a
    /// mirror never moves or lands here; it is drawn, and it still blocks a placement into its cell
    /// (<see cref="OverlapsAny"/>).
    /// </summary>
    internal void ReplaceMirrors(IReadOnlyList<(Vector3 Centre, byte BlockId)> cubes)
    {
        _falling.Clear();
        for (var i = 0; i < cubes.Count; i++)
            _falling.Add(new FallingBlock(cubes[i].Centre, cubes[i].BlockId));
    }

    /// <summary>
    /// Moves every live cube and writes the rested ones back as blocks. <paramref name="lod"/>
    /// receives the surface change of each landing (see <see cref="LodSurface.RecordEdit"/>);
    /// null records nothing, which the headless tests use.
    /// </summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops, FluidManager fluids,
        LodManager? lod = null)
    {
        _landings.Clear();
        // Ascending, because a toppled column is stored bottom-up: the lowest cube is written
        // back as a block before the one above it moves for this frame, so each cube lands on
        // a real support. Iterating top-first let every cube fall a frame into the cell below,
        // which sank a tall column until the top cube popped out as a drop.
        for (var i = 0; i < _falling.Count; i++)
        {
            var block = _falling[i];
            block.Update(deltaTime, chunks);

            // Wedged somewhere physics cannot free it from: break it into its usual drops
            // rather than hovering forever
            if (!block.IsSettled && block.IsStuck)
            {
                _falling.RemoveAt(i);
                var cell = block.Position.Floor();
                drops.SpawnLoot(block.BlockId, cell);
                i--; // the cube after this one shifted down into its slot
                continue;
            }

            if (!block.IsSettled) continue;

            _falling.RemoveAt(i);
            Settle(block, chunks, drops, fluids, lod);
            i--; // the cube after this one shifted down into its slot
        }
    }

    /// <summary>
    /// Topples the <c>falls</c> block at (x, y, z) when the cell below it is loaded and not
    /// solid — fluids never support, so sand sinks through water. Unloaded cells read as air,
    /// so anything touching unloaded space is left alone rather than poured into the void.
    /// Returns whether a cube was spawned. The cells above are toppled bottom-up in the same
    /// batch, so a whole column comes down one cube at a time; when the batch holds more than
    /// one cube every member is flagged <see cref="FallingBlock.ChainCollapse"/>, so the
    /// column stacks on sand and rebuilds instead of avalanching. A lone grain keeps the
    /// default and slides.
    /// </summary>
    public bool TryTopple(ChunkManager chunks, LodManager lod, FluidManager fluids, int x, int y, int z)
    {
        if (!chunks.TryGetCell(x, y, z, out var id, out _) || !BlockType.IsFalling(id))
            return false;
        if (!chunks.TryGetCell(x, y - 1, z, out var below, out _) || BlockType.IsSolid(below))
            return false;

        List<FallingBlock>? batch = null;
        var cy = y;
        while (true)
        {
            if (!chunks.TryGetCell(x, cy, z, out var curId, out _) || !BlockType.IsFalling(curId))
                break;
            // Below the base this repeats the entry check; above it the cell was just freed
            // by the iteration below, which is what lets the column come down together.
            // A level that is still supported stays put and caps the batch.
            if (!chunks.TryGetCell(x, cy - 1, z, out var curBelow, out _) || BlockType.IsSolid(curBelow))
                break;

            var cell = new Vector3(x, cy, z);
            if (!chunks.Chunks.TryGetValue(x >> Chunk.SizeShift, cy >> Chunk.SizeShift, z >> Chunk.SizeShift,
                    out var chunk)) break;
            if (!chunk.RemoveBlockAt(cell, chunks)) break;

            batch ??= [];
            batch.Add(Spawn(cell, curId));

            // Water beside or above the freed cell pours into it on the next fluid tick
            fluids.ScheduleAround(cell);

            cy++;
        }

        if (batch == null) return false;

        // The removals change the far LOD like any mined block, recorded once the whole run is
        // clear: the highest cell first, which rescans down through the emptied run when it was
        // the column's top, then the lowest, which caps the column when the run sat below the
        // top. Recording bottom-up while cells still stood above would cap a column whose top
        // simply fell away.
        var lodChanged = LodSurface.RecordEdit(chunks, x, cy - 1, z);
        if (cy - 1 > y)
            lodChanged |= LodSurface.RecordEdit(chunks, x, y, z);
        if (lodChanged)
            lod.MarkEditedColumn(x, z);

        if (batch.Count > 1)
            foreach (var block in batch)
                block.ChainCollapse = true;
        return true;
    }

    /// <summary>
    /// Topples bottom-up over a column span, so a blast or felling that took out several
    /// blocks drops every unsupported <c>falls</c> block above the hole.
    /// </summary>
    public void CheckColumn(ChunkManager chunks, LodManager lod, FluidManager fluids,
        int x, int z, int yMin, int yMax)
    {
        for (var y = yMin; y <= yMax; y++)
            TryTopple(chunks, lod, fluids, x, y, z);
    }

    /// <summary>
    /// Writes a rested cube back as a block, displacing water like a placement; when the cell
    /// is taken (a second cube won it) or unloaded, the block pops out as an item drop so
    /// nothing is ever lost or duplicated. A cube resting half off a corner can report a
    /// floored cell with air underneath — that is dropped too rather than cemented floating.
    /// </summary>
    private void Settle(FallingBlock block, ChunkManager chunks, DropManager drops, FluidManager fluids,
        LodManager? lod)
    {
        var x = (int)System.MathF.Floor(block.Position.X);
        var y = (int)System.MathF.Floor(block.Position.Y);
        var z = (int)System.MathF.Floor(block.Position.Z);
        var cell = new Vector3(x, y, z);

        var chunk = chunks.ChunkAt(x, y, z);
        if (chunk == null)
        {
            drops.SpawnAt(block.BlockId, block.Position);
            return;
        }

        if (!chunks.TryGetCell(x, y - 1, z, out var below, out _) || !BlockType.IsSolid(below))
        {
            drops.Spawn(block.BlockId, cell);
            return;
        }

        if (chunk.PlaceBlockAt(cell, block.BlockId, chunks))
        {
            _landings.Add(new FallingBlockLanding(cell, block.BlockId, block.FallBlocks, block.IsSubmerged));
            fluids.ScheduleAround(cell);

            // A landing cube can raise or re-tile the far LOD's surface like a placement; only a
            // surface-solid block is recorded, since anything else would read as a sub-surface
            // removal and cap the column
            if (lod != null && LodSurface.IsSurfaceSolid(block.BlockId)
                && LodSurface.RecordEdit(chunks, x, y, z))
                lod.MarkEditedColumn(x, z);
        }
        else
            drops.Spawn(block.BlockId, cell);
    }
}
