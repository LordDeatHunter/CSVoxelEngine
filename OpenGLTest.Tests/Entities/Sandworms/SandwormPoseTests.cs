using System;
using System.Numerics;
using VoxelEngine.Entities.Sandworms;
using VoxelEngine.Particles;
using Xunit;

namespace VoxelEngine.Tests.Entities.Sandworms;

/// <summary>
/// How the sandworm model is placed on its chain (<see cref="SandwormPose"/>) and the numbers of its cosmetic
/// effects (<see cref="SandwormEffectRules"/>): a part sits on its segment, faces along the chain with its top up
/// and shrinks along the taper; the jaw swings about its hinge and opens downward; the sand and the quake grow
/// as the worm nears the surface or the camera, and the quake exists only while it digs.
/// </summary>
public sealed class SandwormPoseTests
{
    private const float Eps = 1e-3f;

    private static SandwormChain StraightChain(Vector3 forward)
    {
        var chain = new SandwormChain(new Vector3(10, 70, -4), forward);
        var head = chain.Head;
        for (var step = 0; step < 40; step++)
        {
            head += forward * 2f;
            chain.Follow(head);
        }
        return chain;
    }

    [Fact]
    public void ASegmentPartSitsOnItsSegmentAndFacesAlongTheChain()
    {
        var forward = Vector3.Normalize(new Vector3(1, 0, 1));
        var chain = StraightChain(forward);

        for (var i = 0; i < chain.Count; i++)
        {
            var model = SandwormPose.Segment(chain, i);
            Assert.True(Vector3.Distance(Vector3.Transform(Vector3.Zero, model), chain[i]) < Eps);
            // The model faces -Z: its front point lands ahead of the segment along the chain
            var front = Vector3.Transform(-Vector3.UnitZ, model) - chain[i];
            Assert.True(Vector3.Dot(Vector3.Normalize(front), forward) > 0.98f, $"segment {i} faces away");
            Assert.InRange(Vector3.Transform(Vector3.UnitY, model).Y - chain[i].Y, SandwormChain.ScaleOf(i) - Eps, SandwormChain.ScaleOf(i) + Eps);
        }
    }

    [Fact]
    public void APartShrinksAlongTheTaperAndWithItsScaleFactor()
    {
        var chain = StraightChain(Vector3.UnitX);
        var headSize = Vector3.Distance(Vector3.Transform(Vector3.UnitY, SandwormPose.Segment(chain, 0)), chain[0]);
        var tailSize = Vector3.Distance(Vector3.Transform(Vector3.UnitY, SandwormPose.Segment(chain, chain.Count - 1)), chain[^1]);
        var shrunk = Vector3.Distance(Vector3.Transform(Vector3.UnitY, SandwormPose.Segment(chain, 0, 0.5f)), chain[0]);

        Assert.True(tailSize < headSize);
        Assert.Equal(headSize * 0.5f, shrunk, 3);
    }

    [Fact]
    public void ASinkingPartDropsStraightDown()
    {
        var chain = StraightChain(Vector3.UnitX);

        var level = Vector3.Transform(Vector3.Zero, SandwormPose.Segment(chain, 3));
        var sunk = Vector3.Transform(Vector3.Zero, SandwormPose.Segment(chain, 3, sink: 2f));

        Assert.Equal(level.Y - 2f, sunk.Y, 3);
        Assert.Equal(level.X, sunk.X, 3);
        Assert.Equal(level.Z, sunk.Z, 3);
    }

    [Fact]
    public void AChainPointingStraightUpStillGetsAValidMatrix()
    {
        var chain = new SandwormChain(Vector3.Zero, Vector3.UnitY);
        for (var i = 0; i < chain.Count; i++)
        {
            var model = SandwormPose.Segment(chain, i);
            Assert.True(float.IsFinite(model.M11) && float.IsFinite(model.M32) && float.IsFinite(model.M43));
            Assert.True(Vector3.Dot(Vector3.Normalize(Vector3.Transform(-Vector3.UnitZ, model) - chain[i]), Vector3.UnitY) > 0.98f);
        }
    }

    [Fact]
    public void TheJawSwingsAboutItsHingeAndOpensDownward()
    {
        var head = SandwormPose.Segment(StraightChain(Vector3.UnitX), 0);
        var hingeWorld = Vector3.Transform(SandwormPose.JawHinge, head);
        // The tip of the lower jaw at rest, relative to the head model origin
        var tip = new Vector3(0f, -1.4f, -2.1f);

        var shut = SandwormPose.Jaw(head, 0f);
        var open = SandwormPose.Jaw(head, 0.8f);

        // The jaw pivot is its hinge: a point there does not move however far it opens
        Assert.True(Vector3.Distance(Vector3.Transform(Vector3.Zero, open), hingeWorld) < Eps);
        Assert.True(Vector3.Distance(Vector3.Transform(Vector3.Zero, shut), hingeWorld) < Eps);
        // Opening drops the jaw tip, and it stays the same distance from the hinge
        var tipShut = Vector3.Transform(tip - SandwormPose.JawHinge, shut);
        var tipOpen = Vector3.Transform(tip - SandwormPose.JawHinge, open);
        Assert.True(tipOpen.Y < tipShut.Y);
        Assert.Equal(Vector3.Distance(tipShut, hingeWorld), Vector3.Distance(tipOpen, hingeWorld), 3);
    }

    [Fact]
    public void TheSandThrownGrowsWithHowCloseTheWormIsToSurfacing()
    {
        Assert.True(SandwormEffectRules.ChipsPerSecond(1f) > SandwormEffectRules.ChipsPerSecond(0f));
        Assert.True(SandwormEffectRules.ChipSpeed(1f) > SandwormEffectRules.ChipSpeed(0f));
        Assert.True(SandwormEffectRules.Radius(1f) > SandwormEffectRules.Radius(0f));
        Assert.True(SandwormEffectRules.ChipsPerSecond(0.5f) > SandwormEffectRules.ChipsPerSecond(0.2f));
    }

    [Fact]
    public void TheQuakeGrowsAsTheHeadNearsTheCameraAndOnlyExistsWhileDigging()
    {
        var near = SandwormEffectRules.QuakeTrauma(SandwormPhase.Dig, 8f);
        var far = SandwormEffectRules.QuakeTrauma(SandwormPhase.Dig, 40f);

        Assert.True(near > far);
        Assert.True(far > 0f);
        Assert.Equal(0f, SandwormEffectRules.QuakeTrauma(SandwormPhase.Dig, SandwormConfig.QuakeRangeBlocks + 1f));
        foreach (var phase in new[] { SandwormPhase.Thump, SandwormPhase.Breach, SandwormPhase.Air, SandwormPhase.Dash, SandwormPhase.Retreat })
            Assert.Equal(0f, SandwormEffectRules.QuakeTrauma(phase, 8f));
    }

    [Fact]
    public void HoldingTheShakeAddsWhatTheCameraDecaysInTheFrame()
    {
        // Decaying at 1 / 0.7 trauma a second, a frame holds a trauma by adding trauma * dt / 0.7
        Assert.Equal(0.5f * (1f / 60f) / 0.7f, SandwormEffectRules.HoldTrauma(0.5f, 1f / 60f), 6);
        Assert.Equal(0f, SandwormEffectRules.HoldTrauma(0f, 1f / 60f));
    }
}
