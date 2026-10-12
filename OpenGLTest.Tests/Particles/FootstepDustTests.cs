using System;
using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The sprint footstep dust: when a footfall happens (<see cref="FootstepDust.FootfallsBetween"/>),
/// which footfalls puff (<see cref="FootstepDust.Kicks"/>, the block's <c>footstepDust</c> flag) and
/// the puffs <see cref="GroundDust"/> spawns, down to a real sprinting body.
/// </summary>
public sealed class FootstepDustTests
{
    public FootstepDustTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;
    private const float Sprint = 12f;

    private static byte Id(string block) => ContentRegistry.RequireBlock(block).Id;

    private static readonly Vector3 FloorFeet = new(8.5f, 1f, 8.5f);

    [Fact]
    public void AFullStrideHoldsTwoFootfallsAndBetweenStrikesThereAreNone()
    {
        // A strike is where the hip swing's sine peaks
        const float strike = MathF.PI / 2f;
        Assert.Equal(1, FootstepDust.FootfallsBetween(strike - 0.1f, strike + 0.1f));
        Assert.Equal(0, FootstepDust.FootfallsBetween(strike + 0.1f, strike + MathF.PI - 0.1f));
        Assert.Equal(2, FootstepDust.FootfallsBetween(0f, MathF.Tau));
        // A clock standing still or running back strikes nothing
        Assert.Equal(0, FootstepDust.FootfallsBetween(strike + 0.1f, strike + 0.1f));
        Assert.Equal(0, FootstepDust.FootfallsBetween(strike + 0.1f, strike - 0.1f));

        // Stepped in small frames, n strides strike exactly 2n times wherever the clock starts
        foreach (var start in new[] { 0f, 1.3f, 50f })
        {
            const int strides = 5;
            var total = 0;
            var phase = start;
            while (phase < start + strides * MathF.Tau)
            {
                var next = MathF.Min(phase + 0.137f, start + strides * MathF.Tau);
                total += FootstepDust.FootfallsBetween(phase, next);
                phase = next;
            }
            Assert.Equal(2 * strides, total);
        }
    }

    [Fact]
    public void OnlyASprintingGroundedDryRunOverLooseGroundPuffs()
    {
        var sand = Id("sand");
        Assert.True(FootstepDust.Kicks(true, true, false, false, false, Sprint, sand));

        Assert.False(FootstepDust.Kicks(false, true, false, false, false, Sprint, sand), "not sprinting");
        Assert.False(FootstepDust.Kicks(true, false, false, false, false, Sprint, sand), "airborne");
        Assert.False(FootstepDust.Kicks(true, true, true, false, false, Sprint, sand), "flying");
        Assert.False(FootstepDust.Kicks(true, true, false, true, false, Sprint, sand), "sneaking");
        Assert.False(FootstepDust.Kicks(true, true, false, false, true, Sprint, sand), "in water");
        Assert.False(FootstepDust.Kicks(true, true, false, false, false, 0f, sand), "standing");
    }

    [Fact]
    public void WhetherASurfacePuffsIsTheBlocksOwnFootstepDustFlag()
    {
        var loose = 0;
        foreach (var (id, type) in BlockType.Types)
        {
            Assert.Equal(type.FootstepDust, FootstepDust.Kicks(true, true, false, false, false, Sprint, id));
            if (type.FootstepDust) loose++;
            // A fluid is never ground a footfall scuffs
            if (type.Fluid) Assert.False(type.FootstepDust, type.Name);
        }

        // Anti-vacuity: some block is loose and some is not
        Assert.InRange(loose, 1, BlockType.Types.Count - 1);
        Assert.False(FootstepDust.Kicks(true, true, false, false, false, Sprint, BlockType.Air));
    }

    [Fact]
    public void EachFootfallCrossingPuffsOnceAndNoneOnANonLooseSurface()
    {
        var sand = MobTestWorld.Floor(Id("sand"));
        var stone = MobTestWorld.Floor(Id("stone"));
        const float strike = MathF.PI / 2f;

        var debris = new BlockDebrisSystem();
        var dust = new GroundDust();
        Assert.Equal(0, dust.Step(strike - 0.2f, true, true, false, false, false, Sprint, Vector2.UnitX, FloorFeet, sand, debris));
        // Between strikes: nothing
        Assert.Equal(0, dust.Step(strike - 0.1f, true, true, false, false, false, Sprint, Vector2.UnitX, FloorFeet, sand, debris));
        Assert.Empty(debris.Debris);
        // Across one strike: one puff of the sand underfoot
        var chips = dust.Step(strike + 0.1f, true, true, false, false, false, Sprint, Vector2.UnitX, FloorFeet, sand, debris);
        Assert.True(chips > 0);
        Assert.Equal(chips, debris.Debris.Count);
        Assert.All(debris.Debris, chip => Assert.Equal(Id("sand"), chip.BlockId));
        // The next strike puffs once more, exactly as much
        dust.Step(strike + MathF.PI + 0.1f, true, true, false, false, false, Sprint, Vector2.UnitX, FloorFeet, sand, debris);
        Assert.Equal(2 * chips, debris.Debris.Count);

        // A footfall crossing on stone, or a sneaking one on sand, raises nothing
        var other = new BlockDebrisSystem();
        var onStone = new GroundDust();
        onStone.Step(strike - 0.1f, true, true, false, false, false, Sprint, Vector2.UnitX, FloorFeet, stone, other);
        Assert.Equal(0, onStone.Step(strike + 0.1f, true, true, false, false, false, Sprint, Vector2.UnitX, FloorFeet, stone, other));
        var sneaking = new GroundDust();
        sneaking.Step(strike - 0.1f, true, true, false, true, false, Sprint, Vector2.UnitX, FloorFeet, sand, other);
        Assert.Equal(0, sneaking.Step(strike + 0.1f, true, true, false, true, false, Sprint, Vector2.UnitX, FloorFeet, sand, other));
        Assert.Empty(other.Debris);
    }

    [Fact]
    public void AFootfallKicksItsGritBackAgainstTheRun()
    {
        const float strike = MathF.PI / 2f;
        foreach (var run in new[] { Vector2.UnitX, -Vector2.UnitY })
        {
            var chunks = MobTestWorld.Floor(Id("sand"));
            var debris = new BlockDebrisSystem();
            var dust = new GroundDust();
            dust.Step(strike - 0.1f, true, true, false, false, false, Sprint, run, FloorFeet, chunks, debris, new Random(3));
            dust.Step(strike + 0.1f, true, true, false, false, false, Sprint, run, FloorFeet, chunks, debris, new Random(3));
            Assert.NotEmpty(debris.Debris);
            for (var i = 0; i < 6; i++)
                debris.Update(Dt, chunks);

            var along = 0f;
            var rise = 0f;
            foreach (var chip in debris.Debris)
            {
                along += (chip.Position.X - FloorFeet.X) * run.X + (chip.Position.Z - FloorFeet.Z) * run.Y;
                rise += chip.Position.Y - FloorFeet.Y;
            }
            Assert.True(along < 0f, $"the grit went ahead of the run along {run}: {along}");
            Assert.True(rise > 0f, "the grit never rose off the ground");
        }
    }

    [Fact]
    public void ASprintAcrossSandPuffsAndAWalkOrAStandDoesNot()
    {
        Assert.NotEmpty(Run(sprint: true, move: true));
        Assert.Empty(Run(sprint: false, move: true));
        Assert.Empty(Run(sprint: true, move: false));

        static System.Collections.Generic.IReadOnlyList<BlockDebris> Run(bool sprint, bool move)
        {
            var chunks = MobTestWorld.Floor(Id("sand"));
            var camera = new Camera(null, null);
            camera.TestTeleport(new Vector3(2.5f, 1f + Camera.EyeHeight, 8.5f), yaw: 0f, pitch: 0f);
            var keys = new ScriptedKeyboard();
            var pressed = new System.Collections.Generic.List<Silk.NET.Input.Key>();
            if (move) pressed.Add(Silk.NET.Input.Key.W);
            if (sprint) pressed.Add(Silk.NET.Input.Key.ShiftLeft);
            keys.Press(pressed.ToArray());
            var model = new PlayerModel();
            var dust = new GroundDust();
            var debris = new BlockDebrisSystem();

            for (var i = 0; i < 50; i++)
            {
                var before = camera.Position;
                camera.Update(Dt, keys, chunks);
                var step = camera.Position - before;
                var speed = new Vector2(step.X, step.Z).Length() / Dt;
                model.Update(Dt, speed, camera.IsSprinting, camera.IsGrounded, camera.VerticalVelocity);
                dust.UpdatePlayer(camera, model.WalkPhase, speed, chunks, debris, new Random(i));
            }

            Assert.All(debris.Debris, chip => Assert.Equal(Id("sand"), chip.BlockId));
            return debris.Debris;
        }
    }
}
