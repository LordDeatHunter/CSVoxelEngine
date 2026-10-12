using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle.Tests;

/// <summary>
/// Headless <see cref="IItemUseWorld"/> for item <c>OnUse</c> tests: queries answer from plain
/// fields, effects only record that they were asked for. <see cref="World"/> drags in every
/// manager and its worker threads, so item behavior is pinned through this stub.
/// </summary>
internal sealed class StubItemUseWorld : IItemUseWorld
{
    public Dictionary<(int, int, int), (byte Id, byte State)> Cells { get; } = new();

    public bool RaycastHits { get; set; }
    public Vector3 RaycastHit { get; set; }
    public Vector3 RaycastNormal { get; set; }

    public List<(Vector3 Cell, Vector3 Normal, byte SourceId, byte AnchorId)> ConvertCalls { get; } = new();
    public bool ConvertResult { get; set; }

    public List<(int, int, int)> IgnitedKilns { get; } = new();
    public bool CanIgniteKilnResult { get; set; }

    public bool ToolReadyResult { get; set; } = true;
    public List<float> ToolUsedDelays { get; } = new();
    public int DamageCalls { get; set; }
    public int Consumed { get; set; }

    public double NowSeconds => 0.0;
    public bool IsCreative => false;

    /// <summary>A fixed chance roll, or null for a real random one.</summary>
    public float? FixedRoll { get; set; }

    public float NextUseRoll() => FixedRoll ?? System.Random.Shared.NextSingle();

    public bool RaycastFluidSource(float maxDistance, out Vector3 hit)
    {
        hit = RaycastHit;
        return false;
    }

    public bool RaycastSolid(float maxDistance, out Vector3 hit, out Vector3 normal)
    {
        hit = RaycastHit;
        normal = RaycastNormal;
        return RaycastHits;
    }

    public bool TryGetCell(int x, int y, int z, out byte id, out byte state)
    {
        if (Cells.TryGetValue((x, y, z), out var cell))
        {
            (id, state) = cell;
            return true;
        }
        id = 0;
        state = 0;
        return false;
    }

    public byte GetBlockAt(Vector3 pos) =>
        Cells.TryGetValue(((int)pos.X, (int)pos.Y, (int)pos.Z), out var cell) ? cell.Id : (byte)0;

    public bool ScoopFluidCell(Vector3 pos) => false;

    public bool PourFluidCell(Vector3 pos, byte fluidId) => false;

    public bool PlaceBlockAtFace(Vector3 hit, Vector3 normal, byte blockId, byte state, out Vector3 placedCell)
    {
        placedCell = hit;
        return false;
    }

    public void AfterPlaced(Vector3 pos) { }

    public int PileCountFor(byte blockId, int x, int y, int z, byte state) => 0;

    public int PileSecondaryFor(byte blockId, int x, int y, int z, byte state) => 0;

    public bool SetPileCount(Vector3 pos, byte count) => false;

    public bool SetPileComposition(Vector3 pos, byte blockId, int total, int secondary) => false;

    public void UnmarkPlayerPlaced(int x, int y, int z) { }

    public void SpawnLoot(byte blockId, Vector3 pos) { }

    public bool IsCampfireLit(int x, int y, int z) => false;

    public bool IgniteCampfire(int x, int y, int z) => false;

    public bool CanIgniteKiln(int x, int y, int z) => CanIgniteKilnResult;

    public bool IgniteKiln(int x, int y, int z)
    {
        if (!CanIgniteKilnResult) return false;
        IgnitedKilns.Add((x, y, z));
        return true;
    }

    public bool TryConvertMultiblock(Vector3 hitCell, Vector3 hitNormal, byte sourceId, byte anchorId)
    {
        ConvertCalls.Add((hitCell, hitNormal, sourceId, anchorId));
        return ConvertResult;
    }

    public bool IsPrimedTntBlock(byte id) => false;

    public bool IsCellSubmerged(int x, int y, int z) => false;

    public bool RemoveCellForPrime(Vector3 cell) => false;

    public void SpawnPrimedTnt(Vector3 cell, byte blockId) { }

    public void ScheduleFluidAround(Vector3 cell) { }

    public void MarkLodColumnEdited(int x, int y, int z) { }

    public bool ToolReady() => ToolReadyResult;

    public void MarkToolUsed(float delaySeconds) => ToolUsedDelays.Add(delaySeconds);

    public bool DamageTool(int amount = 1)
    {
        DamageCalls++;
        return false;
    }

    /// <summary>Health the stub's player has left; <see cref="Heal"/> refuses at the full pool.</summary>
    public int Health { get; set; } = PlayerHealth.MaxHealth;

    public int HealCalls { get; private set; }

    public bool Heal(int amount)
    {
        HealCalls++;
        if (Health <= 0 || Health >= PlayerHealth.MaxHealth) return false;
        Health = System.Math.Min(PlayerHealth.MaxHealth, Health + amount);
        return true;
    }

    /// <summary>Cells whose use would grow a tree; <see cref="GrowTree"/> refuses every other cell.</summary>
    public HashSet<(int, int, int)> TreeCells { get; } = new();

    public List<(int, int, int)> GrowTreeCalls { get; } = new();

    public bool GrowTree(Vector3 cell)
    {
        GrowTreeCalls.Add(((int)cell.X, (int)cell.Y, (int)cell.Z));
        return TreeCells.Contains(((int)cell.X, (int)cell.Y, (int)cell.Z));
    }

    /// <summary>Species names <see cref="PlantTree"/> accepts, with the cells they may plant on.</summary>
    public Dictionary<string, HashSet<(int, int, int)>> Plantable { get; } = new();

    public List<(string Species, int X, int Y, int Z)> PlantTreeCalls { get; } = new();

    public bool PlantTree(string species, Vector3 hit, Vector3 normal)
    {
        var ground = normal.Y > 0.5f ? hit : hit + normal;
        var cell = ((int)ground.X, (int)ground.Y, (int)ground.Z);
        PlantTreeCalls.Add((species, cell.Item1, cell.Item2, cell.Item3));
        return Plantable.TryGetValue(species, out var cells) && cells.Contains(cell);
    }

    public void ConsumeSelected() => Consumed++;
}
