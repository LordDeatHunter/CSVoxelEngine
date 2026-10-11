using System;
using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The server's movement rubberband (<see cref="MovementRules"/>): everything the client's own
/// physics can do - walking, sprinting, jumping, stepping up a block, being knocked back, swimming,
/// falling, standing still - is accepted, while a move into a solid cell, a climb too steep for a
/// jump and a hover without the flight flag are rejected back to the last legal position. Flight
/// is noclip, and a body over cells the server has not loaded is never judged.
/// </summary>
public sealed class MovementRulesTests
{
    public MovementRulesTests() => TestContent.EnsureLoaded();

    // The client's state cadence
    private const float Dt = (float)WorldGenerationConfig.NetPlayerStateSeconds;

    // Where a body resting on the y = 0 floor stands: the client's physics stops a landing before
    // the colliding step, a hair above the block top
    private const float Resting = 1.01f;

    // The wall every collision case walks into: three high at x = 8, across the whole chunk
    private const int WallX = 8;

    // A single block to step up onto
    private static readonly (int X, int Y, int Z) Step = (4, 1, 12);

    private static byte Stone => ContentRegistry.RequireBlock("stone").Id;

    /// <summary>The stone floor at y = 0 with the wall and the step on it.</summary>
    private static ChunkManager Course()
    {
        var chunks = MobTestWorld.Floor(Stone);
        var chunk = chunks.Chunks[Vector3.Zero];
        for (var z = 0; z < Chunk.Size; z++)
        for (var y = 1; y <= 3; y++)
            chunk.SetBlockRaw(WallX, y, z, Stone, 0);
        chunk.SetBlockRaw(Step.X, Step.Y, Step.Z, Stone, 0);
        return chunks;
    }

    private static Vector3 Eye(float x, float feet, float z) => new(x, feet + Camera.EyeHeight, z);

    private static MoveVerdict Judge(ChunkManager chunks, Vector3 from, Vector3 to, out Vector3 accepted,
        bool flying = false, MovementState? state = null) =>
        MovementRules.Judge(from, to, Dt, flying, chunks, state ?? new MovementState(), out accepted);

    /// <summary>
    /// Feeds the rules <paramref name="path"/> as one player's states at the client's cadence and
    /// returns the index of the first rejected state, or -1 when every one was taken. A rejected
    /// state snaps the walk back to where the rules left it, as the client would be.
    /// </summary>
    private static int FirstRejection(ChunkManager chunks, IReadOnlyList<Vector3> path, bool flying = false)
    {
        var state = new MovementState();
        var at = path[0];
        for (var i = 1; i < path.Count; i++)
        {
            if (MovementRules.Judge(at, path[i], Dt, flying, chunks, state, out at) == MoveVerdict.Rejected)
                return i;
        }
        return -1;
    }

    private static void AssertNeverRejected(string what, ChunkManager chunks, IReadOnlyList<Vector3> path,
        bool flying = false)
    {
        var rejected = FirstRejection(chunks, path, flying);
        Assert.True(rejected < 0, $"{what}: state {rejected} of {path.Count} was rejected");
    }

    /// <summary>
    /// <paramref name="seconds"/> of states from <paramref name="start"/> at a steady horizontal
    /// <paramref name="velocity"/>, the feet launched upward at <paramref name="launch"/> and pulled
    /// down by the client's gravity until they rest on the floor (<see cref="Resting"/>); a positive
    /// <paramref name="hopEvery"/> relaunches the jump on that period (a bunny hop).
    /// </summary>
    private static List<Vector3> Arc(Vector3 start, Vector3 velocity, float launch, float seconds,
        float hopEvery = 0f)
    {
        var path = new List<Vector3>();
        var feet0 = start.Y - Camera.EyeHeight;
        for (var i = 0; i * Dt <= seconds + 1e-4f; i++)
        {
            var t = i * Dt;
            var phase = hopEvery > 0f ? t % hopEvery : t;
            var feet = MathF.Max(Resting, feet0 + launch * phase - 0.5f * PlayerPhysics.Gravity * phase * phase);
            path.Add(new Vector3(start.X + velocity.X * t, feet + Camera.EyeHeight, start.Z + velocity.Z * t));
        }
        return path;
    }

    /// <summary><paramref name="seconds"/> of states from <paramref name="start"/> at a steady <paramref name="velocity"/>.</summary>
    private static List<Vector3> Line(Vector3 start, Vector3 velocity, float seconds)
    {
        var path = new List<Vector3>();
        for (var i = 0; i * Dt <= seconds + 1e-4f; i++)
            path.Add(start + velocity * (i * Dt));
        return path;
    }

    [Fact]
    public void NormalPlayNeverTripsTheRules()
    {
        // Every upward speed the client's physics produces climbs slower than the rise cap, a sprint
        // auto-step fits the slack of one state, and a jump's climb ends well inside the hover grace
        foreach (var rise in new[]
                 {
                     PlayerPhysics.JumpSpeed, PlayerPhysics.SwimUpSpeed, PlayerPhysics.SwimExitBoost,
                     WorldGenerationConfig.SlimeKnockbackLift, WorldGenerationConfig.GoblinKnockbackLift,
                 })
            Assert.True(rise <= WorldGenerationConfig.NetMaxRiseBlocksPerSecond);
        Assert.True(PlayerPhysics.SprintStepHeight <= WorldGenerationConfig.NetRiseSlackBlocks);
        Assert.True(PlayerPhysics.JumpSpeed / PlayerPhysics.Gravity < WorldGenerationConfig.NetHoverGraceSeconds);

        var course = Course();
        var walk = PlayerPhysics.BaseSpeed;
        var sprint = PlayerPhysics.BaseSpeed * PlayerPhysics.SprintMultiplier;

        AssertNeverRejected("walk", course, Line(Eye(2.5f, Resting, 1f), new Vector3(0f, 0f, walk), 0.5f));
        AssertNeverRejected("sprint", course, Line(Eye(5.5f, Resting, 1f), new Vector3(0f, 0f, sprint), 0.6f));
        AssertNeverRejected("standing jump", course,
            Arc(Eye(2.5f, Resting, 10.5f), Vector3.Zero, PlayerPhysics.JumpSpeed, 1f));
        AssertNeverRejected("sprint jump", course,
            Arc(Eye(5.5f, Resting, 1f), new Vector3(0f, 0f, sprint), PlayerPhysics.JumpSpeed, 0.6f));
        AssertNeverRejected("bunny hop", course,
            Arc(Eye(2.5f, Resting, 10.5f), Vector3.Zero, PlayerPhysics.JumpSpeed, 5f,
                hopEvery: 2f * PlayerPhysics.JumpSpeed / PlayerPhysics.Gravity));

        // A sprint auto-step onto a block inside one state, then a stride on top of it
        AssertNeverRejected("step up", course,
        [
            Eye(Step.X - 0.35f, Resting, Step.Z + 0.5f),
            Eye(Step.X + 0.25f, Step.Y + 1.001f, Step.Z + 0.5f),
            Eye(Step.X + 0.65f, Step.Y + 1.001f, Step.Z + 0.5f),
        ]);

        // A mob's hit on a sprinting player: the strongest shove on top of the sprint, the
        // strongest lift, then gravity
        var shove = sprint + MathF.Max(WorldGenerationConfig.SlimeKnockbackSpeed, WorldGenerationConfig.GoblinKnockbackSpeed);
        var lift = MathF.Max(WorldGenerationConfig.SlimeKnockbackLift, WorldGenerationConfig.GoblinKnockbackLift);
        AssertNeverRejected("knockback", course, Arc(Eye(2.5f, Resting, 1f), new Vector3(0f, 0f, shove), lift, 0.5f));

        // A fall from high up, landing on the floor
        var fall = Arc(Eye(2.5f, 12f, 6.5f), Vector3.Zero, 0f, 1.5f);
        AssertNeverRejected("fall", course, fall);
        Assert.Equal(Resting, fall[^1].Y - Camera.EyeHeight, 3);

        // Standing still on the ground for a while
        AssertNeverRejected("idle", course, Line(Eye(2.5f, Resting, 14.5f), Vector3.Zero, 5f));

        // Swimming straight up through a pool, and the boost out of it
        var pool = MobTestWorld.Pool(6);
        AssertNeverRejected("swim up", pool,
            Line(Eye(2.5f, Resting, 2.5f), new Vector3(0f, PlayerPhysics.SwimUpSpeed, 0f), 1f));
        AssertNeverRejected("swim exit boost", pool,
            Line(Eye(2.5f, Resting, 2.5f), new Vector3(0f, PlayerPhysics.SwimExitBoost, 0f), 0.5f));
    }

    [Fact]
    public void AMoveIntoASolidCellIsRejectedToTheLastLegalPosition()
    {
        var course = Course();
        var before = Eye(WallX - 1.5f, Resting, 4.5f);
        Assert.False(PlayerPhysics.BodyCollides(before, course));

        // Into the wall, whole or by the edge of the body: rejected, back where the player stood
        foreach (var into in new[] { Eye(WallX + 0.5f, Resting, 4.5f), Eye(WallX - 0.2f, Resting, 4.5f) })
        {
            Assert.True(PlayerPhysics.BodyCollides(into, course));
            Assert.Equal(MoveVerdict.Rejected, Judge(course, before, into, out var accepted));
            Assert.Equal(before, accepted);

            // Flight is noclip: the same move is taken
            Assert.Equal(MoveVerdict.Accepted, Judge(course, before, into, out accepted, flying: true));
            Assert.Equal(into, accepted);
        }

        // A body already inside the wall (a block written over it) may move, inside or out
        var buried = Eye(WallX + 0.5f, Resting, 4.5f);
        var stillInside = Eye(WallX + 0.3f, Resting, 4.5f);
        Assert.True(PlayerPhysics.BodyCollides(stillInside, course));
        Assert.Equal(MoveVerdict.Accepted, Judge(course, buried, stillInside, out var moved));
        Assert.Equal(stillInside, moved);
        Assert.Equal(MoveVerdict.Accepted, Judge(course, buried, before, out moved));
        Assert.Equal(before, moved);
    }

    [Fact]
    public void HoverWithoutTheFlightFlagIsRejectedAfterTheGrace()
    {
        var course = Course();
        var grace = WorldGenerationConfig.NetHoverGraceSeconds;

        // Standing still in mid-air: tolerated through the grace, rejected once it has run out, and
        // never while flying
        var air = Eye(2.5f, 5f, 2.5f);
        var still = Line(air, Vector3.Zero, (float)grace * 1.5f);
        var rejected = FirstRejection(course, still);
        Assert.True(rejected > 0, "a hover without flight was never rejected");
        Assert.True(rejected * Dt > grace - Dt * 0.5, $"rejected after {rejected * Dt} s, before the grace");
        Assert.Equal(-1, FirstRejection(course, still, flying: true));

        // A rejected hover leaves the player where it last stood
        var state = new MovementState();
        var at = air;
        for (var i = 0; i < still.Count; i++)
        {
            if (MovementRules.Judge(at, air, Dt, false, course, state, out var accepted) == MoveVerdict.Rejected)
            {
                Assert.Equal(at, accepted);
                break;
            }
            at = accepted;
        }

        // A ten-block climb inside one state is rejected outright
        var ground = Eye(2.5f, Resting, 6.5f);
        var high = ground + new Vector3(0f, 10f, 0f);
        Assert.Equal(MoveVerdict.Rejected, Judge(course, ground, high, out var stayed));
        Assert.Equal(ground, stayed);

        // The same climb spread over many states, each within the rise cap, is a hover: rejected
        // once the grace runs out
        var climb = Line(ground, new Vector3(0f, 10f / ((float)grace * 1.5f), 0f), (float)grace * 1.5f);
        for (var i = 1; i < climb.Count; i++)
            Assert.True(climb[i].Y - climb[i - 1].Y
                        <= WorldGenerationConfig.NetMaxRiseBlocksPerSecond * Dt + WorldGenerationConfig.NetRiseSlackBlocks);
        var climbRejected = FirstRejection(course, climb);
        Assert.True(climbRejected > 0, "a slow climb without flight was never rejected");
        Assert.True(climbRejected * Dt > grace - Dt * 0.5);
        Assert.Equal(-1, FirstRejection(course, climb, flying: true));
    }

    [Fact]
    public void UnloadedCellsAreNeverJudged()
    {
        var course = Course();

        // The same five-block climb in one state: rejected over the loaded floor, taken over a
        // chunk the server has not loaded, where every cell would read as air
        Assert.Equal(MoveVerdict.Rejected,
            Judge(course, Eye(2.5f, Resting, 4.5f), Eye(3.5f, Resting + 5f, 4.5f), out _));
        var edge = Eye(Chunk.Size - 0.5f, Resting, 4.5f);
        var beyond = Eye(Chunk.Size + 0.5f, Resting + 5f, 4.5f);
        Assert.Null(course.ChunkAt(Chunk.Size, 1, 4));
        Assert.Equal(MoveVerdict.Accepted, Judge(course, edge, beyond, out var accepted));
        Assert.Equal(beyond, accepted);

        // Standing still in the air over unloaded cells, beside the chunk or above it, is never a hover
        var grace = (float)WorldGenerationConfig.NetHoverGraceSeconds;
        Assert.Equal(-1, FirstRejection(course, Line(Eye(Chunk.Size + 4.5f, 5f, 4.5f), Vector3.Zero, grace * 3f)));
        Assert.Equal(-1, FirstRejection(course, Line(Eye(2.5f, Chunk.Size + 4f, 4.5f), Vector3.Zero, grace * 3f)));
    }

    /// <summary>
    /// The states the client's own physics reports at its cadence over <paramref name="seconds"/> for
    /// a body starting at <paramref name="eye"/> with <paramref name="keys"/> held for the first
    /// <paramref name="holdSeconds"/>.
    /// </summary>
    private static List<Vector3> Simulated(ChunkManager chunks, Vector3 eye, float seconds,
        Silk.NET.Input.Key[] keys, float holdSeconds)
    {
        const float step = 1f / 120f;
        var camera = new Camera(null, null);
        camera.TestTeleport(eye, yaw: 0f, pitch: 0f);
        var keyboard = new ScriptedKeyboard();
        var path = new List<Vector3> { camera.Position };
        var sinceState = 0f;
        for (var t = 0f; t < seconds; t += step)
        {
            if (t < holdSeconds) keyboard.Press(keys);
            else keyboard.ReleaseAll();
            camera.Update(step, keyboard, chunks);
            sinceState += step;
            if (sinceState < Dt) continue;
            sinceState -= Dt;
            path.Add(camera.Position);
        }
        return path;
    }

    [Fact]
    public void AFloatingBodyAtRestAndItsStrokeHopAreNeverJudgedAHover()
    {
        // The deep pool's surface sits 8/9 into its top cell; the eye starts just out of it
        var pool = MobTestWorld.Pool(12);
        var surface = 12f + FluidState.SourceHeight;
        var start = new Vector3(8.5f, surface + 0.05f, 8.5f);

        // Settling at the float and then floating still for longer than the hover grace: the body
        // rests in water, not in the air
        var grace = (float)WorldGenerationConfig.NetHoverGraceSeconds;
        var floating = Simulated(pool, start, 3f + grace * 2f, [], 0f);
        Assert.True(PlayerBodyProbe.InWater(floating[^1], pool));
        AssertNeverRejected("float at rest", pool, floating);

        // A stroke up from the float, out over the surface and back down to it
        var hop = Simulated(pool, floating[^1], 4f, [Silk.NET.Input.Key.Space], 0.1f);
        AssertNeverRejected("float stroke hop", pool, hop);
    }
}
