using System;
using VoxelEngine.ItemDebugger;
using VoxelEngine.Player;
using Xunit;

namespace VoxelEngine.Tests.ItemDebugger;

/// <summary>
/// The item debugger's player animation modes: every mode exists, drives the real <see cref="PlayerModel"/>,
/// and one-shots replay. Behavior only: no pose numbers.
/// </summary>
public sealed class PlayerPreviewStateTests
{
    private const float Dt = 1f / 60f;

    private static (PlayerPreviewState State, PlayerModel Model) Make(string mode)
    {
        var model = new PlayerModel();
        var state = new PlayerPreviewState(model);
        Assert.True(state.SetMode(mode));
        return (state, model);
    }

    private static void Run(PlayerPreviewState state, float seconds)
    {
        for (var t = 0f; t < seconds; t += Dt) state.Update(Dt);
    }

    [Fact]
    public void EveryModeCanBeSelectedInAnyCaseAndUnknownOnesAreRefused()
    {
        var state = new PlayerPreviewState(new PlayerModel());
        foreach (var mode in PlayerPreviewState.AllModes)
        {
            Assert.True(state.SetMode(mode.ToUpperInvariant()), mode);
            Assert.Equal(mode, state.Mode);
            Run(state, 0.2f);
        }

        var before = state.Mode;
        Assert.False(state.SetMode("moonwalk"));
        Assert.Equal(before, state.Mode);
        // Every mode has a short label for its button.
        foreach (var mode in PlayerPreviewState.AllModes)
            Assert.False(string.IsNullOrWhiteSpace(PlayerPreviewState.LabelOf(mode)));
    }

    [Fact]
    public void TheContinuousPosesShowTheirStateOnTheModel()
    {
        var (walk, walkModel) = Make(PlayerPreviewState.Walk);
        Run(walk, 1f);
        Assert.True(walkModel.MoveBlend > 0.8f);

        var (run, runModel) = Make(PlayerPreviewState.Run);
        Run(run, 1f);
        Assert.True(runModel.RunBlend > 0.8f);

        var (sneak, sneakModel) = Make(PlayerPreviewState.Sneak);
        Run(sneak, 1f);
        Assert.True(sneakModel.CrouchBlend > 0.8f);

        var (swim, swimModel) = Make(PlayerPreviewState.Swim);
        Run(swim, 1f);
        Assert.True(swimModel.SwimBlend > 0.8f);
    }

    [Fact]
    public void TheJumpLoopLeavesTheGroundAndLandsAgainAndTheSwimKeepsStroking()
    {
        var (jump, model) = Make(PlayerPreviewState.Jump);
        var airborne = false;
        var landed = false;
        for (var i = 0; i < (int)(4f / Dt); i++)
        {
            jump.Update(Dt);
            if (model.AirBlend > 0.7f) airborne = true;
            if (airborne && model.LandingDip > 0.05f) landed = true;
        }
        Assert.True(airborne, "The jump loop never took the player off the ground.");
        Assert.True(landed, "The jump never registered a landing.");

        var (swim, swimModel) = Make(PlayerPreviewState.Swim);
        var restarts = 0;
        var last = 1f;
        for (var i = 0; i < (int)(6f / Dt); i++)
        {
            swim.Update(Dt);
            if (swimModel.SwimStrokeProgress < last - 0.5f) restarts++;
            last = swimModel.SwimStrokeProgress;
        }
        Assert.True(restarts >= 3, $"Only {restarts} swim strokes in six seconds.");

        // Floating still makes none.
        var (floating, floatModel) = Make(PlayerPreviewState.SwimIdle);
        Run(floating, 3f);
        Assert.Equal(1f, floatModel.SwimStrokeProgress);
    }

    [Fact]
    public void EveryOneShotPlaysAndThenReplays()
    {
        foreach (var mode in new[] { PlayerPreviewState.Punch, PlayerPreviewState.HeavyPunch, PlayerPreviewState.Slice,
                     PlayerPreviewState.SliceBack, PlayerPreviewState.Throw, PlayerPreviewState.Hurt,
                     PlayerPreviewState.Equip, PlayerPreviewState.Death })
        {
            var (state, model) = Make(mode);
            Assert.True(state.IsOneShot, mode);

            // 0 = waiting, 1 = saw it active, 2 = saw it finish, 3 = saw it active again.
            var phase = 0;
            for (var i = 0; i < (int)(9f / Dt); i++)
            {
                state.Update(Dt);
                var active = mode switch
                {
                    PlayerPreviewState.Punch or PlayerPreviewState.HeavyPunch => model.MineT < 1f,
                    PlayerPreviewState.Slice or PlayerPreviewState.SliceBack => model.IsSlicing,
                    PlayerPreviewState.Throw => model.IsThrowing || model.ThrowCharge.HasValue,
                    PlayerPreviewState.Hurt => MathF.Abs(model.Flinch) > 0.05f,
                    PlayerPreviewState.Equip => model.EquipLowering > 0.05f,
                    _ => model.DeathFall > 0.05f,
                };
                if (phase == 0 && active) phase = 1;
                else if (phase == 1 && !active) phase = 2;
                else if (phase == 2 && active) phase = 3;
            }
            Assert.True(phase == 3, $"'{mode}' did not play and replay (reached phase {phase}).");
        }
    }

    [Fact]
    public void SwitchingModeDropsAHeldChargeAndAFallenBody()
    {
        var (state, model) = Make(PlayerPreviewState.Death);
        Run(state, 1.2f);
        Assert.True(model.DeathFall > 0.5f);

        Assert.True(state.SetMode(PlayerPreviewState.Idle));
        Assert.Equal(0f, model.DeathFall);

        Assert.True(state.SetMode(PlayerPreviewState.Throw));
        Run(state, 0.6f);
        Assert.True(model.ThrowCharge.HasValue);
        Assert.True(state.SetMode(PlayerPreviewState.Idle));
        Assert.False(model.ThrowCharge.HasValue);
    }
}
