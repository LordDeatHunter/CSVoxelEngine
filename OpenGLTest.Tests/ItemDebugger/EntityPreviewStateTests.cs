using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.ItemDebugger;
using Xunit;

namespace VoxelEngine.Tests.ItemDebugger;

/// <summary>
/// The item debugger's ENTITIES tab state: data-driven modes (one per authored clip plus HURT),
/// one-shots that keep replaying and swallow their own events, the hurt pulse, name lookup, and a
/// walk that settles back onto the idle pose. Behavior only: no clip lengths or keyframe values.
/// </summary>
public sealed class EntityPreviewStateTests
{
    private const float Dt = 1f / 60f;

    public EntityPreviewStateTests() => TestContent.EnsureLoaded();

    private static EntityPreviewState Goblin()
    {
        var state = new EntityPreviewState();
        Assert.True(state.SelectByName("goblin"));
        return state;
    }

    private static bool IsLocomotionOrHurt(string mode) =>
        string.Equals(mode, EntityAnimator.IdleClip, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, EntityAnimator.WalkClip, StringComparison.OrdinalIgnoreCase) ||
        mode == EntityPreviewState.HurtMode;

    // Sum of how far two bone transforms are apart: translation, scale and rotation.
    private static float Distance(AnimationClip.BoneTransform a, AnimationClip.BoneTransform b) =>
        Vector3.Distance(a.Translation, b.Translation) +
        Vector3.Distance(a.Scale, b.Scale) +
        (1f - MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a.Rotation), Quaternion.Normalize(b.Rotation))));

    [Fact]
    public void OneShotModesReplayAndConsumeTheirOwnEvents()
    {
        var state = Goblin();
        var oneShots = new List<string>();
        foreach (var mode in state.Modes)
            if (!IsLocomotionOrHurt(mode)) oneShots.Add(mode);
        Assert.NotEmpty(oneShots);

        foreach (var mode in oneShots)
        {
            Assert.True(state.SetMode(mode));
            var clip = state.Animator.Find(mode);
            Assert.NotNull(clip);

            // 0 = waiting for the first play, 1 = saw it play, 2 = saw it stop, 3 = saw it again.
            var phase = 0;
            for (var frame = 0; frame < (int)(5f / Dt); frame++)
            {
                state.Update(Dt);
                var playing = state.Animator.IsPlaying(mode);
                if (phase == 0 && playing) phase = 1;
                else if (phase == 1 && !playing) phase = 2;
                else if (phase == 2 && playing) phase = 3;

                foreach (var e in clip!.Events)
                    Assert.False(state.Animator.ConsumeEvent(e.Name),
                        $"Mode '{mode}' leaked its '{e.Name}' event out of the preview.");
            }
            Assert.True(phase == 3, $"Mode '{mode}' did not replay (reached phase {phase}).");
        }
    }

    [Fact]
    public void HurtModePulsesTheHitFlash()
    {
        var state = Goblin();
        Assert.True(state.SetMode(EntityPreviewState.HurtMode));

        int rises = 0, falls = 0;
        var previous = state.HitFlash;
        for (var frame = 0; frame < (int)(3f / Dt); frame++)
        {
            state.Update(Dt);
            var flash = state.HitFlash;
            // A hit snaps the flash to full (a frame of fade is already off it by the time it is read) and
            // it then fades away again.
            if (flash > 0.7f && previous <= 0.7f) rises++;
            if (flash <= 0.01f && previous > 0.01f) falls++;
            previous = flash;
        }
        Assert.True(rises > 1, $"The hit flash rose {rises} time(s).");
        Assert.True(falls > 1, $"The hit flash fell back {falls} time(s).");
    }

    [Fact]
    public void SelectByNameIgnoresCaseAndRejectsUnknown()
    {
        var state = new EntityPreviewState();
        Assert.True(state.SelectByName("GoBlIn"));
        Assert.Equal("goblin", state.Selected!.Name);

        var before = state.SelectedIndex;
        Assert.False(state.SelectByName("no-such-entity-xyz"));
        Assert.Equal(before, state.SelectedIndex);
    }

    [Fact]
    public void WalkThenIdleReturnsTheRigToRest()
    {
        var state = Goblin();
        var clips = state.Selected!.Model.Animations;
        var walk = state.Animator.Find(EntityAnimator.WalkClip);
        Assert.NotNull(walk);
        var bones = new List<string>();
        foreach (var bone in global::VoxelEngine.Entities.Goblin.RequiredParts)
            if (walk!.Keys(bone)) bones.Add(bone);
        Assert.NotEmpty(bones);

        var walkFrames = (int)(2f / Dt);
        Assert.True(state.SetMode(EntityAnimator.WalkClip));
        var idleOnly = new EntityAnimator(clips, WorldGenerationConfig.GoblinWalkSpeed);
        for (var i = 0; i < walkFrames; i++)
        {
            state.Update(Dt);
            idleOnly.Update(Dt, 0f);
        }
        var walkedAway = 0f;
        foreach (var bone in bones)
            walkedAway = MathF.Max(walkedAway, Distance(state.Animator.LocalTransform(bone), idleOnly.LocalTransform(bone)));
        Assert.True(walkedAway > 1e-3f, "Walking left every walk-keyed bone on the idle pose.");

        // The idle clock ran through the walk too, so the reference covers the whole run.
        var idleFrames = (int)(3f / Dt);
        Assert.True(state.SetMode(EntityAnimator.IdleClip));
        for (var i = 0; i < idleFrames; i++) state.Update(Dt);

        var reference = new EntityAnimator(clips, WorldGenerationConfig.GoblinWalkSpeed);
        for (var i = 0; i < walkFrames + idleFrames; i++) reference.Update(Dt, 0f);

        foreach (var bone in global::VoxelEngine.Entities.Goblin.RequiredParts)
        {
            var d = Distance(state.Animator.LocalTransform(bone), reference.LocalTransform(bone));
            Assert.True(d < 1e-2f, $"Bone '{bone}' is {d} away from the idle pose after the walk faded.");
        }
    }

    [Fact]
    public void ClipLessModelOffersTheSlimesLifeCycleModesAndHurt()
    {
        var state = new EntityPreviewState();
        Assert.True(state.SelectByName("slime"));
        Assert.Empty(state.Selected!.Model.Animations);
        Assert.False(state.HasClips);
        Assert.Contains(EntityPreviewState.HurtMode, state.Modes);
        foreach (var mode in new[] { EntityPreviewState.SlimeIdle, EntityPreviewState.SlimeHop,
                     EntityPreviewState.SpawnMode, EntityPreviewState.DeathMode })
            Assert.Contains(mode, state.Modes);
        Assert.NotNull(state.PreviewMob);
    }

    [Fact]
    public void AGoblinListsItsHurtClipOnceAndTheHurtModeFlinchesIt()
    {
        var state = Goblin();
        Assert.Single(state.Modes, m => m == EntityPreviewState.HurtMode);
        Assert.True(state.SetMode(EntityPreviewState.HurtMode));

        var sawOverlay = false;
        var sawFlash = false;
        for (var i = 0; i < (int)(2.5f / Dt); i++)
        {
            state.Update(Dt);
            sawOverlay |= state.Animator.IsOverlayPlaying;
            sawFlash |= state.HitFlash > 0.5f;
        }

        Assert.True(sawOverlay, "The hurt mode never played the hurt clip.");
        Assert.True(sawFlash, "The hurt mode never flashed the goblin.");
        // The loop never kills the mob it is hurting.
        Assert.False(state.PreviewMob!.IsDead);
    }

    [Fact]
    public void ADeathModePlaysTheDeathThenStartsOverWithASpawn()
    {
        foreach (var name in new[] { "goblin", "slime" })
        {
            var state = new EntityPreviewState();
            Assert.True(state.SelectByName(name));
            Assert.True(state.SetMode(EntityPreviewState.DeathMode), name);

            var died = false;
            var started = false;
            var peak = 0f;
            for (var i = 0; i < (int)(6f / Dt); i++)
            {
                state.Update(Dt);
                var mob = state.PreviewMob!;
                died |= mob.IsDying;
                peak = MathF.Max(peak, mob.DeathProgress);
                // After a death the preview begins a fresh mob (alive, popping in).
                if (died && !mob.IsDying) started = true;
            }

            Assert.True(died, $"{name}: the death mode never killed the mob.");
            Assert.True(peak > 0.9f, $"{name}: the death only reached {peak}.");
            Assert.True(started, $"{name}: the death mode never started over.");
        }
    }

    [Fact]
    public void TheSlimeHopsAndSquashesAndTheGoblinGetsABodyPose()
    {
        var slime = new EntityPreviewState();
        Assert.True(slime.SelectByName("slime"));
        Assert.True(slime.SetMode(EntityPreviewState.SlimeHop));
        var highest = 0f;
        var scaleRange = (Min: 10f, Max: 0f);
        for (var i = 0; i < (int)(3f / Dt); i++)
        {
            slime.Update(Dt);
            highest = MathF.Max(highest, slime.DrawOffset.Y);
            var m = slime.BodyEffect();
            scaleRange = (MathF.Min(scaleRange.Min, m.M22), MathF.Max(scaleRange.Max, m.M22));
        }
        Assert.True(highest > 0.3f, "The slime never left the ground.");
        Assert.True(scaleRange.Max - scaleRange.Min > 0.2f, "The slime's squash and stretch never showed.");

        var goblin = Goblin();
        Assert.True(goblin.SetMode(EntityPreviewState.DeathMode));
        for (var i = 0; i < (int)(0.6f / Dt); i++) goblin.Update(Dt);
        Assert.NotEqual(Matrix4x4.Identity, goblin.BodyEffect());
    }

    [Fact]
    public void SetModeRefusesUnknownModes()
    {
        var state = Goblin();
        var mode = state.Mode;
        Assert.False(state.SetMode("no-such-clip-xyz"));
        Assert.Equal(mode, state.Mode);

        // A listed mode is accepted in any case and stored as the model spells it.
        var listed = state.Modes[state.Modes.Count - 1];
        Assert.True(state.SetMode(listed.ToUpperInvariant()));
        Assert.Equal(listed, state.Mode);
    }
}
