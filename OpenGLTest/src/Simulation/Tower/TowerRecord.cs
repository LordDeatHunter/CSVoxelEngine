using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Entities.TowerMobs;
using VoxelEngine.WorldGen.Structures;

namespace VoxelEngine.Simulation.Tower;

/// <summary>
/// One battle tower's lock state, keyed by its placement cell: its center and footprint radius and a
/// <see cref="TowerFloorState"/> per floor. Built from a site (<see cref="FromSite"/>, the only place a site's pile and
/// door cells are scanned), from explicit floor data (<see cref="Build"/>, the test seam) or, on a client, as a
/// display-only mirror of the server's (<see cref="Mirror"/>). Main thread only.
/// </summary>
public sealed class TowerRecord
{
    private TowerRecord((int CellX, int CellZ) key, int centerX, int centerZ, int outerRadius, TowerFloorState[] floors,
        bool hasSite)
    {
        if (floors.Length == 0) throw new ArgumentException("A tower needs at least one floor.", nameof(floors));
        Key = key;
        CenterX = centerX;
        CenterZ = centerZ;
        OuterRadius = outerRadius;
        Floors = floors;
        HasSite = hasSite;
    }

    /// <summary>The tower's placement cell.</summary>
    public (int CellX, int CellZ) Key { get; }

    /// <summary>The center column's world X.</summary>
    public int CenterX { get; }

    /// <summary>The center column's world Z.</summary>
    public int CenterZ { get; }

    /// <summary>The footprint's radius in blocks (wall plus apron).</summary>
    public int OuterRadius { get; }

    /// <summary>The floors, ground floor first.</summary>
    public TowerFloorState[] Floors { get; }

    /// <summary>Whether the record was built from a world site, so it is saved (a test record or a mirror is not).</summary>
    public bool HasSite { get; }

    /// <summary>Where the roof altar's one fight stands.</summary>
    public TowerAltarState AltarState { get; set; }

    /// <summary>Seconds left of the ritual while <see cref="AltarState"/> is <see cref="TowerAltarState.Ritual"/>
    /// (authoritative side only; never saved or sent).</summary>
    public float RitualLeft { get; set; }

    /// <summary>Whether this tower's Skeleton King was killed (the hook the tower's collapse reads).</summary>
    public bool BossDefeated => AltarState == TowerAltarState.Defeated;

    /// <summary>The roof altar's cell: the center column right above the roof slab (the top floor's top), the same cell
    /// as the site's <see cref="TowerSite.AltarCell"/>.</summary>
    public (int X, int Y, int Z) AltarCell => (CenterX, Floors[^1].TopY + 1, CenterZ);

    /// <summary>Where this tower's Skeleton King lives: its feet on top of the altar block, leashed to the footprint
    /// disc, summoning skeletons of the top floor's tier.</summary>
    public SkeletonKingHome KingHome => new(Key, new Vector3(CenterX + 0.5f, AltarCell.Y + 1, CenterZ + 0.5f),
        OuterRadius, TowerDifficulty.Clamp(Floors.Length - 1));

    /// <summary>Whether the column (<paramref name="x"/>, <paramref name="z"/>) lies in the tower's footprint disc.</summary>
    public bool Contains(int x, int z)
    {
        var dx = x - CenterX;
        var dz = z - CenterZ;
        return dx * dx + dz * dz <= OuterRadius * OuterRadius + OuterRadius;
    }

    /// <summary>The floor whose band holds height <paramref name="y"/>, or the nearest one (the ground floor below the
    /// tower, the top floor above it).</summary>
    public int FloorOf(int y)
    {
        for (var k = 0; k < Floors.Length; k++)
            if (y > Floors[k].BottomY && y <= Floors[k].TopY) return k;
        return y <= Floors[0].BottomY ? 0 : Floors.Length - 1;
    }

    /// <summary>
    /// A record from explicit floor data, ground floor first: each floor's band (<c>Bottom</c> exclusive, <c>Top</c>
    /// inclusive), its pile cells (each starting with <see cref="MobConfig.SkeletonPileWaves"/> waves) and its door cells.
    /// <paramref name="hasSite"/> marks a record of a real world site, which is saved.
    /// </summary>
    public static TowerRecord Build((int CellX, int CellZ) key, int centerX, int centerZ, int outerRadius,
        IReadOnlyList<(int Bottom, int Top, IReadOnlyList<(int X, int Y, int Z)> Piles, IReadOnlyList<(int X, int Y, int Z)> Doors)> floors,
        bool hasSite)
    {
        var states = new TowerFloorState[floors.Count];
        for (var k = 0; k < floors.Count; k++)
        {
            var floor = floors[k];
            var piles = new PileState[floor.Piles.Count];
            for (var i = 0; i < piles.Length; i++)
                piles[i] = new PileState(floor.Piles[i], MobConfig.SkeletonPileWaves);
            states[k] = new TowerFloorState(k, floor.Bottom, floor.Top, new TowerFloorRef(key.CellX, key.CellZ, k), piles,
                floor.Doors);
        }
        return new TowerRecord(key, centerX, centerZ, outerRadius, states, hasSite);
    }

    /// <summary>The record of a world site: each floor's band from its slab to the next, its piles and its door. The
    /// only place a site's pile and door cells are scanned, so a consumer keeps the record.</summary>
    public static TowerRecord FromSite(TowerSite site)
    {
        var floors = new (int, int, IReadOnlyList<(int X, int Y, int Z)>, IReadOnlyList<(int X, int Y, int Z)>)[site.Floors];
        for (var k = 0; k < site.Floors; k++)
            floors[k] = (site.FloorY(k), site.FloorY(k + 1), site.PileCells(k), site.DoorCells(k));
        return Build((site.CellX, site.CellZ), site.CenterX, site.CenterZ, site.OuterRadius, floors, hasSite: true);
    }

    /// <summary>
    /// A client's display-only copy of the server's record: per floor its band, piles left, skeletons left and door bit,
    /// and the altar's state, with no piles or door cells of its own. It never ticks.
    /// </summary>
    public static TowerRecord Mirror((int CellX, int CellZ) key, int centerX, int centerZ, int outerRadius,
        IReadOnlyList<(int Bottom, int Top, int PilesLeft, int Alive, bool DoorOpen)> floors, TowerAltarState altarState)
    {
        var states = new TowerFloorState[floors.Count];
        for (var k = 0; k < floors.Count; k++)
        {
            var floor = floors[k];
            states[k] = new TowerFloorState(k, floor.Bottom, floor.Top, new TowerFloorRef(key.CellX, key.CellZ, k), [], [])
            {
                PilesLeft = floor.PilesLeft,
                Alive = floor.Alive,
                DoorOpen = floor.DoorOpen,
            };
        }
        return new TowerRecord(key, centerX, centerZ, outerRadius, states, hasSite: false) { AltarState = altarState };
    }
}
