using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Crawlers;
using VoxelEngine.Entities.Wildlife;
using Xunit;

namespace VoxelEngine.Tests.Entities.Wildlife;

/// <summary>
/// The centipede's body chain: a straight chain is the rigid pose, every joint stands on the path the head took
/// (round a turn, up a wall) with the up the head had there, no link stretches, and a head that jumps lays the
/// chain out afresh.
/// </summary>
public sealed class CentipedeSpineTests
{
    public CentipedeSpineTests() => TestContent.EnsureLoaded();

    private const float Eps = 1e-3f;
    private const float StepBlocks = 0.03f;
    private const float Half = WildlifeConfig.CentipedeBodySize * 0.5f;

    private static EntityType Type => EntityRegistry.Require(Centipede.EntityName);

    private static CentipedeRig Rig => CentipedeRig.For(Type);

    private static CentipedeSpine NewSpine() => new(Rig, Half);

    // Crawls the head `blocks` along `heading` on the face whose normal is `up`, noting every place it was
    private static Vector3 Crawl(CentipedeSpine spine, Vector3 center, Vector3 up, Vector3 heading, float blocks,
        List<Vector3>? path = null)
    {
        for (var crawled = 0f; crawled < blocks; crawled += StepBlocks)
        {
            center += heading * StepBlocks;
            spine.Follow(center, up, heading);
            path?.Add(center);
        }
        return center;
    }

    [Fact]
    public void AStraightChainIsTheRigidPose()
    {
        var rig = Rig;
        var spine = NewSpine();
        const float facing = 0.7f;
        var heading = CrawlFrame.HeadingOf(Vector3.UnitY, facing);
        var center = new Vector3(3f, 7f, -5f);
        spine.Follow(center, Vector3.UnitY, heading);
        center = Crawl(spine, center, Vector3.UnitY, heading, 2f);

        // The whole model turned by the facing, the head's origin on the floor under the head's center
        var rigid = Matrix4x4.CreateTranslation(-rig.Pivots[0].X, 0f, -rig.Pivots[0].Z)
                    * Matrix4x4.CreateRotationY(facing)
                    * Matrix4x4.CreateTranslation(center - Vector3.UnitY * Half);
        foreach (var part in Type.Model.Parts.Keys)
        {
            var root = spine.RootOf(part);
            foreach (var probe in new[] { Vector3.Zero, new Vector3(1f, 2f, 3f), new Vector3(-2f, 0.5f, 1f) })
                Assert.True(Vector3.Distance(Vector3.Transform(probe, rigid), Vector3.Transform(probe, root)) < Eps,
                    $"part {part} left the rigid pose on a straight body");
        }
    }

    [Fact]
    public void TheBodyFollowsTheHeadsPathUpAWallWithoutStretching()
    {
        var rig = Rig;
        var spine = NewSpine();
        var last = spine.Count - 1;
        var path = new List<Vector3>();
        var floor = Vector3.UnitY;
        var wall = -Vector3.UnitX;

        // Along a floor to a wall, then up it: first only the front of the body is on the wall
        var center = new Vector3(2f, 1f + Half, 5f);
        spine.Follow(center, floor, Vector3.UnitX);
        center = Crawl(spine, center, floor, Vector3.UnitX, rig.Length * 1.5f, path);
        center = Crawl(spine, center, wall, Vector3.UnitY, rig.Length * 0.5f, path);

        Assert.Equal(wall, spine.UpAt(0));
        Assert.Equal(floor, spine.UpAt(last));
        Assert.True(spine[1].Y > spine[last].Y + 0.1f, "the front of the body did not rise with the head");
        AssertOnPath(spine, rig, path);

        // Climbing on, the whole body is on the wall, straight under the head
        Crawl(spine, center, wall, Vector3.UnitY, rig.Length * 1.5f, path);
        AssertOnPath(spine, rig, path);
        for (var i = 0; i < spine.Count; i++)
        {
            Assert.Equal(wall, spine.UpAt(i));
            Assert.True(MathF.Abs(spine[i].X - spine[0].X) < Eps, $"joint {i} is off the wall");
        }
    }

    // Every joint is where the head once was, and no farther from the one ahead than its link is long
    private static void AssertOnPath(CentipedeSpine spine, CentipedeRig rig, List<Vector3> path)
    {
        for (var i = 1; i < spine.Count; i++)
        {
            var off = path.Min(point => Vector3.Distance(point, spine[i]));
            Assert.True(off < WildlifeConfig.CentipedeTrailSpacingBlocks, $"joint {i} is {off} off the head's path");
            Assert.True(Vector3.Distance(spine[i], spine[i - 1]) <= rig.Links[i] + Eps, $"the link to joint {i} stretched");
        }
    }

    [Fact]
    public void ATurnRunsDownTheBodyFromTheHeadAndWalkingOnStraightensIt()
    {
        var rig = Rig;
        var spine = NewSpine();
        var last = spine.Count - 1;
        var up = Vector3.UnitY;
        var center = new Vector3(0f, Half, 0f);
        spine.Follow(center, up, Vector3.UnitX);
        center = Crawl(spine, center, up, Vector3.UnitX, rig.Length * 1.5f);
        var line = center.Z;

        // A quarter turn and a short way on: the joints behind the head have left the old line, the tail has not
        center = Crawl(spine, center, up, Vector3.UnitZ, rig.Length * 0.25f);
        Assert.True(spine[1].Z > line + 0.05f, "the joint behind the head did not follow it round the turn");
        Assert.True(MathF.Abs(spine[last].Z - line) < Eps, "the tail swung round with the head");

        // Walking on for more than a body length pulls every joint in line behind the head again
        center = Crawl(spine, center, up, Vector3.UnitZ, rig.Length * 1.5f);
        for (var i = 1; i < spine.Count; i++)
            Assert.True(MathF.Abs(spine[i].X - center.X) < Eps && spine[i].Z < spine[i - 1].Z,
                $"joint {i} still trails off the line");
    }

    [Fact]
    public void AHeadThatJumpsFartherThanTheBodyIsLongLaysTheChainOutStraight()
    {
        var rig = Rig;
        var spine = NewSpine();
        var up = Vector3.UnitY;
        spine.Follow(Vector3.Zero, up, Vector3.UnitX);
        Crawl(spine, Vector3.Zero, up, Vector3.UnitX, 3f);

        var heading = Vector3.Normalize(new Vector3(-1f, 0f, 2f));
        var landed = new Vector3(rig.Length * 3f, 0f, 40f);
        spine.Follow(landed, up, heading);
        var reach = 0f;
        for (var i = 1; i < spine.Count; i++)
        {
            reach += rig.Links[i];
            Assert.True(Vector3.Distance(landed - heading * reach, spine[i]) < Eps, $"joint {i} was dragged across the jump");
        }
    }
}
