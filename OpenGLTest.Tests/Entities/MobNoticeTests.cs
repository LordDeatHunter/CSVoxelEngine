using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Networking;
using VoxelEngine.Player;
using Xunit;

namespace VoxelEngine.Tests.Entities;

/// <summary>
/// The notice range (<see cref="MobTargets.NoticeRadius"/>): a mob only chases a target it can notice,
/// and a sneaking target is noticed from a shorter range, so it can creep past.
/// </summary>
public sealed class MobNoticeTests
{
    public MobNoticeTests() => TestContent.EnsureLoaded();

    private sealed class Target(Vector3 eye, bool sneaking = false, bool immune = false) : IMobTarget
    {
        public Vector3 Eye { get; } = eye;
        public bool IsImmune { get; } = immune;
        public bool IsSneaking { get; } = sneaking;
        public void TakeHit(int damage, Vector3 awayHorizontal, float knockbackSpeed, float lift, string? attacker = null) { }
    }

    private static readonly Vector3 Mob = new(0f, 1f, 0f);

    private static Target At(float distance, bool sneaking = false, bool immune = false) =>
        new(new Vector3(distance, 2.62f, 0f), sneaking, immune);

    [Fact]
    public void SneakingShortensTheRangeAMobNoticesFrom()
    {
        var standing = At(0f);
        var sneaking = At(0f, sneaking: true);
        Assert.True(MobTargets.NoticeRadius(sneaking) < MobTargets.NoticeRadius(standing));
        Assert.True(MobTargets.NoticeRadius(sneaking) > 0f);

        // Between the two ranges: chased while standing, unnoticed while sneaking
        var between = (MobTargets.NoticeRadius(sneaking) + MobTargets.NoticeRadius(standing)) * 0.5f;
        Assert.Equal(0, MobTargets.Nearest([At(between)], Mob));
        Assert.Equal(-1, MobTargets.Nearest([At(between, sneaking: true)], Mob));

        // Well inside the sneaking range a sneaking target is still noticed
        Assert.Equal(0, MobTargets.Nearest([At(MobTargets.NoticeRadius(sneaking) * 0.5f, sneaking: true)], Mob));
    }

    [Fact]
    public void ATargetPastTheNoticeRangeIsNeverChased()
    {
        var far = MobTargets.NoticeRadius(At(0f)) + 1f;
        Assert.Equal(-1, MobTargets.Nearest([At(far)], Mob));
        // Not even as the immune fallback
        Assert.Equal(-1, MobTargets.Nearest([At(far, immune: true)], Mob));
        // Despawning still measures it: the notice range only decides the chase
        Assert.Equal(far * far, MobTargets.MinHorizontalDistanceSquared([At(far)], Mob), 2);
    }

    [Fact]
    public void ANoticedFartherTargetWinsOverANearerSneakingOneOutOfRange()
    {
        var sneakRange = MobTargets.NoticeRadius(At(0f, sneaking: true));
        var nearSneaker = At(sneakRange + 1f, sneaking: true);
        var farWalker = At(sneakRange + 3f);
        Assert.Equal(1, MobTargets.Nearest([nearSneaker, farWalker], Mob));
        Assert.Equal(0, MobTargets.Nearest([farWalker, nearSneaker], Mob));
    }

    [Fact]
    public void TheCameraReportsItsSneak()
    {
        var camera = new Camera(null, null);
        IMobTarget target = camera;
        Assert.False(target.IsSneaking);
        camera.SetSneaking(true);
        Assert.True(target.IsSneaking);
    }

    [Fact]
    public void AConnectedPlayerSneaksByItsReportedFlag()
    {
        var player = new ServerPlayer(1, "A", Vector3.Zero);
        Assert.False(player.IsSneaking);
        player.Flags = (byte)(NetPlayerFlags.Sneaking | NetPlayerFlags.Airborne);
        Assert.True(player.IsSneaking);
        player.Flags = (byte)NetPlayerFlags.Sprinting;
        Assert.False(player.IsSneaking);
    }
}
