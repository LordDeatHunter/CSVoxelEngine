using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Saves;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Trees;

/// <summary>Opt-in normal-block visibility diagnosis of five close fixed rays around the verified giant.</summary>
[Collection("Worldgen seed")]
public sealed class DynamicJungleCameraVisibilityDiagnostic
{
    private const string OptInVariable = "VOXEL_S4B_CAMERA_VISIBILITY_DIAGNOSTIC";
    private const int RayCellCap = 128;
    private const int TotalCellCap = 512;
    private const int ChunkCap = 12;
    private readonly record struct Cell(int X, int Y, int Z);
    private readonly record struct Key(int X, int Y, int Z);
    private sealed record Ray(string Name, Vector3 Eye, Vector3 Target, ushort ExpectedTarget, List<Cell> Cells);

    [OptInCameraFact]
    [Trait("Purpose", "S4bFixedCameraPreflight")]
    public void ReportsFiveCloseNaturalGiantCameraRaysThroughNormalChunks()
    {
        var phase = "setup";
        var plannedCells = 0;
        var plannedKeys = 0;
        var normalFills = 0;
        var observedRays = 0;
        var blockedRays = 0;
        var eyeChecks = 0;
        var targetChecks = 0;
        long growsBefore = 0;
        using var log = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
        try
        {
            TestContent.EnsureLoaded();
            var giant = TreeSpeciesRegistry.Require("jungle_giant");
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            growsBefore = TreeBatches.GrowsOnThisThread;

            phase = "plan-rays";
            var rays = new[]
            {
                MakeRay("north-to-giant-highest-leaf", new Vector3(4026.5f, 70.5f, 754.5f),
                    new Vector3(4026.5f, 73.5f, 779.5f), giant.LeavesId),
                MakeRay("north-to-giant-bare-trunk", new Vector3(4026.5f, 70.5f, 754.5f),
                    new Vector3(4026.5f, 58.5f, 778.5f), giant.WoodId),
                MakeRay("east-to-giant-highest-leaf", new Vector3(4045.5f, 61.5f, 778.5f),
                    new Vector3(4026.5f, 73.5f, 779.5f), giant.LeavesId),
                MakeRay("east-to-giant-bare-trunk", new Vector3(4045.5f, 61.5f, 778.5f),
                    new Vector3(4026.5f, 58.5f, 778.5f), giant.WoodId),
                MakeRay("close-above-to-giant-highest-leaf", new Vector3(4026.5f, 84.5f, 760.5f),
                    new Vector3(4026.5f, 73.5f, 779.5f), giant.LeavesId),
            };
            var keys = new HashSet<Key>();
            foreach (var ray in rays)
            {
                plannedCells += ray.Cells.Count;
                foreach (var cell in ray.Cells) keys.Add(KeyOf(cell));
                var delta = ray.Target - ray.Eye;
                var yaw = MathF.Atan2(delta.Z, delta.X) * (180f / MathF.PI);
                var pitch = MathF.Atan2(delta.Y, MathF.Sqrt(delta.X * delta.X + delta.Z * delta.Z))
                    * (180f / MathF.PI);
                log.WriteLine($"S4B_CAMERA_PLAN ray={ray.Name} eye={ray.Eye} target={ray.Target} yaw={yaw:R} pitch={pitch:R} cells={ray.Cells.Count}");
            }
            plannedKeys = keys.Count;
            log.WriteLine($"S4B_CAMERA_BUDGET cells={plannedCells} uniqueKeys={plannedKeys} keys={string.Join(';', keys.OrderBy(k => k.X).ThenBy(k => k.Y).ThenBy(k => k.Z))}");
            if (plannedCells > TotalCellCap || plannedKeys > ChunkCap || keys.Any(k => k.Y < 0))
                throw new InvalidOperationException("Unsupported fixed-camera cell/key/positive-Y budget; no normal fills made.");

            phase = "normal-fills";
            var chunks = new Dictionary<Key, Chunk>();
            foreach (var key in keys.OrderBy(k => k.X).ThenBy(k => k.Y).ThenBy(k => k.Z))
            {
                var chunk = new Chunk(null!, new Vector3(key.X, key.Y, key.Z), generateBlockData: false);
                normalFills++;
                ChunkTerrainFill.GenerateBlockData(chunk);
                chunks.Add(key, chunk);
                log.WriteLine($"S4B_CAMERA_FILL key={key} ready={chunk.IsBlockDataReady}");
            }

            phase = "observe-rays";
            var seenEyes = new HashSet<Cell>();
            foreach (var ray in rays)
            {
                var eye = CellOf(ray.Eye);
                var endpoint = CellOf(ray.Target);
                if (seenEyes.Add(eye))
                {
                    eyeChecks++;
                    var eyeBlock = Read(chunks, eye);
                    log.WriteLine($"S4B_CAMERA_EYE cell={eye} block={Describe(eyeBlock.Id)} state={eyeBlock.State} air={eyeBlock.Id == BlockType.Air}");
                }
                targetChecks++;
                var target = Read(chunks, endpoint);
                Cell? first = null;
                ushort firstId = BlockType.Air;
                byte firstState = 0;
                foreach (var cell in ray.Cells)
                {
                    if (cell == eye || cell == endpoint) continue;
                    var block = Read(chunks, cell);
                    if (block.Id == BlockType.Air) continue;
                    first = cell;
                    firstId = block.Id;
                    firstState = block.State;
                    break;
                }
                observedRays++;
                if (first != null) blockedRays++;
                log.WriteLine($"S4B_CAMERA_RAY name={ray.Name} visited={ray.Cells.Count} clear={first == null} first={first?.ToString() ?? "none"} firstBlock={Describe(firstId)} firstState={firstState} targetCell={endpoint} target={Describe(target.Id)} targetState={target.State} expected={Describe(ray.ExpectedTarget)} targetMatches={target.Id == ray.ExpectedTarget}");
            }
        }
        finally
        {
            try
            {
                log.WriteLine($"S4B_CAMERA_LEDGER phase={phase} directColumnGets=0 plannedCells={plannedCells} plannedKeys={plannedKeys} normalFills={normalFills} probeFills=0 eyeChecks={eyeChecks} targetChecks={targetChecks} observedRays={observedRays} blockedRays={blockedRays} growsOnThreadBefore={growsBefore} growsOnThreadAfter={TreeBatches.GrowsOnThisThread} growDelta={TreeBatches.GrowsOnThisThread - growsBefore}");
            }
            finally
            {
                WorldSeed.Apply(WorldSeed.Default);
            }
        }
    }

    private static Ray MakeRay(string name, Vector3 eye, Vector3 target, ushort expected)
    {
        var cells = Walk(eye, target);
        if (cells.Count > RayCellCap)
            throw new InvalidOperationException($"{name} exceeds {RayCellCap} cells before any fill.");
        return new Ray(name, eye, target, expected, cells);
    }

    // Supercover DDA: at an exact edge/corner crossing include every adjacent touched cell,
    // then advance all tied axes. This over-reports opaque obstacles rather than missing one.
    private static List<Cell> Walk(Vector3 eye, Vector3 target)
    {
        var delta = target - eye;
        var current = CellOf(eye);
        var end = CellOf(target);
        var sx = Math.Sign(delta.X);
        var sy = Math.Sign(delta.Y);
        var sz = Math.Sign(delta.Z);
        var dx = sx == 0 ? double.PositiveInfinity : Math.Abs(1d / delta.X);
        var dy = sy == 0 ? double.PositiveInfinity : Math.Abs(1d / delta.Y);
        var dz = sz == 0 ? double.PositiveInfinity : Math.Abs(1d / delta.Z);
        var tx = sx > 0 ? (current.X + 1d - eye.X) * dx : sx < 0 ? (eye.X - current.X) * dx : double.PositiveInfinity;
        var ty = sy > 0 ? (current.Y + 1d - eye.Y) * dy : sy < 0 ? (eye.Y - current.Y) * dy : double.PositiveInfinity;
        var tz = sz > 0 ? (current.Z + 1d - eye.Z) * dz : sz < 0 ? (eye.Z - current.Z) * dz : double.PositiveInfinity;
        var cells = new List<Cell> { current };
        var seen = new HashSet<Cell> { current };
        while (current != end)
        {
            var next = Math.Min(tx, Math.Min(ty, tz));
            if (next > 1d + 1e-10 || cells.Count >= RayCellCap)
                throw new InvalidOperationException("Fixed ray exceeds target or cell cap before any fill.");
            const double epsilon = 1e-10;
            var ax = Math.Abs(tx - next) <= epsilon;
            var ay = Math.Abs(ty - next) <= epsilon;
            var az = Math.Abs(tz - next) <= epsilon;
            for (var mask = 1; mask < 8; mask++)
            {
                if ((mask & 1) != 0 && !ax || (mask & 2) != 0 && !ay || (mask & 4) != 0 && !az)
                    continue;
                var cell = new Cell(current.X + ((mask & 1) != 0 ? sx : 0),
                    current.Y + ((mask & 2) != 0 ? sy : 0),
                    current.Z + ((mask & 4) != 0 ? sz : 0));
                if (seen.Add(cell)) cells.Add(cell);
            }
            current = new Cell(current.X + (ax ? sx : 0), current.Y + (ay ? sy : 0),
                current.Z + (az ? sz : 0));
            if (ax) tx += dx;
            if (ay) ty += dy;
            if (az) tz += dz;
        }
        return cells;
    }

    private static Cell CellOf(Vector3 point) => new((int)MathF.Floor(point.X),
        (int)MathF.Floor(point.Y), (int)MathF.Floor(point.Z));
    private static Key KeyOf(Cell cell) => new(cell.X >> Chunk.SizeShift,
        cell.Y >> Chunk.SizeShift, cell.Z >> Chunk.SizeShift);
    private static (ushort Id, byte State) Read(Dictionary<Key, Chunk> chunks, Cell cell)
    {
        var chunk = chunks[KeyOf(cell)];
        return (chunk.GetLocalBlock(cell.X & Chunk.SizeMask, cell.Y & Chunk.SizeMask, cell.Z & Chunk.SizeMask),
            chunk.GetLocalState(cell.X & Chunk.SizeMask, cell.Y & Chunk.SizeMask, cell.Z & Chunk.SizeMask));
    }
    private static string Describe(ushort id) =>
        $"{id}:{SaveNames.NameOf(id) ?? "air/unknown"}:{TreeSpeciesRegistry.ForBlock(id)?.Name ?? "none"}";

    /// <summary>Default discovery skips this fixed close-camera diagnostic.</summary>
    public sealed class OptInCameraFactAttribute : FactAttribute
    {
        public OptInCameraFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
                Skip = $"Set {OptInVariable}=1 for five fixed S4b close-camera rays.";
        }
    }
}
