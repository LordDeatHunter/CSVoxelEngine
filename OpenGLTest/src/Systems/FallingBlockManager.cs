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

    public IReadOnlyList<FallingBlock> Falling => _falling;

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
    /// <paramref name="blockId"/> so it renders as the block that toppled.
    /// </summary>
    public void Spawn(Vector3 blockPosition, byte blockId)
    {
        _falling.Add(new FallingBlock(blockPosition + new Vector3(0.5f), blockId));
    }

    public void Update(float deltaTime, ChunkManager chunks, DropManager drops, FluidManager fluids)
    {
        for (var i = _falling.Count - 1; i >= 0; i--)
        {
            var block = _falling[i];
            block.Update(deltaTime, chunks);

            // Wedged somewhere physics cannot free it from: break it into its usual drops
            // rather than hovering forever
            if (!block.IsSettled && block.IsStuck)
            {
                _falling.RemoveAt(i);
                var cell = new Vector3(
                    MathF.Floor(block.Position.X),
                    MathF.Floor(block.Position.Y),
                    MathF.Floor(block.Position.Z));
                drops.SpawnLoot(block.BlockId, cell);
                continue;
            }

            if (!block.IsSettled) continue;

            _falling.RemoveAt(i);
            Settle(block, chunks, drops, fluids);
        }
    }

    /// <summary>
    /// Topples the <c>falls</c> block at (x, y, z) when the cell below it is loaded and not
    /// solid — fluids never support, so sand sinks through water. Unloaded cells read as air,
    /// so anything touching unloaded space is left alone rather than poured into the void.
    /// Returns whether a cube was spawned. The cell above is checked recursively, so a whole
    /// column comes down one cube at a time.
    /// </summary>
    public bool TryTopple(ChunkManager chunks, LodManager lod, FluidManager fluids, int x, int y, int z)
    {
        if (!chunks.TryGetCell(x, y, z, out var id, out _) || !BlockType.IsFalling(id))
            return false;
        if (!chunks.TryGetCell(x, y - 1, z, out var below, out _) || BlockType.IsSolid(below))
            return false;

        var cell = new Vector3(x, y, z);
        var chunkPos = new Vector3(x >> Chunk.SizeShift, y >> Chunk.SizeShift, z >> Chunk.SizeShift);
        if (!chunks.Chunks.TryGetValue(chunkPos, out var chunk)) return false;
        if (!chunk.RemoveBlockAt(cell, chunks)) return false;

        Spawn(cell, id);

        // Water beside or above the freed cell pours into it on the next fluid tick
        fluids.ScheduleAround(cell);

        // The removal exposes a hole like any mined block
        if (LodRegion.LowerColumnBelow(x, y, z))
            lod.MarkEditedColumn(x, z);

        TryTopple(chunks, lod, fluids, x, y + 1, z);
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
    private void Settle(FallingBlock block, ChunkManager chunks, DropManager drops, FluidManager fluids)
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
            fluids.ScheduleAround(cell);
        else
            drops.Spawn(block.BlockId, cell);
    }
}
