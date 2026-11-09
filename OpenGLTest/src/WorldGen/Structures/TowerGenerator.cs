using System;
using System.Collections.Generic;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Structures;

namespace VoxelEngine.WorldGen.Structures;

/// <summary>
/// The battle tower's cells: the procedural shell (<see cref="TowerShell"/>) dressed by each floor's and the roof's template.
/// One cell function (<see cref="Merge"/>) feeds both the chunk stamp (<see cref="StampSite"/>) and the point query
/// (<see cref="CellAt(int, int, int)"/>), so the two agree across every chunk seam by construction. A template cell naming the
/// plain tower brick is swapped for the site's palette brick, and a tower chest cell carries its floor's tier in its state
/// (<see cref="ChestProfile.TierState"/> OR'd onto its facing; the roof's chest <see cref="TowerSite.TreasureTier"/>). The roof's
/// template leaves its center cell free (the structure loader's rule) and the generator fills it with the altar block at
/// <see cref="TowerSite.AltarCell"/>. GL-free and pure.
/// </summary>
public static class TowerGenerator
{
    /// <summary>The tower cell at world (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>): -1 when no tower
    /// writes it, else <c>block | state &lt;&lt; 8</c>, air included.</summary>
    public static int CellAt(int x, int y, int z) => TowerPlacement.TryFindAt(x, z, out var site) ? CellAt(site, x, y, z) : -1;

    /// <summary>The cell <paramref name="site"/> writes at world (<paramref name="x"/>, <paramref name="y"/>,
    /// <paramref name="z"/>): -1 or <c>block | state &lt;&lt; 8</c>.</summary>
    public static int CellAt(TowerSite site, int x, int y, int z)
    {
        var dx = x - site.CenterX;
        var dz = z - site.CenterZ;
        var step = TowerShell.StepOf(site, dx, dz);
        return Merge(site, TowerShell.RingOf(site, dx, dz, step), step, x, y, z);
    }

    // The shell cell, unless a floor's or the roof's dressing template writes there
    private static int Merge(TowerSite s, TowerRing ring, int step, int x, int y, int z)
    {
        if (ring == TowerRing.Outside) return -1;
        var shell = TowerShell.CellAt(s, ring, step, y);
        if (shell < 0) return shell;

        // A room cell between two slabs (a slab itself is always shell)
        if (ring == TowerRing.Room && y > s.GroundY && y < s.RoofY && (y - s.GroundY) % WorldGenerationConfig.TowerFloorHeight != 0)
        {
            var k = (y - s.GroundY) / WorldGenerationConfig.TowerFloorHeight;
            var dressed = TemplateCell(s, s.FloorTemplates[k], s.FloorTurns[k], x, y - s.FloorY(k) - 1, z, s.Tier(k));
            if (dressed >= 0) return dressed;
        }
        else if (ring != TowerRing.Apron && y > s.RoofY && y <= s.RoofY + s.Altar.Height)
        {
            if ((x, y, z) == s.AltarCell) return TowerPalette.AltarBlock;
            var dressed = TemplateCell(s, s.Altar, s.AltarTurns, x, y - s.RoofY - 1, z, TowerSite.TreasureTier);
            if (dressed >= 0) return dressed;
        }
        return shell;
    }

    // A template centered on the tower's center column at template layer ly, remapped to the site's palette, a chest given
    // the tier of the floor (or roof) it stands on; -1 outside it or where it writes nothing
    private static int TemplateCell(TowerSite s, StructureTemplate template, int turns, int x, int ly, int z, int tier)
    {
        var lx = x - StructureStamper.OriginOf(s.CenterX, template);
        var lz = z - StructureStamper.OriginOf(s.CenterZ, template);
        if ((uint)lx >= template.Size || (uint)lz >= template.Size || (uint)ly >= template.Height) return -1;
        var cell = template.CellAt(turns, lx, ly, lz);
        if (cell < 0) return cell;
        var block = cell & 0xFF;
        if (block == TowerPalette.DefaultBrick) return (cell & ~0xFF) | s.Palette.Brick;
        // The template's state for a chest is its facing alone (bits 0-1), so the tier bits OR on cleanly
        if (block == TowerPalette.ChestBlock) return cell | (ChestProfile.TierState(tier) << 8);
        return cell;
    }

    /// <summary>The skeleton pile cells floor <paramref name="floor"/>'s dressing template stands, in template order (layer,
    /// then row, then column, after the template's turn): the cells where both the turned template and the merged cell are the
    /// pile block. Scans the template, so a consumer keeps the result.</summary>
    internal static IReadOnlyList<(int X, int Y, int Z)> PileCells(TowerSite s, int floor)
    {
        RequireFloor(s, floor);
        return TemplateBlockCells(s, s.FloorTemplates[floor], s.FloorTurns[floor], s.FloorY(floor) + 1, TowerPalette.PileBlock);
    }

    /// <summary>The tower chest cells floor <paramref name="floor"/>'s dressing template stands, in template order, each
    /// carrying the floor's tier in its state. Scans the template, so a consumer keeps the result.</summary>
    internal static IReadOnlyList<(int X, int Y, int Z)> ChestCells(TowerSite s, int floor)
    {
        RequireFloor(s, floor);
        return TemplateBlockCells(s, s.FloorTemplates[floor], s.FloorTurns[floor], s.FloorY(floor) + 1, TowerPalette.ChestBlock);
    }

    /// <summary>The tower chest cells the roof's altar template stands, each carrying <see cref="TowerSite.TreasureTier"/>
    /// in its state. Scans the template, so a consumer keeps the result.</summary>
    internal static IReadOnlyList<(int X, int Y, int Z)> AltarChestCells(TowerSite s) =>
        TemplateBlockCells(s, s.Altar, s.AltarTurns, s.RoofY + 1, TowerPalette.ChestBlock);

    private static void RequireFloor(TowerSite s, int floor)
    {
        if (floor < 0 || floor >= s.Floors)
            throw new ArgumentOutOfRangeException(nameof(floor), floor, "A tower's floors run from 0 to its floor count - 1.");
    }

    // The cells where both the turned template (its layer 0 at world y0) and the merged cell are the block
    private static IReadOnlyList<(int X, int Y, int Z)> TemplateBlockCells(TowerSite s, StructureTemplate template, int turns,
        int y0, byte block)
    {
        var x0 = StructureStamper.OriginOf(s.CenterX, template);
        var z0 = StructureStamper.OriginOf(s.CenterZ, template);
        var cells = new List<(int X, int Y, int Z)>();
        for (var ly = 0; ly < template.Height; ly++)
        for (var lz = 0; lz < template.Size; lz++)
        for (var lx = 0; lx < template.Size; lx++)
        {
            var cell = template.CellAt(turns, lx, ly, lz);
            if (cell < 0 || (cell & 0xFF) != block) continue;
            int x = x0 + lx, y = y0 + ly, z = z0 + lz;
            var merged = CellAt(s, x, y, z);
            if (merged >= 0 && (merged & 0xFF) == block) cells.Add((x, y, z));
        }
        return cells;
    }

    /// <summary>Writes <paramref name="site"/>'s cells inside the chunk whose minimum corner is (<paramref name="worldX0"/>,
    /// <paramref name="worldY0"/>, <paramref name="worldZ0"/>). Every written cell's state byte is assigned, never OR'd, so a
    /// stale tree, leaf or snow state never survives under a tower block.</summary>
    internal static void StampSite(byte[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0, TowerSite site)
    {
        var xLo = Math.Max(site.MinX, worldX0);
        var xHi = Math.Min(site.MaxX, worldX0 + Chunk.Size - 1);
        var zLo = Math.Max(site.MinZ, worldZ0);
        var zHi = Math.Min(site.MaxZ, worldZ0 + Chunk.Size - 1);
        var yLo = Math.Max(site.BottomY, worldY0);
        var yHi = Math.Min(site.TopY, worldY0 + Chunk.Size - 1);
        if (xLo > xHi || zLo > zHi || yLo > yHi) return;

        for (var z = zLo; z <= zHi; z++)
        for (var x = xLo; x <= xHi; x++)
        {
            var dx = x - site.CenterX;
            var dz = z - site.CenterZ;
            var step = TowerShell.StepOf(site, dx, dz);
            var ring = TowerShell.RingOf(site, dx, dz, step);
            if (ring == TowerRing.Outside) continue;
            for (var y = yLo; y <= yHi; y++)
            {
                var cell = Merge(site, ring, step, x, y, z);
                if (cell < 0) continue;
                var index = Chunk.Idx(x - worldX0, y - worldY0, z - worldZ0);
                blocks[index] = (byte)(cell & 0xFF);
                var cellState = (byte)(cell >> 8);
                if (cellState != 0 || state != null)
                    (state ??= new byte[Chunk.Size * Chunk.Size * Chunk.Size])[index] = cellState;
            }
        }
    }
}
