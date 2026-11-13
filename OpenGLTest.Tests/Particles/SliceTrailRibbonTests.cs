using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Data;
using VoxelEngine.Interaction;
using VoxelEngine.Items;
using VoxelEngine.Particles;
using VoxelEngine.Player;
using VoxelEngine.Player.Posing;
using Xunit;

namespace VoxelEngine.Tests.Particles;

/// <summary>
/// The slice-trail ribbon built from trails of real swings: it fits its buffer, tapers at both ends,
/// only fades with age, never bridges two swings, follows the owner frame, and keeps a stable streak
/// coordinate across the ribbon.
/// </summary>
public sealed class SliceTrailRibbonTests
{
    public SliceTrailRibbonTests() => TestContent.EnsureLoaded();

    private static Item Blade => ItemRegistry.TryGet(ContentRegistry.RequireItem("obsidian_dagger").Id)!;

    // Drives one player model and its trail the way the world does (see SliceTrailTests)
    private sealed class Driver(Item item, double hz)
    {
        public readonly PlayerModel Player = new();
        public readonly SliceTrail Trail = new() { Color = new Rgba(190, 120, 255) };
        public readonly double Dt = 1.0 / hz;
        public Vector3 Eye = new(3f, 20f, -7f);
        public float Yaw = -90f;
        public double Now = 10.0;

        public Matrix4x4 BladeMatrix => HeldItemPose.SpriteMatrix(Player, item, Eye, Yaw, 0f, 0f);

        public Matrix4x4 Owner => SliceTrail.OwnerFrameAt(Eye, Player.DrawnBodyYaw(Yaw));

        public void Step()
        {
            Now += Dt;
            Player.Update((float)Dt, 0f);
            Trail.Update(Now, SliceTrail.IsBladeSwing(Player), SliceTrail.SwingProgress(Player),
                Player.IsHitStopped, BladeMatrix, Owner);
        }

        public void StepUntil(Func<bool> done, int limit = 100_000)
        {
            for (var i = 0; i < limit && !done(); i++)
                Step();
        }

        public Vertex[] Build(double? at = null)
        {
            var buffer = new Vertex[SliceTrailRibbon.MaxVertices];
            var n = SliceTrailRibbon.Build(Trail, at ?? Now, buffer);
            return buffer.AsSpan(0, n).ToArray();
        }
    }

    // A slice stepped a little past its strike, so the trail holds the whole fast stroke and a slowing head
    private static Driver PastTheStrike()
    {
        var d = new Driver(Blade, 120);
        d.Player.StartSlice(1);
        d.StepUntil(() => d.Player.SliceProgress >= SliceMotion.SliceStrikeEnd + 0.08f);
        return d;
    }

    private static byte Alpha(Vertex v) => (byte)(v.PackedColor >> 24);

    // The cross-section widths along the ribbon, in quad order: each quad's first, then the last quad's end
    private static List<float> Widths(Vertex[] vs)
    {
        var widths = new List<float>();
        for (var q = 0; q < vs.Length; q += 4)
            widths.Add(Vector3.Distance(vs[q].Position, vs[q + 1].Position));
        widths.Add(Vector3.Distance(vs[^1].Position, vs[^2].Position));
        return widths;
    }

    [Fact]
    public void AnEmptyTrailBuildsNothing()
    {
        var buffer = new Vertex[SliceTrailRibbon.MaxVertices];
        Assert.Equal(0, SliceTrailRibbon.Build(new SliceTrail(), 3.0, buffer));
    }

    [Fact]
    public void TheRibbonFitsItsBuffer()
    {
        var d = new Driver(Blade, 1000);
        var buffer = new Vertex[SliceTrailRibbon.MaxVertices];
        var next = 0;
        var built = 0;
        for (var i = 0; i < 2000; i++)
        {
            if (!SliceTrail.IsBladeSwing(d.Player))
            {
                switch (next++ % 5)
                {
                    case 0: d.Player.StartSlice(1); break;
                    case 1: d.Player.StartSlice(-1); break;
                    case 2: d.Player.StartSword(SwordSwing.Cut); break;
                    case 3: d.Player.StartSword(SwordSwing.BackCut); break;
                    default: d.Player.StartSword(SwordSwing.Chop); break;
                }
            }
            d.Step();
            var n = SliceTrailRibbon.Build(d.Trail, d.Now, buffer);
            Assert.Equal(0, n % 4);
            Assert.InRange(n, 0, SliceTrailRibbon.MaxVertices);
            built = Math.Max(built, n);
        }
        Assert.True(built > 0);
        Assert.True(SliceTrailRibbon.MaxVertices <= ushort.MaxValue);
    }

    [Fact]
    public void ACompletedSwingTapersAtBothEnds()
    {
        var vs = PastTheStrike().Build();
        Assert.NotEmpty(vs);

        var widths = Widths(vs);
        var widest = 0f;
        foreach (var w in widths)
            widest = MathF.Max(widest, w);

        Assert.True(widths[0] < widest, $"old end {widths[0]} as wide as the widest {widest}");
        Assert.True(widths[^1] < widest, $"new end {widths[^1]} as wide as the widest {widest}");
    }

    [Fact]
    public void TheRibbonOnlyFadesWithAge()
    {
        var d = PastTheStrike();
        var earlier = d.Build();
        var later = d.Build(d.Now + 0.05);

        // Match every cross-section by its streak coordinate and edge (the same sample interpolation)
        var alphaBefore = new Dictionary<(float, float), byte>();
        foreach (var v in earlier)
        {
            var key = (v.Uv.X, v.Uv.Y);
            alphaBefore[key] = alphaBefore.TryGetValue(key, out var a) ? Math.Max(a, Alpha(v)) : Alpha(v);
        }

        var lowered = false;
        var matched = 0;
        foreach (var v in later)
        {
            if (!alphaBefore.TryGetValue((v.Uv.X, v.Uv.Y), out var before))
                continue;
            matched++;
            Assert.True(Alpha(v) <= before, $"alpha rose from {before} to {Alpha(v)} at u {v.Uv.X}");
            lowered |= Alpha(v) < before;
        }

        Assert.True(later.Length <= earlier.Length);
        Assert.True(matched > 0);
        Assert.True(lowered);
    }

    [Fact]
    public void SegmentsAreNeverBridged()
    {
        var d = new Driver(Blade, 120);
        d.Player.StartSlice(1);
        d.StepUntil(() => d.Player.SliceProgress >= SliceMotion.SliceStrikeEnd);
        d.Player.StartSlice(-1);
        d.StepUntil(() => d.Player.SliceProgress >= SliceMotion.SliceStrikeEnd * 0.5f);
        var vs = d.Build();
        Assert.NotEmpty(vs);

        var restarts = 0;
        var restartU = float.NaN;
        var endOfFirst = 0f;
        for (var q = 0; q < vs.Length; q += 4)
        {
            // Within a quad the ribbon only runs forward along its arc: a quad joining two runs would not
            Assert.True(vs[q + 2].Uv.X >= vs[q].Uv.X, $"quad {q / 4} runs back from u {vs[q].Uv.X} to {vs[q + 2].Uv.X}");
            if (q == 0 || vs[q].Uv.X >= vs[q - 2].Uv.X)
                continue;
            restarts++;
            endOfFirst = vs[q - 2].Uv.X;
            restartU = vs[q].Uv.X;
        }

        Assert.Equal(1, restarts);
        Assert.True(restartU < endOfFirst);
        Assert.True(restartU < 0.2f, $"the second run starts at u {restartU}");
    }

    [Fact]
    public void TheRibbonFollowsTheOwnerFrame()
    {
        var offset = new Vector3(6f, -3f, 11f);
        var a = new Driver(Blade, 120);
        var b = new Driver(Blade, 120);
        b.Eye += offset;
        a.Player.StartSlice(1);
        b.Player.StartSlice(1);
        a.StepUntil(() => a.Player.SliceProgress >= SliceMotion.SliceStrikeEnd);
        b.StepUntil(() => b.Player.SliceProgress >= SliceMotion.SliceStrikeEnd);

        var va = a.Build();
        var vb = b.Build();
        Assert.NotEmpty(va);
        Assert.Equal(va.Length, vb.Length);
        for (var i = 0; i < va.Length; i++)
        {
            Assert.True(Vector3.Distance(va[i].Position + offset, vb[i].Position) < 1e-3f,
                $"vertex {i}: {va[i].Position} + {offset} != {vb[i].Position}");
            Assert.Equal(Alpha(va[i]), Alpha(vb[i]));
        }
    }

    [Fact]
    public void UvsSpanTheRibbon()
    {
        var vs = PastTheStrike().Build();
        Assert.NotEmpty(vs);

        var lastU = 0f;
        foreach (var v in vs)
        {
            Assert.True(v.Uv.Y is 0f or 1f, $"v {v.Uv.Y} is not an edge");
            Assert.True(v.Uv.X >= 0f);
            Assert.True(v.Uv.X >= lastU, $"u fell from {lastU} to {v.Uv.X}");
            lastU = v.Uv.X;
        }
        Assert.True(lastU > 0f);
    }

    [Fact]
    public void TheStreakCoordinateDoesNotCrawl()
    {
        // Mid-strike, a hit-stop-like hold keeps the head on the same tip while the old samples expire
        var d = new Driver(Blade, 120);
        d.Player.StartSlice(1);
        d.StepUntil(() => d.Player.SliceProgress >= PlayerModel.SliceImpactProgress);
        var before = d.Build();
        Assert.NotEmpty(before);
        var oldest = d.Trail.TimeAt(0);

        var blade = d.BladeMatrix;
        var owner = d.Owner;
        var progress = d.Player.SliceProgress;
        for (var i = 0; i < 12; i++)
        {
            d.Now += d.Dt;
            d.Trail.Update(d.Now, true, progress, true, blade, owner);
        }
        var after = d.Build();

        Assert.True(d.Trail.TimeAt(0) > oldest, "no sample expired");
        Assert.NotEmpty(after);
        Assert.Equal(before[^1].Uv.X, after[^1].Uv.X);
        Assert.Equal(before[^2].Uv.X, after[^2].Uv.X);
    }
}
