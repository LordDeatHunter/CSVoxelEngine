using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Silk.NET.Input;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Icebergs;
using VoxelEngine.Player;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.VisualTest;
using Xunit;

namespace VoxelEngine.Tests.Player;

/// <summary>
/// The non-block solids seam (<see cref="IBodySolids"/>, <see cref="Camera.BodySolids"/>): the
/// player's own physics lands on, is stopped by and auto-steps onto such boxes exactly like blocks,
/// and every iceberg variant can be boarded by a swimmer and climbed to its top on foot.
/// </summary>
public sealed class BodySolidsTests
{
    public BodySolidsTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private static Camera BodyAt(Vector3 feet, IBodySolids solids, float yaw = 0f)
    {
        var camera = new Camera(null, null) { BodySolids = solids };
        camera.TestTeleport(feet + new Vector3(0f, Camera.EyeHeight, 0f), yaw, pitch: 0f);
        return camera;
    }

    private static float Feet(Camera camera) => camera.Position.Y - Camera.EyeHeight;

    private static void Run(Camera camera, ChunkManager chunks, ScriptedKeyboard keys, float seconds,
        Action<Camera>? eachFrame = null)
    {
        for (var t = 0f; t < seconds; t += Dt)
        {
            camera.Update(Dt, keys, chunks);
            eachFrame?.Invoke(camera);
        }
    }

    private static ChunkManager StoneFloor() => MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);

    [Fact]
    public void ABodyFallingOntoASolidBoxRestsOnItsTop()
    {
        var solids = new BoxSolids().Add(new Vector3(6f, 3f, 6f), new Vector3(11f, 4f, 11f));
        var camera = BodyAt(new Vector3(8.5f, 7f, 8.5f), solids);
        Run(camera, StoneFloor(), new ScriptedKeyboard(), 1.5f);

        Assert.True(camera.IsGrounded);
        Assert.InRange(Feet(camera), 4f, 4f + BoxSweep.StepSize);
    }

    [Fact]
    public void AWalkIsStoppedByASolidBox()
    {
        // A wall three high across the chunk at x = 10, too tall for any auto-step
        var solids = new BoxSolids().Add(new Vector3(10f, 1f, 0f), new Vector3(11f, 4f, 16f));
        var camera = BodyAt(new Vector3(6.5f, 1f, 8.5f), solids);
        var keys = new ScriptedKeyboard();
        keys.Press([Key.W]);
        Run(camera, StoneFloor(), keys, 2f);

        Assert.True(camera.Position.X + PlayerPhysics.PlayerRadius <= 10f + 1e-3f);
        Assert.True(camera.Position.X + PlayerPhysics.PlayerRadius > 10f - 0.1f, "the walk stopped short of the box");
        Assert.InRange(Feet(camera), 1f, 1f + BoxSweep.StepSize);
    }

    [Fact]
    public void AHalfBlockStepOnTheBoxIsAutoStepped()
    {
        var solids = new BoxSolids().Add(new Vector3(10f, 1f, 0f), new Vector3(16f, 1.5f, 16f));
        var camera = BodyAt(new Vector3(6.5f, 1f, 8.5f), solids);
        var keys = new ScriptedKeyboard();
        keys.Press([Key.W]);
        Run(camera, StoneFloor(), keys, 1.2f);

        Assert.True(camera.Position.X > 10.5f, "the walk never got onto the step");
        Assert.InRange(Feet(camera), 1.5f, 1.5f + BoxSweep.StepSize);
    }

    /// <summary>
    /// A 3x3-chunk stone floor at y = 0 under source water up to y = <paramref name="waterTop"/>,
    /// wide enough for the largest berg at its largest scale and a swimmer beside it.
    /// </summary>
    private static ChunkManager WidePool(int waterTop)
    {
        var chunks = new ChunkManager(null!);
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var water = ContentRegistry.RequireBlock("water").Id;
        for (var cx = -1; cx <= 1; cx++)
        for (var cz = -1; cz <= 1; cz++)
        {
            var pos = new Vector3(cx, 0, cz);
            var chunk = new Chunk(null!, pos, generateBlockData: false) { IsBlockDataReady = true };
            chunks.Chunks[pos] = chunk;
            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                chunk.SetBlockRaw(x, 0, z, stone, 0);
                for (var y = 1; y <= waterTop; y++)
                    chunk.SetBlockRaw(x, y, z, water, 0);
            }
        }
        return chunks;
    }

    [Fact]
    public void ASwimmerCanBoardEveryIcebergVariant()
    {
        const int waterTop = 12;
        var surface = waterTop + FluidState.SourceHeight;
        var pool = WidePool(waterTop);
        var shapes = IcebergShape.ForType(EntityRegistry.Require("iceberg"));

        for (var variant = 0; variant < shapes.Length; variant++)
        {
            var shape = shapes[variant];
            var center = new Vector3(8f, surface, 8f);
            var solids = new BoxSolids();
            var placed = new List<(Vector3 Min, Vector3 Max)>();
            shape.PlaceAll(0, IcebergConfig.ScaleMax, center, placed);
            solids.Boxes.AddRange(placed);
            var top = placed.OrderByDescending(b => b.Max.Y).First();
            var bounds = shape.PlacedBounds(0, IcebergConfig.ScaleMax, center);

            // Floating off the -X side of the shelf, in the lane through the top step, swimming at
            // the berg (+X) and stroking up. A swimmer times its stroke to the edge, so the boarding
            // counts from any of a range of starting gaps.
            var lane = (top.Min.Z + top.Max.Z) * 0.5f;
            var shelfMinX = placed.Where(b => b.Min.Y < center.Y && b.Max.Y > center.Y).Min(b => b.Min.X);
            var restFeet = surface - PlayerConfig.WaterFloatRestShare * PlayerPhysics.PlayerHeight;
            var climbed = false;
            var highest = float.NegativeInfinity;
            var keys = new ScriptedKeyboard();
            var tried = new List<string>();
            for (var gap = 0.25f; gap <= 4f && !climbed; gap += 0.25f)
            {
                var swimmer = BodyAt(new Vector3(shelfMinX - PlayerPhysics.PlayerRadius - gap, restFeet, lane), solids);
                keys.ReleaseAll();
                keys.Press([Key.W, Key.Space]);
                var boarded = false;
                // Once aboard, on foot it walks up the steps toward the top
                Run(swimmer, pool, keys, 9f, c =>
                {
                    if (!c.IsGrounded || Feet(c) <= center.Y)
                        return;
                    highest = MathF.Max(highest, Feet(c));
                    if (boarded)
                        return;
                    boarded = true;
                    keys.ReleaseAll();
                    keys.Press([Key.W]);
                });
                climbed = boarded;
                if (!boarded)
                    tried.Add($"{gap}:{Feet(swimmer) - center.Y:0.00}");
            }
            Assert.True(climbed, $"a swimmer never got onto variant {variant} ({string.Join(", ", tried)})");
            Assert.True(highest >= bounds.Max.Y - 1e-3f,
                $"variant {variant}: the walk topped out at {highest - center.Y} above the waterline, the berg stands {shape.Freeboard}");
        }
    }
}
