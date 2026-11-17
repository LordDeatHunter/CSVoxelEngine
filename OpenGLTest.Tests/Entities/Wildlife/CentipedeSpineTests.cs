using System;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Wildlife;
using Xunit;

namespace VoxelEngine.Tests.Entities.Wildlife;

/// <summary>
/// The centipede's drawn body chain: a straight chain is the rigid pose, a turn runs down the body from the head
/// instead of swinging it whole, and no path stretches a link or folds a joint.
/// </summary>
public sealed class CentipedeSpineTests
{
    public CentipedeSpineTests() => TestContent.EnsureLoaded();

    private const float Eps = 1e-3f;
    private const float StepBlocks = 0.06f;

    private static EntityType Type => EntityRegistry.Require(Centipede.EntityName);

    private static CentipedeRig Rig => CentipedeRig.For(Type);

    // The way a body facing `facing` walks: the model's -Z front turned by the yaw
    private static Vector3 Forward(float facing) => new(-MathF.Sin(facing), 0f, -MathF.Cos(facing));

    private static float Angle(Vector3 a, Vector3 b) =>
        MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f));

    private static Vector3 Walk(CentipedeSpine spine, Vector3 feet, float facing, float blocks)
    {
        for (var walked = 0f; walked < blocks; walked += StepBlocks)
        {
            feet += Forward(facing) * StepBlocks;
            spine.Follow(feet, facing);
        }
        return feet;
    }

    [Fact]
    public void AStraightChainIsTheRigidPose()
    {
        var spine = new CentipedeSpine(Rig);
        var feet = new Vector3(3f, 7f, -5f);
        const float facing = 0.7f;
        spine.Follow(feet, facing);
        feet = Walk(spine, feet, facing, 2f);

        var rigid = Matrix4x4.CreateRotationY(facing) * Matrix4x4.CreateTranslation(feet);
        foreach (var part in Type.Model.Parts.Keys)
        {
            var root = spine.RootOf(part);
            foreach (var probe in new[] { Vector3.Zero, new Vector3(1f, 2f, 3f), new Vector3(-2f, 0.5f, 1f) })
                Assert.True(Vector3.Distance(Vector3.Transform(probe, rigid), Vector3.Transform(probe, root)) < Eps,
                    $"part {part} left the rigid pose on a straight body");
        }
    }

    [Fact]
    public void NoPathStretchesALinkOrFoldsAJoint()
    {
        var rig = Rig;
        var spine = new CentipedeSpine(rig);
        var rng = new Random(11);
        var feet = Vector3.Zero;
        var facing = 0f;
        var limit = WildlifeConfig.CentipedeMaxBendDegrees * MathF.PI / 180f + Eps;
        spine.Follow(feet, facing);

        for (var step = 0; step < 600; step++)
        {
            // Turns far sharper than any mob's, some of them on the spot
            facing += ((float)rng.NextDouble() - 0.5f) * 1.2f;
            if (step % 3 != 0) feet += Forward(facing) * StepBlocks;
            spine.Follow(feet, facing);

            for (var i = 1; i < spine.Count; i++)
            {
                Assert.InRange(Vector3.Distance(spine[i], spine[i - 1]), rig.Links[i] - Eps, rig.Links[i] + Eps);
                var ahead = i == 1 ? -Forward(facing) : spine[i - 1] - spine[i - 2];
                Assert.True(Angle(ahead, spine[i] - spine[i - 1]) <= limit, $"joint {i} folded at step {step}");
            }
        }
    }

    [Fact]
    public void ATurnRunsDownTheBodyFromTheHeadAndWalkingOnStraightensIt()
    {
        var spine = new CentipedeSpine(Rig);
        var last = spine.Count - 1;
        var feet = Vector3.Zero;
        spine.Follow(feet, 0f);
        feet = Walk(spine, feet, 0f, 1f);
        var tailBefore = spine[last - 1] - spine[last];

        // A quarter turn on the spot: the head has turned all the way, the tail has barely begun to
        const float turned = MathF.PI / 2f;
        spine.Follow(feet, turned);
        var headTurn = Angle(Forward(0f), Forward(turned));
        var tailTurn = Angle(tailBefore, spine[last - 1] - spine[last]);
        Assert.True(tailTurn < headTurn * 0.5f, $"the tail swung {tailTurn} rad with the head's {headTurn}");

        // Walking on for a few body lengths pulls every joint in line behind the head again
        Walk(spine, feet, turned, Rig.Length * 4f);
        for (var i = 1; i < spine.Count; i++)
            Assert.True(Angle(Forward(turned), spine[i - 1] - spine[i]) < 0.05f, $"joint {i} still trails off the line");
    }

    [Fact]
    public void AHeadThatJumpsFartherThanTheBodyIsLongLaysTheChainOutStraight()
    {
        var rig = Rig;
        var spine = new CentipedeSpine(rig);
        spine.Follow(Vector3.Zero, 0f);
        Walk(spine, Vector3.Zero, 1.2f, 3f);

        const float facing = -2f;
        spine.Follow(new Vector3(rig.Length * 3f, 0f, 40f), facing);
        for (var i = 1; i < spine.Count; i++)
            Assert.True(Angle(Forward(facing), spine[i - 1] - spine[i]) < Eps, $"joint {i} was dragged across the jump");
    }
}
