// Opt-in research source. This file is outside the default test project and is not compiled.
// See README.md for the separately reviewed temporary-inclusion procedure.
using System;
using System.Numerics;
using VoxelEngine.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>Four frozen camera poses at two already recorded natural Jungle tall ferns.</summary>
[Collection("Worldgen seed")]
public sealed class JungleTallFernVisibilityDiagnosticTests(ITestOutputHelper output) : IDisposable
{
    private const int Cx = -550, Cy = 4, Cz = 650;
    private const int MaxRayVisits = 32;
    private const int MaxRays = 40;
    private const int MaxIdReads = 1290;
    private const float Reach = 10f;
    private const float Aspect = 16f / 9f;
    private const float ProjectionMarginDegrees = 10f;
    private int _normalAttempts, _rays, _rayVisits, _rayIdReads, _fixedIdReads;

    private readonly record struct Target(string Name, int X, int GroundY, int Z);
    private readonly record struct Pose(string Name, Target Target, Vector3 Eye, float Yaw, float Pitch);

    private static readonly Target A = new("A", -8799, 68, 10408);
    private static readonly Target B = new("B", -8790, 69, 10410);
    private static readonly Pose[] Poses =
    [
        new("A1-known-obscured", A, new Vector3(-8792.5f, 71.4f, 10404.5f), 125f, -15f),
        new("A2-east", A, new Vector3(-8789.5f, 72.4f, 10406.5f), 145f, -10f),
        new("B1-north", B, new Vector3(-8789.5f, 72.4f, 10405.5f), 68f, -16f),
        new("B2-northwest", B, new Vector3(-8790.5f, 72.4f, 10403.5f), 60f, -12f),
    ];

    public void Dispose() => WorldSeed.Apply(WorldSeed.Default);

    [Fact]
    public void FixedTallFernEyesReportConservativeAirCorridors()
    {
        TestContent.EnsureLoaded();
        var mud = ContentRegistry.RequireBlock("jungle_mud").Id;
        var grass = ContentRegistry.RequireBlock("grass").Id;
        var lower = ContentRegistry.RequireBlock("tall_fern").Id;
        var upper = ContentRegistry.RequireBlock("tall_fern_top").Id;
        var viable = 0;
        string? firstViable = null;
        try
        {
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            // One explicit normal fill at the existing f003 key. Internal cache
            // dependencies of that fill are transitive work, not a new search.
            _normalAttempts++;
            var chunk = new Chunk(null!, new Vector3(Cx, Cy, Cz), generateBlockData: true);
            Assert.True(chunk.IsBlockDataReady);
            CheckTarget(chunk, A, mud, lower, upper);
            CheckTarget(chunk, B, grass, lower, upper);
            foreach (var pose in Poses)
                Assert.Equal(BlockType.Air, FixedRead(chunk, Floor(pose.Eye)));

            foreach (var pose in Poses)
            {
                var forward = Vector3Utils.FromYawPitch(pose.Yaw, pose.Pitch);
                var points = SightPoints(pose.Target);
                var framed = Framed(pose, forward, points);
                var clear = true;
                for (var i = 0; i < points.Length; i++)
                {
                    var ground = pose.Target.Name == "A" ? mud : grass;
                    var outcome = Trace(chunk, pose.Eye, points[i], pose.Target, ground, lower, upper, allowTarget: true);
                    output.WriteLine($"{pose.Name} point={i} outcome={outcome}");
                    clear &= outcome == "clear";
                }

                // Deliberately stricter than the game's box-aware selection ray. Every
                // cell on the full center ray must be observed air, so no foreground
                // selected shape can create an outline over the off-axis target.
                var center = Trace(chunk, pose.Eye, pose.Eye + forward * Reach,
                    pose.Target, pose.Target.Name == "A" ? mud : grass, lower, upper, allowTarget: false);
                output.WriteLine($"{pose.Name} forward={forward} yaw={pose.Yaw} pitch={pose.Pitch} " +
                    $"framed={framed} targetCorridorsClear={clear} centerAirCorridor={center}");
                if (!framed || !clear || center != "clear") continue;
                viable++;
                firstViable ??= pose.Name;
            }

            output.WriteLine($"firstViable={firstViable ?? "none"} viable={viable}/4; zero is an honest visibility result, not a product failure");
        }
        finally
        {
            output.WriteLine($"visibility ledger rays={_rays}/{MaxRays} rayVisits={_rayVisits}/{MaxRays * MaxRayVisits} " +
                $"rayIdReads={_rayIdReads} fixedIdReads={_fixedIdReads}/10 " +
                $"explicitIdReads={_rayIdReads + _fixedIdReads}/{MaxIdReads}; " +
                $"normalChunkAttempts={_normalAttempts}/1 plannerProbeAttempts=0 worldAttempts=0 meshAttempts=0 " +
                "source-derived fill/cache work unmeasured");
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    private void CheckTarget(Chunk chunk, Target target, ushort ground, ushort lower, ushort upper)
    {
        Assert.Equal(ground, FixedRead(chunk, (target.X, target.GroundY, target.Z)));
        Assert.Equal(lower, FixedRead(chunk, (target.X, target.GroundY + 1, target.Z)));
        Assert.Equal(upper, FixedRead(chunk, (target.X, target.GroundY + 2, target.Z)));
    }

    private ushort FixedRead(Chunk chunk, (int X, int Y, int Z) cell)
    {
        Assert.True(InChunk(cell), $"fixed cell outside observed chunk: {cell}");
        _fixedIdReads++;
        Assert.True(_fixedIdReads + _rayIdReads <= MaxIdReads);
        return chunk.GetLocalBlock(cell.X - Cx * Chunk.Size, cell.Y - Cy * Chunk.Size,
            cell.Z - Cz * Chunk.Size);
    }

    // Nine frozen points stay strictly inside the verified ground/lower/upper cells.
    // The joint samples sit 0.02 above the integer boundary to avoid ambiguity.
    private static Vector3[] SightPoints(Target t)
    {
        var x = t.X + .5f;
        var z = t.Z + .5f;
        var joint = t.GroundY + 2.02f;
        return
        [
            new(x, t.GroundY + .98f, z),
            new(x, t.GroundY + 1.5f, z),
            new(x, joint, z),
            new(x - .2f, joint, z),
            new(x + .2f, joint, z),
            new(x, joint, z - .2f),
            new(x, joint, z + .2f),
            new(x, t.GroundY + 2.5f, z),
            new(x, t.GroundY + 2.85f, z),
        ];
    }

    private bool Framed(Pose pose, Vector3 forward, Vector3[] points)
    {
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var up = Vector3.Cross(right, forward);
        var halfVertical = 30f; // Conservative minimum configured vertical FOV is 60 degrees.
        var halfHorizontal = MathF.Atan(MathF.Tan(MathF.PI / 6f) * Aspect) * 180f / MathF.PI;
        var framed = true;
        foreach (var point in points)
        {
            var delta = point - pose.Eye;
            var depth = Vector3.Dot(delta, forward);
            var horizontal = MathF.Atan2(Vector3.Dot(delta, right), depth) * 180f / MathF.PI;
            var vertical = MathF.Atan2(Vector3.Dot(delta, up), depth) * 180f / MathF.PI;
            var inside = depth > 0f && MathF.Abs(horizontal) <= halfHorizontal - ProjectionMarginDegrees
                && MathF.Abs(vertical) <= halfVertical - ProjectionMarginDegrees;
            output.WriteLine($"{pose.Name} projected={point} depth={depth:F2} h={horizontal:F2} v={vertical:F2} inside={inside}");
            framed &= inside;
        }
        return framed;
    }

    private string Trace(Chunk chunk, Vector3 origin, Vector3 endpoint, Target target,
        ushort ground, ushort lower, ushort upper, bool allowTarget)
    {
        _rays++;
        Assert.True(_rays <= MaxRays, "more than 40 fixed rays");
        var delta = endpoint - origin;
        var length = delta.Length();
        Assert.InRange(length, 0.001f, Reach + .001f); // float endpoint rounding only
        var direction = delta / length;
        var cell = Floor(origin);
        var finish = Floor(endpoint);
        var step = (X: Math.Sign(direction.X), Y: Math.Sign(direction.Y), Z: Math.Sign(direction.Z));
        var tx = BoundaryDistance(origin.X, cell.X, direction.X, step.X);
        var ty = BoundaryDistance(origin.Y, cell.Y, direction.Y, step.Y);
        var tz = BoundaryDistance(origin.Z, cell.Z, direction.Z, step.Z);
        var dx = StepDistance(direction.X), dy = StepDistance(direction.Y), dz = StepDistance(direction.Z);
        var visited = 0;

        string? Inspect((int X, int Y, int Z) current)
        {
            if (visited == MaxRayVisits) return "capped";
            visited++;
            _rayVisits++;
            if (!InChunk(current)) return $"unknown-outside:{current}";
            _rayIdReads++;
            Assert.True(_fixedIdReads + _rayIdReads <= MaxIdReads);
            var id = chunk.GetLocalBlock(current.X - Cx * Chunk.Size,
                current.Y - Cy * Chunk.Size, current.Z - Cz * Chunk.Size);
            if (id == BlockType.Air) return null;
            if (allowTarget && current.X == target.X && current.Z == target.Z)
            {
                var expected = current.Y == target.GroundY ? (ushort?)ground
                    : current.Y == target.GroundY + 1 ? lower
                    : current.Y == target.GroundY + 2 ? upper
                    : null;
                if (expected == id) return null;
            }
            return $"blocked:{current}:id={id}";
        }

        var initial = Inspect(cell);
        if (initial != null) return initial;
        while (true)
        {
            var next = MathF.Min(tx, MathF.Min(ty, tz));
            if (next > length) return cell == finish ? "clear" : "unknown-endpoint";
            // Ties touch multiple adjacent cells. Inspect every nonempty subset
            // before advancing the main DDA cell, conservatively including edges.
            var axes = (MathF.Abs(tx - next) <= 1e-5f ? 1 : 0)
                | (MathF.Abs(ty - next) <= 1e-5f ? 2 : 0)
                | (MathF.Abs(tz - next) <= 1e-5f ? 4 : 0);
            if (axes == 0) return "unknown-numeric";
            for (var subset = 1; subset <= 7; subset++)
            {
                if ((subset & axes) != subset) continue;
                var touched = (cell.X + ((subset & 1) != 0 ? step.X : 0),
                    cell.Y + ((subset & 2) != 0 ? step.Y : 0),
                    cell.Z + ((subset & 4) != 0 ? step.Z : 0));
                var result = Inspect(touched);
                if (result != null) return result;
            }
            cell = (cell.X + ((axes & 1) != 0 ? step.X : 0),
                cell.Y + ((axes & 2) != 0 ? step.Y : 0),
                cell.Z + ((axes & 4) != 0 ? step.Z : 0));
            if ((axes & 1) != 0) tx += dx;
            if ((axes & 2) != 0) ty += dy;
            if ((axes & 4) != 0) tz += dz;
        }
    }

    private static float BoundaryDistance(float origin, int cell, float direction, int step) =>
        step == 0 ? float.PositiveInfinity
        : ((step > 0 ? cell + 1 : cell) - origin) / direction;

    private static float StepDistance(float direction) =>
        direction == 0f ? float.PositiveInfinity : MathF.Abs(1f / direction);

    private static (int X, int Y, int Z) Floor(Vector3 p) =>
        ((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y), (int)MathF.Floor(p.Z));

    private static bool InChunk((int X, int Y, int Z) cell) =>
        cell.X >= Cx * Chunk.Size && cell.X < (Cx + 1) * Chunk.Size
        && cell.Y >= Cy * Chunk.Size && cell.Y < (Cy + 1) * Chunk.Size
        && cell.Z >= Cz * Chunk.Size && cell.Z < (Cz + 1) * Chunk.Size;
}
